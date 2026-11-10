using System.Text.Json;
using System.Threading.Channels;
using MoonWalk.Core;

namespace MoonWalk.Web;

public sealed record NewRunRequest(string? Brief, string? Project);

public static class ApiEndpoints
{
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
            var run = engine.Start(request.Brief.Trim(), string.IsNullOrWhiteSpace(request.Project) ? "demo" : request.Project);
            return Results.Content(store.GetJson(run.Id)!, "application/json", statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/api/runs/{id}/cancel", (string id, RunEngine engine) =>
            engine.Cancel(id) ? Results.Accepted() : Results.NotFound());

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
