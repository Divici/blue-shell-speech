# Architecture

Derived from `presearch.md` §9, §10, §12, §19, §21, §23 and the decisions in `DECISIONS.md`.

---

## Shape

```text
                    ┌─────────────────────────────────────────┐
   Browser / PWA    │  Azure Container Apps environment        │
        │           │                                         │
        │  HTTPS    │  ┌───────────────┐   internal ingress   │
        └──────────►│  │  web          │──────┐               │
      session       │  │  Next.js      │      │               │
      cookie only   │  │  (public)     │      ▼               │
                    │  └───────────────┘   ┌───────────────┐  │
                    │                      │  api          │  │
                    │                      │  ASP.NET Core │  │
                    │                      │  (internal)   │  │
                    │                      └───────┬───────┘  │
                    └──────────────────────────────┼──────────┘
                                                   │ managed identity
                        ┌──────────────┬───────────┼────────────┐
                        ▼              ▼           ▼            ▼
                   Azure SQL      Blob Storage  Azure Speech  Azure OpenAI
                   (system of      (audio,       (STT)        (text only)
                    record)        30d max)
```

**The `api` container has no public ingress.** Not "an endpoint nobody links to" — internal
ingress, unreachable from the internet. The only route to clinical data is through `web`, which
means the authentication check cannot be skipped by knowing a URL.

---

## Why the BFF boundary is load-bearing

`web` is not a rendering layer with a proxy bolted on. It owns the session.

| Concern | Where it lives |
|---|---|
| Session cookie (`HttpOnly`, `Secure`, `SameSite=Lax`) | `web` |
| MFA challenge flow | `web` → `api` |
| Access token for `api` | **Server memory in `web`. Never the browser.** |
| Authorization decisions | **`api`, always re-checked** |
| PHI rendering | `web` server components |

`web` re-checking authorization is a convenience for the UI. `api` re-checking is the
authorization. Hiding a button is not a control (`CLAUDE.md` non-negotiable #6).

**What this costs:** an extra hop on every request, and Next.js now holds real responsibility —
a bug in a route handler is a security bug, not a rendering bug. Accepted in `DECISIONS.md` D003.

---

## `/web` — Next.js App Router

Server Components by default. `"use client"` only where interactivity demands it, per the
frontend rules.

```text
web/
├── app/
│   ├── (public)/                  # marketing site — static, no auth, no PHI
│   │   ├── page.tsx               # homepage; all nav anchors resolve here
│   │   ├── consultation/          # real intake form, own route
│   │   └── login/
│   ├── (app)/                     # authenticated — dynamic, never cached
│   │   ├── dashboard/
│   │   ├── patients/[publicId]/
│   │   ├── schedule/
│   │   ├── today/                 # daily visit/trip view (§5.6)
│   │   ├── notes/[publicId]/
│   │   └── dictate/
│   ├── api/                       # BFF route handlers — the only path to `api`
│   └── manifest.ts                # PWA
├── components/
│   ├── ui/                        # shadcn — behind the login only
│   └── marketing/                 # hand-built to the comps
├── lib/
│   ├── api-client/                # typed, generated from OpenAPI
│   ├── auth/                      # session, MFA
│   └── offline/                   # encrypted IndexedDB (D005)
└── e2e/
```

**The route-group split is a security boundary, not organisation.** `(public)` is statically
rendered and cacheable. `(app)` sets `dynamic = "force-dynamic"` and `Cache-Control: no-store`
at the layout. A PHI page reaching a CDN cache is a disclosure, and the default in a framework
that likes caching is the wrong default here.

The public site is hand-built. shadcn/ui appears **only** behind the login — the marketing pages
are pixel-work against comps, and a component library fights that.

### Rendering strategy

| Surface | Strategy | Why |
|---|---|---|
| Marketing pages | Static, prerendered | LCP ≤ 2.5s over a cold start; no per-request work |
| Consultation form | Server Component + server action | No client-side validation-only path |
| Dashboard / schedule | Dynamic SSR | PHI, per-request auth |
| Dictation | Client Component | `MediaRecorder`, timers, IndexedDB |
| Note review | Server shell, client editor | Only the editor needs to be interactive |

---

## `/api` — ASP.NET Core (.NET 10)

Structure follows §10.3. Four projects, boundaries respected, no ceremony added.

```text
api/src/
├── Practice.Api/              # controllers, middleware, auth, Program.cs
├── Practice.Application/      # use cases: Patients, Scheduling, Documentation, Dictation
├── Practice.Domain/           # entities, invariants, no EF or ASP.NET references
└── Practice.Infrastructure/   # Persistence, AI, Speech, ExternalServices
```

**`Practice.Domain` references nothing.** Note immutability, the amendment rule, and the
"never fabricate a number" rule are domain invariants — they must be testable without a database
and unavoidable from any caller. A rule enforced in a controller is a rule that a second
controller forgets.

### Provider abstractions (§8.1)

`ITranscriptionProvider` and `ITextGenerationProvider` are interfaces in `Application`,
implemented in `Infrastructure`.

This is not speculative indirection. It is required by three live facts:

1. **Blocker #1** may force a different model deployment or vendor entirely.
2. **§8.2 benchmarking** compares providers on synthetic data — through OpenRouter, which must
   never see PHI. The seam is where that prohibition is enforced in code, not in a comment.
3. **§19 reliability** requires the app to work when a provider is down.

### Cross-cutting middleware

Ordered, because order is the behaviour:

1. Correlation ID — generated at `web`, forwarded, on every log line
2. Exception handling → RFC 7807 `ProblemDetails`, **correlation ID only, never PHI**
3. Authentication → authorization
4. Audit — writes `AuditEvent` including reads (`PatientViewed`)
5. Rate limiting — login and dictation upload

---

## The dictation pipeline

The one place where "just call the API and wait" is guaranteed to fail. `api` scales to zero;
a synchronous request spanning transcription plus two model calls will hit a cold start, a
timeout, or a locked phone screen.

```text
Client                     web (BFF)         api                 Workers
  │                            │               │                    │
  ├─ record take (≤300s) ──────┤               │                    │
  ├─ encrypt → IndexedDB       │               │                    │
  ├─ chunked resumable upload ►│──────────────►│ SAS → Blob         │
  │                            │               │ DictationSession   │
  │                            │               │   = Uploading      │
  │                            │               ├───enqueue─────────►│ transcode → 16kHz PCM
  │                            │               │                    │ Azure Speech
  │  ◄── poll status ──────────┤◄──────────────┤                    │ de-identify
  │      (Transcribing…)       │               │                    │ extract → validate
  │                            │               │                    │ missing-info
  │                            │               │                    │ generate SOAP
  │  ◄── ReadyForReview ───────┤◄──────────────┤◄───────────────────┤ re-identify
  │                            │               │                    │
  ├─ review chips, sign ──────►│──────────────►│ ClinicalNote signed
  │                            │               │ audio deleted
```

**Background job + status polling, never a synchronous request.** Status is an explicit enum
because Michelle is standing in a driveway wanting to know what is happening — `Transcribing`
and `Generating` are different answers to "is it stuck?"

**Every stage is independently retryable**, and each has a manual fallback (§19). Transcription
down means the audio is preserved and manual entry works. Generation down means the transcript
and structured observations survive. **Patient records, scheduling, and manual notes never
depend on AI availability** — that is a hard requirement, not a resilience nicety.

### iOS realities, designed for rather than discovered

| Constraint | Design response |
|---|---|
| No Background Sync API in Safari | Feature-detect; sync-on-foreground plus an `online`-event retry |
| Only home-screen PWAs escape 7-day storage eviction | Detect standalone mode; prompt to install. Offline drafts are not durable in a tab |
| `MediaRecorder` emits mp4/AAC, not webm | Server-side transcode to 16 kHz PCM before Azure Speech |
| Screen lock kills the tab | Takes are capped at 300s and uploaded per-take, not per-session |

### Where the workers run

A hosted service inside the `api` container, driven by a queue — **not** a separate Container
App.

**Why:** a scale-to-zero container with no HTTP traffic will not process a queue, so a separate
worker needs `minReplicas: 1` (~$14/mo) or KEDA scaling on queue depth. Co-locating means the
upload request that enqueues the job is also what wakes the container.

**What it costs:** a long transcription competes with request handling for CPU in the same
0.25 vCPU container, and a deploy mid-job interrupts it. Jobs are therefore idempotent and
resumable from their last completed stage. **Revisit if** transcription latency becomes visible
to Michelle — the fix is KEDA queue-depth scaling, which is a config change.

---

## Data flow classification

| Path | Carries PHI | Control |
|---|---|---|
| Browser → `web` | Yes | TLS, `HttpOnly` session cookie |
| `web` → `api` | Yes | Internal ingress, managed identity |
| `api` → SQL | Yes | Private endpoint, TDE, managed identity, no SQL password |
| `api` → Blob | Yes (audio) | Managed identity, deleted on sign, 30-day hard cap |
| `api` → Azure Speech | Yes (audio) | BAA-covered. **HIPAA-eligible** |
| `api` → Azure OpenAI | **De-identified** | Text endpoints only. BAA-covered |
| `api` → OpenRouter | **Never** | Synthetic benchmarking only, enforced at the provider seam |
| `api` → email | **No content** | "New consultation request, sign in to view" |
| `api` → logs | **Never** | IDs and correlation IDs only |

---

## Environments (§23)

Two: **local** and **production**. No staging.

**Why not three:** a staging environment for a solo practice doubles the infrastructure and the
secret surface to rehearse a deploy nobody is racing. CI plus a smoke test on the real deploy
covers the risk at a fraction of the cost.

**What it costs:** the first production deploy of any slice is the first real-infrastructure
deploy. Mitigated by revision-based rollout in Container Apps — traffic shifts to a new revision
only after health checks pass, and rollback is repointing traffic at the previous revision.

Local runs `docker compose`: SQL Server in a container, Azurite for blob, and **the real Azure
Speech and OpenAI endpoints against synthetic data** — mocking an AI provider locally teaches
you nothing about how it actually behaves.

---

## Observability (§21)

Structured logging (Serilog) → Azure Monitor. Correlation ID generated at `web`, forwarded on
every hop, surfaced to the user in error states so a support conversation has a handle that is
not a patient name.

**No PHI in logs, ever.** Not in messages, not in exception details, not in a serialized DTO
that happens to contain a `FirstName`. The enforcement is a destructuring policy that redacts
known PHI-bearing types, plus a test that asserts it — because "remember not to log the patient
object" is not a control.

Health endpoints: `/health/live` and `/health/ready`. Ready checks SQL and blob; it does **not**
check Azure OpenAI, because AI being down must not take the app down (§19).

---

## Open

1. **Cold-start latency.** Unmeasured. A deliverable, not an assumption (D001).
2. **Container Apps HIPAA eligibility.** Unconfirmed — blocker #3. App Service for Containers is
   the swap; `/infra` changes, nothing above it does.
3. **Queue technology.** Azure Storage Queues (already have the storage account, free-tier
   friendly) vs Service Bus (sessions, dead-lettering, better retry semantics). Leaning Storage
   Queues; Service Bus is worth it only if ordering across takes turns out to matter.
