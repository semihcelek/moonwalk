using System.Collections.Concurrent;
using System.Text.Json;
using MoonWalk.Agents;
using MoonWalk.Projects;

namespace MoonWalk.Core;

public sealed class BatchItem
{
    public int Index { get; set; }

    /// <summary>The variant name for a comparison ("Opus plans, Sonnet implements"), or a short title for a queued brief.</summary>
    public string Label { get; set; } = "";

    public string Brief { get; set; } = "";

    public Dictionary<string, AgentChoice>? Stages { get; set; }

    public string? RunId { get; set; }

    /// <summary>Pending, Running, Done, Failed, Cancelled, Skipped or Interrupted.</summary>
    public string Status { get; set; } = "Pending";

    public string? Error { get; set; }
}

/// <summary>
/// Several runs started together. <c>queue</c>: one brief after another on a project. <c>compare</c>: one brief through several agent
/// combinations. Runs of a branch-mode project go one at a time; a compare on a worktree-mode project runs all at once.
/// </summary>
public sealed class Batch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Kind { get; set; } = "queue";

    /// <summary>Running, Done, Cancelled or Interrupted.</summary>
    public string Status { get; set; } = "Running";

    public bool StopOnFailure { get; set; }
    public bool Parallel { get; set; }
    public AutoMode Auto { get; set; } = new();
    public Dictionary<string, AgentChoice>? Stages { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<BatchItem> Items { get; set; } = new();
}

public sealed record VariantRequest(string? Label, Dictionary<string, AgentChoice>? Stages);

public sealed record BatchRequest(
    string? Name,
    string? Project,
    string? Kind,
    List<string>? Briefs,
    string? Brief,
    List<VariantRequest>? Variants,
    Dictionary<string, AgentChoice>? Stages,
    AutoMode? Auto,
    bool StopOnFailure = false);

/// <summary>Starts and watches batches. A batch is a list of runs the service starts for you, in order, so an overnight list needs no one to click Start.</summary>
public sealed class BatchService
{
    private const int MaxItems = 40;

    private static readonly JsonSerializerOptions Options = new(Json.Options) { WriteIndented = true };

    private readonly RunStore _store;
    private readonly RunEngine _engine;
    private readonly ProjectRegistry _projects;
    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, Batch> _batches = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    public BatchService(RunStore store, RunEngine engine, ProjectRegistry projects, string dir)
    {
        _store = store;
        _engine = engine;
        _projects = projects;
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(file), Options);
                if (batch is null)
                {
                    continue;
                }
                if (batch.Status == "Running")
                {
                    // MoonWalk stopped mid-batch. The runs it had started are recovered or released like any run; the rest are not started.
                    batch.Status = "Interrupted";
                    foreach (var item in batch.Items.Where(i => i.Status is "Pending" or "Running"))
                    {
                        item.Status = "Interrupted";
                    }
                    Save(batch);
                }
                _batches[batch.Id] = batch;
            }
            catch (JsonException)
            {
                // A damaged batch file is ignored rather than stopping MoonWalk.
            }
        }
    }

    public IReadOnlyList<Batch> List()
    {
        lock (_lock)
        {
            return _batches.Values.OrderByDescending(b => b.CreatedAt).ToList();
        }
    }

    public Batch? Get(string id)
    {
        lock (_lock)
        {
            return _batches.GetValueOrDefault(id);
        }
    }

    /// <summary>Checks the whole request, then starts running it in the background. Throws <see cref="ArgumentException"/> with the reason when it cannot work.</summary>
    public Batch Create(BatchRequest request)
    {
        var project = request.Project ?? throw new ArgumentException("A project is required.");
        var entry = _projects.Get(project) ?? throw new ArgumentException($"Unknown project '{project}'.");
        var kind = string.Equals(request.Kind, "compare", StringComparison.OrdinalIgnoreCase) ? "compare" : "queue";

        var items = new List<BatchItem>();
        if (kind == "queue")
        {
            foreach (var brief in (request.Briefs ?? []).Select(b => b.Trim()).Where(b => b.Length > 0))
            {
                items.Add(new BatchItem { Index = items.Count + 1, Label = Title(brief), Brief = brief });
            }
            if (items.Count == 0)
            {
                throw new ArgumentException("A queue needs at least one brief.");
            }
            _engine.Validate(project, request.Stages);
        }
        else
        {
            var brief = request.Brief?.Trim();
            if (string.IsNullOrEmpty(brief))
            {
                throw new ArgumentException("A comparison needs a brief.");
            }
            var variants = request.Variants ?? [];
            if (variants.Count < 2)
            {
                throw new ArgumentException("A comparison needs at least two variants.");
            }
            foreach (var variant in variants)
            {
                _engine.Validate(project, variant.Stages);
                items.Add(new BatchItem
                {
                    Index = items.Count + 1,
                    Label = string.IsNullOrWhiteSpace(variant.Label) ? $"Variant {items.Count + 1}" : variant.Label.Trim(),
                    Brief = brief,
                    Stages = variant.Stages,
                });
            }
        }
        if (items.Count > MaxItems)
        {
            throw new ArgumentException($"A batch can hold at most {MaxItems} runs.");
        }

        var now = DateTimeOffset.Now;
        var batch = new Batch
        {
            Id = $"b-{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}",
            Name = string.IsNullOrWhiteSpace(request.Name) ? (kind == "compare" ? "Comparison" : "Queue") + $" of {items.Count}" : request.Name.Trim(),
            Project = entry.Profile.Name,
            Kind = kind,
            StopOnFailure = request.StopOnFailure,
            Parallel = kind == "compare" && entry.Profile.Workspace == WorkspaceMode.Worktree,
            Auto = request.Auto ?? entry.Profile.Auto,
            Stages = kind == "queue" ? request.Stages : null,
            CreatedAt = now,
            Items = items,
        };
        lock (_lock)
        {
            _batches[batch.Id] = batch;
        }
        Save(batch);

        var cts = new CancellationTokenSource();
        _running[batch.Id] = cts;
        _ = Task.Run(() => RunAsync(batch, cts));
        return batch;
    }

    /// <summary>Stops a batch: no further run is started, and the one in progress is cancelled.</summary>
    public bool Cancel(string id)
    {
        if (!_running.TryGetValue(id, out var cts))
        {
            return false;
        }
        cts.Cancel();
        return true;
    }

    private async Task RunAsync(Batch batch, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (batch.Parallel)
            {
                await Task.WhenAll(batch.Items.Select(item => RunItemAsync(batch, item, ct)));
            }
            else
            {
                var stop = false;
                foreach (var item in batch.Items)
                {
                    if (stop || ct.IsCancellationRequested)
                    {
                        item.Status = "Skipped";
                        Save(batch);
                        continue;
                    }
                    var status = await RunItemAsync(batch, item, ct);
                    if (batch.StopOnFailure && status != "Done")
                    {
                        stop = true;
                    }
                }
            }
            batch.Status = ct.IsCancellationRequested ? "Cancelled" : "Done";
        }
        catch (Exception ex)
        {
            batch.Status = "Interrupted";
            foreach (var item in batch.Items.Where(i => i.Status is "Pending" or "Running"))
            {
                item.Status = "Interrupted";
                item.Error = ex.Message;
            }
        }
        batch.FinishedAt = DateTimeOffset.Now;
        Save(batch);
        _running.TryRemove(batch.Id, out _);
        cts.Dispose();
    }

    private async Task<string> RunItemAsync(Batch batch, BatchItem item, CancellationToken ct)
    {
        try
        {
            var run = _engine.Start(item.Brief, batch.Project, item.Stages ?? batch.Stages, batch.Auto);
            item.RunId = run.Id;
            item.Status = "Running";
            _store.Update(run.Id, r => { r.BatchId = batch.Id; r.BatchLabel = $"{item.Index}/{batch.Items.Count} {item.Label}"; });
            Save(batch);

            var cancelled = false;
            while (true)
            {
                var current = _store.Get(run.Id);
                if (current is null || !Run.IsActive(current.Status))
                {
                    item.Status = current?.Status.ToString() ?? "Failed";
                    item.Error = current?.Error;
                    break;
                }
                if (ct.IsCancellationRequested && !cancelled)
                {
                    cancelled = true;
                    _engine.Cancel(run.Id);
                }
                await Task.Delay(1000, CancellationToken.None);
            }
        }
        catch (ArgumentException ex)
        {
            item.Status = "Failed";
            item.Error = ex.Message;
        }
        Save(batch);
        return item.Status;
    }

    private void Save(Batch batch)
    {
        lock (_lock)
        {
            var path = Path.Combine(_dir, batch.Id + ".json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(batch, Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    private static string Title(string brief)
    {
        var line = brief.Split('\n', 2)[0].Trim();
        return line.Length <= 60 ? line : line[..60] + "...";
    }
}
