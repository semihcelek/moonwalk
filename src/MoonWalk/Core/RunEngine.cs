using System.Collections.Concurrent;
using MoonWalk.Pipeline;

namespace MoonWalk.Core;

/// <summary>Runs a pipeline stage by stage in the background, recording state and emitting events as it goes.</summary>
public sealed class RunEngine(RunStore store, EventHub hub, string runsRoot)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public Run Start(string brief, string project)
    {
        var stages = DemoPipeline.Create();
        var now = DateTimeOffset.Now;
        var run = new Run
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}",
            Project = project,
            Brief = brief,
            Title = MakeTitle(brief),
            CreatedAt = now,
            Stages = stages.Select(s => new StageRecord { Name = s.Name }).ToList(),
        };
        store.Add(run);

        var cts = new CancellationTokenSource();
        _active[run.Id] = cts;
        _ = Task.Run(() => ExecuteAsync(run.Id, brief, stages, cts));
        return run;
    }

    public bool Cancel(string id)
    {
        if (!_active.TryGetValue(id, out var cts))
        {
            return false;
        }
        cts.Cancel();
        return true;
    }

    private async Task ExecuteAsync(string id, string brief, IReadOnlyList<IStage> stages, CancellationTokenSource cts)
    {
        var runDir = Path.Combine(runsRoot, id);
        var ct = cts.Token;
        var currentName = "";
        try
        {
            store.Update(id, r => r.Status = RunStatus.Running);
            hub.Append(id, EventTypes.RunStarted, null, "Run started.");

            foreach (var stage in stages)
            {
                currentName = stage.Name;
                ct.ThrowIfCancellationRequested();
                store.Update(id, r =>
                {
                    r.CurrentStage = stage.Name;
                    var record = r.Stages.First(s => s.Name == stage.Name);
                    record.Status = StageStatus.Running;
                    record.StartedAt = DateTimeOffset.Now;
                });
                hub.Append(id, EventTypes.StageStarted, stage.Name, $"Stage {stage.Name} started.");

                var result = await stage.RunAsync(new StageContext(id, brief, runDir, stage.Name, hub), ct);

                store.Update(id, r =>
                {
                    var record = r.Stages.First(s => s.Name == stage.Name);
                    record.Status = ToStageStatus(result.Outcome);
                    record.FinishedAt = DateTimeOffset.Now;
                    record.Summary = result.Summary;
                });
                hub.Append(id, EventTypes.StageFinished, stage.Name, result.Summary,
                    new() { ["outcome"] = result.Outcome.ToString() });

                if (result.Outcome != StageOutcome.Passed)
                {
                    Finish(id, result.Outcome == StageOutcome.NeedsHuman ? RunStatus.WaitingForHuman : RunStatus.Failed,
                        result.Outcome == StageOutcome.NeedsHuman ? null : result.Summary);
                    return;
                }
            }

            Finish(id, RunStatus.Done, null);
        }
        catch (OperationCanceledException)
        {
            store.Update(id, r =>
            {
                var running = r.Stages.FirstOrDefault(s => s.Name == currentName && s.Status == StageStatus.Running);
                if (running is not null)
                {
                    running.Status = StageStatus.Cancelled;
                    running.FinishedAt = DateTimeOffset.Now;
                }
            });
            Finish(id, RunStatus.Cancelled, "Cancelled by user.");
        }
        catch (Exception ex)
        {
            hub.Append(id, EventTypes.Error, currentName, ex.Message);
            Finish(id, RunStatus.Failed, ex.Message);
        }
        finally
        {
            _active.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    private void Finish(string id, RunStatus status, string? error)
    {
        store.Update(id, r =>
        {
            r.Status = status;
            r.Error = error;
            if (!Run.IsActive(status))
            {
                r.FinishedAt = DateTimeOffset.Now;
                r.CurrentStage = null;
            }
        });
        hub.Append(id, EventTypes.RunFinished, null, error ?? status.ToString(),
            new() { ["status"] = status.ToString() });
    }

    private static StageStatus ToStageStatus(StageOutcome outcome) => outcome switch
    {
        StageOutcome.Passed => StageStatus.Passed,
        StageOutcome.Failed => StageStatus.Failed,
        StageOutcome.NeedsHuman => StageStatus.NeedsHuman,
        _ => StageStatus.Blocked,
    };

    private static string MakeTitle(string brief)
    {
        var line = brief.Split('\n', 2)[0].Trim();
        return line.Length <= 80 ? line : line[..80] + "...";
    }
}
