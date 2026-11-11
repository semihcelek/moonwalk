using System.Text;
using System.Text.Json;
using MoonWalk.Core;

namespace MoonWalk.Agents;

public sealed record ParsedEvent(string Type, string? Text, Dictionary<string, object?>? Data = null);

public sealed class ClaudeResultInfo
{
    /// <summary>Input (including cache reads and writes) plus output tokens, as the CLI reports them in the final result.</summary>
    public long? Tokens { get; set; }

    public string? Model { get; set; }

    /// <summary>The reasons the CLI gives for an error result (for example "Reached maximum budget ($0.003)").</summary>
    public string? Errors { get; set; }

    public bool Seen { get; set; }

    public string? Text { get; set; }

    public double? CostUsd { get; set; }

    public bool IsError { get; set; }

    public int? NumTurns { get; set; }

    public string? SessionId { get; set; }
}

/// <summary>
/// Turns one line of <c>claude -p --output-format stream-json --include-partial-messages</c> into zero or more UI events.
/// Text arrives as stream deltas, tool calls complete in "assistant" messages, tool results in "user" messages, and a
/// final "result" line carries the summary and cost.
/// </summary>
public sealed class ClaudeStreamParser(string workDir)
{
    private const int MaxString = 300;

    private readonly Dictionary<int, string> _blockTypes = new();
    private readonly Dictionary<string, string> _toolNames = new();
    private bool _sawDelta;

    public ClaudeResultInfo Result { get; } = new();

    public IEnumerable<ParsedEvent> Feed(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return [new ParsedEvent(EventTypes.System, line)];
        }

        using (doc)
        {
            return Handle(doc.RootElement).ToList();
        }
    }

    private IEnumerable<ParsedEvent> Handle(JsonElement root)
    {
        switch (Str(root, "type"))
        {
            case "system" when Str(root, "subtype") == "init":
                Result.SessionId = Str(root, "session_id");
                var model = Str(root, "model");
                Result.Model = model;
                yield return new ParsedEvent(EventTypes.System, model is null ? "Agent session started." : $"Agent session started ({model}).",
                    new() { ["sessionId"] = Result.SessionId, ["model"] = model });
                break;

            case "stream_event" when root.TryGetProperty("event", out var ev):
                foreach (var parsed in HandleStreamEvent(ev))
                {
                    yield return parsed;
                }
                break;

            case "assistant" when root.TryGetProperty("message", out var message):
                foreach (var parsed in HandleAssistant(message))
                {
                    yield return parsed;
                }
                break;

            case "user" when root.TryGetProperty("message", out var userMessage):
                foreach (var parsed in HandleToolResults(userMessage))
                {
                    yield return parsed;
                }
                break;

            case "result":
                Result.Seen = true;
                Result.Text = Str(root, "result");
                Result.IsError = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                Result.SessionId = Str(root, "session_id") ?? Result.SessionId;
                if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                {
                    Result.CostUsd = cost.GetDouble();
                }
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                {
                    var reasons = errors.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToList();
                    Result.Errors = reasons.Count > 0 ? string.Join("; ", reasons) : null;
                }
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    long sum = 0;
                    foreach (var key in new[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" })
                    {
                        if (usage.TryGetProperty(key, out var count) && count.ValueKind == JsonValueKind.Number)
                        {
                            sum += count.GetInt64();
                        }
                    }
                    Result.Tokens = sum > 0 ? sum : null;
                }
                if (root.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number)
                {
                    Result.NumTurns = turns.GetInt32();
                }
                break;

            case "rate_limit_event" when root.TryGetProperty("rate_limit_info", out var info) && Str(info, "status") is { } status && status != "allowed":
                yield return new ParsedEvent(EventTypes.Warning, $"Rate limit status: {status}.");
                break;
        }
    }

    private IEnumerable<ParsedEvent> HandleStreamEvent(JsonElement ev)
    {
        var index = ev.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
        switch (Str(ev, "type"))
        {
            case "content_block_start" when ev.TryGetProperty("content_block", out var block):
                _blockTypes[index] = Str(block, "type") ?? "";
                break;

            case "content_block_delta" when ev.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta":
                var text = Str(delta, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    _sawDelta = true;
                    yield return new ParsedEvent(EventTypes.AgentTextDelta, text);
                }
                break;

            case "content_block_stop" when _blockTypes.TryGetValue(index, out var type) && type == "text":
                yield return new ParsedEvent(EventTypes.AgentText, "");
                break;
        }
    }

    private IEnumerable<ParsedEvent> HandleAssistant(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var streamed = _sawDelta;
        _sawDelta = false;

        foreach (var item in content.EnumerateArray())
        {
            switch (Str(item, "type"))
            {
                case "text" when !streamed && Str(item, "text") is { Length: > 0 } text:
                    yield return new ParsedEvent(EventTypes.AgentText, text);
                    break;

                case "tool_use":
                    var name = Str(item, "name") ?? "tool";
                    var id = Str(item, "id");
                    if (id is not null)
                    {
                        _toolNames[id] = name;
                    }
                    var input = item.TryGetProperty("input", out var inputElement) ? inputElement : default;
                    var hint = Hint(input);
                    yield return new ParsedEvent(
                        EventTypes.AgentToolUse,
                        hint is null ? name : $"{name} {hint}",
                        new() { ["tool"] = name, ["id"] = id, ["input"] = Shorten(input) });
                    break;
            }
        }
    }

    private IEnumerable<ParsedEvent> HandleToolResults(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (Str(item, "type") != "tool_result")
            {
                continue;
            }

            var id = Str(item, "tool_use_id");
            var tool = id is not null && _toolNames.TryGetValue(id, out var name) ? name : null;
            var isError = item.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
            var body = item.TryGetProperty("content", out var c) ? FlattenContent(c) : "";
            var lines = body.Split('\n');
            var first = lines[0].Trim();
            if (first.Length > 140)
            {
                first = first[..140] + "...";
            }
            var summary = lines.Length > 1 ? $"{first} (+{lines.Length - 1} lines)" : first;
            if (isError)
            {
                summary = "error: " + summary;
            }

            yield return new ParsedEvent(
                EventTypes.AgentToolResult,
                string.IsNullOrEmpty(summary) ? "(empty)" : summary,
                new() { ["tool"] = tool, ["isError"] = isError, ["preview"] = body.Length > 600 ? body[..600] + "..." : body });
        }
    }

    private static string FlattenContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (Str(part, "text") is { } text)
            {
                sb.AppendLine(text);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private string? Hint(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "file_path", "path", "command", "pattern", "query", "url", "description", "prompt" })
        {
            if (Str(input, key) is { Length: > 0 } value)
            {
                value = value.ReplaceLineEndings(" ");
                if (key is "file_path" or "path" && value.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[workDir.Length..].TrimStart('\\', '/');
                }
                return value.Length > 110 ? value[..110] + "..." : value;
            }
        }
        return null;
    }

    /// <summary>Top-level copy of a tool input with long strings cut, so large Write payloads do not flood the log.</summary>
    private static Dictionary<string, object?>? Shorten(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object?>();
        foreach (var property in input.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } s && s.Length > MaxString
                ? s[..MaxString] + $"... ({s.Length} chars)"
                : property.Value.Clone();
        }
        return result;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
