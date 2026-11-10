# MoonWalk

A small orchestrator for agent-driven feature development. MoonWalk runs a pipeline of stages (spec, implement,
verify, playtest, hand-over) with the coding agent of your choice, keeps every run on its own git branch, and gives
you a live dark web UI to watch what each agent is doing and to steer it.

It is project-agnostic: each project is a profile file (repo path, stages, guards, agents). It is not tied to Unity.

> Status: chunk 1 of 4. The live UI, run engine and event log work end to end with scripted **demo** stages. Real
> agents, project profiles and the control surface (gates, pause, retry, guidance) come in the next chunks.

## Run it

```powershell
dotnet run --project src/MoonWalk
```

Open <http://localhost:5870>, press **New run**, and watch the demo pipeline stream live.

Run data is written to `runs/<id>/` (`run.json` and `events.jsonl`) under the folder you start MoonWalk from.
Override with `--MoonWalk:DataDir=<path>`; override the address with `--urls=http://localhost:5999`.

## Layout

| Path | What |
|---|---|
| `src/MoonWalk/Core` | Run model, append-only event log + live fan-out, run store, run engine |
| `src/MoonWalk/Pipeline` | `IStage` contract, stages, pipelines |
| `src/MoonWalk/Web` | HTTP API and server-sent-event streams |
| `src/MoonWalk/wwwroot` | The web UI (vanilla JS and CSS, no build step) |
| `docs/architecture.md` | How it fits together |

## Roadmap

1. Skeleton and live UI (done)
2. Real agent adapter (Claude), project profiles, branch workspace, Implement stage, guards
3. Control surface: approval gates, pause/resume/cancel, retry, guidance notes, Verify and Hand-over
4. Generic CLI agents (Gemini, Muse, others), agent health checks, resume on restart, worktree workspace mode
