using System.Text;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Pipeline;

public enum StageOutcome
{
    Passed,
    Failed,
    NeedsHuman,
    Blocked,
}

public sealed record StageResult(StageOutcome Outcome, string? Summary = null)
{
    public static StageResult Passed(string? summary = null) => new(StageOutcome.Passed, summary);

    public static StageResult Failed(string summary) => new(StageOutcome.Failed, summary);

    public static StageResult NeedsHuman(string summary) => new(StageOutcome.NeedsHuman, summary);

    public static StageResult Blocked(string summary) => new(StageOutcome.Blocked, summary);
}

public sealed class StageContext
{
    public required string RunId { get; init; }

    public required string Brief { get; init; }

    public required string RunDir { get; init; }

    public required string StageName { get; init; }

    public required EventHub Hub { get; init; }

    public required ProjectProfile Profile { get; init; }

    public required Action<Action<Run>> UpdateRunAction { get; init; }

    public required Func<Run?> GetRunFunc { get; init; }

    public WorkspaceSession? Workspace { get; init; }

    public IAgent? Agent { get; init; }

    /// <summary>The folder the agent works in: the project's own folder, or the run's worktree.</summary>
    public string WorkDir => Workspace?.State.WorkDir is { Length: > 0 } dir ? dir : Profile.RepoPath;

    /// <summary>1 for the first time this stage runs in the run, 2 after a retry, and so on.</summary>
    public int Attempt { get; init; } = 1;

    public IReadOnlyList<string> Guidance { get; init; } = [];

    /// <summary>Why the previous stage failed, when this stage is running again because of it.</summary>
    public string? PreviousFailure { get; init; }

    public void Emit(string type, string? text, Dictionary<string, object?>? data = null) =>
        Hub.Append(RunId, type, StageName, text, data);

    public void UpdateRun(Action<Run> mutate) => UpdateRunAction(mutate);

    public Run? GetRun() => GetRunFunc();

    public void AddCost(double usd) => UpdateRun(r => r.CostUsd = (r.CostUsd ?? 0) + usd);

    /// <summary>Dollars left in the run's budget before this stage started, or null when the project sets no dollar limit. Passed to agents that can enforce it.</summary>
    public double? RemainingBudgetUsd { get; init; }

    /// <summary>Records what an agent attempt used: one entry in the run's usage ledger, and the dollars added to the run's cost.</summary>
    public void AddUsage(string agentName, AgentResult result)
    {
        if (result.Tokens is null && result.CostUsd is null)
        {
            return;
        }
        UpdateRun(r =>
        {
            r.Usage.Add(new UsageEntry
            {
                Stage = StageName,
                Attempt = Attempt,
                Agent = agentName,
                Model = result.Model,
                Tokens = result.Tokens,
                CostUsd = result.CostUsd,
            });
            if (result.CostUsd is { } usd)
            {
                r.CostUsd = (r.CostUsd ?? 0) + usd;
            }
        });
    }

    public void SetStageAgent(string agentName) =>
        UpdateRun(r => r.Stages.First(s => s.Name == StageName).Agent = agentName);

    public void AddArtifact(string name) =>
        UpdateRun(r =>
        {
            if (!r.Artifacts.Contains(name))
            {
                r.Artifacts.Add(name);
            }
        });

    /// <summary>Writes a text file under the run folder and lists it as an artifact the UI can show.</summary>
    public void WriteArtifact(string name, string text)
    {
        var path = Path.Combine(RunDir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        AddArtifact(name);
    }

    /// <summary>Writes a binary file (a screenshot) under the run folder and lists it as an artifact the UI can show.</summary>
    public void WriteArtifactBytes(string name, byte[] bytes)
    {
        var path = Path.Combine(RunDir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        AddArtifact(name);
    }

    /// <summary>
    /// The MCP servers this stage's agent may use: a config file MoonWalk writes into the run folder and the Claude tool patterns that allow
    /// exactly those servers (Muse has no tool allow-list: it is handed the same config and gets just the listed servers, so its patterns are empty).
    /// Null when the project configures no MCP (nothing changes), or when the agent is neither Claude nor Muse (a warning says the stage runs without them).
    /// </summary>
    public McpAccess? McpFor(string stageName)
    {
        if (!Profile.Mcp.Configured)
        {
            return null;
        }
        var names = Profile.Mcp.Stages.TryGetValue(stageName, out var listed) ? listed : [];
        if (Agent is not { Name: "claude" or "muse" })
        {
            if (names.Length > 0)
            {
                Emit(EventTypes.Warning, $"MCP servers are configured for {stageName} ({string.Join(", ", names)}) but only the claude and muse agents can be given them; {Agent?.Name ?? "this agent"} runs without them.");
            }
            return null;
        }

        var servers = new Dictionary<string, object>();
        foreach (var name in names)
        {
            var server = Profile.Mcp.Servers[name];
            servers[name] = string.Equals(server.Type, "http", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, object?> { ["type"] = "http", ["url"] = server.Url }
                : new Dictionary<string, object?> { ["type"] = "stdio", ["command"] = server.Command, ["args"] = server.Args, ["env"] = server.Env };
        }
        var file = Path.Combine(RunDir, $"mcp-{stageName.ToLowerInvariant()}.json");
        Directory.CreateDirectory(RunDir);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { mcpServers = servers }));
        Emit(EventTypes.System, names.Length > 0 ? $"MCP for {stageName}: {string.Join(", ", names)}." : $"MCP for {stageName}: none (only the project's configured servers are allowed, and none is listed for this stage).");
        return new McpAccess(file, Agent.Name == "claude" ? names.Select(n => $"mcp__{n}__*").ToArray() : []);
    }

    /// <summary>Prompt sections for guidance notes and the reason the previous attempt failed.</summary>
    public string PromptContext()
    {
        var sb = new StringBuilder();
        if (GetRun() is { Rounds: > 1 } run)
        {
            sb.AppendLine($"## Follow-up round {run.Rounds}");
            sb.AppendLine("This branch already holds the finished work for the brief (earlier commits, listed by `git log --oneline`). It was reviewed and needs corrections.");
            sb.AppendLine("Read the existing code first, keep what works, and change only what the latest guidance below asks for.");
            sb.AppendLine();
        }
        if (Guidance.Count > 0)
        {
            sb.AppendLine("## Guidance from the user (follow it)");
            foreach (var note in Guidance)
            {
                sb.AppendLine("- " + note.ReplaceLineEndings(" "));
            }
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(PreviousFailure))
        {
            sb.AppendLine("## What went wrong last time");
            sb.AppendLine(PreviousFailure);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>What <see cref="StageContext.McpFor"/> hands a stage: the per-stage MCP config file and the Claude tool patterns that allow it.</summary>
public sealed record McpAccess(string ConfigFile, string[] ToolPatterns);

public interface IStage
{
    string Name { get; }

    Task<StageResult> RunAsync(StageContext context, CancellationToken ct);
}
