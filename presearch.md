# Pre-Research Brief: Production-Quality SLP Practice Platform

## 0. Purpose of this document

This document is the handoff brief for planning and building a
production-capable V1 for a solo pediatric speech-language pathology
(SLP) private practice.

The application has two simultaneous goals:

1.  **Real product goal:** Build software that can genuinely support the
    owner's early private-practice workflow, initially at roughly 5
    patients and plausibly growing to 20 patients within the first few
    months.
2.  **Engineering/interview goal:** Demonstrate strong React/Next.js
    frontend engineering and the curiosity/tinkering ability to learn
    and use modern .NET well enough to discuss the architecture and code
    confidently in a technical interview with Sandstorm Design.

This must **not** be treated as a throwaway demo. The product should be
deployed, usable, secure, reliable at its intended scale, and designed
from the beginning around handling clinical information responsibly.
However, this is intentionally a **narrow V1**, not an attempt to
recreate a full commercial EHR.

The planning principle is:

> Build a small number of real workflows completely, rather than a large
> number of superficial EHR features.

------------------------------------------------------------------------

# 1. Product context

## 1.1 Primary user

The initial provider is a Maryland-licensed speech-language pathologist
starting a solo private practice.

Initial practice characteristics:

-   Solo provider.
-   Pediatric population, primarily **birth to age 5**.
-   Early intervention is a major focus.
-   AAC is an area of focus.
-   Sessions may occur **in patients' homes**.
-   Private-pay model initially.
-   The provider is comfortable accepting payment outside the
    application initially (for example, Zelle).
-   Families may eventually receive superbills to pursue out-of-network
    reimbursement themselves.
-   Expected initial caseload: approximately **5 patients**.
-   Expected near-term caseload: potentially **up to 20 patients**
    within the first few months.

The application should therefore be optimized for one clinician rather
than designed prematurely for a large organization.

## 1.2 Direct user research

The provider identified the following basic needs.

### Private practice/EMR needs

She wants a system that can:

-   Schedule therapy sessions and evaluations.
-   Track clinical data and SOAP notes.
-   Store patient information and documentation.
-   Potentially support billing eventually, although integrated payments
    are not necessary initially.

### Public website needs

She wants a public website containing:

-   Information about the SLP.
-   Description of the population served (birth to 5).
-   Services/areas such as early intervention and AAC.
-   Ability for parents to book/request a free consultation call.
-   Free downloadable handouts/resources for early intervention and AAC.
-   Contact information.

### Most important workflow pain discovered so far

The strongest product insight came directly after an actual private
session.

During therapy, the SLP does not want to spend the session actively
typing detailed notes because doing so takes attention away from the
child and reduces session quality.

Instead, she retains much of the session information mentally.

The problem is that she then has to retain those details until she gets
home and reconstruct the clinical note later.

Her proposed high-value workflow is:

> Immediately after the session, dictate the relevant session
> information while it is fresh. The system transcribes and structures
> it, prepares the appropriate documentation, and leaves a draft ready
> for her to review and approve later.

This should be treated as the **hero workflow** of V1.

------------------------------------------------------------------------

# 2. Product positioning

Do **not** frame the project as "building another full EHR."

The intended product is:

> A lightweight practice-management and clinical-documentation platform
> for a solo in-home pediatric SLP, centered around dramatically
> reducing post-session documentation friction.

The product combines:

1.  A polished public-facing practice website.
2.  A secure authenticated provider application.

They should feel like one coherent product and can share one domain and
Next.js application, while maintaining strict security boundaries
between public and clinical functionality.

Example conceptual URLs:

``` text
practice-domain.com/
practice-domain.com/about
practice-domain.com/services
practice-domain.com/resources
practice-domain.com/consultation

practice-domain.com/login

practice-domain.com/app
practice-domain.com/app/schedule
practice-domain.com/app/patients
practice-domain.com/app/documentation
```

------------------------------------------------------------------------

# 3. Core product principles

## 3.1 Production-quality V1, not finished commercial EHR

"Production ready" here means:

-   Actually deployed.
-   Real authentication.
-   Real persistent database.
-   Real backend API.
-   Real error handling.
-   Real validation.
-   Responsive.
-   Accessible.
-   Tested.
-   Backed up.
-   Monitored appropriately.
-   Secure enough for the intended clinical use after compliance gates
    are satisfied.
-   Designed so that growth does not require rewriting the application.

It does **not** mean implementing every possible healthcare feature.

## 3.2 Scope discipline

Avoid initially building:

-   Insurance claim submission.
-   ERA/EOB processing.
-   Insurance eligibility verification.
-   Telehealth.
-   Patient portal.
-   Complex secure messaging.
-   Multi-provider administration.
-   E-prescribing.
-   Lab integrations.
-   Integrated card processing.
-   Large-scale reporting.
-   Full accounting.
-   Generic hospital EHR features.
-   Automated route optimization until an appropriate
    provider/data-handling arrangement is verified.

## 3.3 Optimize for actual scale

The initial system serves:

-   1 provider.
-   \~5 initial patients.
-   Potentially \~20 patients in the near term.

Do not architect infrastructure as though it has thousands of concurrent
users.

The architecture should be able to scale later, but current cost and
operational simplicity matter.

## 3.4 Cost target

The target is:

> **Approximately \$0/month in fixed infrastructure cost at the initial
> scale wherever possible.**

Acceptable expenses:

-   Domain registration.
-   Small usage-based AI/transcription costs.
-   Eventually paid infrastructure once usage or reliability
    requirements justify it.

Do not pay \$10-\$30/month merely to keep mostly idle compute running if
a secure consumption/scale-to-zero alternative is available.

Cost optimization must **not** compromise:

1.  Security.
2.  Appropriate handling of PHI.
3.  Reliability.
4.  Recoverability.
5.  Maintainability.

------------------------------------------------------------------------

# 4. V1 functional scope

## 4.1 Public website

The public website should be fully polished.

Required:

### Home

-   Clear value proposition.
-   Who the SLP serves.
-   Major services.
-   Strong consultation CTA.
-   Contact information.

### About

-   Provider biography.
-   Credentials.
-   Philosophy/approach.
-   Population served.

### Services

At minimum: - Birth-to-5 speech/language services. - Early
intervention. - AAC.

### Resources

-   Publicly accessible parent resources.
-   Downloadable handouts.
-   Resources should be structured so additional resources can be added
    later.
-   SEO-friendly individual resource pages are desirable.

### Consultation

-   Parent can request/book a free consultation.
-   Exact scheduling implementation should be chosen during planning
    based on scope/security.

### General frontend requirements

-   Responsive/mobile-first.
-   Strong accessibility.
-   Semantic HTML.
-   Keyboard navigation.
-   Appropriate color contrast.
-   Screen-reader-conscious implementation.
-   Good Core Web Vitals/performance.
-   SEO/metadata/Open Graph.
-   Reusable design system/components.

------------------------------------------------------------------------

# 5. Authenticated provider application

## 5.1 Authentication

Initial system has one provider.

Requirements:

-   Secure provider login.
-   MFA.
-   Secure session handling.
-   Server-side authorization.
-   Appropriate inactivity/session expiration.
-   No client-side-only authorization decisions.

Avoid building elaborate RBAC for hypothetical future employees.

Initial role model may simply be:

``` text
Provider
```

Architecture should permit future roles such as Owner, Provider, or
Administrative without requiring a rewrite.

## 5.2 Dashboard

Useful at-a-glance information:

-   Today's appointments.
-   Upcoming evaluations.
-   Notes needing completion/review.
-   Dictation drafts ready.
-   Potential system-capacity warning.
-   Potential documentation reminders.

Keep dashboard focused rather than turning it into analytics software.

## 5.3 Patient records

Store only information actually required for practice workflow.

Likely initial data:

-   Patient name.
-   Date of birth.
-   Guardian(s).
-   Contact information.
-   Home/session address.
-   Relevant clinical information.
-   Active/inactive status.
-   Treatment goals.
-   Session history.
-   Clinical notes.
-   Evaluations/documents where needed.

Clinical data models should be planned carefully rather than implemented
as arbitrary JSON blobs.

## 5.4 Treatment goals

Each patient may have active treatment goals.

Goals are particularly important because the dictation system should
understand what the clinician is currently targeting.

Possible goal attributes:

-   Goal ID.
-   Patient ID.
-   Goal text.
-   Domain/category.
-   Target criteria.
-   Cueing expectations where applicable.
-   Status.
-   Start date.
-   Completion/discontinuation date.

Avoid over-engineering a universal clinical goal language in V1.

## 5.5 Scheduling

Support:

-   Therapy appointments.
-   Evaluations.
-   Date/time.
-   Patient.
-   Appointment type.
-   Location/address.
-   Duration.
-   Status.
-   Basic notes if needed.

Possible statuses:

``` text
Scheduled
Completed
Cancelled
NoShow
```

The schedule should work particularly well on mobile.

## 5.6 Daily visit/trip view

Because sessions occur in patient homes, the provider needs a practical
daily view.

V1 can include:

-   Chronological visits.
-   Patient.
-   Address.
-   Appointment duration.
-   Planned travel block.
-   Mileage field.
-   Visit status.
-   Manual departure/travel information.

Do not automatically send patient-identifying health information to a
mapping provider unless that provider and data flow have been explicitly
evaluated for the intended healthcare use.

Automated routing is not required for initial V1.

------------------------------------------------------------------------

# 6. Clinical documentation

## 6.1 SOAP notes

Provider needs normal SOAP documentation.

Lifecycle should distinguish at minimum:

``` text
Draft
Approved/Signed
Amended
```

An approved clinical note should not behave like an ordinary mutable
CRUD record.

Do not silently overwrite signed clinical documentation.

If a signed note changes, preserve an audit trail and model it as an
amendment/version according to the final compliance design.

## 6.2 Manual note workflow

The provider must always be able to create/edit a SOAP note without AI.

AI is an accelerator, not a hard dependency for clinical documentation.

The application remains usable if transcription/AI is temporarily
unavailable.

------------------------------------------------------------------------

# 7. Hero feature: post-session voice recap

## 7.1 User problem

The provider has high-quality information immediately after a session
but cannot comfortably type it during therapy.

The application should capture that information immediately after the
session while it is fresh.

## 7.2 Ideal user workflow

``` text
Appointment
    ↓
Session ends
    ↓
Provider opens/starts recap while stationary
    ↓
Provider dictates naturally
    ↓
Speech-to-text
    ↓
Structured clinical extraction
    ↓
Validate extracted information
    ↓
Compare against active patient goals
    ↓
Identify missing information
    ↓
Optional follow-up prompts
    ↓
Generate SOAP draft
    ↓
Save draft
    ↓
Provider reviews later
    ↓
Provider edits if necessary
    ↓
Provider approves/signs
```

## 7.3 Dictation should be natural

Do not require the provider to speak in SOAP-note syntax.

Example input:

> Maya did really well today. We mostly worked on two-word combinations
> and requesting. She was independently requesting around sixty percent
> of the time and around eighty percent with minimal verbal cues. Mom
> said she has started saying "want juice" at home. We didn't work on
> her two-step directions today. Next session I want to introduce more
> requesting opportunities during play.

The system should extract facts rather than simply reformatting the
transcript.

## 7.4 Structured extraction

Do not use:

``` text
Transcript -> "Write a SOAP note"
```

as the entire AI architecture.

Prefer:

``` text
Transcript
    ↓
Structured extraction
    ↓
Schema validation
    ↓
Missing-information analysis
    ↓
Validated structured session data
    ↓
SOAP generation
```

Illustrative structured representation:

``` json
{
  "goalsAddressed": [
    {
      "goalId": "goal_123",
      "independentAccuracy": 0.60,
      "accuracyWithCueing": 0.80,
      "cueLevel": "minimal_verbal"
    }
  ],
  "goalsNotAddressed": ["goal_456"],
  "caregiverReports": [
    "Patient has begun using 'want juice' at home."
  ],
  "nextSessionPlan": "Increase requesting opportunities during play.",
  "missingInformation": []
}
```

Exact schema should be designed during implementation planning.

## 7.5 Hallucination rule

The system must **never intentionally invent clinical observations**.

If required information is absent:

-   Mark it missing.
-   Ask the clinician if appropriate.
-   Leave it unspecified.

Do not fabricate:

-   Accuracy.
-   Trial count.
-   Cueing level.
-   Caregiver report.
-   Intervention.
-   Patient behavior.
-   Diagnosis.
-   Treatment response.

## 7.6 Human-in-the-loop requirement

AI output is always a **draft**.

The clinician:

-   Reviews.
-   Edits.
-   Approves/signs.

The AI does not authoritatively finalize the clinical record.

## 7.7 Driving safety

The product concept includes post-session dictation that may occur
around travel between home visits.

Do not design a workflow requiring screen interaction while driving.

Production design should assume:

-   Dictation is initiated while stationary, or through a truly
    hands-free mechanism.
-   Once moving, no visual interaction should be necessary.
-   No typing/tapping workflow should be encouraged while driving.

A more sophisticated automotive integration is not V1.

------------------------------------------------------------------------

# 8. AI/transcription provider strategy

## 8.1 Provider abstraction

Do not tightly couple the clinical domain to one AI vendor.

.NET should expose interfaces similar to:

``` csharp
public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        AudioInput audio,
        TranscriptionContext context,
        CancellationToken cancellationToken);
}
```

Possible implementations:

``` text
OpenAiTranscriptionService
AzureSpeechTranscriptionService
DeepgramTranscriptionService
```

Similarly consider abstractions for:

``` text
IClinicalExtractionService
IClinicalNoteGenerationService
```

Benefits:

-   Vendor can change without rewriting the domain.
-   Providers can be benchmarked.
-   Compliance changes do not force architecture rewrites.
-   Failure/fallback behavior can be introduced later.
-   Excellent demonstration of dependency inversion/DI in ASP.NET Core.

## 8.2 Development benchmarking

The developer currently has approximately **\$100 in OpenRouter
credits**.

OpenRouter may be useful for development benchmarking with **synthetic
data only**.

Do not route real PHI through a provider merely because development
credits exist.

Create a synthetic SLP dictation evaluation corpus and compare
transcription models on:

-   General word error rate.
-   Clinical terminology.
-   AAC terminology.
-   Numbers.
-   Percentages.
-   Trial counts.
-   Cueing levels.
-   Proper names.
-   Noisy/car-like environments.
-   False starts.
-   Natural conversational dictation.

This could itself be a valuable engineering artifact.

## 8.3 Production transcription selection

Production provider must be selected based on:

1.  Accuracy.
2.  Reliability.
3.  Cost.
4.  Appropriate healthcare contractual/compliance support.
5.  Data retention behavior.
6.  Ability to prevent training on customer PHI where required.
7.  Availability and failure characteristics.

Current candidates worth validating during planning include:

-   Azure Speech.
-   Direct OpenAI transcription APIs.
-   Deepgram medical/general transcription.

Do not assume the development provider is automatically the production
provider.

------------------------------------------------------------------------

# 9. Proposed technical architecture

## 9.1 Frontend

Use **Next.js** with modern App Router architecture.

Conceptual route organization:

``` text
app/
  (marketing)/
    page.tsx
    about/
    services/
    resources/
    resources/[slug]/
    consultation/

  (auth)/
    login/

  (practice)/
    app/
      layout.tsx
      page.tsx
      schedule/
      patients/
      patients/[id]/
      documentation/
      settings/
```

Route groups allow public and authenticated experiences to remain
organized without leaking implementation grouping into URLs.

## 9.2 Frontend engineering goals

The project should intentionally demonstrate strong React/Next.js
judgment.

Use:

### Server Components

Prefer for: - Read-heavy dashboard sections. - Patient information
pages. - Public content. - Resources. - Server-side data composition
where interactivity is unnecessary.

### Client Components

Use only where necessary: - Interactive calendar. - Dictation
controls. - Rich forms. - Client-side transitions requiring local
state. - Interactive note editor.

### State ownership

Be deliberate about:

-   Local component state.
-   Form state.
-   URL state.
-   Server state.
-   Derived state.
-   Context.

Do not use global state merely because Redux/Context exists.

Context should be limited to truly cross-cutting client concerns.

### Other Next.js areas to demonstrate

-   App Router.
-   Nested layouts.
-   Loading boundaries.
-   Error boundaries.
-   Metadata.
-   SEO.
-   Caching strategy.
-   Explicit no-cache behavior for sensitive/current clinical data where
    appropriate.
-   Responsive design.
-   Accessibility.
-   Progressive enhancement where practical.
-   Optimistic UI only where semantically safe.
-   Server/client boundary reasoning.

------------------------------------------------------------------------

# 10. ASP.NET Core backend

## 10.1 Version

Plan around the current .NET LTS release (currently .NET 10 as of this
planning context), subject to verification at implementation time.

## 10.2 Purpose

ASP.NET Core is not being included as a gimmick.

It should be the system-of-record API responsible for:

-   Authorization.
-   Domain validation.
-   Patients.
-   Guardians.
-   Goals.
-   Appointments.
-   Clinical notes.
-   Dictations.
-   AI orchestration.
-   Audit events.
-   Persistence.
-   Record lifecycle.
-   Potential future superbill generation.

React should never be the authority for clinical authorization or record
integrity.

## 10.3 Backend structure

A reasonable starting structure:

``` text
src/
├── Practice.Api/
│   ├── Controllers/
│   ├── Middleware/
│   ├── Authentication/
│   └── Program.cs
│
├── Practice.Application/
│   ├── Patients/
│   ├── Scheduling/
│   ├── Documentation/
│   ├── Dictation/
│   └── Common/
│
├── Practice.Domain/
│   ├── Patients/
│   ├── Appointments/
│   ├── Goals/
│   ├── ClinicalNotes/
│   └── Audit/
│
└── Practice.Infrastructure/
    ├── Persistence/
    ├── AI/
    ├── Speech/
    └── ExternalServices/

tests/
├── Practice.Domain.Tests/
├── Practice.Application.Tests/
└── Practice.Api.Tests/
```

Do not force architectural ceremony where it adds no value, but preserve
clear domain/application/infrastructure boundaries.

## 10.4 .NET concepts this project should demonstrate

-   ASP.NET Core.
-   Dependency injection.
-   Middleware.
-   Authentication.
-   Authorization.
-   EF Core.
-   Migrations.
-   Async APIs.
-   DTOs.
-   Validation.
-   Domain modeling.
-   Provider abstractions/interfaces.
-   Error handling.
-   Structured logging.
-   Unit testing.
-   Integration/API testing.
-   Cancellation tokens where appropriate.
-   Configuration/secrets management.

The goal is to demonstrate real familiarity with .NET engineering
practices, not simply C# syntax.

------------------------------------------------------------------------

# 11. Persistence

## 11.1 Current preferred direction

Evaluate **Azure SQL Database + EF Core SQL Server provider** as the
default database.

Reasons:

-   Natural fit with .NET/EF Core.
-   Strongly relational application domain.
-   Managed service.
-   Current Azure free SQL offer may fit the initial scale.
-   Straightforward future paid upgrade path.
-   No application rewrite should be required when upgrading
    infrastructure.

## 11.2 Relational domain

Conceptual relationships:

``` text
Provider
  |
  +-- Appointments
  |
Patient
  |
  +-- Guardians
  +-- Goals
  +-- Appointments
  |      |
  |      +-- ClinicalNote
  |
  +-- ClinicalDocuments
```

Additional entities will emerge during domain modeling.

## 11.3 Do not store large audio directly in SQL

If audio must temporarily exist:

-   Use appropriate object/blob storage.
-   Minimize retention.
-   Define lifecycle/deletion policy.
-   Do not persist audio indefinitely merely because storage is
    available.

------------------------------------------------------------------------

# 12. Proposed hosting strategy

## 12.1 Core constraint

Target **near-zero fixed monthly infrastructure cost** at the initial
5-20 patient scale.

## 12.2 Container approach

Current preferred compute approach:

**Azure Container Apps**

Potential deployment:

``` text
Azure Container Apps Environment
│
├── Next.js container
│
└── ASP.NET Core container
        │
        ▼
Azure SQL Database
```

Benefits:

-   ASP.NET Core remains a conventional production .NET application.
-   Docker/container skills are demonstrated.
-   Scale-to-zero can reduce idle compute cost.
-   Frontend/backend remain independently deployable.
-   Easy future scaling.
-   Avoid paying for an always-on VM for a mostly idle application.

Cold starts are an accepted potential tradeoff at this scale, but must
be measured.

## 12.3 Infrastructure should be replaceable

Do not allow Azure-specific infrastructure decisions to infect the
domain model.

The application should be portable enough that hosting can change later
without rewriting business logic.

------------------------------------------------------------------------

# 13. Free-tier/capacity strategy

The system should monitor actual infrastructure usage rather than
patient count alone.

Potential monitored resources:

-   Database compute allocation.
-   Database storage.
-   Backup storage.
-   Container compute.
-   Request volume.
-   Object storage.
-   AI usage/cost.
-   Transcription minutes/cost.

## 13.1 Provider-facing capacity warning

Normally invisible.

At an appropriate threshold such as \~75%:

> Approaching free service limit. No action is required yet.

At a higher threshold such as \~90%:

> Upgrade may be required soon. Current usage is approaching the
> included service capacity.

Do not expose infrastructure jargon to the clinician.

## 13.2 Administrative alerts

Also configure infrastructure-level alerts for the
developer/administrator.

The application banner is not a substitute for cloud monitoring.

## 13.3 Avoid surprise bills

Where free services allow a choice between:

-   Pausing when free allocation is exhausted, or
-   Automatically billing,

prefer a safe initial configuration that prevents surprise charges,
provided the availability tradeoff is explicitly understood.

Warn well before any limit is reached.

------------------------------------------------------------------------

# 14. HIPAA/security posture

## 14.1 Goal

The project should be designed from inception to handle clinical
information responsibly and to support applicable HIPAA obligations.

Do **not** casually claim that a repository, framework, or cloud service
is inherently "HIPAA compliant."

Compliance depends on:

-   Practice status/obligations.
-   Application controls.
-   Infrastructure.
-   Vendors.
-   BAAs where applicable.
-   Policies.
-   Risk analysis.
-   Operational behavior.

Before real PHI enters production, the complete data flow and vendor
arrangements must be verified.

## 14.2 Security controls worth including immediately

These are considered baseline V1 requirements because their value is
high relative to implementation complexity:

-   MFA.
-   HTTPS/TLS everywhere.
-   Encryption at rest through appropriate managed services.
-   Server-side authorization.
-   Secure session/cookie configuration.
-   Appropriate inactivity/session expiration.
-   Secrets outside source control.
-   Request validation.
-   Rate limiting where appropriate.
-   Audit logging.
-   No PHI in ordinary application logs.
-   Managed backups.
-   Restore/recovery plan.
-   Signed-note lifecycle.
-   Minimal data collection.
-   Dependency/security update process.
-   Principle of least privilege.
-   Environment separation.
-   Synthetic data in development/test.
-   Security headers.
-   CSRF protection as appropriate to chosen auth architecture.
-   XSS-safe rendering practices.
-   SQL injection protection through parameterized ORM/database access.
-   Avoid exposing internal IDs/data unnecessarily.

## 14.3 Audit trail

Record meaningful security/clinical events.

Examples:

``` text
Provider authenticated
Patient record viewed
Patient record updated
Clinical note created
Clinical note approved
Clinical note amended
Dictation created
AI draft generated
Export generated
```

Do not log the entire clinical payload in the audit event.

## 14.4 Clinical record integrity

Approved notes should not be silently overwritten.

Model:

-   Draft.
-   Approved/signed.
-   Amendment/version history.

Retain appropriate metadata:

-   Created by.
-   Created timestamp.
-   Approved timestamp.
-   Amended timestamp.
-   Amendment reason.
-   Previous version/reference.

Exact legal retention behavior must be verified before production PHI
use.

## 14.5 Vendor review

Any vendor that may create, receive, maintain, or transmit ePHI must be
explicitly evaluated.

Potential vendors include:

-   Cloud hosting.
-   Database.
-   Object storage.
-   Authentication.
-   Logging/monitoring.
-   Email.
-   SMS.
-   Transcription.
-   LLM/AI.
-   Error monitoring.
-   Backups.

Do not assume a vendor is appropriate for PHI because it has strong
general security.

## 14.6 Risk analysis

Before real patient data is used, create a documented security risk
analysis covering at least:

-   Where ePHI exists.
-   Where it travels.
-   Who can access it.
-   Threats.
-   Vulnerabilities.
-   Existing controls.
-   Likelihood.
-   Impact.
-   Residual risk.
-   Mitigation.
-   Review cadence.

Example:

``` text
Risk:
Provider credentials stolen.

Likelihood:
Medium.

Impact:
High.

Controls:
- MFA
- rate limiting
- secure session handling
- login monitoring

Residual risk:
Low/Medium.

Additional mitigation:
Review authentication logs and revoke compromised sessions.
```

------------------------------------------------------------------------

# 15. Maryland-specific considerations

The provider practices in Maryland.

The planning/build phase must verify Maryland-specific requirements
before enabling production PHI.

Previously identified areas requiring attention include:

-   Confidentiality of medical records.
-   Medical-record retention requirements.
-   Special implications for records belonging to minors.
-   Practice/legal documentation requirements.

Do not rely solely on memory or this brief for final legal
interpretation. Re-verify authoritative Maryland and federal sources
during compliance planning.

The data model should assume clinical records may need long-term
retention and should therefore avoid destructive "delete everything"
semantics.

------------------------------------------------------------------------

# 16. Superbills

Superbills are useful but are **not on the Tuesday critical path**.

The architecture/data model should make future superbills easy.

Relevant future data may include:

-   Provider identity.
-   Credentials.
-   NPI.
-   Practice information.
-   Patient information.
-   Date of service.
-   Diagnosis code(s).
-   Procedure/service code.
-   Units.
-   Charge.
-   Amount paid.

Potential conceptual entity:

``` text
Encounter
  patientId
  providerId
  appointmentId
  dateOfService
  diagnosisCodes
  serviceCode
  units
  charge
  amountPaid
  clinicalNoteId
```

Important future research:

-   Current ASHA superbill guidance.
-   Current payer expectations.
-   CPT licensing requirements.
-   ICD-10-CM data sourcing.
-   Good Faith Estimate requirements for private-pay/self-pay patients.

V1 may simply preserve the data needed to add superbill generation in
V1.1.

------------------------------------------------------------------------

# 17. Payments

Integrated payment processing is explicitly **not required** initially.

The provider is comfortable receiving payment externally.

The system can optionally record:

``` text
Amount charged
Amount paid
Payment status
External payment method
Payment date
```

Avoid taking on PCI/card-processing complexity until there is a clear
user need.

------------------------------------------------------------------------

# 18. Performance strategy

The initial load is tiny, but implementation should still follow good
practices.

## Public site

Optimize for:

-   Fast first load.
-   Static/server rendering where appropriate.
-   Image optimization.
-   Minimal client JavaScript.
-   Good Core Web Vitals.
-   SEO.

## Clinical application

Optimize for:

-   Correctness first.
-   Efficient database queries.
-   Avoid N+1 query patterns.
-   Pagination where records can grow.
-   Do not fetch entire patient histories unnecessarily.
-   Appropriate indexes.
-   Avoid unnecessary client state duplication.
-   Avoid caching PHI in inappropriate shared/public caches.
-   Measure cold-start behavior from scale-to-zero hosting.

## AI

AI work should not block UI unnecessarily.

Consider:

``` text
Dictation submitted
      ↓
Backend accepts job
      ↓
Processing status displayed
      ↓
Transcription/extraction/generation
      ↓
Draft ready
```

Exact synchronous vs asynchronous processing strategy should be chosen
based on measured transcription latency and V1 complexity.

------------------------------------------------------------------------

# 19. Reliability strategy

The system must remain useful even when external AI is unavailable.

Required design principle:

> Core patient records, scheduling, and manual SOAP notes do not depend
> on AI availability.

Potential failure behavior:

### Transcription unavailable

-   Preserve audio temporarily if safely configured.
-   Allow retry.
-   Allow manual note entry.

### AI generation unavailable

-   Preserve transcript/structured data.
-   Allow retry.
-   Allow manual SOAP creation.

### Backend error

-   Clear user-safe error.
-   No silent data loss.
-   Correlation ID for diagnostics without exposing PHI.

### Network loss

-   At minimum, prevent accidental loss of unsaved note work.
-   Investigate draft persistence/offline behavior for mobile workflow.

### Database outage

-   Clear downtime state.
-   Recovery procedure.
-   Managed backup/restore.
-   No browser-only authoritative clinical data.

------------------------------------------------------------------------

# 20. Testing strategy

The project should have meaningful tests rather than tests written only
for coverage numbers.

## Frontend

Potential: - Vitest/Jest. - React Testing Library. - Playwright for
critical end-to-end flows.

Critical frontend flows: - Public navigation. - Consultation form. -
Login. - Patient creation/editing. - Appointment creation. - Manual SOAP
note. - Dictation workflow. - Draft review/approval. - Accessibility
checks.

## Backend

Use: - xUnit. - Unit tests for domain/application logic. - Integration
tests for API/database behavior. - Authorization tests. - Validation
tests. - Clinical-note lifecycle tests.

## AI evaluation

Separate deterministic application tests from model-quality evaluations.

Maintain synthetic examples for:

-   Transcription accuracy.
-   Extraction correctness.
-   Missing-field detection.
-   Hallucination detection.
-   SOAP generation fidelity.

Do not make the entire CI suite dependent on live paid model calls.

------------------------------------------------------------------------

# 21. Observability

Use structured logs, but aggressively avoid PHI.

Good:

``` text
ClinicalNoteGenerationFailed
noteId=941
correlationId=...
provider=OpenAI
```

Bad:

``` text
Generation failed for Maya Johnson, DOB ..., whose mother reported ...
```

Monitor:

-   API errors.
-   Authentication failures.
-   AI failures.
-   Database health.
-   Capacity/free-tier consumption.
-   Container restarts.
-   Latency.
-   Failed note processing.

Keep the initial observability stack simple and ensure any external
monitoring vendor is reviewed before PHI-bearing data can reach it.

------------------------------------------------------------------------

# 22. Development data policy

Until production compliance gates are completed:

> **All development, test, screenshots, interview demonstrations, and AI
> benchmarks use synthetic patient data only.**

Do not copy real patient information into:

-   Local databases.
-   GitHub issues.
-   Seed files.
-   Prompt fixtures.
-   Screenshots.
-   Logs.
-   OpenRouter.
-   Test recordings.

Create realistic fictional patients and sessions.

------------------------------------------------------------------------

# 23. Deployment and environments

At minimum distinguish:

``` text
Development
Production
```

A staging environment is desirable if free-tier constraints allow it,
but do not multiply paid infrastructure solely for formality.

Production secrets must never be used in local development.

Use infrastructure configuration/environment variables appropriately.

CI/CD should eventually:

``` text
Pull request
   ↓
Lint
   ↓
Type check
   ↓
Frontend tests
   ↓
.NET build
   ↓
.NET tests
   ↓
Security/dependency checks
   ↓
Build containers
   ↓
Deploy
```

Exact GitHub Actions/Azure deployment strategy should be designed during
planning.

------------------------------------------------------------------------

# 24. User experience direction

The desired experience is:

-   Simple.
-   Calm.
-   Mobile-first.
-   Low cognitive load.
-   iOS-like clarity.
-   Minimal unnecessary options.
-   Strong visual hierarchy.
-   Fast common workflows.

Avoid traditional EHR UI patterns that present dozens of tabs and dense
tables simply because that is what healthcare software often looks like.

For one provider, the product can be substantially more opinionated.

The most common tasks should require very few actions.

------------------------------------------------------------------------

# 25. Accessibility

Accessibility is both a product requirement and a major interview
showcase.

Apply WCAG-oriented practices:

-   Semantic HTML.
-   Correct heading hierarchy.
-   Proper labels.
-   Keyboard support.
-   Focus management.
-   Visible focus.
-   Screen reader semantics.
-   Sufficient color contrast.
-   Accessible errors.
-   Reduced-motion consideration.
-   Touch targets suitable for mobile.
-   Do not communicate clinical/status information through color alone.

The developer has prior professional experience with semantic HTML,
screen-reader testing, and color contrast; the implementation should
demonstrate that experience.

------------------------------------------------------------------------

# 26. Interview/engineering showcase goals

The technical interview is for a React/Next.js frontend role at
Sandstorm Design.

The application should make it easy to discuss:

## Frontend

-   Component architecture.
-   Next.js App Router.
-   Server vs Client Components.
-   State ownership.
-   Local state vs Context.
-   URL state.
-   Form state.
-   Server state.
-   Accessibility.
-   Responsive design.
-   SEO.
-   Performance.
-   Caching.
-   Error/loading states.
-   Testing.

## .NET

The interviewer specifically said .NET knowledge is a bonus.

The project should demonstrate that the developer deliberately learned
and applied:

-   C#.
-   ASP.NET Core.
-   EF Core.
-   Dependency injection.
-   Middleware.
-   Interfaces.
-   Service abstractions.
-   DTOs.
-   Validation.
-   Async programming.
-   Testing.
-   Containerization.

The .NET implementation must have real architectural value; it should
not look bolted on merely to mention C#.

## Product thinking

The project story should demonstrate:

-   User interviews.
-   Competitive research.
-   Scope reduction.
-   Identification of a real pain point.
-   Security/compliance tradeoffs.
-   Cost-aware architecture.
-   Human-in-the-loop AI.
-   Production deployment.

## AI

The developer has significant AI-assisted engineering experience and
should be able to discuss:

-   Requirements-first planning.
-   Structured outputs.
-   Provider benchmarking.
-   Evaluation suites.
-   Hallucination mitigation.
-   Human approval.
-   AI as a subsystem rather than source of truth.
-   Provider abstraction.
-   Quality gates.

------------------------------------------------------------------------

# 27. Expected interview demo path

Do not try to show every screen.

A strong short walkthrough could be:

``` text
1. Public landing page
2. Briefly show responsive/accessibility quality
3. Provider login
4. Dashboard/today's schedule
5. Open a fictional patient
6. Show active goals
7. Complete fictional session
8. Start post-session voice recap
9. Show transcription
10. Show structured facts / missing-info behavior
11. Show generated SOAP draft
12. Edit/approve
13. Briefly open code:
      - Next.js server/client boundary
      - ASP.NET endpoint/application service
      - transcription interface
      - EF Core model
      - one meaningful test
```

The demo should be a real production flow using synthetic data, not
mocked screenshots.

------------------------------------------------------------------------

# 28. Competitive-research context

Prior research found that building a full SLP EHR solely to save
subscription cost is not compelling because low-cost SLP-specific
products already exist.

Competitors previously considered include:

-   Fledge.
-   Callie.
-   SpeakEasy.
-   Vochella.
-   Sessions Health.
-   SLP Now.
-   SLP Toolkit.
-   SLPFlow.
-   SOAPBox AI.
-   SLP Scribe.

Important product lesson:

> Scheduling, patient records, SOAP notes, portals, billing, and basic
> AI notes increasingly exist as commodity features.

The strongest value discovered for this product is not "another EHR." It
is the specific post-session workflow for an in-home clinician who wants
to preserve attention during therapy and turn fresh recollection into
documentation immediately afterward.

This justifies keeping the surrounding practice-management functionality
intentionally small.

------------------------------------------------------------------------

# 29. Known decisions

Treat these as currently decided unless planning uncovers a serious
technical/legal issue.

1.  One coherent product containing public website + authenticated
    practice app.
2.  Next.js is the frontend.
3.  ASP.NET Core is the real backend/system-of-record API.
4.  .NET is included for legitimate architectural reasons, not interview
    decoration.
5.  Use a relational database.
6.  Azure SQL + EF Core is the current preferred database direction.
7.  Scale-to-zero/containerized compute is preferred to always-on paid
    compute.
8.  Azure Container Apps is the current preferred compute candidate.
9.  Fixed infrastructure cost target is approximately \$0/month at 5-20
    patients.
10. AI/transcription usage costs are acceptable if small.
11. Domain cost is acceptable.
12. Post-session voice dictation is the hero feature.
13. AI output must remain clinician-reviewed.
14. Development/test/demo data is synthetic.
15. Integrated payments are deferred.
16. Insurance claim processing is deferred.
17. Patient portal is deferred.
18. Multi-provider functionality is deferred.
19. Automated route optimization is deferred pending provider/compliance
    review.
20. Superbills should be architecturally supported but can be V1.1 if
    time is limited.
21. Security/compliance must influence architecture from the beginning.
22. Production PHI is not enabled through any unverified vendor/data
    path.
23. Capacity warnings should appear before free-tier limits are
    exhausted.
24. Infrastructure should have an easy paid upgrade path rather than
    requiring an application rewrite.

------------------------------------------------------------------------

# 30. Open decisions for the next planning phase

Claude should investigate/resolve these before producing the final
implementation plan.

## Architecture

-   Exact Next.js deployment model within Azure Container Apps.
-   Whether public/static pages should share the same container as
    authenticated Next.js or be separately hosted.
-   BFF pattern vs direct browser-to-ASP.NET API.
-   Exact authentication flow.
-   Exact Entra configuration for a one-provider application.
-   Cookie/session strategy.
-   CSRF strategy.
-   CORS strategy.
-   Whether Container Apps scale-to-zero cold starts are acceptable in
    practice.
-   Minimum replicas, if any, for production.

## Database

-   Verify current Azure SQL free-offer terms and suitability for
    intended production use.
-   Determine free-tier pause/limit behavior.
-   Exact EF Core model.
-   Indexing strategy.
-   Migration strategy.
-   Backup/export strategy.
-   Restore testing.
-   Data retention model.

## Compliance/security

-   Verify current HIPAA applicability for the specific private-pay
    practice workflow.
-   Verify Maryland requirements.
-   Verify all BAAs/service eligibility.
-   Create data-flow diagram.
-   Create threat model.
-   Create initial risk analysis.
-   Determine audit requirements.
-   Determine record retention/amendment requirements.
-   Determine minimum necessary data.
-   Decide whether any legal/compliance review is required before first
    real patient.

## AI/transcription

-   Benchmark Azure Speech vs direct OpenAI vs other viable providers.
-   Verify exact BAA/eligible-service status before PHI.
-   Define audio retention/deletion policy.
-   Define transcript retention policy.
-   Design structured extraction schema.
-   Design prompt/version management.
-   Design hallucination/evaluation suite.
-   Decide synchronous vs background processing.
-   Define failure/retry behavior.

## Frontend

-   Design system.
-   Mobile information architecture.
-   Calendar implementation.
-   Dictation UX.
-   SOAP editor.
-   Public-site content model.
-   Resource/handout content strategy.
-   Accessibility test strategy.
-   State-management decisions.
-   Caching strategy.

## Deployment

-   Dockerfiles.
-   Container registry.
-   GitHub Actions.
-   Secrets.
-   Environment separation.
-   Monitoring.
-   Capacity alerts.
-   Custom domain.
-   TLS.
-   Cost safeguards.

------------------------------------------------------------------------

# 31. Suggested planning artifacts to create next

Before heavy coding, produce:

1.  `PRD.md`
2.  `ARCHITECTURE.md`
3.  `DATA_MODEL.md`
4.  `SECURITY.md`
5.  `THREAT_MODEL.md`
6.  `HIPAA_DATA_FLOW.md`
7.  `AI_PIPELINE.md`
8.  `API_SPEC.md`
9.  `UX_FLOWS.md`
10. `TEST_STRATEGY.md`
11. `DEPLOYMENT.md`
12. `IMPLEMENTATION_PLAN.md`

Keep these concise enough to remain useful to coding agents. Do not
create documentation merely for volume.

The implementation plan should decompose the project into independently
verifiable vertical slices rather than "build frontend, then backend."

Example:

``` text
Slice 1:
Public website + deployment

Slice 2:
Provider authentication

Slice 3:
Patient CRUD end-to-end

Slice 4:
Scheduling end-to-end

Slice 5:
Goals + manual SOAP notes

Slice 6:
Audio capture + transcription

Slice 7:
Structured extraction + validation

Slice 8:
SOAP generation + approval

Slice 9:
Audit/capacity/security hardening

Slice 10:
Production-readiness verification
```

Each slice should have acceptance criteria and tests.

------------------------------------------------------------------------

# 32. Non-goals

Do not allow scope creep into:

-   A generic SaaS EHR platform.
-   Supporting every healthcare profession.
-   Enterprise healthcare integrations.
-   Multi-tenant SaaS architecture.
-   Complex billing.
-   Insurance adjudication.
-   Full accounting.
-   Patient social network.
-   Therapy-material marketplace.
-   AI diagnosis.
-   AI treatment-plan decision making.
-   Autonomous clinical decision making.
-   Recording full therapy sessions by default.
-   Building infrastructure solely to look technically impressive.

The sophistication should come from **quality of execution and
engineering decisions**, not unnecessary complexity.

------------------------------------------------------------------------

# 33. Definition of success

The V1 is successful when:

### For the provider

She can realistically:

1.  Log in securely.
2.  See her day.
3.  Create/manage a patient.
4.  Store basic patient/guardian/goal information.
5.  Schedule an in-home session/evaluation.
6.  View the day's visits.
7.  Complete a session.
8.  Dictate a recap.
9.  Receive a structured SOAP draft.
10. Review/edit/approve the note.
11. Return later and reliably retrieve the clinical record.

### For the public

A parent can:

1.  Understand who the provider serves.
2.  Understand the services offered.
3.  Learn about the provider.
4.  Access free resources.
5.  Request a free consultation.
6.  Use the site comfortably on mobile and with assistive technology.

### For engineering quality

The system:

1.  Is deployed.
2.  Uses Next.js appropriately rather than as generic React.
3.  Uses ASP.NET Core as a legitimate backend.
4.  Uses a persistent relational database.
5.  Has real authentication/authorization.
6.  Has meaningful automated tests.
7.  Has clear error handling.
8.  Has appropriate audit/security controls.
9.  Has a documented PHI/compliance boundary.
10. Has no unverified production PHI flow.
11. Can operate near \$0 fixed infrastructure cost at the initial scale.
12. Has an obvious paid upgrade path.
13. Is understandable enough that the developer can walk senior
    engineers through both the product and code confidently.

------------------------------------------------------------------------

# 34. Guiding decision rule

For every proposed feature or technology, ask:

> Does this materially improve the provider's real workflow, the
> application's production quality, or the engineering quality relevant
> to the role?

If not, defer it.

For every shortcut involving clinical data, ask:

> Would we be comfortable putting an actual child's medical information
> through this exact data path?

If not, that path must remain synthetic-only until corrected.

For every infrastructure expense, ask:

> At 5-20 patients, does paying for this materially improve security,
> reliability, or usability compared with an appropriate
> free/consumption option?

If not, do not pay for it yet.

------------------------------------------------------------------------

# 35. Final planning brief

Build a **production-quality, narrowly scoped pediatric SLP practice
platform** with:

-   A polished Next.js public practice website.
-   A secure Next.js provider application.
-   ASP.NET Core as the system-of-record API.
-   EF Core + relational persistence, currently favoring Azure SQL.
-   Containerized, scale-to-zero Azure deployment where appropriate.
-   Approximately \$0 fixed infrastructure cost at the initial 5-20
    patient scale.
-   Strong accessibility and responsive design.
-   Patient records, goals, scheduling, daily visit planning, and SOAP
    documentation.
-   A standout post-session voice-dictation workflow.
-   Structured AI extraction before note generation.
-   Human approval of every AI-generated clinical note.
-   Security, auditing, backups, and compliance boundaries designed from
    the beginning.
-   Synthetic-only data until every production PHI path is verified.
-   A deliberate, defensible use of modern .NET and Next.js that can
    withstand a senior-engineer code walkthrough.

The project should feel small, complete, opinionated, and useful---not
like a miniature version of Epic and not like a hackathon prototype.
