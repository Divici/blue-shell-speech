# Security Controls

The controls implementing `THREAT_MODEL.md`. Each is testable; a control with no test is an
intention.

**Language rule:** this system is *designed to support HIPAA obligations*. It is never described
as "HIPAA compliant" — in code, docs, README, UI, or conversation with Michelle (§14.1).
Compliance is a property of an organization's practices, not a property of software.

---

## Authentication

ASP.NET Core Identity, self-hosted. No external IdP.

| Control | Detail |
|---|---|
| Password hashing | Identity default (PBKDF2, current iteration count). Never custom crypto |
| Minimum length | 12 characters. **No composition rules, no forced rotation** — both push users toward weaker, reused, written-down passwords (NIST SP 800-63B) |
| Breached-password check | Rejected against a known-compromised list at set time |
| **MFA** | **TOTP, mandatory, not optional.** Single account holding all PHI |
| Recovery codes | Generated once, hashed at rest, single-use, regenerable. Treated as credentials |
| Lockout | Exponential backoff after 5 failures; `LoginFailed` audited with IP |
| Session | `HttpOnly`, `Secure`, `SameSite=Lax`, sliding 30 min, absolute 12 h |
| Re-auth | Required to change password, regenerate recovery codes, or disable MFA |

**MFA cannot be disabled without re-authentication and an audit event.** The account recovery
path is the weakest link in any MFA deployment — an attacker who can reset MFA does not need to
defeat it.

## Authorization

**Server-side, always, in `api`.** Hiding UI is not authorization (`CLAUDE.md` #6).

- Provider identity is derived from the validated token. **Never** from a request body or query
  parameter, however convenient.
- Every query filters by `ProviderId` via an EF Core global query filter — a default that must
  be explicitly overridden, not remembered.
- Resource-level ownership re-checked on every read and write, including reads.
- `PublicId` in URLs; `Id` never leaves the server.

Tested by an integration suite that attempts cross-provider access on **every** endpoint. Adding
an endpoint without adding its authorization test fails CI.

## Transport and network

- HTTPS only. HSTS with a long max-age, `includeSubDomains`.
- `api`: **internal ingress**. No public route exists.
- SQL: private endpoint, public network access disabled.
- Storage: no anonymous access; clinical audio and public resources in **separate containers**.

## Secrets

**No secret exists in the repository, in an image, or in an environment variable holding a
password.**

| Where | Mechanism |
|---|---|
| Local dev | `dotnet user-secrets`, `.env.local` (gitignored) |
| Azure | Managed identity. SQL, Storage, Speech, OpenAI all identity-based |
| Remaining secrets | Key Vault, referenced by Container Apps |
| CI | GitHub OIDC federated credentials — **no long-lived secret** |

`disableLocalAuth=true` on Cognitive Services once identity is wired, which turns key theft into
a non-event by removing keys as an auth path.

Enforcement: PreToolUse hooks block writes to secret-shaped paths; `.gitignore` deny-lists;
secret scanning on push. **The repo is public — a committed secret is compromised the moment it
is pushed, and rotation is the only remedy.**

## Headers and CSP

Common to every response:

```
Strict-Transport-Security: max-age=63072000; includeSubDomains; preload
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
X-Frame-Options: DENY
Permissions-Policy: geolocation=(), camera=(), microphone=(self), payment=()
```

`microphone=(self)` is the one capability the app needs, for dictation, and the only one
granted.

**CSP differs between the two surfaces, deliberately.**

### Public site — shipped

```
default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';
img-src 'self' data:; font-src 'self'; connect-src 'self'; form-action 'self';
frame-ancestors 'none'; base-uri 'self'; object-src 'none'; upgrade-insecure-requests
```

`unsafe-inline` on `script-src` is a **documented deviation**, not an oversight.

A nonce must be unique per response, which requires middleware and forces every page to
render dynamically. That would discard the static prerendering these pages rely on and
defeat the edge caching chosen in `DECISIONS.md` D038 to fix a 22-second cold start. Next.js
hydration emits inline bootstrap scripts, so without a nonce the choice is `unsafe-inline`
or a page that does not work.

**Why it is acceptable here specifically:** the public site renders no user-generated
content and holds no PHI. Every string on it is a compile-time constant in
`lib/site-content.ts`. The XSS surface a nonce would defend is empty.

**This does not generalise.** The moment a public page renders anything a visitor supplied,
this deviation is void.

### Authenticated app — required, slice 2

Nonce-based, **no `unsafe-inline`**. The app is dynamic by necessity, so the tradeoff above
does not apply. This is where the control actually matters: it renders PHI and holds a
session cookie, which is the combination `THREAT_MODEL.md` boundary 1 names.

## Input validation

- Validate at the `api` boundary. `web` validation is UX; it is not a control.
- Reject unknown fields rather than ignoring them.
- Length caps on every string field, enforced in the schema and the database.
- Clinical free text is stored raw and **escaped at render**. Sanitizing on input corrupts
  clinical content — "80% w/ min cues" must survive intact.
- Uploads: content-type allow-list, size cap, extension and magic-byte check.

## Caching

Authenticated responses: `Cache-Control: no-store`. Set at the `(app)` layout, not per-route —
per-route means someone forgets. Asserted by a test hitting every authenticated route.

**Ranked #1 in the threat model** as the most likely accidental disclosure.

## Logging

**No PHI. Ever.**

- Structured logs carry `ProviderId`, `PublicId`, correlation ID, event type, outcome.
- Serilog destructuring policy redacts PHI-bearing types by default — an accidental
  `logger.LogInformation("{@Patient}", patient)` emits redacted fields.
- A test asserts that serializing every PHI-bearing entity produces no clinical values.
- Exceptions: correlation ID to the user, full detail to Azure Monitor with PHI stripped.
- **Build and CI logs are world-readable** (public repo) and treated accordingly.

## Audit

Append-only `AuditEvent`. Application principal has **no `UPDATE` or `DELETE`** grant on it.

Recorded: `PatientViewed`, `NoteSigned`, `NoteAmended`, `NoteDiscarded`, `AudioDeleted`,
`LoginSucceeded`, `LoginFailed`, `MfaChallenged`, `ExportGenerated`.

**Reads are audited, not just writes.** Under HIPAA, access to ePHI is an auditable event; most
homegrown systems log only writes and discover the gap during an investigation.

The read event has to be written on **the path the product actually uses**, not on the one that
looks like the read endpoint. Opening a note goes through `GET /notes/{id}/history`, which
returns full S/O/A/P for every version; that endpoint writes `PatientViewed` and records how many
versions were disclosed. A sibling endpoint that audits correctly but is never called is a
control on paper.

`NoteDiscarded` covers the one delete this application performs: an unsigned draft with nothing
written in any section (`ClinicalNote.CanBeDiscarded`). Anything else is refused by the API, by
the aggregate, and by `TR_ClinicalNotes_PreventDeletingRealNotes`.

`Metadata` never contains clinical content — the audit log is the table most likely to be
exported or read by a third party, which multiplies the blast radius of anything in it.

## Data retention

| Data | Policy |
|---|---|
| Session audio | Deleted when the note is signed. **Hard 30-day cap** regardless. Deletion audited |
| Transcript | Open — see `DATA_MODEL.md`. Leaning delete-with-audio |
| Clinical notes | Retained. Maryland minors' floor **must be verified** (§15) |
| Consultation requests | Retained while `New`/`Contacted`; purge policy needed for `Declined` |
| Audit log | Retained. Never purged by the application |
| Offline drafts | 24h TTL, purge on server ack |

**Deletion is verified, not assumed.** An alert fires on any audio past its deletion date,
because a silently failing lifecycle job looks exactly like a working one.

## Dependencies

Lockfiles committed · Dependabot on · pinned action SHAs · `pull_request_target` banned ·
no secrets exposed to fork workflows.

## Backup and recovery

Azure SQL automated backups, point-in-time restore. **Restore is rehearsed, not assumed** — an
untested backup is a belief. Rehearsal is a §31 go-live deliverable with a recorded RTO.

## What is deliberately not done

| Not done | Why |
|---|---|
| Always Encrypted on identity columns | D012 — does not stop app compromise, breaks patient search, defends against threats we already occupy |
| WAF | Cost. Revisit if the public site attracts real traffic |
| SIEM integration | Azure Monitor is proportionate for one provider |
| Pen test | Not proportionate now. **Would be required before multi-provider** |
| Field-level encryption in the app | Key management complexity exceeds the benefit at this scale |

Each of these is a real gap stated plainly rather than an omission. Reassess when a second
clinician joins — that single change alters more of this document than any technology choice.
