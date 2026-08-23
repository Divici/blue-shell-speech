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

- ACA HIPAA-eligibility unverified — blocks go-live, not development.
- Design comps cover the homepage only. About, Services, Resources, Consultation, Login and
  every authenticated screen are undesigned.
- Missing assets: blue shell logo, wave dividers, organic blob masks, bubbles, icon set.
