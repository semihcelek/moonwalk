# MoonWalk architecture

## Principles

- **The event log is the source of truth.** Everything that happens in a run is appended to `runs/<id>/events.jsonl`.
  The UI replays that file and then tails new events, so a refresh, a second browser or a crash never loses history.
- **Orchestrator owns git.** Agents never commit or push. A run never auto-merges. (Workspace code arrives in chunk 2.)
- **Agents are black boxes.** Prompt in, events out, exit code, summary file.
- **Simplest thing that works.** One project, files instead of a database, no plugin system, no DI beyond the default.

## Run folder

```
runs/<id>/
  run.json        current snapshot of the run (status, stages), rewritten atomically on every change
  events.jsonl    one JSON event per line, `seq` increasing from 1
```

## Core types (`src/MoonWalk/Core`)

| Type | Role |
|---|---|
| `Run`, `StageRecord` | Persistent run model. `RunStatus`: Queued, Running, WaitingForHuman, Paused, Done, Failed, Cancelled, Interrupted |
| `RunEvent`, `EventTypes` | One log entry: `seq`, `time`, `type`, `stage`, `text`, optional `data` |
| `EventHub` | Per-run append-only log with live fan-out. `OpenStream(runId, afterSeq)` subscribes and reads the backlog under one lock, so a client gets no gaps and no duplicates |
| `Fanout<T>` | Publish to all current subscribers through unbounded channels |
| `RunStore` | In-memory runs, persisted to `run.json`, publishes every change as JSON. On startup, runs left `Queued` or `Running` become `Interrupted` |
| `RunEngine` | Runs a pipeline in the background: sets stage state, emits events, handles cancel and failure |

## Pipeline (`src/MoonWalk/Pipeline`)

A stage implements `IStage` (`Name`, `RunAsync(StageContext, CancellationToken)`) and returns a `StageResult` with one of
`Passed`, `Failed`, `NeedsHuman`, `Blocked`. `StageContext.Emit` writes events tagged with the stage name.

`DemoStage` is a scripted stand-in that streams text deltas and tool calls so the UI can be exercised without an agent.
Pipelines are plain ordered lists (`DemoPipeline`); per-project pipelines arrive with project profiles in chunk 2.

`NeedsHuman` currently ends the run in `WaitingForHuman`. Resuming from a gate is part of chunk 3.

## Event types

| Type | UI group | Notes |
|---|---|---|
| `AgentTextDelta` | Agent text | Streamed fragment. The UI merges consecutive deltas of one stage into a single row with a live caret |
| `AgentText` | Agent text | Closes a streamed block, or a whole line |
| `AgentToolUse`, `AgentToolResult` | Tools | `text` is the tool name or result summary, `data` is shown in a collapsible block |
| `RunStarted`, `RunFinished`, `StageStarted`, `StageFinished`, `System`, `Warning`, `Error` | System | |

## HTTP API (`src/MoonWalk/Web`)

| Route | Purpose |
|---|---|
| `GET /api/runs`, `GET /api/runs/{id}` | Run snapshots |
| `POST /api/runs` `{ brief, project }` | Start a run |
| `POST /api/runs/{id}/cancel` | Cancel (kills the stage through its cancellation token) |
| `GET /api/stream` | SSE: `run` events carry a full run snapshot on every change (drives the board) |
| `GET /api/runs/{id}/stream` | SSE: `ev` events carry run events. Resumes from `Last-Event-ID` or `?after=` |

## Web UI (`src/MoonWalk/wwwroot`)

Vanilla ES module, no build step. Hash routing: `#/` is the board, `#/run/<id>` is the run detail with the stage
timeline, the live feed (follow mode, filters by text, tools and system) and the brief. Two EventSource connections:
one global for snapshots, one per open run for events. Colors are CSS tokens on `:root`; the theme is dark only.

## Not built yet

Project profiles, workspace modes (branch in place by default, worktree opt-in), real agent adapters, guards,
approval gates, pause/resume, retry, guidance notes, agent health checks. See the roadmap in `README.md`.
