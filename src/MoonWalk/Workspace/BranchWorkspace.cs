using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Workspace;

/// <summary>Persisted per run (runs/&lt;id&gt;/workspace.json) so a held workspace can be released after a restart.</summary>
public sealed class WorkspaceState
{
    public string Branch { get; set; } = "";

    /// <summary>The branch (or commit) the repository was on before the run; restored on release.</summary>
    public string ReturnTo { get; set; } = "";

    public string BaseBranch { get; set; } = "";

    /// <summary>Paths that were already modified when the run started, with a cheap file signature.</summary>
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

public sealed record WorkspaceSession(BranchWorkspace Workspace, WorkspaceState State);

/// <summary>
/// Runs a job on its own branch inside the project's normal working folder (no second checkout, so Unity does not
/// reimport the project). Only one run per project may use it at a time; the engine enforces that.
/// </summary>
public sealed class BranchWorkspace(string repoPath, string baseBranch, DirtyPolicy policy)
{
    public async Task<WorkspaceState> PrepareAsync(string runId, string title, Action<string> log, CancellationToken ct)
    {
        var inside = await Git.RunAsync(repoPath, ct, "rev-parse", "--is-inside-work-tree");
        if (!inside.Ok || inside.Out.Trim() != "true")
        {
            throw new WorkspaceException($"'{repoPath}' is not a git working tree.");
        }

        var dirty = await StatusPathsAsync(ct);
        if (dirty.Count > 0 && policy == DirtyPolicy.Refuse)
        {
            var sample = string.Join(", ", dirty.Take(8)) + (dirty.Count > 8 ? ", ..." : "");
            throw new WorkspaceException(
                $"The working tree has {dirty.Count} uncommitted change(s) ({sample}). Commit or stash them, or set dirtyPolicy to 'baseline' in the project profile.");
        }

        var state = new WorkspaceState { BaseBranch = baseBranch };
        foreach (var path in dirty)
        {
            state.Baseline[path] = Signature(path);
        }
        if (state.Baseline.Count > 0)
        {
            log($"Baseline: {state.Baseline.Count} file(s) were already modified and will be left alone.");
        }

        var current = await Git.RunAsync(repoPath, ct, "symbolic-ref", "--short", "-q", "HEAD");
        state.ReturnTo = current.Ok
            ? current.Out.Trim()
            : (await Git.RunOrThrowAsync(repoPath, ct, "rev-parse", "HEAD")).Trim();

        var remotes = await Git.RunOrThrowAsync(repoPath, ct, "remote");
        var hasOrigin = remotes.Split('\n', StringSplitOptions.TrimEntries).Contains("origin");
        if (hasOrigin)
        {
            log("Fetching origin.");
            var fetch = await Git.RunAsync(repoPath, ct, "fetch", "origin", "--quiet");
            if (!fetch.Ok)
            {
                log($"Fetch failed, continuing with local refs: {fetch.Err.Trim()}");
            }
        }

        var baseExists = await Git.RunAsync(repoPath, ct, "rev-parse", "--verify", "-q", $"refs/heads/{baseBranch}");
        if (!baseExists.Ok)
        {
            throw new WorkspaceException($"Base branch '{baseBranch}' does not exist locally.");
        }

        if (state.ReturnTo != baseBranch)
        {
            log($"Checking out base branch '{baseBranch}' (was on '{state.ReturnTo}').");
            await Git.RunOrThrowAsync(repoPath, ct, "checkout", baseBranch);
        }

        var remoteBase = await Git.RunAsync(repoPath, ct, "rev-parse", "--verify", "-q", $"refs/remotes/origin/{baseBranch}");
        if (remoteBase.Ok)
        {
            var ff = await Git.RunAsync(repoPath, ct, "merge", "--ff-only", $"origin/{baseBranch}");
            if (!ff.Ok)
            {
                throw new WorkspaceException($"Local '{baseBranch}' has diverged from origin/{baseBranch}; fix that first. {ff.Err.Trim()}");
            }
        }

        state.Branch = $"moonwalk/{runId}-{Slug(title)}";
        log($"Creating branch {state.Branch}.");
        await Git.RunOrThrowAsync(repoPath, ct, "checkout", "-b", state.Branch);
        return state;
    }

    /// <summary>Everything changed since the run started, minus files that were already modified and are untouched by the agent.</summary>
    public async Task<AgentChanges> GetChangesAsync(WorkspaceState state, CancellationToken ct)
    {
        var changes = new List<string>();
        var conflicts = new List<string>();
        foreach (var path in await StatusPathsAsync(ct))
        {
            if (!state.Baseline.TryGetValue(path, out var before))
            {
                changes.Add(path);
            }
            else if (before != Signature(path))
            {
                conflicts.Add(path);
            }
        }
        return new AgentChanges(changes, conflicts);
    }

    /// <summary>Stages exactly these paths and returns how many lines they add or remove.</summary>
    public async Task<int> StageAsync(IReadOnlyList<string> paths, string runDir, CancellationToken ct)
    {
        var pathspec = WritePathspec(runDir, paths);
        await Git.RunOrThrowAsync(repoPath, ct, "add", $"--pathspec-from-file={pathspec}", "--pathspec-file-nul");

        var lines = 0;
        foreach (var batch in paths.Chunk(100))
        {
            var args = new List<string> { "diff", "--cached", "--numstat", "--no-renames", "--" };
            args.AddRange(batch);
            var numstat = await Git.RunOrThrowAsync(repoPath, ct, args.ToArray());
            foreach (var row in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = row.Split('\t');
                if (cols.Length >= 2 && int.TryParse(cols[0], out var added) && int.TryParse(cols[1], out var removed))
                {
                    lines += added + removed;
                }
            }
        }
        return lines;
    }

    /// <summary>Commits only the given paths (other staged files are left alone) and returns the short commit id.</summary>
    public async Task<string> CommitAsync(IReadOnlyList<string> paths, string message, string runDir, CancellationToken ct)
    {
        var pathspec = WritePathspec(runDir, paths);
        var messageFile = Path.Combine(runDir, "commit.txt");
        File.WriteAllText(messageFile, message, new UTF8Encoding(false));
        await Git.RunOrThrowAsync(repoPath, ct, "commit", "-F", messageFile, $"--pathspec-from-file={pathspec}", "--pathspec-file-nul");
        return (await Git.RunOrThrowAsync(repoPath, ct, "rev-parse", "--short", "HEAD")).Trim();
    }

    /// <summary>
    /// Keeps whatever the agent produced as a local wip commit on the run branch (never pushed), then switches the
    /// repository back. Returns the warning to show when that was not fully possible, or null.
    /// </summary>
    public async Task<string?> AbortAsync(WorkspaceState state, string runId, string runDir, CancellationToken ct)
    {
        try
        {
            var changes = await GetChangesAsync(state, ct);
            if (changes.Paths.Count > 0)
            {
                var pathspec = WritePathspec(runDir, changes.Paths);
                await Git.RunAsync(repoPath, ct, "add", $"--pathspec-from-file={pathspec}", "--pathspec-file-nul");
                await Git.RunAsync(repoPath, ct, "commit", "-m", $"wip(moonwalk): unfinished run {runId} - not pushed",
                    $"--pathspec-from-file={pathspec}", "--pathspec-file-nul");
            }
            await ReleaseAsync(state, ct);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not restore the repository to '{state.ReturnTo}': {ex.Message}";
        }
    }

    /// <summary>Commit list and per-file diff summary of the run branch against its base, for the handover note.</summary>
    public async Task<(string Log, string DiffStat)> GetHandoverInfoAsync(WorkspaceState state, CancellationToken ct)
    {
        var range = $"{state.BaseBranch}..{state.Branch}";
        var log = await Git.RunAsync(repoPath, ct, "log", "--oneline", range);
        var stat = await Git.RunAsync(repoPath, ct, "diff", "--stat", "--no-renames", range);
        return (log.Ok ? log.Out : "", stat.Ok ? stat.Out : "");
    }

    public async Task ReleaseAsync(WorkspaceState state, CancellationToken ct) =>
        await Git.RunOrThrowAsync(repoPath, ct, "checkout", state.ReturnTo);

    private async Task<List<string>> StatusPathsAsync(CancellationToken ct)
    {
        var output = await Git.RunOrThrowAsync(repoPath, ct, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var paths = new List<string>();
        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 4)
            {
                continue;
            }
            paths.Add(token[3..]);
            if (token[0] is 'R' or 'C' && i + 1 < tokens.Length)
            {
                paths.Add(tokens[++i]);
            }
        }
        return paths.Distinct().ToList();
    }

    private string Signature(string relativePath)
    {
        var full = Path.Combine(repoPath, relativePath);
        if (File.Exists(full))
        {
            var info = new FileInfo(full);
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }
        return Directory.Exists(full) ? "dir" : "missing";
    }

    private static string WritePathspec(string runDir, IReadOnlyList<string> paths)
    {
        var file = Path.Combine(runDir, "pathspec.txt");
        File.WriteAllText(file, string.Join('\0', paths) + '\0', new UTF8Encoding(false));
        return file;
    }

    private static string Slug(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 30)
        {
            slug = slug[..30].Trim('-');
        }
        return slug.Length == 0 ? "run" : slug;
    }
}
