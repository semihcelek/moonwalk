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
`Interrupted`; the workspace flag survives so the workspace can still be released.

### Recovery

`POST /api/runs/{id}/recover` continues an `Interrupted` run. It re-validates the project and that the stage list is unchanged,
checks the workspace is usable (`EnsureReadyAsync`: the right branch is checked out, or the worktree folder exists; otherwise 409
with the reason), then restarts the loop. If the run was waiting when MoonWalk stopped (`Run.ResumeWait`, stored at load time),
the same question is asked again with the stage's old result kept. Otherwise it resumes at the first stage that had not passed;
an agent stage that was cut off runs again as attempt 2, so its prompt says "this is a retry, check git status and log first".
The project lock was already re-taken at startup for branch-mode runs that held the workspace. Agent and verify processes are
in a job object that dies with MoonWalk (see Safety below), so a recovered run never has an old agent still editing the repository.

### Reopening a finished run (follow-up rounds)

`POST /api/runs/{id}/reopen` `{ note, from?, agent?, model?, effort?, auto? }` (`RunEngine.ReopenAsync`) takes a Done, Failed or Cancelled run
back to work on its own branch, so a fix after review (or after an auto-mode run released the workspace) stays on one branch and in one
run. It refuses an active or `Interrupted` run (recover that instead), an empty note, a missing workspace record, and
a branch that no longer exists (checked up front with `git rev-parse refs/heads/<branch>`). Otherwise it raises `Run.Rounds`, clears the
error, finish time, review-merge fields and the archived flag, sets automation to what the request says (default all off, so the round
can be reviewed), adopts the profile's current stage list (stages the run lacks, such as a Verify added later, start Pending), applies agent, model and effort from the restart stage on like a retry, resets the stages from `from` (default
`Implement`) to `Pending`, records the note as guidance and starts `ExecuteAsync(reopenFrom: index)`.

`ExecuteAsync` then takes the project lock like any run (it waits if another run holds the workspace). If the run still holds its workspace
it only calls `EnsureReadyAsync`; otherwise `IWorkspace.ReopenAsync`: branch mode refuses a dirty tree under the `refuse` policy, records
the branch the repository was on as the new `ReturnTo` (and a fresh baseline under `baseline`), and checks the run branch out; worktree mode
re-creates the removed folder with `git worktree add <dir> <branch>`. If that fails the run ends `Failed` with the reason and nothing is
touched (it can be reopened again once fixed). `StageContext.PromptContext` adds a "Follow-up round N" section when `Rounds > 1`: the branch
already holds the work, keep what works, change only what the guidance asks. The base branch for diffs stays the project's, so Changes and
`handover.md` show the whole feature; `Rounds` counts attempts to reopen that got past validation, including one that then failed to attach.

## Safety (`ProcessJob`, `AgentGitCheck`)

- **Agents die with MoonWalk.** `ProcessJob` is a Windows job object with kill-on-close; `CliProcess` adds every process it starts
  (agents, verify steps) right after launch, and children join automatically. The OS closes the handle when MoonWalk exits, however it
  exits, so even a hard kill takes the agent down with it. Without a job (other platforms) only the normal cancel and timeout kill apply.
  There is a tiny window between a process starting and being added to the job.
- **Agents touching git.** Before an agent stage, `IWorkspace.GetHeadAsync` records the branch head; afterwards `InspectAsync` reports
  whether it moved, the commits, files and lines they touched, and whether the run branch exists on `origin` (a push updates the local
  remote-tracking ref, so this needs no network). `AgentGitCheck` then applies the rules for Implement and Integrate:
  a **push** is `Blocked`; the agent's **own commits** are allowed but reported as a Warning and checked against the same guards, and if
  they break the guards the stage fails and names them (they stay on the run branch); an Implement stage whose agent committed everything
  itself and left nothing uncommitted passes with that as its result. This matters for agents MoonWalk cannot hold to a tool list
  (agy with `allowedCommands`, Muse with `allowShell`).

### Auto mode

`Run.Auto` (`AutoMode { Plan, Implementation }`, set at start from the dialog or the project's `auto` default) lets a run answer its own
questions. In the loop, right where a run would wait, `AutoDecision(auto, wait)` returns a decision: a `Gate` wait is approved when
`Plan` is on; a `Review` wait (Handover) is approved when `Implementation` is on; `Failed`, `Blocked` and `Budget` waits are answered
with **stop** only when both are on (`Full`), and that stop ends the run as `Failed` with the stage message and "full auto mode stops
instead of waiting" (a person's stop is `Cancelled`). Each automatic answer is recorded as a `Decision` event ("Auto mode: ..."). After
a run finishes `Done` with `Implementation` on, the engine calls `ReleaseAsync`, so in branch mode the next run on the project is not
left waiting for a release. Automatic mode never retries and never pushes or merges.

The run page's agent line is derived from the stages, not from `Run.Agent` (which is only the project default): `stageAgent(run, stage)`
takes the agent the stage actually ran with (`StageRecord.Agent`), else the run's per-stage choice, else the default, and `agentsLine`
shows one name when all agent stages agree and otherwise "plan agy · implement muse".

### SpecStage

Asks the agent to be read-only (Claude: tools `Read`, `Grep`, `Glob` with `Edit`, `Write`, `NotebookEdit` and the shells blocked,
because Claude Code auto-approves read-only shell commands; agy: plan mode). It must answer with only the spec (Goal, Acceptance
criteria, Out of scope, Where it likely lives). **Afterwards the stage checks that no file changed**, since not every agent can
enforce read-only; if one did, the stage fails and names the files. MoonWalk saves the spec as `spec.md` in the run folder,
never in the repository.

### VerifyStage

Runs the profile's `verify` steps in order in the repository: `command` plus `args`, no shell, per-step timeout. Output streams
into the feed as `System` events and is saved as `evidence/verify.log`. The first failing step fails the stage, and the last
15 lines of its output become the failure message that is passed to the next attempt. A command that cannot be started is a failure with a clear message.

### HandoverStage

Writes `handover.md` (branch, stage results, commits and diff stat against the base branch, guidance given, what to do next) and
returns `NeedsHuman`, so the run waits for you to play the result with the repository on the run branch.

For Unity projects it first runs `UnityMeta` (on when the profile has `commitMetaFiles: true`, or when it is unset and the work folder
has `Assets` and `ProjectSettings`; `false` switches it off). Unity writes a `.meta` (the asset's GUID) only when the Editor imports a
file, so a script an agent added is committed without one, and scenes referring to it break after a merge. `UnityMeta` takes the new
`.meta` files that appeared during the run (`GetChangesAsync`, so files that were already untracked at the start stay alone), keeps
those whose asset is tracked in git and that are not tracked themselves, checks them against the guards, and commits them as
`chore: add Unity .meta files` (a guard violation is a warning, not a failure). Other files the Editor touched are never committed
here. It then lists files added under `Assets` or `Packages` (`git diff --diff-filter=A base...HEAD`, skipping what Unity ignores:
dot and `~` names) that still have no `.meta` in HEAD, as a `Warning` event and a "Unity .meta files missing" section in
`handover.md`. MoonWalk does not write `.meta` files itself (a made-up GUID could differ from the one the Editor would pick); open
the project so the Editor imports the files, then **Retry** from Handover to commit them.

### IntegrateStage

The Unity step after Implement. An agent with the unity-mcp tools reads the Console and waits for compilation (fixing any `error CS`),
wires what the change needs into the scene or assets, checks the Console again and saves. It has its own agent, model and effort like
every agent stage. For Claude the allowed tools are the profile's plus the stage's MCP patterns (see MCP below); with no `mcp` section
they are `mcp__unityMCP__*` and `mcp__unity-mcp__*` (the server is registered under either name). Whatever it changed on disk goes through
the same baseline check, guards (`guards.integrateAllowPaths` are exempt from deny and allow rules for this stage only, so a saved scene
or prefab can pass) and commit as Implement (changing nothing is fine: scene edits made through unity-mcp live in the open
Editor until saved). If the Editor is not reachable it replies `STATUS: BLOCKED`, which waits for you like any blocked stage. Its report
is saved as the `integrate.md` artifact.

### MCP access (`McpOptions`, `StageContext.McpFor`)

`mcp.servers` (name to `type` stdio with `command`/`args`/`env`, or http with `url`) and `mcp.stages` (Spec, Implement or Integrate to server
names) are validated by `ProjectRegistry` (unknown server, non-agent stage, a stdio server without a command, an http one without a url,
and an Integrate stage with no server listed are profile errors). When the section is non-empty, each agent stage calls
`StageContext.McpFor(stage)`: for the `claude` agent it writes `runs/<id>/mcp-<stage>.json` (`{ "mcpServers": { ... } }` with only that stage's
servers) and returns the tool patterns `mcp__<server>__*`. The stage passes them as `AgentRequest.McpConfigFile` and adds the patterns to
the allowed tools; `ClaudeAgent` then adds `--mcp-config <file> --strict-mcp-config`, so even the project's `.mcp.json` is ignored and a
stage with no servers sees none. **Muse** (verified against muse 1.4.14 with Spark, through a real Spec run) gets the same config as one `-c mcpServers.<name>=<json>`
per server, in Muse's own shape (`{command,args}` for stdio, `{url}` for http; it refuses unknown keys such as `type`, and `env` values
must be `${VAR}` references, so a server's env is skipped with a note). The JSON contains double quotes, which the `muse.cmd` shim
(`cmd` to Windows PowerShell to the binary) mangles, so `MuseAgent` starts `muse-bin-<version>.exe` from the shim's folder directly
(`.muse-version` names the version; not found means Muse runs without the servers, with a warning). Muse has no tool allow-list, so the
patterns are empty and it sees every tool of each listed server; it lists MCP tools behind its own `tool_search`. Other agents get a
`Warning` event naming the servers they run without. Spec keeps its read-only tools
plus the MCP patterns (so list only read-only servers there; MoonWalk still verifies afterwards that no file changed). Verified with
Claude Haiku against a stdio server: allowed gives the real answer, a stage with none listed cannot reach it, and both leave no denials.

### ImplementStage

1. Build the prompt: the brief, the approved `spec.md` if there is one, guidance and the previous failure, a "this is a retry, check git status and log first" note on later attempts, fixed rules (smallest change, no git commit/push/add, final summary, `STATUS: BLOCKED` if it cannot finish), then the profile's `extraInstructions`.
2. Run the agent in the repository folder.
3. Fail on timeout, non-zero exit or `is_error`; `Blocked` when the answer starts with `STATUS: BLOCKED`.
4. Ask the workspace what changed. `Blocked` if the agent edited a file that was already modified before the run (baseline policy). If nothing changed: `Failed` on the first attempt, but a **retry** (attempt 2 or later) passes when the run branch already has commits, because the earlier attempt's work then stands ("No further changes needed").
5. Stage exactly those paths, count changed lines, run `Guards.Check`. Violations fail the stage.
6. Commit only those paths with trailers `MoonWalk-Run`, `MoonWalk-Agent` (and `Co-Authored-By` for Claude).

## Projects (`src/MoonWalk/Projects`)

`ProjectProfile` is read from `projects/*.json` (comments and trailing commas allowed). `ProjectRegistry` validates each
profile (repo exists and is a git repo, known agent and stages, workspace mode supported, unique name) and keeps invalid ones
listed with their errors so the UI can explain them. The built-in `demo` project always exists.

`ignorePaths` (globs) lists files MoonWalk pretends do not exist in branch mode: they are filtered out of `git status` before
anything else, so they never count as dirty, never appear as the agent's changes and are never committed. It is for files that a
tool rewrites on its own during a run (for Unity, some settings assets); without it such a file, if already modified at the start,
makes the run `Blocked` because MoonWalk cannot tell the tool's edit from the agent's.

Settings paths (`MoonWalk:DataDir`, `ProjectsDir`, `AgentsFile`) are resolved once by `AppPaths`: relative values resolve against
the MoonWalk repository root (the folder holding `MoonWalk.slnx`, found by walking up from the app), not the current folder,
because `dotnet run` starts the app in the project folder. The artifact endpoint uses the same resolved paths.

`Guards.Check(changedPaths, changedLines, rules)` is a pure function: max files, max lines, deny globs, optional allow globs.
Globs: `**` crosses folders, `*` and `?` stay in one segment, a trailing `/` means everything below.

## Workspace modes (`src/MoonWalk/Workspace`)

`IWorkspace` has two implementations, chosen by the profile's `workspace` (`WorkspaceFactory`). The engine and stages only see the
interface (`PrepareAsync`, `EnsureReadyAsync`, `GetChangesAsync`, `StageAsync`, `CommitAsync`, `GetHandoverInfoAsync`,
`AbortAsync`, `ReleaseAsync`) and the persisted `WorkspaceState` (`Mode`, `Branch`, `WorkDir`, `ReturnTo`, baseline).
`StageContext.WorkDir` is the folder the agent and the verify steps run in.

- **`branch`** (default): the run works on `moonwalk/<run>-<slug>` inside the project's normal working folder. For Unity this
  avoids a second checkout and a full reimport. Because the folder is shared, the engine enforces **one run per project**:
  later runs stay `Queued` (with a System event naming the holder) until the holder finishes or is released.
- **`worktree`** (opt-in): `git worktree add -b <branch> <root>/<run> <start>`, where the start point is `origin/<base>` when the
  local base can fast-forward to it and the local base otherwise; the root defaults to `wt-moonwalk` beside the repository
  (`worktreeRoot` overrides it). The user's own checkout is never touched, so there is no lock and no dirty-tree policy, and
  several runs of a project proceed in parallel. File, stage and commit logic is the branch workspace's, pointed at the worktree
  folder. Release (and abort) removes the folder with `git worktree remove --force`; the run branch stays in the repository.
  For Unity the cost is a full import of each new folder.

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

`IAgent.RunAsync(request, emit, ct)` runs one agent job and streams events through `emit`. `AgentRequest` carries the prompt,
the work folder, optional tool lists and `ReadOnly`; `AgentResult` carries exit code, final text, cost, turns, usage and
diagnostics. `CliProcess` spawns the CLI (prompt on stdin unless the adapter passes it differently), reads stdout and stderr
line by line, and kills the whole process tree on cancel or timeout. It also resolves commands the way a shell does (PATH plus
PATHEXT), so `muse` finds `muse.cmd`.

`AgentRegistry` knows the built-in `claude`, `agy` and `muse` plus the CLI agents in `agents.json`, creates adapters by name, and runs
each agent's version command (15 s limit, default command names) for the health check. Profiles are validated against it.

| Agent | Adapter | Notes |
|---|---|---|
| `claude` | `ClaudeAgent` + `ClaudeStreamParser` | Honours tool allow/deny lists |
| `agy` | `AgyAgent` + `AgyStreamParser` | Gemini via Antigravity; details below |
| `muse` | `MuseAgent` + `MuseStreamParser` | Muse Code (Meta) via `muse exec --json`; details below |
| custom | `GenericCliAgent` | Config in `agents.json`: `command`, `args` with `{prompt}`, `{promptFile}`, `{workdir}`, `timeoutMinutes`, `versionArgs`. Each stdout line becomes an `AgentText` event; stderr becomes a System event. `{prompt}` is refused for `.cmd`/`.bat` commands (argument injection) |

### agy adapter

Runs `agy -p <prompt> --output-format stream-json --mode accept-edits --print-timeout <n>m`. The prompt must be an argument
(`-p` does not read stdin). A read-only request uses `--mode plan`. Observed stream (`agy` 1.5.5):

| agy line | MoonWalk event |
|---|---|
| `init` | `System` "Agent session started" (conversation id kept) |
| `step_update`, `agent_response`, `text_delta` | `AgentTextDelta`; the closing DONE step becomes `AgentText` (empty) |
| `step_update`, `tool` ACTIVE (first time per step) | `AgentToolUse` (tool name and a hint from `Path`/`CommandLine`/...; data: parameters, long strings cut) |
| `step_update`, `tool` DONE or ERROR | `AgentToolResult` (output summary; ERROR is shown as `error:`) |
| `result` | final text, status (anything but `SUCCESS` is an error), turns, total tokens (no cost reported) |
| stderr | `Warning`, and kept as `Diagnostics`, which the stage failure message includes |

### muse adapter

Runs `muse exec --json --prompt-file <tmp> --workspace <dir>` (Muse 1.4.14). The prompt goes through a temporary file because on
Windows `muse` is a batch shim (`muse.cmd` starts a PowerShell launcher that starts the versioned exe); the work folder is
rejected if it contains a character unsafe for batch arguments. A read-only request adds `--disable-write --disable-shell`; the
shell is also disabled when implementing unless `muse.allowShell` is set. Muse's own approval and sandbox stay on, and the
workspace's rules and skills load only with `muse.trustWorkspace`. `--json` writes one envelope per line (`payload_type`,
`payload`) in an event-sourced protocol; the parser keeps only:

| payload_type | MoonWalk event |
|---|---|
| `run.model.configured` | `System` "Agent session started (model)" |
| `task.lifecycle.proposed` with `task_kind` `tool.<name>` | `AgentToolUse` (the tool name; arguments are not in this event) |
| `tool.result` | `AgentToolResult`: for file writes `path (+added -removed)` from `edit_facts`, otherwise the first line and line count; a non-success outcome is shown as `error:` |
| `run.output.delta` | `AgentTextDelta` (token-level) |
| `run.terminal.*` | `AgentText` (empty, closes the block); final text and error state from the payload |
| `run.usage` | total input plus output tokens, shown after the run |
| everything else (task lifecycle bookkeeping, reminders) | ignored |

stderr lines (`muse: workspace root ...`) become `System` events and are kept as diagnostics. No cost is reported.

### agy permissions

Headless agy cannot ask for permission, and a denied shell command ends the session (observed: the model ran
`Get-ChildItem`, was auto-denied, and the run ended with no changes). So by default MoonWalk allows no commands and appends
"shell commands are not permitted, use file tools only" to the prompt. `agy.allowedCommands` opts in: the entries are written to a
temporary `--settings` file as `{"permissions":{"allow":["command(<entry>)"]}}` (an exact command line, or `*` for everything;
prefix wildcards were observed not to match). agy has no tool allow/deny lists, so MoonWalk cannot stop it from running git
commands once commands are allowed.

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

## Models and effort per stage

`ModelCatalog` lists, per built-in agent, the models the UI offers and the effort levels each accepts (`GET /api/models`): Claude
`opus`, `sonnet`, `haiku` with `low`..`max` (Haiku none); agy's model ids with the effort baked into the name (so no separate effort);
Muse `muse-wonder-1.0`, `muse-spark-1.3`, `muse-ember-1.0` with `none`..`ultra`. Custom agents from `agents.json` take no model or effort.
`ModelCatalog.Validate(agent, model, effort)` returns the reason a choice cannot work.

A run stores `StageAgents`: stage name to `AgentChoice(agent, model, effort)`, every field optional (empty means the project's default).
`POST /api/runs` takes it as `stages`; the engine validates it at start (unknown stage, agent, model or effort is a 400 naming the stage)
and `CreateStageAgent` builds each stage's agent from the run's choice. The choice is applied to a **copy** of the project's agent
options (`AgentRegistry.Create(name, profile, model, effort)`), so it never leaks into the shared profile or other runs. The adapters pass
it as `--model` plus `--effort` (Claude), `--reasoning-effort` (Muse) or a model id with the effort inside it (agy).

A retry decision may carry `agent`, `model` and `effort`; they are validated against the catalog and applied to every Spec, Implement and
Integrate stage from the retry point onward, and stored back on the run. The model each stage actually started with is visible in the
feed's "Agent session started (model)" line.

`RunStore.Save` retries the atomic replace of `run.json` a few times (25 ms steps), because on Windows an antivirus scan, the search
indexer or an open viewer can hold the file for a moment and fail the replace with "Access to the path is denied".

## Usage, budgets and presets

**Usage.** Each agent parser reports tokens and the model: Claude sums `input`, `cache_creation_input`, `cache_read_input` and
`output` tokens from the final `result` line and reads the model from `system/init`; agy takes `total_tokens`; Muse sums `run.usage`
input and output tokens and reads the model from `run.model.configured`. Claude alone reports dollars (`total_cost_usd`). A stage calls
`StageContext.AddUsage(agent, result)`, which appends a `UsageEntry` (stage, attempt, agent, model, tokens, dollars) to `Run.Usage` and
adds the dollars to `Run.CostUsd`. `RunStore.UsageSummary(days)` adds the ledgers of all runs by day and by agent and model
(`GET /api/usage?days=N`, default 7, at most 90). The UI shows run totals, a line per stage, and a strip on the board.
`AgentResult` also carries the CLI's own error reasons as `Diagnostics` for Claude (for example "Reached maximum budget ($0.003)"), which
the stage failure messages include.

**Budgets.** `budget.maxCostUsd` and `budget.maxTokens` in the profile. The engine calls `BudgetProblem` after each stage that passed and
has stages after it; when a limit is reached the run waits with `WaitState.Reason = "Budget"` (approvable). Approving sets a flag for the
rest of the run so it asks once. Enforcement inside a stage is only possible where the agent has a cap: the engine gives each stage
`RemainingBudgetUsd` (the cost limit minus the dollars spent so far, never less than $0.01, none once waived) and `ClaudeAgent` passes it
as `--max-budget-usd`. On exceeding it the CLI exits 1 with `subtype: error_max_budget_usd`; it checks between turns, so one turn can
overshoot (observed: a $0.07 turn against a $0.01 cap). Muse can be capped by steps (`muse.maxSteps`, `--max-model-steps`); agy has only
`--print-timeout`.

**Presets.** `PresetStore` holds five built-in presets (defined in code) and the user's saved ones in `presets.json` (written atomically,
`MoonWalk:PresetsFile` to move it). `GET /api/presets` returns both lists, `POST /api/presets {name, stages}` saves (a built-in name is
refused; each choice is validated against `ModelCatalog`), `DELETE /api/presets/{name}` removes a saved one. The dialog applies a preset's
choices to the stages the project has and skips agents that are not installed.

The app sets the invariant culture at startup so dollar amounts and counts in messages never use a decimal comma.

## Observability

- **Diff.** `GET /api/runs/{id}/diff` runs `git diff --numstat` and `git diff -U3` for `<base>..<branch>` in the project's repository
  (the base and branch come from the run's `workspace.json`), so it works after the workspace is released. It returns the file list
  (added, removed, binary), the patch (cut at 300,000 characters, with a `truncated` flag), or `available: false` with the reason for
  a run with no branch (the demo project) or a branch git cannot compare. The Changes tab renders it.
- **Images.** The artifact endpoint serves `.png`, `.jpg`, `.gif` and `.webp` with their image content type;
  `StageContext.WriteArtifactBytes` lets a stage save a screenshot under the run folder. The UI shows image artifacts in their own tab.
  The demo project's Implement stage saves a generated PNG so this path is exercised without an agent.
- **Archive and delete.** `Run.Archived` hides a finished run from the board. `POST /api/runs/{id}/archive` and `/unarchive`, and
  `DELETE /api/runs/{id}`, are refused (409) while the run is active, and delete also while it holds its workspace. Delete removes the run from
  `RunStore` (publishing `{id, removed: true}` so open boards drop it), closes its event log (`EventHub.Close`, so Windows lets the folder
  go) and deletes `runs/<id>/`. The git branch is untouched.
- **Board.** Search, status and project filters and *Show archived* live in client-side `boardFilter` state, so they survive live updates
  and navigation. Rows show the run's tokens and cost, a stage bar, the branch and a "why" line (the wait message or the error).
  Flat mode pins `WaitingForHuman` runs in a "Needs you" section; the grouping mode (`boardFilter.group`, `flat` or `project`, saved in
  `localStorage`) puts runs in collapsible per-project sections (`collapsedGroups`). Finished runs have a checkbox (`selected`) and an
  Archive/Restore button; bulk archive just calls the existing per-run routes in parallel (the server still refuses active runs) and
  `toast` offers Undo, which calls the opposite route. No new API.
  The bulk bar also offers "Archive Done older than `STALE_DAYS` (7) days", computed client-side from `finishedAt`; there is no
  server-side auto-archive.
  Row details: `cleanTitle` drops a leading "Goal:"; `batchNum` keeps only the "2/5" of a batch label; `verifyBadge` marks a Done run
  verified only when its `Verify` stage is `Passed` (client-side, from the run snapshot). `md()` renders headings, bullets, `**bold**` and
  `code` as DOM nodes (never `innerHTML`) for stage summaries, agent text and stage results. The batch block header adds cost, time since
  the last finish and merged count. When auto mode approves a `Review` or `Gate` wait the engine rewrites the stage summary
  ("Auto-approved; nobody played this result.") and the feed header shows "Auto-approved" instead of NeedsHuman.
  Batches: `loadBatches` also fills `batchInfo` (id to name, kind, done, total from `GET /api/batches`) and re-renders when it changes.
  `batchRows` folds two or more shown runs with the same `batchId` into a `batchBlock` (collapsed unless in `openBatches` or a run needs
  you); a single run of a batch stays a plain row. No new API.
  Keyboard: one document-level `keydown` listener hands keys to `boardKeys`, which only the board sets (`route()` clears it), and it
  ignores typing in inputs and open dialogs. `cursor` (run id plus index) is the highlighted row; if that row disappears (archived),
  the cursor stays at the same position.
- **Feed groups.** Each `StageStarted` event opens a `<details>` group for that attempt; later events join it until the next stage
  starts or the run finishes. Starting a new attempt folds the earlier ones of the same stage, and `StageFinished` stamps the group's header
  with the outcome.
- **Browser notifications.** The bell toggles `localStorage['mw.notify']` after the browser grants permission. The global run stream compares
  each snapshot with the previous one and, only on a status change and only when the page is hidden or unfocused, notifies for
  WaitingForHuman (titled by reason: Needs you, Ready for you to play, Budget reached), Done, Failed and Interrupted. A run first seen in
  a snapshot never notifies, so a page load does not fire old news. Clicking opens that run. There is no server-side push.

## Batches (`BatchService`)

A `Batch` is a list of `BatchItem`s (label, brief, optional per-item stage choices, `RunId`, status) saved as
`runs/_batches/<id>.json`. `kind = "queue"` has one item per brief and shares the request's stage choices; `kind = "compare"` has one item
per variant, the same brief, and each variant's own choices. `Create` validates everything up front (project, brief count, at least two
variants, and `RunEngine.Validate` for every distinct choice set, which shares the code `Start` uses), then runs in the background.
Items run **sequentially** (the service starts item *n+1* when run *n* is no longer active) except a comparison on a worktree-mode project,
whose items all start at once. A run is "active" while it is Queued, Running, Paused or WaitingForHuman, so a non-automatic batch waits
with its runs; with full auto nothing waits. `stopOnFailure` skips the remaining items when one does not finish `Done`. `Cancel` cancels the
run in progress and skips the rest. Runs record `BatchId` and `BatchLabel` ("2/5 title"). At startup a `Running` batch becomes `Interrupted`
and its unfinished items too; nothing is resumed.

`GET /api/batches`, `POST /api/batches`, `POST /api/batches/{id}/cancel`, and `GET /api/batches/{id}`, which returns each item with its full run and,
for runs with a branch, `git diff --numstat` totals (files, added, removed) against the base. The UI has a mode switch in the New run dialog, a
`#/batch/<id>` page (a queue table, or a comparison table with the cheapest and fastest finished variant marked) and chips for running batches on the board (finished batches show as folded blocks).

## Brief templates and branch hygiene

**Templates.** `BriefStore` keeps one `<name>.md` per template in `briefs/` (`MoonWalk:BriefsDir`). A name must match
`^[A-Za-z0-9][A-Za-z0-9 _-]{0,59}$`, which cannot name another folder, and a template holds at most 40,000 characters.
`GET /api/briefs`, `POST /api/briefs {name, text}` (replaces by name), `DELETE /api/briefs/{name}`.

**Branches.** `BranchHygiene` lists `refs/heads/moonwalk/*` with `git for-each-ref`, whether each is merged into the project's base branch
(`git branch --merged <base>`), commits ahead (`rev-list --count base..branch`), the checked-out branch, and worktree checkouts
(`git worktree list --porcelain`). The run a branch belongs to is read from its name (`moonwalk/<run id>-slug`). `GET /api/branches?project=`,
`POST /api/branches/delete {project, branch, force}`, `/delete-merged` and `/prune`. The server decides what is protected, not the UI: a
branch is kept when it is checked out in the repository or a worktree, or when its run is active or still holds the workspace (409 with the
reason). Only `moonwalk/*` names are accepted. An unmerged branch needs `force`. The delete itself uses `git branch -D` after MoonWalk's own
merged-into-base check, because `-d` judges "merged" against whatever happens to be checked out and refused branches that were merged into the base.

## Review branch (`ReviewService`)

`ProjectProfile.ReviewBranch` (default `waiting-review`; validated: letters, digits, `.`, `_`, `-`, `/`, at most 60 characters, not under
`moonwalk/`, not the base branch). `MergeRunAsync(runId)` takes a `Done` run and merges its branch into the review branch under a
per-repository lock: the review branch is created from the base if missing; a branch already contained in it is reported as already
merged; otherwise **`git merge-tree --write-tree --name-only --no-messages <review> <branch>`** computes the result without touching any
checkout. Exit 1 means conflicts: the file names are returned (HTTP 409) and nothing is written. On success `commit-tree` writes a merge
commit with both parents (message `Merge moonwalk/<run>-... into <review>`, trailer `MoonWalk-Run`) and `update-ref` moves the branch with the
old tip as a guard, so a concurrent change fails instead of being overwritten. The run records `ReviewMergedAt`, `ReviewBranch` and
`ReviewCommit`. If the review branch is the one checked out, a ref update would leave the working folder out of sync, so a real
`git merge --no-ff` is used instead, only when `git status` shows no changes to tracked files, and a conflict is aborted.

`CheckoutAsync` switches the repository to the review branch and is refused while any run of the project is active or holds the workspace
(git itself refuses if local changes would be overwritten). `ResetAsync` runs `git branch -f <review> <base>` (refused while it is checked
out) and clears the merge marks of the project's runs. `StatusAsync` lists the first-parent merges with their run ids.
`AutoMode.MergeToReview` merges a run right after it finishes Done (after the automatic release); a conflict is a Warning event and the run stays Done.

API: `GET /api/review?project=`, `POST /api/runs/{id}/merge-review`, `POST /api/review/checkout`, `POST /api/review/reset`.

## Delegation guide (`GuideBuilder`)

`docs/delegating-to-moonwalk.md` is embedded in the app (`EmbeddedResource`) and served by `GET /api/guide[?project=]` as markdown. Placeholders
are filled at request time: `{{base_url}}` from the request, `{{models}}` from `ModelCatalog`, `{{presets}}` from the built-in and saved presets,
and `{{projects}}` from the ready, non-demo projects (a table of stages, default agent, workspace, base and review branch and auto default, then per
project the `briefingNotes` and `extraInstructions`). It tells a main agent how to write briefs, the queue format and `POST /api/batches` body, the
automation flags, and how to follow results, and to answer with a split, a `moonwalk-queue` block, settings and the request body, without submitting.

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
| `POST /api/runs/{id}/release` | Give the workspace back (409 with a reason while the run is active or if the checkout or folder removal fails) |
| `POST /api/runs/{id}/recover` | Continue an `Interrupted` run (409 with a reason otherwise) |
| `POST /api/runs/{id}/reopen` `{ note, from?, agent?, model?, effort?, auto? }` | Continue a finished run on its own branch as a new round (409 with a reason if active, interrupted, the note is empty or the branch is gone) |
| `GET /api/runs/{id}/diff` | The run's git diff against its base branch (file list and patch) |
| `POST /api/runs/{id}/archive`, `/unarchive`, `DELETE /api/runs/{id}` | Hide, restore or permanently delete a finished run (409 while active; delete also while it holds the workspace) |
| `GET /api/runs/{id}/files/{name}` | An artifact of the run (`spec.md`, `evidence/verify.log`, `handover.md`); only names listed on the run |
| `GET /api/agents` | Known agent names and definitions, plus `agents.json` errors |
| `GET /api/agents/health` | Runs each agent's version command and reports availability |
| `POST /api/agents/reload` | Re-read `agents.json` |
| `GET /api/projects`, `POST /api/projects/reload` | Project profiles with validity and errors; reload from disk |
| `GET /api/stream` | SSE: `run` events carry a full run snapshot on every change (drives the board) |
| `GET /api/runs/{id}/stream` | SSE: `ev` events carry run events. Resumes from `Last-Event-ID` or `?after=` |

## Web UI (`src/MoonWalk/wwwroot`)

Vanilla ES module, no build step. Hash routing: `#/` is the board, `#/run/<id>` is the run detail with facts (agent, branch,
commit, cost, elapsed), pause/resume/cancel buttons, a "workspace held" banner with the Release button, the **decision card**
(approve, retry from a chosen stage with another agent and a note, stop), the stage timeline, a guidance box with the notes
so far, the brief, and a panel with tabs: the live feed (follow mode, filters by text, tools and system) plus one tab per
artifact. `#/settings` lists every agent with its health and every project with its problems, and reloads both configs. An
`Interrupted` run shows **Recover run**; a held worktree says so in the workspace banner. Rows waiting for you are highlighted
on the board and the browser tab title shows `(n)`. Two EventSource
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
- Agents and workspaces (a scripted fake CLI agent, Muse in echo mode, real agy, a missing agent): health reported for all of
  them; two parallel worktree runs on one project (separate folders, main checkout untouched and clean, commits on separate
  branches, release removes the folder and keeps the branch); a Spec agent that edits a file is caught and stopped; a real agy run
  (`write_file` streamed live, commit with the agy trailer, handover, release); a generic agent (Muse in echo mode before the
  adapter existed) resolved through its `.cmd` shim with its output streamed line by line; a project naming an unknown agent is
  rejected at start.
- Real Muse run through all four stages on a throwaway repo: Spec read-only (only `read_file`, repo untouched, spec saved,
  text streamed token by token), Implement (`read_file` then `write_file src/greeter.txt (+1)`, guards, commit with the Muse
  trailer), Verify, Handover, approve, release. About 56k and 87k tokens for the two agent stages.
- Recovery: a run killed hard in the middle of an agent stage comes back `Interrupted` with the workspace held, a second run
  queues behind it, recover re-runs the stage as attempt 2 with the retry note and finishes; a run killed while waiting at a gate is
  asked the same question again and continues after approval; recovering a run that is not interrupted is refused.
- The UI was rendered in headless Chrome at desktop and phone widths (decision card, stage timeline, guidance, artifact
  tabs, board highlight, Settings page).

## Not built yet

Phone or other push notifications (browser notifications only), a playtest stage, and the rest of the improvement plan (Unity compile
check, brief templates, task queue, branch hygiene, compare). Verified for the safety work: an agent that commits within the guards, one whose own commit breaks them,
one that pushes to a bare origin, a plain agent as control, and an agent process that was gone 3 seconds after MoonWalk was hard-killed.
