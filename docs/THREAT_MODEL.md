# Threat Model

The bar the **security adversary** review lane judges against, alongside OWASP.

Scope: everything that creates, receives, maintains, or transmits ePHI. Method: STRIDE per
trust boundary, then a ranked list of what actually threatens this system.

**A finding against this document must name a boundary and a control that is missing or
wrong.** "This could be attacked" is not a finding.

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

| | Threat | Control |
|---|---|---|
| **S** | Credential stuffing against `/login` | TOTP MFA; rate limit per IP and per account; lockout with backoff; `LoginFailed` audited |
| **S** | Session cookie theft via XSS | `HttpOnly` + `Secure` + `SameSite=Lax`; strict CSP, no `unsafe-inline`; React escaping; no `dangerouslySetInnerHTML` on user content |
| **T** | CSRF on state-changing routes | Server actions carry Next.js's built-in token; `SameSite=Lax`; origin check on POST route handlers |
| **R** | "I didn't sign that note" | `ClinicalNote.SignedBy` + `SignedAtUtc` + `ContentHash`; append-only `AuditEvent` |
| **I** | PHI cached at a CDN edge | `(app)` route group forces dynamic render + `no-store`. **The highest-likelihood accidental disclosure in the system** |
| **I** | PHI in a URL, hence in logs and Referer | Opaque `PublicId` only; never names or DOB in a path or query |
| **D** | Consultation-form spam / cost amplification | Rate limit, hashed source IP, honeypot field. Scale-to-zero means a flood costs money as well as noise |
| **E** | Forced browsing to another patient | Every `api` call re-checks provider ownership. **Hiding UI is not authorization** |

### ② web → api

| | Threat | Control |
|---|---|---|
| **S** | Anything on the internet calling `api` directly | **Internal ingress. No public route exists.** Not obscurity |
| **S** | Forged internal caller | Managed identity; token audience validated |
| **E** | `web` passing a provider ID it was told rather than one it authenticated | `api` derives identity from the token, **never from a request body field** |

### ③ api → SQL

| | Threat | Control |
|---|---|---|
| **T/I** | SQL injection | EF Core parameterization; no string-concatenated SQL; raw SQL requires review |
| **I** | Stolen connection string | Managed identity, **no SQL password exists**; private endpoint, public access disabled |
| **T** | Direct tampering with a signed note | `UPDATE` trigger rejects SOAP edits when `Status <> 'Draft'`; `ContentHash` detects it anyway |
| **R** | Audit log edited to hide access | App principal has **no `UPDATE`/`DELETE`** on `AuditEvent` |
| **D** | Free-tier auto-pause on limit | Capacity banner; admin alerts; documented degraded state (§13) |

### ④ api → Blob

| | Threat | Control |
|---|---|---|
| **I** | Guessable or leaked audio URL | Short-lived user-delegation SAS, never a public container, never a permanent URL |
| **I** | Audio outliving its purpose | Deleted on note signature; hard 30-day lifecycle policy; `BlobDeletedAtUtc` audited |
| **I** | Clinical audio and public handouts in one place | **Separate containers, separate access rules.** `ResourceDocument` is public by design and must never share a container with PHI |

### ⑤ api → Azure Speech · ⑥ api → Azure OpenAI

| | Threat | Control |
|---|---|---|
| **I** | PHI to a non-BAA vendor | Provider seam declares `IsPhiEligible`; OpenRouter throws (D019) |
| **I** | PHI to a non-HIPAA-eligible endpoint | **Text endpoints only.** Azure OpenAI audio models are not BAA-covered |
| **I** | Worldwide inference routing | **Open — blocker #1.** GlobalStandard is dev-only, synthetic data only |
| **I** | 30-day prompt retention, possible human review | **Open — blocker #2.** De-identification is the mitigating control (D018), not a fix |
| **T** | Model output asserting invented clinical facts | Structured extraction, span provenance, numeric-provenance check (D016, D017) |

### ⑦ browser → device storage (IndexedDB, Cache API)

| | Threat | Control |
|---|---|---|
| **I** | Lost or stolen phone with drafts on it | AES-GCM, non-extractable key, in-memory wrapping key; purge on server ack; 24h hard TTL |
| **I** | Drafts surviving indefinitely | TTL enforced on read **and** on a timer, not only at write |
| **I** | Another site reading it | Same-origin. HTTPS only |
| **I** | PHI in `localStorage`/`sessionStorage` | **Absolutely prohibited.** Lint rule, not just a policy |
| **I** | PHI in the **Cache API** — `no-store` does not reach it | The service worker writes the cache **once, at install, from a constant allowlist of files in `public/`**. No `cache.put` exists anywhere in `sw.js`, so a network response has no path into storage. Activation deletes every other cache on the origin |
| **I** | A cached authenticated page served to a later viewer | The fetch handler answers navigations from the network and never stores them; it is an allowlist, so a route added later is not handled at all rather than handled wrongly |
| **D** | iOS 7-day eviction destroying an unsynced draft | Detect standalone mode, prompt to install; sync-on-foreground; `online`-event retry |

### ⑧ CI/CD → Azure

| | Threat | Control |
|---|---|---|
| **S** | Stolen deploy credential | OIDC federated identity, **no long-lived secret in GitHub** |
| **T** | Malicious dependency in the build | Lockfiles committed; Dependabot; pinned action SHAs |
| **E** | A fork PR running with repo secrets | **Repo is public.** `pull_request_target` is banned; no secrets on fork workflows |
| **I** | Secret printed into a public build log | Never echo config; masked secrets; logs assumed world-readable, because they are |

### ⑨ api → email

| | Threat | Control |
|---|---|---|
| **I** | PHI in an inbox we don't control | **Notifications carry no content.** "New consultation request, sign in to view" |

---

## Ranked — what will actually go wrong

1. **PHI accidentally cached or statically rendered.** A framework default, one missing route
   config, silent failure. Mitigation: enforced at the layout, plus a test asserting `no-store`
   on authenticated responses.
2. **A secret reaching the public repo.** Hooks, `.gitignore`, and pre-push scanning exist
   because this is the mistake that cannot be undone.
3. **PHI in a log line.** A serialized DTO containing `FirstName`, logged during debugging.
   Mitigation: Serilog destructuring policy that redacts PHI-bearing types + a test.
4. **Michelle's account compromised.** Phishing, reused password. MFA is the whole defence, so
   **recovery-code handling is as security-critical as login itself.**
5. **A fabricated clinical value reaching a signed note.** Not an attacker — the system itself.
   Ranked here because impact is patient harm.
6. **Audio not deleted on schedule.** A silently failing lifecycle job. Needs an alert on
   overdue deletions, not just a policy.
7. **Records disclosed to a guardian without legal authority.** `HasLegalAuthority` is a data
   field; it must be an enforced check on any export or share path.

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
