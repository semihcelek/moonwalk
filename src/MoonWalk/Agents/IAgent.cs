namespace MoonWalk.Agents;

public delegate void EmitEvent(string type, string? text, Dictionary<string, object?>? data = null);

/// <summary>Allowed/disallowed tool lists, when set, replace the ones from the project profile for this request (the Spec stage uses read-only tools).</summary>
public sealed record AgentRequest(string Prompt, string WorkDir, string[]? AllowedTools = null, string[]? DisallowedTools = null);

public sealed record AgentResult(
    int ExitCode,
    bool TimedOut,
    string? Text,
    double? CostUsd,
    bool IsError,
    int? NumTurns,
    string? SessionId);

public interface IAgent
{
    string Name { get; }

    /// <summary>Runs the agent to completion, streaming what it does through <paramref name="emit"/>. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<AgentResult> RunAsync(AgentRequest request, EmitEvent emit, CancellationToken ct);
}
