# Orchestration Protocol

How to run `WORK_QUEUE.md` unattended without the driving session's context filling up.

## The problem this solves

A single session that does all the work accumulates every file it reads, every test run,
and every diff. It exhausts its context after a few hours and stops — and restarting it
requires a human to paste a prompt. Overnight, that means the work stops at 1am and waits.

## The shape

**The orchestrator does no building.** It holds the queue, dispatches one task at a time to
a fresh sub-agent, verifies the result, ticks the box, and moves on. Its context grows by
roughly one short report per task instead of by everything the task touched.

```
orchestrator  ──dispatch──►  builder sub-agent   (fresh context, does the work)
      ▲                            │
      │                            ▼
      └──── report ◄──── commits, pushes, returns a summary
      │
      └──dispatch──►  reviewer sub-agent  (fresh context, reads the diff only)
```

A sub-agent's tool output does **not** enter the orchestrator's context — only its final
report does. That is the whole reason this works.

## One builder at a time — this is forced, not preferred

Sub-agents run in the **same working tree**, not an isolated worktree. A second builder
would not merely race on the queue file; it would see and stage the first one's
half-finished edits. Before dispatching, or before touching any file yourself, run
`git status --porcelain` — a dirty tree means a builder owns it, so keep your hands off.

A **reviewer** is safe to run alongside a builder, because it is read-only and works from a
fixed commit (`git show <sha>`). Tell it so explicitly, or it will report the builder's
in-progress edits as findings.

When a reviewer returns findings while a builder holds the tree, write them to the session
scratchpad rather than the repo, and fold them into `WORK_QUEUE.md` once the tree is clean.

## Loop, per task

1. Read `WORK_QUEUE.md`. Take the topmost unchecked task.
2. Dispatch a **builder** with the brief below.
3. When it reports, verify cheaply — `git log --oneline -1`, `gh run list --limit 1`.
   Do not re-read the diff yourself; that is what the reviewer is for.
4. On a substantive task (new endpoint, new entity, security-relevant change), dispatch a
   **reviewer**. Skip it for cleanup and docs — a review costs a whole agent and finds
   nothing on a file deletion.
5. If the reviewer returns confirmed findings, dispatch a builder to fix them, then
   re-verify. Cap at one fix round; if findings remain, record them in the queue as a
   follow-up task rather than looping.
6. Tick the box, append to the Log, commit the queue update.
7. Immediately take the next task. Do not stop to summarise.

## Builder brief

Give a builder exactly this, with `<TASK>` filled in. Keep it short — the point is that
the sub-agent reads the repo, not that you re-explain the repo.

```
You are building one task in an existing, mature codebase. Read these first, in order:

  CLAUDE.md              non-negotiables, stack, conventions
  WORK_QUEUE.md          your task and the rules
  DECISIONS.md           ~60 prior decisions. GITIGNORED — it exists locally.
                         Read it. Do not contradict it silently.
  docs/ARCHITECTURE.md   only if your task touches the API or the BFF boundary
  docs/DATA_MODEL.md     only if your task touches persistence
  docs/SECURITY.md       only if your task touches auth, PHI, or logging

YOUR TASK: <TASK>

Rules:
- TDD. Write the failing test first; run it; then implement.
- Match the surrounding code's style, comment density, and rigour. The bar is high —
  read a neighbouring file before writing a new one.
- Synthetic data only. No PHI anywhere, ever.
- Full gate before committing: in /web `npm run lint && npm run typecheck && npx vitest run`
  (plus Playwright if a route or component changed); in /api `dotnet test`.
- Docker must be running for dotnet test. Start Docker Desktop if it is not.
- Commit with a short imperative lowercase message. NO AI attribution, no Co-Authored-By.
- Push to main.
- If a decision comes up that DECISIONS.md does not cover and that would be expensive to
  reverse, append an entry to DECISIONS.md explaining what you chose, what lost, and what
  it cost.

Report back in under 200 words: what you built, the commit sha, test counts, and anything
you could not do. Do not paste code.
```

## Reviewer brief

```
Review the most recent commit on main in this repo. Read CLAUDE.md and DECISIONS.md for
the standards it is held to, then `git show HEAD`.

Look for: correctness bugs, security gaps against docs/THREAT_MODEL.md, PHI reaching logs
or browser storage, missing tests for the behaviour just added, and contradictions with
DECISIONS.md.

A finding must ship with a concrete failure scenario — inputs and state that produce a
wrong result. "This could be better" is not a finding.

Report at most 5 findings, most severe first, each in two sentences. If the commit is
sound, say so in one line. Do not fix anything.
```

## Keeping the orchestrator small

- Never read source files. Dispatch instead.
- Never run the test suite yourself. The builder does; you check CI.
- Never paste diffs into your own context.
- If you find yourself investigating a bug, stop and dispatch a builder to investigate.

## When the orchestrator's context fills anyway

It will, eventually — around 30–40 tasks. When it does:

1. Finish the current task and commit the queue update.
2. Say so plainly and stop.

The queue file is the state. A fresh orchestrator resumes from `RESUME.md` with no loss.

## Cron as the safety net

A recurring cron job (`2-59/5 * * * *`) fires only while the session is idle — which is
exactly the moment the loop would otherwise have stalled. It re-enters this protocol. It
does not replace the loop; it catches it when it drops.

Cron jobs are session-only and die with the session.

## What NOT to delegate

- Anything in the **Blocked** section of `WORK_QUEUE.md`. Those need David.
- Force-pushes and history rewrites. A git hook blocks them, deliberately.
- Azure spend, quota requests, BAA decisions, go-live sign-off.
