using MoonWalk.Agents;
using MoonWalk.Core;

namespace MoonWalk.Pipeline.Stages;

/// <summary>
/// Turns the brief into a short spec with testable acceptance criteria. The agent is asked to be read-only (Claude: read-only tools,
/// shells blocked; agy: plan mode) and answers with the spec text; MoonWalk saves it as spec.md in the run folder, never in the
/// repository. Because not every agent can enforce read-only, the stage checks afterwards that no file changed.
/// </summary>
public sealed class SpecStage : IStage
{
    private static readonly string[] ReadOnlyTools = ["Read", "Grep", "Glob"];

    // Claude Code auto-approves read-only shell commands, so shells are blocked outright to make "read-only" absolute.
    private static readonly string[] NoWriteTools = ["Edit", "Write", "NotebookEdit", "Bash", "PowerShell"];

    public string Name => "Spec";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        var agent = context.Agent ?? throw new InvalidOperationException("Spec needs an agent.");
        context.SetStageAgent(agent.Name);
        context.Emit(EventTypes.System, $"Running {agent.Name} read-only to write the spec.");

        var result = await agent.RunAsync(
            new AgentRequest(BuildPrompt(context), context.WorkDir, ReadOnlyTools, NoWriteTools, ReadOnly: true),
            context.Emit,
            ct);

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
            return StageResult.Failed($"The agent failed (exit {result.ExitCode}).");
        }

        if (context.Workspace is { } workspace)
        {
            var changes = await workspace.Workspace.GetChangesAsync(workspace.State, ct);
            if (changes.Paths.Count > 0)
            {
                return StageResult.Failed(
                    $"The spec agent changed {changes.Paths.Count} file(s) even though it was asked to be read-only: "
                    + string.Join(", ", changes.Paths.Take(6)) + ". Revert them and retry, or use another agent for this stage.");
            }
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            return StageResult.Failed("The agent returned no spec.");
        }

        context.WriteArtifact("spec.md", result.Text.Trim() + Environment.NewLine);
        context.Emit(EventTypes.System, $"Spec written ({result.Text.Length} characters).");

        var firstLine = result.Text.Split('\n').Select(l => l.Trim().TrimStart('#', ' ')).FirstOrDefault(l => l.Length > 0) ?? "Spec";
        return StageResult.Passed(firstLine.Length > 160 ? firstLine[..160] + "..." : firstLine);
    }

    private static string BuildPrompt(StageContext context)
    {
        return $"""
            You are writing a short spec for a change in the repository at the current working directory (project "{context.Profile.Name}"). You may read the code but must not change anything.

            ## Brief
            {context.Brief}

            {context.PromptContext()}## Task
            Read what you need, then reply with ONLY the spec in Markdown, using exactly these sections:

            # <short title>
            ## Goal
            One or two sentences.
            ## Acceptance criteria
            A numbered list. Each item must be observable and testable by playing or checking the result, not a description of code.
            ## Out of scope
            What this change deliberately does not do.
            ## Where it likely lives
            The files or folders that probably need to change.

            Keep it under 60 lines. Do not edit any file.
            """;
    }
}
