# Resume Here

A fresh session needs nothing from the previous conversation. Everything required is in
the repo.

## Bootstrap prompt

Start a **new** session in this directory (`claude`, not `--continue`) and paste:

```
Read CLAUDE.md, WORK_QUEUE.md, and memory-bank/active-context.md.

Then set up an autonomous loop: create a recurring cron job (every 5 minutes,
"2-59/5 * * * *") whose prompt is:

  AUTONOMOUS RUN — do not reply with a plan, do the work. Read WORK_QUEUE.md.
  Take the topmost unchecked task. Complete it fully: code, tests, lint, typecheck,
  commit, push. Tick the box, append to the Log, and immediately start the next task
  in the same turn. If a task needs David, move it to Blocked with the reason and take
  the next one. Never stop to ask. Never end a turn saying you will continue.
  Docker must be running for dotnet test. Synthetic data only. No AI attribution in
  commit messages.

Then start on the topmost unchecked task immediately.
```

Cron fires only while the session is idle, which is exactly when work would otherwise
stall. Jobs are session-only — they die when the session ends, and the queue file is what
survives.

## Context budget

Each session will exhaust its context after a few hours of building. When it does:

1. Make sure the current task is committed and its box ticked.
2. End the session.
3. Start a fresh one with the prompt above.

`WORK_QUEUE.md` is the only state that matters. Everything else is in git.

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
