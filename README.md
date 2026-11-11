# MoonWalk

A small orchestrator for agent-driven feature development. MoonWalk runs a pipeline of stages (spec, implement,
verify, playtest, hand-over) with the coding agent of your choice, keeps every run on its own git branch, and gives
you a live dark web UI to watch what each agent is doing and to steer it.

It is project-agnostic: each project is a profile file (repo path, stages, guards, agent). It is not tied to Unity.

> Status: all four planned chunks are built. Agents (Claude, Gemini via `agy`, any CLI such as Muse), project profiles,
> branch or worktree workspaces, the Spec, Implement, Verify and Handover stages, guards, the control surface (gates, pause,
> retry, guidance, stop), recovery of interrupted runs, and a Settings page with agent health all work end to end.

## Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/download) and `git`.
- Windows is the tested platform (agent processes are tied to MoonWalk through a Windows job object). Other systems are untested.
- For real runs, at least one agent CLI on your `PATH`: `claude`, `agy` or `muse`, or any CLI you describe in `agents.json`. The
  built-in `demo` project needs none.

> **Security:** MoonWalk is a local, single-user tool with no authentication. It listens on `localhost` only and its agents can edit
> the repositories you configure. Do not expose the port. See [SECURITY.md](SECURITY.md).

## Run it

```powershell
dotnet run --project src/MoonWalk
```

Open <http://localhost:5870>, press **New run**, pick a project and describe the change. The built-in `demo` project
streams scripted stages and needs nothing else.

Run data is written to `runs/<id>/` (`run.json`, `events.jsonl`, `workspace.json`) in the MoonWalk repository root. Relative
paths in the settings below resolve against that root (the folder holding `MoonWalk.slnx`), so it does not matter where you
start it from; absolute paths are used as given. Settings can be given as arguments or environment variables:

| Setting | Argument | Environment variable | Default |
|---|---|---|---|
| Address | `--urls=http://localhost:5999` | `ASPNETCORE_URLS` | `http://localhost:5870` |
| Run data folder | `--MoonWalk:DataDir=<path>` | `MoonWalk__DataDir` | `runs` |
| Project profiles | `--MoonWalk:ProjectsDir=<path>` | `MoonWalk__ProjectsDir` | `projects` |
| Extra agents | `--MoonWalk:AgentsFile=<path>` | `MoonWalk__AgentsFile` | `agents.json` |

## Add a project

1. Copy `projects/example.json.sample` to `projects/<name>.json` (a `*.local.json` name is git-ignored).
2. Set `repoPath` and `baseBranch`; adjust `guards` and `dirtyPolicy`.
3. Restart MoonWalk, or `POST /api/projects/reload`. A profile with problems stays listed with its errors.

Needs `git` on the PATH and the CLI of whichever agent the project uses. The **Settings** page shows which agents are
installed and which projects have problems.

## Agents

| Agent | What it is | What you see live |
|---|---|---|
| `claude` | Claude Code CLI (built in) | Streamed text, every tool call and result, cost |
| `agy` | Gemini through the Antigravity CLI (built in) | Streamed text, every tool call and result, token usage |
| `muse` | Muse Code by Meta (built in) | Streamed text, every tool call and result (with file edits), token usage |
| any name in `agents.json` | A command line tool that takes a prompt and edits files (see `agents.json.sample`) | Its output, line by line |

Notes:
- **Muse** runs as `muse exec --json` with its own approval and sandbox left on. The shell is off by default
  (`muse.allowShell` opts in), and the Spec stage adds `--disable-write --disable-shell`. The workspace's own Muse rules and
  skills are not loaded unless `muse.trustWorkspace` is true. Muse takes about a minute for a one-line change.
- **agy runs headless**, where it cannot ask for permission, and a denied shell command ends its session. By default MoonWalk
  allows none and tells it to use file tools only; `agy.allowedCommands` in the profile opts in (`["*"]` also lets it run
  `git commit` and `git push`, so only use that if you accept it).
- Only Claude can be held to a tool list. For the others MoonWalk asks for read-only in the Spec stage (agy plan mode, Muse
  `--disable-write`) and then **verifies** that no file changed.
- A retry can switch agent ("retry from Implement with agy"), so you can compare agents on the same run.

## How a run works (branch workspace)

1. The run waits if another run holds the project's working folder.
2. MoonWalk checks the tree is clean (or records a baseline), fast-forwards the base branch, and creates
   `moonwalk/<run>-<slug>` **inside your normal working folder**, so Unity does not reimport anything.
3. **Spec** (optional): a read-only agent writes acceptance criteria to `spec.md`. A gate after it lets you review.
4. **Implement**: the agent works on the branch and cannot commit or push. MoonWalk checks what changed against the
   guards and commits only those files.
5. **Verify**: runs your commands (compile, lint, tests) and saves the output as evidence.
6. **Handover**: writes `handover.md` and waits for you. The repository stays on the run branch so you can play it. In a Unity
   project it first commits the `.meta` files the Editor created during the run (they hold the asset GUIDs that scenes refer to) and
   lists new files that still have none; open the project and **Retry** from Handover to commit those. `commitMetaFiles: false` in
   the profile switches this off.
7. You **approve**, or **retry** from any stage with a note ("jump feels floaty"), or **stop**. After approving, press
   **Release workspace** to switch back to the branch you were on.
8. On failure or stop, partial work is kept as a local `wip(moonwalk)` commit on the run branch, the repository is
   restored, and the project is free again. Nothing is ever pushed or merged.

### Choosing the model per stage

In **New run**, the *Agent per stage* section lets each agent stage use its own agent, model and effort, so you can plan with a
strong model and implement with a cheaper one. Blank means the project's default.

| Stage | What it is |
|---|---|
| Plan (`Spec`) | Writes the acceptance criteria |
| `Implement` | Changes the code |
| Unity integrate (`Integrate`) | An agent with the unity-mcp tools wires the scene, checks the Console for `error CS` and reports |

| Agent | Models offered | Effort |
|---|---|---|
| `claude` | Opus, Sonnet, Haiku | `low` to `max` (Haiku has none) |
| `agy` | Gemini 4 Argon, Gemini 3.8 Flash, Claude Opus/Sonnet 5.5, GPT-OSS 120B | Part of the model name (Low, Medium, High) |
| `muse` | Wonder (plans big), Spark (medium), Ember (bounded steps) | `none` to `ultra` |

Examples: Claude Opus (high) for the plan and Claude Sonnet or Haiku for the implementation; agy Gemini 4 Argon (Medium) to plan
and Gemini 3.8 Flash (High) for the Unity step; Muse Wonder or Spark to plan and Ember to implement. Efforts are only offered
where the agent's CLI accepts them, and a choice that cannot work is refused when the run starts. A **retry** can change agent,
model and effort too. The Integrate stage needs the Unity Editor open with unity-mcp connected; for Claude it is allowed the
`mcp__unityMCP__*` tools, and agy and Muse need unity-mcp in their own configuration. Add `"Integrate"` to the profile's `stages`
(after `Implement`) to use it.

### Seeing what a run did

- **Changes tab:** the run's git diff against its base branch, with a file list and a colored patch. It keeps working after you
  release the workspace, because the branch stays in the repository.
- **Image tabs:** any screenshot a stage saves under the run (`screenshots/...png`) opens in its own tab.
- **Attempts:** the live feed groups each stage attempt in a collapsible section; when a retry starts, the earlier attempt of that
  stage folds away. Each header shows the outcome (Passed, Failed, NeedsHuman).

### Finding old runs

The board has a search box (title, id, project, brief), a status filter, a project filter and *Show archived*. Each row shows its
tokens and cost, a stage bar, its branch, and one line saying why it waits or failed. Runs that need you are pinned in a **Needs you**
section at the top. The **Flat / By project** switch groups the board into collapsible project sections (with counts, a "need you"
badge and spend); the choice is remembered.

Finished runs have an **Archive** button on the row (it appears on hover) and a checkbox. **Select finished** or tick several runs to
archive or restore them together; a toast offers **Undo**. When Done runs older than 7 days are on the board, **Archive Done older
than 7 days (N)** clears them in one click. Archiving hides a run from the board (nothing on disk changes). **Delete**,
on the run page, removes the run's folder (events, artifacts) for good; the git branch is never touched. Delete is refused while a
run is active or still holds its workspace.

Runs that came from the same queue or comparison fold into one block (**Queue: name, 2/5 done**, with badges for runs that need you,
are active or failed). Click it to open the rows, **Archive N** archives its finished runs, **Open** goes to the batch page. A block
with a run waiting for you stays open.

Keyboard on the board: `j` / `k` move the highlighted row, `Enter` opens it, `e` archives or restores it, `x` selects it, `/` jumps to
search (`Esc` leaves it) and `n` opens the New run dialog.

### Notifications

The bell in the top bar turns on browser notifications (the browser asks for permission once). When the tab is in the background you
are told when a run **needs you** (a gate, a failed or blocked stage, the budget, or the playtest at Handover), **finishes**, **fails**
or is **interrupted**. Clicking the notification opens that run. They work only while a MoonWalk tab is open; there is no phone push.

### Running without you (auto mode)

The New run dialog has two checkboxes under **Automation**; a project profile can set the defaults with `"auto": { "plan": true,
"implementation": true }`.

| Setting | What the run does by itself |
|---|---|
| Auto-accept the plan | Approves the gates after a stage (normally Spec), so it goes straight on to Implement |
| Auto-accept the implementation | Approves the final review (Handover), finishes as Done, **then releases the workspace**, so the next run on the project can start |
| Both (full auto) | Also **stops the run as Failed** when a stage fails or is blocked, or a budget is passed, instead of waiting for you |

With only one box ticked, failures still wait for you. Full auto never retries on its own and never merges or pushes: the result is a
branch left in the repository for you to look at later. The run page shows a *full auto* / *auto plan* / *auto implementation* tag.

### Queue and compare

The **New run** dialog has a switch at the top:

- **Queue several**: paste several briefs separated by a line containing `---`. MoonWalk starts them one after another (the stage
  choices and automation you set apply to all). Tick *Stop the queue at the first run that does not finish* to skip the rest after
  a failure. Use full auto and the whole list runs with nobody there; without auto, each run waits for you as usual and the queue
  waits with it.
- **Compare agents**: one brief, and you tick two or more variants (the built-in presets, your saved ones, or *Project defaults*).
  Each variant runs the brief with its own agent, model and effort for the plan and the implementation. On a branch-mode project they
  run one at a time; on a **worktree-mode** project all of them run at once. The batch page is a table of agents and models, status,
  time, tokens, cost and how many files and lines each variant changed, with the cheapest and the fastest highlighted; **open** goes to
  the run and its Changes tab.

The whole batch is checked before anything starts (unknown stage, agent or model is refused with the reason), and recent batches
show as chips on the board. **Cancel batch** cancels the run in progress and skips the rest. If MoonWalk stops mid-batch, the batch
comes back as Interrupted and its remaining runs are not started; the runs already started are recovered like any other.

### Testing several results together: the review branch

Every run is on its own branch, so testing three results would mean three checkouts. Instead, on a finished (Done) run press **Merge into
review branch**: MoonWalk merges that run's branch into the project's `waiting-review` branch (the profile's `reviewBranch`, created from the
base branch the first time). Then check out `waiting-review` once and test everything together.

- **Your working folder is not touched by a merge**, so it is safe while a run holds the repository on its own branch. (If `waiting-review`
  is the branch checked out right now, a normal merge is used instead, and only when there are no uncommitted changes.)
- **Conflicts change nothing.** If a run's changes clash with what is already in the review branch, you get the list of files and the
  branch stays as it was. Merge the other run first, or test them separately.
- A merged run shows an *in waiting-review* tag. Merging it again says it is already there.
- **Settings > Review branch** shows what is in it, with **Check out for testing** (refused while a run is using the repository) and
  **Reset to base** (starts a new round: the branch goes back to the base, the runs' own branches stay; refused while it is checked out).
- For unattended batches, tick **Merge each finished run into the review branch** (or set `auto.mergeToReview`): every run that finishes Done
  is merged as it completes, and one that conflicts stays unmerged with a warning in its feed.

### Delegating with your main agents

**Settings > For your main agents > Copy the guide** (also served at `/api/guide`, or `/api/guide?project=<name>` for one project) gives
your main agent everything it needs to turn a goal into briefs and queue them: how a run works, how to write a brief the run agents can
finish alone (short, one concern, outcome not steps, independent of the other briefs), the queue format (`---` between briefs) and the
`POST /api/batches` request, which models and efforts exist, what each auto setting does, and how to read results. It is filled with this
installation's real project, agent and model names, plus the project's `briefingNotes` (facts for brief writers) and the rules its run agents
already follow. The main agent answers with the split, a `moonwalk-queue` block and the request body, and waits for you to say go. The
template is `docs/delegating-to-moonwalk.md`.

### Brief templates

Above the brief box, **Save as template** keeps the current brief under a name (a markdown file in `briefs/`, git-ignored) and the
dropdown fills the box from a saved one. A template is plain text, so it can say "read `docs/plans/impact-juice.md` first" and the
agent reads that file in the repository. Names use letters, digits, spaces, dashes and underscores.

### Cleaning up branches

Every run leaves a `moonwalk/<run>-<title>` branch. **Settings > Branches left by runs** lists them for a project with whether each is
merged into the base branch, how many commits it is ahead, and when it was last committed. **Delete** removes one; an unmerged branch asks
you to confirm because its commits would be lost. **Delete merged** removes every merged branch that is not in use, and **Prune worktrees**
clears git's records of worktree folders that no longer exist. A branch is never deleted while it is checked out in the repository, checked
out in a worktree, or belongs to a run that is still active or still holds the workspace. Only `moonwalk/*` branches can be deleted here.

### Presets

The *Agent per stage* section has a **Preset** dropdown. Built in: Claude Opus plans and Sonnet implements, Claude Sonnet plans and
Haiku implements, Muse Wonder or Spark plans and Ember implements, Gemini Argon plans and Flash implements. **Save as preset** stores
the current choices under your own name (kept in `presets.json`, git-ignored); a preset only fills the stages the chosen project has.

### What a run costs

Every agent attempt adds a line to the run's usage ledger: tokens, the model it actually used, and dollars **where the agent reports
them** (only Claude does; agy and Muse report tokens). The run page shows totals plus a line under each stage, and the board shows
today, the last 7 days, and the split by model. MoonWalk does not guess prices for agents that do not report them.

A project can set a budget in its profile: `"budget": { "maxCostUsd": 0.50, "maxTokens": 2000000 }`. After each agent stage, if a
limit has been passed the run waits (**Budget reached**): approve to continue anyway (it then stops asking), retry, or stop. Claude is
also given the remaining dollars as `--max-budget-usd`, so a single Claude stage stops itself, though the CLI checks between turns and
can overshoot by one turn. Muse has `muse.maxSteps` (a step cap) and agy only a time limit, so one stage of theirs can pass a limit
before the check.

### Branch or worktree

| | `branch` (default) | `worktree` |
|---|---|---|
| Where | Your own project folder, on a run branch | A separate folder per run (`wt-moonwalk/<run>` beside the repo) |
| Unity | Nothing to reimport | A new folder is imported before you can open it |
| Parallel runs | One per project, the rest queue | As many as you like |
| Your folder | Switched to the run branch until you release | Never touched |
| Release | Back to your previous branch | Removes the folder; the branch stays |

### If MoonWalk stops mid-run

Unfinished runs come back as **Interrupted** and still own their workspace. **Recover run** continues where it stopped:
if it was waiting for you, it asks the same question again; if an agent was cut off, that stage runs again as a retry
and the agent is told to check what is already there. **Release workspace** is also available instead.
Agent processes are tied to MoonWalk (a Windows job object), so killing MoonWalk also stops the agents it started.

### What MoonWalk checks after an agent runs

MoonWalk commits the agent's work itself, after the guards. If an agent commits on its own, MoonWalk reports it and checks those
commits against the same guards; if it **pushes** the run branch, the stage is blocked. Nothing in a run is ever pushed by MoonWalk.

## Steering a run

| You want to | Do this |
|---|---|
| Review before the next stage | List it in the profile's `gates`; the run waits at **Needs you** |
| Tell the agents something | **Add note** on the run page; every later agent prompt includes it |
| Redo a stage differently | **Retry from** any stage, with an optional note and agent; the failure reason is passed to the agent |
| Stop between stages | **Pause** (takes effect before the next stage), **Resume** later |
| Abort | **Cancel run** (kills the agent process tree) or **Stop run** at a decision |
| See what it did | Live feed, plus tabs for `spec.md`, `verify.log` and `handover.md` |

The browser tab title shows `(n)` while runs wait for you, and those rows are highlighted on the board.

## Layout

| Path | What |
|---|---|
| `src/MoonWalk/Core` | Run model, append-only event log + live fan-out, run store, run engine |
| `src/MoonWalk/Pipeline` | `IStage` contract, stages, pipelines |
| `src/MoonWalk/Agents` | Agent interface, process runner, Claude stream parser and adapter |
| `src/MoonWalk/Projects` | Project profiles, registry, guards |
| `src/MoonWalk/Workspace` | git wrapper and the branch workspace |
| `src/MoonWalk/Web` | HTTP API and server-sent-event streams |
| `src/MoonWalk/wwwroot` | The web UI (vanilla JS and CSS, no build step) |
| `docs/architecture.md` | How it fits together |

## Roadmap

1. Skeleton and live UI (done)
2. Claude agent, project profiles, branch workspace, Implement stage, guards (done)
3. Control surface: gates, pause/resume, retry, guidance notes, Spec, Verify and Handover stages (done)
4. agy and generic CLI agents, agent health and Settings page, recovery of interrupted runs, worktree mode (done)

Next (see the improvement plan): a Unity compile check and `.meta` auto-commit, and a playtest stage.

## License

MIT. See [LICENSE](LICENSE).
