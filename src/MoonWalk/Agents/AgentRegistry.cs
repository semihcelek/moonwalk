using System.ComponentModel;
using System.Text.Json;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Agents;

public sealed record AgentInfo(string Name, string Kind, string Command);

public sealed record AgentHealth(string Name, string Kind, string Command, bool Available, string? Version, string? Detail);

/// <summary>
/// The agents a project can name: the built-in <c>claude</c> and <c>agy</c> (Gemini via Antigravity), plus any CLI agent defined in
/// agents.json. Also checks that each one is installed.
/// </summary>
public sealed class AgentRegistry(string file)
{
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly string[] Reserved = ["claude", "agy", "muse"];

    private readonly object _lock = new();
    private List<AgentDefinition> _custom = [];
    private List<string> _errors = [];

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_lock)
            {
                return _errors;
            }
        }
    }

    public void Reload()
    {
        var custom = new List<AgentDefinition>();
        var errors = new List<string>();

        if (File.Exists(file))
        {
            try
            {
                var text = File.ReadAllText(file);
                var definitions = text.TrimStart().StartsWith('[')
                    ? JsonSerializer.Deserialize<List<AgentDefinition>>(text, ReadOptions)
                    : JsonSerializer.Deserialize<AgentsFile>(text, ReadOptions)?.Agents;

                foreach (var definition in definitions ?? [])
                {
                    if (string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Command))
                    {
                        errors.Add("An agent needs a name and a command.");
                    }
                    else if (Reserved.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        errors.Add($"Agent name '{definition.Name}' is built in; choose another name.");
                    }
                    else if (custom.Any(c => string.Equals(c.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"Agent '{definition.Name}' is defined twice.");
                    }
                    else
                    {
                        custom.Add(definition);
                    }
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"{Path.GetFileName(file)} is not valid JSON: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _custom = custom;
            _errors = errors;
        }
    }

    public IReadOnlyList<AgentInfo> List()
    {
        List<AgentDefinition> custom;
        lock (_lock)
        {
            custom = _custom;
        }
        return new[]
            {
                new AgentInfo("claude", "built-in", "claude"),
                new AgentInfo("agy", "built-in", "agy"),
                new AgentInfo("muse", "built-in", "muse"),
            }
            .Concat(custom.Select(c => new AgentInfo(c.Name, "cli", c.Command)))
            .ToList();
    }

    public IReadOnlyList<string> Names => List().Select(a => a.Name).ToList();

    public bool IsKnown(string name) => List().Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public IAgent Create(string name, ProjectProfile profile, string? model = null, string? effort = null)
    {
        switch (name.ToLowerInvariant())
        {
            case "claude":
                return new ClaudeAgent(Override(profile.Claude, model, effort, (o, m) => o.Model = m, (o, e) => o.Effort = e));
            case "agy":
                // agy puts the effort in the model name, so a chosen model replaces the profile's effort.
                return new AgyAgent(Override(profile.Agy, model, null, (o, m) => { o.Model = m; o.Effort = null; }, (_, _) => { }));
            case "muse":
                return new MuseAgent(Override(profile.Muse, model, effort, (o, m) => o.Model = m, (o, e) => o.Effort = e));
        }

        AgentDefinition? definition;
        lock (_lock)
        {
            definition = _custom.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        return definition is null ? throw new ArgumentException($"Unknown agent '{name}'.") : new GenericCliAgent(definition);
    }

    // Per-stage choices must not leak into the shared profile, so they apply to a copy.
    private static T Override<T>(T options, string? model, string? effort, Action<T, string> setModel, Action<T, string> setEffort)
    {
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(effort))
        {
            return options;
        }
        var copy = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(options))!;
        if (!string.IsNullOrWhiteSpace(model))
        {
            setModel(copy, model);
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            setEffort(copy, effort);
        }
        return copy;
    }

    /// <summary>Runs each agent's version command (a few seconds at most) to see whether it is installed. Uses the default command names, not per-project paths.</summary>
    public async Task<IReadOnlyList<AgentHealth>> CheckAllAsync(CancellationToken ct)
    {
        List<AgentDefinition> custom;
        lock (_lock)
        {
            custom = _custom;
        }

        var checks = new List<Task<AgentHealth>>
        {
            CheckAsync("claude", "built-in", "claude", ["--version"], ct),
            CheckAsync("agy", "built-in", "agy", ["--version"], ct),
            CheckAsync("muse", "built-in", "muse", ["--version"], ct),
        };
        checks.AddRange(custom.Select(c => CheckAsync(c.Name, "cli", c.Command, c.VersionArgs, ct)));
        return await Task.WhenAll(checks);
    }

    private static async Task<AgentHealth> CheckAsync(string name, string kind, string command, string[] args, CancellationToken ct)
    {
        string? first = null;
        void OnLine(string line) => first ??= line.Trim();

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var (exit, timedOut) = await CliProcess.RunAsync(command, args, Directory.GetCurrentDirectory(), "", TimeSpan.FromSeconds(15), OnLine, OnLine, limit.Token);
            if (timedOut)
            {
                return new AgentHealth(name, kind, command, false, null, "The version command did not finish within 15 seconds.");
            }
            return exit == 0
                ? new AgentHealth(name, kind, command, true, first, null)
                : new AgentHealth(name, kind, command, false, null, $"'{command} {string.Join(' ', args)}' exited with {exit}. {first}".Trim());
        }
        catch (Win32Exception)
        {
            return new AgentHealth(name, kind, command, false, null, $"'{command}' was not found on the PATH.");
        }
    }

    private sealed class AgentsFile
    {
        public List<AgentDefinition> Agents { get; set; } = [];
    }
}
