# Autonomous Work Queue

**Purpose:** this file is the resume point. Any invocation — a cron wake-up, a fresh
session, a continued one — reads this, takes the topmost unchecked task, finishes it
completely, ticks it, and moves on.

## Rules for whoever is working

1. **Finish a task completely before ticking it.** Code, tests, lint, typecheck, commit,
   push. A ticked box means CI-green and pushed, not "written".
2. **Never stop to ask a question.** If a task turns out to need David, move it to
   *Blocked* at the bottom with the reason, and take the next one.
3. **Never say "continuing" and stop.** Either do the next task or tick nothing.
4. **Commit per task**, with the project's message style and no AI attribution.
5. **Run the full gate before pushing:** `npm run lint && npm run typecheck && npx vitest run`
   in `/web`, `dotnet test` in `/api`. Playwright when a route or component changed.
6. **Docker must be running** for `dotnet test` (Testcontainers). Start it if it is not.
7. Update this file in the same commit as the work it describes.
8. **Before committing, run the control-deletion protocol on every test you wrote or
   changed:** delete the control the test names, run it, read the failure, restore the
   control, and record what you read on a `Control:` line in the test's docstring. A test
   that stays green with its control deleted is not a test. Five have already shipped that
   way — `docs/TEST_STRATEGY.md` has the procedure, D070 has the reasoning.

---

## Phase 1 — Usability gaps

The app has more API than UI. These are the cheapest work with the highest visible
return: every one is a form against an endpoint that already exists and is already tested.

- [x] **1.1 Appointment creation UI** — `/today` and a patient page can schedule a visit.
      Patient picker, type, date/time in practice-local, duration, travel block, notes.
      Surface the 409 conflict message (including the travel-time case) as readable text.
- [x] **1.2 Start-a-note entry point** — from a visit on `/today`, create or open its
      note. Currently `/notes/[publicId]` is reachable only by typing a URL.
- [x] **1.3 Goals UI** — list, add, mark met, discontinue, on a patient page. AAC fields
      appear only when the domain is AAC (the aggregate and a CHECK both enforce it).
- [x] **1.4 Guardian + address forms** — add/edit on a patient page. `HasLegalAuthority`
      must be its own explicit control, never inferred from primary contact.
- [x] **1.5 `ConsultationRequest` entity + persistence** — closes slice 1's one unmet
      criterion. Includes the **contentless** notification ("New consultation request,
      sign in to view") and `SourceIpHash`. Wire `app/consultation/actions.ts`, removing
      its `TODO(slice 3)`.
- [ ] **1.6 `Encounter` + `ResourceDocument` entities** — ship empty per the scope ledger.
      Adding a billing table to a live clinical database later means backfilling history.
- [ ] **1.13 Consultation inbox, and a notification that is actually sent** — 1.5 left two
      things visible rather than implied, and this is where they land.
      - The notification says "sign in to view" and **there is nothing to view**. A list of
        enquiries, a detail view, the `New`/`Contacted`/`Converted`/`Declined` transitions
        the aggregate already holds, and convert-to-patient wired to `ConvertTo`. Reading an
        enquiry is a read of PHI-adjacent data and must be audited on the endpoint the
        product actually calls (D065), not on a sibling.
      - The notification is **composed and logged, not sent**:
        `LoggingConsultationNotifier` exists because the practice has no mailbox. Blocked on
        "real practice phone and email" and on the domain purchase a verified sender needs.
        The seam (`IConsultationNotifier`) is deliberately unable to carry content, so the
        transport is a new class and nothing else (D079).

- [ ] **1.7 Remove Next template assets** — `next.svg`, `vercel.svg`, `globe.svg`,
      `file.svg`, `window.svg` in `web/public/`.
- [ ] **1.8 `/health/ready` dependency checks** — register SQL and blob under the "ready"
      tag. Removes the `TODO(slice 3)` in `Program.cs`, and **flips the test that pins
      zero checks** — that failure is the reminder, by design.
- [x] **1.9 Four failing `mobile-safari` E2E tests on the public site** — pre-existing,
      confirmed against a clean tree while working 1.2, so not a regression from any
      recent slice. `homepage.spec.ts:63` and `:212` cannot find the header's `About`
      link at an iPhone 14 viewport; `:172` and `:183` (consultation submissions) fail
      only under full-suite parallel load. **A suite with known reds stops being a
      signal** — either the nav is genuinely unreachable on a phone, which is a real
      defect on the page parents actually land on, or the test is desktop-shaped like the
      one in D040.
- [x] **1.10 Fix the four reviewer findings against `0d75f37`** — run this BEFORE the
      remaining Phase 1 forms; F2 is a compliance gap, not a polish item.
      - **F1** "Start note" renders on every undocumented visit, including `Cancelled`,
        `NoShow`, and future ones, and posts four empty strings. `Sign()` refuses an empty
        note, no DELETE endpoint exists, and the filtered unique index blocks replacement —
        so one mis-tap leaves a permanent "Draft" badge that can only be cleared by writing
        content onto that child's chart and signing it into immutability.
      - **F2** Every note read now routes through `GetNoteHistory`, which injects no
        `IAuditWriter` and returns full S/O/A/P. The audited endpoint
        (`GetNoteForAppointment`, which writes `PatientViewed`) is now reached only in the
        409 race. Contradicts `docs/SECURITY.md` §Audit and D012.
      - **F3** `Starting_a_note_on_another_providers_visit_reveals_nothing` counts by the
        seeded note's own unique `PublicId`, so it returns 1 whatever the stranger's POST
        did; the fixture already has a note, so the genuinely exploitable case — a foreign
        visit with **no** note — is never exercised. Count on `AppointmentId` instead.
      - **F4** `The_daily_view_carries_no_note_from_another_provider` asserts
        `Assert.Empty(day.Visits)`, so deleting the `ClinicalNote` query filter outright
        leaves it green, despite its comment claiming two independent scopes.
      - Record the standing rule in `DECISIONS.md`: this is the **third** time a test here
        has asserted a weaker claim than its comment states — see D042 finding #2 and D061.
        A tenancy test must fail when the filter it names is deleted.

- [x] **1.11 Fix the five reviewer findings against `098d41c`** — **F1 is severe: it
      destroys navigational access to a signed clinical note.** Run this before any
      remaining Phase 1 item. Full detail, including the confirmed-clean list, is in the
      reviewer report; the essentials:
      - **F1** Discard reaches an amendment draft and strands the signed version it
        supersedes. Sign v1 → `POST /notes/{v1}/amend` → v2 (Draft, content copied) →
        `PUT /notes/{v2}` with four empty strings (allowed: v2 is a Draft, and
        `TR_ClinicalNotes_PreventSignedEdits` only guards `Status <> 1`) →
        `DELETE /notes/{v2}` passes all four layers, because `CanBeDiscarded`, the
        endpoint, the query filter, and `TR_ClinicalNotes_PreventDeletingRealNotes` all
        check Status and emptiness and **never examine `SupersedesNoteId`**. v1 is left
        `Amended, IsCurrent = 0`, so the visit has no current note: the day card offers
        "Start note" again, `GET /notes/appointment/{visit}` 404s, and the signed record is
        unreachable through the product's only navigation. `isEmptyNote()` even re-renders
        "Discard this empty note" on the cleared amendment, leading the user there.
        Fix in the aggregate, the endpoint, AND the trigger — a guard in one is not a guard
        in all.
      - **F2** `An_amendment_is_never_discardable` is green by construction — the D066
        defect, **fourth occurrence**, inside the commit that established D066. It asserts
        on `Signed().Amend(...)` while content is still the copied original. Adding
        `amendment.UpdateContent("", "", "", "")` before the assertion turns it red against
        the code as committed. Its docstring is also false: nothing obliges an amendment to
        be signed.
      - **F3** `NoteDiscarded` is written outside the delete's transaction, on the request's
        cancellation token. `SaveChangesAsync(ct)` commits the delete, then `audit.WriteAsync`
        is a second save; there is no `BeginTransaction` in the API. Background the PWA
        mid-request and the row is gone with nothing in `AuditEvents`.
      - **F4** Every refused delete is unaudited — 409 writes nothing, and the raw-SQL
        `THROW 50002` propagates unhandled (no `UseExceptionHandler`/`AddProblemDetails` in
        `api/src`). Walking note `PublicId`s with DELETE leaves zero evidence.
      - **F5** `DayVisit.StartUtc` is `DateTimeKind.Unspecified` from `datetime2`, so it
        serialises **without a `Z`**. `new Date(visit.startUtc)` in
        `web/lib/visit-documentation.ts` parses it as process-local time; under
        `TZ=America/New_York` a 09:00 ET visit reads "This visit has not started yet" for
        four hours after it ended, with no button and no override. Every fixture in
        `visit-documentation.test.ts` and `VisitCard.test.tsx` hardcodes a `Z` the endpoint
        never sends, so the suite cannot see it. Fix the serialisation at the source and
        make at least one test consume a real endpoint payload.

- [x] **1.12 Fix the five reviewer findings against `a4d6ff5`** — run before 1.5. **F2
      must land before any second transaction is written anywhere in the API**, because
      D071 names that block as the pattern to copy.
      - **Fix the CLASS, not the reported instance.** Four rounds running, a fix has closed
        the named case and left an identical sibling open. Enumerate every call site of the
        pattern before claiming a finding closed.
      - **F1** `AuditRefusedDiscardAsync` passes the endpoint's `ct`
        (`HttpContext.RequestAborted`) into `SaveChangesAsync`, so a client that sends
        `DELETE /notes/{guid}` and drops the connection leaves nothing behind. Walk 10,000
        ids that way and `AuditEvents` is empty. This is the survivorship bias D071 fixed on
        the success path, left on all four refusal paths — including the `not-found` one
        that `docs/SECURITY.md`, amended in the same commit, calls "the only place the
        attempt is recorded at all."
      - **F2** The retried transaction body is not idempotent. `AuditEvent.Record(...)` is
        constructed INSIDE the `strategy.ExecuteAsync` lambda; a transient failure of the
        audit save leaves it `Added` (a failed save never calls `AcceptAllChanges`), the
        transaction rolls back, the lambda re-runs, and the next save inserts BOTH. Two
        `Success` discard rows for one deletion, in a table the app principal cannot UPDATE
        or DELETE. The retry also re-`Remove`s an entity EF has already detached.
      - **F3** `SupersedesNoteId` is checked before `CanBeDiscarded`, so every signed note
        from v2 on audits as `refused;reason=amendment` rather than `signed`. A query for
        "attempts to delete a signed clinical record" undercuts by exactly the set of
        amended — i.e. contested — records. The clinician copy is wrong in the same case: a
        superseded v1 is told "amend it instead," which `Amend()` then refuses.
      - **F4** `Program.cs:27` claims the exception message and the caller's `traceId` "are
        joined by Serilog." `git grep -in serilog -- api/` returns only that comment — no
        package, no sink, no `IncludeScopes`. Michelle gets a trace id nothing can look up.
        Either wire it or delete the claim; note 4.1 already owns the Serilog work.
      - **F5** A `Control:` line in `NoteEditor.test.tsx` is a paraphrase naming a clause
        (`!note.isAmendment`) that does not exist; the real line is
        `if (note.isAmendment) return false;` and it produces a different message. D070's
        whole argument is that the line cannot be written without running the deletion.
        **Audit every `Control:` line added in `a4d6ff5` the same way**, not just this one.

## Phase 2 — Slice 6, dictation

- [ ] **2.1 PWA shell** — `manifest.ts`, icons, service worker, offline shell.
- [ ] **2.2 Install prompt + standalone detection** — only home-screen PWAs escape iOS's
      7-day storage eviction. Explain the durability limit to the user rather than
      assuming drafts are safe.
- [ ] **2.3 `DictationSession` + `DictationTake` entities + migration** — including the
      `CHECK (DurationSeconds <= 300)` constraint, the status enum, and `BlobDeletedAtUtc`.
- [ ] **2.4 Recording UI** — one button toggling pause/resume, elapsed timer, takes list,
      auto-stop at the cap. No visual interaction required once recording starts (§7.7).
- [ ] **2.5 Chunked resumable upload + blob storage** — a 9.6 MB take must survive a
      dropped connection. Managed identity to the `session-audio` container.
- [ ] **2.6 Background job + status polling** — queue-driven, co-located in `api` (D014).
      Status enum surfaced meaningfully: `Transcribing` is not `Generating`.
- [ ] **2.7 Server-side transcode to 16 kHz PCM** — iOS emits mp4/AAC, Azure Speech wants
      PCM.
- [ ] **2.8 `ITranscriptionService` + Azure Speech implementation** — behind the provider
      seam, with the `IsPhiEligible` guard.
- [ ] **2.9 Failure paths** — transcription down preserves audio, offers retry, allows
      manual entry (§19).
- [ ] **2.10 Audio retention** — deleted on signature, 30-day hard cap, deletion audited.
- [ ] **2.11 Background Sync feature detection** — Safari has none. Fall back to
      sync-on-foreground plus an `online` retry.

## Phase 3 — Slices 7 and 8, the AI pipeline

Buildable and testable against synthetic data only — blocker #1 means the sole model
deployment is `GlobalStandard`, and §22 forbids real data regardless.

- [ ] **3.1 De-identification** — roster-first (patient and guardians are known rows), NER
      second. Token map in memory only, never persisted or logged.
- [ ] **3.2 `ExtractedObservation` entity** — every quantitative field NULLABLE with no
      default. Null means *not stated*.
- [ ] **3.3 `IClinicalExtractionService`** — strict JSON schema, not "return JSON" in a
      prompt. `sourceQuote` + `sourceOffset` required on every claim.
- [ ] **3.4 Deterministic validation gate** — no model involved. Unresolvable offset
      rejects the claim; rejections degrade to *missing*, never to a substituted value.
      Completeness invariant: every active goal appears in addressed ∪ notAddressed ∪
      missing.
- [ ] **3.5 Missing-info analysis + review chips** — fillable by typing or by tapping to
      speak. Never a suggested clinical value.
- [ ] **3.6 `IClinicalNoteGenerationService`** — receives validated structure only, never
      the transcript (D016).
- [ ] **3.7 Numeric-provenance check** — every number in the output must trace to
      validated input, or the job fails with no draft produced.
- [ ] **3.8 OpenRouter provider that throws on non-synthetic data** (D019).
- [ ] **3.9 Synthetic eval corpus + harness** — numeric accuracy and fabrication rate
      reported separately from WER. Does not gate CI.

## Phase 4 — Slice 9, hardening

- [ ] **4.1 Serilog destructuring policy** — redacts PHI-bearing types, plus a test that
      serialises every such entity and asserts no clinical value appears.
- [ ] **4.2 Nonce-based CSP for the authenticated app** — the public site's
      `unsafe-inline` deviation does not extend here (D042).
- [ ] **4.3 Rate limiting on login and dictation upload.**
- [ ] **4.4 `web` → `api` caller identity** — currently network isolation alone, weaker
      than `THREAT_MODEL.md` boundary 2 specifies.
- [ ] **4.5 Capacity banner + admin alerts** — against internal counters (§13).
- [ ] **4.6 Alert on overdue audio deletion** — a silently failing lifecycle job looks
      exactly like a working one.
- [ ] **4.7 Audit completeness test** — every event type in `SECURITY.md` emitted and
      queryable; verify the app principal cannot UPDATE or DELETE `AuditEvents`.

## Phase 5 — Documentation and review

- [ ] **5.1 `HIPAA_DATA_FLOW.md`** — every hop that touches PHI.
- [ ] **5.2 `API_SPEC.md`** — now that the endpoints exist.
- [ ] **5.3 `UX_FLOWS.md`** — now that the screens exist.
- [ ] **5.4 `PRD.md`.**
- [ ] **5.5 Security risk analysis draft (§14.6)** — everything that does not require
      David's sign-off.
- [ ] **5.6 Vendor review table (§14.5)** — every service touching ePHI.
- [ ] **5.7 Maryland retention research** — authoritative sources, minors' records.
- [ ] **5.8 `/super-review` on slices 2–6**, fix every confirmed finding.
- [ ] **5.9 Authenticated-screen design pass** — no comps exist; build in the established
      language and record it.
- [ ] **5.10 Update `STUDY_GUIDE.md`** — interview prep, per the global rule.

---

## Blocked — needs David

Do not attempt these. Recorded so nothing is silently dropped.

- Buy the practice domain → unblocks the CDN (blocker #6) and a real contact address.
- Upgrade Azure to Pay-As-You-Go under the practice identity → unblocks blockers #1, #4, #5.
- Request `DataZoneStandard` quota → unblocks a PHI-safe model deployment.
- Real practice phone and email.
- A timed dictation of a **fictional** patient → eval-corpus fixture #1.
- `$20`/month budget with 50/80/100% alerts.
- Confirm Container Apps HIPAA eligibility, or approve the App Service swap.
- Sign-off on residual risk if Modified Abuse Monitoring is refused.
- Final go-live sign-off (§34).

---

## Log

Append one line per completed task: date, task id, commit sha.

- 2026-08-25 · 1.1 appointment creation UI · practice-local to UTC conversion tested across both DST boundaries; 409 conflict surfaces the clashing visit time
- 2026-08-24 · 1.2 start-a-note entry point · `DayVisit` carries the current note's id and status, resolved in the day query as one OUTER APPLY rather than a request per card; `startNote` server action creates the draft and treats the API's 409 as "open the one that exists", so a duplicate clinical record stays impossible outside the UI too
- 2026-08-24 · 1.3 goals UI · the AAC fields are **unmounted** on a non-AAC domain rather than hidden, and the BFF refuses the combination rather than blanking it, so the form asks the same question the aggregate and `CK_Goals_AacFieldsOnlyOnAacGoals` ask; marking met and discontinuing are transitions with no delete anywhere in the chain, and closed goals stay on the page. Found while testing: the goals projection translated `Nullable<TEnum>.ToString()` into SQL and returned `""` where the DTO promised `null`, so "no cue level recorded" arrived as a value
- 2026-08-25 · 1.10 four reviewer findings · **F1** a note can only be started on a visit that has begun and was not cancelled or marked a no-show (`Appointment.DocumentationBlockedReason`, mirrored in `web/lib/visit-documentation.ts` so the card explains rather than offers), and an unsigned draft with nothing in any section can be discarded — endpoint, aggregate, and a new DELETE trigger all say the same thing, audited as `NoteDiscarded` (D064). **F2** `GetNoteHistory` audits the read it performs, recording how many versions were disclosed; that endpoint is the one the product actually opens notes through, and it was writing nothing (D065). **F3/F4** both tenancy tests were counting the wrong thing — F3 on a unique `PublicId` that answered 1 regardless, F4 on a collection the Appointment filter had already emptied. Each was rewritten to need a row the API cannot produce, then **verified by deleting the filter it names and watching it fail** (D066). Splitting F4 in two came out of that: deleting the Appointment filter left it green because the day query's join to `Patients` was covering for it
- 2026-08-25 · 1.9 four failing mobile-safari E2E tests · **the site was at fault on the nav pair and the tests on the consultation pair.** The disclosure panel rendered after `</nav>`, so on a phone — where the inline list is `display:none` — the `Main` navigation landmark held a logo and a button and the site's actual navigation was outside it; moved inside, and the tests now reach the links through the disclosure at whatever width they run rather than skipping below `md` (D067). The consultation pair was one shared rate-limit bucket, not a race: with no `x-forwarded-for` on localhost every test in every project keys to the same constant, and the suite submits the form eight times against a limit of five, so whichever three ran last were answered with the throttle message (D068). Three of the new tests initially passed with the control they name deleted — the D066 pattern, caught during authoring this time
- 2026-08-25 · 1.11 five reviewer findings · **F1** the discard path reached an amendment and stranded the signature underneath it — sign, amend, clear the amendment, delete, and the visit had no current note while a signed record sat unreachable through every screen the product renders. Four guards passed because each asked *is this a draft* and *is it empty* and none asked *does it supersede anything*; the clause is now in the aggregate, the endpoint, a new DELETE trigger, and the editor's own affordance, and each was deleted in turn to watch its test go red (D069). Recorded there too: **refusing** the discard beat reverting v1 to `IsCurrent = 1`, because reverting builds a write path that UPDATEs a signed note and puts the row out of step with its own `NoteAmended` audit entry — the cost being that an amendment started by accident cannot be abandoned. **F2** the test asserting "an amendment is never discardable" was asserting on a fresh copy of the signed content, so it passed on the emptiness clause and never reached its own claim. Beyond fixing it: a `Control:` line naming the symbol and quoting the failure seen when it was deleted, plus a pre-commit step here and a protocol in `TEST_STRATEGY.md` — a convention over a mutation harness, because the mechanical options all verify a sentence exists and only running the deletion verifies the claim (D070). **F3/F4** the delete and its `NoteDiscarded` row are one transaction now, inside the retrying execution strategy and committed on `CancellationToken.None` — the first explicit transaction in the API; refused deletes write `AuditOutcome.Failure` with a fixed reason vocabulary, 404s included, since that is the only record a walk through note ids can leave; `AddProblemDetails`/`UseExceptionHandler` added, because the integration suite runs in Development and the developer exception page was rendering SQL and parameters (D071). **F5** the comment claiming "every DateTime is UTC, enforced at the mapping layer" set the column type and nothing else, so every timestamp read back from `datetime2` serialised without a `Z` and a 09:00 ET visit read as "not started yet" until 1pm; `SignedAtUtc` had it too. Fixed with a model-wide value converter, asserted on the raw response body against every `*Utc` property rather than a named list, and the web fixtures replaced with two verbatim recorded payloads while the vitest suite is pinned to `America/New_York` — a suite running in UTC agrees with this bug (D072)
- 2026-08-25 · 1.4 guardian + address forms · **`HasLegalAuthority` is a required radio group with neither option preselected**, refused as `bool?` null at the API and read from its own argument on the aggregate — because the column is a `bit` with no room for "nobody said", and a checkbox submits the same `false` for "she may not" as for "nobody looked". Four layers ask it, none answers it. Guardians with none authorised is rendered as a real state (`recordsReleaseState`) with a `role="status"` notice, never resolved by picking somebody, and every card states the answer in both directions because an absent badge means *no* and *never recorded* equally (D073). Editing an address is **two operations**: recording a move supersedes and keeps the old row, correcting a typo changes one row in place and its request shape carries no type and no dates at all, so it cannot invent a move or erase one (D074). Guardian and address writes are now audited — adding a guardian previously wrote nothing. Found while writing it: the `Guardian` and `PatientAddress` query filters had **no test that could fail**, the D066 F4 shape in two more places — a foreign guardian is only ever reachable through a foreign patient, so the `Patient` filter was covering for both; fixed by planting rows the API cannot produce, and both filters deleted in turn to watch their tests go red. One new test also stayed green with its control deleted: a blank move-in date was being caught by the malformed-date check below it and answered "that date does not look right" about a field nobody had filled in
- 2026-08-25 · 1.12 five reviewer findings · **F2** the retried transaction wrote the audit row twice. A transient failure of the audit save leaves the entity `Added` — a failed `SaveChanges` never calls `AcceptAllChanges` — so the rollback took the delete back and the next attempt inserted both, into a table the app principal cannot UPDATE or DELETE. Hoisting the construction out of the lambda, which is what the finding suggests, closes that case and not the one where the *commit* fails: the entity then carries a store-generated key and EF tries an explicit identity insert. The root cause is a `DbContext` whose state survives into an attempt that assumes a clean one, so the fix is `AtomicWrites.WriteAtomicallyAsync` — transaction inside the execution strategy, `ChangeTracker.Clear()` on every attempt, commit on `CancellationToken.None`, and a written contract that the body re-reads and re-constructs everything it touches. D071 named the inline block as the pattern to copy; it is a helper now, before it was copied (D075). **F1** the same survivorship bias on every other audit write: all 24 in the API handed `SaveChangesAsync` the request token, and the four refusal paths write an audit row and nothing else, so a client that drops the connection leaves no trace at all. Fixed at the seam — `IAuditWriter.WriteAsync` takes no `CancellationToken` — because **CA2016 is an error here**, so while the parameter exists the analyzer *requires* every call site holding a token to forward it. Two tests, on two different endpoints, cancel `RequestAborted` mid-request through a replaced lifetime feature. **F3** the refusal asked about lineage before status, so every signed note from v2 on audited as `reason=amendment` and a query for attempts on signed records undercut by exactly the amended — contested — ones; and a superseded v1 was told "amend it instead", which `Amend()` refuses. Order is status → lineage → content now, the two signed sentences differ on `IsCurrent`, and the test proves the advice by taking it and requiring a refusal. Same wrong sentence in `ClinicalNote.UpdateContent`, reached through `PUT /notes/{id}`, fixed with it (D076). **F4** the `Serilog` correlation claim deleted rather than implemented — 4.1 owns the destructuring policy and half of it first is how PHI reaches a sink; swept `api/` and `web/` for the same defect class and found one more, `consultation/actions.ts` describing a validation failure being logged when nothing there logs. **F5** every `Control:` line from `a4d6ff5` re-run: 12 checked, **2 wrong**. `NoteEditor.test.tsx` named a clause that does not exist, in the commit that invented the convention. `A_signed_note_cannot_be_deleted_by_raw_sql` **stayed green with its named clause deleted** — every signed note the API can make has content, so the trigger's emptiness clauses were covering for its Status clause; isolated by planting a signed-but-empty note with a raw `INSERT`, which `Sign()` will not produce. Also re-run against its new home: one line went stale when F2 moved the transaction out of the endpoint (D077)

- 2026-08-25 · 1.5 `ConsultationRequest` entity + persistence · **slice 1's last unmet criterion is closed: the form stored nothing for five slices while confirming to every parent who used it.** A confirmation is now a claim about a row, so every path that cannot produce one had to stop making it — an unreachable API, a practice with no clinician to receive the enquiry, and a submission the API refuses all render an error that keeps what was typed and points at the phone, because a family told "we'll be in touch" about an enquiry that vanished does not follow up. **The `ProviderId` question** — a public row has no session to take one from — is answered by resolving the **sole active provider** server-side and **refusing with 503 when that is ambiguous**, rather than taking the lowest id: the day a second clinician is added, "who receives a parent's enquiry" is a decision a person has to make, and guessing puts it in front of nobody, ever. The forwarded provider header is ignored on that route at both ends (D078). **The notification carries nothing by SIGNATURE**, not by care: `IConsultationNotifier.NotifyAsync` takes an opaque `Guid` and has no parameter a child's name could travel through, the body is a constant, and it is sent after the commit — inside the retrying transaction body it would be re-sent on every attempt, and an email is not something a rollback takes back. Transport is blocked on the practice having a mailbox, so it is logged; queued as 1.13 along with the inbox "sign in to view" currently points at nothing (D079). The row and its `ConsultationRequestReceived` audit entry commit together through `AtomicWrites`, with `IpAddress` left null on purpose — hashing the address on the row and writing it in full one table over would undo the decision. **Found while checking whether the rate limiter protects the write and not just the response: it protected neither.** `clientKey()` read `x-forwarded-for`'s FIRST entry, which is the half the caller writes; rotating it minted a fresh bucket per request, so the limiter counted to one forever for anyone who thought to set a header. It reads the entry the proxy appended now, and the same hash serves the limiter and `SourceIpHash` — one derivation, two uses (D080). 59 controls deleted and re-run: **one stayed green**, the BFF's `response.ok` guard, because a refusal body has no `publicId` and the missing-id guard answered instead — the D077 shape again, isolated by adding a refusal that carries a well-formed id, so the status is the only thing that can refuse it. **Five `Control:` lines named a failure the run did not print** and were corrected from the output, one of them wrong about the outcome rather than the wording: deleting the length guard on the concerns field gives a 500, not the 201 that was written down, because `nvarchar(2000)` refuses it — the column covers for the aggregate, badly. Playwright gained a stand-in API (`e2e/api-stub.mjs`) because the browser flow now depends on a POST being answered, and the E2E job has no database

---

**Resuming after a context reset?** Read `RESUME.md` at the repo root first — it has
the bootstrap prompt and the current state.
