namespace MoonWalk.Core;

public enum RunStatus
{
    Queued,
    Running,
    WaitingForHuman,
    Paused,
    Done,
    Failed,
    Cancelled,
    Interrupted,
}

public enum StageStatus
{
    Pending,
    Running,
    Passed,
    Failed,
    Blocked,
    NeedsHuman,
    Cancelled,
}

public sealed class StageRecord
{
    public string Name { get; set; } = "";
    public string? Agent { get; set; }
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Summary { get; set; }
}

/// <summary>What a run is waiting for while its status is WaitingForHuman.</summary>
public sealed class WaitState
{
    public string Stage { get; set; } = "";

    /// <summary>Gate (approve before continuing), Review (stage asks for a human, e.g. playtest), Failed, or Blocked.</summary>
    public string Reason { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Failed and Blocked stages cannot be approved, only retried or stopped.</summary>
    public bool CanApprove { get; set; }
}

public sealed class GuidanceNote
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Text { get; set; } = "";
    public string? Stage { get; set; }
}

public sealed class Run
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Title { get; set; } = "";
    public string Brief { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Queued;
    public string? CurrentStage { get; set; }
    public string? Agent { get; set; }
    public string? Branch { get; set; }
    public string? ReturnTo { get; set; }
    public string? Commit { get; set; }
    public double? CostUsd { get; set; }

    /// <summary>True while this run still owns the project's working folder (the repo sits on the run branch).</summary>
    public bool WorkspaceHeld { get; set; }

    public WaitState? Waiting { get; set; }
    public bool PauseRequested { get; set; }
    public List<GuidanceNote> Guidance { get; set; } = new();

    /// <summary>Files under runs/&lt;id&gt;/ the UI may show (spec.md, evidence/verify.log, handover.md).</summary>
    public List<string> Artifacts { get; set; } = new();

    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<StageRecord> Stages { get; set; } = new();

    public static bool IsActive(RunStatus status) =>
        status is RunStatus.Queued or RunStatus.Running or RunStatus.Paused or RunStatus.WaitingForHuman;
}
