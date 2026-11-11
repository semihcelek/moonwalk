using MoonWalk.Projects;

namespace MoonWalk.Workspace;

/// <summary>One line of the pre-flight check: <c>ok</c>, <c>info</c>, <c>warn</c> (the run will work but something is worth knowing) or <c>block</c> (it will refuse or fail).</summary>
public sealed record PreflightNote(string Level, string Text);

/// <summary>
/// What would happen if a run started on this project right now, so the New run dialog can say it before you press Start: where the repository is,
/// whether uncommitted changes would stop the run or fail the checkout of the base branch, and whether another run holds the workspace.
/// Read-only: it only runs git queries.
/// </summary>
public static class Preflight
{
    public static async Task<IReadOnlyList<PreflightNote>> CheckAsync(ProjectProfile profile, (string Id, string Title)? holder, CancellationToken ct)
    {
        var notes = new List<PreflightNote>();
        if (profile.IsDemo || profile.Workspace == WorkspaceMode.None)
        {
            return notes;
        }

        var repo = profile.RepoPath;
        var inside = await Git.RunAsync(repo, ct, "rev-parse", "--is-inside-work-tree");
        if (!inside.Ok || inside.Out.Trim() != "true")
        {
            notes.Add(new("block", $"'{repo}' is not a git working tree."));
            return notes;
        }
        if (!(await Git.RunAsync(repo, ct, "rev-parse", "--verify", "-q", $"refs/heads/{profile.BaseBranch}")).Ok)
        {
            notes.Add(new("block", $"The base branch '{profile.BaseBranch}' does not exist locally."));
            return notes;
        }

        if (profile.Workspace == WorkspaceMode.Branch)
        {
            if (holder is { } h)
            {
                notes.Add(new("warn", $"Run {h.Id} ({h.Title}) still holds this project's workspace. A new run waits until it is released."));
            }

            var current = await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD");
            var branch = current.Ok ? current.Out.Trim() : "(detached HEAD)";
            var onBase = branch == profile.BaseBranch;
            if (!onBase)
            {
                notes.Add(branch.StartsWith("moonwalk/", StringComparison.Ordinal)
                    ? new("info", $"The repository is on the run branch '{branch}'.")
                    : new("warn", $"The repository is on '{branch}'. A run switches to '{profile.BaseBranch}' first and goes back here when released."));
            }

            var dirty = await DirtyPathsAsync(repo, profile, ct);
            if (dirty.Count > 0)
            {
                var sample = string.Join(", ", dirty.Take(4)) + (dirty.Count > 4 ? ", ..." : "");
                notes.Add(profile.DirtyPolicy == DirtyPolicy.Refuse
                    ? new("block", $"{dirty.Count} uncommitted change(s) ({sample}): the run will refuse to start. Commit or stash them, or set dirtyPolicy to 'baseline' in the profile.")
                    : new("warn", $"{dirty.Count} file(s) already modified ({sample}). They are remembered and left alone; the run stops if the agent edits one of them."));

                if (!onBase)
                {
                    // Git refuses to switch branches when an uncommitted change touches a file that differs between the two.
                    var differ = await Git.RunAsync(repo, ct, "diff", "--name-only", "HEAD", profile.BaseBranch);
                    if (differ.Ok)
                    {
                        var changed = differ.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
                        var clash = dirty.Where(changed.Contains).Take(3).ToList();
                        if (clash.Count > 0)
                        {
                            notes.Add(new("block", $"Git will refuse to switch to '{profile.BaseBranch}': your uncommitted change to {string.Join(", ", clash)} touches a file that differs there. Commit or stash it first."));
                        }
                    }
                }
            }
        }

        if (notes.Count == 0)
        {
            notes.Add(new("ok", profile.Workspace == WorkspaceMode.Branch
                ? $"Ready: the repository is on '{profile.BaseBranch}' and clean."
                : $"Ready: each run gets its own worktree from '{profile.BaseBranch}'."));
        }
        return notes;
    }

    private static async Task<List<string>> DirtyPathsAsync(string repo, ProjectProfile profile, CancellationToken ct)
    {
        var ignore = profile.IgnorePaths.Select(Guards.ToRegex).ToList();
        var output = await Git.RunAsync(repo, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var paths = new List<string>();
        if (!output.Ok)
        {
            return paths;
        }
        var tokens = output.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4)
            {
                continue;
            }
            var path = token[3..];
            if (token[0] is 'R' or 'C' && i + 1 < tokens.Length)
            {
                i++; // the second path of a rename
            }
            if (!ignore.Any(r => r.IsMatch(path)))
            {
                paths.Add(path);
            }
        }
        return paths;
    }
}
