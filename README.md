# MoonWalk

A small orchestrator for agent-driven feature development. MoonWalk runs a pipeline of stages (spec, implement,
verify, playtest, hand-over) with the coding agent of your choice, keeps every run on its own git branch, and gives
you a live dark web UI to watch what each agent is doing and to steer it.

It is project-agnostic: each project is a profile file (repo path, stages, guards, agent). It is not tied to Unity.

> Status: chunk 3 of 4. Real Claude agent with live streaming, project profiles, in-place branch workspace, the
> Spec, Implement, Verify and Handover stages, guards, and the control surface (gates, pause, retry, guidance, stop)
> work end to end. Other agents (Gemini, Muse, ...) and worktree mode come next.

## Run it

```powershell
dotnet run --project src/MoonWalk
```

Open <http://localhost:5870>, press **New run**, pick a project and describe the change. The built-in `demo` project
streams scripted stages and needs nothing else.

Run data is written to `runs/<id>/` (`run.json`, `events.jsonl`, `workspace.json`) under the folder you start MoonWalk
from. Settings can be given as arguments or environment variables:

| Setting | Argument | Environment variable | Default |
|---|---|---|---|
| Address | `--urls=http://localhost:5999` | `ASPNETCORE_URLS` | `http://localhost:5870` |
| Run data folder | `--MoonWalk:DataDir=<path>` | `MoonWalk__DataDir` | `runs` |
| Project profiles | `--MoonWalk:ProjectsDir=<path>` | `MoonWalk__ProjectsDir` | `projects` |

## Add a project

1. Copy `projects/example.json.sample` to `projects/<name>.json` (a `*.local.json` name is git-ignored).
2. Set `repoPath` and `baseBranch`; adjust `guards` and `dirtyPolicy`.
3. Restart MoonWalk, or `POST /api/projects/reload`. A profile with problems stays listed with its errors.

Needs the `claude` CLI installed and logged in (agent `claude`) and `git` on the PATH.

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
4. Generic CLI agents (Gemini, Muse, others), agent health checks, resume of interrupted runs, worktree workspace mode
