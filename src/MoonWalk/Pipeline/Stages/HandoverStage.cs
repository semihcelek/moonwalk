using System.Text;
using MoonWalk.Core;

namespace MoonWalk.Pipeline.Stages;

/// <summary>
/// Last stage: writes handover.md (what changed, commits, verify result, how to test) and then waits for you to play the
/// result. The repository stays on the run branch. Approve when it plays well, or retry from an earlier stage with a note.
/// </summary>
public sealed class HandoverStage : IStage
{
    public string Name => "Handover";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        var workspace = context.Workspace ?? throw new InvalidOperationException("Handover needs a workspace.");
        var run = context.GetRun();
        var state = workspace.State;

        var info = await workspace.Workspace.GetHandoverInfoAsync(state, ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Handover: {run?.Title ?? context.Brief}");
        sb.AppendLine();
        sb.AppendLine($"- Project: {context.Profile.Name}");
        sb.AppendLine($"- Run: {context.RunId}");
        sb.AppendLine($"- Branch: `{state.Branch}` (from `{state.BaseBranch}`)");
        sb.AppendLine($"- The repository is on this branch now. Releasing the workspace puts it back on `{state.ReturnTo}`.");
        if (run?.CostUsd is { } cost)
        {
            sb.AppendLine($"- Agent cost so far: ${cost:0.000}");
        }
        sb.AppendLine();

        sb.AppendLine("## Stages");
        foreach (var stage in run?.Stages ?? [])
        {
            if (stage.Name == Name)
            {
                continue;
            }
            sb.AppendLine($"- {stage.Name}: {stage.Status}{(string.IsNullOrWhiteSpace(stage.Summary) ? "" : " - " + stage.Summary.ReplaceLineEndings(" "))}");
        }
        sb.AppendLine();

        sb.AppendLine("## Commits");
        sb.AppendLine(string.IsNullOrWhiteSpace(info.Log) ? "(none)" : info.Log.TrimEnd());
        sb.AppendLine();
        sb.AppendLine("## Changed files");
        sb.AppendLine("```");
        sb.AppendLine(string.IsNullOrWhiteSpace(info.DiffStat) ? "(no changes against the base branch)" : info.DiffStat.TrimEnd());
        sb.AppendLine("```");

        if (run?.Guidance.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Guidance given during the run");
            foreach (var note in run.Guidance)
            {
                sb.AppendLine($"- {note.Text.ReplaceLineEndings(" ")}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## What to do now");
        sb.AppendLine("1. Open the project (the repository is already on the run branch) and play or review the change.");
        sb.AppendLine("2. In MoonWalk: **Approve** if it is good, or **Retry** from Implement with a note describing what you found.");
        sb.AppendLine("3. After approving, press **Release workspace** to return the repository to your previous branch.");

        context.WriteArtifact("handover.md", sb.ToString());
        context.Emit(EventTypes.System, "Handover written.");

        return StageResult.NeedsHuman($"Ready for your playtest on {state.Branch}. Approve when it plays well, or retry with a note.");
    }
}
