using System.Globalization;
using System.Text.RegularExpressions;

namespace MoonWalk.Workspace;

public sealed record BranchInfo(
    string Name,
    bool Merged,
    int Ahead,
    DateTimeOffset? LastCommit,
    string Subject,
    string? RunId,
    bool Current,
    string? Worktree);

/// <summary>The <c>moonwalk/*</c> branches runs leave behind: which are merged, which are in use, and deleting them safely.</summary>
public static partial class BranchHygiene
{
    public const string Prefix = "moonwalk/";

    /// <summary>The run a branch belongs to, read from its name (<c>moonwalk/&lt;run id&gt;-slug</c>), or null.</summary>
    public static string? RunIdOf(string branch) =>
        RunIdPattern().Match(branch) is { Success: true } m ? m.Groups[1].Value : null;

    public static async Task<List<BranchInfo>> ListAsync(string repo, string baseBranch, CancellationToken ct)
    {
        var refs = await Git.RunOrThrowAsync(repo, ct, "for-each-ref", "--format=%(refname:short)\t%(committerdate:iso-strict)\t%(subject)", $"refs/heads/{Prefix}");
        var merged = (await Git.RunAsync(repo, ct, "branch", "--merged", baseBranch, "--list", $"{Prefix}*")).Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(b => b.TrimStart('*', '+', ' '))
            .ToHashSet();
        var current = (await Git.RunAsync(repo, ct, "symbolic-ref", "--short", "-q", "HEAD")).Out.Trim();
        var worktrees = await WorktreesAsync(repo, ct);

        var result = new List<BranchInfo>();
        foreach (var line in refs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('\t', 3);
            var name = cols[0];
            var ahead = int.TryParse((await Git.RunAsync(repo, ct, "rev-list", "--count", $"{baseBranch}..{name}")).Out.Trim(), out var n) ? n : 0;
            result.Add(new BranchInfo(
                name,
                merged.Contains(name),
                ahead,
                cols.Length > 1 && DateTimeOffset.TryParse(cols[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null,
                cols.Length > 2 ? cols[2] : "",
                RunIdOf(name),
                name == current,
                worktrees.GetValueOrDefault(name)));
        }
        return result.OrderByDescending(b => b.LastCommit).ToList();
    }

    /// <summary>Deletes a run branch. An unmerged branch needs <paramref name="force"/>. Returns false with the reason when it was refused.</summary>
    public static async Task<(bool Ok, string Message)> DeleteAsync(string repo, string branch, bool merged, bool force, CancellationToken ct)
    {
        if (!branch.StartsWith(Prefix, StringComparison.Ordinal) || branch.Contains("..") || branch.Contains(' '))
        {
            return (false, $"Only branches starting with '{Prefix}' can be deleted here.");
        }
        if (!merged && !force)
        {
            return (false, "This branch has commits that are not in the base branch. Deleting it loses them; confirm to force it.");
        }

        // "Merged" was already decided against the project's base branch. git's own -d would judge it against whatever is checked out, so a
        // branch merged into the base could be refused (or an unrelated one allowed); -D is safe because of the check above.
        var result = await Git.RunAsync(repo, ct, "branch", "-D", "--", branch);
        return result.Ok ? (true, $"Deleted {branch}.") : (false, result.Err.Trim());
    }

    public static async Task<string> PruneWorktreesAsync(string repo, CancellationToken ct)
    {
        await Git.RunOrThrowAsync(repo, ct, "worktree", "prune");
        return "Pruned worktree records whose folders no longer exist.";
    }

    private static async Task<Dictionary<string, string>> WorktreesAsync(string repo, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        var list = await Git.RunAsync(repo, ct, "worktree", "list", "--porcelain");
        string? path = null;
        foreach (var line in list.Out.Split('\n'))
        {
            if (line.StartsWith("worktree "))
            {
                path = line["worktree ".Length..].Trim();
            }
            else if (line.StartsWith("branch refs/heads/") && path is not null)
            {
                map[line["branch refs/heads/".Length..].Trim()] = path;
            }
        }
        return map;
    }

    [GeneratedRegex(@"^moonwalk/(\d{8}-\d{6}-[0-9a-f]{3,5})-")]
    private static partial Regex RunIdPattern();
}
