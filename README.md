# MoonWalk

A small orchestrator for agent-driven feature development. MoonWalk runs a pipeline of stages (spec, implement,
verify, playtest, hand-over) with the coding agent of your choice, keeps every run on its own git branch, and gives
you a live dark web UI to watch what each agent is doing and to steer it.

It is project-agnostic: each project is a profile file (repo path, stages, guards, agent). It is not tied to Unity.

> Status: all four planned chunks are built. Agents (Claude, Gemini via `agy`, any CLI such as Muse), project profiles,
> branch or worktree workspaces, the Spec, Implement, Verify and Handover stages, guards, the control surface (gates, pause,
> retry, guidance, stop), recovery of interrupted runs, and a Settings page with agent health all work end to end.

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
6. **Handover**: writes `handover.md` and waits for you. The repository stays on the run branch so you can play it.
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
tokens and cost. On a finished run, **Archive** hides it from the board (nothing on disk changes) and **Delete** removes the run's
folder (events, artifacts) for good; the git branch is never touched. Delete is refused while a run is active or still holds its
workspace.

### Notifications

The bell in the top bar turns on browser notifications (the browser asks for permission once). When the tab is in the background you
are told when a run **needs you** (a gate, a failed or blocked stage, the budget, or the playtest at Handover), **finishes**, **fails**
or is **interrupted**. Clicking the notification opens that run. They work only while a MoonWalk tab is open; there is no phone push.

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

Next (see the improvement plan): a Unity compile check and `.meta` auto-commit, a task queue and agent comparison, and a playtest
stage.
