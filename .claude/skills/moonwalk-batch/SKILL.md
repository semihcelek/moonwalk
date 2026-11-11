---
name: moonwalk-batch
description: Turn a list of goals, tasks or polish items into independent MoonWalk briefs and submit them as one queue (or a compare). Use when the user says "queue these", "batch these tasks", "give me tasks I can run with MoonWalk" or hands over several things to run unattended.
---

# Batch tasks into MoonWalk

Goal: several briefs, each independent and small, submitted as ONE batch to the running MoonWalk (`http://localhost:5870`).

## 1. Read the live facts first (never from memory)

- `GET /api/guide`: the brief format and rules. Follow it.
- `GET /api/projects`: pick the project; check `valid: true` and its `stages`, `baseBranch`, `agent`. Read its `briefingNotes` in the guide.
- `GET /api/runs` and `GET /api/batches`: see what is already running or waiting on that project (one run per project at a time in branch mode).

If MoonWalk is not answering, say so and stop. Do not start it unless asked.

## 2. Write the briefs

- One concern per brief, 4 to 12 lines, outcome in observable terms. The plan stage finds the files.
- **Independent**: every run starts from the base branch, so brief B cannot see brief A. If B needs A, merge them into one brief, or run A first and queue B after A is merged.
- Split by file overlap: two briefs that edit the same file will conflict when merged. Either merge them or note the overlap to the user.
- State what must not change. Name a doc or folder only if you saw it exist.
- No git actions, no secrets.

## 3. Choose settings (per stage)

- Spec is read-only. **Do not use `muse-ember-1.0` for Spec**: on 2026-11-11 every read-only Ember call got HTTP 404 ("does not exist or you lack access"). Use `muse-spark-1.3` for Spec; `muse-wonder-1.0` for big or hard-to-undo work.
- Implement: `muse-spark-1.3` for one feature, `muse-wonder-1.0` for large ones, `muse-ember-1.0` only for small bounded edits. **Ember was unreliable the same day**: it worked for 4 Implement runs, then 2 more failed with the same 404 about 30 s in with no tool calls. If a run fails with "does not exist or you lack access", retry it on `muse-spark-1.3` rather than assuming the brief is bad. Check `src/MoonWalk/Agents/ModelCatalog.cs` for the current ids.
- Overnight/unattended: `auto: {plan: true, implementation: true}`; add `mergeToReview: true` only if the user wants results merged into the review branch. `stopOnFailure: false` so one failure does not stop the rest.
- Tiny test batches: use the cheapest model and docs-only briefs.

## 4. Show the plan, then submit

Show the user the brief list (one line each), the stages/models and auto settings. Submit only after they agree, unless they already said to just do it. Real runs create branches and commits in the project's repo.

```powershell
$body = @{ name = "..."; project = "..."; kind = "queue"; briefs = @("...","...")
  stages = @{ Spec = @{agent="muse";model="muse-spark-1.3";effort="high"}; Implement = @{agent="muse";model="muse-ember-1.0";effort="medium"} }
  auto = @{ plan=$true; implementation=$true; mergeToReview=$true }; stopOnFailure = $false } | ConvertTo-Json -Depth 6
Invoke-RestMethod -Method Post -Uri http://localhost:5870/api/batches -ContentType 'application/json; charset=utf-8' `
  -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 30
```

Always pass `-TimeoutSec`. Save the body to a file in the scratchpad if it is long. Never put it in the project repo.

## 5. Verify it started

Wait ~20 s, then `GET /api/batches`. Item 1 should be `Running` with a `runId`. Report the batch id and what is pending.

## Rules learned

- There is **no append-to-batch** call. A second batch on the same project works (its first run waits for the workspace lock) but may jump ahead of the first batch's next item, because the lock is not FIFO. Put everything in one batch when order matters.
- Do not restart or kill MoonWalk while a batch runs. Reload profiles with `POST /api/projects/reload`.
- Cancel with `POST /api/batches/{id}/cancel`.
