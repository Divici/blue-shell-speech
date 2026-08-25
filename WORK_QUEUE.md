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
- [x] **1.6 `Encounter` + `ResourceDocument` entities** — ship empty per the scope ledger.
      Adding a billing table to a live clinical database later means backfilling history.
- [x] **1.13 Consultation inbox** — "sign in to view" now has somewhere to lead. A list of
      enquiries with status filters, a detail view, the
      `New`/`Contacted`/`Converted`/`Declined` transitions the aggregate already holds, and
      convert-to-patient wired to `ConvertTo`. Reading an enquiry is audited on **both**
      endpoints that disclose one (D065 applied before the second reader exists), and the
      summary carries no `Concerns` so the listing cannot become a second, unaudited read of
      the same content.
      **The real email transport is NOT in this task** — it moved to *Blocked — needs David*
      below. `LoggingConsultationNotifier` stays.

- [x] **1.7 Remove Next template assets** — `next.svg`, `vercel.svg`, `globe.svg`,
      `file.svg`, `window.svg` in `web/public/`.
- [x] **1.8 `/health/ready` dependency checks** — register SQL and blob under the "ready"
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

- [x] **1.14 Fix the reviewer findings against `6ef8b56`** — **run this BEFORE 1.6 and
      1.13. F1 is a REGRESSION introduced by 1.12's own fix.** Fix the class, not the
      instance (see ORCHESTRATION.md).
      - **F1 (regression)** `ChangeTracker.Clear()` detaches the note the endpoint
        validated; the lambda re-reads `doomed` and calls `Remove(doomed)` with **no
        re-check of `Status` / `SupersedesNoteId` / `CanBeDiscarded`**. Michelle taps
        Discard while the editor's autosave lands `PUT /notes/N` with real clinical text:
        the re-read returns a non-empty row at `RowVersion` V2, and
        `DELETE … WHERE Id=@id AND RowVersion=V2` **matches** — where the pre-1.12 code
        carried V1 and correctly raised `DbUpdateConcurrencyException`. Only the trigger
        stops it, as a 500 rather than a 409, with **zero `AuditEvents` rows**: the success
        row is inside the rolled-back transaction and `AuditRefusedDiscardAsync` never runs
        on that path. D064's three-layer guard is now one layer.
        `NoteEndpoints.cs:460-500`. **Re-validate inside the lambda, against the row
        actually being deleted.**
      - **F2** `WriteAtomicallyAsync` silently discards the caller's staged changes. The
        next adopter is `AmendNote`: wrapped as written, `ChangeTracker.Clear()` throws away
        v1's `IsCurrent=false` flip staged by `note.Amend(reason)`, the amendment inserts as
        a second current row, and `UX_ClinicalNotes_OneCurrentPerAppointment` rejects it —
        hoist the `Add` above the call too and the endpoint answers **201 Created with a
        Location for a row that was never inserted**. The contract is prose only; **one
        `if (db.ChangeTracker.HasChanges()) throw` on entry makes the whole class loud
        instead of silent.** `AtomicWrites.cs:47-64`.
      - **F3** No `CommandTimeout` is set in `AddInfrastructure` (while
        `DesignTimeDbContextFactory.cs:46` sets 180 right beside it), and no
        `RequestTimeouts` in `Program.cs`. A refusal against a resuming serverless database
        holds a request and a pooled connection ~3 minutes after the caller has gone.
        `IAuditWriter`'s docstring names a command timeout no configuration sets — D072's
        class. `InfrastructureServices.cs:23-29`.
      - **F4** `strategy.ExecuteAsync(async () => …)` is the token-less overload, so `ct`
        never reaches the retry loop; a cancelled discard sleeps out the full backoff. A
        parameter the signature advertises and the helper half-honours. `AtomicWrites.cs:53`.
      - **F5 (minor)** `BlipsOnceAuditWriter`'s docstring says "the entity is Added and the
        save is what breaks," but it throws before `SaveChangesAsync`. The D070 defect class
        inside the harness written to enforce D070.

- [x] **1.15 Fix the reviewer findings against `0145d6c`** — **F1 is a regression that
      degrades Michelle's first request of every day.** Run before 1.13, 1.7 and 1.8.
      - **Before writing any `DECISIONS.md` entry claiming a class is closed, enumerate
        every window in which the same outcome is reachable and test each.** Three rounds
        running, a fix has closed the reported window, left an adjacent one, and recorded
        the class as closed. F2 below is that exact failure.
      - **F1 (regression)** `DatabaseTimeouts.cs` sets a 30s request timeout and justifies
        it as "the BFF gives up at twenty-five (`web/lib/api`)". `AbortSignal.timeout`
        appears **once** in the whole web tree — `web/lib/api/consultations.ts`, the public
        form. `notes.ts`, `patients.ts`, `schedule.ts` (×2) and `web/lib/auth/api-client.ts`
        set no signal, so the clinician is still attached. Michelle's first request of the
        day hits an auto-paused Azure SQL, `EnableRetryOnFailure(5, 10s)` starts carrying it
        — the stated reason that policy exists — and `RequestTimeoutsMiddleware` kills it at
        30s with a 504, where before this commit it had ~3 minutes and would have
        succeeded. Either set the BFF timeouts the comment claims, or raise the request
        timeout above the retry budget. Do not leave the two contradicting each other.
      - **F2** "500 and zero audit rows" is still reachable one round trip later than the
        new test covers: the autosave can land between `SingleOrDefaultAsync` and
        `SaveChangesAsync`, the `RowVersion` predicate misses,
        `DbUpdateConcurrencyException` throws, and nothing catches `DbUpdate*`.
        `InterleavesOneWriteBeforeTheSecondRead` fires only *before* the second read, so the
        existing test **cannot reach this window by construction**. D081 claims this class
        is closed; it is not.
      - **F3** The late refusal's audit row is the only refusal row a rollback can erase —
        every other refusal is written outside any transaction on `CancellationToken.None`.
        If the commit fails through the retry budget, the row rolls back and the outcome is
        again 500 with nothing on file. That inverts D075's principle for the row the commit
        itself calls "the interesting row".
      - **F4** The rewritten `Control:` line on `A_refused_discard_is_audited_as_a_failure`
        says "one now"; there are **two** `AuditRefusedDiscardAsync` calls after the commit,
        and deleting the late one leaves that test green.
      - **F5 (from 1.6, carry forward)** The control for a database constraint is the
        **migration**, not the EF configuration — the test database is built by running
        migrations, so deleting `.IsUnique()` from a configuration leaves the index in place
        and the test green. **Re-verify every constraint-related `Control:` line already in
        the repo against the migration**, not the configuration. Add this to
        `docs/TEST_STRATEGY.md`.

- [x] **1.16 Fix the reviewer findings against `98974dc`** — the timeout nesting, **third
      round**. Run before 1.7 and 1.8.
      - **Verify by MEASUREMENT, not by deriving a number.** Three consecutive rounds
        (1.14 F3, 1.15 F1, this) computed a formula that looked right and shipped a test
        that could not detect it being wrong. If a test asserts an inequality between two
        constants, it proves the constants, not the system. Instrument the real path and
        observe the real ceiling.
      - **F1** `RetryBudgetFor` models ONE 30s command per attempt. One attempt of
        `DiscardTheRow` issues **three** independently bounded commands — the SELECT, the
        DELETE save, and the audit save (`AuditWriter` saves on the same context) — so six
        attempts is ~590s, not 230s. `ProviderContextMiddleware`'s lookup, the endpoint's
        first read, and the early refusal audit each sit OUTSIDE `strategy.ExecuteAsync`
        with their own full budget. At ~25s a statement, attempts 1–2 of a discard consume
        ~200s and the 260s `Request` cancels attempt 3 with three retries unspent.
        **The backoff term is fine** — EF's real `GetNextDelay` is
        `min(1s×(2^i−1)×[1,1.1), MaxRetryDelay)` = 0/1.1/3.3/7.7/10 ≈ 22s against the
        modelled 50s. The command term is the error and it errs unsafe.
      - **F2** `RequestTimeoutsMiddleware` cancels `RequestAborted` then WAITS for the
        pipeline; `AuditWriter` holds no token. `DELETE /notes/{unknown-guid}` against a
        wedged database runs `AuditRefusedDiscardAsync` for up to 230s **past** the 260s
        cancellation — ~490s real ceiling, while `apiSignal()` aborts at 300s. The stated
        invariant is false by the repo's own arithmetic: 260 + 230 > 300. So
        `consultations.ts` can still return `{stored:false}` for a committed enquiry — the
        bug 1.15 believed it fixed.
      - **F3** `timeouts.test.ts`'s `fetch`-count guard iterates a hard-coded five-path
        `CLIENTS` array while claiming a new call site "arrives bounded or arrives red". A
        seventh module (`web/lib/api/encounters.ts` is the likely next) leaves every layer
        green — the .NET cross-tree test only reads `API_TIMEOUT_MS` and never looks at a
        call site. **Glob the directory, do not list files.** D072's fifth appearance,
        inside the test written to close its fourth.
      - **F4** `A_request_the_retry_policy_is_carrying_is_not_cut_off` passes
        `command: 250ms` but `FailureHarness` pins the real command timeout at 30s, so
        "one command's worth of patience" is fiction and F1's error is unreachable by
        construction. It also leaves only 750ms of headroom for host warm-up and ~10
        Testcontainers round trips.
      - **If this round does not converge, stop and flag it for David** rather than opening
        a fourth. At that point it is a design question about whether a scale-to-zero
        database and a synchronous request path are compatible, not a bug.

- [x] **1.17 Fix three reviewer findings against `8022079`** — the fourth finding, the
      240s ingress ceiling, is **Blocked — needs David**; do not attempt it here. These
      three stand on their own regardless of what David decides about the ladder.
      - **F1** Every database call in
        `api/src/Practice.Infrastructure/Identity/ProviderAuthenticator.cs` goes through
        `UserManager<PracticeUser>`, and **none of those methods has a `CancellationToken`
        overload** — verified by reflection: `FindByEmailAsync`, `CheckPasswordAsync`,
        `AccessFailedAsync`, `ResetAccessFailedCountAsync`, `GetTwoFactorEnabledAsync`,
        `VerifyTwoFactorTokenAsync`, `UpdateAsync`. So the login path observes neither
        `RequestAborted` nor `deadline.Token`. `POST /auth/login` with a wrong password,
        phone locks at t=0.2s → grace starts → `AccessFailedAsync` carries a resume for
        >90s → the deadline is already expired when
        `audit.WriteAsync(LoginFailed, reason=bad-password)` runs, so it throws instantly
        and **the row is lost** where `CancellationToken.None` would have landed it. The
        enumeration failure D090 claims to have closed "by construction".
      - **F3** `docs/SECURITY.md` lines 177–178 still assert "**No audit write is
        cancellable.** `IAuditWriter.WriteAsync` takes no `CancellationToken`, and
        `AuditWriter` saves on `CancellationToken.None`." `AuditWriter` now saves on
        `deadline.Token`. The document a compliance reviewer reads denies the durability gap
        D090 knowingly accepted, and D012's append-only framing rests on it. Sibling stale
        comment at `InfrastructureServices.cs:52`.
      - **F4** `DatabaseTimeouts.cs` says `Request + UncancellableGrace` is "the whole of
        what this tier will spend", while its own class docstring says `BEGIN TRANSACTION`
        and `COMMIT` are round trips with **no command timeout and no bound**;
        `AtomicWrites` commits on `CancellationToken.None`. `IConsultationNotifier.NotifyAsync`
        is the same shape and is not on the deadline — harmless only while the notifier
        writes a log line, so **the ceiling stops holding the day the real mail transport
        lands**, which is already queued under Blocked.
      - Also worth carrying: a scoped deadline resolved OUTSIDE a request scope does not
        throw — a long-lived scope (a retention job draining `AudioDeleted`) silently gets
        an already-expired token. Relevant to task **2.10**.

- [x] **1.18 Fix five reviewer findings against `e8beb68` — verdict was NOT SOUND.**
      **Run before any further Phase 2 work.** Every finding below was MEASURED on an
      extracted copy, not argued. F1 and F3 are regressions introduced by 1.17 itself.
      - **F1 (regression)** `/auth/password` is an account-existence oracle in status, body
        AND time, and the attempts leave no audit row. Stalling `FROM [AspNetUsers]` 1.5s
        under a 1s bound + 10s grace: known email + wrong password → **200
        `{"status":"invalid"}` in 4696 ms**; unknown email → **504, empty body, 1527 ms**;
        `COUNT(*) WHERE Metadata='reason=unknown-email'` → **0**. Cause:
        `PracticeUserManager` moved the known-email path onto `deadline.Token`, but the
        unknown-email branch's `await Task.Run(… HashPassword …, ct)`
        (`ProviderAuthenticator.cs:71`) still rides `RequestAborted` and throws before its
        audit write. Defeats `Unknown_email_is_indistinguishable_from_a_wrong_password`.
      - **F2** **The five-failure lockout does not work.** Measured: 4 waves of 20
        simultaneous wrong-password POSTs = **80 attempts, `AccessFailedCount = 4`,
        `LockoutEnd = NULL`.** One increment survives per wave because `ConcurrencyStamp` is
        `.IsConcurrencyToken()`, `UserStore.UpdateAsync` swallows
        `DbUpdateConcurrencyException` into `IdentityResult.Failed`, and
        `ProviderAuthenticator.cs:100/154/178` **discards that result**. 1.17's reorder
        widened the window by adding a round trip between read and UPDATE. **There is no
        login rate limiter anywhere in `api`** — `web/lib/rate-limit.ts` serves only the
        consultation form — so an N-wide attacker buys N guesses per counted failure. This
        is the threat D092 cited to justify rejecting `RequestAborted`. At minimum, stop
        discarding the `IdentityResult`; coordinate with task **4.3**, which owns login rate
        limiting and is now urgent rather than scheduled.
      - **F3 (regression)** `LoginSucceeded` can describe a sign-in that never happened.
        With `UPDATE [AspNetUsers]` stalled 20s under a 1s bound + 2s grace,
        `POST /auth/mfa/verify` with a **valid** TOTP returned **504 with no session**, yet
        the audit table held `MfaEnrolled, MfaChallenged, LoginSucceeded` and
        `LastMfaAtUtc` was **null**. D092's asymmetry argument is about *failures* and
        **inverts** for `CompleteSignInAsync`: a false `LoginSucceeded` is the row an
        investigator uses to scope a breach.
      - **F4** `docs/SECURITY.md` claims four more controls that do not exist: §Logging's
        Serilog PHI-redaction policy and its asserting test (no Serilog package in any
        `.csproj`; `Program.cs:34` says so); §Dependencies' "Dependabot on · pinned action
        SHAs" (no `dependabot.yml`; every action is a floating tag); §Authorization's
        "adding an endpoint without its authorization test fails CI" (nothing enumerates
        `EndpointDataSource`); §Caching's "test hitting every authenticated route".
      - **F5** `docs/THREAT_MODEL.md` still carries what SECURITY.md just retracted —
        boundary ① S "rate limit per IP and per account; lockout with backoff", boundary ⑧ T
        "pinned action SHAs". CLAUDE.md names this file as the security-adversary lane's
        bar, so leaving it stale means the next review is judged against controls that were
        removed from the compliance doc a commit earlier.
      - Also noted: deleting the `.AddUserManager<PracticeUserManager>()` registration
        leaves a **green build**, so the reflection test is its only guard.

- [ ] **1.19 Rate limiting on login and dictation upload — PULLED FORWARD FROM 4.3.**
      **This is the next task. Do it before any Phase 2 work.**
      1.18 fixed the lockout so that concurrent attempts are counted; it did not close the
      hole, because **the lockout can only count attempts against an account that exists**.
      An unbounded stream of guesses at random addresses is limited by nothing in `api` —
      each one wakes a container that scales from zero, runs a PBKDF2 hash, and inserts an
      audit row. `web/lib/rate-limit.ts` serves the public consultation form only.
      A minimal in-process limiter was **deliberately not** shipped inside 1.18: the whole
      subject of that commit was a control that looked present and was not, and an
      in-process fixed window on a horizontally scaled Container App is exactly that shape
      (its own docstring says so). Scope, in full:
      - A shared store, so the limit holds across replicas and across a scale-to-zero cycle.
      - Partition by account AND by source. The source has to survive the BFF hop — D080's
        `x-forwarded-for` handling and `SourceIpHash` are the existing pieces.
      - **A 429 must not become a fresh enumeration oracle.** Rate-limiting per account
        tells a caller which accounts exist unless the unknown-email branch is limited
        identically — 1.18's F1 measured that exact class in three dimensions.
      - Dictation upload too (2.5's chunked resumable upload is the other expensive path).
      - `docs/SECURITY.md` §Authentication and `docs/THREAT_MODEL.md` ① S both say
        "planned — 4.3" today, and
        `SecurityDocumentTests.Both_documents_describe_login_rate_limiting_as_the_code_leaves_it`
        goes red in BOTH directions: if the sentence is tidied away while the limiter is
        absent, and if the limiter lands while the sentence still says planned.

## Phase 2 — Slice 6, dictation

- [x] **2.1 PWA shell** — `manifest.ts`, icons, service worker, offline shell.
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
- [ ] **4.3 Rate limiting on login and dictation upload** — **moved to 1.19 and pulled to
      the front of the queue.** 1.18 measured the login half as an open hole rather than a
      scheduled one; the scope lives there.
- [ ] **4.4 `web` → `api` caller identity** — currently network isolation alone, weaker
      than `THREAT_MODEL.md` boundary 2 specifies.
- [ ] **4.5 Capacity banner + admin alerts** — against internal counters (§13).
- [ ] **4.6 Alert on overdue audio deletion** — a silently failing lifecycle job looks
      exactly like a working one.
- [ ] **4.7 Audit completeness test** — every event type in `SECURITY.md` emitted and
      queryable; verify the app principal cannot UPDATE or DELETE `AuditEvents`.
- [ ] **4.8 Cross-provider tenancy, parameterized over the route table** —
      `docs/TEST_STRATEGY.md` claimed this existed for five slices and it did not: every
      cross-provider test is hand-written, one per endpoint, so an endpoint added without one
      is an oversight nothing catches. Enumerate `EndpointDataSource` and drive each route
      with a foreign provider, expecting **404, not 403**. Found by D090's sweep for
      hard-coded lists that mean "all of them"; the doc now describes what exists rather than
      what it wished for.

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

- **DECIDE: the request-timeout ladder is unreachable, because Container Apps ingress cuts
  every request at 240s.** `infra/provision-platform.sh:65` creates a **Consumption-only**
  environment; that limit is fixed and not raisable without premium ingress on a
  workload-profiles environment, and it applies to both the external hop to `web` and the
  internal hop to `api`. Every number tasks 1.14–1.16 negotiated sits above it — `Request`
  620s, `Ceiling` 710s, `API_TIMEOUT_MS` 750s — and the measured retry budget alone is
  ~590s. **The platform decides first, so no BFF constant can fix this.** A consultation
  POST during an auto-pause resume takes a 504 from ingress at 240s,
  `web/lib/api/consultations.ts:118` reads `!response.ok` → `{ stored: false }`, and a
  parent is told their enquiry was not stored while the API commits the row — the exact
  defect D086 claims to have closed.
  Options, with costs:
  1. **Shrink the budget under 240s** — reduce `CommandSeconds` (30s, named in D090 as the
     smallest lever) and/or `MaxRetryCount`. Free. Cost: a genuine resume that outruns the
     budget now fails honestly where it previously succeeded.
  2. **Move these paths to background job + polling.** CLAUDE.md **already mandates this for
     dictation** — "never a synchronous request that must survive a scale-to-zero cold
     start." Free and consistent with a decision already made. Cost: real work, plus a UI
     that shows pending state.
  3. Premium ingress on a workload-profiles environment. Costs money, leaves consumption.
  4. Disable Azure SQL auto-pause. Exits the free offer.
  **Recommendation: 1 + 2.** 3 and 4 convert a design problem into a monthly bill.
- Buy the practice domain → unblocks the CDN (blocker #6) and a real contact address.
- Upgrade Azure to Pay-As-You-Go under the practice identity → unblocks blockers #1, #4, #5.
- Request `DataZoneStandard` quota → unblocks a PHI-safe model deployment.
- Real practice phone and email.
- **A real transport for the consultation notification** (the second half of 1.13). The
  notification is composed and logged, not sent: `LoggingConsultationNotifier` exists
  because the practice has no mailbox, and a verified sender needs the domain purchase
  above. Nothing about it is a code problem — `IConsultationNotifier.NotifyAsync(Guid)` is
  deliberately unable to carry content (D079), so the transport is one new class registered
  in `AddInfrastructure` and nothing else. Until then Michelle learns of an enquiry by
  signing in, which is what the email would have told her to do anyway — and 1.13's inbox
  is now the destination it points at.
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

- 2026-08-25 · 1.14 five reviewer findings · **F1 was a regression this project's own fix caused, and the reproduction is the deliverable.** `ChangeTracker.Clear()` detached the note `DiscardDraft` had validated, so the checks were about `note` and the delete was about `doomed` — and the `RowVersion` guard that used to catch that changed sides, because the re-read carries the current version and the DELETE therefore matches where it once raised `DbUpdateConcurrencyException`. What was left was the trigger alone, answering a race with a **500 and zero audit rows**: the success row is inside the rolled-back transaction and the refusal helper is not on that path. Forced deterministically with an EF command interceptor that lands one write immediately before a request's second read of a table (`InterleavesOneWriteBeforeTheSecondRead`) — two live requests hit this ordering once in thousands of runs and never in CI. Closed in three parts: the three refusals became one predicate asked again **inside** the transaction body against the row being deleted, a late refusal answers 409 with the same clinician-facing sentence, and its audit row is written inside the transaction so the near miss is on file (D081). **The class, not the case:** the helper has two call sites and the other one had the same shape — `SubmitConsultationRequest` resolved the sole active provider outside and used the answer inside, so a second clinician activated mid-write would have committed an enquiry against whoever was sole a moment earlier, the exact silent answer D078 exists to refuse. Both fixed; the sibling was found by walking the call sites, not reported. **F2** the reviewer's one-line `HasChanges()` entry guard implemented, plus the same guard before the commit — the identical silence sits one layer in, where a body stages and forgets to save, commits an empty transaction, and lets the caller answer 201 for a row that does not exist. Re-checking itself stays prose: nothing in the type system separates a fact about a row from one the body invented, and a revalidator parameter only helps the caller who already knew. **F3** `CommandTimeout` and a default `RequestTimeouts` policy both set, both asserted, and the `IAuditWriter` docstring corrected — it named a bound no configuration set, and the honest one is the command timeout **times the retry budget**, which is minutes rather than thirty seconds. The middleware is pinned separately from the options, because options nothing reads is the D072 defect exactly. Swept every configuration name appearing in an API comment plus the web's caching, timezone and replica claims: one defect (this one), one already-honest known gap (`Program.cs` on the absent Serilog, owned by 4.1). **F4** the token-less `strategy.ExecuteAsync` overload replaced; a cancelled write measurably waited out all 5.026 s of a 5 s backoff before the fix (D082). **F5** `BlipsOnceAuditWriter`'s docstring described a mechanism it does not use — it throws before the save, not through it — corrected to describe the tracker state that is actually the point (D083). Eight controls deleted and re-run for the nine new tests — the ninth asserts the guards are not over-strict and names none — and **none stayed green**. Four existing `Control:` lines were rewritten and re-verified after the refusal branches moved into a predicate and the three refusal-audit calls became one (D077), and four more tests naming `AtomicWrites` internals were re-run because the helper changed shape. One deletion turned out not to be a control at all: removing `AddRequestTimeouts` outright stops `RequestTimeoutsMiddleware` constructing and fails the whole class, so the policy assignment is the control and that is what the line now names

- 2026-08-25 · 1.6 `Encounter` + `ResourceDocument` entities · **three tables, no endpoints, no screens — the shape is the deliverable.** `Encounter` carries what a superbill prints (CPT/HCPCS code, up to four modifiers, CMS place of service, units, charge, payments, rendering clinician, service date) and **diagnoses are rows rather than a delimited `IcdCodes` column**, because the first code is the primary reason for the encounter and a string stores an order it cannot enforce; two unique indexes cover position and code, and they are two tests because either would answer for the other on a duplicate planted at the same sequence (D077). Modifiers stayed delimited, deliberately — a payer-driven qualifier is not a claim about a child. **`ServiceDate` is a `date` derived in `America/New_York` inside the aggregate**: an in-home practice runs evening sessions, 01:30Z is the previous evening in Maryland, and a superbill dated a day late is not detectable afterwards — so `PracticeTimeZone()` moved out of `AppointmentEndpoints` into `PracticeTime` in the domain rather than being copied. `RenderingProviderId` is separate from `ProviderId` because one is tenancy and the other prints an NPI (the D073 conflation). **Nothing freezes when a superbill is generated, and that is the decision**: a freeze with no void-and-replace path is the D069 trap, and the trigger plus two nullable columns is a no-backfill migration when generation ships (D084). **`ResourceDocument` belongs to a PROVIDER** — the doc previously said no filter was needed, which is true of the published rows and wrong about the table, since the library is edited through a session and an unfiltered one shows a second clinician's drafts to the first. The anonymous read opts out with one greppable `IgnoreQueryFilters().Where(r => r.IsPublished)` rather than the filter carrying a null-provider branch, because forgetting the opt-out renders an empty page and forgetting the branch leaks whatever gets added next. Retention: none — not PHI, never deleted, `Withdraw` stamps a date and `PublishedAtUtc` records when it FIRST went up and is never cleared; versioning is a revision counter, not a row chain. Slug unique across the whole table because `/resources/{slug}` has no tenant segment; content type is an allowlist because this is the one table served to anonymous readers (D085). 62 tests added (API 284 → 346, vitest unchanged at 360). **39 controls deleted and re-run; none stayed green.** Two findings from doing it: the non-UTC guard on `Encounter.Record` is partly covered by `PracticeTime.LocalDateOf`, so the test asserts on `ParamName` — asserting only that something threw would have stayed green; and the control for a database constraint is the **migration**, not the EF configuration, since the test database is built by running migrations, so deleting `.IsUnique()` from a configuration leaves the index on the table and the test green. Also caught: MSBuild skipped rebuilding after a restore because `mv` put back an older mtime, so the last deletion of each batch stayed compiled into the DLL and showed up as two failures in the full run

- 2026-08-25 · 1.15 five reviewer findings · **F1 was a regression that degraded Michelle's first request of every day, and the comment justifying it was false in another language.** A 30-second request timeout sat on top of a retry policy allowed six commands and fifty seconds of backoff, so `RequestTimeoutsMiddleware` cancelled the auto-pause wake-up `EnableRetryOnFailure` exists to carry — 504 at thirty seconds where the request had previously arrived after a minute. It was justified by "the BFF gives up at twenty-five (`web/lib/api`)"; `AbortSignal.timeout` appeared **once** in the whole web tree, on the public form, and five of six clients — every clinician-facing one — set no signal at all. `DatabaseTimeouts.Request` is DERIVED now, `RetryBudgetFor` + one command of grace, with `EnableRetryOnFailure`'s own arguments read from the same class so the halves cannot drift; a test reads the command timeout, the retry count and the maximum backoff off the running application and asserts the inequality, a second exercises it at a scale worth waiting for, and a third **reads `web/lib/api/timeouts.ts` off disk from the .NET tests** and asserts the BFF's bound is the larger — a claim about another tree is what failed, so the test crosses the same boundary the claim did. All six BFF fetches carry `apiSignal()`, and the consultation form's old 25 s is gone: it sat under the API's retry budget and told a parent "not stored" about an enquiry the API went on to commit (D086). **F2** the "500 with zero audit rows" class was recorded closed by D081 and was not: `InterleavesOneWriteBeforeTheSecondRead` fires before the second read and therefore **cannot reach** the gap between that read and the DELETE it decides on. **Seven windows enumerated between the first read and the commit; three were new, each has a test and a new interceptor that fires immediately before the DELETE.** An autosave with content answers the same 409 and the same `has-content` reason as the window before it; a competing DELETE answers 200 with no second row; an autosave that writes and clears leaves the row moved and still discardable and answers `contended` — a fifth reason, the only one that describes the race rather than the row, and deliberately not retried in place because that loop has a child's record at the end of it. The trigger is deliberately still a 500: it means the endpoint's predicate and the database's disagree, which is a defect. **F3** the late refusal's audit row was the only refusal row in the endpoint a rollback could erase — the other three are written outside any transaction on an uncancellable writer. It is in a `finally` now, so a commit that fails through the retry budget answers 500 **with the attempt on file**; proven with a `DbTransactionInterceptor` that refuses to commit, because inside and outside are indistinguishable on any run that succeeds (D087). **F4** the `Control:` line said "one now"; there were two then and three after this change, the test reaches one, and deleting either other leaves it green — checked rather than counted. **F5** the control for a database constraint is the migration, and *which* migration: `TR_ClinicalNotes_PreventDeletingRealNotes` is written three times with `CREATE OR ALTER` and only `AmendmentDeletionGuard.Up` survives, so the emptiness clauses deleted from `ClinicalNoteDeletionGuard` leave **all four** trigger tests green. Ten constraint `Control:` lines re-run against the migration: the six from 1.6 were already right, and one of the four was a **false negative on its own terms as well** — `A_signed_note_cannot_be_deleted_by_raw_sql` isolates neither the Status clause nor the emptiness clauses, because for a signed note the API can produce they cover for each other in both directions. Rule added to `docs/TEST_STRATEGY.md` (D088). D072's sweep over every comment in `api/` and `web/` asserting something configured elsewhere found **one** defect beyond F1: `recorder.ts` claimed the 300-second take cap was "also enforced by a CHECK constraint on `DictationTake.DurationSeconds`", and that entity, table and constraint are unbuilt work in 2.3. Twenty-one new tests (7 API, 14 vitest). Fourteen controls deleted and re-run on those, and eleven existing deletions re-run — five against the trigger, one spot check on a billing constraint, and five lines whose code moved this round (D077), including the request-timeout value, which is now 00:04:20 rather than the 00:00:30 its line quoted. **One stayed green** and is documented as a test that isolates nothing. API 346 → 353, vitest 360 → 374, Playwright 115 passed / 2 skipped

- 2026-08-25 · 1.13 consultation inbox · **the inbox half shipped; the email transport moved to Blocked, because the practice has no mailbox and a verified sender needs the domain purchase — nothing about it is a code problem, so building it would have meant configuring a mail provider and putting an address in a public tree.** "Sign in to view" now leads somewhere: `/enquiries` with status tabs that are URLs rather than client state, a detail view, the three transitions, and convert-to-patient. **Reading an enquiry is audited on BOTH endpoints that disclose one**, which is D065 applied before the second reader exists rather than after — the finding's own sentence is that an endpoint has to be safe on its own terms and not because of who currently calls it. Verified by tracing the call the UI makes: `page.tsx` → `enquiriesApi.get` → `GET /consultation-requests/{publicId}`, and the test asserts the concerns text and the audit row out of the same response. **The list carries no `Concerns` member at all** — a preview on the row would be a second, larger disclosure of the same content one fetch from the audited one, which is exactly the shape D065 found on note history. `ConsultationRequestViewed` and `ConsultationRequestUpdated` are their own event types rather than `PatientViewed`/`PatientUpdated`: these families are not patients, and recording their enquiry as a patient record being viewed inflates the one count anybody runs against that event by exactly the set of people never treated here (D089). **Converting is one transaction** — patient inserted, enquiry linked, audit row written — because a patient created with the enquiry still `New` is the state that produces a second record for the same child on the next tap; through `AtomicWrites` with the refusal re-asked inside against the re-read row and `Patient.Create` re-validated there too, proven with `InterleavesOneWriteBeforeTheSecondRead`. The refusal predicate takes the target move as a parameter so it MIRRORS the aggregate rather than approximating it: `Decline()` refuses only a converted enquiry, and one stricter rule would have refused a second tap on Decline that the aggregate allows — D076's defect with the sign flipped. **52 controls deleted and re-run; two stayed green and both are documented rather than left as predictions.** `formatSubmittedAt`'s `timeZone` option cannot be isolated in a suite pinned to `America/New_York` (D072) — the ambient zone is the target zone — so the line says so and points at `daysWaiting`, which does isolate it. `EnquiryActions`'s declined-enquiry test was green with the branch it names deleted, because the main panel already hides *Mark contacted* on anything that is not New and already carries the "kept" sentence: two controls covering for each other, the D077 shape, fixed by asserting on the **Decline** button. Two more `Control:` lines predicted the wrong outcome and were corrected from the run — deleting the inner re-check on the conversion, and deleting the `Converted` refusal branch, both leave the patient count at ONE, because the aggregate refuses from inside the transaction and the rollback takes the duplicate with it. What the re-check actually buys is a 409 with a sentence instead of a 500 with a trace id. API 353 → 374, vitest 374 → 430, Playwright 115 → 121 passed / 2 skipped

- 2026-08-25 · 1.16 four reviewer findings, the timeout nesting · **the number is measured now, and the first measurement said the old one was wrong by a factor of five.** **F2 changed the design and is the reason this converged.** `RequestTimeoutsMiddleware` cancels `RequestAborted` and then AWAITS the pipeline, so it bounds work that observes a token — and `IAuditWriter` observes none by design (D075). The two therefore ADD rather than nest, and the tier's ceiling was the request bound plus however long a wedged audit write felt like taking. Instrumented rather than argued: stall the `AuditEvents` table for 20 s, send `DELETE /notes/{unknown-guid}` under a scaled 2 s request bound, and time the response — **20.1 s before, 4.0 s after**, which is the request bound plus its 2 s grace to a tenth of a second. A bigger constant could not have fixed it, because the tail is exactly the part a request bound cannot see. `UncancellableWriteDeadline` is one scoped deadline per request, bound to `RequestAborted` by `ProviderContextMiddleware` and expiring one shared grace after it fires, so `Ceiling = Request + Grace` holds **by construction rather than by counting how many uncancellable writes a path can reach** — an enumeration being this repository's recurring way of being wrong (D081→D087, D088). **F1** the retry budget modelled ONE command per attempt; the discard's transaction body issues three, so 230 s modelled against 590 s real, and the request bound cancelled retries it claimed to contain. `DiscardCommandsPerAttempt` is **counted on a real DELETE** by interceptors that tally commands between the transaction opening and closing — set it to 1 and the test says "Expected: 1, Actual: 3". The backoff term was left alone on purpose: EF's real delays are ~22 s against 50 modelled, and erring long on a term that only widens a bound is safe. **The invariant now holds and every term in it was observed:** retry budget 9m50s < request bound 10m20s < ceiling 11m50s < BFF 12m30s. **The twelve-and-a-half-minute BFF timeout is the honest cost** and D090 records it as the entry to read first if anyone wants the numbers smaller. **F3** the fetch guard iterated five hard-coded paths while claiming a new call site "arrives bounded or arrives red" — `lib/api/enquiries.ts` had already arrived and was checked by nothing; it happened to be correct, which is the worst way to find that out. It walks `lib` and `app` now and checks `cache: "no-store"` too, which had **no** cross-file guard at all. Swept the class: **five more found**, two fixed (the E2E spec listed six of nine authenticated pages under a comment claiming coverage-by-existence; the Application architecture test used a denylist of the projects that existed when it was written, and stays green on a `ProjectReference` to `Practice.Domain.Tests`), two documented as sound, and one that is not a guard at all — `docs/TEST_STRATEGY.md` claimed the cross-provider tests were "parameterized over the route table" and they are hand-written, one per endpoint. Corrected there and queued as 4.8. **F4** the retry-carry test derived its bound from `command: 250ms` while the harness pinned the real command timeout at 30 s, so the command term could have held any value; the harness takes it now and an interceptor makes the request spend real time in commands, sized to sit between the bound the function derives and the bound it derives with either term missing — 8 s of work under a 10 s bound, 504 at 7 s without the backoff term and at 6 s without the commands factor. **Fourteen controls deleted and re-run; none stayed green**, and four `Control:` lines were corrected from the run — one quoted 00:09:00 where the failure said 00:09:30, and `The_request_bound_outlives_the_retry_budget` could not isolate the term this round is about until its budget stopped being computed by the same function the policy is derived from (both sides moved together — the tautology D042 #2 warns about, one level up). API 374 → 376, vitest 430 → 432, Playwright 121 → 124 passed / 2 skipped

- 2026-08-25 · 1.7 remove Next template assets · `next.svg`, `vercel.svg`, `globe.svg`, `file.svg`, `window.svg` deleted from `web/public/`. **Nothing referenced any of them.** A grep of the whole tree for the five filenames returns only this file's own task line, and the wider sweep — `public/` paths, `.svg` literals, icon and manifest metadata, `next.config.ts`'s headers, the Dockerfile's `COPY … /app/public`, the CSS, the vitest and Playwright specs — finds no use either: the site's iconography is inline JSX in `components/icons`, and `public/img` holds only the generated `children-*` / `headshot-*` responsive sets. So none was load-bearing and nothing needed replacing or re-pointing; `web/public` now contains `img` and nothing else. **No test was written, deliberately.** The change is the deletion of five unreferenced static files — non-behavioural under `tdd.md`'s own clause — and the guard that would fit it, "these five filenames are absent", is precisely the hard-coded list `docs/TEST_STRATEGY.md` calls a test about the day it was written. The existing suites are the verification and all are unchanged: lint, typecheck, `next build` (18 routes, 7 prerendered, including the two static pages that serve `public`), vitest 432, Playwright 124 passed / 2 skipped. Noted and left alone: `web/README.md` is still the create-next-app template and talks about Vercel — prose, not an asset, and not this task

- 2026-08-25 · 1.8 `/health/ready` dependency checks · **the pin did its job: `Readiness_has_no_dependency_checks_yet` asserted `Assert.Equal(0, count)` and went red the moment the checks were registered** — "Set: [] / Not found: \"sql\"" — and is now `Readiness_runs_the_dependency_checks`, asserting a FLOOR on the names rather than an exact set so a third dependency does not have to come here to be allowed. **The split is decided by the consequence, not by the dependency**: a failing liveness probe RESTARTS the container and a failing readiness probe removes it from rotation, so nothing tagged `live` touches another machine — an auto-paused Azure SQL would otherwise restart a process that is working perfectly, and a restart cannot wake a database. Asserted by asking the running application what each probe ran and requiring the two sets to be DISJOINT, which is derived rather than listed. **Readiness tells REFUSED from SLOW and only the first is unready**: a connection string that does not work, an identity never granted, a container that is not there → `Unhealthy` → 503, so a bad revision does not take traffic; running out of probe time → `Degraded` → 200, still in rotation, because the only thing that resumes an auto-paused Azure SQL is a connection and a probe that pulls the replica out removes the traffic that would have woken it. The classifier is the SHAPE of the failure — "did my own budget run out" — not a table of SQL error numbers, which would have been complete the day it was written (D091). **The cost half:** the probe would otherwise hold a vCore-second-billed database online for the life of the replica, so the result is CACHED asymmetrically (5 min on success, 10 s otherwise, singleton because `HealthCheckService` resolves from a fresh scope per probe) and the SQL connection is UNPOOLED — a pooled connection is a live session and a live session cannot auto-pause. Proven against `sys.dm_exec_sessions` rather than asserted about the code. **The probe's own timeout is 2 s and the endpoint's is 5 s**, applied per-route: both health routes inherited `DatabaseTimeouts.Request` — **10m20s** — through the default policy, a number justified for a clinician waiting on a resuming database and absurd for a question an orchestrator will ask again in seconds. The 2 s is anchored on `api/Dockerfile`'s `HEALTHCHECK --timeout=3s`, the only in-tree statement of how long a probe of this container may take; Container Apps' own defaults are deliberately not quoted (D072). **Blob uses managed identity and names nothing**: `DefaultAzureCredential` against the account's https endpoint, or Azurite's key-free shorthand locally, reading `session-audio`'s metadata rather than the account's service properties — the container read is the grant the app actually needs. `/health/ready` is unauthenticated, so the writer emits a name and a status and nothing else; `Exception` is the field that would have published the account, the container and the SQL server name, and it is asserted as an ALLOWLIST over the property names present plus a forced failure carrying a synthetic account URL that must not appear in the body. **15 controls deleted and re-run; ONE stayed green** — `Storage_that_is_not_configured_is_unready` asserted only the status, and the generic refusal catch answers `Unhealthy` for a `NullReferenceException` just as readily, so the two covered for each other (D077's shape). Fixed by asserting the sentence, which is the half an operator acts on. Also recorded: deleting that guard outright fails the BUILD first (`CS8602`), so nullable analysis is a real second layer, and four `Control:` lines were corrected from the run rather than from reading. Fourteen new tests (API 376 → 390), vitest 432 and Playwright 124 passed / 2 skipped unchanged — nothing in `/web` was touched

- 2026-08-25 · 1.17 three reviewer findings, the work that was outside every bound · **F1** `UserManager<PracticeUser>` offers no `CancellationToken` on any of its 82 async methods, so the whole login path observed neither `RequestAborted` nor the deadline — and the unbounded failure-count UPDATE spent the shared grace, so the `LoginFailed` row written after it hit an already-cancelled token and was lost outright rather than late. Fixed as a class, not a call site: `PracticeUserManager` overrides the one protected property all 22 store calls pass through, including the ones nobody has written yet. Binding alone does not save the row, so ordering became a control — `ProviderAuthenticator` audits before it does its bookkeeping on all four failure paths and on sign-in, because a lost row leaves no evidence while a lost increment leaves countable rows. Swept for the class: the other token-less database seams are `IAuditWriter` (bounded by design), `IConsultationNotifier` (F4) and `AtomicWrites`' BEGIN/COMMIT (F4); every EF call in `api/src` already carries one. **F3** `docs/SECURITY.md` §Audit and `InfrastructureServices.cs:52` both still denied the durability gap D090 knowingly accepted; corrected to state the gap rather than deny it, and guarded by a test that reads the token out of `AuditWriter` and out of the paragraph. The re-sweep found three more false claims in the same table — exponential lockout backoff, an IP on the `LoginFailed` row, a breached-password check — none of which exists; all corrected. D072's sixth appearance. **F4** the notifier is bounded at its call site with `.WaitAsync(deadline.Token)` and a directory-walking guard, so the queued mail transport cannot silently move the ceiling; a notification abandoned at the ceiling still answers 201, because the row is committed and `!response.ok` would tell a family their enquiry was not stored. BEGIN/COMMIT are **not** bounded and the claim was corrected instead — `CommitAsync` refuses to start on a cancelled token, so a deadline there would roll back a decision already taken. **No constant in the timeout ladder moved**; that is David's decision and every fix here holds either way. API 396, vitest 432, Playwright 124/2 skipped.

- 2026-08-25 · 2.1 PWA shell · **the service worker is precache-only, and that is the security decision, not a performance one.** `Cache-Control: no-store` governs the HTTP cache and CDN edges and says nothing about the Cache API — script-controlled, unencrypted, disk-backed storage that outlives the tab, the same class of exposure as the `localStorage` non-negotiable #4 bans outright. So `public/sw.js` has **no `cache.put` anywhere**: the only write is `cache.addAll(PRECACHE)` at install over three compile-time constants, the fetch handler is an allowlist rather than a catch-all (non-GET returns, cross-origin returns, navigations are answered live and never stored, everything else falls through with no `respondWith`), and activation deletes every *other* cache on the origin so anything that ever did write clinical content is gone on the next deploy. A route added in slice 7 cannot fall into a handler by accident because there is no handler for it to fall into — asserted by walking `app/(app)` and driving a navigation for each of the nine pages found, not by a list somebody extends (D093). The cost is stated rather than hidden: **this app is not offline-capable and will not become so** — offline, every route returns one static screen. That screen is `public/offline.html` + `offline.css`, hand-written with no JavaScript and no build step, because a Next route would depend on content-hashed chunks and render unstyled the first time a hash outran the cache (D094); its colours are held to `lib/design-tokens.ts` by test, its copy is held to what is true today (three overclaims asserted absent), and its subresources are derived from the HTML so a forgotten one is red rather than unstyled. **Tests read `public/sw.js` off disk and evaluate it in a synthetic `ServiceWorkerGlobalScope`** rather than re-implementing the routing in TypeScript — D072's class, avoided by construction. Registration is in the ROOT layout, not behind the login: the scope is the whole origin, `/login` needs the shell too, and registering inside `app/(app)` would have put the wiring beyond an E2E suite that has no session (D095). Manifest opens on `/today` with `id: "/"`; icons are generated from two committed SVG sources sharing `ShellMark.tsx`'s geometry, with a maskable variant inside Android's 409.6px safe zone and an opaque full-bleed `apple-touch-icon`. CSP gained `worker-src 'self'` and `manifest-src 'self'` — tightenings, since `worker-src` otherwise falls back to `script-src` and would have inherited D042's `unsafe-inline` into a new execution context; `/sw.js` is served `max-age=0, must-revalidate`. **`docs/SECURITY.md` was still publishing `upgrade-insecure-requests` in its "shipped" CSP, removed by D048 two slices ago** — corrected, and the served policy is now asserted end-to-end. **24 control deletions run, 4 came back green and all four were fixed** (D096): the origin guard was covered by the allowlist, the support guard by its own `catch`, the shell-geometry test by a second copy of the path inside the `clipPath`, and one component test was green by construction because `void promise` makes a failure an unhandled rejection rather than a throw — deleted and replaced with a falsifiable claim. Playwright's WebKit cannot express "offline" above a service worker (`setOffline` and `route.abort` both fail the navigation before the worker sees it, verified by probe), so the offline-navigation E2E is Chromium-only and says so; the decision itself is covered by the Vitest suite that runs everywhere. API 396, vitest 469, Playwright 149 passed / 4 skipped.

- 2026-08-25 · 1.18 five reviewer findings, the lockout that concurrency defeated · **F2 was the security hole and it is closed at the statement rather than at the call site.** `UserManager.AccessFailedAsync` is a read-modify-write behind an optimistic `ConcurrencyStamp`, and `UserStore.UpdateAsync` catches the `DbUpdateConcurrencyException` and returns an `IdentityResult` this class discarded — twenty simultaneous wrong passwords counted as one, reproduced here as a test before anything was changed. A retry loop lost because it makes the work quadratic in the width of the attack and every retry draws on the one grace the request shares with its audit row; a lock lost because an in-process one does not hold on a horizontally scaled Container App and `sp_getapplock` costs two round trips to serialise what the engine already serialises. It is now one `UPDATE … CASE WHEN [AccessFailedCount] + 1 >= @max` against the row, in raw parameterised SQL because the atomicity IS the control and it deserves to be readable. **The result is read**: a refusal that was not counted throws rather than answering `invalid`, since a lockout that silently stops counting looks exactly like one that works. Swept the class — **eight discarded `IdentityResult`s, all in `ProviderAuthenticator`**, and the worst was not the lockout: a discarded `SetTwoFactorEnabledAsync` answers "enrolled" with ten recovery codes for an account where MFA is still off. Guarded by reflection over `UserManager`'s surface, not by a list of the eight. **4.3 was pulled forward to 1.19 rather than absorbed here** — the lockout can only count attempts against accounts that EXIST, so guesses at random addresses are still limited by nothing, and a half-limiter shipped inside a commit about a control that looked present and was not would be the same defect again. **F1** the unknown-email branch still rode `RequestAborted` where the rest of the path had moved to the deadline: `Task.Run(action, ct)` cannot interrupt a CPU-bound hash, only refuse to start it, so after the bound fired it threw before its own audit write. Measured before — unknown **504, empty body, 1527 ms, zero `reason=unknown-email` rows**; wrong password **200, `{"status":"invalid"}`, 4696 ms**. After: **200 and 200, byte-identical bodies, 3068 ms and 3061 ms, both audited.** The clock needed more than the token — collapsing `AccessFailedAsync`'s three round trips to one, and giving the unknown branch the same single statement against an id that matches no row, which is the dummy-hash argument applied to the dimension that dominates against a network database. The existing test could not have caught it: no induced latency means nothing is ever cancelled, and it read the response through `EnsureSuccessStatusCode`. **F3** `LoginSucceeded` was written before the sign-in's own writes, so a stalled `UPDATE [AspNetUsers]` produced 504, no session, `LastMfaAtUtc` null, and a row saying the login succeeded. **D092's asymmetry does not transfer and inverts**: the rule that holds is that an audit row is written at the earliest point at which the fact it asserts is already TRUE — immediately for a refusal, last for a success, because a missing success row can be reconstructed from what a session leaves behind and a false one cannot be falsified by anything. `LoginSucceeded` was the only success row in `api/src` written ahead of its own claim. **F4/F5** every control claim in both documents enumerated and checked against the tree — 89 in SECURITY.md, 43 in THREAT_MODEL.md — and **49 were stated in the present tense and are not true** — Serilog and its test, Dependabot, pinned action SHAs, the authorization test that fails CI, the validated token audience, re-auth flows and MFA disable (no endpoints exist), recovery-code regeneration, "reject unknown fields", uploads, Key Vault, the SQL private endpoint, the nonce CSP, three audit event types listed as recorded that nothing writes and four emitted ones missing, audio retention and its alert, offline drafts, the .NET lockfile. Each says **planned, task n.n** rather than being deleted. **§Caching's "test hitting every authenticated route" stayed green** — `web/e2e/auth.spec.ts` walks `app/(app)` rather than listing it — and is now cited as the shape the others need. Two new guards read the tree instead of the prose: the audited-event list is compared against every `AuditEventType.X` under `api/src`, and the rate-limiting sentence fails in both directions. **10 control falsifications run across the 8 tests written or changed; none stayed green**, and two of the falsifications were moves or restorations rather than deletions because the defect was an ordering and a token, neither of which has a line to remove. API 396 → 403, vitest 469, Playwright 149/4 skipped.

---

**Resuming after a context reset?** Read `RESUME.md` at the repo root first — it has
the bootstrap prompt and the current state.
