using System.Collections.Concurrent;
using MoonWalk.Agents;
using MoonWalk.Pipeline;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Core;

/// <summary>
/// Runs a pipeline stage by stage in the background, recording state and emitting events as it goes.
/// In branch workspace mode only one run per project may own the working folder: others wait as Queued. A run that
/// finishes successfully keeps the folder (the repo stays on its branch so it can be played) until it is released.
/// </summary>
public sealed class RunEngine
{
    private readonly RunStore _store;
    private readonly EventHub _hub;
    private readonly ProjectRegistry _projects;
    private readonly string _runsRoot;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _holders = new(StringComparer.OrdinalIgnoreCase);

    public RunEngine(RunStore store, EventHub hub, ProjectRegistry projects, string runsRoot)
    {
        _store = store;
        _hub = hub;
        _projects = projects;
        _runsRoot = runsRoot;

        // A run that still owns its workspace from before a restart keeps owning it.
        foreach (var (id, project) in store.HeldWorkspaces())
        {
            if (LockFor(project).Wait(0))
            {
                _holders[project] = id;
            }
        }
    }

    public Run Start(string brief, string projectName)
    {
        var entry = _projects.Get(projectName) ?? throw new ArgumentException($"Unknown project '{projectName}'.");
        if (!entry.IsValid)
        {
            throw new ArgumentException($"Project '{projectName}' has configuration errors: {string.Join(" ", entry.Errors)}");
        }

        var profile = entry.Profile;
        var stages = PipelineFactory.Create(profile);
        var now = DateTimeOffset.Now;
        var run = new Run
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}",
            Project = profile.Name,
            Brief = brief,
            Title = MakeTitle(brief),
            Agent = profile.IsDemo ? null : profile.Agent,
            CreatedAt = now,
            Stages = stages.Select(s => new StageRecord { Name = s.Name }).ToList(),
        };
        _store.Add(run);

        var cts = new CancellationTokenSource();
        _active[run.Id] = cts;
        _ = Task.Run(() => ExecuteAsync(run.Id, brief, profile, stages, cts));
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

    /// <summary>Gives the working folder back: the repository returns to the branch it was on before the run.</summary>
    public async Task<(bool Ok, string Message)> ReleaseAsync(string id)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (!run.WorkspaceHeld)
        {
            return (false, "This run does not hold the workspace.");
        }

        var entry = _projects.Get(run.Project);
        var state = WorkspaceState.Load(Path.Combine(_runsRoot, id));
        if (entry is null || state is null)
        {
            return (false, "Project profile or workspace state is missing; restore the branch by hand.");
        }

        try
        {
            var profile = entry.Profile;
            await new BranchWorkspace(profile.RepoPath, profile.BaseBranch, profile.DirtyPolicy).ReleaseAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _hub.Append(id, EventTypes.Warning, null, $"Could not release the workspace: {ex.Message}");
            return (false, ex.Message);
        }

        var first = false;
        _store.Update(id, r =>
        {
            first = r.WorkspaceHeld;
            r.WorkspaceHeld = false;
        });
        if (first)
        {
            ReleaseLock(run.Project, id);
            _hub.Append(id, EventTypes.System, null, $"Workspace released; the repository is back on '{state.ReturnTo}'.");
        }
        return (true, $"Back on '{state.ReturnTo}'.");
    }

    private async Task ExecuteAsync(string id, string brief, ProjectProfile profile, IReadOnlyList<IStage> stages, CancellationTokenSource cts)
    {
        var runDir = Path.Combine(_runsRoot, id);
        var ct = cts.Token;
        var usesWorkspace = !profile.IsDemo && profile.Workspace == WorkspaceMode.Branch;

        var lockHeld = false;
        WorkspaceSession? session = null;
        var currentStage = "";
        var status = RunStatus.Done;
        string? error = null;

        try
        {
            if (usesWorkspace)
            {
                var gate = LockFor(profile.Name);
                if (!gate.Wait(0))
                {
                    _holders.TryGetValue(profile.Name, out var holder);
                    _hub.Append(id, EventTypes.System, null,
                        $"Waiting for project '{profile.Name}': its workspace is in use by run {holder ?? "(unknown)"}. Release it to continue.");
                    await gate.WaitAsync(ct);
                }
                lockHeld = true;
                _holders[profile.Name] = id;
            }

            _store.Update(id, r => r.Status = RunStatus.Running);
            _hub.Append(id, EventTypes.RunStarted, null, "Run started.");

            if (usesWorkspace)
            {
                _hub.Append(id, EventTypes.System, null, "Preparing workspace.");
                var workspace = new BranchWorkspace(profile.RepoPath, profile.BaseBranch, profile.DirtyPolicy);
                var state = await workspace.PrepareAsync(id, _store.Get(id)!.Title, msg => _hub.Append(id, EventTypes.System, null, msg), ct);
                Directory.CreateDirectory(runDir);
                state.Save(runDir);
                session = new WorkspaceSession(workspace, state);
                _store.Update(id, r =>
                {
                    r.Branch = state.Branch;
                    r.ReturnTo = state.ReturnTo;
                });
            }

            var agent = profile.IsDemo ? null : AgentFactory.Create(profile);

            foreach (var stage in stages)
            {
                currentStage = stage.Name;
                ct.ThrowIfCancellationRequested();
                _store.Update(id, r =>
                {
                    r.CurrentStage = stage.Name;
                    var record = r.Stages.First(s => s.Name == stage.Name);
                    record.Status = StageStatus.Running;
                    record.StartedAt = DateTimeOffset.Now;
                });
                _hub.Append(id, EventTypes.StageStarted, stage.Name, $"Stage {stage.Name} started.");

                var context = new StageContext(id, brief, runDir, stage.Name, _hub, profile, session, agent, mutate => _store.Update(id, mutate));
                var result = await stage.RunAsync(context, ct);

                _store.Update(id, r =>
                {
                    var record = r.Stages.First(s => s.Name == stage.Name);
                    record.Status = ToStageStatus(result.Outcome);
                    record.FinishedAt = DateTimeOffset.Now;
                    record.Summary = result.Summary;
                });
                _hub.Append(id, EventTypes.StageFinished, stage.Name, result.Summary,
                    new() { ["outcome"] = result.Outcome.ToString() });

                if (result.Outcome != StageOutcome.Passed)
                {
                    status = result.Outcome == StageOutcome.NeedsHuman ? RunStatus.WaitingForHuman : RunStatus.Failed;
                    error = result.Outcome == StageOutcome.NeedsHuman ? null : result.Summary;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = RunStatus.Cancelled;
            error = "Cancelled by user.";
            _store.Update(id, r =>
            {
                var running = r.Stages.FirstOrDefault(s => s.Name == currentStage && s.Status == StageStatus.Running);
                if (running is not null)
                {
                    running.Status = StageStatus.Cancelled;
                    running.FinishedAt = DateTimeOffset.Now;
                }
            });
        }
        catch (Exception ex)
        {
            status = RunStatus.Failed;
            error = ex.Message;
            _hub.Append(id, EventTypes.Error, currentStage.Length > 0 ? currentStage : null, ex.Message);
        }

        await SettleWorkspaceAsync(id, profile, runDir, session, lockHeld, status);
        Finish(id, status, error);
        _active.TryRemove(id, out _);
        cts.Dispose();
    }

    /// <summary>
    /// A finished or waiting run keeps the workspace so the result can be played. Anything else keeps its partial work as a local
    /// wip commit, puts the repository back where it was, and frees the project for the next run.
    /// </summary>
    private async Task SettleWorkspaceAsync(string id, ProjectProfile profile, string runDir, WorkspaceSession? session, bool lockHeld, RunStatus status)
    {
        if (!lockHeld)
        {
            return;
        }

        if (session is not null && status is RunStatus.Done or RunStatus.WaitingForHuman)
        {
            _store.Update(id, r => r.WorkspaceHeld = true);
            _hub.Append(id, EventTypes.System, null,
                $"Workspace kept: the repository is on '{session.State.Branch}'. Release it when you are done to return to '{session.State.ReturnTo}'.");
            return;
        }

        if (session is not null)
        {
            var warning = await session.Workspace.AbortAsync(session.State, id, runDir, CancellationToken.None);
            _hub.Append(id, warning is null ? EventTypes.System : EventTypes.Warning,
                null,
                warning ?? $"Partial work (if any) kept on local branch '{session.State.Branch}'; repository restored to '{session.State.ReturnTo}'.");
        }
        ReleaseLock(profile.Name, id);
    }

    private SemaphoreSlim LockFor(string project) => _locks.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));

    private void ReleaseLock(string project, string runId)
    {
        if (_holders.TryGetValue(project, out var holder) && holder == runId)
        {
            _holders.TryRemove(project, out _);
            LockFor(project).Release();
        }
    }

    private void Finish(string id, RunStatus status, string? error)
    {
        _store.Update(id, r =>
        {
            r.Status = status;
            r.Error = error;
            if (!Run.IsActive(status))
            {
                r.FinishedAt = DateTimeOffset.Now;
                r.CurrentStage = null;
            }
        });
        _hub.Append(id, EventTypes.RunFinished, null, error ?? status.ToString(),
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
