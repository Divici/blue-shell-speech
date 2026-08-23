# Pre-Launch Blockers

Items that must close **before the first real patient record exists**. None block development —
development is synthetic-data-only (`presearch.md` §22) and stays that way until every row here
is resolved.

Each entry names the mitigation if it cannot be resolved, because "we'll sort it out later" is
not a plan when the alternative is delaying a real practice's launch.

---

## 1. Azure OpenAI has no PHI-safe deployment

**Status:** Open. Discovered 2026-08-23 during provisioning.

The only non-zero gpt-5 quota on this subscription is `OpenAI.GlobalStandard.gpt-5-mini`
(500K TPM). Every `DataZoneStandard` and regional `Standard` quota for the entire gpt-5 family
is **0** — verified via `az cognitiveservices usage list -l eastus`.

`GlobalStandard` routes inference to any Microsoft region worldwide. That is acceptable for
synthetic development data and **not** acceptable for PHI, because it defeats any data-residency
claim in `HIPAA_DATA_FLOW.md`.

**Action:** request `DataZoneStandard` quota (US data zone) for the chosen production model via
Azure AI Foundry → Quotas. Redeploy and repoint config. Quota requests are reviewed by Microsoft
and are not instant — start early.

**If refused:** regional `Standard` quota for `gpt-5.1` is the fallback (it is the only current
GA model offering that SKU). If neither is granted, PHI cannot flow through Azure OpenAI and the
extraction/generation steps must move to a provider that will commit to residency.

**Partial mitigation already designed:** de-identification before the model call (see
`AI_PIPELINE.md`) means the model receives tokenized text rather than names and dates. This
reduces exposure but does not eliminate it — a transcript can contain identifiers we did not
anticipate — so it does not close this item on its own.

---

## 2. Azure OpenAI abuse monitoring retains prompts for 30 days

**Status:** Open.

By default, Azure OpenAI stores prompts and completions for up to 30 days for abuse monitoring,
and Microsoft reviewers can access **flagged** content through Secure Access Workstations with
just-in-time approval. Storage is in-region and not shared with OpenAI or other Microsoft teams.

This is within the BAA, but it means human review of therapy transcripts is possible.

**Action:** apply for **Modified Abuse Monitoring**, which removes human review and that storage.

**Important:** an executed HIPAA BAA does **not** automatically qualify a customer — Modified
Abuse Monitoring, Zero Data Retention, and No Human Review are separate programs with independent
eligibility, generally aimed at enterprise or managed customers. **A solo practice on
pay-as-you-go may not qualify.**

**If refused:** the de-identification design in `AI_PIPELINE.md` becomes the primary control
rather than a secondary one, and the residual risk is recorded and accepted in the risk analysis
with Michelle's sign-off. Do not launch without that sign-off being explicit.

---

## 3. Azure Container Apps HIPAA eligibility unconfirmed

**Status:** Open.

Azure SQL Database, App Service, AKS, Functions, and Key Vault are explicitly named HIPAA-eligible
in the Microsoft Product Terms. **Container Apps was not found** in that list during research.

**Action:** verify against the current Product Terms / Service Trust Portal.

**If not listed:** swap the compute target to **App Service for Containers**, which is listed.
Same container image, same registry, different deployment target — no application code changes.
The `/infra` scripts change; nothing above them does.

---

## 4. BAA names the wrong legal entity

**Status:** Open.

Microsoft's HIPAA BAA flows through the Product Terms and names **the customer entity on the
subscription**. The subscription is currently under a personal account
(`doa9200@gmail.com`, "Azure subscription 1").

If the Covered Entity is Michelle's practice, the BAA names the wrong party — and that surfaces
during BAA verification, after everything is deployed.

**Action:** decide whether the subscription should move to the practice's business identity.
If the practice is a registered entity, do this before resources multiply — migration cost grows
with every resource added.

---

## 5. Standing gates from presearch

These are required by the spec itself, not discoveries:

- [ ] Documented security risk analysis (§14.6) — where ePHI lives, travels, who can reach it,
      threats, controls, likelihood, impact, residual risk, review cadence.
- [ ] Data-flow diagram covering every hop that touches PHI (§30).
- [ ] Threat model (`docs/THREAT_MODEL.md`).
- [ ] Vendor review for **every** service that creates, receives, maintains, or transmits ePHI
      (§14.5) — hosting, database, storage, auth, logging, email, transcription, LLM, monitoring,
      backups.
- [ ] Maryland requirements verified against authoritative sources — confidentiality, retention,
      and the specific implications for minors' records (§15).
- [ ] Audio and transcript retention policies implemented and tested, not just written.
- [ ] Restore-from-backup actually rehearsed, not assumed.
- [ ] Confirmation that no real PHI has ever entered a synthetic-only path.

---

## Rule

> Would we be comfortable putting an actual child's medical information through this exact
> data path? (§34)

If the answer is no, the path stays synthetic-only until it is corrected. That question governs
every row above.
