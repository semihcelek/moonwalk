using System.Text;
using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;

namespace MoonWalk.Pipeline.Stages;

/// <summary>
/// Runs the agent on the brief inside the run branch, checks what it changed against the guards, and commits it.
/// The agent never commits or pushes; MoonWalk does, only after the guards pass.
/// </summary>
public sealed class ImplementStage : IStage
{
    public string Name => "Implement";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        var agent = context.Agent ?? throw new InvalidOperationException("Implement needs an agent.");
        var workspace = context.Workspace ?? throw new InvalidOperationException("Implement needs a workspace.");
        var profile = context.Profile;

        context.SetStageAgent(agent.Name);
        context.Emit(EventTypes.System, $"Running {agent.Name} in {context.WorkDir}.");

        var result = await agent.RunAsync(new AgentRequest(BuildPrompt(context), context.WorkDir), context.Emit, ct);

        if (result.CostUsd is { } cost)
        {
            context.AddCost(cost);
        }
        context.Emit(EventTypes.System,
            $"Agent finished: exit {result.ExitCode}" + (result.NumTurns is { } turns ? $", {turns} turns" : "")
            + (result.CostUsd is { } c ? $", ${c:0.000}" : "") + (result.Usage is { } usage ? $", {usage}" : "") + ".",
            new() { ["exitCode"] = result.ExitCode, ["turns"] = result.NumTurns, ["costUsd"] = result.CostUsd, ["usage"] = result.Usage, ["sessionId"] = result.SessionId });

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
                "The agent edited files that were already modified before the run started, so MoonWalk cannot separate your changes from the agent's: "
                + string.Join(", ", changes.Conflicts.Take(8)));
        }
        if (changes.Paths.Count == 0)
        {
            return StageResult.Failed($"The agent finished without changing any file. {Short(result.Text)} {Short(result.Diagnostics, 400)}".Trim());
        }

        context.Emit(EventTypes.System, $"Agent changed {changes.Paths.Count} file(s).",
            new() { ["files"] = changes.Paths.Take(100).ToArray() });

        var lines = await workspace.Workspace.StageAsync(workspace.State, changes.Paths, context.RunDir, ct);
        var violations = Guards.Check(changes.Paths, lines, profile.Guards);
        if (violations.Count > 0)
        {
            foreach (var violation in violations)
            {
                context.Emit(EventTypes.Warning, "Guard: " + violation);
            }
            return StageResult.Failed("Guards failed: " + string.Join(" ", violations));
        }
        context.Emit(EventTypes.System, $"Guards passed ({changes.Paths.Count} files, {lines} lines).");

        var sha = await workspace.Workspace.CommitAsync(workspace.State, changes.Paths, BuildCommitMessage(context, agent, result), context.RunDir, ct);
        context.UpdateRun(r => r.Commit = sha);
        context.Emit(EventTypes.System, $"Committed {sha} on {workspace.State.Branch}.", new() { ["commit"] = sha });

        return StageResult.Passed(Short(result.Text, 400));
    }

    private static string BuildPrompt(StageContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are implementing a change in the repository at the current working directory (project \"{context.Profile.Name}\").");
        sb.AppendLine();
        sb.AppendLine("## Brief");
        sb.AppendLine(context.Brief);
        sb.AppendLine();

        var specPath = Path.Combine(context.RunDir, "spec.md");
        if (File.Exists(specPath))
        {
            var spec = File.ReadAllText(specPath);
            sb.AppendLine("## Spec (acceptance criteria to satisfy)");
            sb.AppendLine(spec.Length > 12000 ? spec[..12000] + "\n..." : spec);
            sb.AppendLine();
        }

        sb.Append(context.PromptContext());
        if (context.Attempt > 1)
        {
            sb.AppendLine("This is a retry. Earlier attempts may have left uncommitted changes in the working tree or commits on this branch.");
            sb.AppendLine("Run `git status` and `git log --oneline -5` first and build on that work instead of starting over.");
            sb.AppendLine();
        }

        sb.AppendLine("## Rules");
        sb.AppendLine("- Make the smallest change that fully satisfies the brief. Do not touch files unrelated to it.");
        sb.AppendLine("- Do not run git commit, git push or git add. MoonWalk reviews and commits your work after it checks it.");
        sb.AppendLine("- When you are done, reply with a short summary: what changed and in which files.");
        sb.AppendLine("- If you cannot complete the brief, make no half-finished edits and reply with `STATUS: BLOCKED` on the first line, then the reason.");
        foreach (var extra in context.Profile.ExtraInstructions)
        {
            sb.AppendLine("- " + extra);
        }
        return sb.ToString();
    }

    private static string BuildCommitMessage(StageContext context, IAgent agent, AgentResult result)
    {
        var title = context.Brief.Split('\n', 2)[0].Trim();
        if (title.Length > 72)
        {
            title = title[..72].TrimEnd() + "...";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"feat: {title}");
        sb.AppendLine();
        sb.AppendLine($"Automated by MoonWalk (agent: {agent.Name}).");
        if (!string.IsNullOrWhiteSpace(result.Text))
        {
            sb.AppendLine();
            sb.AppendLine(Short(result.Text, 800));
        }
        sb.AppendLine();
        sb.AppendLine($"MoonWalk-Run: {context.RunId}");
        sb.AppendLine($"MoonWalk-Agent: {agent.Name}");
        if (agent.Name == "claude")
        {
            sb.AppendLine("Co-Authored-By: Claude <noreply@anthropic.com>");
        }
        else if (agent.Name == "agy")
        {
            sb.AppendLine("Co-Authored-By: Gemini <noreply@google.com>");
        }
        return sb.ToString();
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
