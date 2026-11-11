using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Pipeline;

/// <summary>What <see cref="UnityMeta.CommitAsync"/> did: the commit it made (if any) and the new Unity files that still have no committed .meta.</summary>
public sealed record MetaReport(int Committed, string? Commit, IReadOnlyList<string> Missing);

/// <summary>
/// Unity gives every asset a .meta file holding its GUID, and scenes and prefabs refer to assets by that GUID. A script an agent added
/// is committed without one (Unity only writes it when the Editor imports the file), so a branch without the .meta breaks references
/// after a merge. Before Handover MoonWalk commits the .meta files that appeared during the run, and lists the new files that still lack one.
/// </summary>
public static class UnityMeta
{
    /// <summary>On when the profile says so; otherwise when the work folder looks like a Unity project (Assets and ProjectSettings).</summary>
    public static bool Applies(StageContext context) =>
        context.Profile.CommitMetaFiles
        ?? (Directory.Exists(Path.Combine(context.WorkDir, "Assets")) && Directory.Exists(Path.Combine(context.WorkDir, "ProjectSettings")));

    public static async Task<MetaReport> CommitAsync(StageContext context, WorkspaceSession workspace, CancellationToken ct)
    {
        var repo = context.WorkDir;
        var commit = (string?)null;
        var committed = 0;

        // New .meta files (not there when the run started) whose asset is already tracked. Anything else the Editor touched is left alone.
        var changes = await workspace.Workspace.GetChangesAsync(workspace.State, ct);
        var candidates = new List<string>();
        foreach (var path in changes.Paths.Where(p => p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
        {
            var alreadyTracked = (await Git.RunAsync(repo, ct, "ls-files", "--", path)).Out.Trim().Length > 0;
            var assetTracked = (await Git.RunAsync(repo, ct, "ls-files", "--", path[..^".meta".Length])).Out.Trim().Length > 0;
            if (!alreadyTracked && assetTracked)
            {
                candidates.Add(path);
            }
        }

        if (candidates.Count > 0)
        {
            var violations = Guards.Check(candidates, 0, context.Profile.Guards);
            if (violations.Count > 0)
            {
                foreach (var violation in violations)
                {
                    context.Emit(EventTypes.Warning, "Guard (.meta files, not committed): " + violation);
                }
            }
            else
            {
                await workspace.Workspace.StageAsync(workspace.State, candidates, context.RunDir, ct);
                var message = $"chore: add Unity .meta files\n\nAutomated by MoonWalk.\n\nMoonWalk-Run: {context.RunId}\n";
                commit = await workspace.Workspace.CommitAsync(workspace.State, candidates, message, context.RunDir, ct);
                committed = candidates.Count;
                context.UpdateRun(r => r.Commit = commit);
                context.Emit(EventTypes.System, $"Committed {committed} Unity .meta file(s) as {commit} on {workspace.State.Branch}.", new() { ["commit"] = commit });
            }
        }

        var missing = await FindMissingAsync(repo, workspace.State.BaseBranch, ct);
        if (missing.Count > 0)
        {
            context.Emit(EventTypes.Warning,
                $"{missing.Count} new Unity file(s) have no committed .meta (Unity writes it when the Editor imports the file): {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", ..." : "")}");
        }
        return new MetaReport(committed, commit, missing);
    }

    /// <summary>Files this branch added under Assets or Packages that Unity would import, with no .meta committed next to them.</summary>
    private static async Task<List<string>> FindMissingAsync(string repo, string baseBranch, CancellationToken ct)
    {
        var added = await Git.RunAsync(repo, ct, "diff", "--name-only", "--diff-filter=A", $"{baseBranch}...HEAD", "--", "Assets", "Packages");
        var missing = new List<string>();
        if (!added.Ok)
        {
            return missing;
        }
        foreach (var path in added.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || IsIgnoredByUnity(path))
            {
                continue;
            }
            if (!(await Git.RunAsync(repo, ct, "cat-file", "-e", $"HEAD:{path}.meta")).Ok)
            {
                missing.Add(path);
            }
        }
        return missing;
    }

    /// <summary>Unity skips files and folders that start with a dot or end with a tilde.</summary>
    private static bool IsIgnoredByUnity(string path) =>
        path.Split('/').Any(part => part.StartsWith('.') || part.EndsWith('~'));
}
