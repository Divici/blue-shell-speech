# Threat Model

The bar the **security adversary** review lane judges against, alongside OWASP.

Scope: everything that creates, receives, maintains, or transmits ePHI. Method: STRIDE per
trust boundary, then a ranked list of what actually threatens this system.

**A finding against this document must name a boundary and a control that is missing or
wrong.** "This could be attacked" is not a finding.

**Every control cell below is marked built or planned, and the same tense rule governs this
file as `SECURITY.md`.** `CLAUDE.md` names this document as the bar the security-adversary
review lane judges against, so a control described here and absent from the tree does not merely
mislead a reader — it sets the standard the next review is measured by. That is how this file
came to carry "rate limit per IP and per account" and "pinned action SHAs" for a commit after
`SECURITY.md` had retracted both. A row for planned work stays, with its task number: the
mitigation is the design, and deleting it would lose the reason the threat is acceptable in the
meantime.

---

## Assets, ranked

| Asset | Why it matters | Worst case |
|---|---|---|
| Patient identity + clinical notes | ePHI for children | Breach notification, licensure exposure, real harm to families |
| Session audio | Raw ePHI, unredacted | Same, plus caregiver voices |
| Michelle's credentials | Single account = total access | Full compromise of every record |
| Audit log | The evidence trail | A breach nobody can scope or prove bounded |
| Guardian contact details | PHI, custody-sensitive | Disclosure to the wrong parent |
| Michelle's home address | Personal safety | Not clinical — still a real-world risk |

**Single-provider blast radius.** One account holds everything. There is no lateral movement to
worry about because there is nowhere to move — a compromise of Michelle's account is a
compromise of the practice. That is why MFA is non-negotiable and why account recovery is a
threat surface rather than a convenience feature.

---

## Trust boundaries

```text
  ①  Internet ──────► web (Next.js)          public ingress
  ②  web ───────────► api (ASP.NET Core)     internal ingress only
  ③  api ───────────► Azure SQL              private endpoint
  ④  api ───────────► Blob Storage           managed identity
  ⑤  api ───────────► Azure Speech           PHI leaves our subscription
  ⑥  api ───────────► Azure OpenAI           de-identified text leaves
  ⑦  browser ───────► IndexedDB              PHI at rest on a personal phone
  ⑧  CI/CD ─────────► Azure                  deploy identity
  ⑨  api ───────────► email                  no content, by design
```

---

## STRIDE by boundary

### ① Internet → web

| | Threat | Control | State |
|---|---|---|---|
| **S** | Credential stuffing against `/login` | TOTP MFA; **fixed** 15-minute lockout after 5 failures — no backoff, and the count is one serialised UPDATE rather than a read-modify-write (D097); `LoginFailed` audited with reason and actor. **Rate limited per source (20/5 min) and per SUBMITTED address (10/15 min)**, counters in `RateLimitCounters` in Azure SQL so the limit holds across replicas and a scale-to-zero cycle; the refusal is a contentless 429 with no `Retry-After`, audited as `RateLimited` once per partition per window (D098) | Built (WORK_QUEUE 1.19). **Two limits remain honest about their reach:** a caller with many sources AND many addresses is bounded by the product of the two, and the source key is one `web` forwards — anything reaching `api` directly can pick its own until **4.4**. It cannot pick its account bucket |
| **S** | Session cookie theft via XSS | `HttpOnly` + `Secure` + `SameSite=Lax`; React escaping; no `dangerouslySetInnerHTML` on user content | Built. **Strict CSP with no `unsafe-inline` for the authenticated app: planned — 4.2.** One policy is served for the whole origin today and it carries `unsafe-inline` |
| **T** | CSRF on state-changing routes | Server actions carry Next.js's built-in origin/token check; `SameSite=Lax` | Built. **No POST route handler exists** — the only handler in `web/app/api` is a GET health probe — so "origin check on POST route handlers" is a rule for the first one written, not a control in place |
| **R** | "I didn't sign that note" | `ClinicalNote.SignedBy` + `SignedAtUtc` + `ContentHash`; append-only `AuditEvent` | Built |
| **I** | PHI cached at a CDN edge | `(app)` route group forces dynamic render, so responses carry `no-store`; the E2E assertion walks the route group rather than listing it. **The highest-likelihood accidental disclosure in the system** | Built |
| **I** | PHI in a URL, hence in logs and Referer | Opaque `PublicId` only; never names or DOB in a path or query | Built |
| **D** | Consultation-form spam / cost amplification | Rate limit, hashed source IP, honeypot field. Scale-to-zero means a flood costs money as well as noise | Built (`web/lib/rate-limit.ts`, in process memory — its own docstring states the multi-replica limit) |
| **E** | Forced browsing to another patient | Every `api` call re-checks provider ownership. **Hiding UI is not authorization** | Built |

### ② web → api

| | Threat | Control | State |
|---|---|---|---|
| **S** | Anything on the internet calling `api` directly | **Internal ingress. No public route exists.** Not obscurity | Built |
| **S** | Forged internal caller | Managed identity; token audience validated | **Planned — 4.4.** Network isolation alone today; `AuthEndpoints`' docstring says so |
| **E** | `web` passing a provider ID it was told rather than one it authenticated | `api` derives identity from its own request context, **never from a request body field** | Partial. The context comes from a header the BFF forwards, resolved by opaque `PublicId` against an active provider row — so it is not a body field, and it is not a validated token either. **Planned — 4.4** |

### ③ api → SQL

| | Threat | Control | State |
|---|---|---|---|
| **T/I** | SQL injection | EF Core parameterization; **no string-concatenated SQL**; raw SQL requires review. There are **two** raw statements in `api/src`, and both exist because the atomicity IS the control and belongs somewhere reviewable: `LoginBookkeeping`'s serialised failure count (`ExecuteSqlAsync`) and `SqlRateLimitStore`'s counter batch (`FromSqlInterpolated`). Both interpolate through EF, so every hole is a `DbParameter` and no concatenation reaches the engine | Built |
| **I** | Stolen connection string | Managed identity, **no SQL password exists** anywhere in the tree or in a variable | Built. **Private endpoint with public access disabled: go-live deliverable** — the dev server allows Azure services (D025), and holds no PHI |
| **T** | Direct tampering with a signed note | `UPDATE` trigger rejects SOAP edits when `Status <> 'Draft'`; `ContentHash` detects it anyway | Built |
| **R** | Audit log edited to hide access | App principal has **no `UPDATE`/`DELETE`** on `AuditEvent` | Built |
| **D** | Free-tier auto-pause on limit | Capacity banner; admin alerts; documented degraded state (§13) | **Planned — 4.5** |

### ④ api → Blob

| | Threat | Control | State |
|---|---|---|---|
| **I** | Guessable or leaked audio URL | Short-lived user-delegation SAS, never a public container, never a permanent URL | **Planned — 2.5.** Nothing writes to blob storage yet |
| **I** | Audio outliving its purpose | Deleted on note signature; hard 30-day lifecycle policy; `BlobDeletedAtUtc` audited | **Planned — 2.10** |
| **I** | Clinical audio and public handouts in one place | **Separate containers, separate access rules.** `ResourceDocument` is public by design and must never share a container with PHI | Built — `session-audio` and `public-resources` are created separately in `infra/provision-platform.sh`, before either has a caller |

### ⑤ api → Azure Speech · ⑥ api → Azure OpenAI

**Nothing in `api` calls either service yet.** Every row here is the acceptance criteria for
Phases 2 and 3; the two open blockers are live and are the reason those phases run against
synthetic data only.

| | Threat | Control | State |
|---|---|---|---|
| **I** | PHI to a non-BAA vendor | Provider seam declares `IsPhiEligible`; OpenRouter throws (D019) | **Planned — 2.8, 3.8** |
| **I** | PHI to a non-HIPAA-eligible endpoint | **Text endpoints only.** Azure OpenAI audio models are not BAA-covered | Policy in force (`CLAUDE.md`); no code path exists to violate it yet |
| **I** | Worldwide inference routing | **Open — blocker #1.** GlobalStandard is dev-only, synthetic data only | Open |
| **I** | 30-day prompt retention, possible human review | **Open — blocker #2.** De-identification is the mitigating control (D018), not a fix | Open; de-identification is **planned — 3.1** |
| **T** | Model output asserting invented clinical facts | Structured extraction, span provenance, numeric-provenance check (D016, D017) | **Planned — 3.3, 3.4, 3.7** |

### ⑦ browser → device storage (IndexedDB, Cache API)

| | Threat | Control | State |
|---|---|---|---|
| **I** | Lost or stolen phone with drafts on it | AES-GCM, non-extractable key, in-memory wrapping key; purge on server ack; 24h hard TTL | **Planned — Phase 2.** No draft store exists, so nothing is at rest on a device today |
| **I** | Drafts surviving indefinitely | TTL enforced on read **and** on a timer, not only at write | **Planned — Phase 2** |
| **I** | Another site reading it | Same-origin. HTTPS only | Built |
| **I** | PHI in `localStorage`/`sessionStorage` | **Absolutely prohibited.** Lint rule, not just a policy | Built — `no-restricted-globals` and `no-restricted-properties` in `web/eslint.config.mjs`, both naming D005 |
| **I** | PHI in the **Cache API** — `no-store` does not reach it | The service worker writes the cache **once, at install, from a constant allowlist of files in `public/`**. No `cache.put` exists anywhere in `sw.js`, so a network response has no path into storage. Activation deletes every other cache on the origin | Built (D093) |
| **I** | A cached authenticated page served to a later viewer | The fetch handler answers navigations from the network and never stores them; it is an allowlist, so a route added later is not handled at all rather than handled wrongly | Built (D093) |
| **D** | iOS 7-day eviction destroying an unsynced draft | Detect standalone mode, prompt to install; sync-on-foreground; `online`-event retry | **Planned — 2.2 and 2.11.** The manifest and worker ship (2.1); the install prompt and the retry do not |

### ⑧ CI/CD → Azure

| | Threat | Control | State |
|---|---|---|---|
| **S** | Stolen deploy credential | OIDC federated identity, **no long-lived secret in GitHub** | Built |
| **T** | Malicious dependency in the build | Lockfiles committed; Dependabot; pinned action SHAs | Partial, and this row was the F5 finding. `web/package-lock.json` is committed; **`api` has no `packages.lock.json`, there is no `.github/dependabot.yml`, and every `uses:` is a floating tag.** A moved tag is a supply-chain write into a workflow holding the deploy identity |
| **E** | A fork PR running with repo secrets | **Repo is public.** `pull_request_target` is banned; no secrets on fork workflows | Built — `permissions: contents: read`, and deploy runs on `main` only |
| **I** | Secret printed into a public build log | Never echo config; masked secrets; Gitleaks scans the tree on every push and PR; logs assumed world-readable, because they are | Built |

### ⑨ api → email

| | Threat | Control | State |
|---|---|---|---|
| **I** | PHI in an inbox we don't control | **Notifications carry no content.** "New consultation request, sign in to view" — enforced by the seam's signature, not by care at the call site (D079) | Built. The real transport is *Blocked — needs David*; `LoggingConsultationNotifier` composes and logs it today |

---

## Ranked — what will actually go wrong

1. **PHI accidentally cached or statically rendered.** A framework default, one missing route
   config, silent failure. Mitigation **built**: `force-dynamic` at the `(app)` layout, plus an
   E2E assertion that walks the route group and requires a non-cacheable directive on each.
2. **A secret reaching the public repo.** Mitigation **built**: PreToolUse hooks on
   secret-shaped paths, `.gitignore` deny-lists, and a Gitleaks CI job over the whole tree on
   every push and PR. This is the mistake that cannot be undone.
3. **PHI in a log line.** A serialized DTO containing `FirstName`, logged during debugging.
   Mitigation **planned — 4.1**: a Serilog destructuring policy that redacts PHI-bearing types,
   plus a test. **There is no Serilog in the tree**, so today's control is that nothing logs an
   entity — a discipline, and it is why this sits at #3 rather than lower.
4. **Michelle's account compromised.** Phishing, reused password. MFA is the whole defence, so
   **recovery-code handling is as security-critical as login itself.** MFA and the lockout are
   **built** — and the lockout only began counting concurrent attempts at D097, having been
   defeasible by twenty simultaneous requests before it. Rate limiting per source and per
   submitted address is **built** (1.19, D098), on counters in `RateLimitCounters` rather than
   in a replica's memory, which is what closed the case the lockout structurally cannot see: an
   unlimited stream of guesses against *unknown* addresses, counted by nothing because there is
   no row to count on.
5. **A fabricated clinical value reaching a signed note.** Not an attacker — the system itself.
   Ranked here because impact is patient harm. Mitigations **planned — Phase 3** (3.3, 3.4, 3.7);
   nothing generates clinical text yet.
6. **Audio not deleted on schedule.** A silently failing lifecycle job. Needs an alert on
   overdue deletions, not just a policy — **planned — 2.10 and 4.6**, and neither exists, which
   is survivable only because no audio is stored yet.
7. **Records disclosed to a guardian without legal authority.** `HasLegalAuthority` is asked
   explicitly and never inferred (D073, **built**), and every change to it is audited. **The
   enforced check is planned**: there is no export or share path yet for it to gate, and the
   day one is built it is that path's first acceptance criterion.

---

## Explicitly out of scope

- **Nation-state adversaries.** Not a proportionate threat model for a solo practice.
- **Insider threat from other staff.** There are none. Revisit when a second person joins —
  that change alters the model more than any technical decision here.
- **DDoS beyond platform defaults.** Azure's baseline; a cost alert is the real control.
- **Physical security of Michelle's phone** beyond encryption, TTL, and device passcode.

---

## Review triggers

Re-run this document when: a second user is added · any new vendor touches ePHI · document
upload ships · payments ship · a blocker in `PRELAUNCH_BLOCKERS.md` closes or changes.
