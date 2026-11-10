using MoonWalk.Core;

namespace MoonWalk.Pipeline.Stages;

public enum DemoBehavior
{
    Pass,

    /// <summary>Fails on the first attempt when the brief contains "[fail-verify]", so retry can be tried without an agent.</summary>
    FailFirstAttemptOnFlag,

    /// <summary>Ends by asking for a human, like the real Handover stage.</summary>
    NeedsHuman,
}

/// <summary>
/// Scripted stand-in for a real agent stage. It streams text deltas, tool calls and warnings so the live UI and the control
/// surface (gates, retry, pause, guidance) can be exercised end to end without any agent installed.
/// </summary>
public sealed class DemoStage(string name, string intro, DemoBehavior behavior = DemoBehavior.Pass, string? artifact = null) : IStage
{
    public string Name { get; } = name;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        context.Emit(EventTypes.System, $"Demo stage '{Name}' started (attempt {context.Attempt}).");
        await Stream(context, intro, ct);

        if (context.Guidance.Count > 0)
        {
            await Stream(context, "Taking your guidance into account: " + string.Join("; ", context.Guidance), ct);
        }

        context.Emit(EventTypes.AgentToolUse, "Read Assets/Scripts/Example.cs", new() { ["tool"] = "Read", ["path"] = "Assets/Scripts/Example.cs" });
        await Task.Delay(500, ct);
        context.Emit(EventTypes.AgentToolResult, "142 lines", new() { ["tool"] = "Read", ["lines"] = 142 });

        await Stream(context, "Looking at the brief again: \"" + Truncate(context.Brief, 80) + "\". I will keep the change small.", ct);

        context.Emit(EventTypes.AgentToolUse, "Edit Assets/Scripts/Example.cs", new() { ["tool"] = "Edit", ["replacements"] = 2 });
        await Task.Delay(600, ct);
        context.Emit(EventTypes.AgentToolResult, "2 replacements applied", new() { ["tool"] = "Edit" });

        if (artifact is not null)
        {
            context.WriteArtifact(artifact, $"# Demo {Name}\n\nBrief: {context.Brief}\n\nAttempt {context.Attempt}.\n");
        }

        switch (behavior)
        {
            case DemoBehavior.FailFirstAttemptOnFlag when context.Attempt == 1 && context.Brief.Contains("[fail-verify]", StringComparison.OrdinalIgnoreCase):
                context.Emit(EventTypes.Warning, "Demo stage fails on its first attempt.");
                return StageResult.Failed("Demo failure: the check did not pass. Retry it, or retry from Implement with a note.");
            case DemoBehavior.NeedsHuman:
                return StageResult.NeedsHuman("Demo handover: play it, then approve or retry with a note.");
        }

        context.Emit(EventTypes.AgentText, "Done.");
        return StageResult.Passed($"{Name} finished (demo).");
    }

    private static async Task Stream(StageContext context, string text, CancellationToken ct)
    {
        foreach (var word in text.Split(' '))
        {
            context.Emit(EventTypes.AgentTextDelta, word + " ");
            await Task.Delay(40, ct);
        }
        context.Emit(EventTypes.AgentText, "");
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
