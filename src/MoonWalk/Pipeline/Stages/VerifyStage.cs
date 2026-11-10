using System.ComponentModel;
using System.Text;
using MoonWalk.Agents;
using MoonWalk.Core;

namespace MoonWalk.Pipeline.Stages;

/// <summary>Runs the project's verify steps (compile, lint, tests, ...) in the repository and saves the combined output as evidence.</summary>
public sealed class VerifyStage : IStage
{
    public string Name => "Verify";

    public async Task<StageResult> RunAsync(StageContext context, CancellationToken ct)
    {
        var steps = context.Profile.Verify;
        if (steps.Length == 0)
        {
            return StageResult.Passed("No verify steps configured; skipped.");
        }

        var log = new StringBuilder();
        var tail = new Queue<string>();
        string? failure = null;

        foreach (var step in steps)
        {
            var label = string.IsNullOrWhiteSpace(step.Name) ? step.Command : step.Name;
            context.Emit(EventTypes.System, $"Verify step '{label}': {step.Command} {string.Join(' ', step.Args)}".TrimEnd());
            log.AppendLine($"$ {step.Command} {string.Join(' ', step.Args)}".TrimEnd());

            void OnLine(string line)
            {
                lock (log)
                {
                    log.AppendLine(line);
                    tail.Enqueue(line);
                    while (tail.Count > 15)
                    {
                        tail.Dequeue();
                    }
                }
                context.Emit(EventTypes.System, "  " + line);
            }

            tail.Clear();
            try
            {
                var (exit, timedOut) = await CliProcess.RunAsync(
                    step.Command, step.Args, context.Profile.RepoPath, "", TimeSpan.FromMinutes(step.TimeoutMinutes), OnLine, OnLine, ct);

                if (timedOut)
                {
                    failure = $"Verify step '{label}' timed out after {step.TimeoutMinutes} minutes.";
                }
                else if (exit != 0)
                {
                    failure = $"Verify step '{label}' failed (exit {exit}). Last output:\n" + string.Join('\n', tail);
                }
            }
            catch (Win32Exception ex)
            {
                failure = $"Verify step '{label}' could not start '{step.Command}': {ex.Message}";
            }

            if (failure is not null)
            {
                log.AppendLine(failure);
                break;
            }
            context.Emit(EventTypes.System, $"Verify step '{label}' passed.");
        }

        context.WriteArtifact("evidence/verify.log", log.ToString());

        if (failure is not null)
        {
            return StageResult.Failed(failure.Length > 1500 ? failure[..1500] + "..." : failure);
        }
        return StageResult.Passed($"{steps.Length} verify step(s) passed.");
    }
}
