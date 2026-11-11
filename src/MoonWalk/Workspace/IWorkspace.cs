using System.Text.Json;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Workspace;

/// <summary>Persisted per run (runs/&lt;id&gt;/workspace.json) so a held workspace can be released or recovered after a restart.</summary>
public sealed class WorkspaceState
{
    /// <summary>"Branch" (the project's own folder) or "Worktree" (a separate folder).</summary>
    public string Mode { get; set; } = "Branch";

    public string Branch { get; set; } = "";

    /// <summary>The folder the agent works in: the repository itself, or the worktree folder.</summary>
    public string WorkDir { get; set; } = "";

    /// <summary>Branch mode only: the branch (or commit) the repository was on before the run; restored on release.</summary>
    public string ReturnTo { get; set; } = "";

    public string BaseBranch { get; set; } = "";

    /// <summary>Branch mode only: paths that were already modified when the run started, with a cheap file signature.</summary>
    public Dictionary<string, string> Baseline { get; set; } = new();

    public static WorkspaceState? Load(string runDir)
    {
        var path = Path.Combine(runDir, "workspace.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(path), Json.Options) : null;
    }

    public void Save(string runDir) =>
        File.WriteAllText(Path.Combine(runDir, "workspace.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
}

public sealed record AgentChanges(IReadOnlyList<string> Paths, IReadOnlyList<string> Conflicts);

public sealed record WorkspaceSession(IWorkspace Workspace, WorkspaceState State);

/// <summary>What an agent did to git on its own while it ran: new commits on the run branch, and whether it pushed the branch.</summary>
public sealed record HeadDelta(
    string Head,
    bool Moved,
    IReadOnlyList<string> Commits,
    IReadOnlyList<string> ChangedPaths,
    int Lines,
    bool Pushed);

/// <summary>Where a run's code changes happen. Two implementations: the project's own folder on a run branch, or a separate git worktree.</summary>
public interface IWorkspace
{
    Task<WorkspaceState> PrepareAsync(string runId, string title, Action<string> log, CancellationToken ct);

    /// <summary>Makes sure the workspace is usable again after a MoonWalk restart (right branch checked out, folder present). Throws with a reason when not.</summary>
    Task EnsureReadyAsync(WorkspaceState state, CancellationToken ct);

    /// <summary>Everything changed since the run started, minus files that were already modified and are untouched by the agent.</summary>
    Task<AgentChanges> GetChangesAsync(WorkspaceState state, CancellationToken ct);

    /// <summary>Stages exactly these paths and returns how many lines they add or remove.</summary>
    Task<int> StageAsync(WorkspaceState state, IReadOnlyList<string> paths, string runDir, CancellationToken ct);

    /// <summary>Commits only the given paths and returns the short commit id.</summary>
    Task<string> CommitAsync(WorkspaceState state, IReadOnlyList<string> paths, string message, string runDir, CancellationToken ct);

    Task<(string Log, string DiffStat)> GetHandoverInfoAsync(WorkspaceState state, CancellationToken ct);

    /// <summary>The commit the run branch points at now. Taken before an agent runs, so <see cref="InspectAsync"/> can tell what it did.</summary>
    Task<string> GetHeadAsync(WorkspaceState state, CancellationToken ct);

    /// <summary>Compares the branch with <paramref name="headBefore"/>: did the agent commit, which files and lines did those commits touch, and did it push?</summary>
    Task<HeadDelta> InspectAsync(WorkspaceState state, string headBefore, CancellationToken ct);

    /// <summary>Keeps unfinished work as a local wip commit on the run branch, then gives the workspace back. Returns a warning when that was not fully possible.</summary>
    Task<string?> AbortAsync(WorkspaceState state, string runId, string runDir, CancellationToken ct);

    /// <summary>Gives the workspace back (branch mode: checks out the previous branch; worktree mode: removes the worktree folder, the branch stays).</summary>
    Task ReleaseAsync(WorkspaceState state, CancellationToken ct);
}

public static class WorkspaceFactory
{
    public static IWorkspace Create(ProjectProfile profile) => profile.Workspace switch
    {
        WorkspaceMode.Branch => new BranchWorkspace(profile.RepoPath, profile.BaseBranch, profile.DirtyPolicy, profile.IgnorePaths),
        WorkspaceMode.Worktree => new WorktreeWorkspace(profile.RepoPath, profile.BaseBranch, profile.WorktreeRoot),
        _ => throw new ArgumentException($"Workspace mode '{profile.Workspace}' has no workspace."),
    };
}
