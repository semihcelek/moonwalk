using MoonWalk.Projects;

namespace MoonWalk.Workspace;

/// <summary>
/// Gives each run its own git worktree folder (default: <c>wt-moonwalk/&lt;run&gt;</c> beside the repository), so several runs of
/// one project can work in parallel and your own working folder is never touched. The catch for Unity: a new folder means a full
/// import before you can open it. Release removes the folder; the run branch stays in the repository.
/// </summary>
public sealed class WorktreeWorkspace(string repoPath, string baseBranch, string? worktreeRoot) : IWorkspace
{
    public async Task<WorkspaceState> PrepareAsync(string runId, string title, Action<string> log, CancellationToken ct)
    {
        var inside = await Git.RunAsync(repoPath, ct, "rev-parse", "--is-inside-work-tree");
        if (!inside.Ok || inside.Out.Trim() != "true")
        {
            throw new WorkspaceException($"'{repoPath}' is not a git working tree.");
        }

        var remotes = await Git.RunOrThrowAsync(repoPath, ct, "remote");
        if (remotes.Split('\n', StringSplitOptions.TrimEntries).Contains("origin"))
        {
            log("Fetching origin.");
            var fetch = await Git.RunAsync(repoPath, ct, "fetch", "origin", "--quiet");
            if (!fetch.Ok)
            {
                log($"Fetch failed, continuing with local refs: {fetch.Err.Trim()}");
            }
        }

        var localBase = await Git.RunAsync(repoPath, ct, "rev-parse", "--verify", "-q", $"refs/heads/{baseBranch}");
        if (!localBase.Ok)
        {
            throw new WorkspaceException($"Base branch '{baseBranch}' does not exist locally.");
        }

        // Start from the freshest base without touching the user's own checkout.
        var remoteBase = await Git.RunAsync(repoPath, ct, "rev-parse", "--verify", "-q", $"refs/remotes/origin/{baseBranch}");
        var startPoint = baseBranch;
        if (remoteBase.Ok)
        {
            var ahead = await Git.RunAsync(repoPath, ct, "merge-base", "--is-ancestor", baseBranch, $"origin/{baseBranch}");
            if (ahead.Ok)
            {
                startPoint = $"origin/{baseBranch}";
            }
            else
            {
                log($"Local '{baseBranch}' is ahead of or diverged from origin/{baseBranch}; starting from the local branch.");
            }
        }

        var root = worktreeRoot is { Length: > 0 }
            ? worktreeRoot
            : Path.Combine(Directory.GetParent(repoPath.TrimEnd('\\', '/'))?.FullName ?? repoPath, "wt-moonwalk");
        var dir = Path.GetFullPath(Path.Combine(root, runId));
        Directory.CreateDirectory(root);

        var branch = $"moonwalk/{runId}-{BranchWorkspace.Slug(title)}";
        log($"Creating worktree {dir} on branch {branch} from {startPoint}.");
        await Git.RunOrThrowAsync(repoPath, ct, "worktree", "add", "-b", branch, dir, startPoint);

        return new WorkspaceState { Mode = "Worktree", Branch = branch, BaseBranch = baseBranch, WorkDir = dir };
    }

    public Task EnsureReadyAsync(WorkspaceState state, CancellationToken ct) =>
        Directory.Exists(state.WorkDir)
            ? Task.CompletedTask
            : throw new WorkspaceException($"The worktree folder '{state.WorkDir}' no longer exists.");

    public Task<AgentChanges> GetChangesAsync(WorkspaceState state, CancellationToken ct) => Inner(state).GetChangesAsync(state, ct);

    public Task<int> StageAsync(WorkspaceState state, IReadOnlyList<string> paths, string runDir, CancellationToken ct) =>
        Inner(state).StageAsync(state, paths, runDir, ct);

    public Task<string> CommitAsync(WorkspaceState state, IReadOnlyList<string> paths, string message, string runDir, CancellationToken ct) =>
        Inner(state).CommitAsync(state, paths, message, runDir, ct);

    public Task<(string Log, string DiffStat)> GetHandoverInfoAsync(WorkspaceState state, CancellationToken ct) =>
        Inner(state).GetHandoverInfoAsync(state, ct);

    public async Task<string?> AbortAsync(WorkspaceState state, string runId, string runDir, CancellationToken ct)
    {
        try
        {
            if (Directory.Exists(state.WorkDir))
            {
                await Inner(state).CommitWipAsync(state, runId, runDir, ct);
            }
            await ReleaseAsync(state, ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not clean up the worktree '{state.WorkDir}': {ex.Message}";
        }
    }

    public async Task ReleaseAsync(WorkspaceState state, CancellationToken ct)
    {
        if (Directory.Exists(state.WorkDir))
        {
            await Git.RunOrThrowAsync(repoPath, ct, "worktree", "remove", "--force", state.WorkDir);
        }
        await Git.RunAsync(repoPath, ct, "worktree", "prune");
    }

    // The worktree is an ordinary working tree on the run branch, so the branch workspace's file and commit logic applies to it.
    private BranchWorkspace Inner(WorkspaceState state) => new(state.WorkDir, baseBranch, DirtyPolicy.Baseline);
}
