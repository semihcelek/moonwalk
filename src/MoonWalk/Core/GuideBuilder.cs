using System.Text;
using MoonWalk.Agents;
using MoonWalk.Projects;

namespace MoonWalk.Core;

/// <summary>
/// Builds the delegation guide a main agent reads before it writes briefs. The text lives in docs/delegating-to-moonwalk.md (embedded in
/// the app); the placeholders are filled from the running configuration, so the names in it are the real project, agent and model names.
/// </summary>
public static class GuideBuilder
{
    public static string Build(string baseUrl, ProjectRegistry projects, PresetStore presets, string? onlyProject)
    {
        using var stream = typeof(GuideBuilder).Assembly.GetManifestResourceStream("delegating-to-moonwalk.md")
            ?? throw new InvalidOperationException("The delegation guide is not embedded in this build.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        return text
            .Replace("{{base_url}}", baseUrl.TrimEnd('/'))
            .Replace("{{models}}", Models())
            .Replace("{{presets}}", Presets(presets))
            .Replace("{{projects}}", Projects(projects, onlyProject));
    }

    private static string Models()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Agent | Model id | Efforts |");
        sb.AppendLine("|---|---|---|");
        foreach (var agent in ModelCatalog.All)
        {
            foreach (var model in agent.Models)
            {
                var efforts = model.Efforts.Length > 0 ? string.Join(", ", model.Efforts)
                    : agent.EffortNote is { } note ? note.TrimEnd('.') : "none";
                sb.AppendLine($"| `{agent.Agent}` | `{model.Id}` ({model.Label}) | {efforts} |");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Agents without a model list (any extra agent defined in `agents.json`) take no model or effort.");
        return sb.ToString().TrimEnd();
    }

    private static string Presets(PresetStore presets)
    {
        var names = PresetStore.Builtin.Select(p => p.Name).Concat(presets.Custom().Select(p => p.Name)).ToList();
        return names.Count == 0 ? "none yet" : string.Join("; ", names.Select(n => $"*{n}*"));
    }

    private static string Projects(ProjectRegistry registry, string? only)
    {
        var entries = registry.List()
            .Where(e => e.IsValid && !e.Profile.IsDemo)
            .Where(e => only is null || string.Equals(e.Profile.Name, only, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (entries.Count == 0)
        {
            return only is null ? "No project is configured yet." : $"No ready project named '{only}'.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Project | Stages | Default agent | Workspace | Base branch | Review branch | Auto default |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var e in entries)
        {
            var p = e.Profile;
            var auto = p.Auto.Full ? "full" : p.Auto.Plan ? "plan" : p.Auto.Implementation ? "implementation" : "off";
            sb.AppendLine($"| `{p.Name}` | {string.Join(" → ", p.Stages)} | {p.Agent} | {p.Workspace.ToString().ToLowerInvariant()} | `{p.BaseBranch}` | `{p.ReviewBranch}` | {auto} |");
        }

        foreach (var e in entries)
        {
            var p = e.Profile;
            if (p.BriefingNotes.Length == 0 && p.ExtraInstructions.Length == 0)
            {
                continue;
            }
            sb.AppendLine();
            sb.AppendLine($"### About `{p.Name}`");
            if (p.BriefingNotes.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("What to know when writing briefs for it:");
                foreach (var note in p.BriefingNotes)
                {
                    sb.AppendLine($"- {note}");
                }
            }
            if (p.ExtraInstructions.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Rules its run agents already follow (do not contradict them in a brief):");
                foreach (var rule in p.ExtraInstructions)
                {
                    sb.AppendLine($"- {rule}");
                }
            }
        }
        return sb.ToString().TrimEnd();
    }
}
