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
