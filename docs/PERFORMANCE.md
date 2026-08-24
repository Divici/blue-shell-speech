# Performance Measurements

Real numbers from the deployed dev environment. Not estimates.

---

## Cold start — the D001 deliverable

`DECISIONS.md` D001 chose scale-to-zero and committed to measuring the cost rather than
assuming it. Measured 2026-08-24 against `blueshell-dev-web`, confirmed scaled to zero
before each run.

| Condition | TTFB |
|---|---|
| **Cold, first pull** (image not on node) | **21.7 s** |
| **Cold, image cached on node** | **23.2 s** |
| **Cold, 1.0 vCPU / 2 GiB** | **22.4 s** |
| Warm | **0.035 – 0.052 s** |

### What the numbers rule out

- **Not a first-pull penalty.** The second cold start was no faster than the first, so
  registry pull time is not the cost.
- **Not app boot, and not image size.** Quadrupling CPU changed nothing (23.2 s → 22.4 s).
  A Next.js standalone server boots in well under a second on 0.25 vCPU.

What remains is **Container Apps activation latency itself** — scheduling the replica and
wiring ingress. That is platform behaviour, not something the application can optimise.

### Why this matters

Warm performance is excellent: 35 ms TTFB. Every visitor arriving while the container is
already awake gets an outstanding experience.

The first visitor after an idle period waits **~22 seconds**. On a marketing site for a
paediatric practice, that visitor is a parent who leaves. It also breaks the frontend rules'
LCP budget of 2.5 s by roughly 9x, and it is not a budget the application can win back —
the page itself is statically prerendered and 63 KB.

**For a solo practice with low, bursty traffic, nearly every visitor is the first visitor.**

### Options

| Option | Cost | Cold start | Notes |
|---|---|---|---|
| Keep `minReplicas: 0` | **$0** | ~22 s | Warm visitors unaffected |
| `minReplicas: 1` on `web` only | **~$14/mo** | none | `api` stays scale-to-zero; it is only reached after a parent is already engaged |
| CDN in front, caching static HTML | **$0** on a free tier | none for cache hits | Adds a vendor; only the public site, which holds no PHI |

`api` should stay at `minReplicas: 0` regardless. It is only reached after authentication or
a form submission, where a few seconds is tolerable and the traffic is Michelle's, not a
prospective parent's.

**Open — needs a decision.** This is a recurring cost against an explicit $0 target, so it
is not ours to make silently.

---

## Page weight

| Asset | Original | Shipped | Reduction |
|---|---|---|---|
| `children.png` | 2063 KB | 19 KB (720w AVIF) | **99.1%** |
| `headshot.PNG` | 2398 KB | 18 KB (480w AVIF) | **99.2%** |
| Homepage HTML | — | 63 KB | — |

Fonts are self-hosted via `next/font` — no runtime request to Google, and no font-swap
layout shift.

---

## Method

```bash
# Confirm scaled to zero
az containerapp replica list --name blueshell-dev-web -g blueShellRG --query "length(@)"

# Measure
curl -sS -o /dev/null \
  -w "ttfb=%{time_starttransfer}s total=%{time_total}s\n" \
  https://<fqdn>/
```

Re-run after any change to image size, base image, or container resources.

---

## Decision (2026-08-24)

**CDN in front of the public site**, deferred until the practice domain is purchased.
`minReplicas` stays 0. See `DECISIONS.md` D038.

Interim: ~22 s cold start on the dev subscription, which has no real users. **This is a
go-live blocker** — the CDN or `minReplicas: 1` must be in place before the first parent
visits the site.

---

## Lighthouse — deployed, warm, mobile profile

Run against the deployed dev app after warming the container. 2026-08-24.

| Category | Score | Target |
|---|---|---|
| Performance | **100** | 90 |
| Accessibility | **100** | 95 |
| Best practices | **100** | 95 |
| SEO | **100** | 95 |

| Metric | Value | Budget |
|---|---|---|
| First Contentful Paint | 1.0 s | — |
| **Largest Contentful Paint** | **1.6 s** | 2.5 s |
| **Cumulative Layout Shift** | **0** | 0.1 |
| Total Blocking Time | 10 ms | — |
| Speed Index | 1.0 s | — |

All slice 1 performance criteria met.

### Measure against the deployment, not localhost

The same page measured on `localhost` scored **95** with an LCP of **2.9 s** — a fail
against the 2.5 s budget that sent me chasing two wrong hypotheses.

The cause: Lighthouse's default *simulated* throttling models slow-4G round-trip latency on
top of observed timings. Against localhost, where real RTT is ~0, the model produces a
"render delay" that does not exist. The trace showed it plainly once read properly —
**2451 ms of render delay with 0 ms of load time**, and every asset finishing within 51 ms.

Two hypotheses were tested and eliminated before the artifact was identified:

1. *The hero image was too large for mobile.* Real: it was hardcoded to a single 1080w
   source and a 390px phone was downloading it. Fixed — but LCP did not move.
2. *`decoding="sync"` was blocking presentation of a CPU-heavy AVIF.* Plausible, and
   also wrong. Fixed — LCP did not move.

Both fixes were worth keeping. Neither was the reported problem.

**Rule: LCP and any latency-derived metric are measured against the deployed site.**
Localhost is valid for accessibility, best practices, SEO, and bundle size.

### Payload

| Asset | Size |
|---|---|
| HTML | 63 KB |
| CSS | 7 KB |
| Fonts (Inter + Playfair 700) | ~48 KB |
| Hero image (720w AVIF) | 20 KB |
| Headshot (480w AVIF) | 19 KB |

Playfair weight 600 was requested in the font config and never referenced — one whole font
file for nothing. Fonts are the largest payload on this page, so an unused weight is the
most expensive dead code available.
