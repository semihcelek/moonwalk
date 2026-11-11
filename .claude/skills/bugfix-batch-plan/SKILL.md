---
name: bugfix-batch-plan
description: Triage and plan several bug fixes at once before running them in MoonWalk. Groups bugs by root cause and file overlap, flags ones that need diagnosis first, and produces one independent brief per fix for review. Use for "plan these bugfixes", "I have N bugs, batch them", or a pasted bug list or issue list.
---

# Plan a batch of bugfixes

Output: a short triage table the user can approve, then briefs ready for the `moonwalk-batch` skill. Edit nothing yet. This is a planning step.

## 1. Collect each bug

For every bug get: symptom, repro, where it is seen (for netcode: host, client, both), and any log lines. If one is missing and cannot be found in the repo or logs, mark it **needs info** instead of guessing.

## 2. Triage (read-only, cheap)

For each bug, search the code for the likely area (owning module or folder, the 1-3 files most involved). Use Grep and targeted reads, not whole-folder reads. Then classify:

| Class | Meaning | What to do |
|---|---|---|
| **Clear** | Root cause visible in code or logs | One brief, with the evidence named |
| **Needs diagnosis** | Symptom known, cause not | Brief asks for a root-cause analysis and a fix only if the evidence is clear. Prefer Spec gate review |
| **Previously failed** | A fix was tried and did not hold | Do NOT queue another patch. Brief asks for root cause with file/line evidence and 2-3 options, no edits |
| **Duplicate** | Same cause as another bug | Merge into that bug's brief |
| **Needs info** | Cannot be reproduced or located | Ask the user, do not queue |

## 3. Check collisions

List the files or modules each fix is expected to touch. Two fixes touching the same file will conflict when merged into one review branch, so either **merge them into one brief** or **order them** (run one, merge, then queue the next). Fixes in different modules are safe to run in one batch. Scenes, prefabs and ProjectSettings are never edited by agents; if a fix needs one, the brief says to describe the wiring only.

## 4. Brief per fix

Use the format from `GET /api/guide`. Per bug: the symptom as "what is true when fixed", the repro, the evidence (log lines, file names you actually saw), constraints (what must not change), and "no new features". Keep each to 4-12 lines.

## 5. Present for approval

Show a table: `#`, bug, class, area, risk, model suggestion (Spec: `muse-spark-1.3`; Implement: `muse-spark-1.3` for most fixes, `muse-wonder-1.0` for architectural ones; `muse-ember-1.0` only for trivial one-file fixes). `muse-ember-1.0` fails for Spec and was flaky for Implement (see the `moonwalk-batch` skill), so default to Spark. List what you are NOT queuing and why. Wait for a yes, then hand off to `moonwalk-batch`.

## Rules

- Evidence first: logs and code, not the symptom description alone.
- Never invent paths, signal names or module names. Only name what you saw.
- Prefer the gate on Spec for `Needs diagnosis` bugs: the user reviews the plan before any code changes.
- Tell the user plainly what you could not verify.
