# System Patterns

## Shape

```
Browser (PWA)
    │  cookies only — HttpOnly, Secure, SameSite
    ▼
Next.js container ── route handlers / server actions  (BFF: the only thing the browser talks to)
    │  server-held token
    ▼
ASP.NET Core container ── system of record
    │
    ├── EF Core ──────────► Azure SQL
    ├── Blob storage ─────► audio, short-lived
    ├── Azure Speech ─────► transcription
    └── Azure OpenAI ─────► extraction, then generation (text endpoints only)
```

## Backend layering (presearch §10.3)

`Practice.Api` (controllers, middleware, auth) · `Practice.Application` (use cases) ·
`Practice.Domain` (entities, invariants — no infrastructure references) ·
`Practice.Infrastructure` (EF Core, AI, Speech, external services).

Preserve the boundaries; skip ceremony that buys nothing.

## Provider abstractions

`ITranscriptionService`, `IClinicalExtractionService`, `IClinicalNoteGenerationService`.
The clinical domain never names a vendor. Enables benchmarking, vendor change without domain
rewrite, and fallback behaviour later. This is the dependency-inversion showcase.

## AI pipeline — never collapse these steps

```
audio → transcript → structured extraction → schema validation
      → missing-information analysis → validated session data → SOAP draft
```

Validation sits **between** extraction and generation. Generation only ever sees data that
passed the schema. That is what makes the no-hallucination rule enforceable rather than
aspirational.

## Clinical record integrity

`Draft → Approved/Signed → Amended`. A signed note is not a mutable CRUD row. Amendments create
versions carrying created-by, created-at, approved-at, amended-at, amendment reason, and a
reference to the previous version. Nothing is ever silently overwritten. No hard deletes —
records may need long-term retention, and minors' records especially (§15).

## Async work

Dictation submits a job; the UI polls status. Never a synchronous request expected to survive a
scale-to-zero cold start. Uses `IHostedService` and cancellation tokens.

## Offline drafts (documented deviation)

Encrypted IndexedDB — AES-GCM, non-extractable key, wrapping key in memory only, purge on
server ack, 24h hard TTL, opaque `appointmentId` as the only local metadata. Deviates from
frontend-rules §11; keep the deviation and its risk-analysis entry documented together.

## Conventions

- `ProviderId` on every domain row from day one.
- Opaque GUIDs outward; never sequential integers.
- Store UTC, render `America/New_York`.
- Structured logs carry IDs and correlation IDs. Never clinical content.
- Audit events record the action, not the payload.
