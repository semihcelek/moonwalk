using System.Text.Json;

namespace MoonWalk.Core;

/// <summary>Holds all runs in memory, persists each to runs/&lt;id&gt;/run.json, and publishes every change as JSON.</summary>
public sealed class RunStore
{
    private readonly object _lock = new();
    private readonly string _root;
    private readonly Dictionary<string, Run> _runs = new();

    public Fanout<string> Changed { get; } = new();

    public RunStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "run.json", SearchOption.AllDirectories))
        {
            var run = JsonSerializer.Deserialize<Run>(File.ReadAllText(file), Json.Options);
            if (run is null)
            {
                continue;
            }
            if (run.Status is RunStatus.Queued or RunStatus.Running)
            {
                run.Status = RunStatus.Interrupted;
                run.Error = "MoonWalk stopped while this run was in progress.";
                Save(run);
            }
            _runs[run.Id] = run;
        }
    }

    public void Add(Run run)
    {
        lock (_lock)
        {
            _runs[run.Id] = run;
            Save(run);
            Changed.Publish(JsonSerializer.Serialize(run, Json.Options));
        }
    }

    public void Update(string id, Action<Run> mutate)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(id, out var run))
            {
                return;
            }
            mutate(run);
            run.UpdatedAt = DateTimeOffset.Now;
            Save(run);
            Changed.Publish(JsonSerializer.Serialize(run, Json.Options));
        }
    }

    public string ListJson()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(_runs.Values.OrderByDescending(r => r.CreatedAt), Json.Options);
        }
    }

    public Run? Get(string id)
    {
        lock (_lock)
        {
            return _runs.TryGetValue(id, out var run)
                ? JsonSerializer.Deserialize<Run>(JsonSerializer.Serialize(run, Json.Options), Json.Options)
                : null;
        }
    }

    public string? GetJson(string id)
    {
        lock (_lock)
        {
            return _runs.TryGetValue(id, out var run) ? JsonSerializer.Serialize(run, Json.Options) : null;
        }
    }

    public IReadOnlyList<(string Id, string Project)> HeldWorkspaces()
    {
        lock (_lock)
        {
            return _runs.Values.Where(r => r.WorkspaceHeld).Select(r => (r.Id, r.Project)).ToList();
        }
    }

    public bool Exists(string id)
    {
        lock (_lock)
        {
            return _runs.ContainsKey(id);
        }
    }

    private void Save(Run run)
    {
        var dir = Path.Combine(_root, run.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "run.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
