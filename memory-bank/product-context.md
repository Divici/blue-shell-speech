# Product Context

## The problem, stated by the user

During therapy Michelle does not want to type notes — it takes attention off the child and
makes the session worse. So she holds the session in her head, drives home, and reconstructs
the note hours later from fading memory.

Her own proposed fix, which became the hero feature:

> Immediately after the session, dictate the relevant information while it is fresh. The
> system transcribes and structures it, prepares the documentation, and leaves a draft ready
> to review and approve later.

## Primary user

One clinician. Pediatric, birth to age 5. Early intervention and AAC are focus areas.
Sessions happen **in patients' homes**, so she is mobile, often in a car, frequently on
imperfect cellular. Private-pay; payment handled outside the app for now.

Optimise for one clinician doing real work on a phone — not for a hypothetical org chart.

## The hero flow

Session ends → dictate while stationary → transcribe → **structured extraction** →
schema validation → compare against active goals → flag missing information →
generate SOAP draft → she reviews, fills gaps, edits → she signs.

The middle of that chain is the point. `transcript → "write a SOAP note"` is the anti-pattern
(presearch §7.4). Extract facts, validate them, *then* generate.

## Rules the product cannot break

- **Never invent a clinical observation.** No fabricated accuracy, trial count, cueing level,
  caregiver report, intervention, behavior, diagnosis, or treatment response. Missing is
  marked missing (§7.5).
- **AI output is always a draft.** The clinician reviews and signs. AI never finalises a
  clinical record (§7.6).
- **AI is an accelerator, never a dependency.** Records, scheduling, and manual SOAP notes
  work fully with transcription and generation down (§6.2).
- **No workflow requires screen interaction while driving** (§7.7).

## Experience direction

Calm, mobile-first, low cognitive load, iOS-like clarity. Deliberately *not* the dozens-of-tabs
dense-table look of traditional EHRs. For one provider the product can be far more opinionated.
The most common tasks should take very few actions.
