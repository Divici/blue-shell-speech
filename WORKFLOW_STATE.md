# Workflow State

## Mode
Planning — interactive. Not yet in autonomous build.

## Current phase
Planning artifacts (presearch §31).

## Completed
- [x] Presearch read and locked — `presearch.md`, 2,036 lines
- [x] Hosting, AI vendor, and auth decisions resolved with research
- [x] Project governance — `CLAUDE.md`, `.claude/settings.json`
- [x] Safety hooks — protect-env, block-destructive-git (18/18 tests pass)
- [x] `slice-gauntlet` skill written to `~/.claude/skills/`
- [x] Memory bank seeded
- [x] Michelle's homepage edits captured

## Next action
Write the §31 planning artifacts, then per-slice acceptance criteria.

## Review lanes
Spec critic (every slice) · Security adversary (auth, PHI, API, dictation) ·
Visual gauntlet (homepage, app UI, dictation UX) · `/super-review` (every slice boundary).

## Resume protocol
Read in order: `CLAUDE.md` → `memory-bank/active-context.md` → `memory-bank/progress.md`.
`presearch.md` is the authority for anything those three do not settle.

## Spec drift
Changes to `presearch.md` are marked `[REVISED - Slice N]` inline and logged in
`memory-bank/progress.md` under "Decisions to revisit". Criteria never change silently —
`slice-gauntlet`'s first guardrail checks for exactly that.
