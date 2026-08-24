# AI Pipeline

From `presearch.md` §7.3–7.7, §8.1–8.3, §19.

The governing constraint, from `CLAUDE.md`:

> **Never fabricate a percentage, cue level, trial count, or caregiver report. Mark it missing.**

Everything below exists to make that enforceable rather than aspirational. A fluent model asked
to write a clinical note will produce plausible numbers, because plausible numbers are what
clinical notes contain. The architecture has to make that structurally impossible, not prompt
against it.

---

## Stages

```text
audio (≤300s/take)
   ↓  transcode 16 kHz mono PCM        iOS emits mp4/AAC, Speech wants PCM
   ↓
STT                                    Azure Speech · BAA-covered · sees PHI
   ↓  transcript (PHI)
   ↓
DE-IDENTIFY                            local, deterministic, reversible
   ↓  tokenized transcript
   ↓
EXTRACT                                Azure OpenAI text · JSON schema-constrained
   ↓  structured observations
   ↓
VALIDATE                               schema + domain rules · deterministic, no model
   ↓
MISSING-INFO ANALYSIS                  deterministic diff against active goals
   ↓
GENERATE SOAP                          Azure OpenAI text · from STRUCTURED DATA, not transcript
   ↓
RE-IDENTIFY                            local, token → name
   ↓
DRAFT NOTE → clinician review → sign
```

**Two independent model calls, never one.** The seam between them is where validation happens,
and a pipeline with no seam has nowhere to put a check.

**Generation reads the validated structure, not the transcript.** If a number is not in the
structured data, the generator has no way to emit it — it never saw the raw text. That is the
hallucination control. A prompt saying "do not invent numbers" is a request; withholding the
source material is a guarantee.

---

## Stage 1 — De-identification

Runs **before** any text reaches Azure OpenAI. Local, in-process, no network call.

| Detected | Replaced with | Source of truth |
|---|---|---|
| Patient first/last name | `[PATIENT_1]` | `Patient` row for this session |
| Guardian names | `[GUARDIAN_1]`, `[GUARDIAN_2]` | `Guardian` rows |
| Sibling / other names | `[PERSON_n]` | NER pass |
| Dates of birth, explicit dates | `[DATE_n]` | Pattern + NER |
| Addresses, place names | `[LOCATION_n]` | NER |
| Phone, email | `[CONTACT_n]` | Pattern |

The token map lives **in memory for the duration of the job** and is never persisted, logged, or
sent anywhere. Re-identification is a dictionary lookup after generation.

**Why roster-driven first, NER second:** we know exactly who is in this session — the patient and
their guardians are rows in the database. Matching against a known roster is exact and cannot
miss the most important name in the transcript. NER catches what the roster cannot know about:
a sibling, a teacher, a daycare.

**What this does NOT do, stated plainly:** it is not anonymization and does not make the payload
non-PHI. Clinical content is inherently identifying — a rare diagnosis plus an age plus a county
can identify a child with no name attached. HIPAA Safe Harbor requires eighteen identifier
categories removed, and we are not claiming to meet it.

What de-identification actually buys: **if** the Modified Abuse Monitoring application is refused
and Microsoft retains prompts for 30 days with possible human review, a reviewer sees
`[PATIENT_1] worked on two-word combinations` rather than a named child. That reduces harm. It
does not eliminate exposure. It is a mitigating control recorded in the risk analysis, and it is
the fallback named in `PRELAUNCH_BLOCKERS.md` items 1 and 2 — not a reason to relax either.

**Failure mode we accept:** a name the roster does not contain and NER misses reaches the model.
Rate unknown until measured against the synthetic corpus. **Measuring it is a deliverable** —
recall on names is an eval metric, not a hope.

---

## Stage 2 — Extraction

**Structured outputs with a strict JSON schema**, not "return JSON" in a prompt. The model is
constrained by the API to emit conforming output; a malformed response is not a parsing problem
we handle, it is a case that cannot occur.

```jsonc
{
  "goalsAddressed": [
    {
      "goalId": "goal_123",
      "independentAccuracy": 0.60,      // null when not stated
      "accuracyWithCueing": 0.80,       // null when not stated
      "cueLevel": "minimal_verbal",     // null when not stated
      "trialsAttempted": null,
      "trialsCorrect": null,
      "sourceQuote": "she was independently requesting around sixty percent of the time",
      "sourceOffset": 142
    }
  ],
  "goalsNotAddressed": ["goal_456"],
  "caregiverReports": [
    { "text": "has begun using 'want juice' at home",
      "reportedBy": "[GUARDIAN_1]", "sourceOffset": 268 }
  ],
  "nextSessionPlan": "Increase requesting opportunities during play.",
  "missingInformation": [
    { "goalId": "goal_456", "field": "accuracy", "reason": "goal not mentioned in dictation" }
  ]
}
```

**Every quantitative field is nullable, and the schema forbids a default.** This mirrors
`DATA_MODEL.md`. A non-nullable `trialsAttempted` with a `0` default is exactly how silence
becomes a clinical claim of zero trials.

**`sourceQuote` and `sourceOffset` are required on every extracted claim.** A number without a
span in the transcript is rejected by validation — the model cannot assert something it cannot
point at. This also drives the review UI: Michelle taps a figure and sees the sentence it came
from, which is §7.6 human-in-the-loop made concrete rather than declared.

**Goals are provided as context**, resolved from `Goal` rows for this patient with
`Status = Active`. The model classifies which were addressed; it never invents a goal ID. An ID
outside the supplied set fails validation.

---

## Stage 3 — Validation

**Deterministic C#. No model involved.** This is the gate, and a gate implemented by a model is
not a gate.

| Check | On failure |
|---|---|
| Schema conformance | Reject, retry once, then fail the job |
| `goalId` ∈ supplied active goals | Drop the observation, log the correlation ID |
| Accuracy ∈ [0, 1] | Reject the field, mark missing |
| `trialsCorrect ≤ trialsAttempted` | Reject both, mark missing |
| `cueLevel` ∈ enum | Reject the field, mark missing |
| `sourceOffset` resolves to real transcript text | **Reject the claim entirely** |
| `sourceQuote` substring-matches at the offset (fuzzy) | Reject the claim entirely |
| Every active goal appears in addressed ∪ notAddressed ∪ missing | Add to missing |

The last row is the completeness invariant: **a goal cannot silently vanish.** If Michelle
didn't mention it, that is a `goalsNotAddressed` entry or a missing-info chip — never absence.

Rejection **always** degrades to "missing," never to a substituted value. Every rejection is
counted and exposed in the eval suite; a rising rejection rate is the signal that a prompt or a
model version has regressed.

---

## Stage 4 — Missing-information analysis

**Deterministic. No model.** A diff between active goals and validated observations, plus
per-goal required-field rules driven by `Goal.TargetCriteria` and `CueLevelExpected`.

Output drives the review chips. Michelle fills them by typing **or** by tapping to speak —
never by accepting a suggestion, because a suggested clinical value is a fabricated clinical
value wearing a UI affordance.

---

## Stage 5 — Generation

Input: **validated structured data only.** The transcript is not passed. Neither are rejected
observations.

Output: four SOAP fields as prose, plus explicit markers where information is missing.

**Missing information renders as a visible placeholder in the draft** — not smoothed over with
a hedge. "Accuracy not documented" is honest and forces a decision at signing. A model asked to
write around a gap will produce "demonstrated good progress," which reads like an observation
and is not one.

Prose only. **No number appears in the generated text that is not in the structured input** —
asserted by a post-generation check that extracts numerics from the output and confirms each has
a source in the validated data. A numeric with no provenance fails the job rather than reaching
a clinician.

---

## Provider abstraction (§8.1)

```csharp
public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        AudioInput audio, TranscriptionContext context, CancellationToken ct);
}

public interface IClinicalExtractionService { /* ... */ }
public interface IClinicalNoteGenerationService { /* ... */ }
```

Interfaces in `Practice.Application`, implementations in `Practice.Infrastructure`.

**The PHI prohibition is enforced at this seam, in code.** Every implementation declares whether
it is BAA-covered:

```csharp
public interface IAiProvider { bool IsPhiEligible { get; } }
```

The OpenRouter implementation returns `false` and **throws** if invoked with data not flagged
synthetic. A comment saying "synthetic only" is a note; a guard that throws is a control. This
is what stops $100 of benchmarking credits from becoming a breach on a tired evening.

Registered by configuration, not compile-time reference — blocker #1 may force a different
deployment or vendor, and that must be a config change.

---

## Benchmarking (§8.2)

**Synthetic corpus only. Never PHI. Enforced by the guard above.**

The corpus needs recordings that sound like Michelle: natural, unstructured, mid-sentence
corrections, said in a car with the engine running. Fixture #1 is a dictation of a *fictional*
patient — her real timed recap settled the length question but cannot become a test fixture
under §22.

Metrics, per §8.2:

| Dimension | Why it matters here |
|---|---|
| General WER | Baseline |
| Clinical terminology | "phonological process", "dysarthria" |
| **AAC terminology** | "core board", "PECS", "device modeling" — where general models fail hardest |
| **Numbers, percentages, trial counts** | A WER of 5% is worthless if the 5% is the numbers |
| Cue levels | "minimal verbal" vs "moderate verbal" changes clinical meaning |
| Proper names | Also measures de-identification recall |
| Noisy / car-like | The actual recording environment |
| False starts | "she got — sorry, she was requesting…" |

**Word error rate is the wrong headline metric.** A transcript that renders every clinical term
perfectly and turns "sixty percent" into "sixteen percent" is a dangerous transcript with an
excellent WER. Numeric accuracy is reported separately and weighted highest.

Eval results do not gate CI (`CLAUDE.md` tdd carve-out). They gate provider *selection*, and
§8.3 is explicit that the development provider is not automatically the production provider.

---

## Failure behaviour (§19)

| Failure | Behaviour |
|---|---|
| Transcode fails | Audio retained, take marked failed, retry offered, manual entry available |
| STT unavailable | Audio retained (30-day cap still applies), retry, manual entry |
| Extraction fails | Transcript retained, retry, manual entry |
| Extraction returns nothing usable | Transcript shown, all fields missing, manual entry |
| Generation unavailable | **Structured data retained** — chips still work, note assembled manually |
| Numeric-provenance check fails | Job fails loudly. **No draft.** Retry or manual |

**Patient records, scheduling, and manual notes never touch this pipeline.** AI being down
degrades one feature; it does not degrade the practice.

Errors surfaced to Michelle are user-safe and carry a correlation ID:
*"Transcription is unavailable. Your recording is saved — retry, or write the note manually."*
Never a stack trace, never PHI.

---

## Driving safety (§7.7)

Dictation starts while stationary. Once moving, **no visual interaction is required**: recording
continues, the take auto-finalizes at 300s, upload and processing are background, and review
happens later. No prompt, chip, or confirmation ever demands a tap mid-drive.

The 5-minute cap helps here — a take that ends on its own is a take nobody reaches for the phone
to stop.

---

## Open

1. **Which NER for the de-identification pass.** Azure AI Language has a Text Analytics for
   Health PII feature (BAA-covered, another vendor in the data flow) versus a local model
   (no third party, weaker recall). Roster matching covers the critical names either way.
   Decide against measured recall, not vendor preference.
2. **Prompt/version pinning.** Prompts and model versions need to be versioned artifacts with
   eval results attached, or "the notes got worse last week" is unanswerable.
3. **Retry budget.** One retry on schema failure is assumed above. Confirm against observed
   failure rates rather than guessing.
