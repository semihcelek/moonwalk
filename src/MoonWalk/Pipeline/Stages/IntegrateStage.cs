using System.Text;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Pipeline.Stages;

/// <summary>
/// The Unity step after Implement: an agent with the unity-mcp tools wires what the code needs into the scene (components, references,
/// materials), checks the Console for compile errors and reports. It has its own agent, model and effort like every agent stage.
/// Whatever it changed on disk is checked against the guards and committed like Implement's work; changing nothing is fine.
/// </summary>
public sealed class IntegrateStage : IStage
{
    private static readonly string[] UnityTools = ["mcp__unityMCP__*"];

    public string Name => "Integrate";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        var agent = context.Agent ?? throw new InvalidOperationException("Integrate needs an agent.");
        var workspace = context.Workspace ?? throw new InvalidOperationException("Integrate needs a workspace.");

        context.SetStageAgent(agent.Name);
        context.Emit(EventTypes.System, $"Running {agent.Name} with unity-mcp in {context.WorkDir}.");

        var allowed = context.Profile.Claude.AllowedTools.Concat(UnityTools).ToArray();
        var result = await agent.RunAsync(new AgentRequest(BuildPrompt(context), context.WorkDir, allowed), context.Emit, ct);

        if (result.CostUsd is { } cost)
        {
            context.AddCost(cost);
        }
        if (result.Usage is { } usage)
        {
            context.Emit(EventTypes.System, $"Agent used {usage}.");
        }
        if (result.TimedOut)
        {
            return StageResult.Failed("The agent timed out.");
        }
        if (result.ExitCode != 0 || result.IsError)
        {
            return StageResult.Failed($"The agent failed (exit {result.ExitCode}). {Short(result.Text)} {Short(result.Diagnostics)}".Trim());
        }
        if (result.Text is { } text && text.TrimStart().StartsWith("STATUS: BLOCKED", StringComparison.OrdinalIgnoreCase))
        {
            return StageResult.Blocked(Short(text, 600));
        }

        var changes = await workspace.Workspace.GetChangesAsync(workspace.State, ct);
        if (changes.Conflicts.Count > 0)
        {
            return StageResult.Blocked(
                "The agent edited files that were already modified before the run started: " + string.Join(", ", changes.Conflicts.Take(8)));
        }
        if (changes.Paths.Count > 0)
        {
            var lines = await workspace.Workspace.StageAsync(workspace.State, changes.Paths, context.RunDir, ct);
            var violations = Guards.Check(changes.Paths, lines, context.Profile.Guards);
            if (violations.Count > 0)
            {
                foreach (var violation in violations)
                {
                    context.Emit(EventTypes.Warning, "Guard: " + violation);
                }
                return StageResult.Failed("Guards failed: " + string.Join(" ", violations));
            }
            var message = $"chore: unity integration for {Title(context.Brief)}\n\nAutomated by MoonWalk (agent: {agent.Name}).\n\nMoonWalk-Run: {context.RunId}\n";
            var sha = await workspace.Workspace.CommitAsync(workspace.State, changes.Paths, message, context.RunDir, ct);
            context.UpdateRun(r => r.Commit = sha);
            context.Emit(EventTypes.System, $"Committed {sha} on {workspace.State.Branch}.", new() { ["commit"] = sha });
        }
        else
        {
            context.Emit(EventTypes.System, "No files changed on disk (scene edits made through unity-mcp live in the open Editor until you save).");
        }

        context.WriteArtifact("integrate.md", (result.Text ?? "").Trim() + Environment.NewLine);
        return StageResult.Passed(Short(result.Text, 400));
    }

    private static string BuildPrompt(StageContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are integrating a finished code change into the Unity project at the current working directory (project \"{context.Profile.Name}\").");
        sb.AppendLine("The scripts are already written and committed. Your job is the Unity Editor side, using the unity-mcp tools.");
        sb.AppendLine();
        sb.AppendLine("## Brief");
        sb.AppendLine(context.Brief);
        sb.AppendLine();

        var specPath = Path.Combine(context.RunDir, "spec.md");
        if (File.Exists(specPath))
        {
            var spec = File.ReadAllText(specPath);
            sb.AppendLine("## Spec");
            sb.AppendLine(spec.Length > 8000 ? spec[..8000] + "\n..." : spec);
            sb.AppendLine();
        }

        sb.Append(context.PromptContext());
        sb.AppendLine("## Do this");
        sb.AppendLine("1. Read the Unity Console and wait for compilation. If there is any `error CS`, fix the script and check again.");
        sb.AppendLine("2. Wire what the change needs in the scene or assets (add components, assign references and materials), using unity-mcp tools such as execute_code. Do not write new editor scripts.");
        sb.AppendLine("3. Check the Console again; it must have no new errors from your change.");
        sb.AppendLine("4. Save the scene if you changed it.");
        sb.AppendLine("- Do not run git commit, git push or git add. MoonWalk commits afterwards.");
        sb.AppendLine("- If the Unity Editor is not reachable, reply with `STATUS: BLOCKED` on the first line, then the reason.");
        sb.AppendLine("- Finish with a short summary of what you wired and the Console state.");
        return sb.ToString();
    }

    private static string Title(string brief)
    {
        var title = brief.Split('\n', 2)[0].Trim();
        return title.Length > 60 ? title[..60].TrimEnd() + "..." : title;
    }

    private static string Short(string? text, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var flat = text.Trim().ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
