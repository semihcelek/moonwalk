using System.Text.Json;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Pipeline;

namespace MoonWalk.Projects;

public sealed record ProjectEntry(ProjectProfile Profile, string? File, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Loads project profiles from <c>projects/*.json</c> and validates them. A broken profile stays listed with its errors.</summary>
public sealed class ProjectRegistry(string projectsDir, AgentRegistry agents)
{
    private static readonly JsonSerializerOptions ReadOptions = new(Json.Options)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();
    private List<ProjectEntry> _entries = [];

    public static ProjectEntry DemoEntry { get; } = new(
        new ProjectProfile
        {
            Name = "demo",
            Workspace = WorkspaceMode.None,
            Stages = ["Spec", "Implement", "Verify", "Handover"],
            Gates = ["Spec"],
            IsDemo = true,
        },
        null,
        []);

    public IReadOnlyList<ProjectEntry> List()
    {
        lock (_lock)
        {
            return _entries;
        }
    }

    public ProjectEntry? Get(string name) =>
        List().FirstOrDefault(e => string.Equals(e.Profile.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Reload()
    {
        var entries = new List<ProjectEntry> { DemoEntry };

        if (Directory.Exists(projectsDir))
        {
            foreach (var file in Directory.EnumerateFiles(projectsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(Load(file, entries));
            }
        }

        lock (_lock)
        {
            _entries = entries;
        }
    }

    private ProjectEntry Load(string file, List<ProjectEntry> existing)
    {
        ProjectProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<ProjectProfile>(File.ReadAllText(file), ReadOptions) ?? new ProjectProfile();
        }
        catch (JsonException ex)
        {
            return new ProjectEntry(
                new ProjectProfile { Name = Path.GetFileNameWithoutExtension(file) },
                file,
                [$"Invalid JSON: {ex.Message}"]);
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = Path.GetFileNameWithoutExtension(file);
        }

        return new ProjectEntry(profile, file, Validate(profile, existing));
    }

    private List<string> Validate(ProjectProfile p, List<ProjectEntry> existing)
    {
        var errors = new List<string>();

        if (existing.Any(e => string.Equals(e.Profile.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Another project is already named '{p.Name}'.");
        }

        if (string.IsNullOrWhiteSpace(p.RepoPath))
        {
            errors.Add("repoPath is required.");
        }
        else if (!Directory.Exists(p.RepoPath))
        {
            errors.Add($"repoPath '{p.RepoPath}' does not exist.");
        }
        else if (!Directory.Exists(Path.Combine(p.RepoPath, ".git")) && !File.Exists(Path.Combine(p.RepoPath, ".git")))
        {
            errors.Add($"repoPath '{p.RepoPath}' is not a git repository.");
        }

        if (p.Workspace == WorkspaceMode.None)
        {
            errors.Add("workspace 'none' is only for the built-in demo project.");
        }

        if (string.IsNullOrWhiteSpace(p.BaseBranch))
        {
            errors.Add("baseBranch is required.");
        }

        if (!string.Equals(p.Branching, "run", StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Branching, "feature", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"branching '{p.Branching}' must be 'run' or 'feature'.");
        }

        if (!MoonWalk.Workspace.ReviewService.ValidBranchName(p.ReviewBranch))
        {
            errors.Add($"reviewBranch '{p.ReviewBranch}' is not a usable branch name (letters, digits, '.', '_', '-', '/'; not under 'moonwalk/').");
        }
        else if (string.Equals(p.ReviewBranch, p.BaseBranch, StringComparison.Ordinal))
        {
            errors.Add("reviewBranch must differ from baseBranch.");
        }

        if (!agents.IsKnown(p.Agent))
        {
            errors.Add($"Unknown agent '{p.Agent}'. Known: {string.Join(", ", agents.Names)}.");
        }

        if (p.Stages.Length == 0)
        {
            errors.Add("stages must list at least one stage.");
        }

        foreach (var stage in p.Stages.Where(s => !PipelineFactory.IsKnown(s)))
        {
            errors.Add($"Unknown stage '{stage}'. Known: {string.Join(", ", PipelineFactory.Known)}.");
        }

        foreach (var gate in p.Gates.Where(g => !p.Stages.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"Gate '{gate}' is not one of the project's stages.");
        }

        foreach (var step in p.Verify.Where(s => string.IsNullOrWhiteSpace(s.Command) && string.IsNullOrWhiteSpace(s.UnityMcp)))
        {
            errors.Add($"Verify step '{step.Name}' has no command (or unityMcp url).");
        }

        if (p.Stages.Contains("Verify", StringComparer.OrdinalIgnoreCase) && p.Verify.Length == 0)
        {
            errors.Add("The Verify stage needs at least one entry in 'verify'.");
        }

        foreach (var (name, server) in p.Mcp.Servers)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
            {
                errors.Add($"mcp server '{name}': use only letters, digits, '-' and '_' in the name.");
            }
            var type = (server.Type ?? "").ToLowerInvariant();
            if (type is not ("stdio" or "http"))
            {
                errors.Add($"mcp server '{name}': type must be 'stdio' or 'http'.");
            }
            else if (type == "stdio" && string.IsNullOrWhiteSpace(server.Command))
            {
                errors.Add($"mcp server '{name}': a stdio server needs a command.");
            }
            else if (type == "http" && string.IsNullOrWhiteSpace(server.Url))
            {
                errors.Add($"mcp server '{name}': an http server needs a url.");
            }
        }
        foreach (var (stage, names) in p.Mcp.Stages)
        {
            if (!stage.Equals("Spec", StringComparison.OrdinalIgnoreCase) && !stage.Equals("Implement", StringComparison.OrdinalIgnoreCase) && !stage.Equals("Integrate", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"mcp.stages: '{stage}' is not an agent stage (Spec, Implement or Integrate).");
            }
            foreach (var name in names.Where(n => !p.Mcp.Servers.ContainsKey(n)))
            {
                errors.Add($"mcp.stages.{stage}: server '{name}' is not defined under mcp.servers.");
            }
        }
        if (p.Mcp.Configured && p.Stages.Contains("Integrate", StringComparer.OrdinalIgnoreCase)
            && !(p.Mcp.Stages.TryGetValue("Integrate", out var integrateServers) && integrateServers.Length > 0))
        {
            errors.Add("The Integrate stage needs an MCP server: list one under mcp.stages.Integrate (for example the unity-mcp server).");
        }

        return errors;
    }
}
