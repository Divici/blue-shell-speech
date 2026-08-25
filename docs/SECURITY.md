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
`LoginSucceeded`, `LoginFailed`, `MfaChallenged`, `ExportGenerated`,
`ConsultationRequestReceived`, `ConsultationNotificationFailed`,
`ConsultationRequestViewed`, `ConsultationRequestUpdated`.

**Reads are audited, not just writes.** Under HIPAA, access to ePHI is an auditable event; most
homegrown systems log only writes and discover the gap during an investigation.

The read event has to be written on **the path the product actually uses**, not on the one that
looks like the read endpoint. Opening a note goes through `GET /notes/{id}/history`, which
returns full S/O/A/P for every version; that endpoint writes `PatientViewed` and records how many
versions were disclosed. A sibling endpoint that audits correctly but is never called is a
control on paper.

`NoteDiscarded` covers the one delete this application performs: an unsigned, unsuperseding
draft with nothing written in any section (`ClinicalNote.CanBeDiscarded`). Anything else is
refused by the API, by the aggregate, and by `TR_ClinicalNotes_PreventDeletingRealNotes` —
**an amendment included**, whatever it holds, because deleting one leaves the visit with no
current note and the signed version it supersedes reachable by nothing the product renders.

**The delete and its audit row are one transaction.** They were two saves on the request's
cancellation token, so backgrounding the app mid-request removed the row and abandoned the
record of it. The only explicit transaction in the API is here, for that reason (D071), and it
goes through `AtomicWrites.WriteAtomicallyAsync` — the transaction inside the retrying
execution strategy, the change tracker reset on every attempt, and the commit on
`CancellationToken.None`. The reset is not tidiness: a retry re-ran the body against a change
tracker still holding what the failed attempt had staged, and wrote **two** `NoteDiscarded`
rows for one deletion into a table nothing can UPDATE or DELETE (D075).

**No audit write is cancellable.** `IAuditWriter.WriteAsync` takes no `CancellationToken`, and
`AuditWriter` saves on `CancellationToken.None`. An audit row records something that already
happened; the caller going away does not un-happen it. This is a property of the seam rather
than a habit at the call sites, because with a token parameter present CA2016 requires every
call site inside a method that has one to forward it — the analyzer enforces the defect (D075).

**Refused deletes are audited too**, as `NoteDiscarded` with `AuditOutcome.Failure` and a
fixed-vocabulary reason: `not-found`, `amendment`, `has-content`, `signed`. A log holding only
the deletions that succeeded cannot answer "did someone walk the note ids with DELETE", which
is the question it exists for. The `not-found` rows are the ones that answer it — the response
deliberately cannot distinguish "not yours" from "does not exist" (404 either way), so the
audit table is the only place the attempt is recorded at all.

The reason describes **what the row is, not how it came to exist**: a signed amendment audits
as `signed`, because it is a signed clinical record. `amendment` means a draft amendment being
written, which is a different thing to have tried to delete. Asking about lineage first made
every signed note from v2 onward audit as `amendment`, so a count of attempts on signed records
was short by exactly the set of amended — i.e. contested — records (D076).

Unhandled failures answer with RFC 9457 problem details and no stack trace
(`AddProblemDetails` + `UseExceptionHandler`, registered before the automatic developer
exception page). An error body crosses the `web` → `api` boundary exactly like a log line, and
the same rule applies: no SQL text, no parameter values, no PHI.

**Guardian and address writes are audited**, as `PatientUpdated` against the patient's public
id, with a fixed metadata vocabulary:
`action=guardian-added|guardian-updated;guardian={publicId};legalAuthority=granted|withheld;primaryContact=yes|no`
and `action=address-added|address-corrected;address={publicId};type=Session|Billing`.
`HasLegalAuthority` gates who may receive a child's records, so "who was allowed, and when did
that change" has to be answerable after the fact — a custody arrangement cannot be
reconstructed from the current row. Adding a guardian previously wrote nothing (D073).

**The public consultation form's write is audited**, as `ConsultationRequestReceived` with
`source=public-form;sourceIpHash={hash|none}`, in the same transaction as the row itself
(`AtomicWrites.WriteAtomicallyAsync`). It is the only write in this system performed by an
UNAUTHENTICATED caller: no session, no actor id, and nobody to ask afterwards, so the audit row
is the only evidence a submission flood leaves behind. Adding an entity is not audited by
default — the guardian write shipped writing nothing (D073) — so this exists because it was
written. `IpAddress` is left NULL on that row on purpose: hashing the address on the enquiry
and then recording it in full one table over would undo the decision entirely, in the table
this document says is never purged.

A notification that cannot be delivered is `ConsultationNotificationFailed`, its own event
rather than a `Failure` outcome on the arrival — the enquiry DID arrive, and a row saying
otherwise is what gets counted a year later. A silently failing notifier looks exactly like a
working one.

**Reading the consultation inbox is audited**, as `ConsultationRequestViewed` — both the
detail read (`scope=detail`, naming the enquiry) and the listing
(`scope=list;count=n;status=…`, naming none, because a list has no single subject). The
enquiry is not PHI: the family are not patients and there is no treatment relationship. It is
a child's first name beside a parent's description of that child's difficulties, which is the
same category of information whatever the regulation calls it, so it is read under the same
controls.

**Its own event type rather than `PatientViewed`**, which is the one place D076's argument
against growing the vocabulary is overruled. `PatientViewed` answers "who accessed a child's
medical record"; recording an enquiry there would inflate that count by exactly the set of
people who have never been treated here, and it is a count read once, years later, by somebody
who was not present. The `count=` on the listing rows follows D065's `versions=n` reasoning:
"somebody opened the inbox" cannot tell one enquiry from forty apart afterwards.

**Both endpoints that disclose an enquiry write the row**, not only the one the UI happens to
call today — that is the whole of D065, applied before a second reader exists rather than
after. The detail read is the only endpoint returning what the parent wrote; the summary type
carries no `Concerns` member at all, so the listing cannot become a second, larger disclosure
of the same content by accident.

Moving an enquiry is `ConsultationRequestUpdated` with
`action=contacted|converted|declined`, and the conversion also carries `patient={publicId}` —
the opaque id of the record it became, which is the answer to "where did this family come
from". The transition and its audit row commit together through
`AtomicWrites.WriteAtomicallyAsync`, as does the conversion, which creates the patient, links
the enquiry and writes the row in one transaction: a patient created with the enquiry still
saying `New` is the state that produces a SECOND record for the same child on the next tap.

`Metadata` never contains clinical content — the audit log is the table most likely to be
exported or read by a third party, which multiplies the blast radius of anything in it. The
guardian rows carry opaque ids and fixed words only: no names, no numbers, no relationship.

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
