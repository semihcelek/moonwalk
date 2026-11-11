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
            if (!string.IsNullOrWhiteSpace(step.UnityMcp))
            {
                var unityLabel = string.IsNullOrWhiteSpace(step.Name) ? "unity compile" : step.Name;
                context.Emit(EventTypes.System, $"Verify step '{unityLabel}': Unity compile check through {step.UnityMcp}");
                log.AppendLine($"$ unity compile check ({step.UnityMcp})");
                try
                {
                    var check = await UnityCompileCheck.RunAsync(step.UnityMcp, TimeSpan.FromMinutes(step.TimeoutMinutes), line => context.Emit(EventTypes.System, "  " + line), ct);
                    log.AppendLine(check.Summary);
                    if (!check.Passed)
                    {
                        failure = check.Summary;
                    }
                    else
                    {
                        context.Emit(EventTypes.System, $"Verify step '{unityLabel}' passed: {check.Summary}");
                    }
                }
                catch (UnityUnavailableException ex) when (step.Optional)
                {
                    log.AppendLine("skipped: " + ex.Message);
                    context.Emit(EventTypes.Warning, $"Verify step '{unityLabel}' skipped, so this result was NOT compiled: {ex.Message}");
                }
                catch (UnityUnavailableException ex)
                {
                    failure = $"Verify step '{unityLabel}' could not run: {ex.Message}";
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failure = $"Verify step '{unityLabel}' timed out after {step.TimeoutMinutes} minutes (is the Editor stuck on a dialog or still importing?).";
                }
                if (failure is not null)
                {
                    log.AppendLine(failure);
                    break;
                }
                continue;
            }

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
                    step.Command, step.Args, context.WorkDir, "", TimeSpan.FromMinutes(step.TimeoutMinutes), OnLine, OnLine, ct);

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
