# Deployment

From `presearch.md` §12, §13, §23. Two environments: **local** and **production** (D015).

---

## Topology

| Resource | Name | Notes |
|---|---|---|
| Resource group | `blueShellRG` | `eastus`, everything co-located |
| Container Apps env | *to create* | Two apps, consumption plan |
| `web` | Next.js | **External ingress**, `minReplicas: 0` |
| `api` | ASP.NET Core | **Internal ingress**, `minReplicas: 0` |
| Azure SQL | *to create* | Free offer, **auto-pause on limit rather than billing** |
| Storage | *to create* | Separate containers: clinical audio, public resources |
| Azure OpenAI | `blueshellOpenAI` | `gpt-5-mini-global` — **dev-only**, blocker #1 |
| Azure Speech | `blueshellSpeech` | Free F0 |
| Registry | *to create* | ACR or GHCR |
| Key Vault | *to create* | The few secrets managed identity cannot replace |

Provisioning is scripted in `/infra` (§12.3). **A destroyed resource group is a re-run, not
archaeology.** `provision-ai.sh` already covers the AI half.

---

## Cost posture

Target: **$0/month outside AI spend.**

| Service | Free allowance |
|---|---|
| Container Apps | 180,000 vCPU-s + 360,000 GiB-s + 2M requests / month / subscription |
| Azure SQL | 100,000 vCore-s + 32 GB data + 32 GB backup / month, lifetime of subscription |
| Speech F0 | 5 audio hours / month |
| Blob | Minimal — audio is deleted on signature |

Both containers scale to zero, so steady-state cost is genuinely zero rather than merely low.

**Azure SQL's free offer auto-pauses on limit instead of billing.** Chosen deliberately: for a
solo practice a surprise invoice is worse than a pause, and the capacity banner plus admin
alerts (§13) mean a pause is never a surprise.

**Cold start is the price** (D001). Measuring it is a slice-1 deliverable. If the number is
unacceptable, `minReplicas: 1` on `web` alone is ~$14/mo and changes nothing else.

---

## Pipeline

```
PR      → lint · typecheck · unit · integration (Testcontainers) · build both images
main    → build · push to registry · deploy both apps as a new revision
        → health checks · smoke test · shift traffic
        → rollback = repoint traffic at the previous revision
```

**GitHub OIDC federated identity. No long-lived Azure secret in GitHub.**

The repo is public, so `pull_request_target` is banned, fork PRs receive no secrets, action SHAs
are pinned, and **build logs are assumed world-readable — because they are.**

---

## Migrations

EF Core migrations run as a **pre-deploy step, not at app startup.** Two scale-to-zero replicas
waking simultaneously and both running migrations is a race with a corrupted schema at the end
of it.

Forward-only. Every migration must be safe against the previous app revision, because both
versions are briefly live during a rollout.

---

## Configuration

| Setting | Source |
|---|---|
| SQL connection | Managed identity — no password exists |
| Storage | Managed identity |
| Speech / OpenAI endpoints | App config — **endpoints are not secrets** |
| Speech / OpenAI auth | Managed identity; `disableLocalAuth=true` once wired |
| Practice phone / email | Env config. **Never in the tree** |
| Anything remaining | Key Vault, referenced by Container Apps |

---

## Local

`docker compose`: SQL Server, Azurite, `web`, `api`.

Points at **real Azure Speech and OpenAI endpoints with synthetic data** (D015). Mocking a model
provider locally teaches nothing about latency, refusals, or malformed output — precisely the
failure modes §19 requires the app to survive.

---

## Go-live gate

**No real patient data until every item in `PRELAUNCH_BLOCKERS.md` is closed and slice 10 is
signed off.** Development is synthetic-only until then (§22), without exception.
