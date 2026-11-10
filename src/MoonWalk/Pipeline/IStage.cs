using MoonWalk.Core;

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

public sealed class StageContext(string runId, string brief, string runDir, string stageName, EventHub hub)
{
    public string RunId { get; } = runId;

    public string Brief { get; } = brief;

    public string RunDir { get; } = runDir;

    public void Emit(string type, string? text, Dictionary<string, object?>? data = null) =>
        hub.Append(RunId, type, stageName, text, data);
}

public interface IStage
{
    string Name { get; }

    Task<StageResult> RunAsync(StageContext context, CancellationToken ct);
}
