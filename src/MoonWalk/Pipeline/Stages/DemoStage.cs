using MoonWalk.Core;

namespace MoonWalk.Pipeline.Stages;

/// <summary>
/// Scripted stand-in for a real agent stage. It streams text deltas, tool calls and warnings so the live UI
/// can be exercised end to end without any agent installed. Replaced by real stages in chunk 2.
/// </summary>
public sealed class DemoStage(string name, string intro, bool fail = false) : IStage
{
    public string Name { get; } = name;

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        context.Emit(EventTypes.System, $"Demo stage '{Name}' started.");
        await Stream(context, intro, ct);

        context.Emit(EventTypes.AgentToolUse, "Read", new() { ["tool"] = "Read", ["path"] = "Assets/Scripts/Example.cs" });
        await Task.Delay(700, ct);
        context.Emit(EventTypes.AgentToolResult, "142 lines", new() { ["tool"] = "Read", ["lines"] = 142 });

        await Stream(context, "Looking at the brief again: \"" + Truncate(context.Brief, 80) + "\". I will keep the change small.", ct);

        context.Emit(EventTypes.AgentToolUse, "Edit", new() { ["tool"] = "Edit", ["path"] = "Assets/Scripts/Example.cs", ["replacements"] = 2 });
        await Task.Delay(900, ct);
        context.Emit(EventTypes.AgentToolResult, "2 replacements applied", new() { ["tool"] = "Edit" });

        if (fail)
        {
            context.Emit(EventTypes.Warning, "Demo stage was told to fail.");
            return StageResult.Failed("Demo failure requested.");
        }

        context.Emit(EventTypes.AgentText, "Done.");
        return StageResult.Passed($"{Name} finished (demo).");
    }

    private static async Task Stream(StageContext context, string text, CancellationToken ct)
    {
        foreach (var word in text.Split(' '))
        {
            context.Emit(EventTypes.AgentTextDelta, word + " ");
            await Task.Delay(55, ct);
        }
        context.Emit(EventTypes.AgentText, "");
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";
}
