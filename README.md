# MoonWalk

A small orchestrator for agent-driven feature development. MoonWalk runs a pipeline of stages (spec, implement,
verify, playtest, hand-over) with the coding agent of your choice, keeps every run on its own git branch, and gives
you a live dark web UI to watch what each agent is doing and to steer it.

It is project-agnostic: each project is a profile file (repo path, stages, guards, agent). It is not tied to Unity.

> Status: chunk 2 of 4. Real Claude agent with live streaming, project profiles, in-place branch workspace, the
> Implement stage and guards work end to end. Control surface (gates, pause, retry, guidance) comes next.

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
3. The agent works on that branch. It cannot commit or push.
4. MoonWalk checks what changed against the guards, commits only those files, and keeps the repository on the run
   branch so you can play it. The run page shows **Release workspace**: press it to switch back to the branch you were on.
5. On failure or cancel, partial work is kept as a local `wip(moonwalk)` commit on the run branch, the repository is
   restored, and the project is free again. Nothing is ever pushed or merged.

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
3. Control surface: approval gates, pause/resume, retry, guidance notes, Spec, Verify and Hand-over stages
4. Generic CLI agents (Gemini, Muse, others), agent health checks, worktree workspace mode
