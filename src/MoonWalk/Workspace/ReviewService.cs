using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Workspace;

public sealed record ReviewMerge(string Sha, string Subject, DateTimeOffset? Date, string? RunId);

public sealed record ReviewStatus(string Branch, bool Exists, string BaseBranch, int Ahead, bool Current, IReadOnlyList<ReviewMerge> Merges);

public sealed record ReviewResult(bool Ok, string Message, IReadOnlyList<string> Conflicts, string? Commit = null);

/// <summary>
/// One branch (default <c>waiting-review</c>) that finished runs are merged into, so several results can be tested together with a single
/// checkout. A merge never touches the working folder: it is computed with <c>git merge-tree</c> and written with <c>commit-tree</c> and
/// <c>update-ref</c>, so it is safe while a run holds the repository on its own branch. The one exception is when the review branch is the
/// branch checked out right now: then a real merge is used (only on a clean tree), because moving a checked-out branch underneath the
/// working folder would leave it out of sync. A conflict changes nothing and lists the files.
/// </summary>
public sealed partial class ReviewService(ProjectRegistry projects, RunStore store, EventHub hub)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public static bool ValidBranchName(string? name) =>
        name is { Length: > 0 and <= 60 }
        && BranchPattern().IsMatch(name)
        && !name.Contains("..")
        && !name.EndsWith('/') && !name.EndsWith(".lock")
        && !name.StartsWith(BranchHygiene.Prefix, StringComparison.Ordinal);

    public async Task<ReviewStatus?> StatusAsync(string projectName, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return null;
        }
        var repo = entry.Profile.RepoPath;
        var review = entry.Profile.ReviewBranch;
        var exists = (await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{review}")).Ok;
        if (!exists)
        {
            return new ReviewStatus(review, false, entry.Profile.BaseBranch, 0, false, []);
        }

        var ahead = int.TryParse((await Git.RunAsync(repo, ct, "rev-list", "--count", $"{entry.Profile.BaseBranch}..{review}")).Out.Trim(), out var n) ? n : 0;
        var current = (await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD")).Out.Trim() == review;
        var log = await Git.RunAsync(repo, ct, "log", "--first-parent", "--merges", "--format=%h\t%cI\t%s", $"{entry.Profile.BaseBranch}..{review}");
        var merges = log.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var cols = line.Split('\t', 3);
            var subject = cols.Length > 2 ? cols[2] : "";
            var branch = BranchToken().Match(subject) is { Success: true } m ? m.Value : "";
            return new ReviewMerge(cols[0],
                subject,
                cols.Length > 1 && DateTimeOffset.TryParse(cols[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null,
                BranchHygiene.RunIdOf(branch));
        }).ToList();
        return new ReviewStatus(review, true, entry.Profile.BaseBranch, ahead, current, merges);
    }

    /// <summary>Merges a finished run's branch into the project's review branch. Nothing changes unless it returns Ok.</summary>
    public async Task<ReviewResult> MergeRunAsync(string runId, CancellationToken ct)
    {
        var run = store.Get(runId);
        if (run is null)
        {
            return Fail("Run not found.");
        }
        if (run.Status != RunStatus.Done)
        {
            return Fail("Only a run that finished (Done) can be merged into the review branch.");
        }
        if (string.IsNullOrEmpty(run.Branch))
        {
            return Fail("This run has no git branch.");
        }
        if (Real(run.Project) is not { } entry)
        {
            return Fail($"Project '{run.Project}' is missing or has no repository.");
        }

        var gate = _locks.GetOrAdd(entry.Profile.RepoPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var repo = entry.Profile.RepoPath;
            var review = entry.Profile.ReviewBranch;
            var branch = run.Branch;

            if (!(await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{branch}")).Ok)
            {
                return Fail($"The run's branch '{branch}' no longer exists.");
            }
            if (!(await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{review}")).Ok)
            {
                var created = await Git.RunAsync(repo, ct, "branch", review, entry.Profile.BaseBranch);
                if (!created.Ok)
                {
                    return Fail($"Could not create '{review}' from '{entry.Profile.BaseBranch}': {created.Err.Trim()}");
                }
            }

            if ((await Git.RunAsync(repo, ct, "merge-base", "--is-ancestor", branch, review)).Ok)
            {
                var head = (await Git.RunAsync(repo, ct, "rev-parse", "--short", review)).Out.Trim();
                MarkMerged(run.Id, review, head);
                return new ReviewResult(true, $"'{branch}' is already in '{review}'.", [], head);
            }

            var message = $"Merge {branch} into {review}\n\n{run.Title}\n\nMoonWalk-Run: {run.Id}";
            var checkedOut = (await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD")).Out.Trim() == review;
            var result = checkedOut
                ? await MergeCheckedOutAsync(repo, branch, message, ct)
                : await MergeWithoutCheckoutAsync(repo, review, branch, message, ct);

            if (result.Ok && result.Commit is { } sha)
            {
                MarkMerged(run.Id, review, sha);
                hub.Append(run.Id, EventTypes.System, null, $"Merged into '{review}' ({sha}).");
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Switches the repository to the review branch, so everything merged into it can be tested from one checkout.</summary>
    public async Task<ReviewResult> CheckoutAsync(string projectName, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return Fail("Choose a project that has a repository.");
        }
        if (BusyRuns(entry.Profile.Name) is { Count: > 0 } busy)
        {
            return Fail($"A run is still using this repository ({string.Join(", ", busy.Take(3))}). Finish or release it first.");
        }
        var repo = entry.Profile.RepoPath;
        var review = entry.Profile.ReviewBranch;
        if (!(await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{review}")).Ok)
        {
            return Fail($"'{review}' does not exist yet. Merge a finished run into it first.");
        }

        var checkout = await Git.RunAsync(repo, ct, "checkout", review);
        return checkout.Ok
            ? new ReviewResult(true, $"The repository is on '{review}'.", [])
            : Fail($"git could not switch branches: {checkout.Err.Trim()}");
    }

    /// <summary>Points the review branch back at the base branch, to start a new round. The merged runs lose their "in review" mark; their own branches stay.</summary>
    public async Task<ReviewResult> ResetAsync(string projectName, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return Fail("Choose a project that has a repository.");
        }
        var repo = entry.Profile.RepoPath;
        var review = entry.Profile.ReviewBranch;
        if ((await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD")).Out.Trim() == review)
        {
            return Fail($"'{review}' is checked out. Switch the repository to another branch first.");
        }
        if (!(await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{review}")).Ok)
        {
            return new ReviewResult(true, $"'{review}' does not exist; nothing to reset.", []);
        }

        var moved = await Git.RunAsync(repo, ct, "branch", "-f", review, entry.Profile.BaseBranch);
        if (!moved.Ok)
        {
            return Fail(moved.Err.Trim());
        }
        foreach (var r in AllRuns().Where(r => r.Project == entry.Profile.Name && r.ReviewMergedAt is not null))
        {
            store.Update(r.Id, x =>
            {
                x.ReviewMergedAt = null;
                x.ReviewBranch = null;
                x.ReviewCommit = null;
            });
        }
        return new ReviewResult(true, $"'{review}' is back at '{entry.Profile.BaseBranch}'.", []);
    }

    private async Task<ReviewResult> MergeWithoutCheckoutAsync(string repo, string review, string branch, string message, CancellationToken ct)
    {
        var tip = (await Git.RunOrThrowAsync(repo, ct, "rev-parse", $"refs/heads/{review}")).Trim();
        var merge = await Git.RunAsync(repo, ct, "merge-tree", "--write-tree", "--name-only", "--no-messages", review, branch);
        var lines = merge.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (merge.Exit == 1)
        {
            var files = lines.Skip(1).Distinct().ToList();
            return new ReviewResult(false, $"These changes conflict with what is already in '{review}': {string.Join(", ", files.Take(8))}. Nothing was merged.", files);
        }
        if (merge.Exit != 0 || lines.Length == 0)
        {
            return Fail($"git could not merge: {merge.Err.Trim()}");
        }

        var commit = await Git.RunAsync(repo, ct, "commit-tree", lines[0], "-p", review, "-p", branch, "-m", message);
        if (!commit.Ok)
        {
            return Fail($"git could not write the merge commit (is user.name and user.email set?): {commit.Err.Trim()}");
        }
        var sha = commit.Out.Trim();
        var update = await Git.RunAsync(repo, ct, "update-ref", $"refs/heads/{review}", sha, tip);
        return update.Ok
            ? new ReviewResult(true, $"Merged into '{review}'.", [], sha[..Math.Min(7, sha.Length)])
            : Fail($"'{review}' changed while merging; try again. {update.Err.Trim()}");
    }

    private async Task<ReviewResult> MergeCheckedOutAsync(string repo, string branch, string message, CancellationToken ct)
    {
        var dirty = (await Git.RunAsync(repo, ct, "status", "--porcelain", "--untracked-files=no")).Out.Trim();
        if (dirty.Length > 0)
        {
            return Fail("The review branch is checked out and has uncommitted changes. Commit or stash them, or switch to another branch, then merge again.");
        }

        var merge = await Git.RunAsync(repo, ct, "merge", "--no-ff", "-m", message, branch);
        if (!merge.Ok)
        {
            var files = (await Git.RunAsync(repo, ct, "diff", "--name-only", "--diff-filter=U")).Out
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            await Git.RunAsync(repo, ct, "merge", "--abort");
            return new ReviewResult(false, $"These changes conflict with what is already checked out: {string.Join(", ", files.Take(8))}. The merge was undone.", files);
        }
        var sha = (await Git.RunAsync(repo, ct, "rev-parse", "--short", "HEAD")).Out.Trim();
        return new ReviewResult(true, "Merged into the checked-out review branch.", [], sha);
    }

    private void MarkMerged(string runId, string review, string sha) =>
        store.Update(runId, r =>
        {
            r.ReviewMergedAt = DateTimeOffset.Now;
            r.ReviewBranch = review;
            r.ReviewCommit = sha;
        });

    private List<string> BusyRuns(string project) =>
        AllRuns().Where(r => r.Project == project && (Run.IsActive(r.Status) || r.WorkspaceHeld)).Select(r => r.Id).ToList();

    private IEnumerable<Run> AllRuns() =>
        System.Text.Json.JsonSerializer.Deserialize<List<Run>>(store.ListJson(), Json.Options) ?? [];

    private ProjectEntry? Real(string name) =>
        projects.Get(name) is { IsValid: true, Profile.IsDemo: false } entry ? entry : null;

    private static ReviewResult Fail(string message) => new(false, message, []);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex BranchPattern();

    [GeneratedRegex(@"moonwalk/\S+")]
    private static partial Regex BranchToken();
}
