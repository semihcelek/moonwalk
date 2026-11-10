using System.Text.Json;
using MoonWalk.Core;

namespace MoonWalk.Agents;

public sealed class MuseResultInfo
{
    public bool Seen { get; set; }

    public string? Text { get; set; }

    public bool IsError { get; set; }

    public string? SessionId { get; set; }

    public string? Usage { get; set; }
}

/// <summary>
/// Turns one line of <c>muse exec --json</c> into UI events. Each line is an envelope with a <c>payload_type</c> and a <c>payload</c>.
/// Used here: the model being configured, a task proposed with a <c>tool.*</c> kind (a tool call about to run), <c>tool.result</c>
/// (what it did, with edit facts for file writes), <c>run.output.delta</c> (the answer), <c>run.usage</c> and <c>run.terminal.*</c>.
/// Everything else (task lifecycle bookkeeping, reminders) is ignored.
/// </summary>
public sealed class MuseStreamParser
{
    public MuseResultInfo Result { get; } = new();

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
        var type = Str(root, "payload_type");
        if (type is null || !root.TryGetProperty("payload", out var payload))
        {
            yield break;
        }

        switch (type)
        {
            case "run.model.configured":
                var model = Str(payload, "model_id");
                yield return new ParsedEvent(EventTypes.System, model is null ? "Agent session started." : $"Agent session started ({model}).",
                    new() { ["model"] = model, ["runId"] = Str(payload, "command_id") });
                Result.SessionId = Str(payload, "command_id");
                break;

            case "task.lifecycle.proposed" when payload.TryGetProperty("event", out var evt) && Str(evt, "task_kind") is { } kind && kind.StartsWith("tool."):
                var tool = kind["tool.".Length..];
                yield return new ParsedEvent(EventTypes.AgentToolUse, tool, new() { ["tool"] = tool });
                break;

            case "tool.result":
                yield return ToolResult(payload);
                break;

            case "run.output.delta":
                if (Str(payload, "text") is { Length: > 0 } text)
                {
                    yield return new ParsedEvent(EventTypes.AgentTextDelta, text);
                }
                break;

            case "run.usage":
                Result.Usage = Usage(payload);
                break;

            case var t when t.StartsWith("run.terminal."):
                Result.Seen = true;
                var outcome = Str(payload, "terminal") ?? t["run.terminal.".Length..];
                Result.IsError = !string.Equals(outcome, "completed", StringComparison.OrdinalIgnoreCase);
                Result.Text = Str(payload, "text") ?? Result.Text;
                yield return new ParsedEvent(EventTypes.AgentText, "");
                if (Result.IsError)
                {
                    yield return new ParsedEvent(EventTypes.Warning, $"Agent finished as '{outcome}'" + (Str(payload, "reason") is { } reason ? $": {reason}" : "."));
                }
                break;
        }
    }

    private static ParsedEvent ToolResult(JsonElement payload)
    {
        var facts = payload.TryGetProperty("correlation_facts", out var f) ? f : default;
        var tool = Str(facts, "tool_name");
        var success = !string.Equals(Str(facts, "outcome"), "failure", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(Str(facts, "outcome"), "error", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(Str(facts, "outcome"), "denied", StringComparison.OrdinalIgnoreCase);
        var body = Str(payload, "text") ?? "";

        string summary;
        if (payload.TryGetProperty("edit_facts", out var edit) && Str(edit, "path") is { } path)
        {
            var added = edit.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
            var removed = edit.TryGetProperty("removed", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
            summary = $"{path} (+{added}" + (removed > 0 ? $" -{removed}" : "") + ")";
        }
        else
        {
            var lines = body.Split('\n');
            var first = lines[0].Trim();
            if (first.Length > 140)
            {
                first = first[..140] + "...";
            }
            summary = lines.Length > 1 ? $"{first} (+{lines.Length - 1} lines)" : first;
        }

        if (!success)
        {
            summary = "error: " + summary;
        }
        return new ParsedEvent(EventTypes.AgentToolResult, string.IsNullOrEmpty(summary) ? "(empty)" : summary,
            new() { ["tool"] = tool, ["isError"] = !success, ["preview"] = body.Length > 600 ? body[..600] + "..." : body });
    }

    private static string? Usage(JsonElement payload)
    {
        if (!payload.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        long total = 0;
        foreach (var m in models.EnumerateArray())
        {
            foreach (var key in new[] { "input_tokens", "output_tokens" })
            {
                if (m.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.Number)
                {
                    total += n.GetInt64();
                }
            }
        }
        return total > 0 ? $"{total:N0} tokens" : null;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
