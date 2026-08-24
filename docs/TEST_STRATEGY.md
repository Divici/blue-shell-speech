# Test Strategy

From `presearch.md` §20. TDD per `~/.claude/rules/tdd.md`: red → green → refactor.

**Meaningful tests, not coverage theatre.** No coverage percentage is a target. The question is
always "would this test have caught a real defect," not "is this line executed."

**Exempt from TDD** (that rule's own non-behavioral clause): visual iteration inside a gauntlet
round, and AI output quality — which lives in the eval suite and does not gate CI.

---

## Layers

| Layer | Tool | What it proves |
|---|---|---|
| Domain unit | xUnit | Invariants hold with **no database and no framework** |
| Application unit | xUnit + NSubstitute | Use-case orchestration, error paths, cancellation |
| API integration | `WebApplicationFactory` + Testcontainers SQL | Real HTTP, real EF, real migrations |
| Component | Vitest + Testing Library | Rendering, interaction, states, a11y basics |
| E2E | Playwright | The flows Michelle actually performs |
| Eval | Custom harness | Model quality on synthetic corpora. **Never gates CI** |

**Integration tests run against real SQL Server in Testcontainers, never SQLite or InMemory.**
The provider differences that matter here — triggers, filtered unique indexes, `rowversion`,
`datetime2` — are exactly what an in-memory provider fakes away. A suite that passes on a
different engine than production proves the wrong thing.

---

## Security tests are not optional extras

These are ordinary CI tests. Adding an endpoint without them fails the build.

- **Cross-provider access on every endpoint.** Parameterized over the route table, so a new
  endpoint with no test is a build failure rather than an oversight. Expects **404, not 403** —
  a 403 confirms the resource exists.
- **Provider identity comes from the token.** A request supplying `providerId` in the body is
  rejected.
- **`Cache-Control: no-store` on every authenticated response.** Ranked #1 in the threat model.
- **No PHI in logs.** Serialize every PHI-bearing entity through the logging pipeline; assert no
  clinical value appears.
- **Browser storage is empty.** `localStorage` and `sessionStorage` asserted empty after a full
  dictation flow.
- **Audit immutability.** Attempt `UPDATE` and `DELETE` on `AuditEvent` as the app principal;
  both must fail.
- **Signed-note immutability.** Attempt a direct SQL `UPDATE` of SOAP fields on a signed note;
  the trigger must reject it.

---

## Domain tests carry the clinical rules

`Practice.Domain` references nothing, so these run in milliseconds with no infrastructure:

- A signed note cannot transition back to draft.
- An amendment without a reason is rejected.
- An amendment increments the version and preserves the prior row.
- Exactly one current note per appointment.
- An observation with no transcript span is invalid.
- `trialsCorrect > trialsAttempted` is invalid.
- A goal absent from all three buckets violates the completeness invariant.

**These are the rules protecting a child's medical record.** They belong where they cannot be
bypassed, and where they are cheap enough to run on every save.

---

## Frontend

Component tests cover rendering, interaction, **loading / empty / error states**, and keyboard
access. States are behaviour, not polish, so they are tested like behaviour.

Playwright E2E on real flows:

1. Public site: browse → consultation → submit → success
2. Login with MFA → dashboard
3. Create patient → add guardian → add goal
4. Schedule appointment → daily view
5. Manual SOAP note → sign → attempt edit (must fail) → amend
6. **Full dictation:** record → multiple takes → upload → poll → review chips → sign
7. **Offline:** record with network disabled → draft persists → reconnect → syncs
8. Accessibility sweep (axe) on every page

Run against Chromium **and WebKit**. The iOS constraints in `ARCHITECTURE.md` are Safari
behaviours; testing only Chromium tests the one browser whose limitations do not apply.

---

## Eval suite (§8.2)

Separate from CI. Synthetic corpus only, enforced by the `IsPhiEligible` guard (D019).

Reported per model: WER · clinical terminology · **AAC terminology** · **numeric accuracy** ·
cue-level accuracy · proper-name accuracy · noisy-environment degradation · false-start handling
· extraction precision/recall · **fabrication rate** · de-identification recall.

**Numeric accuracy and fabrication rate are the headline metrics.** A transcript with excellent
WER that renders "sixty percent" as "sixteen percent" is a dangerous transcript. WER alone hides
exactly the errors that matter clinically.

---

## What is deliberately not tested

- Third-party library internals.
- Azure platform behaviour — that resources exist is deploy verification, not a unit test.
- Exact prose of generated notes. Non-deterministic by nature: **structure and provenance are
  asserted, wording is not.**
- Visual pixel diffs. The visual gauntlet lane covers appearance; screenshot tests against a
  design still in flux produce noise, not signal.
