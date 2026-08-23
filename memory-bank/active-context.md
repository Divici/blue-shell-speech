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

## Next

1. Produce the presearch §31 planning artifacts — `PRD.md`, `ARCHITECTURE.md`, `DATA_MODEL.md`,
   `SECURITY.md`, `THREAT_MODEL.md`, `HIPAA_DATA_FLOW.md`, `AI_PIPELINE.md`, `API_SPEC.md`,
   `UX_FLOWS.md`, `TEST_STRATEGY.md`, `DEPLOYMENT.md`, `IMPLEMENTATION_PLAN.md`.
2. Write per-slice acceptance criteria — `slice-gauntlet` cannot run without them.
3. Bootstrap the monorepo and the deploy pipeline.

## Waiting on Michelle

- Real bio, credentials, phone, email, service area (placeholders until then).
- Headshot (stand-in for now).
- **One timed dictation of a fictional patient** — gives the real recap length and the first
  eval-corpus fixture in one go.
- Decision on the services chips: confirm AAC is named, since the cut section removed it.

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
