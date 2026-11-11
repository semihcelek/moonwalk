using System.Collections.Concurrent;
using MoonWalk.Agents;
using MoonWalk.Pipeline;
using MoonWalk.Pipeline.Stages;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Core;

/// <summary>What the person decided while a run was waiting: approve, retry (optionally from an earlier stage, with another agent), or stop.</summary>
public sealed record DecisionRequest(string Action, string? Note = null, string? Agent = null, string? RetryFrom = null, string? Model = null, string? Effort = null);

/// <summary>
/// Runs a pipeline stage by stage in the background, recording state and emitting events as it goes. A run waits for a
/// decision at configured gates, after a stage that asks for a human, and after a failed or blocked stage (retry or stop).
/// In branch workspace mode only one run per project may own the working folder: others wait as Queued. In worktree mode every
/// run has its own folder and runs in parallel. A run that finishes successfully keeps its workspace (so the result can be
/// played) until it is released. A run interrupted by a MoonWalk restart can be recovered where it stopped.
/// </summary>
public sealed class RunEngine
{
    private sealed class RunControl(CancellationTokenSource cts, IReadOnlyList<string> stageNames)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public IReadOnlyList<string> StageNames { get; } = stageNames;

        public bool PauseRequested { get; set; }

        public TaskCompletionSource? Resume { get; set; }

        public TaskCompletionSource<DecisionRequest>? Decision { get; set; }

        public WaitState? Waiting { get; set; }
    }

    private readonly RunStore _store;
    private readonly EventHub _hub;
    private readonly ProjectRegistry _projects;
    private readonly AgentRegistry _agents;
    private readonly string _runsRoot;
    private readonly ConcurrentDictionary<string, RunControl> _controls = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _holders = new(StringComparer.OrdinalIgnoreCase);

    public RunEngine(RunStore store, EventHub hub, ProjectRegistry projects, AgentRegistry agents, string runsRoot)
    {
        _store = store;
        _hub = hub;
        _projects = projects;
        _agents = agents;
        _runsRoot = runsRoot;

        // A run that still owns a shared project folder from before a restart keeps owning it. Worktrees need no lock.
        foreach (var (id, project) in store.HeldWorkspaces())
        {
            if (_projects.Get(project)?.Profile.Workspace == WorkspaceMode.Branch && LockFor(project).Wait(0))
            {
                _holders[project] = id;
            }
        }
    }

    /* ------------------------------------------------------------ starting */

    public Run Start(string brief, string projectName, IReadOnlyDictionary<string, AgentChoice>? choices = null)
    {
        var entry = _projects.Get(projectName) ?? throw new ArgumentException($"Unknown project '{projectName}'.");
        if (!entry.IsValid)
        {
            throw new ArgumentException($"Project '{projectName}' has configuration errors: {string.Join(" ", entry.Errors)}");
        }

        var profile = entry.Profile;
        var stages = PipelineFactory.Create(profile);
        var stageAgents = new Dictionary<string, AgentChoice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stageName, choice) in choices ?? new Dictionary<string, AgentChoice>())
        {
            if (stages.All(s => !string.Equals(s.Name, stageName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Unknown stage '{stageName}'.");
            }
            var agentFor = string.IsNullOrWhiteSpace(choice.Agent) ? profile.Agent : choice.Agent;
            if (!_agents.IsKnown(agentFor))
            {
                throw new ArgumentException($"Unknown agent '{agentFor}'.");
            }
            if (ModelCatalog.Validate(agentFor, choice.Model, choice.Effort) is { } problem)
            {
                throw new ArgumentException($"{stageName}: {problem}");
            }
            if (!string.IsNullOrWhiteSpace(choice.Agent) || !string.IsNullOrWhiteSpace(choice.Model) || !string.IsNullOrWhiteSpace(choice.Effort))
            {
                stageAgents[stageName] = choice;
            }
        }
        var now = DateTimeOffset.Now;
        var run = new Run
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}",
            Project = profile.Name,
            Brief = brief,
            Title = MakeTitle(brief),
            Agent = profile.IsDemo ? null : profile.Agent,
            StageAgents = stageAgents,
            CreatedAt = now,
            Stages = stages.Select(s => new StageRecord { Name = s.Name }).ToList(),
        };
        _store.Add(run);

        var control = new RunControl(new CancellationTokenSource(), stages.Select(s => s.Name).ToList());
        _controls[run.Id] = control;
        _ = Task.Run(() => ExecuteAsync(run.Id, brief, profile, stages, control, recover: false));
        return run;
    }

    /// <summary>The agent for one stage: the run's choice for that stage (agent, model, effort) over the project's defaults.</summary>
    private IAgent CreateStageAgent(string runId, string stageName, string currentAgent, ProjectProfile profile)
    {
        var choice = _store.Get(runId)?.StageAgents.GetValueOrDefault(stageName);
        var name = string.IsNullOrWhiteSpace(choice?.Agent) ? currentAgent : choice!.Agent!;
        return _agents.Create(name, profile, choice?.Model, choice?.Effort);
    }

    /// <summary>
    /// Continues a run that a MoonWalk restart interrupted. If it was waiting for you, the same question is asked again; otherwise
    /// it picks up at the first stage that had not passed (an agent stage that was cut off runs again as a retry).
    /// </summary>
    public async Task<(bool Ok, string Message)> RecoverAsync(string id)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (run.Status != RunStatus.Interrupted || _controls.ContainsKey(id))
        {
            return (false, "Only an interrupted run can be recovered.");
        }

        var entry = _projects.Get(run.Project);
        if (entry is null || !entry.IsValid)
        {
            return (false, $"Project '{run.Project}' is missing or has configuration errors.");
        }

        var profile = entry.Profile;
        IReadOnlyList<IStage> stages;
        try
        {
            stages = PipelineFactory.Create(profile);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message);
        }
        if (!stages.Select(s => s.Name).SequenceEqual(run.Stages.Select(s => s.Name)))
        {
            return (false, "The project's stages changed since this run started, so it cannot be resumed.");
        }

        if (!profile.IsDemo && profile.Workspace != WorkspaceMode.None && WorkspaceState.Load(Path.Combine(_runsRoot, id)) is { } state)
        {
            try
            {
                await WorkspaceFactory.Create(profile).EnsureReadyAsync(state, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return (false, $"The workspace cannot be reused: {ex.Message}");
            }
        }

        var control = new RunControl(new CancellationTokenSource(), stages.Select(s => s.Name).ToList());
        if (!_controls.TryAdd(id, control))
        {
            return (false, "The run is already being recovered.");
        }

        _store.Update(id, r =>
        {
            r.Status = RunStatus.Queued;
            r.Error = null;
            r.FinishedAt = null;
        });
        _ = Task.Run(() => ExecuteAsync(id, run.Brief, profile, stages, control, recover: true));
        return (true, "Recovering the run.");
    }

    /* ------------------------------------------------------------ controls */

    public bool Cancel(string id)
    {
        if (!_controls.TryGetValue(id, out var control))
        {
            return false;
        }
        control.Cts.Cancel();
        return true;
    }

    /// <summary>Asks the run to pause before its next stage (an agent in the middle of a stage is not interrupted).</summary>
    public (bool Ok, string Message) Pause(string id)
    {
        if (!_controls.TryGetValue(id, out var control))
        {
            return (false, "The run is not active.");
        }
        lock (control)
        {
            if (control.Waiting is not null)
            {
                return (false, "The run is already waiting for you.");
            }
            control.PauseRequested = true;
        }
        _store.Update(id, r => r.PauseRequested = true);
        _hub.Append(id, EventTypes.Paused, null, "Pause requested: the run will pause before its next stage.");
        return (true, "Pause requested.");
    }

    public (bool Ok, string Message) Resume(string id)
    {
        if (!_controls.TryGetValue(id, out var control))
        {
            return (false, "The run is not active.");
        }
        lock (control)
        {
            control.PauseRequested = false;
            control.Resume?.TrySetResult();
        }
        _store.Update(id, r => r.PauseRequested = false);
        return (true, "Resumed.");
    }

    /// <summary>Adds a note that every later agent prompt in this run will include.</summary>
    public (bool Ok, string Message) AddGuidance(string id, string text)
    {
        if (!_controls.ContainsKey(id))
        {
            return (false, "The run is not active.");
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, "The note is empty.");
        }
        AddGuidanceNote(id, text.Trim());
        return (true, "Note added. It applies to the next agent stage or retry.");
    }

    public (bool Ok, string Message) Decide(string id, DecisionRequest request)
    {
        if (!_controls.TryGetValue(id, out var control))
        {
            return (false, "The run is not active.");
        }

        lock (control)
        {
            if (control.Decision is null || control.Waiting is null)
            {
                return (false, "The run is not waiting for a decision.");
            }

            var action = request.Action?.Trim().ToLowerInvariant();
            switch (action)
            {
                case "approve" when !control.Waiting.CanApprove:
                    return (false, $"Stage '{control.Waiting.Stage}' did not pass, so it cannot be approved. Retry it or stop the run.");
                case "approve":
                case "stop":
                    break;
                case "retry":
                    if (request.RetryFrom is { Length: > 0 } from && !control.StageNames.Contains(from, StringComparer.OrdinalIgnoreCase))
                    {
                        return (false, $"Unknown stage '{from}'.");
                    }
                    if (request.Agent is { Length: > 0 } agent && !_agents.IsKnown(agent))
                    {
                        return (false, $"Unknown agent '{agent}'.");
                    }
                    if (ModelCatalog.Validate(request.Agent is { Length: > 0 } a2 ? a2 : _store.Get(id)?.Agent ?? "", request.Model, request.Effort) is { } problem)
                    {
                        return (false, problem);
                    }
                    break;
                default:
                    return (false, "Action must be approve, retry or stop.");
            }

            control.Decision.TrySetResult(request with { Action = action! });
        }
        return (true, "Decision recorded.");
    }

    /// <summary>Gives the workspace back: branch mode returns the repository to the branch it was on; worktree mode removes the worktree folder.</summary>
    public async Task<(bool Ok, string Message)> ReleaseAsync(string id)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (_controls.ContainsKey(id))
        {
            return (false, "The run is still active. Approve, stop or cancel it first.");
        }
        if (!run.WorkspaceHeld)
        {
            return (false, "This run does not hold the workspace.");
        }

        var entry = _projects.Get(run.Project);
        var state = WorkspaceState.Load(Path.Combine(_runsRoot, id));
        if (entry is null || state is null)
        {
            return (false, "Project profile or workspace state is missing; restore the branch or remove the worktree by hand.");
        }

        try
        {
            await WorkspaceFactory.Create(entry.Profile).ReleaseAsync(state, CancellationToken.None);
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
        var message = state.Mode == "Worktree" ? $"Worktree removed; branch '{state.Branch}' stays." : $"Back on '{state.ReturnTo}'.";
        if (first)
        {
            ReleaseLock(run.Project, id);
            _hub.Append(id, EventTypes.System, null, "Workspace released. " + message);
        }
        return (true, message);
    }

    /* ----------------------------------------------------------- execution */

    private async Task ExecuteAsync(string id, string brief, ProjectProfile profile, IReadOnlyList<IStage> stages, RunControl control, bool recover)
    {
        var runDir = Path.Combine(_runsRoot, id);
        var ct = control.Cts.Token;
        var usesWorkspace = !profile.IsDemo && profile.Workspace != WorkspaceMode.None;
        var sharedFolder = usesWorkspace && profile.Workspace == WorkspaceMode.Branch;

        var lockHeld = false;
        WorkspaceSession? session = null;
        var currentStage = "";
        var status = RunStatus.Done;
        string? error = null;

        try
        {
            if (sharedFolder)
            {
                var gate = LockFor(profile.Name);
                if (_holders.TryGetValue(profile.Name, out var current) && current == id)
                {
                    lockHeld = true; // already ours: re-held after a restart
                }
                else
                {
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
            }

            _store.Update(id, r => r.Status = RunStatus.Running);
            _hub.Append(id, EventTypes.RunStarted, null, recover ? "Run recovered after a restart." : "Run started.");

            if (usesWorkspace)
            {
                var workspace = WorkspaceFactory.Create(profile);
                var state = recover ? WorkspaceState.Load(runDir) : null;
                if (state is null)
                {
                    _hub.Append(id, EventTypes.System, null, "Preparing workspace.");
                    state = await workspace.PrepareAsync(id, _store.Get(id)!.Title, msg => _hub.Append(id, EventTypes.System, null, msg), ct);
                    Directory.CreateDirectory(runDir);
                    state.Save(runDir);
                }
                else
                {
                    _hub.Append(id, EventTypes.System, null, $"Reusing the workspace on branch {state.Branch}.");
                }
                session = new WorkspaceSession(workspace, state);
                _store.Update(id, r =>
                {
                    r.Branch = state.Branch;
                    r.ReturnTo = string.IsNullOrEmpty(state.ReturnTo) ? null : state.ReturnTo;
                    r.WorkspaceMode = state.Mode;
                    r.WorkDir = state.WorkDir;
                    r.WorkspaceHeld = true;
                });
            }

            var attempts = new Dictionary<string, int>();
            string? previousFailure = null;
            var agentName = profile.Agent;
            var index = 0;
            WaitState? pendingWait = null;

            if (recover)
            {
                (index, pendingWait) = FindResumePoint(_store.Get(id)!, stages);
                _store.Update(id, r =>
                {
                    r.ResumeWait = null;
                    for (var i = index; i < r.Stages.Count; i++)
                    {
                        if (i == index && pendingWait is not null)
                        {
                            continue; // keeps the result it had when the question was asked
                        }
                        if (i == index && r.Stages[i].StartedAt is not null)
                        {
                            attempts[r.Stages[i].Name] = 1; // it was cut off: the next run is a retry
                        }
                        r.Stages[i].Status = StageStatus.Pending;
                        r.Stages[i].StartedAt = null;
                        r.Stages[i].FinishedAt = null;
                        r.Stages[i].Summary = null;
                    }
                });
                _hub.Append(id, EventTypes.Decision, index < stages.Count ? stages[index].Name : null,
                    index >= stages.Count ? "Recovered: every stage had already passed."
                    : pendingWait is not null ? $"Recovered: asking again what to do at {stages[index].Name}."
                    : $"Recovered: continuing at {stages[index].Name}.");
            }

            while (index < stages.Count)
            {
                ct.ThrowIfCancellationRequested();
                var stage = stages[index];
                currentStage = stage.Name;
                WaitState wait;

                if (pendingWait is { } carried)
                {
                    pendingWait = null;
                    wait = carried;
                    if (!wait.CanApprove)
                    {
                        previousFailure = wait.Message;
                    }
                }
                else
                {
                    await PauseIfRequestedAsync(id, control, ct);
                    attempts[stage.Name] = attempts.GetValueOrDefault(stage.Name) + 1;

                    _store.Update(id, r =>
                    {
                        r.CurrentStage = stage.Name;
                        var record = r.Stages.First(s => s.Name == stage.Name);
                        record.Status = StageStatus.Running;
                        record.StartedAt = DateTimeOffset.Now;
                        record.FinishedAt = null;
                        record.Summary = null;
                    });
                    _hub.Append(id, EventTypes.StageStarted, stage.Name,
                        attempts[stage.Name] > 1 ? $"Stage {stage.Name} started (attempt {attempts[stage.Name]})." : $"Stage {stage.Name} started.");

                    StageResult result;
                    try
                    {
                        var context = new StageContext
                        {
                            RunId = id,
                            Brief = brief,
                            RunDir = runDir,
                            StageName = stage.Name,
                            Hub = _hub,
                            Profile = profile,
                            UpdateRunAction = mutate => _store.Update(id, mutate),
                            GetRunFunc = () => _store.Get(id),
                            Workspace = session,
                            Agent = profile.IsDemo ? null : CreateStageAgent(id, stage.Name, agentName, profile),
                            Attempt = attempts[stage.Name],
                            Guidance = _store.Get(id)!.Guidance.Select(g => g.Text).ToList(),
                            PreviousFailure = previousFailure,
                        };
                        result = await stage.RunAsync(context, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _hub.Append(id, EventTypes.Error, stage.Name, ex.Message);
                        result = StageResult.Failed(ex.Message);
                    }

                    _store.Update(id, r =>
                    {
                        var record = r.Stages.First(s => s.Name == stage.Name);
                        record.Status = ToStageStatus(result.Outcome);
                        record.FinishedAt = DateTimeOffset.Now;
                        record.Summary = result.Summary;
                    });
                    _hub.Append(id, EventTypes.StageFinished, stage.Name, result.Summary,
                        new() { ["outcome"] = result.Outcome.ToString(), ["attempt"] = attempts[stage.Name] });

                    switch (result.Outcome)
                    {
                        case StageOutcome.Passed when profile.Gates.Contains(stage.Name, StringComparer.OrdinalIgnoreCase):
                            previousFailure = null;
                            wait = new WaitState
                            {
                                Stage = stage.Name,
                                Reason = "Gate",
                                Message = $"{stage.Name} finished. Review it, then approve to continue, or redo it with a note.",
                                CanApprove = true,
                            };
                            break;
                        case StageOutcome.Passed:
                            previousFailure = null;
                            index++;
                            continue;
                        case StageOutcome.NeedsHuman:
                            wait = new WaitState { Stage = stage.Name, Reason = "Review", Message = result.Summary ?? "Waiting for you.", CanApprove = true };
                            break;
                        default:
                            previousFailure = result.Summary;
                            wait = new WaitState
                            {
                                Stage = stage.Name,
                                Reason = result.Outcome == StageOutcome.Blocked ? "Blocked" : "Failed",
                                Message = result.Summary ?? "The stage did not pass.",
                                CanApprove = false,
                            };
                            break;
                    }
                }

                var decision = await WaitForDecisionAsync(id, control, wait, ct);
                _store.Update(id, r =>
                {
                    r.Status = RunStatus.Running;
                    r.Waiting = null;
                    r.CurrentStage = stage.Name;
                });

                if (!string.IsNullOrWhiteSpace(decision.Note))
                {
                    AddGuidanceNote(id, decision.Note.Trim(), stage.Name);
                }

                if (decision.Action == "stop")
                {
                    _hub.Append(id, EventTypes.Decision, stage.Name, "Run stopped by you.");
                    status = RunStatus.Cancelled;
                    error = "Stopped by user.";
                    break;
                }

                if (decision.Action == "approve")
                {
                    _hub.Append(id, EventTypes.Decision, stage.Name, $"Approved {stage.Name}.");
                    previousFailure = null;
                    _store.Update(id, r =>
                    {
                        var record = r.Stages.First(s => s.Name == stage.Name);
                        if (record.Status == StageStatus.NeedsHuman)
                        {
                            record.Status = StageStatus.Passed;
                        }
                    });
                    index++;
                    continue;
                }

                // retry
                if (!string.IsNullOrWhiteSpace(decision.Agent) && !string.Equals(decision.Agent, agentName, StringComparison.OrdinalIgnoreCase))
                {
                    agentName = decision.Agent;
                    _store.Update(id, r => r.Agent = agentName);
                }
                var target = decision.RetryFrom is { Length: > 0 } from
                    ? stages.Select(s => s.Name).ToList().FindIndex(n => string.Equals(n, from, StringComparison.OrdinalIgnoreCase))
                    : index;
                if (target < 0)
                {
                    target = index;
                }
                if (!string.IsNullOrWhiteSpace(decision.Model) || !string.IsNullOrWhiteSpace(decision.Effort))
                {
                    _store.Update(id, r =>
                    {
                        for (var i = target; i < stages.Count; i++)
                        {
                            if (stages[i] is not (SpecStage or ImplementStage or IntegrateStage))
                            {
                                continue;
                            }
                            r.StageAgents[stages[i].Name] = new AgentChoice(agentName, decision.Model, decision.Effort);
                        }
                    });
                }
                _hub.Append(id, EventTypes.Decision, stage.Name,
                    $"Retrying from {stages[target].Name}" + (agentName != profile.Agent ? $" with agent {agentName}." : "."));
                _store.Update(id, r =>
                {
                    for (var i = target; i < r.Stages.Count; i++)
                    {
                        r.Stages[i].Status = StageStatus.Pending;
                        r.Stages[i].StartedAt = null;
                        r.Stages[i].FinishedAt = null;
                        r.Stages[i].Summary = null;
                    }
                });
                index = target;
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
        _controls.TryRemove(id, out _);
        control.Cts.Dispose();
    }

    /// <summary>Where a recovered run continues: the question it was asked, or the first stage that had not passed.</summary>
    private static (int Index, WaitState? Pending) FindResumePoint(Run run, IReadOnlyList<IStage> stages)
    {
        if (run.ResumeWait is { } wait)
        {
            var at = run.Stages.FindIndex(s => string.Equals(s.Name, wait.Stage, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                return (at, wait);
            }
        }

        var first = run.Stages.FindIndex(s => s.Status != StageStatus.Passed);
        return (first < 0 ? stages.Count : first, null);
    }

    private async Task PauseIfRequestedAsync(string id, RunControl control, CancellationToken ct)
    {
        TaskCompletionSource? resume = null;
        lock (control)
        {
            if (control.PauseRequested)
            {
                resume = control.Resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        if (resume is null)
        {
            return;
        }

        _store.Update(id, r => r.Status = RunStatus.Paused);
        _hub.Append(id, EventTypes.Paused, null, "Run paused. Resume it when you are ready.");
        using var registration = ct.Register(() => resume.TrySetCanceled(ct));
        try
        {
            await resume.Task;
        }
        finally
        {
            lock (control)
            {
                control.Resume = null;
            }
        }
        _store.Update(id, r =>
        {
            r.Status = RunStatus.Running;
            r.PauseRequested = false;
        });
        _hub.Append(id, EventTypes.Resumed, null, "Run resumed.");
    }

    private async Task<DecisionRequest> WaitForDecisionAsync(string id, RunControl control, WaitState wait, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<DecisionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (control)
        {
            control.PauseRequested = false;
            control.Decision = decision;
            control.Waiting = wait;
        }

        _store.Update(id, r =>
        {
            r.Status = RunStatus.WaitingForHuman;
            r.Waiting = wait;
            r.PauseRequested = false;
        });
        _hub.Append(id, EventTypes.WaitingForHuman, wait.Stage, $"Waiting for you ({wait.Reason}): {wait.Message}",
            new() { ["reason"] = wait.Reason, ["canApprove"] = wait.CanApprove });

        using var registration = ct.Register(() => decision.TrySetCanceled(ct));
        try
        {
            return await decision.Task;
        }
        finally
        {
            lock (control)
            {
                control.Decision = null;
                control.Waiting = null;
            }
        }
    }

    private void AddGuidanceNote(string id, string text, string? stage = null)
    {
        _store.Update(id, r => r.Guidance.Add(new GuidanceNote { Text = text, Stage = stage ?? r.CurrentStage }));
        _hub.Append(id, EventTypes.Guidance, stage, text);
    }

    /// <summary>
    /// A successful run keeps the workspace so the result can be played. Anything else keeps its partial work as a local wip commit,
    /// gives the workspace back (branch mode: repository restored; worktree mode: folder removed), and frees the project.
    /// </summary>
    private async Task SettleWorkspaceAsync(string id, ProjectProfile profile, string runDir, WorkspaceSession? session, bool lockHeld, RunStatus status)
    {
        if (session is null)
        {
            if (lockHeld)
            {
                ReleaseLock(profile.Name, id);
            }
            return;
        }

        if (status == RunStatus.Done)
        {
            _hub.Append(id, EventTypes.System, null, session.State.Mode == "Worktree"
                ? $"Workspace kept: the change lives in {session.State.WorkDir}. Release it when you are done to remove that folder."
                : $"Workspace kept: the repository is on '{session.State.Branch}'. Release it when you are done to return to '{session.State.ReturnTo}'.");
            return;
        }

        var warning = await session.Workspace.AbortAsync(session.State, id, runDir, CancellationToken.None);
        _hub.Append(id, warning is null ? EventTypes.System : EventTypes.Warning,
            null,
            warning ?? (session.State.Mode == "Worktree"
                ? $"Partial work (if any) kept on local branch '{session.State.Branch}'; the worktree folder was removed."
                : $"Partial work (if any) kept on local branch '{session.State.Branch}'; repository restored to '{session.State.ReturnTo}'."));
        if (warning is not null)
        {
            return; // still holding the workspace: keep the lock so nothing starts on top of it
        }

        _store.Update(id, r => r.WorkspaceHeld = false);
        if (lockHeld)
        {
            ReleaseLock(profile.Name, id);
        }
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
            r.Waiting = null;
            r.PauseRequested = false;
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
