using System.Security.Cryptography;
using System.Text;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Workspace;

/// <summary>One path that is modified or untracked in the working folder. <see cref="Kind"/>: modified, added, deleted, renamed or untracked.</summary>
public sealed record DirtyFile(string Path, string Kind, bool Meta, bool Ignored);

/// <summary>
/// What is uncommitted in a project's folder. <see cref="Fingerprint"/> identifies exactly this list: a discard is only carried out when it
/// still matches, so nothing that appeared after the person looked at the list is thrown away unseen.
/// </summary>
public sealed record DirtyReport(string Branch, IReadOnlyList<DirtyFile> Files, int Total, int Meta, int Untracked, string Fingerprint);

public sealed record CheckoutResult(bool Ok, string Message, DirtyReport? Dirty = null);

// Checking out revisions for testing, and clearing a dirty working folder so that checkout can go ahead.
public sealed partial class ReviewService
{
    private const string StashTag = "moonwalk: before checkout";
    private const int KeepStashes = 5;

    /// <summary>The uncommitted changes in the project's working folder (tracked edits and untracked files; git-ignored files such as Unity's Library are not listed).</summary>
    public async Task<DirtyReport?> DirtyAsync(string projectName, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return null;
        }
        return await BuildReportAsync(entry.Profile, ct);
    }

    /// <summary>
    /// Switches the repository to <paramref name="target"/>: <c>review</c> (the review branch), <c>base</c>, <c>run:&lt;id&gt;</c> (that run's result:
    /// its branch, or for an earlier step of a feature the exact commit, detached) or <c>feature:&lt;name&gt;</c> (the feature branch as it is now).
    /// A dirty tree that blocks the switch comes back with the list, so the person can decide. <paramref name="discard"/> ("stash" or "delete") clears it first,
    /// but only when <paramref name="fingerprint"/> still matches the current list.
    /// </summary>
    public async Task<CheckoutResult> CheckoutAsync(string projectName, string target, string? discard, string? fingerprint, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return new CheckoutResult(false, "Choose a project that has a repository.");
        }
        if (BusyRuns(entry.Profile.Name) is { Count: > 0 } busy)
        {
            return new CheckoutResult(false, $"A run is still using this repository ({string.Join(", ", busy.Take(3))}). Finish or release it first.");
        }

        var gate = _locks.GetOrAdd(entry.Profile.RepoPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var repo = entry.Profile.RepoPath;
            var resolved = await ResolveTargetAsync(entry.Profile, target, ct);
            if (resolved.Error is not null)
            {
                return new CheckoutResult(false, resolved.Error);
            }

            string? cleared = null;
            if (!string.IsNullOrEmpty(discard))
            {
                var done = await DiscardCoreAsync(entry.Profile, discard, fingerprint, ct);
                if (!done.Ok)
                {
                    return done;
                }
                cleared = done.Message;
            }

            var args = resolved.Detach ? new[] { "checkout", "--detach", resolved.Ref! } : ["checkout", resolved.Ref!];
            var result = await Git.RunAsync(repo, ct, args);
            if (!result.Ok)
            {
                var report = await BuildReportAsync(entry.Profile, ct);
                var why = result.Err.Trim();
                return new CheckoutResult(false,
                    report.Total > 0
                        ? $"git could not switch to {resolved.Label}: your uncommitted changes are in the way. {why}"
                        : $"git could not switch to {resolved.Label}: {why}",
                    report.Total > 0 ? report : null);
            }
            return new CheckoutResult(true, (cleared is null ? "" : cleared + " ") + $"The repository is on {resolved.Label}.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Clears the working folder without switching anything. "stash" keeps everything in a git stash; "delete" discards it for good.</summary>
    public async Task<CheckoutResult> DiscardAsync(string projectName, string mode, string? fingerprint, CancellationToken ct)
    {
        if (Real(projectName) is not { } entry)
        {
            return new CheckoutResult(false, "Choose a project that has a repository.");
        }
        if (BusyRuns(entry.Profile.Name) is { Count: > 0 } busy)
        {
            return new CheckoutResult(false, $"A run is still using this repository ({string.Join(", ", busy.Take(3))}). Finish or release it first.");
        }
        var gate = _locks.GetOrAdd(entry.Profile.RepoPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await DiscardCoreAsync(entry.Profile, mode, fingerprint, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CheckoutResult> DiscardCoreAsync(ProjectProfile profile, string mode, string? fingerprint, CancellationToken ct)
    {
        if (mode is not ("stash" or "delete"))
        {
            return new CheckoutResult(false, "Discard mode must be 'stash' or 'delete'.");
        }
        var repo = profile.RepoPath;
        var report = await BuildReportAsync(profile, ct);
        if (report.Total == 0)
        {
            return new CheckoutResult(true, "The working folder is already clean.");
        }
        if (string.IsNullOrEmpty(fingerprint) || fingerprint != report.Fingerprint)
        {
            return new CheckoutResult(false, "The working folder changed since you looked at the list. Review it again before discarding.", report);
        }

        if (mode == "stash")
        {
            var stash = await Git.RunAsync(repo, ct, "stash", "push", "--include-untracked", "-m", $"{StashTag} ({report.Total} files)");
            if (!stash.Ok)
            {
                return new CheckoutResult(false, $"git could not stash the changes: {stash.Err.Trim()}");
            }
            await PruneStashesAsync(repo, ct);
            return new CheckoutResult(true, $"Saved {report.Total} changed file(s) in a git stash (git stash list); the working folder is clean.");
        }

        var reset = await Git.RunAsync(repo, ct, "reset", "--hard", "HEAD");
        if (!reset.Ok)
        {
            return new CheckoutResult(false, $"git reset failed: {reset.Err.Trim()}");
        }
        var clean = await Git.RunAsync(repo, ct, "clean", "-fd"); // not -x: git-ignored files (Unity's Library, Temp) stay
        if (!clean.Ok)
        {
            return new CheckoutResult(false, $"git clean failed: {clean.Err.Trim()}");
        }
        return new CheckoutResult(true, $"Discarded {report.Total} changed file(s) for good; the working folder is clean.");
    }

    /// <summary>Keeps the newest few stashes MoonWalk made, so repeated discards do not pile up. Other stashes are never touched.</summary>
    private static async Task PruneStashesAsync(string repo, CancellationToken ct)
    {
        var list = await Git.RunAsync(repo, ct, "stash", "list", "--format=%gd%x09%gs");
        if (!list.Ok)
        {
            return;
        }
        var ours = list.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t', 2))
            .Where(c => c.Length == 2 && c[1].Contains(StashTag, StringComparison.Ordinal))
            .Select(c => c[0])
            .ToList();
        // stash@{n} gets renumbered after a drop, so drop the oldest (highest n) first.
        foreach (var name in ours.Skip(KeepStashes).Reverse())
        {
            await Git.RunAsync(repo, ct, "stash", "drop", name);
        }
    }

    private async Task<DirtyReport> BuildReportAsync(ProjectProfile profile, CancellationToken ct)
    {
        var repo = profile.RepoPath;
        var branch = (await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD")).Out.Trim();
        if (branch.Length == 0)
        {
            branch = "(detached) " + (await Git.RunAsync(repo, ct, "rev-parse", "--short", "HEAD")).Out.Trim();
        }
        var ignore = profile.IgnorePaths.Select(Guards.ToRegex).ToList();
        var output = await Git.RunOrThrowAsync(repo, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<DirtyFile>();
        var codes = new List<string>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4)
            {
                continue;
            }
            var code = token[..2];
            var path = token[3..];
            if (code[0] is 'R' or 'C' && i + 1 < tokens.Length)
            {
                i++; // the original name of a rename is the next token
            }
            var kind = code == "??" ? "untracked"
                : code.Contains('D') ? "deleted"
                : code.Contains('A') ? "added"
                : code[0] == 'R' ? "renamed"
                : "modified";
            files.Add(new DirtyFile(path, kind, path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase), ignore.Any(r => r.IsMatch(path))));
            codes.Add(code + path);
        }
        codes.Sort(StringComparer.Ordinal);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', codes))))[..16];
        var shown = files.OrderBy(f => f.Kind == "untracked").ThenBy(f => f.Path, StringComparer.Ordinal).Take(300).ToList();
        return new DirtyReport(branch, shown, files.Count, files.Count(f => f.Meta), files.Count(f => f.Kind == "untracked"), fingerprint);
    }

    private sealed record Resolved(string? Ref, bool Detach, string Label, string? Error = null);

    private async Task<Resolved> ResolveTargetAsync(ProjectProfile profile, string target, CancellationToken ct)
    {
        var repo = profile.RepoPath;
        static Resolved Bad(string why) => new(null, false, "", why);

        async Task<bool> BranchExists(string name) => (await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{name}")).Ok;

        var (kind, id) = target.Contains(':') ? (target[..target.IndexOf(':')], target[(target.IndexOf(':') + 1)..]) : (target, "");
        switch (kind)
        {
            case "review":
                return await BranchExists(profile.ReviewBranch)
                    ? new Resolved(profile.ReviewBranch, false, $"'{profile.ReviewBranch}'")
                    : Bad($"'{profile.ReviewBranch}' does not exist yet. Merge a finished run into it first.");
            case "base":
                return await BranchExists(profile.BaseBranch)
                    ? new Resolved(profile.BaseBranch, false, $"'{profile.BaseBranch}'")
                    : Bad($"The base branch '{profile.BaseBranch}' does not exist.");
            case "feature":
            {
                var feature = features.Get(profile.Name, id);
                if (feature is null || !await BranchExists(feature.Branch))
                {
                    return Bad($"Feature '{id}' has no branch to check out.");
                }
                return new Resolved(feature.Branch, false, $"feature '{feature.Name}' ({feature.Branch})");
            }
            case "run":
            {
                var run = store.Get(id);
                if (run is null || run.Project != profile.Name)
                {
                    return Bad("Run not found in this project.");
                }
                if (string.IsNullOrEmpty(run.Branch) || !await BranchExists(run.Branch))
                {
                    return Bad("This run's branch no longer exists.");
                }
                if (run.Feature is not null && !string.IsNullOrEmpty(run.Commit))
                {
                    // An earlier step of a feature: the branch has moved on, so test the exact commit that run ended on.
                    var commit = (await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"{run.Commit}^{{commit}}")).Out.Trim();
                    var tip = (await Git.RunAsync(repo, ct, "rev-parse", $"refs/heads/{run.Branch}")).Out.Trim();
                    if (commit.Length > 0 && commit != tip)
                    {
                        return new Resolved(commit, true, $"the result of run {run.Id} (commit {commit[..8]}, detached: earlier step of feature '{run.Feature}')");
                    }
                }
                return new Resolved(run.Branch, false, $"'{run.Branch}'");
            }
            default:
                return Bad("Unknown target. Use review, base, run:<id> or feature:<name>.");
        }
    }
}
