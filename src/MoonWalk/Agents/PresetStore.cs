using System.Text.Json;
using MoonWalk.Core;

namespace MoonWalk.Agents;

/// <summary>A named combination of per-stage agent, model and effort choices, applied in one click in the New run dialog.</summary>
public sealed record Preset(string Name, Dictionary<string, AgentChoice> Stages, bool Builtin = false);

/// <summary>
/// Presets are the built-in ones (read-only, defined here) plus the ones you save, kept in presets.json. A preset names stages; the
/// dialog applies the names that exist in the chosen project and ignores the rest.
/// </summary>
public sealed class PresetStore(string file)
{
    private static readonly JsonSerializerOptions Options = new(Json.Options) { WriteIndented = true };
    private readonly object _lock = new();

    public static readonly IReadOnlyList<Preset> Builtin =
    [
        new("Claude: Opus plans, Sonnet implements", Pair("claude", "opus", "high", "claude", "sonnet", "medium"), true),
        new("Claude: Sonnet plans, Haiku implements", Pair("claude", "sonnet", "medium", "claude", "haiku", null), true),
        new("Muse: Wonder plans, Ember implements", Pair("muse", "muse-wonder-1.0", null, "muse", "muse-ember-1.0", null), true),
        new("Muse: Spark plans, Ember implements", Pair("muse", "muse-spark-1.3", null, "muse", "muse-ember-1.0", null), true),
        new("Gemini: Argon plans, Flash implements", Pair("agy", "gemini-4-argon-medium", null, "agy", "gemini-3.8-flash-high", null), true),
    ];

    public IReadOnlyList<Preset> Custom()
    {
        lock (_lock)
        {
            return Read();
        }
    }

    /// <summary>Saves (or replaces) a preset by name. Returns an error message, or null when saved.</summary>
    public string? Save(string name, Dictionary<string, AgentChoice> stages)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 80)
        {
            return "A preset needs a name of 1 to 80 characters.";
        }
        if (Builtin.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{name}' is a built-in preset; choose another name.";
        }
        foreach (var (stage, choice) in stages)
        {
            if (!string.IsNullOrWhiteSpace(choice.Agent) && ModelCatalog.Validate(choice.Agent, choice.Model, choice.Effort) is { } problem)
            {
                return $"{stage}: {problem}";
            }
        }

        lock (_lock)
        {
            var all = Read().Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            all.Add(new Preset(name, stages));
            Write(all);
        }
        return null;
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            var all = Read();
            var kept = all.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == all.Count)
            {
                return false;
            }
            Write(kept);
            return true;
        }
    }

    private List<Preset> Read()
    {
        if (!File.Exists(file))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<PresetFile>(File.ReadAllText(file), Options)?.Presets ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Write(List<Preset> presets)
    {
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new PresetFile { Presets = presets }, Options));
        File.Move(temp, file, overwrite: true);
    }

    private static Dictionary<string, AgentChoice> Pair(string planAgent, string planModel, string? planEffort, string implAgent, string implModel, string? implEffort) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Spec"] = new AgentChoice(planAgent, planModel, planEffort),
            ["Implement"] = new AgentChoice(implAgent, implModel, implEffort),
        };

    private sealed class PresetFile
    {
        public List<Preset> Presets { get; set; } = [];
    }
}
