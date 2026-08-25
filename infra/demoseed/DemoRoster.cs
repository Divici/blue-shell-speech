using Practice.Domain.Consultations;
using Practice.Domain.Goals;
using Practice.Domain.Patients;
using Practice.Domain.Scheduling;

namespace BlueShell.DemoSeed;

/// <summary>
/// The cast. Pure data, no database, no clock.
///
/// EVERY NAME HERE IS INVENTED, and invented in a way that cannot be mistaken for a real
/// family (CLAUDE.md non-negotiable #1). Surnames are constructed rather than borrowed,
/// every email is on <c>example.com</c> (RFC 2606, reserved and unroutable), and every
/// phone number is in the 555-0100…555-0199 block the NANP reserves for fiction. Street
/// names are invented; the towns and postcodes are real Maryland ones, because a demo of
/// an in-home practice in Montgomery and Howard counties has to look like one.
///
/// Michelle's own address, phone and email appear NOWHERE in this file. The practice's
/// contact details come from environment config (CLAUDE.md non-negotiable #7), and this
/// repository is public.
///
/// Kept separate from <see cref="DemoSeeder"/> so the fixture can be read as a fixture:
/// what a reviewer needs to check here is that none of it is real, and that is a question
/// about data, not about control flow.
/// </summary>
public static class DemoRoster
{
    /// <summary>
    /// The name a signed note is attributed to when the seeder cannot ask.
    ///
    /// Overridden with the provider's real <c>DisplayName</c> at run time — this is only
    /// the fallback, and it is deliberately not a person.
    /// </summary>
    public const string FallbackSigner = "Demo Clinician";

    /// <summary>
    /// Eight children, birth to six, with the age spread an early-intervention caseload
    /// actually has. Dates of birth are FIXED rather than computed from the run date: a
    /// fixture whose contents depend on the day it was seeded cannot be reasoned about,
    /// and the ages only drift by the length of a demo window.
    /// </summary>
    public static IReadOnlyList<DemoPatient> Patients { get; } =
    [
        new(
            FirstName: "Amara",
            LastName: "Quillfeather",
            DateOfBirth: new DateOnly(2024, 10, 14),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Minimally speaking. Trialling a high-tech AAC device at home and in "
                + "daycare. Family bilingual (English/Twi). No feeding concerns.",
            Guardians:
            [
                new("Adjoa", "Quillfeather", "Mother", "555-0142", "a.quillfeather@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
                new("Kofi", "Quillfeather", "Father", "555-0143", null,
                    IsPrimaryContact: false, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("18 Tidewrack Lane", null, "Silver Spring", "MD", "20901",
                    AddressType.Session, "Side door, buzzer is broken — knock.", DaysAgoEffective: 420),
            ],
            Goals:
            [
                new("Use the AAC device to request a preferred item across 3 activities.",
                    GoalDomain.Aac, TargetCriteria: "8 of 10 opportunities across 3 sessions",
                    CueLevelExpected: CueLevel.Gestural, StartedDaysAgo: 96,
                    AacModality: AacModality.HighTech,
                    AacDeviceNotes: "Grid 3 on a locked tablet, 24-button core board, keyguard fitted.",
                    Outcome: DemoGoalOutcome.Active),
                new("Combine two symbols on the device to comment (e.g. WANT + MORE).",
                    GoalDomain.Aac, TargetCriteria: "5 spontaneous combinations per session",
                    CueLevelExpected: CueLevel.Visual, StartedDaysAgo: 40,
                    AacModality: AacModality.HighTech,
                    AacDeviceNotes: "Same page set; motor plan unchanged when vocabulary grows.",
                    Outcome: DemoGoalOutcome.Active),
                new("Tolerate the device being within reach during mealtimes.",
                    GoalDomain.Aac, TargetCriteria: "Every meal for 2 consecutive weeks",
                    CueLevelExpected: CueLevel.Independent, StartedDaysAgo: 210,
                    AacModality: AacModality.LowTech,
                    AacDeviceNotes: "Laminated core board used before the device arrived.",
                    Outcome: DemoGoalOutcome.Met),
            ]),

        new(
            FirstName: "Theo",
            LastName: "Saltmarsh",
            DateOfBirth: new DateOnly(2023, 4, 9),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Moderate phonological disorder. Fronting and final-consonant deletion. "
                + "Hearing screened, passed, March 2026.",
            Guardians:
            [
                new("Ruth", "Saltmarsh", "Mother", "555-0118", "r.saltmarsh@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("204 Harrowgate Row", "Apt 3B", "Takoma Park", "MD", "20912",
                    AddressType.Session, "Street parking only, permit zone after 6pm.",
                    DaysAgoEffective: 310),
            ],
            Goals:
            [
                new("Produce /k/ and /g/ in the initial position of single words.",
                    GoalDomain.Articulation, "80% accuracy over 3 consecutive sessions",
                    CueLevel.Verbal, StartedDaysAgo: 150, null, null, DemoGoalOutcome.Active),
                new("Produce final consonants in CVC words in structured play.",
                    GoalDomain.Articulation, "75% accuracy across 20 trials",
                    CueLevel.Visual, StartedDaysAgo: 88, null, null, DemoGoalOutcome.Active),
                new("Produce /f/ in all word positions.",
                    GoalDomain.Articulation, "90% accuracy over 2 sessions",
                    CueLevel.Independent, StartedDaysAgo: 240, null, null, DemoGoalOutcome.Met),
            ]),

        new(
            FirstName: "Nina",
            LastName: "Bramblecoat",
            DateOfBirth: new DateOnly(2021, 12, 3),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Expressive language delay. MLU 2.4 at last sample. Shared custody — "
                + "records go to the father only; see guardian record.",
            /*
             * THE CUSTODY CASE, and the reason it is in the fixture at all.
             *
             * The stepmother is who Michelle rings when a visit has to move, and she has
             * no authority to receive the record. The father holds the authority and is
             * not the person who answers the phone. Those are two different facts about
             * one family, and the whole of D073 is that the product must not derive
             * either from the other — so the demo has to contain a family where deriving
             * one from the other gives the wrong answer.
             */
            Guardians:
            [
                new("Delia", "Marchetti", "Stepmother", "555-0177", "d.marchetti@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: false),
                new("Owen", "Bramblecoat", "Father", "555-0178", "o.bramblecoat@example.com",
                    IsPrimaryContact: false, HasLegalAuthority: true),
            ],
            Addresses:
            [
                // A move: the earlier row is kept and superseded, so the versioned-address
                // UI has something to show and a note from the spring still points at the
                // address the visit actually happened at.
                new("77 Cobblewick Street", null, "Rockville", "MD", "20850",
                    AddressType.Session, null, DaysAgoEffective: 730),
                new("12 Elderfield Close", null, "Rockville", "MD", "20852",
                    AddressType.Session, "Gate code 4417. Dog is friendly but loud.",
                    DaysAgoEffective: 120),
                new("12 Elderfield Close", null, "Rockville", "MD", "20852",
                    AddressType.Billing, null, DaysAgoEffective: 120),
            ],
            Goals:
            [
                new("Use 3–4 word utterances to request and comment during play.",
                    GoalDomain.ExpressiveLanguage, "Average MLU 3.5 across 2 language samples",
                    CueLevel.Verbal, StartedDaysAgo: 175, null, null, DemoGoalOutcome.Active),
                new("Answer 'what' and 'where' questions about a shared picture book.",
                    GoalDomain.ReceptiveLanguage, "8 of 10 questions across 3 sessions",
                    CueLevel.Gestural, StartedDaysAgo: 130, null, null, DemoGoalOutcome.Active),
                new("Use regular past tense -ed in modelled sentence frames.",
                    GoalDomain.ExpressiveLanguage, "70% accuracy across 20 trials",
                    CueLevel.Verbal, StartedDaysAgo: 300, null, null, DemoGoalOutcome.Discontinued),
            ]),

        new(
            FirstName: "Jonah",
            LastName: "Winterbourne",
            DateOfBirth: new DateOnly(2024, 2, 27),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Selective eating with texture aversion, plus receptive language delay. "
                + "Paediatric GI review complete, no medical cause found.",
            Guardians:
            [
                new("Marguerite", "Winterbourne", "Mother", "555-0155", "m.winterbourne@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("9 Pellham Hollow", null, "Bethesda", "MD", "20814",
                    AddressType.Session, "Driveway is steep — park on the street.",
                    DaysAgoEffective: 200),
            ],
            Goals:
            [
                new("Accept one bite of a non-preferred smooth texture at each session.",
                    GoalDomain.Feeding, "4 of 5 opportunities across 3 sessions",
                    CueLevel.Tactile, StartedDaysAgo: 65, null, null, DemoGoalOutcome.Active),
                new("Follow one-step directions containing a spatial term.",
                    GoalDomain.ReceptiveLanguage, "8 of 10 directions across 2 sessions",
                    CueLevel.Gestural, StartedDaysAgo: 110, null, null, DemoGoalOutcome.Active),
                new("Remain at the table for a 10-minute family meal.",
                    GoalDomain.Feeding, "Reported by caregiver on 5 consecutive days",
                    CueLevel.Verbal, StartedDaysAgo: 30, null, null, DemoGoalOutcome.OnHold),
            ]),

        new(
            FirstName: "Priya",
            LastName: "Hollowell",
            DateOfBirth: new DateOnly(2021, 6, 18),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Childhood-onset fluency disorder. Family history on the maternal side. "
                + "Awareness emerging; no avoidance behaviours observed yet.",
            Guardians:
            [
                new("Anita", "Hollowell", "Mother", "555-0164", "a.hollowell@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
                new("Dev", "Hollowell", "Father", null, "d.hollowell@example.com",
                    IsPrimaryContact: false, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("31 Marrowbrook Way", null, "Columbia", "MD", "21044",
                    AddressType.Session, null, DaysAgoEffective: 150),
            ],
            Goals:
            [
                new("Use easy onset on identified difficult words in structured conversation.",
                    GoalDomain.Fluency, "In 8 of 10 opportunities across 3 sessions",
                    CueLevel.Verbal, StartedDaysAgo: 70, null, null, DemoGoalOutcome.Active),
                new("Identify moments of stuttering in the clinician's speech.",
                    GoalDomain.Fluency, "9 of 10 modelled moments",
                    CueLevel.Independent, StartedDaysAgo: 190, null, null, DemoGoalOutcome.Met),
            ]),

        new(
            FirstName: "Elias",
            LastName: "Fernwhistle",
            DateOfBirth: new DateOnly(2025, 5, 21),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Early intervention referral. Fewer than 10 words at 15 months. Grandmother "
                + "is the day-to-day carer; custody paperwork has not arrived.",
            /*
             * A FAMILY WITH NO AUTHORISED GUARDIAN ON FILE, which docs/DATA_MODEL.md calls
             * a real state rather than an error. The patient page renders
             * `recordsReleaseState` for exactly this case, and it has nothing to render
             * unless the fixture contains one.
             */
            Guardians:
            [
                new("Bernadette", "Fernwhistle", "Grandmother", "555-0131", null,
                    IsPrimaryContact: true, HasLegalAuthority: false),
            ],
            Addresses:
            [
                new("5 Thistledown Court", null, "Ellicott City", "MD", "21042",
                    AddressType.Session, "Ring twice — she does not hear the first.",
                    DaysAgoEffective: 45),
            ],
            Goals:
            [
                new("Respond to his name by orienting to the speaker.",
                    GoalDomain.ReceptiveLanguage, "4 of 5 opportunities across 3 sessions",
                    CueLevel.Gestural, StartedDaysAgo: 35, null, null, DemoGoalOutcome.Active),
                new("Use a consistent gesture or vocalisation to request.",
                    GoalDomain.SocialCommunication, "5 spontaneous requests per session",
                    CueLevel.Visual, StartedDaysAgo: 35, null, null, DemoGoalOutcome.Active),
            ]),

        new(
            FirstName: "Sadie",
            LastName: "Thornbury",
            DateOfBirth: new DateOnly(2020, 7, 30),
            Status: PatientStatus.Active,
            ClinicalSummary:
                "Social communication differences. Starting kindergarten in the autumn; "
                + "reassessment scheduled to inform the school's plan.",
            Guardians:
            [
                new("Corinne", "Thornbury", "Mother", "555-0109", "c.thornbury@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("46 Quarrymoor Drive", null, "Frederick", "MD", "21701",
                    AddressType.Session, null, DaysAgoEffective: 500),
                new("46 Quarrymoor Drive", null, "Frederick", "MD", "21701",
                    AddressType.Billing, "Invoices to this address, attn. C. Thornbury.",
                    DaysAgoEffective: 500),
            ],
            Goals:
            [
                new("Initiate a turn in a two-person game without a prompt.",
                    GoalDomain.SocialCommunication, "3 initiations per 15-minute activity",
                    CueLevel.Independent, StartedDaysAgo: 260, null, null, DemoGoalOutcome.Active),
                new("Repair a breakdown when a listener signals confusion.",
                    GoalDomain.SocialCommunication, "7 of 10 breakdowns across 3 sessions",
                    CueLevel.Verbal, StartedDaysAgo: 100, null, null, DemoGoalOutcome.Active),
            ]),

        new(
            FirstName: "Rowan",
            LastName: "Casterbridge",
            DateOfBirth: new DateOnly(2022, 8, 11),
            Status: PatientStatus.Discharged,
            ClinicalSummary:
                "Discharged June 2026 — all goals met, speech intelligible to unfamiliar "
                + "listeners. Record retained.",
            Guardians:
            [
                new("Imogen", "Casterbridge", "Mother", "555-0126", "i.casterbridge@example.com",
                    IsPrimaryContact: true, HasLegalAuthority: true),
            ],
            Addresses:
            [
                new("83 Fallowmere Avenue", null, "Silver Spring", "MD", "20904",
                    AddressType.Session, null, DaysAgoEffective: 600),
            ],
            Goals:
            [
                new("Produce /r/ in all word positions in conversation.",
                    GoalDomain.Articulation, "90% accuracy in a 10-minute sample",
                    CueLevel.Independent, StartedDaysAgo: 520, null, null, DemoGoalOutcome.Met),
                new("Be understood by an unfamiliar listener without repetition.",
                    GoalDomain.Articulation, "Caregiver report over 2 weeks",
                    CueLevel.Independent, StartedDaysAgo: 430, null, null, DemoGoalOutcome.Met),
            ]),
    ];

    /// <summary>
    /// The calendar: two days back through three days forward, in practice-local time.
    ///
    /// Offsets rather than dates, resolved against the practice-local date of the run
    /// (<c>PracticeTime.LocalDateOf</c>), so <c>/today</c> is a full day whenever it is
    /// seeded. A visit whose END is already in the past is completed by the seeder; one
    /// that has not started is left Scheduled. That is not a special case — it is what a
    /// day looks like at any hour, and it means the documentation gate refuses for a real
    /// reason rather than a contrived one.
    ///
    /// The past days carry the notes, because "has this visit started" is not a question
    /// about yesterday. Today's visits carry none, so the day view shows the entry point
    /// rather than the outcome.
    ///
    /// TRAVEL BLOCKS are on almost everything: this is an in-home practice and the drive
    /// between two houses in different counties is occupied time (presearch §5.6). The
    /// slots below do not conflict once travel is counted, and <see cref="DemoSeeder"/>
    /// asserts that through <c>Appointment.ConflictsWith</c> rather than trusting it.
    /// </summary>
    public static IReadOnlyList<DemoVisit> Visits { get; } =
    [
        // ----------------------------------------------------------- two days ago
        new("Saltmarsh", DayOffset: -2, new TimeOnly(9, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, Mileage: 11.4m,
            Notes: null,
            Note: new DemoNote(
                Subjective:
                    "Mum reports Theo has been using 'go' and 'car' at home this week and "
                    + "that his older brother understood him without repeating.",
                Objective:
                    "Initial /k/ in single words: 16 of 20 correct with a verbal model "
                    + "(80%). Final consonants in CVC: 11 of 20 (55%) with visual cue. "
                    + "Sustained attention across four activities of 8–10 minutes.",
                Assessment:
                    "Initial /k/ has met criterion for a second consecutive session. "
                    + "Final-consonant deletion persists and is the limiting factor for "
                    + "intelligibility with unfamiliar listeners.",
                Plan:
                    "Move initial /k/ to phrase level. Continue CVC final consonants with "
                    + "a visual cue, fading to verbal. Home programme: five minutes daily "
                    + "with the picture cards left today.",
                State: DemoNoteState.Signed,
                Origin: DemoNoteOrigin.Manual,
                Amendment: new DemoAmendment(
                    Reason: "Trial count for final consonants was transcribed from the wrong "
                            + "column of the data sheet.",
                    Subjective:
                        "Mum reports Theo has been using 'go' and 'car' at home this week and "
                        + "that his older brother understood him without repeating.",
                    Objective:
                        "Initial /k/ in single words: 16 of 20 correct with a verbal model "
                        + "(80%). Final consonants in CVC: 11 of 24 (46%) with visual cue. "
                        + "Sustained attention across four activities of 8–10 minutes.",
                    Assessment:
                        "Initial /k/ has met criterion for a second consecutive session. "
                        + "Final-consonant deletion persists and is the limiting factor for "
                        + "intelligibility with unfamiliar listeners.",
                    Plan:
                        "Move initial /k/ to phrase level. Continue CVC final consonants with "
                        + "a visual cue, fading to verbal. Home programme: five minutes daily "
                        + "with the picture cards left today."))),

        new("Thornbury", DayOffset: -2, new TimeOnly(13, 30), 60, AppointmentType.Therapy,
            TravelBlockMinutes: 45, DemoVisitOutcome.AsScheduled, null, Mileage: 31.2m,
            Notes: "Long drive — Frederick.",
            Note: null),

        // ----------------------------------------------------------------- yesterday
        new("Bramblecoat", DayOffset: -1, new TimeOnly(10, 30), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 20, DemoVisitOutcome.AsScheduled, null, Mileage: 8.9m,
            Notes: null,
            Note: new DemoNote(
                Subjective:
                    "Stepmother reports Nina is stringing more words together at nursery "
                    + "and that staff have noticed the change.",
                Objective:
                    "Language sample of 62 utterances during play: MLU 3.1, up from 2.4 at "
                    + "the last sample. Answered 7 of 10 'what' questions and 4 of 10 "
                    + "'where' questions with a gestural cue.",
                Assessment:
                    "Expressive gains are consistent with the home report. 'Where' "
                    + "questions remain well below criterion and appear to be a "
                    + "comprehension issue rather than an expressive one.",
                Plan:
                    "Continue the expressive goal at conversation level. Add a spatial-term "
                    + "sort to the receptive goal. Re-sample in four weeks.",
                State: DemoNoteState.Signed,
                Origin: DemoNoteOrigin.Manual,
                Amendment: null)),

        new("Quillfeather", DayOffset: -1, new TimeOnly(13, 0), 60, AppointmentType.Therapy,
            TravelBlockMinutes: 30, DemoVisitOutcome.AsScheduled, null, Mileage: 14.7m,
            Notes: null,
            /*
             * The unfinished note, left as a DRAFT on purpose.
             *
             * Yesterday's paperwork that did not get signed is the ordinary state of a
             * solo practice, and it is the one state the immutability story needs on
             * screen alongside the other two: a draft is still editable, and the moment
             * it is signed it stops being.
             */
            Note: new DemoNote(
                Subjective:
                    "Mum reports Amara took the device to her father unprompted on Saturday "
                    + "to ask for the tablet.",
                Objective:
                    "Requesting with the device across three activities: 7 of 10 "
                    + "opportunities with a gestural cue. Two spontaneous WANT + MORE "
                    + "combinations during snack, both unprompted.",
                Assessment: "",
                Plan: "",
                State: DemoNoteState.Draft,
                Origin: DemoNoteOrigin.DictationAssisted,
                Amendment: null)),

        new("Winterbourne", DayOffset: -1, new TimeOnly(15, 15), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, Mileage: 12.1m,
            Notes: null,
            Note: new DemoNote(
                Subjective:
                    "Mum reports two refused meals this week and one where Jonah stayed at "
                    + "the table for the whole ten minutes.",
                Objective:
                    "Accepted one bite of a smooth non-preferred texture in 3 of 5 "
                    + "opportunities with hand-over-hand support. Followed 6 of 10 one-step "
                    + "directions containing a spatial term with a gestural cue.",
                Assessment:
                    "Below criterion on both goals this session. The refusals at home "
                    + "coincide with a reported cold, so the drop is likely situational.",
                Plan:
                    "Repeat the same textures next session before changing the target. "
                    + "Keep the mealtime goal on hold until the family reports two "
                    + "settled weeks.",
                State: DemoNoteState.Signed,
                Origin: DemoNoteOrigin.Manual,
                Amendment: null)),

        new("Hollowell", DayOffset: -1, new TimeOnly(17, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 20, DemoVisitOutcome.NoShow, null, Mileage: null,
            Notes: null, Note: null),

        // --------------------------------------------------------------------- today
        new("Fernwhistle", DayOffset: 0, new TimeOnly(8, 15), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 15, DemoVisitOutcome.AsScheduled, null, Mileage: 6.3m,
            Notes: null, Note: null),

        new("Saltmarsh", DayOffset: 0, new TimeOnly(10, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, Mileage: 11.4m,
            Notes: null, Note: null),

        new("Bramblecoat", DayOffset: 0, new TimeOnly(11, 30), 60, AppointmentType.Therapy,
            TravelBlockMinutes: 20, DemoVisitOutcome.Cancelled,
            CancellationReason: "Family called this morning — Nina is unwell.",
            Mileage: null, Notes: null, Note: null),

        new("Quillfeather", DayOffset: 0, new TimeOnly(13, 30), 60, AppointmentType.Therapy,
            TravelBlockMinutes: 30, DemoVisitOutcome.AsScheduled, null, Mileage: 14.7m,
            Notes: "Bring the second keyguard.", Note: null),

        new("Thornbury", DayOffset: 0, new TimeOnly(15, 15), 60, AppointmentType.Reassessment,
            TravelBlockMinutes: 20, DemoVisitOutcome.NoShow, null, Mileage: null,
            Notes: null, Note: null),

        new("Winterbourne", DayOffset: 0, new TimeOnly(17, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, Mileage: 12.1m,
            Notes: null, Note: null),

        // An evening in-home session. It is the visit that is still in the future for most
        // of the working day, which is what gives `/today` a documentation gate to refuse.
        new("Hollowell", DayOffset: 0, new TimeOnly(19, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 20, DemoVisitOutcome.AsScheduled, null, Mileage: 9.8m,
            Notes: "Rescheduled from yesterday's no-show.", Note: null),

        // ------------------------------------------------------------------ the week
        new("Quillfeather", DayOffset: 1, new TimeOnly(9, 0), 60, AppointmentType.Therapy,
            TravelBlockMinutes: 30, DemoVisitOutcome.AsScheduled, null, null, null, null),

        new("Fernwhistle", DayOffset: 1, new TimeOnly(11, 30), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, null, null, null),

        new("Thornbury", DayOffset: 1, new TimeOnly(14, 30), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 45, DemoVisitOutcome.AsScheduled, null, null, null, null),

        new("Saltmarsh", DayOffset: 2, new TimeOnly(9, 30), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, null, null, null),

        new("Bramblecoat", DayOffset: 2, new TimeOnly(13, 0), 90, AppointmentType.Evaluation,
            TravelBlockMinutes: 30, DemoVisitOutcome.AsScheduled, null, null,
            Notes: "Annual re-evaluation. Allow the full ninety minutes.", Note: null),

        new("Winterbourne", DayOffset: 3, new TimeOnly(10, 0), 45, AppointmentType.Therapy,
            TravelBlockMinutes: 25, DemoVisitOutcome.AsScheduled, null, null, null, null),

        new("Hollowell", DayOffset: 3, new TimeOnly(12, 30), 30, AppointmentType.Consultation,
            TravelBlockMinutes: 20, DemoVisitOutcome.AsScheduled, null, null,
            Notes: "Phone catch-up with both parents about school liaison.", Note: null),
    ];

    /// <summary>
    /// One enquiry in each of the four statuses the inbox filters on.
    ///
    /// The converted one points at Nina Bramblecoat, so "this enquiry became a patient"
    /// leads somewhere in the demo rather than dangling.
    /// </summary>
    public static IReadOnlyList<DemoEnquiry> Enquiries { get; } =
    [
        new("Priya Vantell", "p.vantell@example.com", "555-0188", "Nadia", 30,
            "Nadia has about fifteen words and mostly points. Our paediatrician suggested "
            + "we get a speech assessment. We are in Silver Spring and would prefer visits "
            + "at home if that is possible.",
            PreferredContactMethod.Either, ConsultationStatus.New,
            ConvertToPatientLastName: null, SubmittedDaysAgo: 1),

        new("Marcus Ledgerwood", "m.ledgerwood@example.com", "555-0193", "Owen", 44,
            "Owen stammers on the first sound of words and has started to avoid talking at "
            + "nursery. It got noticeably worse over the summer. Happy to be emailed.",
            PreferredContactMethod.Email, ConsultationStatus.Contacted,
            ConvertToPatientLastName: null, SubmittedDaysAgo: 6),

        new("Delia Marchetti", "d.marchetti@example.com", "555-0177", "Nina", 45,
            "Nina is not putting words together the way her cousin did at the same age. "
            + "Nursery raised it with us at the last review and suggested we ask.",
            PreferredContactMethod.Phone, ConsultationStatus.Converted,
            ConvertToPatientLastName: "Bramblecoat", SubmittedDaysAgo: 190),

        new("Harriet Ovendale", "h.ovendale@example.com", null, "Sam", 84,
            "Sam is seven and has a lisp that bothers him at school. We are in Delaware — "
            + "is that too far, or do you do video sessions?",
            PreferredContactMethod.Email, ConsultationStatus.Declined,
            ConvertToPatientLastName: null, SubmittedDaysAgo: 22),
    ];
}

/// <summary>One child, with everything that hangs off the patient record.</summary>
public sealed record DemoPatient(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    PatientStatus Status,
    string ClinicalSummary,
    IReadOnlyList<DemoGuardian> Guardians,
    IReadOnlyList<DemoAddress> Addresses,
    IReadOnlyList<DemoGoal> Goals);

/// <summary>
/// A parent or carer.
///
/// <paramref name="IsPrimaryContact"/> and <paramref name="HasLegalAuthority"/> are two
/// separate arguments here for the same reason they are two separate columns: neither is
/// derivable from the other, and the fixture contains a family where deriving one from the
/// other releases a record to the wrong adult (D073).
/// </summary>
public sealed record DemoGuardian(
    string FirstName,
    string LastName,
    string Relationship,
    string? Phone,
    string? Email,
    bool IsPrimaryContact,
    bool HasLegalAuthority);

/// <summary>
/// An address, dated relative to the run so the record has plausible history.
/// </summary>
public sealed record DemoAddress(
    string Line1,
    string? Line2,
    string City,
    string State,
    string PostalCode,
    AddressType AddressType,
    string? Notes,
    int DaysAgoEffective);

public sealed record DemoGoal(
    string GoalText,
    GoalDomain Domain,
    string? TargetCriteria,
    CueLevel? CueLevelExpected,
    int StartedDaysAgo,
    AacModality? AacModality,
    string? AacDeviceNotes,
    DemoGoalOutcome Outcome);

/// <summary>What happened to the goal after it was written.</summary>
public enum DemoGoalOutcome
{
    Active = 1,
    Met = 2,
    Discontinued = 3,
    OnHold = 4,
}

public sealed record DemoVisit(
    string PatientLastName,
    int DayOffset,
    TimeOnly LocalStart,
    short DurationMinutes,
    AppointmentType AppointmentType,
    short? TravelBlockMinutes,
    DemoVisitOutcome Outcome,
    string? CancellationReason,
    decimal? Mileage,
    string? Notes,
    DemoNote? Note);

/// <summary>
/// What the seeder does to a visit once it exists.
///
/// <see cref="AsScheduled"/> is not "leave it Scheduled": it means nothing went wrong, so
/// the seeder completes it if its end time has already passed and leaves it alone if it
/// has not.
/// </summary>
public enum DemoVisitOutcome
{
    AsScheduled = 1,
    Cancelled = 2,
    NoShow = 3,
}

public sealed record DemoNote(
    string Subjective,
    string Objective,
    string Assessment,
    string Plan,
    DemoNoteState State,
    DemoNoteOrigin Origin,
    DemoAmendment? Amendment);

/// <summary>
/// The correction, and the version it produces.
///
/// The seeder signs the amendment, so the chain on screen is v1 Amended (retained, not
/// current) → v2 Signed (current). Both halves of the record are visible, which is the
/// only way the immutability claim is demonstrable rather than asserted.
/// </summary>
public sealed record DemoAmendment(
    string Reason,
    string Subjective,
    string Objective,
    string Assessment,
    string Plan);

// Same false positive NoteStatus carries, for the same reason: 'Signed' is the clinical
// term for an attested note, and the analyzer is matching it against the 'signed' numeric
// type. Renaming would make the demo's vocabulary disagree with the domain's.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "'Signed' mirrors NoteStatus.Signed, which carries the same suppression.")]
public enum DemoNoteState
{
    Draft = 1,
    Signed = 2,
}

public enum DemoNoteOrigin
{
    Manual = 1,
    DictationAssisted = 2,
}

public sealed record DemoEnquiry(
    string ParentName,
    string Email,
    string? Phone,
    string ChildFirstName,
    short ChildAgeMonths,
    string Concerns,
    PreferredContactMethod PreferredContactMethod,
    ConsultationStatus Status,
    string? ConvertToPatientLastName,
    int SubmittedDaysAgo);
