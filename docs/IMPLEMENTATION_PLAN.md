# Implementation Plan

Ten vertical slices (§31). Each ships end-to-end — schema, API, UI, tests, deployed.
Never "build the backend, then the frontend."

**`slice-gauntlet` refuses to run without acceptance criteria.** They are below, frozen per
slice at kickoff. A criterion may be clarified during a slice; it may not be added or relaxed —
that is Guardrail 1, and it exists so a slice cannot be declared done by moving the line.

**Exit condition is always "every acceptance criterion demonstrably met," never a round count.**

Review lanes per slice are named. Do not run all four on everything.
`/super-review` runs **at slice boundaries, never inside a gauntlet loop** — the two optimize
opposite axes and give a builder contradictory pressure.

---

## Slice 0 — Monorepo and pipeline

Not in §31, and required before slice 1 can be verified. A slice that cannot deploy cannot be
demonstrated, and "deployed" is in every acceptance list below.

**Acceptance**
- [ ] `/web` `/api` `/docs` `/infra` exist; `docker compose up` runs SQL + Azurite + both apps
- [ ] TypeScript strict, no `any` in committed code; `dotnet build` warnings-as-errors
- [ ] CI on every PR: lint, typecheck, unit tests, build both containers
- [ ] CI deploys to Azure via **OIDC federated identity — no long-lived secret**
- [ ] Both containers reach Container Apps; `/health/live` and `/health/ready` return 200
- [ ] `api` confirmed **unreachable from the public internet** (test, not assertion)
- [ ] Secret scanning active; a test commit with a fake key is blocked

**Lanes:** spec critic · `/super-review`

---

## Slice 1 — Public website + deployment

**Acceptance**
- [ ] Sections in order: Header → Hero → three badges → Meet Your SLP (+ service chips) →
      Getting Started is Easy → Get In Touch → Footer
- [ ] Copy matches `SITE_CONTENT.md` exactly; credentials not embellished
- [ ] **Service chips include AAC**
- [ ] Nav anchors scroll on the homepage; `Free Consultation` → `/consultation`;
      `Login` → `/login`, styled secondary
- [ ] **No services grid. No testimonials. No Resources tab**
- [ ] `/consultation` posts a real request, persists it, sends a **contentless** notification
- [ ] Consultation form: validation, error, loading, and success states all implemented
- [ ] Lighthouse ≥ 90 perf / ≥ 95 a11y / ≥ 95 best-practices / ≥ 95 SEO on mobile
- [ ] LCP ≤ 2.5s, INP ≤ 200ms, CLS ≤ 0.1 on a warm container
- [ ] **Cold-start latency measured and recorded** (D001) — a number, not an adjective
- [ ] All body copy ≥ 4.5:1; the comps' light gray is darkened, deviation noted
- [ ] Keyboard-navigable end to end; visible focus; correct heading order
- [ ] `children.png` and the headshot ship as responsive AVIF/WebP, not multi-MB PNG
- [ ] Missing assets generated as optimized SVG (logo, waves, blobs, bubbles, icons)
- [ ] Contact details come from env config; **no address anywhere in the tree**

**Lanes:** spec critic · visual gauntlet · `/super-review`

---

## Slice 2 — Provider authentication

**Acceptance**
- [ ] Register (seeded, not public), login, logout
- [ ] **TOTP MFA mandatory** — no path to the app without it
- [ ] Recovery codes: generated once, hashed at rest, single-use, regenerable
- [ ] Re-auth required to change password, regenerate codes, or disable MFA
- [ ] Lockout with exponential backoff after 5 failures
- [ ] Session `HttpOnly` + `Secure` + `SameSite=Lax`; sliding 30 min, absolute 12 h
- [ ] **No token or session material in `localStorage`/`sessionStorage`** — lint-enforced
- [ ] `LoginSucceeded`, `LoginFailed`, `MfaChallenged` audited with IP
- [ ] Authenticated routes return `Cache-Control: no-store` — asserted by test
- [ ] Unauthenticated access to any `(app)` route redirects, never renders
- [ ] `api` rejects a request whose provider identity comes from the body rather than the token
- [ ] Rate limiting on `/login` per IP and per account

**Lanes:** spec critic · **security adversary** · `/super-review`

---

## Slice 3 — Patient CRUD end-to-end

**Acceptance**
- [ ] Create, read, update, soft-delete patients; guardians and addresses
- [ ] `ProviderId` on every row; **global query filter** applied, not per-query `WHERE`
- [ ] `PublicId` in every URL; `Id` never in a response
- [ ] `HasLegalAuthority` distinct from `IsPrimaryContact` and enforced on any share path
- [ ] Search works on name (this is what D012 protected)
- [ ] **Cross-provider access test on every endpoint** — 404, not 403 (403 confirms existence)
- [ ] `PatientViewed` audited on read
- [ ] Empty, loading, and error states implemented
- [ ] Synthetic seed data only; **zero real patient data anywhere**

**Lanes:** spec critic · security adversary · `/super-review`

---

## Slice 4 — Scheduling end-to-end

**Acceptance**
- [ ] Appointment CRUD: type, start, duration, status, address, notes
- [ ] **Stored UTC, rendered `America/New_York`**; correct across a DST boundary — tested
- [ ] `Evaluation` ships as an appointment type
- [ ] Daily visit/trip view: chronological, patient, address, duration, travel block, mileage
- [ ] **Mobile-first and usable one-handed** — this is used in a car, stationary
- [ ] No patient-identifying data sent to any mapping provider
- [ ] Status transitions audited

**Lanes:** spec critic · visual gauntlet · `/super-review`

---

## Slice 5 — Goals + manual SOAP notes

The clinical core. **Must work with no AI whatsoever** (§19).

**Acceptance**
- [ ] Goal CRUD; domain enum **includes AAC**; `AacModality` and `AacDeviceNotes` nullable
- [ ] Manual SOAP note creation, edit while draft, sign
- [ ] **A signed note cannot be edited.** Enforced by database trigger, not only by the app
- [ ] Amendment creates a **new version**; `AmendmentReason` required; prior version retained
- [ ] Exactly one `IsCurrent` note per appointment — filtered unique index
- [ ] `ContentHash` computed at signature
- [ ] Version history visible and navigable in the UI
- [ ] `NoteSigned` and `NoteAmended` audited
- [ ] **Domain invariants unit-tested with no database** — `Practice.Domain` references nothing
- [ ] Unsaved note work survives navigation and refresh

**Lanes:** spec critic · security adversary · `/super-review`

---

## Slice 6 — Audio capture + transcription

**Acceptance**
- [ ] Installable PWA; manifest, service worker, offline shell
- [ ] **One record button** toggling to pause/resume
- [ ] **300s hard cap per take**, enforced client-side *and* by `CHECK` constraint
- [ ] Multiple takes per session, ordered by `SequenceNumber`
- [ ] Chunked resumable upload — a 9.6 MB take survives a dropped connection
- [ ] iOS mp4/AAC transcoded server-side to 16 kHz PCM
- [ ] Drafts in **encrypted IndexedDB**: AES-GCM, non-extractable key, in-memory wrapping key
- [ ] Purge on server ack; **24h hard TTL enforced on read and on a timer**
- [ ] `localStorage`/`sessionStorage` contain nothing — asserted by test
- [ ] Background Sync **feature-detected**; fallback to sync-on-foreground + `online` retry
- [ ] Standalone mode detected; install prompted; **durability limits explained to the user**
- [ ] Transcription runs as a background job with status polling — no synchronous request
- [ ] Status enum surfaced meaningfully (`Transcribing` ≠ `Generating`)
- [ ] Transcription failure preserves audio, offers retry, allows manual entry
- [ ] **Audio deleted on signature; 30-day hard cap; deletion audited**
- [ ] No visual interaction required once recording starts (§7.7)

**Lanes:** spec critic · security adversary · visual gauntlet · `/super-review`

---

## Slice 7 — Structured extraction + validation

**Acceptance**
- [ ] De-identification before any text reaches Azure OpenAI; roster first, NER second
- [ ] Token map in memory only — **never persisted, logged, or transmitted**
- [ ] Extraction uses a **strict JSON schema**, not "return JSON" in a prompt
- [ ] Every quantitative field nullable; **no defaults**
- [ ] `sourceQuote` + `sourceOffset` required; unresolvable offset ⇒ claim rejected
- [ ] `goalId` outside the supplied active set ⇒ rejected
- [ ] **Completeness invariant:** every active goal appears in addressed ∪ notAddressed ∪ missing
- [ ] Rejections degrade to **missing**, never to a substituted value
- [ ] Missing-info analysis is deterministic — **no model call**
- [ ] Review chips fillable by typing **or** by tapping to speak
- [ ] Provider abstraction in place; **OpenRouter implementation throws on non-synthetic data**
- [ ] Synthetic eval corpus exists; numeric accuracy reported **separately from WER**
- [ ] De-identification name recall **measured**, not assumed

**Lanes:** spec critic · security adversary · `/super-review`

---

## Slice 8 — SOAP generation + approval

**Acceptance**
- [ ] Generation receives **validated structure only — never the transcript**
- [ ] **Numeric-provenance check:** every number in the output traces to validated input, or the
      job fails with no draft produced
- [ ] Missing information renders as a **visible placeholder**, never smoothed over
- [ ] Output is always a draft; AI never finalizes a record
- [ ] Michelle can view the source sentence behind any extracted figure
- [ ] Generation unavailable ⇒ structured data retained, chips work, manual assembly possible
- [ ] End-to-end on synthetic audio: record → transcribe → extract → validate → generate → sign
- [ ] Eval suite runs and reports; **does not gate CI**

**Lanes:** spec critic · security adversary · visual gauntlet · `/super-review`

---

## Slice 9 — Audit / capacity / security hardening

**Acceptance**
- [ ] Every event type in `SECURITY.md` emitted and queryable
- [ ] App principal has **no `UPDATE`/`DELETE` on `AuditEvent`** — verified by attempting it
- [ ] `Metadata` contains no clinical content — asserted by test
- [ ] Serilog destructuring policy redacts PHI types; **test proves no PHI in any log line**
- [ ] CSP nonce-based, no `unsafe-inline`; full header set present
- [ ] Capacity banner against internal counters; admin alerts on threshold
- [ ] Rate limiting on login and dictation upload
- [ ] Every deferred-audio deletion alerts
- [ ] Dependency scanning clean; no `pull_request_target`; no fork-secret exposure

**Lanes:** spec critic · **security adversary** · `/super-review`

---

## Slice 10 — Production-readiness verification

Nothing here is a documentation exercise. Every item is a live deliverable with sign-off.

**Acceptance**
- [ ] Security risk analysis complete (§14.6) — where ePHI lives, travels, who reaches it,
      threats, controls, likelihood, impact, residual risk, review cadence
- [ ] `HIPAA_DATA_FLOW.md` covers **every** hop touching PHI
- [ ] Vendor review for every service that touches ePHI (§14.5)
- [ ] **BAA verified, naming the correct entity** — blocker #4
- [ ] Maryland retention and minors'-records requirements verified against authoritative sources
- [ ] **Container Apps HIPAA eligibility confirmed**, or swapped to App Service — blocker #3
- [ ] **PHI-safe model deployment in place** — blocker #1
- [ ] Modified Abuse Monitoring resolved, or residual risk **explicitly signed off** — blocker #2
- [ ] Retention policies implemented **and tested**, not merely written
- [ ] **Restore from backup rehearsed**, RTO recorded
- [ ] Confirmation no real PHI ever entered a synthetic-only path
- [ ] One full `/super-review` pass over the whole codebase
- [ ] §34 answered yes: *would we put an actual child's medical information through this exact
      data path?*

**Lanes:** all four, then sign-off

---

## Sequenced later — designed in, not cut

Seams exist now; the features come after slice 10.

1. Document / file upload — `PatientDocument` mirrors `ResourceDocument`
2. Evaluation report authoring — the appointment type already ships
3. Superbill PDF — `Encounter` already ships
4. Live Azure Cost Management API — threshold logic already runs on internal counters

---

## Standing rules

- **Synthetic data only** until slice 10 closes every blocker.
- **Never** claim "HIPAA compliant" anywhere.
- Every slice ends green: tests pass, deployed, criteria demonstrably met.
- Drift from `presearch.md` is recorded as `[REVISED - Slice N]`, never silently applied.
- A spec-critic finding needs a **failing test or a spec citation**. No evidence, no finding.
