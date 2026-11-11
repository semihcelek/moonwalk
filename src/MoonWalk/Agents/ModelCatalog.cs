namespace MoonWalk.Agents;

/// <summary>What a person picked for one stage. Every field is optional: empty means "the project profile's default".</summary>
public sealed record AgentChoice(string? Agent = null, string? Model = null, string? Effort = null);

public sealed record ModelInfo(string Id, string Label, string[] Efforts);

public sealed record AgentModels(string Agent, ModelInfo[] Models, string[] Efforts, string? EffortNote);

/// <summary>
/// The models and effort levels the UI offers per built-in agent. Efforts are what the agent's CLI flag accepts; a model can narrow
/// them. agy bakes the effort into the model id (Gemini 4 Argon Medium is <c>gemini-4-argon-medium</c>), so it has no separate effort.
/// Custom agents from agents.json have no catalog: they take no model or effort.
/// </summary>
public static class ModelCatalog
{
    private static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] MuseEfforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public static readonly IReadOnlyList<AgentModels> All =
    [
        new("claude",
            [
                new("opus", "Claude Opus", ClaudeEfforts),
                new("sonnet", "Claude Sonnet", ClaudeEfforts),
                new("haiku", "Claude Haiku", []),
            ],
            ClaudeEfforts, null),
        new("agy",
            [
                new("gemini-4-argon-low", "Gemini 4 Argon (Low)", []),
                new("gemini-4-argon-medium", "Gemini 4 Argon (Medium)", []),
                new("gemini-4-argon-high", "Gemini 4 Argon (High)", []),
                new("gemini-3.8-flash-low", "Gemini 3.8 Flash (Low)", []),
                new("gemini-3.8-flash-medium", "Gemini 3.8 Flash (Medium)", []),
                new("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)", []),
                new("claude-opus-5-5-low", "Claude Opus 5.5 (Low)", []),
                new("claude-opus-5-5-medium", "Claude Opus 5.5 (Medium)", []),
                new("claude-opus-5-5-high", "Claude Opus 5.5 (High)", []),
                new("claude-sonnet-5-5-low", "Claude Sonnet 5.5 (Low)", []),
                new("claude-sonnet-5-5-medium", "Claude Sonnet 5.5 (Medium)", []),
                new("claude-sonnet-5-5-high", "Claude Sonnet 5.5 (High)", []),
                new("gpt-oss-120b-medium", "GPT-OSS 120B (Medium)", []),
            ],
            [], "The effort is part of the agy model name."),
        new("muse",
            [
                new("muse-wonder-1.0", "Muse Wonder (plans big)", MuseEfforts),
                new("muse-spark-1.3", "Muse Spark (medium)", MuseEfforts),
                new("muse-ember-1.0", "Muse Ember (bounded steps)", MuseEfforts),
            ],
            MuseEfforts, null),
    ];

    public static AgentModels? For(string agent) =>
        All.FirstOrDefault(a => string.Equals(a.Agent, agent, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns an error message when the choice cannot work, or null when it is fine.</summary>
    public static string? Validate(string agent, string? model, string? effort)
    {
        var entry = For(agent);
        if (entry is null)
        {
            return string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(effort)
                ? null
                : $"Agent '{agent}' does not take a model or effort.";
        }
        ModelInfo? info = null;
        if (!string.IsNullOrWhiteSpace(model))
        {
            info = entry.Models.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
            if (info is null)
            {
                return $"Unknown model '{model}' for {agent}.";
            }
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            var allowed = info?.Efforts ?? entry.Efforts;
            if (!allowed.Contains(effort, StringComparer.OrdinalIgnoreCase))
            {
                return $"Effort '{effort}' is not available for {agent}{(info is null ? "" : " " + info.Id)}.";
            }
        }
        return null;
    }
}
