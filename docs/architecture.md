# MoonWalk architecture

## Principles

- **The event log is the source of truth.** Everything that happens in a run is appended to `runs/<id>/events.jsonl`.
  The UI replays that file and then tails new events, so a refresh, a second browser or a crash never loses history.
- **Orchestrator owns git.** Agents never commit or push. A run never auto-merges or pushes.
- **Agents are black boxes.** Prompt in, events out, exit code, final text.
- **Simplest thing that works.** One project, files instead of a database, no plugin system, no DI beyond the default.

## Run folder

```
runs/<id>/
  run.json        current snapshot of the run (status, stages, branch, commit, cost), rewritten atomically on every change
  events.jsonl    one JSON event per line, `seq` increasing from 1
  workspace.json  branch, the branch to return to, baseline of already-modified files (branch workspace only)
  commit.txt, pathspec.txt   scratch files for the last commit
```

## Core types (`src/MoonWalk/Core`)

| Type | Role |
|---|---|
| `Run`, `StageRecord` | Persistent run model. `RunStatus`: Queued, Running, WaitingForHuman, Paused, Done, Failed, Cancelled, Interrupted. `WorkspaceHeld` is true while the run owns the project's working folder |
| `RunEvent`, `EventTypes` | One log entry: `seq`, `time`, `type`, `stage`, `text`, optional `data` |
| `EventHub` | Per-run append-only log with live fan-out. `OpenStream(runId, afterSeq)` subscribes and reads the backlog under one lock, so a client gets no gaps and no duplicates |
| `Fanout<T>` | Publish to all current subscribers through unbounded channels |
| `RunStore` | In-memory runs, persisted to `run.json`, publishes every change as JSON. On startup, runs left `Queued` or `Running` become `Interrupted` |
| `RunEngine` | Runs a pipeline in the background: workspace lock, prepare, stages, settle, release, cancel |

## Pipeline (`src/MoonWalk/Pipeline`)

A stage implements `IStage` (`Name`, `RunAsync(StageContext, CancellationToken)`) and returns a `StageResult` with one of
`Passed`, `Failed`, `NeedsHuman`, `Blocked`. `StageContext` carries the project profile, the workspace session, the agent,
`Emit` (events tagged with the stage) and `UpdateRun`/`AddCost`/`SetStageAgent`.

`PipelineFactory` turns a profile's `stages` names into stages. Known: `Implement`. The built-in `demo` project uses the
scripted `DemoPipeline` instead. `NeedsHuman` currently ends the run in `WaitingForHuman`; resuming from a gate is chunk 3.

### ImplementStage

1. Build the prompt: the brief, fixed rules (smallest change, no git commit/push/add, final summary, `STATUS: BLOCKED` if it cannot finish), then the profile's `extraInstructions`.
2. Run the agent in the repository folder.
3. Fail on timeout, non-zero exit or `is_error`; `Blocked` when the answer starts with `STATUS: BLOCKED`.
4. Ask the workspace what changed. `Blocked` if the agent edited a file that was already modified before the run (baseline policy); `Failed` if nothing changed.
5. Stage exactly those paths, count changed lines, run `Guards.Check`. Violations fail the stage.
6. Commit only those paths with trailers `MoonWalk-Run`, `MoonWalk-Agent` (and `Co-Authored-By` for Claude).

## Projects (`src/MoonWalk/Projects`)

`ProjectProfile` is read from `projects/*.json` (comments and trailing commas allowed). `ProjectRegistry` validates each
profile (repo exists and is a git repo, known agent and stages, workspace mode supported, unique name) and keeps invalid ones
listed with their errors so the UI can explain them. The built-in `demo` project always exists.

`Guards.Check(changedPaths, changedLines, rules)` is a pure function: max files, max lines, deny globs, optional allow globs.
Globs: `**` crosses folders, `*` and `?` stay in one segment, a trailing `/` means everything below.

## Workspace modes (`src/MoonWalk/Workspace`)

`workspace: "branch"` (default and the only implemented mode): the run works on `moonwalk/<run>-<slug>` inside the
project's normal working folder. For Unity this avoids a second checkout and a full reimport. `"worktree"` (a separate
folder per run, parallel runs) is reserved and rejected by validation until chunk 4.

Because the folder is shared, the engine enforces **one run per project**: later runs stay `Queued` (with a System event
naming the holder) until the holder finishes or is released.

| Moment | What happens |
|---|---|
| Prepare | Check it is a git working tree. `dirtyPolicy: refuse` stops on uncommitted changes; `baseline` records them (path to size and write time). Remember the current branch as `ReturnTo`. Fetch `origin` if present, check out `baseBranch`, fast-forward from `origin/<base>` (stops if diverged), create the run branch |
| Run finishes `Done` or `WaitingForHuman` | The repository **stays on the run branch** so the result can be played. `WorkspaceHeld` is set and the lock stays with the run |
| Release (`POST /api/runs/{id}/release`, button on the run page) | `git checkout ReturnTo`, clear `WorkspaceHeld`, free the project |
| Failed, Blocked, Cancelled | Partial agent changes become a local `wip(moonwalk)` commit on the run branch (never pushed), the repository goes back to `ReturnTo`, the project is freed |
| MoonWalk restarts | Runs with `WorkspaceHeld` get their project lock back. Runs that were `Running` become `Interrupted` |

Note the release target is the branch you were on when the run started (`ReturnTo`), not necessarily `baseBranch`.

`Git` is a thin wrapper over the git CLI (no prompts, no pager). Staging and committing use `--pathspec-from-file` so only
the agent's paths are touched, never other staged changes.

## Agents (`src/MoonWalk/Agents`)

`IAgent.RunAsync(request, emit, ct)` runs one agent job and streams events through `emit`. `CliProcess` spawns the CLI with
the prompt on stdin, reads stdout and stderr line by line, and kills the whole process tree on cancel or timeout.
`AgentFactory` maps the profile's `agent` name to an adapter (`claude` for now; generic CLI adapters come in chunk 4).

### Claude adapter

Runs `claude -p --output-format stream-json --verbose --include-partial-messages --max-turns N` with `--allowedTools` and
`--disallowedTools` from the profile (default: read/edit/write/search plus read-only git; no git commit, push or add).
`ClaudeStreamParser` is a pure line-to-events function:

| Claude line | MoonWalk event |
|---|---|
| `system/init` | `System` "Agent session started (model)" |
| `stream_event` `text_delta` | `AgentTextDelta` |
| `stream_event` `content_block_stop` on a text block | `AgentText` (empty, closes the streamed block) |
| `assistant` message `tool_use` | `AgentToolUse` (text: tool and a short hint such as the file path; data: input with long strings cut) |
| `user` message `tool_result` | `AgentToolResult` (first line plus line count; `error:` prefix when it failed) |
| `assistant` message `text` when no deltas were streamed | `AgentText` |
| `rate_limit_event` not `allowed` | `Warning` |
| `result` | kept as the agent result: final text, cost, turns, session id (not an event; the stage reports it) |
| anything non-JSON, stderr | `System` / `Warning` |

## Event types

| Type | UI group | Notes |
|---|---|---|
| `AgentTextDelta` | Agent text | Streamed fragment. The UI merges consecutive deltas of one stage into a single row with a live caret |
| `AgentText` | Agent text | Closes a streamed block, or a whole line |
| `AgentToolUse`, `AgentToolResult` | Tools | `text` is the tool and hint or result summary, `data` is shown in a collapsible block |
| `RunStarted`, `RunFinished`, `StageStarted`, `StageFinished`, `System`, `Warning`, `Error` | System | |

## HTTP API (`src/MoonWalk/Web`)

| Route | Purpose |
|---|---|
| `GET /api/runs`, `GET /api/runs/{id}` | Run snapshots |
| `POST /api/runs` `{ brief, project }` | Start a run (400 for an unknown or invalid project) |
| `POST /api/runs/{id}/cancel` | Cancel: the stage's cancellation token fires and the agent process tree is killed |
| `POST /api/runs/{id}/release` | Give the working folder back (409 with a reason if the checkout fails) |
| `GET /api/projects`, `POST /api/projects/reload` | Project profiles with validity and errors; reload from disk |
| `GET /api/stream` | SSE: `run` events carry a full run snapshot on every change (drives the board) |
| `GET /api/runs/{id}/stream` | SSE: `ev` events carry run events. Resumes from `Last-Event-ID` or `?after=` |

## Web UI (`src/MoonWalk/wwwroot`)

Vanilla ES module, no build step. Hash routing: `#/` is the board, `#/run/<id>` is the run detail with facts (agent, branch,
commit, cost, elapsed), a "workspace held" banner with the Release button, the stage timeline, the live feed (follow mode,
filters by text, tools and system) and the brief. Two EventSource connections: one global for snapshots, one per open run
for events. Colors are CSS tokens on `:root`; the theme is dark only.

## Verified behaviour

Exercised against a throwaway repo with the real `claude` CLI: happy path (branch, commit with trailers, workspace kept,
release back to the previous branch), second run queued while the first holds the workspace and proceeding after release,
dirty tree refused, guard failure (wip commit kept, repo restored, lock freed), `STATUS: BLOCKED` handling, cancel
mid-agent (agent process killed, repo restored), and a held workspace still blocking new runs after a MoonWalk restart.

## Not built yet

Spec, Verify and Hand-over stages, approval gates, pause/resume, retry, guidance notes, generic CLI agents, agent health
checks, the worktree workspace mode. See the roadmap in `README.md`.
