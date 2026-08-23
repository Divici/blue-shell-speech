# Tech Context

## Stack (locked — presearch.md §29)

| Layer | Choice | Why |
|---|---|---|
| Frontend | Next.js App Router, TypeScript strict | RSC by default; client JS only where interaction demands it |
| Backend | ASP.NET Core (.NET 10 LTS) | Real system of record, not decoration |
| ORM / DB | EF Core → Azure SQL Database | Strongly relational domain; free offer fits current scale |
| Auth | ASP.NET Core Identity, self-hosted, TOTP MFA | Keeps identity inside the Azure/BAA boundary; no extra vendor |
| API boundary | BFF | Browser → Next route handlers/server actions → .NET. Never browser → .NET |
| Transcription | Azure Speech STT | HIPAA-eligible under Microsoft BAA |
| Extraction + generation | Azure OpenAI, **text endpoints only** | Audio/voice models are **not** BAA-covered |
| Hosting | Two containers, Azure Container Apps, scale-to-zero | Portable; ~$0 at current scale |
| Repo | Monorepo — `/web` `/api` `/docs` `/infra` | Public GitHub |

## Verified numbers

- **ACA free grant:** 180,000 vCPU-s + 360,000 GiB-s + 2M requests, per subscription, monthly.
- **Cost of removing cold starts:** `minReplicas: 1` on 0.25 vCPU / 0.5 GiB ≈ **$14/month per
  container**. That is the upgrade lever; know it by heart.
- **Azure SQL free offer:** 100,000 vCore-s + 32 GB data + 32 GB backup, per database, monthly,
  lifetime of subscription. Configure **auto-pause on limit**, not overage billing (§13.3).
- **Vercel Hobby prohibits commercial use** — unavailable to us, this is a commercial practice.
- **Azure Static Web Apps hybrid Next.js is still preview**, cannot link to Container Apps
  backends, 250 MB cap. Rejected.

## Constraints that shape design

- Cold starts are accepted **and must be measured** (§12.2). The measurement is a deliverable.
- Infrastructure must stay replaceable — no Azure specifics in the domain model (§12.3).
- Cost optimisation must never compromise security, PHI handling, reliability, recoverability,
  or maintainability (§3.4).

## Open item — blocks go-live

**Azure Container Apps was not confirmed HIPAA-eligible** in the Microsoft Product Terms.
App Service, AKS, Functions, Key Vault and Azure SQL are explicitly listed; ACA was not found.
Verify before real PHI. If absent, App Service for Containers is the swap — same container,
different target, zero application change.
