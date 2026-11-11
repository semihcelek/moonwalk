using System.Collections.Concurrent;
using MoonWalk.Agents;
using MoonWalk.Pipeline;
using MoonWalk.Pipeline.Stages;
using MoonWalk.Projects;
using MoonWalk.Workspace;

namespace MoonWalk.Core;

/// <summary>What the person decided while a run was waiting: approve, retry (optionally from an earlier stage, with another agent), or stop.</summary>
/// <summary>What to change on a finished run's branch (<see cref="Note"/>), where to restart (<see cref="From"/>, default Implement) and, optionally, other agent choices and automation.</summary>
public sealed record ReopenRequest(string Note, string? From = null, string? Agent = null, string? Model = null, string? Effort = null, AutoMode? Auto = null);

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
    private readonly ReviewService _review;
    private readonly FeatureStore _features;
    private readonly string _runsRoot;
    private readonly ConcurrentDictionary<string, RunControl> _controls = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _holders = new(StringComparer.OrdinalIgnoreCase);

    public RunEngine(RunStore store, EventHub hub, ProjectRegistry projects, AgentRegistry agents, ReviewService review, FeatureStore features, string runsRoot)
    {
        _review = review;
        _features = features;
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

    public Run Start(string brief, string projectName, IReadOnlyDictionary<string, AgentChoice>? choices = null, AutoMode? auto = null, string? feature = null)
    {
        var (profile, stages, stageAgents) = Prepare(projectName, choices);
        if (!string.IsNullOrWhiteSpace(feature))
        {
            if (profile.IsDemo || profile.Workspace == WorkspaceMode.None)
            {
                throw new ArgumentException("This project has no git workspace, so it cannot use a feature branch.");
            }
            feature = _features.Open(profile.Name, feature).Name; // creates the feature when new; refuses a completed one
        }
        else
        {
            feature = null;
        }
        var now = DateTimeOffset.Now;
        var run = new Run
        {
            Id = $"{now:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x1000, 0xFFFF):x}",
            Project = profile.Name,
            Brief = brief,
            Title = MakeTitle(brief),
            Agent = profile.IsDemo ? null : profile.Agent,
            Feature = feature,
            StageAgents = stageAgents,
            Auto = new AutoMode
            {
                Plan = (auto ?? profile.Auto).Plan,
                Implementation = (auto ?? profile.Auto).Implementation,
                MergeToReview = (auto ?? profile.Auto).MergeToReview,
            },
            CreatedAt = now,
            Stages = stages.Select(s => new StageRecord { Name = s.Name }).ToList(),
        };
        _store.Add(run);
        if (feature is not null)
        {
            _features.AddRun(profile.Name, feature, run.Id);
        }

        var control = new RunControl(new CancellationTokenSource(), stages.Select(s => s.Name).ToList());
        _controls[run.Id] = control;
        _ = Task.Run(() => ExecuteAsync(run.Id, brief, profile, stages, control, recover: false));
        return run;
    }

    /// <summary>Throws an <see cref="ArgumentException"/> saying what is wrong when a run could not start with these choices. Used to check a whole batch up front.</summary>
    public void Validate(string projectName, IReadOnlyDictionary<string, AgentChoice>? choices) => Prepare(projectName, choices);

    /// <summary>The project, its stage list and the validated per-stage choices (only the stages that actually have one).</summary>
    private (ProjectProfile Profile, IReadOnlyList<IStage> Stages, Dictionary<string, AgentChoice> StageAgents) Prepare(
        string projectName, IReadOnlyDictionary<string, AgentChoice>? choices)
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
        return (profile, stages, stageAgents);
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

    /// <summary>
    /// Takes a finished run (Done, Failed or Cancelled) back on its own branch to continue with fix instructions: the same run, the same branch,
    /// a new round. The note becomes guidance, the stages from <see cref="ReopenRequest.From"/> (default Implement) run again, and automation is
    /// off unless the request turns it on, so the result can be reviewed this time. Needs the branch to still exist and, in branch mode, a clean tree.
    /// </summary>
    public async Task<(bool Ok, string Message)> ReopenAsync(string id, ReopenRequest request)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (_controls.ContainsKey(id) || Run.IsActive(run.Status))
        {
            return (false, "The run is still active.");
        }
        if (run.Status == RunStatus.Interrupted)
        {
            return (false, "Recover an interrupted run instead of reopening it.");
        }
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            return (false, "Say what should change; the note is what the agent works from.");
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
        // The profile may have gained or lost a stage since the run started (for example a Verify stage): keep the records of the
        // stages both have, add the new ones as Pending.
        var names = stages.Select(s => s.Name).ToList();
        var target = request.From is { Length: > 0 } from
            ? names.FindIndex(n => string.Equals(n, from, StringComparison.OrdinalIgnoreCase))
            : names.FindIndex(n => string.Equals(n, "Implement", StringComparison.OrdinalIgnoreCase));
        if (target < 0)
        {
            if (request.From is { Length: > 0 })
            {
                return (false, $"The project has no stage '{request.From}'.");
            }
            target = 0;
        }

        var agentName = string.IsNullOrWhiteSpace(request.Agent) ? (run.Agent ?? profile.Agent) : request.Agent;

        if (!profile.IsDemo && profile.Workspace != WorkspaceMode.None)
        {
            var state = WorkspaceState.Load(Path.Combine(_runsRoot, id));
            if (state is null)
            {
                return (false, "This run has no workspace record, so its branch cannot be found.");
            }
            var branch = await Git.RunAsync(profile.RepoPath, CancellationToken.None, "rev-parse", "--verify", "-q", $"refs/heads/{state.Branch}");
            if (!branch.Ok)
            {
                return (false, $"Branch '{state.Branch}' no longer exists, so there is nothing to continue on.");
            }
        }

        var control = new RunControl(new CancellationTokenSource(), names);
        if (!_controls.TryAdd(id, control))
        {
            return (false, "The run is already being reopened.");
        }

        var rounds = run.Rounds + 1;
        _store.Update(id, r =>
        {
            r.Rounds = rounds;
            if (!names.SequenceEqual(r.Stages.Select(s => s.Name)))
            {
                r.Stages = names.Select(n => r.Stages.FirstOrDefault(x => x.Name == n) ?? new StageRecord { Name = n }).ToList();
            }
            r.Status = RunStatus.Queued;
            r.Error = null;
            r.FinishedAt = null;
            r.Waiting = null;
            r.ResumeWait = null;
            r.Archived = false;
            r.Agent = agentName;
            r.Auto = new AutoMode { Plan = request.Auto?.Plan ?? false, Implementation = request.Auto?.Implementation ?? false, MergeToReview = request.Auto?.MergeToReview ?? false };
            r.ReviewMergedAt = null;
            r.ReviewBranch = null;
            r.ReviewCommit = null;
            if (!string.IsNullOrWhiteSpace(request.Model) || !string.IsNullOrWhiteSpace(request.Effort) || !string.Equals(agentName, profile.Agent, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = target; i < stages.Count; i++)
                {
                    if (stages[i] is SpecStage or ImplementStage or IntegrateStage)
                    {
                        r.StageAgents[stages[i].Name] = new AgentChoice(agentName, request.Model, request.Effort);
                    }
                }
            }
            for (var i = target; i < r.Stages.Count; i++)
            {
                r.Stages[i].Status = StageStatus.Pending;
                r.Stages[i].StartedAt = null;
                r.Stages[i].FinishedAt = null;
                r.Stages[i].Summary = null;
            }
        });
        _hub.Append(id, EventTypes.Decision, names[target], $"Reopened for follow-up round {rounds}, continuing from {names[target]} on the same branch.");
        AddGuidanceNote(id, request.Note.Trim(), names[target]);
        _ = Task.Run(() => ExecuteAsync(id, run.Brief, profile, stages, control, recover: false, reopenFrom: target));
        return (true, $"Continuing on the run's branch from {names[target]}.");
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

    /// <summary>Hides a finished run from the board (or shows it again). Nothing on disk changes.</summary>
    public (bool Ok, string Message) Archive(string id, bool archived)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (_controls.ContainsKey(id) || Run.IsActive(run.Status))
        {
            return (false, "The run is still active.");
        }
        _store.Update(id, r => r.Archived = archived);
        return (true, archived ? "Archived." : "Restored.");
    }

    /// <summary>
    /// Ends a feature: no more runs can be added to it. Refused while one of its runs is active or still holds the workspace. The branch stays
    /// (review it, merge it, delete it yourself). <paramref name="complete"/> false reopens it.
    /// </summary>
    public (bool Ok, string Message) SetFeatureComplete(string project, string name, bool complete)
    {
        var feature = _features.Get(project, name);
        if (feature is null)
        {
            return (false, "Feature not found.");
        }
        if (complete)
        {
            var busy = feature.Runs.Select(_store.Get).FirstOrDefault(r => r is not null && (Run.IsActive(r.Status) || r.WorkspaceHeld));
            if (busy is not null)
            {
                return (false, $"Run {busy.Id} of this feature is still active or holds the workspace. Finish or release it first.");
            }
        }
        _features.SetStatus(project, name, complete);
        return (true, complete
            ? $"Feature '{feature.Name}' is complete. Its branch '{feature.Branch}' stays for review."
            : $"Feature '{feature.Name}' is open again.");
    }

    /// <summary>
    /// Permanently removes a finished run and its folder (events, artifacts). Refused while the run is active or still holds its
    /// workspace. The run's git branch is not touched.
    /// </summary>
    public (bool Ok, string Message) Delete(string id)
    {
        var run = _store.Get(id);
        if (run is null)
        {
            return (false, "Run not found.");
        }
        if (_controls.ContainsKey(id) || Run.IsActive(run.Status))
        {
            return (false, "The run is still active. Cancel or stop it first.");
        }
        if (run.WorkspaceHeld)
        {
            return (false, "The run still holds its workspace. Release it first.");
        }

        _store.Remove(id);
        _hub.Close(id);
        try
        {
            var dir = Path.Combine(_runsRoot, id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, $"Removed from the list, but its folder could not be deleted: {ex.Message}");
        }
        return (true, "Deleted.");
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

    private async Task ExecuteAsync(string id, string brief, ProjectProfile profile, IReadOnlyList<IStage> stages, RunControl control, bool recover, int? reopenFrom = null)
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
            _hub.Append(id, EventTypes.RunStarted, null, recover ? "Run recovered after a restart." : reopenFrom is not null ? "Run reopened." : "Run started.");

            if (usesWorkspace)
            {
                var workspace = WorkspaceFactory.Create(profile);
                var state = recover || reopenFrom is not null ? WorkspaceState.Load(runDir) : null;
                if (state is null)
                {
                    _hub.Append(id, EventTypes.System, null, "Preparing workspace.");
                    var featureBranch = _store.Get(id)!.Feature is { } featureName ? FeatureStore.BranchFor(featureName) : null;
                    state = await workspace.PrepareAsync(id, _store.Get(id)!.Title, msg => _hub.Append(id, EventTypes.System, null, msg), ct, featureBranch);
                    Directory.CreateDirectory(runDir);
                    state.Save(runDir);
                }
                else if (reopenFrom is not null)
                {
                    // Still held (never released): just make sure the branch is checked out. Otherwise take the branch back into use.
                    if (_store.Get(id)!.WorkspaceHeld)
                    {
                        await workspace.EnsureReadyAsync(state, ct);
                    }
                    else
                    {
                        await workspace.ReopenAsync(state, msg => _hub.Append(id, EventTypes.System, null, msg), ct);
                        state.Save(runDir);
                    }
                    _hub.Append(id, EventTypes.System, null, $"Continuing on branch {state.Branch}.");
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
            var budgetWaived = false;
            var agentName = reopenFrom is not null ? (_store.Get(id)!.Agent ?? profile.Agent) : profile.Agent;
            var index = reopenFrom ?? 0;
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
                            RemainingBudgetUsd = budgetWaived ? null : RemainingBudgetUsd(id, profile),
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
                        case StageOutcome.Passed when !budgetWaived && index + 1 < stages.Count && BudgetProblem(id, profile) is { } overBudget:
                            // A limit was passed: ask before spending more. Approve continues (and stops asking), retry or stop as usual.
                            previousFailure = null;
                            wait = new WaitState { Stage = stage.Name, Reason = "Budget", Message = overBudget + " Approve to continue anyway, or stop the run.", CanApprove = true };
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

                // Auto mode answers the question itself; otherwise the run waits for a person.
                string? autoFailure = null;
                DecisionRequest decision;
                var (autoDecision, autoReason) = AutoDecision(_store.Get(id)!.Auto, wait);
                if (autoDecision is not null)
                {
                    decision = autoDecision;
                    autoFailure = autoReason;
                    _hub.Append(id, EventTypes.Decision, stage.Name, autoDecision.Action == "approve"
                        ? $"Auto mode: approved {stage.Name} ({wait.Reason})."
                        : $"Auto mode: stopping the run instead of waiting. {wait.Message}");
                }
                else
                {
                    decision = await WaitForDecisionAsync(id, control, wait, ct);
                }
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
                    if (autoFailure is null)
                    {
                        _hub.Append(id, EventTypes.Decision, stage.Name, "Run stopped by you.");
                    }
                    status = autoFailure is null ? RunStatus.Cancelled : RunStatus.Failed;
                    error = autoFailure ?? "Stopped by user.";
                    break;
                }

                if (decision.Action == "approve")
                {
                    _hub.Append(id, EventTypes.Decision, stage.Name, wait.Reason == "Budget" ? "Continuing past the budget." : $"Approved {stage.Name}.");
                    previousFailure = null;
                    if (wait.Reason == "Budget")
                    {
                        budgetWaived = true;
                    }
                    _store.Update(id, r =>
                    {
                        var record = r.Stages.First(s => s.Name == stage.Name);
                        if (record.Status == StageStatus.NeedsHuman)
                        {
                            record.Status = StageStatus.Passed;
                        }
                        if (autoDecision is not null && wait.Reason is "Review" or "Gate")
                        {
                            // Say so in the summary: "Ready for your playtest, approve when it plays well" is wrong once auto mode approved it.
                            record.Summary = wait.Reason == "Review"
                                ? "Auto-approved; nobody played this result."
                                : "Auto-approved. " + record.Summary;
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

        // Unattended runs hand the workspace back, so the next run on the project can start.
        if (status == RunStatus.Done && _store.Get(id) is { Auto.Implementation: true, WorkspaceHeld: true })
        {
            var (released, message) = await ReleaseAsync(id);
            if (!released)
            {
                _hub.Append(id, EventTypes.Warning, null, $"Auto mode could not release the workspace: {message}");
            }
        }

        // Optionally collect the result in the review branch. A conflict leaves the run unmerged and says why.
        if (status == RunStatus.Done && _store.Get(id) is { Auto.MergeToReview: true })
        {
            var merged = await _review.MergeRunAsync(id, CancellationToken.None);
            _hub.Append(id, merged.Ok ? EventTypes.System : EventTypes.Warning, null,
                merged.Ok ? $"Auto mode: {merged.Message}" : $"Auto mode could not merge into the review branch: {merged.Message}");
        }
    }

    /// <summary>
    /// What a run may answer by itself. Gates are the plan; Review is the implementation. A stage that failed or was blocked, or a passed
    /// budget, is only answered (by stopping the run) when everything is automatic, so a half-automatic run still waits for you there.
    /// </summary>
    private static (DecisionRequest? Decision, string? Failure) AutoDecision(AutoMode auto, WaitState wait)
    {
        if (wait.Reason == "Gate" && auto.Plan)
        {
            return (new DecisionRequest("approve"), null);
        }
        if (wait.Reason == "Review" && auto.Implementation)
        {
            return (new DecisionRequest("approve"), null);
        }
        if (auto.Full && wait.Reason is "Failed" or "Blocked" or "Budget")
        {
            return (new DecisionRequest("stop"), $"{wait.Stage} {wait.Reason.ToLowerInvariant()}: {wait.Message} (full auto mode stops instead of waiting)");
        }
        return (null, null);
    }

    /// <summary>Dollars left under the project's cost limit, for agents that can enforce a per-run cap (Claude). Null when there is no limit.</summary>
    private double? RemainingBudgetUsd(string id, ProjectProfile profile)
    {
        if (profile.Budget.MaxCostUsd is not { } max || max <= 0)
        {
            return null;
        }
        var spent = _store.Get(id)?.Usage.Sum(u => u.CostUsd ?? 0) ?? 0;
        return Math.Max(max - spent, 0.01);
    }

    private static string Usd(double amount) => amount >= 1 ? amount.ToString("0.00") : amount.ToString("0.0000").TrimEnd('0').PadRight(6, '0');

    /// <summary>Returns what was exceeded when the run has used up a limit of the project's budget, or null.</summary>
    private string? BudgetProblem(string id, ProjectProfile profile)
    {
        var usage = _store.Get(id)?.Usage;
        if (usage is null || usage.Count == 0)
        {
            return null;
        }
        var spent = usage.Sum(u => u.CostUsd ?? 0);
        var tokens = usage.Sum(u => u.Tokens ?? 0);
        if (profile.Budget.MaxCostUsd is > 0 and var maxCost && spent >= maxCost)
        {
            return $"The run has spent ${Usd(spent)} of its ${Usd(maxCost)} budget.";
        }
        if (profile.Budget.MaxTokens is > 0 and var maxTokens && tokens >= maxTokens)
        {
            return $"The run has used {tokens:N0} of its {maxTokens:N0} token budget.";
        }
        return null;
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

    /// <summary>The run that holds a branch-mode project's workspace right now (a new run on that project would wait for it), or null.</summary>
    public (string Id, string Title)? HolderOf(string project) =>
        _holders.TryGetValue(project, out var id) && _store.Get(id) is { } run ? (id, run.Title) : null;

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
