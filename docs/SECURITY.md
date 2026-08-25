# Security Controls

The controls implementing `THREAT_MODEL.md`. Each is testable; a control with no test is an
intention.

**Language rule:** this system is *designed to support HIPAA obligations*. It is never described
as "HIPAA compliant" — in code, docs, README, UI, or conversation with Michelle (§14.1).
Compliance is a property of an organization's practices, not a property of software.

**Tense rule, and it is load-bearing.** Every sentence in this file is either a description of
code that exists today or is marked **Planned — WORK_QUEUE n.n**. Nothing here is written in the
present tense about work that is queued. Three consecutive reviews found controls described here
and absent from the tree, twice in paragraphs a previous sweep had just corrected — an exponential
lockout backoff, an IP on the login audit row, a breached-password list, a Serilog redaction
policy, Dependabot, pinned action SHAs, an authorization test that fails CI. **A control described
and absent reads as STRONGER than no control at all**, because the next reader checks whether the
question was considered rather than whether it was answered (D072). A row that says "planned"
keeps the intent on the page and makes the gap countable; deleting it loses both.

---

## Authentication

ASP.NET Core Identity, self-hosted. No external IdP.

| Control | Detail |
|---|---|
| Password hashing | Identity default (PBKDF2, current iteration count). Never custom crypto |
| Minimum length | 12 characters. **No composition rules, no forced rotation** — both push users toward weaker, reused, written-down passwords (NIST SP 800-63B) |
| Breached-password check | **NOT BUILT.** No compromised-password validator is registered in `AddInfrastructure`. Intended, unqueued |
| **MFA** | **TOTP, mandatory, not optional.** Single account holding all PHI |
| Recovery codes | Generated once at enrolment, hashed at rest, single-use. Treated as credentials. **Regeneration after enrolment is not built** — there is no endpoint for it; the ten issued at enrolment are all there are |
| Lockout | Fixed **15 minutes** after 5 failures (`IdentityOptions.DefaultLockoutTimeSpan`), not exponential. The count is a **single UPDATE the database serialises** (`ILoginBookkeeping`), not Identity's read-modify-write — see below. `LoginFailed` is audited with the reason and the actor; **it does not carry an IP** — `AuditEvent.IpAddress` is filled on patient reads only |
| Rate limiting on login | **Planned — WORK_QUEUE 1.19**, pulled forward from 4.3 because 1.18 measured it as an open hole rather than a scheduled one. Nothing in `api` limits attempts by source or by account; `web/lib/rate-limit.ts` serves the public consultation form only. The lockout above is the whole of the throttle today, and it does not touch attempts against addresses that have no account |
| Session | `HttpOnly`, `Secure` in production, `SameSite=Lax`, sliding 30 min, absolute 12 h (`web/lib/auth/session.ts`) |
| Re-auth | **Planned.** `PracticeUser.LastMfaAtUtc` is recorded on every completed sign-in so the timestamp exists, but no endpoint changes a password, regenerates recovery codes, or disables MFA, so there is nothing yet to gate. When one lands, it is gated on that timestamp |

**The lockout counts concurrent attempts, and did not until D097.** Identity's
`AccessFailedAsync` reads the user, increments in memory, and saves with the row's previous
`ConcurrencyStamp` in the WHERE clause; `UserStore.UpdateAsync` catches the resulting
`DbUpdateConcurrencyException` and returns a failed `IdentityResult`, which the caller discarded.
Measured: **four waves of twenty simultaneous wrong passwords — eighty attempts — left
`AccessFailedCount = 4` and `LockoutEnd = NULL`.** One increment survived per wave, so an N-wide
caller bought N guesses per counted failure. It is now one statement against the row, and
`ProviderAuthenticator` refuses to answer at all if that statement changes nothing:
a refusal that is not counted is a guess that cost the attacker nothing.

**MFA cannot be disabled by any path this application exposes, because no such path exists.**
The account recovery path is the weakest link in any MFA deployment — an attacker who can reset
MFA does not need to defeat it — so when one is built it carries re-authentication and its own
audit event. Today the only recovery is a single-use recovery code, which signs in and does not
alter the second factor.

## Authorization

**Server-side, always, in `api`.** Hiding UI is not authorization (`CLAUDE.md` #6).

- Provider identity is derived from the request context `api` resolves, **never** from a request
  body or query parameter, however convenient. **Today that context comes from a header the BFF
  forwards** (`ProviderContextMiddleware`, resolved by opaque `PublicId` against an active
  provider row) and the only thing stopping anyone else sending it is internal ingress. **A
  validated token audience is planned — WORK_QUEUE 4.4**, and `AuthEndpoints`' own docstring has
  said so since it was written.
- Every query against patient data filters by `ProviderId` via an EF Core global query filter —
  a default that must be explicitly overridden, not remembered. A null provider matches **no**
  rows. Identity's tables, `Providers` and `AuditEvents` are deliberately unfiltered: they are
  not patient data, and resolving the provider is the step that arms the filter.
- Resource-level ownership re-checked on every read and write, including reads.
- `PublicId` in URLs; `Id` never leaves the server.

Tested by an integration suite that attempts cross-provider access on the endpoints it names
(`PatientIsolationTests`, `NoteImmutabilityTests`, `ConsultationInboxTests`). **It is a named
list, not the route table**: nothing enumerates `EndpointDataSource`, so adding an endpoint
without an authorization test does NOT fail CI. This sentence claimed the opposite for five
slices. **Planned — WORK_QUEUE 4.8**, parameterized over the route table so a new endpoint
arrives covered or arrives red.

## Transport and network

- HTTPS only. HSTS with a two-year max-age, `includeSubDomains`, `preload`
  (`web/next.config.ts`, asserted end-to-end).
- `api`: **internal ingress**. No public route exists (`infra/provision-apps.sh --ingress
  internal`, and the script prints the command to verify it independently).
- Storage: no anonymous access; clinical audio and public resources in **separate containers**
  (`session-audio`, `public-resources`, created separately in `infra/provision-platform.sh`).
- SQL: **public network access is currently ALLOWED for Azure services on the dev
  subscription**, which never holds PHI (D025). `infra/provision-sql.sh` says so where it does
  it. **A private endpoint with public access disabled is a go-live deliverable — see
  `docs/PRELAUNCH_BLOCKERS.md`**, not a control this environment has today.

## Secrets

**No secret exists in the repository, in an image, or in an environment variable holding a
password.**

| Where | Mechanism |
|---|---|
| Local dev | `dotnet user-secrets`, `.env.local` (gitignored) |
| Azure — Storage | System-assigned managed identity, role assigned in `infra/provision-apps.sh`. **Built** |
| Azure — SQL | Managed identity; **no SQL password exists anywhere**. The database-side `CREATE USER FROM EXTERNAL PROVIDER` grant is a documented manual step (`infra/dbgrant`), not something a script has run |
| Azure — Speech, OpenAI | Resources provisioned; **identity is not wired yet** and no application code calls either (Phases 2 and 3). `infra/provision-ai.sh` deliberately does not read the keys |
| Remaining secrets | **Planned.** No Key Vault is provisioned and nothing references one. The row is kept because it is where the next secret goes, not because it is in place |
| CI | GitHub OIDC federated credentials — **no long-lived secret**. Built (`infra/provision-github-oidc.sh`, `.github/workflows/deploy.yml`) |

`disableLocalAuth=true` on Cognitive Services **once identity is wired** — which it is not — will
turn key theft into a non-event by removing keys as an auth path.

Enforcement: PreToolUse hooks block writes to secret-shaped paths
(`.claude/hooks/protect-env.sh`); `.gitignore` deny-lists; Gitleaks runs as its own CI job on
every push to `main` and every pull request, against the whole tree. **The repo is public — a
committed secret is compromised the moment it is pushed, and rotation is the only remedy.**

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
default-src 'self'; script-src 'self' 'unsafe-inline'; worker-src 'self';
manifest-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;
font-src 'self'; connect-src 'self'; form-action 'self'; frame-ancestors 'none';
base-uri 'self'; object-src 'none'
```

**`upgrade-insecure-requests` used to appear in this block and is not served.** It was
removed in D048 — it broke every WebKit E2E run on `http://localhost`, and HSTS with a
two-year `max-age` already guarantees what it was there for. The line above is now the
policy `next.config.ts` actually emits, asserted end-to-end in
`e2e/homepage.spec.ts:"serves the documented security headers"`.

`worker-src` and `manifest-src` are named rather than left to fall back. Without
`worker-src`, the service worker is governed by `script-src` and inherits the
`unsafe-inline` below — a deviation scoped to marketing HTML would silently extend to a
new execution context. Naming them is a tightening.

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

### Authenticated app — **Planned — WORK_QUEUE 4.2**

Nonce-based, **no `unsafe-inline`**. The app is dynamic by necessity, so the tradeoff above
does not apply. This is where the control actually matters: it renders PHI and holds a
session cookie, which is the combination `THREAT_MODEL.md` boundary 1 names.

**It is not built.** `next.config.ts` serves ONE policy for the whole origin — the public one
above, `unsafe-inline` included — so the authenticated app is presently governed by the
deviation that was scoped to marketing HTML. That is the gap 4.2 closes, and it is the reason
that deviation's own paragraph says it does not generalise.

## Input validation

- Validate at the `api` boundary. `web` validation is UX; it is not a control.
- Length caps on every string field, enforced in the schema and the database.
- Clinical free text is stored raw and **escaped at render**. Sanitizing on input corrupts
  clinical content — "80% w/ min cues" must survive intact.
- **Unknown fields are IGNORED, not rejected.** `System.Text.Json`'s default binds what it
  recognises and drops the rest; no `JsonUnmappedMemberHandling.Disallow` is configured. This
  line said the opposite. The exposure is small — every endpoint binds to a sealed record with
  no extra settable members — and it is a real difference between what this file claimed and
  what the code does.
- **Uploads: not built.** No endpoint in `api` accepts a file. Document upload is sequenced
  later (`CLAUDE.md` scope ledger); the content-type allow-list, size cap and magic-byte check
  are its acceptance criteria, not a control that exists.

## Caching

Authenticated responses are **dynamically rendered**: `export const dynamic = "force-dynamic"`
on the `(app)` layout, not per-route — per-route means someone forgets — which makes Next emit
`private, no-cache, no-store, max-age=0, must-revalidate` rather than a cacheable default. **No
`Cache-Control` header is set by hand**, and this section used to name one as though it were.

Asserted by a test hitting every authenticated route — and *every* is accurate here: `no
PHI-bearing route is cacheable` in `web/e2e/auth.spec.ts` **walks `app/(app)`** and derives the
list from the directory, with a companion assertion that fails if the walk stops finding pages.
A route added later arrives covered.

**Ranked #1 in the threat model** as the most likely accidental disclosure.

### The Cache API is a separate store, and `no-store` does not reach it

`Cache-Control` governs the HTTP cache and any CDN edge. It says nothing about the Cache
API, which is script-controlled, unencrypted, disk-backed storage on the device — the same
class of exposure as `localStorage`, which is prohibited outright. A service worker that
kept a copy of a rendered page would defeat both `no-store` and `force-dynamic` without
touching either.

`web/public/sw.js` is therefore built so that **PHI has no path into the Cache API**:

- **The cache is written exactly once, at install, from a constant allowlist.** There is no
  `cache.put` anywhere in the file — not on success, not on a fallback. A network response
  cannot be stored, so no response can carry clinical content into storage.
- **The fetch handler is an allowlist, not a catch-all.** A same-origin GET is answered from
  the cache only if its pathname is literally in that constant. Every authenticated page,
  every BFF route and every Next chunk falls through with no `respondWith`, so a route added
  later cannot land in a handler by accident — there is no handler for it to land in.
- **Activation deletes every other cache on the origin,** not merely older versions of this
  one. Anything that ever did write clinical content into the Cache API is removed on the
  next deploy.
- Every entry on the allowlist is a file committed under `web/public` — a compile-time
  static asset with no request context. Tests assert that each entry resolves to a real file
  there and that none matches a page in `app/(app)`, discovering the routes by walking the
  directory rather than listing them.

Offline, a navigation to any route returns one static screen (`public/offline.html`) that
says what needs a connection. **This application is deliberately not offline-capable**: the
alternative is a device holding a readable copy of a child's record for as long as the
browser chooses to keep it.

## Logging

**No PHI. Ever.**

- Structured logs carry `ProviderId`, `PublicId`, correlation ID, event type, outcome — through
  `Microsoft.Extensions.Logging` with source-generated `LoggerMessage` methods, whose parameters
  are ids and reasons by construction.
- **There is no Serilog in this repository.** No package reference in any `.csproj`, no sink, no
  destructuring policy, and `Program.cs:34` says so where a previous version of this file's claim
  had been copied into a comment. **Planned — WORK_QUEUE 4.1**: the redaction policy, plus a test
  that serialises every PHI-bearing entity and asserts no clinical value appears. Until then the
  control is that nothing logs an entity — a discipline, not a mechanism, and it is stated as one.
- Exceptions: correlation ID (`traceId`) to the user through RFC 9457 problem details, with no
  stack trace and no SQL text. **Azure Monitor is not wired** — no Application Insights or
  OpenTelemetry package exists — so "full detail to Azure Monitor with PHI stripped" describes
  the intended destination, not a shipped pipe.
- **Build and CI logs are world-readable** (public repo) and treated accordingly.

## Audit

Append-only `AuditEvent`. Application principal has **no `UPDATE` or `DELETE`** grant on it.

**Emitted today:** `LoginSucceeded`, `LoginFailed`, `MfaChallenged`, `MfaEnrolled`,
`RecoveryCodeUsed`, `PatientViewed`, `PatientCreated`, `PatientUpdated`, `NoteSigned`,
`NoteAmended`, `NoteDiscarded`, `ConsultationRequestReceived`,
`ConsultationNotificationFailed`, `ConsultationRequestViewed`, `ConsultationRequestUpdated`.

**Declared in `AuditEventType` and not yet written by anything:** `LoggedOut`, `AudioDeleted`
(**planned — WORK_QUEUE 2.10**), `ExportGenerated`. The enum values exist so that historical
rows can never be renumbered; listing them here as "recorded" made three events look like
controls. **WORK_QUEUE 4.7** is the test that will hold this list to the code.

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

**No audit write can be cancelled by the caller, and every audit write is bounded.** Those are
two different statements, and this section used to make only the first in words that denied the
second — it named `CancellationToken.None` as the token an audit write ran on, for two commits
after the code had stopped using it.

What is true now: `IAuditWriter.WriteAsync` takes no `CancellationToken`, so no call site can
hand over the request's — a property of the seam rather than a habit at the call sites, because
with a token parameter present CA2016 requires every call site inside a method that has one to
forward it, and the analyzer would enforce the defect (D075).
And `AuditWriter` saves on `deadline.Token` — a per-request `UncancellableWriteDeadline` that
does **not** move when the caller goes away, and expires one
`DatabaseTimeouts.UncancellableGrace` (90 seconds) after the request bound fires, shared once
between every uncancellable write in that request (D090).

**So there is a durability gap, it was accepted knowingly, and it is stated here rather than
denied.** If the database is still refusing work 90 seconds after a request has already burned
its entire 10m20s budget, the audit row is lost, where an unbounded write might eventually have
landed it. That is the price of a ceiling on a request that anybody can state — without it, an
uncancellable write ran on past the request timeout and *added* to it, and the tier had no
stated worst case at all. Two things bound the exposure: every path that writes an audit row has
already read from this database on the same request, so an audit write is never the query
carrying a resume from auto-pause; and where two uncancellable writes compete for that one
grace, the ORDER between them is chosen rather than incidental — see below.

**An audit row is written at the earliest point at which the fact it asserts is already true,
and not before.** That single rule puts the failure rows first and the success row last, which
looks inconsistent and is the opposite of it.

- **On a failure**, the fact is established the moment the credential check returns.
  `ProviderAuthenticator` audits a failed login *before* incrementing its failure count,
  because a lost row leaves no evidence — the response is deliberately indistinguishable from
  every other refusal, so nothing else records the attempt — while a lost increment leaves
  countable rows behind (D092).
- **On a success, that argument inverts, and reading it as a general rule shipped a defect.**
  With the users table stalled past the grace, `POST /auth/mfa/verify` answered **504 with no
  session** and the audit table nevertheless held `LoginSucceeded`, with `LastMfaAtUtc` still
  null. Nothing about a sign-in is a fact until the writes the session depends on have landed,
  so a row written before them is a *prediction* — and `LoginSucceeded` is the row an
  investigator uses to decide which sessions a breach has to be scoped to. A missing success
  row can be reconstructed from what a session leaves behind; a false one is not falsifiable by
  anything. `CompleteSignInAsync` therefore writes it **last**, so the row and the caller's
  "you are signed in" fail together (D097).

`RequestBoundsTests.The_security_document_names_the_token_audit_writes_run_on` reads the token
out of `AuditWriter` and out of this paragraph and fails when they disagree, because the last
time they disagreed nothing noticed (D072).

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

| Data | Policy | State |
|---|---|---|
| Session audio | Deleted when the note is signed. **Hard 30-day cap** regardless. Deletion audited | **Planned — WORK_QUEUE 2.10.** No audio is stored yet, so nothing is overdue; the policy is the acceptance criterion for that task |
| Transcript | Open — see `DATA_MODEL.md`. Leaning delete-with-audio | Open |
| Clinical notes | Retained. Maryland minors' floor **must be verified** (§15) | Retained; the floor is unverified |
| Consultation requests | Retained while `New`/`Contacted`; purge policy needed for `Declined` | Retained; no purge exists |
| Audit log | Retained. Never purged by the application | Built — the app principal has no `DELETE` on it |
| Offline drafts | 24h TTL, purge on server ack | **Planned — Phase 2.** There is no offline draft store yet |

**Deletion is verified, not assumed** — that is the design, and the alert that makes it true is
**planned — WORK_QUEUE 4.6**. Nothing today would notice audio past its deletion date, which is
tolerable only because nothing stores audio yet. A silently failing lifecycle job looks exactly
like a working one, so this row closes with 2.10 and 4.6 together or not at all.

## Dependencies

`pull_request_target` is **banned** and appears nowhere; no workflow exposes secrets to a fork
(`permissions: contents: read` on CI, and deploy runs only on `main`). `web/package-lock.json`
is committed.

**Two claims here were false and are now the queue's problem rather than this page's:**

| Claim | State |
|---|---|
| Dependabot on | **NOT BUILT.** There is no `.github/dependabot.yml`. Nothing watches either lockfile |
| Pinned action SHAs | **NOT BUILT.** Every `uses:` in both workflows is a floating tag — `actions/checkout@v5`, `docker/build-push-action@v6`, `azure/login@v2`. A moved tag is a supply-chain write into a workflow holding a deploy identity (`THREAT_MODEL.md` ⑧) |
| .NET lockfile | **NOT BUILT.** `RestorePackagesWithLockFile` is not set, so there is no `packages.lock.json` — "lockfiles committed" was true of `web` only |

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
