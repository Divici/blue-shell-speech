# Project Brief

**Blue Shell Speech** — a production practice platform for a solo Maryland pediatric
speech-language pathologist (Michelle), plus the public website for her practice.

Authoritative spec: `presearch.md`. This file summarises; that file decides.

## Two goals, both real

1. **Product.** Software Michelle actually runs her early private practice on — ~5 patients
   initially, plausibly ~20 within months.
2. **Engineering evidence.** Demonstrable React/Next.js judgment plus deliberately-learned
   modern .NET, defensible in a senior code walkthrough.

Neither goal is allowed to make the other worse. Where they conflict, the product wins and the
tradeoff gets documented — that documentation *is* the engineering evidence.

## The governing principle

> Build a small number of real workflows completely, rather than a large number of
> superficial EHR features.

This is not another EHR. Scheduling, records, and SOAP notes are commodity. The differentiator
is the post-session workflow for an in-home clinician.

## Success

**Michelle can:** log in securely · see her day · manage patients, guardians, goals ·
schedule in-home sessions and evaluations · complete a session · dictate a recap ·
receive a structured SOAP draft · review, edit, approve · reliably retrieve the record later.

**A parent can:** understand who she serves and what she offers · learn about her ·
request a free consultation · use the site comfortably on mobile and with assistive tech.

**The system is:** deployed · authenticated · persistently stored · audited · tested ·
backed up · ~$0 fixed infrastructure cost at current scale · on an obvious paid upgrade path.

## Explicitly not building

Insurance claims, ERA/EOB, eligibility checks, telehealth, patient portal, secure messaging,
multi-provider admin, e-prescribing, lab integrations, card processing, large-scale reporting,
full accounting, automated route optimization, multi-tenant SaaS, AI diagnosis, or any
autonomous clinical decision-making.
