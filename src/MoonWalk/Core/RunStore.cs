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
            if (run.Status is RunStatus.Queued or RunStatus.Running or RunStatus.Paused or RunStatus.WaitingForHuman)
            {
                run.ResumeWait = run.Status == RunStatus.WaitingForHuman ? run.Waiting : null;
                run.Status = RunStatus.Interrupted;
                run.Error = "MoonWalk stopped while this run was in progress. If it still holds the workspace, release it from the run page.";
                run.Waiting = null;
                run.PauseRequested = false;
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

    /// <summary>Adds up the usage ledgers of all runs over the last <paramref name="days"/> days, by day and by agent and model.</summary>
    public object UsageSummary(int days)
    {
        var since = DateTimeOffset.Now.Date.AddDays(-(Math.Max(days, 1) - 1));
        List<UsageEntry> entries;
        lock (_lock)
        {
            entries = _runs.Values.SelectMany(r => r.Usage).Where(u => u.Time.LocalDateTime >= since).ToList();
        }

        return new
        {
            days,
            totalCostUsd = entries.Sum(u => u.CostUsd ?? 0),
            totalTokens = entries.Sum(u => u.Tokens ?? 0),
            today = new
            {
                costUsd = entries.Where(u => u.Time.LocalDateTime.Date == DateTime.Today).Sum(u => u.CostUsd ?? 0),
                tokens = entries.Where(u => u.Time.LocalDateTime.Date == DateTime.Today).Sum(u => u.Tokens ?? 0),
            },
            byDay = entries.GroupBy(u => u.Time.LocalDateTime.Date).OrderBy(g => g.Key)
                .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), costUsd = g.Sum(u => u.CostUsd ?? 0), tokens = g.Sum(u => u.Tokens ?? 0) }).ToList(),
            byAgent = entries.GroupBy(u => new { u.Agent, Model = u.Model ?? "" }).OrderByDescending(g => g.Sum(u => u.Tokens ?? 0))
                .Select(g => new { agent = g.Key.Agent, model = g.Key.Model, costUsd = g.Sum(u => u.CostUsd ?? 0), tokens = g.Sum(u => u.Tokens ?? 0), attempts = g.Count() }).ToList(),
        };
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

        // On Windows an antivirus scan, the search indexer or an open viewer can hold run.json for a moment, which makes the
        // replace fail with "Access to the path is denied". The lock is short, so retry before giving up.
        const int attempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < attempts && e is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(25 * attempt);
            }
        }
    }
}
