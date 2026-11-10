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
| `RunEngine` | Runs a pipeline in the background: workspace lock, prepare, stage loop with decisions, pause, guidance, retry, settle, release, cancel |

`Run` also carries `Waiting` (what the run is waiting for), `Guidance` (notes), `PauseRequested` and `Artifacts` (files under
the run folder the UI may show).

## Pipeline (`src/MoonWalk/Pipeline`)

A stage implements `IStage` (`Name`, `RunAsync(StageContext, CancellationToken)`) and returns a `StageResult` with one of
`Passed`, `Failed`, `NeedsHuman`, `Blocked`. `StageContext` carries the project profile, the workspace session, the agent,
the attempt number, the guidance notes, the previous failure, `Emit` (events tagged with the stage) and
`UpdateRun`/`AddCost`/`SetStageAgent`/`WriteArtifact`. `PromptContext()` renders guidance and the previous failure as prompt sections.

`PipelineFactory` turns a profile's `stages` names into stages: `Spec`, `Implement`, `Verify`, `Handover`. The built-in
`demo` project uses scripted `DemoStage`s instead (a gate after Spec, a Verify that fails once when the brief contains
`[fail-verify]`, a Handover that asks for a human), so the whole control surface can be tried without an agent.

### The run loop and decisions

After each stage the engine decides whether to wait:

| Stage outcome | Run waits? | Waiting reason | Can approve |
|---|---|---|---|
| `Passed`, stage listed in `gates` | yes | `Gate` | yes |
| `Passed`, no gate | no, next stage | | |
| `NeedsHuman` (Handover) | yes | `Review` | yes |
| `Failed` | yes | `Failed` | no |
| `Blocked` | yes | `Blocked` | no |

Waiting sets the run to `WaitingForHuman` and keeps the workspace. The decision (`POST /api/runs/{id}/decision`) is one of:

- **approve**: continue to the next stage (a `NeedsHuman` stage becomes `Passed`). Not allowed for `Failed`/`Blocked`.
- **retry** with optional `retryFrom` (any stage, default the waiting one), `agent` (sticky for the rest of the run) and `note`.
  The stages from `retryFrom` on go back to `Pending`; the failure message is passed to the next attempt as "What went wrong last time".
- **stop**: ends the run as `Cancelled` ("Stopped by user"); the usual wip-commit and restore applies.

A `note` on any decision is added to the guidance. An exception inside a stage becomes a `Failed` result, so it can be retried
instead of killing the run. **Pause** is a request that takes effect before the next stage (an agent in the middle of a stage
is never interrupted); a run that reaches a decision clears it. **Guidance** notes are stored on the run and included in every
later agent prompt (Spec and Implement). A MoonWalk restart turns `Queued`, `Running`, `Paused` and `WaitingForHuman` runs into
`Interrupted`; the workspace flag survives so the repository can still be released.

### SpecStage

Runs the agent with read-only tools (`Read`, `Grep`, `Glob`; `Edit`, `Write`, `NotebookEdit` and the shells are blocked, because
Claude Code auto-approves read-only shell commands). It must answer with only the spec (Goal, Acceptance criteria, Out of scope,
Where it likely lives). MoonWalk saves it as `spec.md` in the run folder, never in the repository.

### VerifyStage

Runs the profile's `verify` steps in order in the repository: `command` plus `args`, no shell, per-step timeout. Output streams
into the feed as `System` events and is saved as `evidence/verify.log`. The first failing step fails the stage, and the last
15 lines of its output become the failure message that is passed to the next attempt. A command that cannot be started is a failure with a clear message.

### HandoverStage

Writes `handover.md` (branch, stage results, commits and diff stat against the base branch, guidance given, what to do next) and
returns `NeedsHuman`, so the run waits for you to play the result with the repository on the run branch.

### ImplementStage

1. Build the prompt: the brief, the approved `spec.md` if there is one, guidance and the previous failure, a "this is a retry, check git status and log first" note on later attempts, fixed rules (smallest change, no git commit/push/add, final summary, `STATUS: BLOCKED` if it cannot finish), then the profile's `extraInstructions`.
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
| Branch created | `WorkspaceHeld` is set at once: the run owns the folder for its whole life, including while it waits for you |
| Run finishes `Done` | The repository **stays on the run branch** so the result can be played; the lock stays with the run |
| Release (`POST /api/runs/{id}/release`, button on the run page) | Only when the run is no longer active. `git checkout ReturnTo`, clear `WorkspaceHeld`, free the project (409 while the run is active, so a retry can never run on the wrong branch) |
| Stopped, cancelled or failed run | Partial agent changes become a local `wip(moonwalk)` commit on the run branch (never pushed), the repository goes back to `ReturnTo`, the project is freed. If the checkout back fails, the flag and lock are kept |
| MoonWalk restarts | Runs with `WorkspaceHeld` get their project lock back; unfinished runs become `Interrupted` and show the Release button |

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
| `WaitingForHuman`, `Decision`, `Guidance`, `Paused`, `Resumed` | System | Control-surface events, colored in the feed |

## HTTP API (`src/MoonWalk/Web`)

| Route | Purpose |
|---|---|
| `GET /api/runs`, `GET /api/runs/{id}` | Run snapshots |
| `POST /api/runs` `{ brief, project }` | Start a run (400 for an unknown or invalid project) |
| `POST /api/runs/{id}/cancel` | Cancel: the stage's cancellation token fires and the agent process tree is killed |
| `POST /api/runs/{id}/pause`, `/resume` | Pause before the next stage; resume (409 with a reason when not applicable) |
| `POST /api/runs/{id}/guidance` `{ text }` | Add a note for later agent prompts |
| `POST /api/runs/{id}/decision` `{ action, note?, agent?, retryFrom? }` | `approve`, `retry` or `stop` while the run is `WaitingForHuman` (409 otherwise) |
| `POST /api/runs/{id}/release` | Give the working folder back (409 with a reason while the run is active or if the checkout fails) |
| `GET /api/runs/{id}/files/{name}` | An artifact of the run (`spec.md`, `evidence/verify.log`, `handover.md`); only names listed on the run |
| `GET /api/agents` | Known agent names |
| `GET /api/projects`, `POST /api/projects/reload` | Project profiles with validity and errors; reload from disk |
| `GET /api/stream` | SSE: `run` events carry a full run snapshot on every change (drives the board) |
| `GET /api/runs/{id}/stream` | SSE: `ev` events carry run events. Resumes from `Last-Event-ID` or `?after=` |

## Web UI (`src/MoonWalk/wwwroot`)

Vanilla ES module, no build step. Hash routing: `#/` is the board, `#/run/<id>` is the run detail with facts (agent, branch,
commit, cost, elapsed), pause/resume/cancel buttons, a "workspace held" banner with the Release button, the **decision card**
(approve, retry from a chosen stage with another agent and a note, stop), the stage timeline, a guidance box with the notes
so far, the brief, and a panel with tabs: the live feed (follow mode, filters by text, tools and system) plus one tab per
artifact. Rows waiting for you are highlighted on the board and the browser tab title shows `(n)`. Two EventSource
connections: one global for snapshots, one per open run for events. Colors are CSS tokens on `:root`; the theme is dark
only, and the layout collapses to one column below 900px.

## Verified behaviour

Exercised against a throwaway repo with the real `claude` CLI:

- Branch workspace: happy path (branch, commit with trailers, workspace kept, release back to the previous branch), second
  run queued while the first holds the workspace and proceeding after release, dirty tree refused, guard failure (wip commit
  kept, repo restored, lock freed), `STATUS: BLOCKED`, cancel mid-agent (process killed, repo restored), a held workspace
  still blocking new runs after a MoonWalk restart.
- Control surface: Spec writes `spec.md` outside the repo, the gate waits, a guidance note and approval continue the run,
  Implement commits, Verify passes and saves its log, Handover writes `handover.md` and waits, release is refused while the
  run is active and works after approval. A failing Verify waits as `Failed` (cannot be approved), a retry from Implement
  with a note makes a second commit that fixes it, a stop restores the repository and keeps the run branch, and a verify
  command that does not exist fails with a readable message.
- Without an agent (`demo` project): gate, bad action and approve-a-failed-stage refusals, retry with note and agent,
  pause then resume, stop at handover, cancel while waiting, waiting runs become `Interrupted` after a restart.
- The UI was rendered in headless Chrome at desktop and phone widths (decision card, stage timeline, guidance, artifact
  tabs, board highlight).

## Not built yet

Generic CLI agents (Gemini, Muse, others), agent health checks, resuming `Interrupted` runs, the worktree workspace mode,
notifications beyond the tab title. See the roadmap in `README.md`.
