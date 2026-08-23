# Progress

## Slices (presearch §31)

| # | Slice | Status |
|---|---|---|
| 1 | Public website + deployment | Not started |
| 2 | Provider authentication | Not started |
| 3 | Patient CRUD end-to-end | Not started |
| 4 | Scheduling end-to-end | Not started |
| 5 | Goals + manual SOAP notes | Not started |
| 6 | Audio capture + transcription | Not started |
| 7 | Structured extraction + validation | Not started |
| 8 | SOAP generation + approval | Not started |
| 9 | Audit / capacity / security hardening | Not started |
| 10 | Production-readiness verification | Not started |

Every slice needs written acceptance criteria before its builder starts.
`slice-gauntlet` refuses to run without them, by design.

## Sequenced later — designed in, not cut

The data model and interfaces must accommodate all four now:

1. Document / file upload
2. Evaluation reports (appointment type ships; formal report authoring later)
3. Superbill PDF generation (`Encounter` entity ships now)
4. Live Azure Cost Management API for the capacity banner (threshold logic ships against
   internal counters)

## Known issues

- **No PHI-safe model deployment.** Only `GlobalStandard.gpt-5-mini` has quota; every
  DataZoneStandard and regional Standard quota for the gpt-5 family is 0. Dev-only until resolved.
- The §8.2 model benchmark cannot run yet — a second model needs quota first.

## Decisions to revisit

- Cold-start measurement is a **deliverable**, not a nice-to-have. Record the real number.
- Testimonials are deleted, not deferred. Do not reintroduce placeholder reviews.
