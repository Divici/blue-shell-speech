# Slice 1 — Acceptance Verification

Spec-critic pass against the criteria frozen in `docs/IMPLEMENTATION_PLAN.md`.

**Rule:** a criterion is met only with evidence — a passing test, a measured number, or a
file that can be pointed at. "Looks right" is not evidence.

Verified 2026-08-24. CI green across all five jobs.

---

| # | Criterion | Evidence | Status |
|---|---|---|---|
| 1 | Sections in confirmed order | `e2e` asserts headings and their relative order | **Met** |
| 2 | Copy matches `SITE_CONTENT.md`, credentials not embellished | Copy lives in `lib/site-content.ts`, mirrored from the doc | **Met** |
| 3 | Service chips include AAC | `e2e` asserts `#services` contains "AAC" | **Met** |
| 4 | Nav anchors scroll; Consultation → `/consultation`; Login → `/login`, secondary | `e2e` asserts every nav href is an anchor, plus both routes | **Met** |
| 5 | No services grid, no testimonials, no Resources tab | `e2e` asserts each removed string is absent | **Met** |
| 6 | `/consultation` posts, persists, contentless notification | `ConsultationRequest` + migration; `POST /consultation-requests`; notification contentless by signature | **Met** (2026-08-25) |
| 7 | Form has validation / error / loading / success states | `e2e` covers empty submit and success; `useFormStatus` drives pending | **Met** |
| 8 | Lighthouse ≥90/95/95/95 mobile | **100 / 100 / 100 / 100** deployed | **Met** |
| 9 | LCP ≤2.5s, INP ≤200ms, CLS ≤0.1 | **LCP 1.6s · CLS 0 · TBT 10ms** deployed | **Met** |
| 10 | Cold-start latency measured and recorded | **~22s**, `docs/PERFORMANCE.md` | **Met** |
| 11 | Body copy ≥4.5:1, comps' gray darkened, deviation noted | 55 unit tests incl. alpha compositing; axe clean | **Met** |
| 12 | Keyboard navigable, visible focus, correct heading order | `e2e` skip-link + single-h1; axe checks order | **Met** |
| 13 | Images responsive AVIF/WebP, not multi-MB PNG | 2063 KB → 20 KB, 2398 KB → 19 KB | **Met** |
| 14 | Missing assets generated as optimized SVG | Shell mark, waves, blobs, bubbles, 11 icons | **Met** |
| 15 | Contact from env; no address in the tree | `resolvePracticeContact` throws in prod; `e2e` asserts no address | **Met** |

---

## The one partial — closed 2026-08-25 (WORK_QUEUE 1.5)

**Criterion 6 — persistence.** For the length of slices 1–5 this form validated on the server,
rejected bad input, blocked bots, and confirmed to the parent while storing **nothing**. That
was recorded here rather than quietly counted as passing, which is the distinction Guardrail 1
exists to protect.

It is closed. The submission is written to `ConsultationRequests` — with its
`ConsultationRequestReceived` audit row in the same transaction — before the parent is thanked,
and the notification is contentless *by signature*: `IConsultationNotifier` has no parameter
through which a child's name could travel.

**What changed beyond adding a write:** a confirmation is now a claim about a row, so every
path that fails to produce one had to stop making it. An unreachable API, a practice with no
clinician to receive the enquiry, or a submission the API refuses all render an error that
keeps what the parent typed and points at the phone number. Telling a family "we'll be in
touch" about an enquiry that vanished is worse than an error, because they do not follow up
and nobody ever finds out.

**Still open, and tracked rather than implied** (WORK_QUEUE 1.13): the notification is composed
and logged, not sent, because the practice has no mailbox — "real practice phone and email" is
on the Blocked list, and a verified sender domain needs the domain purchase blocked with it.
And nothing yet renders the enquiries that email tells Michelle to sign in and read.

---

## Findings this slice produced

Real defects caught, each now covered by a regression test or a recorded decision:

1. **The comp's primary button fails WCAG AA** — white on `#2D7FF9` is 3.81:1 (D033).
2. **The comp's dark swatch label is wrong** — labelled `#AA5568`, renders `#4E5B6D` (D032).
3. **Contrast tests missed translucent text** — `text-white/60` shipped at 3.91:1 (D034).
4. **A `<dl>` with `<div>` wrappers** — valid HTML, rejected by axe (D035).
5. **Three defects invisible in source** — logo silhouette, H1 line breaks, a wave path
   spanning only x=1296 of a 1440 viewBox (D036).
6. **Lighthouse localhost LCP is an artifact** — 2.9s local vs 1.6s deployed (D039).
7. **A Chromium-shaped keyboard test** — caught by WebKit on its first run (D040).

---

## Remaining before the slice closes

- [x] Persist consultation requests — done 2026-08-25, WORK_QUEUE 1.5
- [ ] Visual gauntlet round against the comps
- [ ] `/super-review` pass at the slice boundary
- [ ] Cold start resolved via CDN — blocked on the domain, tracked as blocker #6
