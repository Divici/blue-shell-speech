# Blue Shell Speech

Production practice platform for a solo Maryland pediatric SLP (Michelle), plus a public
practice website. Two audiences, one product, one domain, strict boundary between them.

Authoritative spec: **`presearch.md`** in the repo root. It is decided, not draft. §29 locks
24 decisions. Do not re-litigate them; if one is genuinely wrong, say so and stop — do not
silently substitute a different choice.

Front-end law: **`blue-shell-frontend-engineering-rules.md`**. Every deviation needs a
documented technical reason, per that file's own closing clause.

---

## Stack (locked — see presearch.md §29)

| Layer | Choice |
|---|---|
| Frontend | Next.js (App Router) + TypeScript strict |
| Backend | ASP.NET Core (.NET 10 LTS) — system of record |
| ORM / DB | EF Core → Azure SQL Database (free offer, auto-pause on limit) |
| Auth | ASP.NET Core Identity, self-hosted, TOTP MFA |
| API boundary | BFF — browser talks only to Next route handlers / server actions, never to .NET directly |
| Transcription | Azure Speech STT |
| Extraction + generation | Azure OpenAI, **text endpoints only** |
| Hosting | Two containers on Azure Container Apps, scale-to-zero |
| Repo | Monorepo — `/web` `/api` `/docs` `/infra` |

**Azure OpenAI audio/voice models are not HIPAA-eligible under Microsoft's BAA.** Only text
endpoints are. Transcription goes through Azure Speech. This is not a preference.

OpenRouter credits are for **synthetic benchmarking only**. Never route PHI through them.

---

## Non-negotiables

1. **Synthetic data only** until every production PHI path is verified (presearch.md §22).
   No real patient data in local DBs, seeds, fixtures, screenshots, logs, or tests.
2. **Never claim "HIPAA compliant"** in code, docs, README, or UI. The phrasing is
   "designed to support HIPAA obligations." See §14.1.
3. **No PHI in logs.** Structured logs carry IDs and correlation IDs, never clinical content.
4. **No PHI in `localStorage` or `sessionStorage`.** Offline drafts use encrypted IndexedDB —
   AES-GCM, non-extractable key, in-memory wrapping key, purge on server ack, 24h hard TTL.
   This is a documented deviation; keep it documented.
5. **Signed notes are immutable.** Amendments create versions. Never overwrite.
6. **Authorization is server-side.** Hiding UI is not authorization.
7. This repo is **public**. Secrets never enter the tree. Michelle's home address never
   enters the tree. Contact details come from environment config.

---

## Review lanes

Four review mechanisms, routed by slice type. Do not run all four on every slice.

| Lane | Bar it judges against | Applies to |
|---|---|---|
| **Spec critic** | Slice acceptance criteria + tests | Every slice |
| **Security adversary** | `docs/THREAT_MODEL.md` + OWASP | Auth, PHI, API, dictation |
| **Visual gauntlet** (`gauntlet-loop`) | A named live site, fetched and compared blind | Homepage, app UI, dictation UX |
| **`/super-review`** | Maintainability standards | Every slice boundary |

**Spec critic rule:** a finding must ship with a failing test or a citation to a spec line.
Findings without evidence are not findings. The exit condition is "every acceptance criterion
demonstrably met," never a round count.

**`/super-review` runs at slice boundaries, never inside a gauntlet loop.** The two optimize
opposite axes and give the builder contradictory pressure. One full-codebase pass before go-live.

`gauntlet-loop` produces a paste-ready prompt; it is not a build orchestrator and must not be
handed the whole project — it deliberately withholds architecture, which would discard
`presearch.md`. Use it only where a blind A/B against a named external artifact is meaningful.

---

## Skills

**Use:** `security-review` (frequent here — auth, user input, secrets, endpoints),
`documentation-lookup` (prefer live docs over training data for .NET 10 / Next 16 / EF Core),
`frontend-design`, `frontend-patterns`, `coding-standards`, `backend-patterns`, `api-design`,
`nextjs-turbopack`, `database-migrations`, `e2e-testing`, `webapp-testing`, `ui-audit`,
`gauntlet-loop` (scoped as above), `thermo-nuclear-code-quality-review` via `/super-review`.

**Denied in `.claude/settings.json`** — do not work around these:
- `postgres-patterns` — wrong engine, this is SQL Server
- `build`, `workflow`, `conductor`, `kickoff`, `project-bootstrap` — competing orchestrators
- `presearch`, `presearch2` — presearch is complete and locked
- `search-first` — spawns subagents unprompted
- superpowers plugin — its hook mandates invoking skills before any response and routes all
  building through `brainstorming` → `writing-plans`, which conflicts with this project's
  review lanes

`build-docs` and `build-summary` run **once at the end**, not throughout.

---

## Global rule overrides

These override `~/.claude/rules/` for this project:

- **`forge-defaults.md`** — deployment is **Azure Container Apps**, not Railway/Vercel.
  Vercel's Hobby tier prohibits commercial use and this is a commercial practice.
  shadcn/ui is used **behind the login only**; the public site is hand-built to the comps.
- **`commit-message.md`** — use the trailer the harness injects (`Claude Opus 5`), not
  `Claude Opus 4.6`. Everything else in that rule stands, including auto-commit.
- **`tdd.md`** — binds logic, API contracts, domain rules, and stateful components. Visual
  iteration during a gauntlet round is exempt under that rule's own non-behavioral clause.
  AI model quality lives in a separate eval suite and does not gate CI.
- **`study-guide.md`** — keep. This project is interview preparation; the rule is on-point.

---

## Decision log

`DECISIONS.md` at the repo root is **gitignored** — it exists locally but will not appear in a
fresh clone. Do not assume it is missing because it was never created.

Append an entry whenever a choice is made that would be expensive to reverse: architecture,
vendor, data model, security posture, deployment target, a deviation from the frontend rules.
Library picks a single PR could swap out do not need one.

Each entry records the alternatives that lost and **what the choice cost us**, not just why it
won. A decision log with no downsides in it is marketing.

It is **append-only**. Supersede entries; never edit one to look correct in hindsight. That
distinguishes it from `STUDY_GUIDE.md`, which is rewritten freely and holds current state only.

---

## Public site

Sections, after Michelle's edits: Header → Hero → three badges → Meet Your SLP (with a light
row of service chips, **AAC included**) → Getting Started is Easy → Get In Touch → Footer.

**Removed:** the "Therapy That's Tailored to Your Child" services grid, and the testimonials
carousel. Testimonials are deleted, not deferred — do not reintroduce placeholder reviews.

**Nav:** Home / About / Services / Contact all anchor-scroll on the homepage.
`Free Consultation` → `/consultation` (real intake form, its own route).
`Login` → `/login`, styled secondary so parents aren't drawn to it.
Resources tab is removed until handouts exist; build the resource system anyway so adding
one later is a content change.

Design tokens come from comp 2's sidebar; layout from comp 3. Light-gray body copy in the
comps fails 4.5:1 — darken it and note the deviation.

**Missing assets** (blue shell logo, wave dividers, organic blob masks, bubbles, icon set,
headshot): generate as optimized SVG. `children.png` is 2.1 MB — convert before shipping.

---

## Dictation

Installable PWA. One record button that toggles to pause/resume. **5-minute cap per take**,
multiple takes per session. Background job + status polling — never a synchronous request
that must survive a scale-to-zero cold start.

iOS constraints that are already known and must be designed around, not rediscovered:
- Background Sync API does not exist in Safari. Feature-detect; fall back to sync-on-foreground
  plus an `online`-event retry.
- Only home-screen-installed PWAs escape Safari's 7-day storage eviction. Detect standalone
  mode and prompt to install — offline drafts are not durable in a browser tab.
- iOS `MediaRecorder` emits mp4/AAC, not webm. Server-side transcode to 16 kHz PCM for Azure Speech.

Missing information surfaces as chips in the review UI that Michelle can fill by typing **or**
by tapping to speak. Never fabricate a percentage, cue level, trial count, or caregiver report —
mark it missing (§7.5).

---

## Scope

Everything in `presearch.md` gets built. These four are **sequenced later, not cut** — design
the data model and interfaces to accommodate them now:

1. Document/file upload
2. Evaluation reports (the appointment type ships; formal report authoring comes after)
3. Superbill PDF generation (`Encounter` entity ships now)
4. Live Azure Cost Management API for the capacity banner (threshold logic ships against
   internal counters)

Real-patient go-live **is planned**. The risk analysis, threat model, data-flow diagram, and
BAA verification are live deliverables with sign-off, not documentation exercises.

**Open blocker before go-live:** Azure Container Apps was not confirmed as a HIPAA-eligible
service in the Microsoft Product Terms. Verify. If it is not listed, App Service for Containers
is the swap — same container, different target, no application changes.

---

## Conventions

- `ProviderId` on every domain row from day one, even at one provider.
- Store UTC; render `America/New_York`.
- Audio retained until the note is signed, hard 30-day cap, then deleted.
- Email notifications carry **no content** — "New consultation request, sign in to view."
- Patient-facing identifiers are opaque GUIDs, never sequential integers.
