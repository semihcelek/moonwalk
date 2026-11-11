using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoonWalk.Pipeline;

/// <summary>The Unity Editor (its MCP server) could not be reached.</summary>
public sealed class UnityUnavailableException(string message) : Exception(message);

/// <summary>
/// Asks the open Unity Editor, through its unity-mcp HTTP server, to compile the scripts and reports the compile errors (<c>error CS...</c>)
/// left in the Console. It costs no agent quota. It needs the Editor open on the project's working folder; in branch mode that is the
/// folder the run works in, so it compiles exactly what the run changed. Read-only apart from the refresh the Editor does anyway.
/// </summary>
public static partial class UnityCompileCheck
{
    public sealed record Result(bool Passed, IReadOnlyList<string> Errors, string Summary);

    public static async Task<Result> RunAsync(string url, TimeSpan timeout, Action<string> log, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var client = new McpClient(http, url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;

        try
        {
            await client.InitializeAsync(token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new UnityUnavailableException($"The Unity MCP server at {url} is not reachable ({ex.Message}). Is the Editor open on the project?");
        }

        var started = await CompileStartedAsync(client, token);
        log("Asking the Editor to refresh and compile scripts.");
        try
        {
            await client.CallToolAsync("refresh_unity", new { mode = "force", scope = "scripts", compile = "request", wait_for_ready = true }, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException or IOException)
        {
            // A domain reload can drop the bridge in the middle of this call; the wait below decides whether the Editor comes back.
            log($"refresh_unity ended early ({ex.Message}); waiting for the Editor.");
        }

        await WaitUntilIdleAsync(client, started, log, token);

        var errors = await ReadCompileErrorsAsync(client, token);
        return errors.Count == 0
            ? new Result(true, errors, "Unity compiled the scripts without errors.")
            : new Result(false, errors, $"Unity reports {errors.Count} compile error(s):\n" + string.Join('\n', errors.Take(15)) + (errors.Count > 15 ? "\n..." : ""));
    }

    private static async Task<long?> CompileStartedAsync(McpClient client, CancellationToken ct)
    {
        try
        {
            var state = await client.ReadResourceAsync("mcpforunity://editor/state", ct);
            return state.TryGetProperty("data", out var d) && d.TryGetProperty("compilation", out var c)
                && c.TryGetProperty("last_compile_started_unix_ms", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or McpException)
        {
            return null;
        }
    }

    /// <summary>Waits until the Editor is no longer compiling or reloading. Calls that fail meanwhile mean it is reloading; only the deadline ends the wait.</summary>
    private static async Task WaitUntilIdleAsync(McpClient client, long? startedBefore, Action<string> log, CancellationToken ct)
    {
        var grace = DateTimeOffset.UtcNow.AddSeconds(6); // a compile may not start at all when no script changed
        var sawBusy = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var state = await client.ReadResourceAsync("mcpforunity://editor/state", ct);
                var data = state.GetProperty("data");
                var compiling = data.GetProperty("compilation").GetProperty("is_compiling").GetBoolean()
                    || data.GetProperty("compilation").GetProperty("is_domain_reload_pending").GetBoolean();
                var ready = data.GetProperty("advice").GetProperty("ready_for_tools").GetBoolean();
                var startedNow = data.GetProperty("compilation").TryGetProperty("last_compile_started_unix_ms", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt64() : (long?)null;
                if (compiling || !ready || startedNow != startedBefore)
                {
                    sawBusy = true;
                }
                if (!compiling && ready && (sawBusy || DateTimeOffset.UtcNow >= grace))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or McpException or KeyNotFoundException or InvalidOperationException or JsonException)
            {
                sawBusy = true; // the bridge is down while Unity reloads the domain
            }
            await Task.Delay(1000, ct);
        }
    }

    private static async Task<List<string>> ReadCompileErrorsAsync(McpClient client, CancellationToken ct)
    {
        // The bridge can still be coming back just after a domain reload: retry a few times before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await client.CallToolAsync("read_console", new { action = "get", types = new[] { "error" }, count = "200" }, ct);
                var errors = new List<string>();
                if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in data.EnumerateArray())
                    {
                        var text = entry.ValueKind == JsonValueKind.String ? entry.GetString()
                            : entry.TryGetProperty("message", out var m) ? m.GetString() : null;
                        if (text is not null && CompileError().IsMatch(text))
                        {
                            var line = text.Trim().Replace('\\', '/');
                            if (!errors.Contains(line))
                            {
                                errors.Add(line);
                            }
                        }
                    }
                }
                return errors;
            }
            catch (Exception ex) when (attempt < 5 && ex is HttpRequestException or McpException)
            {
                await Task.Delay(2000, ct);
            }
        }
    }

    [GeneratedRegex(@"error CS\d+")]
    private static partial Regex CompileError();

    public sealed class McpException(string message) : Exception(message);

    /// <summary>Just enough of the MCP streamable-HTTP protocol: initialize, tools/call, resources/read.</summary>
    private sealed class McpClient(HttpClient http, string url)
    {
        private string? _session;
        private int _id;

        public async Task InitializeAsync(CancellationToken ct)
        {
            await SendAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "moonwalk", version = "1" } }, ct);
            await SendAsync("notifications/initialized", new { }, ct, notify: true);
        }

        public async Task<JsonElement> CallToolAsync(string name, object arguments, CancellationToken ct)
        {
            var result = await SendAsync("tools/call", new { name, arguments }, ct);
            if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            {
                throw new McpException($"{name} failed: {Text(result)}");
            }
            if (result.TryGetProperty("structuredContent", out var structured))
            {
                return structured;
            }
            return JsonDocument.Parse(Text(result)).RootElement.Clone();
        }

        public async Task<JsonElement> ReadResourceAsync(string uri, CancellationToken ct)
        {
            var result = await SendAsync("resources/read", new { uri }, ct);
            return JsonDocument.Parse(result.GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement.Clone();
        }

        private static string Text(JsonElement result) =>
            result.TryGetProperty("content", out var content) && content.GetArrayLength() > 0 && content[0].TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

        private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken ct, bool notify = false)
        {
            var id = notify ? 0 : Interlocked.Increment(ref _id);
            var body = notify
                ? (object)new { jsonrpc = "2.0", method, @params = parameters }
                : new { jsonrpc = "2.0", id, method, @params = parameters };
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            if (_session is not null)
            {
                request.Headers.Add("mcp-session-id", _session);
            }
            using var response = await http.SendAsync(request, ct);
            if (response.Headers.TryGetValues("mcp-session-id", out var sid))
            {
                _session = sid.First();
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new McpException($"{method}: HTTP {(int)response.StatusCode}");
            }
            if (notify)
            {
                return default;
            }

            // The reply is either plain JSON or an event stream whose "data:" lines may start with notifications before the result.
            var candidates = text.TrimStart().StartsWith('{')
                ? [text]
                : text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()).ToArray();
            foreach (var candidate in candidates)
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
                {
                    if (root.TryGetProperty("error", out var error))
                    {
                        throw new McpException($"{method}: {error.GetProperty("message").GetString()}");
                    }
                    return root.GetProperty("result").Clone();
                }
            }
            throw new McpException($"{method}: no reply with id {id}.");
        }
    }
}
