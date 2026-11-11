using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Pipeline;

/// <summary>
/// After an agent stage: did the agent touch git itself? MoonWalk commits the agent's work only after checking the guards, so an agent
/// that commits or pushes on its own would skip that check. A push is refused outright. Commits the agent made are allowed but are
/// checked against the same guards, and reported, so nothing slips past them.
/// </summary>
internal static class AgentGitCheck
{
    /// <summary>Returns a stage result to stop with when something is wrong, or null to carry on. <paramref name="delta"/> says what happened.</summary>
    public static async Task<(StageResult? Problem, HeadDelta Delta)> AfterAgentAsync(
        StageContext context, WorkspaceSession workspace, string headBefore, CancellationToken ct)
    {
        var delta = await workspace.Workspace.InspectAsync(workspace.State, headBefore, ct);

        if (delta.Pushed)
        {
            context.Emit(EventTypes.Warning, $"The agent pushed '{workspace.State.Branch}' to origin.");
            return (StageResult.Blocked(
                $"The agent pushed the run branch '{workspace.State.Branch}' to origin on its own. MoonWalk never pushes, and the guards could not check it first. "
                + "Delete that remote branch if it should not be there, and turn off the agent's shell access (or git permissions) before retrying."), delta);
        }

        if (delta.Moved)
        {
            context.Emit(EventTypes.Warning,
                $"The agent made {delta.Commits.Count} commit(s) itself: {string.Join(" | ", delta.Commits.Take(4))}. MoonWalk normally commits the work after the guard check.",
                new() { ["commits"] = delta.Commits.ToArray() });

            var violations = Guards.Check(delta.ChangedPaths, delta.Lines, context.Profile.Guards);
            if (violations.Count > 0)
            {
                foreach (var violation in violations)
                {
                    context.Emit(EventTypes.Warning, "Guard: " + violation);
                }
                return (StageResult.Failed("The agent's own commit(s) break the guards: " + string.Join(" ", violations)
                    + " They are still on the run branch; retry, or stop the run."), delta);
            }
            context.Emit(EventTypes.System, $"The agent's own commit(s) pass the guards ({delta.ChangedPaths.Count} files, {delta.Lines} lines).");
        }

        return (null, delta);
    }
}
