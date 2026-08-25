# Resume Here

A fresh session needs nothing from the previous conversation. Everything required is in
the repo.

## Bootstrap prompt

Start a **new** session in this directory (`claude`, not `--continue`) and paste:

```
Read CLAUDE.md, ORCHESTRATION.md, and WORK_QUEUE.md.

You are the ORCHESTRATOR. You do not build. You dispatch sub-agents, one task at a
time, and verify their work. Follow ORCHESTRATION.md exactly — it has the loop and
the briefs.

Then arm a recurring cron job (every 5 minutes, "2-59/5 * * * *") whose prompt is:

  AUTONOMOUS RUN — you are the orchestrator. Read ORCHESTRATION.md and WORK_QUEUE.md.
  Dispatch a sub-agent for the topmost unchecked task. Verify, tick, commit, and
  immediately dispatch the next. Do not build anything yourself. Do not stop to ask.
  Never end a turn saying you will continue.

Then dispatch the first sub-agent immediately.
```

Cron fires only while the session is idle, which is exactly when the loop would otherwise
have stalled. Jobs are session-only — they die when the session ends, and the queue file is
what survives.

## Why an orchestrator rather than one session doing the work

A session that builds everything itself accumulates every file, test run, and diff it
touches, and stops after a few hours. Restarting it needs a human. An orchestrator that
only dispatches and verifies grows by about one short report per task, so it covers most
of the queue in a single sitting. `ORCHESTRATION.md` has the full rationale.

## Context budget

The orchestrator will still fill eventually, around 30–40 tasks. When it does:

1. Make sure the current task is committed and its box ticked.
2. End the session.
3. Start a fresh one with the prompt above.

`WORK_QUEUE.md` is the only state that matters. Everything else is in git.

## The limit worth knowing

Neither approach survives the terminal closing. Cron fires inside a live session; a session
that has exited runs nothing. Leave the window open overnight — the orchestrator keeps
going on its own, but it has to still exist.

## State as of the last handoff

**Slices 0–5 complete and deployed.** Slice 6 partially built.

| | |
|---|---|
| Live | https://blueshell-dev-web.gentlesmoke-b0e719aa.eastus.azurecontainerapps.io |
| Sign-in | `michelle@blueshellspeech.example` |
| Password | Azure portal → `blueshell-dev-api` → Settings → Secrets → `seed-password` |
| Tests | 141 .NET · 134 web unit · 65 E2E · CI green |
| Repo | `github.com/Divici/blue-shell-speech` |

First sign-in forces MFA enrolment — scan the QR, save the recovery codes.

**Last completed:** WORK_QUEUE 1.1, visit scheduling UI.
**Next up:** WORK_QUEUE 1.2, the start-a-note entry point.

## Local prerequisites

- Docker Desktop running (Testcontainers for `dotnet test`)
- `az` on PATH: `/c/Program Files/Microsoft SDKs/Azure/CLI2/wbin`
- `dotnet` on PATH: `/c/Program Files/dotnet`, EF tools at `/c/Users/doa92/.dotnet/tools`
- `gh` on PATH: `/c/Program Files/GitHub CLI`
- Local SQL: `docker compose up -d sql`
- Force-push is blocked by a git hook, by design — it needs a human.

## Decision log

`DECISIONS.md` is **gitignored** — it exists locally with 60 entries and will not appear in
a fresh clone. Do not assume it is missing because it was never written. Keep appending to
it; it carries the reasoning behind everything above, plus an interview-talking-points
index at the bottom.
