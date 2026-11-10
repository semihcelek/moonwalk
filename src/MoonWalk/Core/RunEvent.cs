namespace MoonWalk.Core;

public sealed record RunEvent(
    long Seq,
    DateTimeOffset Time,
    string RunId,
    string Type,
    string? Stage,
    string? Text,
    Dictionary<string, object?>? Data);

public static class EventTypes
{
    public const string RunStarted = "RunStarted";
    public const string RunFinished = "RunFinished";
    public const string StageStarted = "StageStarted";
    public const string StageFinished = "StageFinished";
    public const string AgentText = "AgentText";
    public const string AgentTextDelta = "AgentTextDelta";
    public const string AgentToolUse = "AgentToolUse";
    public const string AgentToolResult = "AgentToolResult";
    public const string System = "System";
    public const string Warning = "Warning";
    public const string Error = "Error";
}
