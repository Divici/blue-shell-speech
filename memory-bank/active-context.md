# Active Context

_Update at every slice boundary._

## Phase

**Planning.** No application code written yet.

## Just completed

- Full read of `presearch.md`, the frontend rules, three design comps, and Michelle's edits.
- Hosting decided: two containers on ACA scale-to-zero + Azure SQL free tier. Alternatives
  (SWA hybrid, Vercel, always-on) evaluated and rejected with reasons in `tech-context.md`.
- AI path decided and forced by BAA coverage: Azure Speech STT → Azure OpenAI text ×2.
- Project governance written: `CLAUDE.md`, `.claude/settings.json`, two PreToolUse hooks
  (tested, 18/18 cases), `slice-gauntlet` skill.
- Homepage edits from Michelle captured: services grid and testimonials removed; Get In Touch
  section added; Login added to header; nav reduced to on-page anchors plus `/consultation`.

## Azure — provisioned 2026-08-23

Subscription `818cd2da…` · `blueShellRG` · `eastus` · all resources co-located.

| Resource | Detail |
|---|---|
| `blueshellOpenAI` | Azure OpenAI, S0 |
| `blueshellSpeech` | Speech, **free F0** |
| `gpt-5-mini-global` | gpt-5-mini 2025-08-07, GlobalStandard, 50K TPM |

Reproducible via `infra/provision-ai.sh`. Keys deliberately never read — auth is managed identity.

**GlobalStandard is dev-only.** It routes inference worldwide: fine for synthetic data,
disqualifying for PHI. Every DataZoneStandard and regional Standard quota for the gpt-5 family
is 0 on this subscription. See `docs/PRELAUNCH_BLOCKERS.md` item 1.

## Subscriptions — dev / production split (2026-08-24)

| Subscription | Role | Holds PHI |
|---|---|---|
| `818cd2da…` (David, currently trial) | **Development, permanently** | Never |
| Practice PAYG (not yet created) | **Production only** | Yes, after slice 10 |

Michelle is a sole proprietor **with an EIN**, so the practice can be Azure's named business
customer directly — no LLC required. The production subscription is created fresh under the
practice rather than transferred, because provisioning is scripted and nothing is deployed yet.
Full reasoning: `DECISIONS.md` D024, D025; execution steps in `docs/PRELAUNCH_BLOCKERS.md` #4.

**Slices 0–9 need nothing from Michelle.** The entire product builds and deploys against the dev
subscription. Only slice 10 — BAA verification, real-entity subscription, go-live sign-off —
requires the production subscription to exist.

## Next

1. **Slice 0** — monorepo (`/web` `/api` `/docs` `/infra`), CI, deploy pipeline to the dev
   subscription. Acceptance criteria in `docs/IMPLEMENTATION_PLAN.md`.
2. Slices 1–9 in order, each ending green and deployed.
3. `PRD.md` and `HIPAA_DATA_FLOW.md` still outstanding; `API_SPEC.md` and `UX_FLOWS.md` are
   written against real endpoints and screens as their slices land.

## Planning artifacts — complete

`ARCHITECTURE.md` · `DATA_MODEL.md` · `SECURITY.md` · `THREAT_MODEL.md` · `AI_PIPELINE.md` ·
`TEST_STRATEGY.md` · `DEPLOYMENT.md` · `IMPLEMENTATION_PLAN.md` · `SITE_CONTENT.md` ·
`PRELAUNCH_BLOCKERS.md`

## Content — closed 2026-08-23

All confirmed with Michelle. Full copy in `docs/SITE_CONTENT.md`.

- Bio and credentials from the comp are **accurate as written**. Do not embellish them.
- Service area: **Maryland**.
- **AAC confirmed** for the services chips.
- Headshot received — `assets/headshot.PNG`, 2.4 MB, needs conversion to AVIF/WebP.
- Recap length measured: **~3 min actual, 2–5 min range → 5-minute hard cap** per take,
  multiple takes per session. See `DECISIONS.md` D010.

**Still outstanding:** phone and email are `PLACEHOLDER`, supplied via env config, not the tree.

**Still wanted, not blocking:** a dictation of a *fictional* patient as eval-corpus fixture #1.
Michelle's timed run settled the length question; the corpus still needs synthetic audio, and
under §22 it must be fictional — a real recap cannot become a test fixture.

## Live risks

Tracked in full in `docs/PRELAUNCH_BLOCKERS.md`. Headlines:

- No PHI-safe Azure OpenAI deployment exists yet — quota request needed.
- Default abuse monitoring retains prompts 30 days with possible human review; a BAA alone does
  not exempt us, and a solo practice may not qualify for Modified Abuse Monitoring.
- ACA HIPAA-eligibility unverified — blocks go-live, not development.
- Subscription sits under a personal account; the BAA would name the wrong entity.
- Design comps cover the homepage only. About, Services, Resources, Consultation, Login and
  every authenticated screen are undesigned.
- Missing assets: blue shell logo, wave dividers, organic blob masks, bubbles, icon set.
