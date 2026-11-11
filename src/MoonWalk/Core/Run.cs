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

/// <summary>What one agent attempt of one stage used. Dollars only where the agent reports them (Claude); tokens where it reports those.</summary>
public sealed class UsageEntry
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;
    public string Stage { get; set; } = "";
    public int Attempt { get; set; } = 1;
    public string Agent { get; set; } = "";
    public string? Model { get; set; }
    public long? Tokens { get; set; }
    public double? CostUsd { get; set; }
}

/// <summary>
/// Lets a run go on without you. <see cref="Plan"/> approves the gates after a stage (normally the plan); <see cref="Implementation"/>
/// approves the final review (Handover) and then gives the workspace back, so the next run can start. With both on the run is fully
/// automatic and a failed or blocked stage, or a passed budget, stops the run (as Failed) instead of waiting for a person.
/// </summary>
public sealed class AutoMode
{
    public bool Plan { get; set; }
    public bool Implementation { get; set; }

    /// <summary>After a run finishes Done, also merge its branch into the project's review branch (a conflict leaves it unmerged and says so).</summary>
    public bool MergeToReview { get; set; }

    public bool Full => Plan && Implementation;
    public bool Any => Plan || Implementation;
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

    /// <summary>Agent, model and effort picked per stage name (empty fields use the project's defaults). Set at start; a retry can change them.</summary>
    public Dictionary<string, MoonWalk.Agents.AgentChoice> StageAgents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Branch { get; set; }
    public string? ReturnTo { get; set; }

    /// <summary>"Branch" or "Worktree", once the workspace exists.</summary>
    public string? WorkspaceMode { get; set; }

    /// <summary>The folder the agent works in (the repository, or the worktree).</summary>
    public string? WorkDir { get; set; }

    /// <summary>Set when MoonWalk stopped while the run was waiting for you: the same question is asked again on recovery.</summary>
    public WaitState? ResumeWait { get; set; }
    public string? Commit { get; set; }
    public double? CostUsd { get; set; }

    /// <summary>True while this run still owns the project's working folder (the repo sits on the run branch).</summary>
    public bool WorkspaceHeld { get; set; }

    public WaitState? Waiting { get; set; }
    public bool PauseRequested { get; set; }

    /// <summary>The batch that started this run, and its place in it ("2/5 title"), when it came from a queue or a comparison.</summary>
    public string? BatchId { get; set; }
    public string? BatchLabel { get; set; }

    /// <summary>What this run may decide for itself. Set at start from the dialog or the project's default.</summary>
    public AutoMode Auto { get; set; } = new();

    /// <summary>Set when this run's branch was merged into the project's review branch: when, which branch, and the merge commit.</summary>
    public DateTimeOffset? ReviewMergedAt { get; set; }
    public string? ReviewBranch { get; set; }
    public string? ReviewCommit { get; set; }

    /// <summary>Hidden from the board unless "show archived" is on. Only finished runs can be archived.</summary>
    public bool Archived { get; set; }

    /// <summary>One entry per agent attempt of a stage; <see cref="CostUsd"/> is their sum.</summary>
    public List<UsageEntry> Usage { get; set; } = new();
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
