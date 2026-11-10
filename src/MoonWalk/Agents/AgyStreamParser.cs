using System.Text.Json;
using MoonWalk.Core;

namespace MoonWalk.Agents;

public sealed class AgyResultInfo
{
    public bool Seen { get; set; }

    public string? Text { get; set; }

    public bool IsError { get; set; }

    public int? NumTurns { get; set; }

    public string? SessionId { get; set; }

    public string? Usage { get; set; }
}

/// <summary>
/// Turns one line of <c>agy -p ... --output-format stream-json</c> (Gemini via the Antigravity CLI) into UI events.
/// Each line is an <c>init</c>, a <c>step_update</c> or the final <c>result</c>. Agent text arrives as <c>text_delta</c> on
/// <c>agent_response</c> steps; a tool step goes ACTIVE (with its parameters) and then DONE (with a short output summary).
/// </summary>
public sealed class AgyStreamParser(string workDir)
{
    private static readonly string[] HintKeys =
        ["Path", "AbsolutePath", "TargetFile", "File", "FilePath", "CommandLine", "Command", "Query", "Pattern", "Url", "Description"];

    private readonly HashSet<int> _toolsStarted = new();
    private readonly Dictionary<int, string> _toolNames = new();

    public AgyResultInfo Result { get; } = new();

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
        switch (Str(root, "event"))
        {
            case "init" when root.TryGetProperty("init", out var init):
                Result.SessionId = Str(root, "conversation_id");
                yield return new ParsedEvent(EventTypes.System, "Agent session started.",
                    new() { ["conversationId"] = Result.SessionId, ["cwd"] = Str(init, "cwd") });
                break;

            case "step_update" when root.TryGetProperty("step_update", out var step):
                foreach (var parsed in HandleStep(step))
                {
                    yield return parsed;
                }
                break;

            case "result" when root.TryGetProperty("result", out var result):
                Result.Seen = true;
                Result.Text = Str(result, "response");
                Result.IsError = !string.Equals(Str(result, "status"), "SUCCESS", StringComparison.OrdinalIgnoreCase);
                Result.SessionId = Str(result, "conversation_id") ?? Result.SessionId;
                if (result.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number)
                {
                    Result.NumTurns = turns.GetInt32();
                }
                if (result.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var total) && total.ValueKind == JsonValueKind.Number)
                {
                    Result.Usage = $"{total.GetInt64():N0} tokens";
                }
                if (Result.IsError)
                {
                    yield return new ParsedEvent(EventTypes.Warning, $"Agent finished with status {Str(result, "status")}.");
                }
                break;

            case "error":
                yield return new ParsedEvent(EventTypes.Warning, Str(root, "message") ?? Str(root, "error") ?? root.GetRawText());
                break;
        }
    }

    private IEnumerable<ParsedEvent> HandleStep(JsonElement step)
    {
        var index = step.TryGetProperty("step_index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : -1;
        var state = Str(step, "state");

        switch (Str(step, "step_type"))
        {
            case "agent_response":
                var delta = Str(step, "text_delta");
                var done = string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(delta) && !(done && delta == "\n"))
                {
                    yield return new ParsedEvent(EventTypes.AgentTextDelta, delta);
                }
                if (done)
                {
                    yield return new ParsedEvent(EventTypes.AgentText, "");
                }
                break;

            case "tool":
                var info = step.TryGetProperty("tool_info", out var t) ? t : default;
                var name = Str(step, "tool_name") ?? Str(info, "name") ?? "tool";
                _toolNames[index] = name;

                if (_toolsStarted.Add(index))
                {
                    var parameters = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("parameters", out var p) ? p : default;
                    var hint = Hint(parameters);
                    yield return new ParsedEvent(EventTypes.AgentToolUse, hint is null ? name : $"{name} {hint}",
                        new() { ["tool"] = name, ["input"] = Shorten(parameters) });
                }

                var failed = string.Equals(state, "ERROR", StringComparison.OrdinalIgnoreCase);
                if (failed || string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase))
                {
                    var output = Str(info, "output");
                    var error = Str(info, "error") ?? (failed ? output ?? "the tool failed or was denied" : null);
                    var text = error is not null ? "error: " + error : output;
                    yield return new ParsedEvent(EventTypes.AgentToolResult, string.IsNullOrWhiteSpace(text) ? "done" : text.ReplaceLineEndings(" "),
                        new() { ["tool"] = name, ["isError"] = error is not null, ["preview"] = text });
                }
                break;
        }
    }

    private string? Hint(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in HintKeys)
        {
            foreach (var property in parameters.EnumerateObject())
            {
                if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var value = property.Value.GetString()?.ReplaceLineEndings(" ");
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                if (value.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                {
                    value = value[workDir.Length..].TrimStart('\\', '/');
                }
                return value.Length > 110 ? value[..110] + "..." : value;
            }
        }
        return null;
    }

    private static Dictionary<string, object?>? Shorten(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, object?>();
        foreach (var property in parameters.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 300 } s
                ? s[..300] + $"... ({s.Length} chars)"
                : property.Value.Clone();
        }
        return result;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
