namespace MoonWalk.Agents;

public delegate void EmitEvent(string type, string? text, Dictionary<string, object?>? data = null);

/// <summary>
/// One job for an agent. <paramref name="ReadOnly"/> asks for a run that must not change files (the Spec stage); agents map it to
/// whatever they have (tool lists for Claude, plan mode for agy). The allowed/disallowed lists, when set, replace the ones from the
/// project profile for this request. Whether an agent honoured read-only is always verified by the stage, never trusted.
/// </summary>
public sealed record AgentRequest(
    string Prompt,
    string WorkDir,
    string[]? AllowedTools = null,
    string[]? DisallowedTools = null,
    bool ReadOnly = false,
    double? MaxBudgetUsd = null);

public sealed record AgentResult(
    int ExitCode,
    bool TimedOut,
    string? Text,
    double? CostUsd,
    bool IsError,
    int? NumTurns,
    string? SessionId,
    string? Usage = null,
    string? Diagnostics = null,
    long? Tokens = null,
    string? Model = null);

public interface IAgent
{
    string Name { get; }

    /// <summary>Runs the agent to completion, streaming what it does through <paramref name="emit"/>. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct);
}
