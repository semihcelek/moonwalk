# MoonWalk: Claude Code guide

MoonWalk is a local orchestrator for agent-driven feature development. A run takes a brief through stages (Spec, Implement, optional
Integrate and Verify, Handover) done by a coding agent (Claude, Gemini through `agy`, Muse, or any CLI), keeps the result on its own git
branch, and shows it live in a dark web UI. It is project-agnostic; it was first used on a Unity game for Meta Quest 3.

The README is the user-facing manual and `docs/architecture.md` is the deep reference (stage rules, event types, API, every design decision).
Read the section you need, not the whole file. This file is only what a new session cannot get from them.

## Stack and layout

.NET 10 minimal API (ASP.NET Core) serving a vanilla JS UI with no build step. State is files, not a database. Solution file is
`MoonWalk.slnx` (not `.sln`).

| Folder (`src/MoonWalk`) | What |
|---|---|
| `Core` | `Run` model, `EventHub` (append-only `events.jsonl` + live fan-out), `RunStore`, `RunEngine` (the stage loop), `BatchService`, `BriefStore`, `GuideBuilder`, `AppPaths` |
| `Pipeline` | `IStage`, stages in `Stages/`, `AgentGitCheck`, `PipelineFactory`, the scripted `DemoPipeline` |
| `Agents` | `IAgent`, one adapter plus stream parser per agent, `CliProcess` (process tree kill, PATHEXT, job object), `AgentRegistry`, `ModelCatalog`, `PresetStore` |
| `Projects` | `ProjectProfile`, `ProjectRegistry` (validates `projects/*.json`), `Guards` |
| `Workspace` | `IWorkspace` with `BranchWorkspace` and `WorktreeWorkspace`, `Git` wrapper, `BranchHygiene`, `ReviewService` |
| `Web` | `ApiEndpoints` (all routes) |
| `wwwroot` | `index.html`, `app.js` (one ES module), `app.css` |

Local-only, git-ignored: `projects/*.local.json` (real project profiles, e.g. `my-project.local.json`), `agents.json`, `presets.json`,
`briefs/`, `runs/`. Never commit these.

## Run and build

```powershell
dotnet run --project src/MoonWalk        # http://localhost:5870
```

Settings come from arguments or environment variables: `MoonWalk__DataDir`, `MoonWalk__ProjectsDir`, `MoonWalk__AgentsFile`,
`MoonWalk__PresetsFile`, `MoonWalk__BriefsDir`, `ASPNETCORE_URLS`. Relative paths resolve against the repo root (the folder holding
`MoonWalk.slnx`), found by `AppPaths`, because `dotnet run` starts the app in `src/MoonWalk`.

**The user usually has their own MoonWalk running on port 5870**, and it locks `bin/Debug/net10.0/MoonWalk.exe`, so a normal build fails with
MSB3027 while it runs. Do not stop it. Build to a scratch folder and run that instead:

```powershell
dotnet build src/MoonWalk -nologo -v q -o <scratch>\mw -p:UseAppHost=false
$env:ASPNETCORE_URLS="http://localhost:59xx"; $env:ASPNETCORE_CONTENTROOT="<repo>\src\MoonWalk"   # contentroot gives it wwwroot
$env:MoonWalk__DataDir=...; $env:MoonWalk__ProjectsDir=...; $env:MoonWalk__AgentsFile=...        # point at throwaway folders
$p = Start-Process dotnet -ArgumentList "<scratch>\mw\MoonWalk.dll" -PassThru -WindowStyle Hidden
# ... test ...  then stop only that pid:  Stop-Process -Id $p.Id -Force
```

**Never stop processes by name** (`Get-Process MoonWalk | Stop-Process`): it kills the user's server. Only kill pids you started.

## Rules for changes

- **Docs are part of the change.** Update `README.md` (what a user sees) and `docs/architecture.md` (how it works) in the same commit. When
  you change an API route, a stage, the auto modes, the model catalog or what a main agent should know, also update
  `docs/delegating-to-moonwalk.md` (it is embedded in the app and served at `/api/guide`).
- **Keep it simple.** Files over a database, no framework in the UI, no abstraction a second implementation does not need (`IWorkspace`
  exists because there are two). Prefer extending an existing class to adding a layer.
- **Agents are black boxes.** Prompt in, events out, exit code, final text. Stages verify results instead of trusting an agent (the Spec
  stage checks no file changed; `AgentGitCheck` looks for agent commits and pushes).
- **MoonWalk owns git.** Agents never commit, push or merge. MoonWalk never pushes or merges on its own either; the only merge is the
  explicit review-branch merge, which never touches the working folder.
- **Anything destructive needs an explicit user action** (delete a run, delete a branch, reset the review branch) and the server enforces
  the safety rule, not only the UI.
- **Commits:** stage only the files you changed (never `git add -A`), message via heredoc, end with
  `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Git prints LF to CRLF warnings on Windows; they are harmless.

## Gotchas learned the hard way

**UI (`app.js`)**
- Never pass `false`, `null` or `undefined` into DOM `replaceChildren(...)`: it prints them as text ("false"). Use the `put(el, ...children)`
  helper (it skips them) and `h(tag, props, ...children)` (which also skips them). This bug appeared three times.
- Static files are served `no-cache` straight from `src/MoonWalk/wwwroot`, so UI edits need no rebuild. Syntax check with
  `node --check` on a copy named `.mjs`.
- Filters and other per-page state live in module variables (`boardFilter`, `runMode`, ...) so live updates do not wipe them.

**Windows and processes**
- `Start-Process -ArgumentList` does not quote arguments with spaces. Pass paths through environment variables or 8.3 short paths.
- Windows can briefly lock `run.json` (antivirus, indexer): `RunStore.Save` retries. `EventHub.Close` must run before deleting a run folder.
- Agent and verify processes are in a job object (`ProcessJob`) that kills them when MoonWalk exits, even if MoonWalk is killed.
- `muse` is a `.cmd` shim: `CliProcess.Resolve` finds it through PATHEXT, and the prompt must go through a file, never the command line.
- PowerShell: `Del` is an alias of `Remove-Item` (do not name a function that); the harness guard can misread delete-looking words in a command.

**Agent CLIs (versions observed: claude 2.1.x, agy 1.5.5, muse 1.4.14)**
- Claude: `claude -p --output-format stream-json --verbose --include-partial-messages`. It auto-approves read-only shell commands, so block
  shells for a truly read-only stage. `--max-budget-usd` exits 1 with `subtype: error_max_budget_usd` and can overshoot by one turn.
- agy: the prompt must be an argument to `-p` (no stdin). Headless it cannot ask permission, and a denied shell command ends the session,
  so shell is off by default and the prompt says so; `agy.allowedCommands` opts in (`command(*)` allows everything, prefix wildcards did not match).
  `--mode plan` is its read-only mode. Effort is part of the model id.
- Muse: `muse exec --json --prompt-file <file> --workspace <dir>`; `--disable-write --disable-shell` for read-only, `--max-model-steps` is the
  only cap. Its JSON is an event-sourced envelope (`payload_type`); the parser keeps only a handful of types.

**Git**
- Merges into the review branch use `git merge-tree --write-tree` + `commit-tree` + `update-ref`; only when that branch is checked out is a real merge used.
- `git branch -d` judges "merged" against the checked-out branch, so branch deletion uses `-D` after MoonWalk's own merged-into-base check.
- Staging and commits use `--pathspec-from-file` with NUL separators so only the agent's files are touched.

## Testing (there are no committed tests)

Everything so far was verified with throwaway scripts, kept outside the repo. The method, so you can rebuild it:

1. Make a throwaway git repo with a `main` branch, and a **fake agent**: a PowerShell script defined in `agents.json` as a generic CLI agent
   (`command: pwsh`, `args: [-NoProfile, -File, fake.ps1, {promptFile}, ...]`) that writes a file, sleeps, commits, or pushes depending on a
   `-Mode` argument. This tests every pipeline path for free.
2. Start your own server pid from a scratch build (above) with `MoonWalk__*` pointing at throwaway folders, drive it over HTTP
   (`Invoke-WebRequest`/`Invoke-RestMethod`), and check run status, events (`runs/<id>/events.jsonl`) and git state.
3. The `demo` project needs no repo or agent: it has a gate after Spec, a Verify that fails once if the brief contains `[fail-verify]`, a
   Handover that waits, and generates a screenshot artifact. Use it for the control surface (gates, retry, pause, stop, auto modes).
4. Real agents cost quota: use Haiku, `muse-ember-1.0`, or a one-line brief, and only after the fake-agent run passes.
5. UI: headless Chrome through the DevTools protocol from a small Node script (`--screenshot` and `--virtual-time-budget` hang because of the
   SSE streams). Launch it with its own `--user-data-dir`, navigate, evaluate JS, `Page.captureScreenshot`. Look at the screenshots.
6. After an engine change, rerun the free control-surface suite; it has caught regressions.

If asked to add tests, ask whether they should live in the repo (for example `tools/e2e/`); the user has not decided.

## Working with the user

- The user is building this to run many agent jobs unattended and review the branches later. Reliability and not wasting agent quota matter more than features.
- They prefer a short plan for large work and approve before big builds; for continuing work they say "continue". Report problems you found
  and fixed, and what you did not test, plainly.
- Their real projects are the git-ignored `projects/*.local.json` profiles (agent Muse). Do not start runs against them, or touch their
  branches, without being asked: runs create branches and commits there.
- Browser notifications only (no phone push). The user uses `agy` (Gemini) and Muse as well as Claude.

## What is built and what is left

Built: agents (Claude, agy, Muse, generic CLI) with live events and health checks; branch and worktree workspaces; Spec, Implement, Integrate,
Verify, Handover; gates, retry, pause, guidance, recovery of interrupted runs; guards; per-stage model and effort with presets; usage ledger
and budgets; Changes tab, images, board filters, archive and delete, grouped feed, browser notifications; auto modes; batches (queue and
compare); brief templates; branch cleanup; review branch; the delegation guide; Unity `.meta` commit before Handover (`UnityMeta`, tested with a fake agent only, not against a real Editor).
Continue a finished run on its own branch (`RunEngine.ReopenAsync`, `POST /api/runs/{id}/reopen`): same run, new round, tested with a fake agent.
Per-stage MCP servers for the claude agent (`mcp` in the profile, `StageContext.McpFor`, tested with Haiku); `guards.integrateAllowPaths` for scenes/prefabs in Integrate only. Launch Break now has core3mcp for Spec/Implement and a Verify stage (`core3mcp compile`); Muse cannot be given MCP servers yet (spike how: `muse exec -c mcpServers=...`).

Left, in rough order (see "Not built yet" in `docs/architecture.md`):
1. **Unity compile check** for Verify: spike whether MoonWalk can call unity-mcp over HTTP (`http://127.0.0.1:8080/mcp`: refresh, wait for
   compile, read console errors), else scan `Editor.log` for `error CS`. Needs the user's Editor open.
2. **Playtest stage**: spike first whether Autopilot (`ramenvr/autopilot`), `unity-cli-loop` or unity-mcp play mode can drive a VR rig; the stage
   would score the Spec's acceptance criteria and save screenshots as image artifacts (the artifact path already exists).
3. Move the end-to-end scripts into the repo (`tools/e2e/`) if the user agrees.
