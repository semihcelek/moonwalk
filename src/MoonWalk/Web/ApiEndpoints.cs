using System.Text.Json;
using System.Threading.Channels;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Web;

public sealed record NewRunRequest(string? Brief, string? Project);

public sealed record GuidanceRequest(string? Text);

public static class ApiEndpoints
{
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
                var run = engine.Start(request.Brief.Trim(), string.IsNullOrWhiteSpace(request.Project) ? "demo" : request.Project);
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

        app.MapGet("/api/agents", (AgentRegistry agents) =>
            Results.Json(new { agents = agents.Names, details = agents.List(), errors = agents.Errors }, Json.Options));

        app.MapGet("/api/agents/health", async (AgentRegistry agents, CancellationToken ct) =>
            Results.Json(new { agents = await agents.CheckAllAsync(ct), errors = agents.Errors }, Json.Options));

        app.MapPost("/api/agents/reload", (AgentRegistry agents) =>
        {
            agents.Reload();
            return Results.Ok(new { count = agents.List().Count, errors = agents.Errors });
        });

        app.MapGet("/api/runs/{id}/files/{**name}", (string id, string name, RunStore store, IConfiguration config) =>
        {
            var run = store.Get(id);
            if (run is null || !run.Artifacts.Contains(name))
            {
                return Results.NotFound();
            }
            var root = Path.GetFullPath(Path.Combine(Path.GetFullPath(config["MoonWalk:DataDir"] ?? "runs"), id));
            var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                return Results.NotFound();
            }
            return Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8");
        });

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
