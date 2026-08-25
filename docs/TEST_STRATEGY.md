# Test Strategy

From `presearch.md` §20. TDD per `~/.claude/rules/tdd.md`: red → green → refactor.

**Meaningful tests, not coverage theatre.** No coverage percentage is a target. The question is
always "would this test have caught a real defect," not "is this line executed."

---

## The control-deletion protocol

**A test that names a control must fail when that control is deleted. Find out by deleting it
and running the test — not by reading the assertion and agreeing with yourself.**

Five tests in this codebase have claimed a control in their comment and asserted something
weaker (D042 #2, D061, D066 F3 and F4, D070). Every one was green. Green is not the signal;
*going red on demand* is.

For every test written or changed:

1. Delete the control it names — the clause, the filter, the middleware registration, the line.
2. Run the test. Read the failure message.
3. Restore the control.
4. Write what you read into the test's docstring, on a `Control:` line:

       /// Control: ClinicalNote.CanBeDiscarded — the SupersedesNoteId clause.
       /// Deleted → red on the final assertion, "Assert.False() Failure — Expected: False,
       /// Actual: True".

That line is **evidence the deletion happened**: the message cannot be written without
running it. It is also greppable, so a test asserting a control without one is a reviewable
omission rather than something nobody notices.

If deleting the control leaves the test green, one of two things is true and both are worth
knowing before an incident rather than during one: the assertion is aimed somewhere else, or
a second control is quietly covering for the first. Both usually mean the honest test case is
unreachable through the API and has to be constructed directly — see D066 for two worked
examples, and `An_empty_signed_note_cannot_be_deleted_by_raw_sql` for a third: the DELETE
trigger's `Status` clause and its emptiness clauses each cover for the other on every note the
API can produce, so isolating one needed a signed note with four empty sections planted by raw
`INSERT`, which `Sign()` will not create.

**A `Control:` line names a symbol, so moving the symbol invalidates the line.** Re-run the
deletion against the new home as part of the move. Nothing mechanical will notice: the line is
a comment and the test stays green either way. Same rule for renaming a clause, changing the
order of a branch, or lifting a block into a helper (D077).

### The control for a database constraint is the MIGRATION

**The test database is built by running migrations** (`SqlServerFixture`, `Database.MigrateAsync`).
So the EF configuration is not the control for anything the database enforces:

- Deleting `.IsUnique()` from a `*Configuration` class changes the *model*. The index is
  already on the table, put there by the migration, and the test stays green.
- The same goes for `HasCheckConstraint`, `HasTrigger`, column types, and `IsRequired`.

Delete it **from the migration that actually built the object**, and say which migration on the
`Control:` line.

**"The migration" is not always the one that introduced it.** SQL objects created with
`CREATE OR ALTER` are re-created by every later migration that touches them, and the last one
to run is the only definition the database ends up with:
`TR_ClinicalNotes_PreventDeletingRealNotes` is defined three times — in
`ClinicalNoteDeletionGuard.Up`, in `AmendmentDeletionGuard.Up`, and again in
`AmendmentDeletionGuard.Down` — and only the second one is live. All four tests naming that
trigger stayed green with the emptiness clauses deleted from the first, which is a control
deletion that verified nothing. **Grep the migration folder for the object's name before
deleting anything from it.**

Same reasoning as the rest of this section, one layer down: the question is never "is this
line in the repository", it is "is this line what the database is running".

### A guard over a SET enumerates the set, or it is not a guard

**A test that means "all of them" and holds a hard-coded list is a test about the day it was
written.** It stays green when the set grows, which is exactly when it was supposed to speak.
Five have shipped here (D090's sweep):

- the BFF timeout guard listed five client modules; `lib/api/enquiries.ts` arrived and was
  checked by nothing;
- the E2E route guard listed six of nine authenticated pages, under a comment claiming a new
  page is covered "by existing there";
- `cache: "no-store"` had no cross-file guard at all, only per-file assertions in two of
  seven modules;
- the Application architecture test used a **denylist** of the projects that existed when it
  was written, so a reference to a new one passed;
- `docs/TEST_STRATEGY.md` itself claimed the cross-provider tests were parameterized over the
  route table. They are not.

So: **walk the directory, enumerate the route table, read the assembly, match a naming
convention — derive the set from the thing itself.** Where that is impossible, say so in the
docstring rather than implying otherwise, and name what would close it.

Two further rules the first four of those taught:

- **Prefer an allowlist.** "Anything but X" grows with the codebase; "only Y" does not.
- **A walk that finds nothing registers no tests and the file is green.** Assert a floor on
  what the walk found, in its own `it`/`[Fact]`, or a rename silently deletes the guard.
  Assert a *floor*, never an exact count — an exact count is the list again.

**Deliberately not automated.** A mutation harness in CI would gate the build on a score,
which is the coverage-threshold failure mode this file rejects, and CLAUDE.md keeps quality
signals of this kind out of CI. A hook or a lint rule can only check that the sentence exists
— which is exactly what was already there and already wrong. The reasoning is in D070.

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

- **Cross-provider access on every endpoint.** One test per endpoint, hand-written, in
  `PatientIsolationTests`, `NoteImmutabilityTests`, `SchedulingTests` and
  `ConsultationInboxTests`. Expects **404, not 403** — a 403 confirms the resource exists.
  **This line used to say "parameterized over the route table, so a new endpoint with no test
  is a build failure rather than an oversight". It is not, and never was.** Nothing enumerates
  `EndpointDataSource`, so an endpoint added without a tenancy test is an ordinary oversight
  that nothing catches — and a document claiming automatic coverage is worse than one claiming
  none, because it stops the next person checking (D072's class, D090's sweep). The
  route-table version is real work and is queued as WORK_QUEUE 4.8.
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
