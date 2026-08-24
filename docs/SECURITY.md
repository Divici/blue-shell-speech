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

```
Content-Security-Policy: default-src 'self'; script-src 'self' 'nonce-{random}';
  style-src 'self' 'nonce-{random}'; img-src 'self' data:; connect-src 'self';
  frame-ancestors 'none'; base-uri 'self'; form-action 'self'
Strict-Transport-Security: max-age=63072000; includeSubDomains; preload
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: geolocation=(), camera=(), microphone=(self)
```

Nonce-based, **no `unsafe-inline`**. `microphone=(self)` is the one capability the app needs and
the only one granted.

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

Recorded: `PatientViewed`, `NoteSigned`, `NoteAmended`, `AudioDeleted`, `LoginSucceeded`,
`LoginFailed`, `MfaChallenged`, `ExportGenerated`.

**Reads are audited, not just writes.** Under HIPAA, access to ePHI is an auditable event; most
homegrown systems log only writes and discover the gap during an investigation.

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
