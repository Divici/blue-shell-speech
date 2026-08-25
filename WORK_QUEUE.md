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
- [ ] **1.4 Guardian + address forms** — add/edit on a patient page. `HasLegalAuthority`
      must be its own explicit control, never inferred from primary contact.
- [ ] **1.5 `ConsultationRequest` entity + persistence** — closes slice 1's one unmet
      criterion. Includes the **contentless** notification ("New consultation request,
      sign in to view") and `SourceIpHash`. Wire `app/consultation/actions.ts`, removing
      its `TODO(slice 3)`.
- [ ] **1.6 `Encounter` + `ResourceDocument` entities** — ship empty per the scope ledger.
      Adding a billing table to a live clinical database later means backfilling history.
- [ ] **1.7 Remove Next template assets** — `next.svg`, `vercel.svg`, `globe.svg`,
      `file.svg`, `window.svg` in `web/public/`.
- [ ] **1.8 `/health/ready` dependency checks** — register SQL and blob under the "ready"
      tag. Removes the `TODO(slice 3)` in `Program.cs`, and **flips the test that pins
      zero checks** — that failure is the reminder, by design.
- [ ] **1.9 Four failing `mobile-safari` E2E tests on the public site** — pre-existing,
      confirmed against a clean tree while working 1.2, so not a regression from any
      recent slice. `homepage.spec.ts:63` and `:212` cannot find the header's `About`
      link at an iPhone 14 viewport; `:172` and `:183` (consultation submissions) fail
      only under full-suite parallel load. **A suite with known reds stops being a
      signal** — either the nav is genuinely unreachable on a phone, which is a real
      defect on the page parents actually land on, or the test is desktop-shaped like the
      one in D040.
- [ ] **1.10 Fix the four reviewer findings against `0d75f37`** — run this BEFORE the
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

---

**Resuming after a context reset?** Read `RESUME.md` at the repo root first — it has
the bootstrap prompt and the current state.
