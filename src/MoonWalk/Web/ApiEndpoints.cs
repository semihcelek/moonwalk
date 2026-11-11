using System.Text.Json;
using System.Threading.Channels;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Web;

public sealed record NewRunRequest(string? Brief, string? Project, Dictionary<string, AgentChoice>? Stages = null, AutoMode? Auto = null);

public sealed record GuidanceRequest(string? Text);

public sealed record SaveBriefRequest(string? Name, string? Text);

public sealed record BranchRequest(string? Project, string? Branch, bool Force = false);

public sealed record SavePresetRequest(string? Name, Dictionary<string, AgentChoice>? Stages);

public static class ApiEndpoints
{
    /// <summary>How many files and lines a branch changed against its base, or null when git cannot compare them.</summary>
    private static async Task<object?> DiffStatsAsync(string repo, string range, CancellationToken ct)
    {
        var numstat = await Git.RunAsync(repo, ct, "diff", "--numstat", "--no-renames", range);
        if (!numstat.Ok)
        {
            return null;
        }

        int files = 0, added = 0, removed = 0;
        foreach (var row in numstat.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = row.Split('\t');
            files++;
            added += cols.Length > 0 && int.TryParse(cols[0], out var a) ? a : 0;
            removed += cols.Length > 1 && int.TryParse(cols[1], out var r) ? r : 0;
        }
        return new { files, added, removed };
    }

    private static ProjectEntry? RealProject(ProjectRegistry projects, string? name) =>
        name is not null && projects.Get(name) is { IsValid: true, Profile.IsDemo: false } entry ? entry : null;

    /// <summary>Why a run branch must not be deleted right now, or null. Never the checked-out branch, a worktree's, or an unfinished run's.</summary>
    private static string? ProtectedReason(BranchInfo b, RunStore store)
    {
        if (b.Current)
        {
            return "it is checked out in the repository";
        }
        if (b.Worktree is not null)
        {
            return $"it is checked out in a worktree ({b.Worktree})";
        }
        if (b.RunId is { } id && store.Get(id) is { } run)
        {
            if (Run.IsActive(run.Status))
            {
                return "its run is still active";
            }
            if (run.WorkspaceHeld)
            {
                return "its run still holds the workspace";
            }
        }
        return null;
    }

    private static IResult ReviewReply(ReviewResult result) =>
        result.Ok
            ? Results.Json(new { message = result.Message, commit = result.Commit }, Json.Options)
            : Results.Json(new { error = result.Message, conflicts = result.Conflicts }, Json.Options, statusCode: StatusCodes.Status409Conflict);

    private static IResult Reply((bool Ok, string Message) result) =>
        result.Ok ? Results.Ok(new { message = result.Message }) : Results.Conflict(new { error = result.Message });

    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/runs", (RunStore store) => Results.Content(store.ListJson(), "application/json"));

        app.MapGet("/api/runs/{id}", (string id, RunStore store) =>
            store.GetJson(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());

        app.MapPost("/api/runs", (NewRunRequest request, RunEngine engine, RunStore store) =>
        {
            if (string.IsNullOrWhiteSpace(request.Brief))
            {
                return Results.BadRequest(new { error = "brief is required" });
            }
            try
            {
                var run = engine.Start(request.Brief.Trim(), string.IsNullOrWhiteSpace(request.Project) ? "demo" : request.Project, request.Stages, request.Auto);
                return Results.Content(store.GetJson(run.Id)!, "application/json", statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/runs/{id}/cancel", (string id, RunEngine engine) =>
            engine.Cancel(id) ? Results.Accepted() : Results.NotFound());

        app.MapPost("/api/runs/{id}/pause", (string id, RunEngine engine) => Reply(engine.Pause(id)));

        app.MapPost("/api/runs/{id}/resume", (string id, RunEngine engine) => Reply(engine.Resume(id)));

        app.MapPost("/api/runs/{id}/guidance", (string id, GuidanceRequest request, RunEngine engine) =>
            Reply(engine.AddGuidance(id, request.Text ?? "")));

        app.MapPost("/api/runs/{id}/decision", (string id, DecisionRequest request, RunEngine engine) =>
            Reply(engine.Decide(id, request)));

        app.MapPost("/api/runs/{id}/recover", async (string id, RunEngine engine) => Reply(await engine.RecoverAsync(id)));

        app.MapPost("/api/runs/{id}/reopen", async (string id, ReopenRequest request, RunEngine engine) => Reply(await engine.ReopenAsync(id, request)));

        app.MapGet("/api/agents", (AgentRegistry agents) =>
            Results.Json(new { agents = agents.Names, details = agents.List(), errors = agents.Errors }, Json.Options));

        app.MapGet("/api/models", () => Results.Json(ModelCatalog.All, Json.Options));

        app.MapGet("/api/usage", (RunStore store, int? days) => Results.Json(store.UsageSummary(Math.Clamp(days ?? 7, 1, 90)), Json.Options));

        app.MapGet("/api/presets", (PresetStore presets) =>
            Results.Json(new { builtin = PresetStore.Builtin, custom = presets.Custom() }, Json.Options));

        app.MapPost("/api/presets", (SavePresetRequest request, PresetStore presets) =>
            presets.Save(request.Name ?? "", request.Stages ?? new()) is { } problem
                ? Results.BadRequest(new { error = problem })
                : Results.Ok(new { saved = request.Name }));

        app.MapDelete("/api/presets/{name}", (string name, PresetStore presets) =>
            presets.Delete(name) ? Results.Ok(new { deleted = name }) : Results.NotFound());

        app.MapGet("/api/agents/health", async (AgentRegistry agents, CancellationToken ct) =>
            Results.Json(new { agents = await agents.CheckAllAsync(ct), errors = agents.Errors }, Json.Options));

        app.MapPost("/api/agents/reload", (AgentRegistry agents) =>
        {
            agents.Reload();
            return Results.Ok(new { count = agents.List().Count, errors = agents.Errors });
        });

        app.MapGet("/api/runs/{id}/files/{**name}", (string id, string name, RunStore store, AppPaths paths) =>
        {
            var run = store.Get(id);
            if (run is null || !run.Artifacts.Contains(name))
            {
                return Results.NotFound();
            }
            var root = Path.GetFullPath(Path.Combine(paths.RunsRoot, id));
            var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                return Results.NotFound();
            }
            var image = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => null,
            };
            return image is not null
                ? Results.File(File.ReadAllBytes(path), image)
                : Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8");
        });

        app.MapGet("/api/runs/{id}/diff", async (string id, RunStore store, ProjectRegistry projects, AppPaths paths, CancellationToken ct) =>
        {
            var run = store.Get(id);
            if (run is null)
            {
                return Results.NotFound();
            }
            var entry = projects.Get(run.Project);
            var state = WorkspaceState.Load(Path.Combine(paths.RunsRoot, id));
            if (entry is null || entry.Profile.IsDemo || state is null || string.IsNullOrEmpty(state.Branch))
            {
                return Results.Json(new { available = false, reason = "This run has no git branch." }, Json.Options);
            }

            var repo = entry.Profile.RepoPath;
            var range = $"{state.BaseBranch}..{state.Branch}";
            var numstat = await Git.RunAsync(repo, ct, "diff", "--numstat", "--no-renames", range);
            if (!numstat.Ok)
            {
                return Results.Json(new { available = false, reason = $"git could not compare {range}: {numstat.Err.Trim()}" }, Json.Options);
            }

            var files = numstat.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row =>
            {
                var cols = row.Split('\t');
                return new
                {
                    path = cols.Length > 2 ? cols[2] : row,
                    added = cols.Length > 0 && int.TryParse(cols[0], out var a) ? a : 0,
                    removed = cols.Length > 1 && int.TryParse(cols[1], out var r) ? r : 0,
                    binary = cols.Length > 0 && cols[0] == "-",
                };
            }).ToList();

            const int maxPatch = 300_000;
            var patch = (await Git.RunAsync(repo, ct, "diff", "--no-renames", "-U3", range)).Out;
            var truncated = patch.Length > maxPatch;
            return Results.Json(new
            {
                available = true,
                @base = state.BaseBranch,
                branch = state.Branch,
                files,
                patch = truncated ? patch[..maxPatch] : patch,
                truncated,
            }, Json.Options);
        });

        // The review branch: finished runs merged into one branch, tested with a single checkout.
        app.MapGet("/api/review", async (string? project, ReviewService review, CancellationToken ct) =>
            await review.StatusAsync(project ?? "", ct) is { } status
                ? Results.Json(status, Json.Options)
                : Results.BadRequest(new { error = "Choose a project that has a repository." }));

        app.MapPost("/api/runs/{id}/merge-review", async (string id, ReviewService review, CancellationToken ct) =>
            ReviewReply(await review.MergeRunAsync(id, ct)));

        app.MapPost("/api/review/checkout", async (BranchRequest request, ReviewService review, CancellationToken ct) =>
            ReviewReply(await review.CheckoutAsync(request.Project ?? "", ct)));

        app.MapPost("/api/review/reset", async (BranchRequest request, ReviewService review, CancellationToken ct) =>
            ReviewReply(await review.ResetAsync(request.Project ?? "", ct)));

        // The guide a main agent reads before writing briefs, with this installation's real project, agent and model names filled in.
        app.MapGet("/api/guide", (HttpContext context, ProjectRegistry projects, PresetStore presets, string? project) =>
            Results.Text(
                GuideBuilder.Build($"{context.Request.Scheme}://{context.Request.Host}", projects, presets, project),
                "text/markdown; charset=utf-8"));

        app.MapGet("/api/briefs", (BriefStore briefs) => Results.Json(briefs.List(), Json.Options));

        app.MapPost("/api/briefs", (SaveBriefRequest request, BriefStore briefs) =>
            briefs.Save(request.Name ?? "", request.Text ?? "") is { } problem
                ? Results.BadRequest(new { error = problem })
                : Results.Ok(new { saved = request.Name }));

        app.MapDelete("/api/briefs/{name}", (string name, BriefStore briefs) =>
            briefs.Delete(name) ? Results.Ok(new { deleted = name }) : Results.NotFound());

        // Branch hygiene: the moonwalk/* branches runs leave behind.
        app.MapGet("/api/branches", async (string? project, ProjectRegistry projects, RunStore store, CancellationToken ct) =>
        {
            if (RealProject(projects, project) is not { } entry)
            {
                return Results.BadRequest(new { error = "Choose a project that has a repository." });
            }
            var branches = await BranchHygiene.ListAsync(entry.Profile.RepoPath, entry.Profile.BaseBranch, ct);
            return Results.Json(new
            {
                project = entry.Profile.Name,
                baseBranch = entry.Profile.BaseBranch,
                branches = branches.Select(b => new { b.Name, b.Merged, b.Ahead, b.LastCommit, b.Subject, b.RunId, b.Current, b.Worktree,
                    protectedReason = ProtectedReason(b, store) }),
            }, Json.Options);
        });

        app.MapPost("/api/branches/delete", async (BranchRequest request, ProjectRegistry projects, RunStore store, CancellationToken ct) =>
        {
            if (RealProject(projects, request.Project) is not { } entry)
            {
                return Results.BadRequest(new { error = "Choose a project that has a repository." });
            }
            var all = await BranchHygiene.ListAsync(entry.Profile.RepoPath, entry.Profile.BaseBranch, ct);
            if (all.FirstOrDefault(b => b.Name == request.Branch) is not { } info)
            {
                return Results.NotFound(new { error = "No such moonwalk branch." });
            }
            if (ProtectedReason(info, store) is { } why)
            {
                return Results.Conflict(new { error = $"Not deleted: {why}." });
            }
            var (ok, message) = await BranchHygiene.DeleteAsync(entry.Profile.RepoPath, info.Name, info.Merged, request.Force, ct);
            return ok ? Results.Ok(new { message }) : Results.Conflict(new { error = message });
        });

        app.MapPost("/api/branches/delete-merged", async (BranchRequest request, ProjectRegistry projects, RunStore store, CancellationToken ct) =>
        {
            if (RealProject(projects, request.Project) is not { } entry)
            {
                return Results.BadRequest(new { error = "Choose a project that has a repository." });
            }
            var deleted = new List<string>();
            foreach (var b in (await BranchHygiene.ListAsync(entry.Profile.RepoPath, entry.Profile.BaseBranch, ct)).Where(b => b.Merged && ProtectedReason(b, store) is null))
            {
                if ((await BranchHygiene.DeleteAsync(entry.Profile.RepoPath, b.Name, merged: true, force: false, ct)).Ok)
                {
                    deleted.Add(b.Name);
                }
            }
            return Results.Ok(new { deleted });
        });

        app.MapPost("/api/branches/prune", async (BranchRequest request, ProjectRegistry projects, CancellationToken ct) =>
            RealProject(projects, request.Project) is { } entry
                ? Results.Ok(new { message = await BranchHygiene.PruneWorktreesAsync(entry.Profile.RepoPath, ct) })
                : Results.BadRequest(new { error = "Choose a project that has a repository." }));

        app.MapGet("/api/batches", (BatchService batches) => Results.Json(batches.List(), Json.Options));

        app.MapPost("/api/batches", (BatchRequest request, BatchService batches) =>
        {
            try
            {
                return Results.Json(batches.Create(request), Json.Options, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/batches/{id}/cancel", (string id, BatchService batches) =>
            batches.Cancel(id) ? Results.Ok(new { message = "Cancelling: the run in progress is cancelled and no more are started." })
                : Results.Conflict(new { error = "That batch is not running." }));

        // One batch with its runs and, for a comparison, how much each variant changed in git.
        app.MapGet("/api/batches/{id}", async (string id, BatchService batches, RunStore store, ProjectRegistry projects, AppPaths paths, CancellationToken ct) =>
        {
            var batch = batches.Get(id);
            if (batch is null)
            {
                return Results.NotFound();
            }

            var entry = projects.Get(batch.Project);
            var rows = new List<object>();
            foreach (var item in batch.Items)
            {
                var run = item.RunId is null ? null : store.Get(item.RunId);
                object? stats = null;
                if (run is not null && entry is { Profile.IsDemo: false }
                    && WorkspaceState.Load(Path.Combine(paths.RunsRoot, run.Id)) is { Branch.Length: > 0 } state)
                {
                    stats = await DiffStatsAsync(entry.Profile.RepoPath, $"{state.BaseBranch}..{state.Branch}", ct);
                }
                rows.Add(new { item.Index, item.Label, item.Status, item.Error, item.RunId, run, stats });
            }
            return Results.Json(new { batch, rows }, Json.Options);
        });

        app.MapPost("/api/runs/{id}/archive", (string id, RunEngine engine) => Reply(engine.Archive(id, true)));

        app.MapPost("/api/runs/{id}/unarchive", (string id, RunEngine engine) => Reply(engine.Archive(id, false)));

        app.MapDelete("/api/runs/{id}", (string id, RunEngine engine) => Reply(engine.Delete(id)));

        app.MapPost("/api/runs/{id}/release", async (string id, RunEngine engine) =>
        {
            var (ok, message) = await engine.ReleaseAsync(id);
            return ok ? Results.Ok(new { message }) : Results.Conflict(new { error = message });
        });

        app.MapGet("/api/projects", (ProjectRegistry projects) => Results.Json(
            projects.List().Select(e => new
            {
                name = e.Profile.Name,
                repoPath = e.Profile.IsDemo ? null : e.Profile.RepoPath,
                baseBranch = e.Profile.IsDemo ? null : e.Profile.BaseBranch,
                workspace = e.Profile.IsDemo ? "none" : e.Profile.Workspace.ToString().ToLowerInvariant(),
                dirtyPolicy = e.Profile.DirtyPolicy,
                agent = e.Profile.IsDemo ? null : e.Profile.Agent,
                auto = e.Profile.Auto,
                stages = e.Profile.Stages,
                valid = e.IsValid,
                errors = e.Errors,
                file = e.File,
            }),
            Json.Options));

        app.MapPost("/api/projects/reload", (ProjectRegistry projects) =>
        {
            projects.Reload();
            return Results.Ok(new { count = projects.List().Count });
        });

        app.MapGet("/api/stream", async (HttpContext context, RunStore store) =>
        {
            using var subscription = store.Changed.Subscribe();
            await Sse.StreamAsync(context, subscription.Reader, json => Sse.Frame("run", null, json));
        });

        app.MapGet("/api/runs/{id}/stream", async (string id, HttpContext context, RunStore store, EventHub hub) =>
        {
            if (!store.Exists(id))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var after = 0L;
            if (long.TryParse(context.Request.Headers["Last-Event-ID"], out var fromHeader))
            {
                after = fromHeader;
            }
            else if (long.TryParse(context.Request.Query["after"], out var fromQuery))
            {
                after = fromQuery;
            }

            var (backlog, subscription) = hub.OpenStream(id, after);
            using (subscription)
            {
                static string Format(RunEvent ev) => Sse.Frame("ev", ev.Seq, JsonSerializer.Serialize(ev, Json.Options));
                await Sse.StreamAsync(context, subscription.Reader, Format, backlog.Select(Format));
            }
        });
    }
}

internal static class Sse
{
    public static string Frame(string name, long? id, string data) =>
        (id is { } i ? $"id: {i}\n" : "") + $"event: {name}\ndata: {data}\n\n";

    public static async Task StreamAsync<T>(
        HttpContext context,
        ChannelReader<T> reader,
        Func<T, string> format,
        IEnumerable<string>? backlog = null)
    {
        var ct = context.RequestAborted;
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        try
        {
            await context.Response.WriteAsync(": connected\n\n", ct);
            foreach (var frame in backlog ?? [])
            {
                await context.Response.WriteAsync(frame, ct);
            }
            await context.Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await reader.WaitToReadAsync(wait.Token))
                    {
                        break;
                    }
                    while (reader.TryRead(out var item))
                    {
                        await context.Response.WriteAsync(format(item), ct);
                    }
                    await context.Response.Body.FlushAsync(ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
