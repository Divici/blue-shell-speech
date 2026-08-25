# Data Model

EF Core → Azure SQL Database. This is the system of record.

Derived from `presearch.md` §5.3–5.6, §6, §7.4, §11.2, §14.3, §14.4, §16.

---

## Rules that bind every table

1. **`ProviderId` on every domain row from day one**, even at one provider. Retrofitting a
   tenancy discriminator onto a live clinical database is the kind of migration that goes
   wrong quietly. Row-level filtering is a global EF Core query filter, never a `WHERE` clause
   a developer has to remember.
2. **Patient-facing identifiers are opaque `GUID`s** (`uniqueidentifier`, `NEWSEQUENTIALID()`
   default). Sequential integers leak patient count and permit enumeration.
   Internal clustered keys may still be `bigint` identity — see "Key strategy" below.
3. **Store UTC.** `datetime2(3)`, always UTC, rendered `America/New_York`. Never `datetime`.
4. **Soft-delete is not deletion.** Clinical rows are never hard-deleted; audio is.
5. **No PHI in any column that feeds a log or a metric.**
6. Every table carries `CreatedAtUtc`, `CreatedBy`, `RowVersion` (`rowversion` for optimistic
   concurrency). Clinical tables add the signature/amendment columns below.

### Key strategy

Each table has both:

- `Id` — `bigint identity`, clustered primary key. Narrow, sequential, good index behaviour.
- `PublicId` — `uniqueidentifier`, unique non-clustered. The **only** identifier that appears
  in a URL, an API payload, or an email.

**Why both:** a `GUID` clustered key on SQL Server fragments the index badly on insert.
`NEWSEQUENTIALID()` fixes fragmentation but makes GUIDs partly guessable — which defeats the
reason we wanted GUIDs. Splitting the two roles gets both properties. The cost is one extra
lookup on public-id resolution and the discipline never to leak `Id`.

---

## Entities

### Provider

The clinician. One row today; the schema does not assume that.

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | `bigint` / `guid` | |
| `IdentityUserId` | `nvarchar(450)` | FK → ASP.NET Core Identity `AspNetUsers` |
| `DisplayName` | `nvarchar(200)` | |
| `Credentials` | `nvarchar(100)` | e.g. "M.S., CCC-SLP" |
| `NpiNumber` | `nvarchar(10)` | Nullable. Needed for superbills (§16) |
| `LicenseNumber` / `LicenseState` | `nvarchar(50)` / `char(2)` | |
| `IsActive` | `bit` | |

Identity tables (`AspNetUsers`, roles, TOTP secrets, recovery codes) are ASP.NET Core Identity's
own schema, unmodified. `Provider` is the domain projection alongside it. Keeping them separate
means an Identity upgrade never touches clinical tables.

### Patient

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | `bigint` / `guid` | |
| `ProviderId` | `bigint` | |
| `FirstName` / `LastName` | `nvarchar(100)` | **PHI** |
| `DateOfBirth` | `date` | **PHI.** `date`, not `datetime2` — a birthdate has no timezone |
| `Status` | `tinyint` | `Active` / `Inactive` / `Discharged` |
| `ClinicalSummary` | `nvarchar(max)` | **PHI.** Free text — diagnosis context, precautions |
| `DischargedAtUtc` | `datetime2(3)` | Nullable |

`DateOfBirth` is stored, not age. Age is computed at render. Storing age produces a row that
silently becomes wrong.

**Deliberately absent:** SSN, insurance member ID, race/ethnicity. Private-pay practice, no
claims submission. Do not add a field because an EHR would have it — §5.3 says store only what
the workflow requires, and every PHI column is a column that has to be protected, audited, and
justified in the risk analysis.

### Guardian

Separate table, not columns on `Patient`. Children have two parents at different addresses more
often than not, and one of them is frequently the one who actually answers the phone.

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | | |
| `ProviderId` | `bigint` | |
| `PatientId` | `bigint` | |
| `FirstName` / `LastName` | `nvarchar(100)` | **PHI** |
| `Relationship` | `nvarchar(50)` | Mother / Father / Grandparent / Legal guardian |
| `Phone` / `Email` | `nvarchar(50)` / `nvarchar(256)` | **PHI** |
| `IsPrimaryContact` | `bit` | |
| `HasLegalAuthority` | `bit` | Gates who may receive records |

`HasLegalAuthority` is separate from `IsPrimaryContact` on purpose. The adult who brings the
child is not always the adult entitled to the record. Custody disputes are not an edge case in
paediatrics, and a record released to the wrong parent is a breach.

**It is never defaulted and never inferred.** The column is a `bit` and has no room for
"nobody said", so the distinction lives on the way in: the form is a required radio group with
nothing preselected, `AddGuardianRequest.HasLegalAuthority` is `bool?` and a null is a 400,
and `Patient.UpdateGuardian` reads it from its own argument. Every stored `false` is therefore
a `false` somebody chose, not a form's silence. See D073.

**A patient with guardians but no authorised one is a real state**, not an error — a family
whose custody paperwork has not arrived. The page says so (`recordsReleaseState`) rather than
picking somebody, and states the answer on every guardian in both directions.

Editing goes through `PUT /patients/{id}/guardians/{guardianId}`, on the aggregate rather than
on the entity, because promoting one guardian demotes another. Guardian writes are audited as
`PatientUpdated` with fixed-vocabulary metadata carrying opaque ids only.

### Address

Own table. A patient can have a session address that differs from the billing address, and
addresses change.

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | | |
| `ProviderId`, `PatientId` | `bigint` | |
| `Line1` / `Line2` / `City` / `State` / `PostalCode` | | **PHI** |
| `AddressType` | `tinyint` | `Session` / `Billing` |
| `Notes` | `nvarchar(500)` | Gate code, parking, "dog in yard" |
| `EffectiveFromUtc` / `EffectiveToUtc` | `datetime2(3)` | Nullable end = current |

**Never sent to a mapping provider** without an evaluated data flow (§5.6).

**A correction is not a move, and they are two operations** (D074). `POST .../addresses`
records a move: `Patient.AddAddress` closes the current address of that type as of the new
start date and keeps the row, because a note describing a visit last spring refers to where
the family lived then. `PUT .../addresses/{addressId}` fixes a typo: `Patient.CorrectAddress`
changes one row in place, and `CorrectAddressRequest` carries **no `AddressType` and no
dates** — that absence is the guard, so a correction cannot supersede anything or turn a
session address into a second current billing one. Correcting a superseded address is allowed
and leaves it superseded.

`AddressDto` exposes `EffectiveFrom` and `EffectiveTo`. `IsCurrent` alone cannot answer which
address a past visit happened at, and the page has to show that for the versioning to mean
anything.

### Goal

Drives dictation. The extraction step needs to know what is being targeted (§5.4).

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | | |
| `ProviderId`, `PatientId` | `bigint` | |
| `GoalText` | `nvarchar(1000)` | **PHI** |
| `Domain` | `tinyint` | `Articulation`, `ReceptiveLanguage`, `ExpressiveLanguage`, `SocialCommunication`, `Fluency`, `Feeding`, **`AAC`** |
| `TargetCriteria` | `nvarchar(500)` | "80% accuracy over 3 sessions" — free text |
| `CueLevelExpected` | `tinyint` | `Independent`, `Verbal`, `Visual`, `Gestural`, `Tactile`, `HandOverHand` |
| `Status` | `tinyint` | `Active` / `Met` / `Discontinued` / `OnHold` |
| `StartDate` / `EndDate` | `date` | |
| `AacModality` | `tinyint` | Nullable. `HighTech`, `LowTech`, `PECS`, `Sign`, `Hybrid` |
| `AacDeviceNotes` | `nvarchar(500)` | Nullable |

`TargetCriteria` stays free text. §5.4 explicitly warns against a universal clinical goal
language — parsing "80% accuracy over 3 consecutive sessions with minimal verbal cues" into a
structured rule engine is a project in itself, and it would be wrong for the next goal.

The two `Aac*` columns are the whole of "AAC-specific fields, but don't overmodel." Nullable,
only meaningful when `Domain = AAC`.

### Appointment

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | | |
| `ProviderId`, `PatientId` | `bigint` | |
| `AddressId` | `bigint` | Nullable — where the session happens |
| `AppointmentType` | `tinyint` | `Therapy`, **`Evaluation`**, `Consultation`, `Reassessment` |
| `StartUtc` | `datetime2(3)` | |
| `DurationMinutes` | `smallint` | Duration, not `EndUtc` — DST arithmetic bites |
| `Status` | `tinyint` | `Scheduled` / `Completed` / `Cancelled` / `NoShow` |
| `TravelBlockMinutes` | `smallint` | Nullable (§5.6) |
| `Mileage` | `decimal(6,1)` | Nullable |
| `Notes` | `nvarchar(1000)` | **PHI** |

`Evaluation` ships as an appointment type now. Formal evaluation-report authoring is sequenced
later — the type does not depend on the report.

### ClinicalNote

The immutable core. **Signed notes are never updated in place.**

| Column | Type | Notes |
|---|---|---|
| `Id` / `PublicId` | | |
| `ProviderId`, `PatientId`, `AppointmentId` | `bigint` | |
| `VersionNumber` | `int` | 1, 2, 3… |
| `SupersedesNoteId` | `bigint` | Nullable, self-FK |
| `IsCurrent` | `bit` | Exactly one true per `AppointmentId` — filtered unique index |
| `Status` | `tinyint` | `Draft` / `Signed` / `Amended` |
| `Subjective` / `Objective` / `Assessment` / `Plan` | `nvarchar(max)` | **PHI** |
| `Origin` | `tinyint` | `Manual` / `DictationAssisted` |
| `CreatedAtUtc` / `CreatedBy` | | |
| `SignedAtUtc` / `SignedBy` | | Null while draft |
| `AmendmentReason` | `nvarchar(500)` | **Required** when `SupersedesNoteId` is set |
| `ContentHash` | `binary(32)` | SHA-256 of the four SOAP fields at signature |

**How amendment works:** signing sets `SignedAtUtc` and computes `ContentHash`. An amendment
**inserts a new row** with `VersionNumber + 1`, `SupersedesNoteId` pointing at the old row, and
a non-null `AmendmentReason`. The old row keeps `IsCurrent = 0` forever. Nothing is overwritten.

**Enforced in the database, not just the application:**

- Filtered unique index on `(AppointmentId) WHERE IsCurrent = 1`.
- `CHECK` constraint: `SupersedesNoteId IS NULL OR AmendmentReason IS NOT NULL`.
- An `UPDATE` trigger rejecting changes to SOAP fields where `Status <> 'Draft'`.
- A `DELETE` trigger rejecting the removal of anything but an **empty draft**.

The triggers are the belt to the application's braces. Application-layer immutability survives
exactly until someone writes a migration script or opens SSMS at 11pm. `ContentHash` makes any
after-the-fact tampering detectable rather than merely prohibited.

**The one deletable row.** A draft with `Status = Draft` and nothing in any of the four SOAP
sections may be deleted — `ClinicalNote.CanBeDiscarded`, `DELETE /notes/{publicId}`, audited as
`NoteDiscarded`. That case exists because a note is created the moment the clinician taps
"start note" on the schedule: an empty draft attests to nothing, cannot be signed, and cannot be
replaced while it exists, so keeping it would leave a permanent "Draft" badge on a chart
clearable only by writing content onto it. See D064.

A note may only be **started** for a visit that has begun and was not cancelled or marked a
no-show (`Appointment.DocumentationBlockedReason`).

### DictationSession & DictationTake

Split because **one session holds multiple takes** (D010).

**DictationSession** — `Id`/`PublicId`, `ProviderId`, `PatientId`, `AppointmentId`,
`Status` (`Recording`/`Uploading`/`Transcribing`/`Extracting`/`Generating`/`ReadyForReview`/`Failed`),
`FailureReason`, `StartedAtUtc`, `CompletedAtUtc`, `TranscriptCombined` (`nvarchar(max)`, **PHI**),
`ResultingNoteId`.

**DictationTake** — `Id`/`PublicId`, `DictationSessionId`, `SequenceNumber`,
`DurationSeconds` (`smallint`, **≤ 300** — `CHECK` constraint, the 5-minute cap in the schema),
`BlobUri`, `BlobDeletedAtUtc`, `TranscriptText` (**PHI**), `TranscriptConfidence` (`decimal(4,3)`),
`AudioFormat`, `SizeBytes`.

Status is an explicit enum, not booleans, because the UI polls it (§7.2) and the user is
standing in a driveway wanting to know what is happening. `FailureReason` is user-facing text —
`Transcription unavailable, your audio is saved` (§19), never a stack trace.

**Audio lives in Blob Storage, never in SQL** (§11.3). `BlobUri` is a reference.
Retention: deleted when the note is signed, hard 30-day cap regardless. `BlobDeletedAtUtc`
records that deletion happened — the audit trail must survive the audio.

### ExtractedObservation

Structured output of §7.4, before generation. **This table is why the pipeline is auditable.**

`Id`/`PublicId`, `DictationSessionId`, `GoalId` (nullable), `ObservationType`, `RawText`,
`TrialsAttempted` / `TrialsCorrect` (`smallint`, **nullable**),
`AccuracyPercent` (`decimal(5,2)`, **nullable**), `CueLevel` (nullable),
`IsMissing` (`bit`), `MissingFieldName`, `SourceTranscriptOffset`,
`ConfirmedByProvider` (`bit`), `ConfirmedAtUtc`.

**Every quantitative column is nullable, and that is the point.** A non-nullable
`TrialsAttempted` with a `0` default is exactly how a model's silence becomes a clinical claim
of zero trials. Null means *not stated*. `IsMissing` + `MissingFieldName` drive the review chips
Michelle fills by typing or speaking.

`SourceTranscriptOffset` lets the review UI show the sentence a number came from. A clinician
signing a note should be able to see the evidence for every figure in it — that is the whole of
§7.6 human-in-the-loop, made concrete.

### Encounter

Ships now, per the scope ledger, even though superbill PDF generation is sequenced later.

`Id`/`PublicId`, `ProviderId`, `PatientId`, `AppointmentId`, `ServiceDate`,
`CptCode`, `IcdCodes` (`nvarchar(200)`), `Units`, `ChargeAmount` (`decimal(10,2)`),
`PaymentStatus`, `PaidAtUtc`, `SuperbillGeneratedAtUtc`.

Adding a billing table to a live clinical database later means backfilling every historical
appointment. Shipping the empty table now costs one migration.

### AuditEvent

§14.3. Append-only. **No `UPDATE` or `DELETE` grant to the application's SQL principal.**

`Id`, `OccurredAtUtc`, `ProviderId`, `ActorUserId`, `EventType`
(`PatientViewed`, `NoteSigned`, `NoteAmended`, `AudioDeleted`, `LoginSucceeded`,
`LoginFailed`, `MfaChallenged`, `ExportGenerated`), `EntityType`, `EntityPublicId`,
`CorrelationId`, `IpAddress`, `UserAgent`, `Outcome`, `Metadata` (`nvarchar(max)`).

**`Metadata` must never contain clinical content.** It holds IDs and reasons. The audit log is
the one table most likely to be exported, shipped to a SIEM, or read by a third party during an
audit — PHI in it multiplies the blast radius of every one of those.

`PatientViewed` is logged. Under HIPAA, read access is an auditable event; most homegrown
systems only log writes and discover the gap during an investigation.

### ResourceDocument

Ships empty. The Resources tab is hidden until a row exists — hiding is a content condition,
not a code change (§4.1).

`Id`/`PublicId`, `Title`, `Description`, `BlobUri`, `FileSizeBytes`, `ContentType`,
`IsPublished`, `PublishedAtUtc`, `SortOrder`.

**Public, non-PHI, no `ProviderId` filter needed** — parent handouts, served from a different
container than clinical audio, with different access rules. Do not let it drift into a general
file store; patient document upload is a separate entity when it arrives.

### ConsultationRequest

Public intake form (§4.1). **Not PHI until Michelle acts on it** — it is a prospect enquiry —
but it is treated as PHI-adjacent and stored under the same controls.

`Id`/`PublicId`, `ProviderId`, `SubmittedAtUtc`, `ParentName`, `Email`, `Phone`,
`ChildFirstName`, `ChildAgeMonths`, `Concerns` (`nvarchar(2000)`), `PreferredContactMethod`,
`Status` (`New`/`Contacted`/`Converted`/`Declined`), `ConvertedPatientId`, `SourceIpHash`.

Column widths are the same numbers the aggregate enforces, and an over-long value is **refused,
never truncated**: a column with more room than the aggregate allows is a second, quieter limit
that only a raw `INSERT` can reach.

**`ProviderId` on a row nobody was signed in to create.** The form is public, so there is no
session to take one from — the API resolves the **sole active provider** and refuses with 503
when that answer is ambiguous, rather than picking one. Reasoning and cost in `DECISIONS.md`
D078. Filtered like every other tenant table; a query filter constrains reads, never inserts,
so the anonymous POST is unaffected and the enquiry is only visible through a session.

`SourceIpHash` is hashed, not raw — spam correlation without retaining a visitor identifier.
`char(64)`, because a SHA-256 hex digest is exactly that and the column should say so. The
**BFF** computes it, being the only tier that can see a client address, and it is the *same*
value the consultation rate limiter keys on — one derivation, two uses. The aggregate refuses
anything not shaped like a digest, so an address cannot be passed straight through. The raw
address is not written to the audit row either: `AuditEvent.IpAddress` is deliberately left
null on `ConsultationRequestReceived`.

The notification email carries **no content**: *"New consultation request, sign in to view."*
Email is not a channel we control, and a child's name plus a list of developmental concerns in
a plaintext inbox is a disclosure. This is enforced by the SEAM rather than by care at the call
site: `IConsultationNotifier.NotifyAsync` takes an opaque `Guid` and has no parameter through
which content could travel. Transport is not built — the practice has no mailbox yet (a Blocked
item) — so the implementation writes the same contentless pair to a log, and the enquiry is
durable in the table regardless.

---

## Designed in, not built yet

Per the scope ledger, these have their seams cut now:

| Deferred feature | Seam already present |
|---|---|
| Document / file upload | `PatientDocument` mirrors `ResourceDocument`; blob container split already exists |
| Evaluation reports | `AppointmentType.Evaluation` ships; a report is a `ClinicalNote` subtype or sibling |
| Superbill PDF | `Encounter` ships complete |
| Live Azure Cost API | Threshold logic runs against internal counters; the source is swappable |

---

## Open, needs a decision before the first migration

1. ~~Encryption at rest beyond TDE.~~ **Resolved 2026-08-23 — TDE only, no Always Encrypted.**
   It does not defend against a compromised application (the app holds the key), the threats it
   does stop are ones a solo practice already occupies, and it breaks `LIKE` patient search.
   Full reasoning in `DECISIONS.md` D012.
2. **Retention floor for Maryland minors' records.** Drives whether `Patient` can ever be purged
   at all. §15 flags it; it needs an authoritative source, not an assumption.
3. **Whether `TranscriptCombined` is retained after signing.** It is PHI, it is derived, and the
   note supersedes it. Deleting it with the audio is the defensible position; keeping it helps
   eval work. Synthetic corpora should cover the eval need instead.
