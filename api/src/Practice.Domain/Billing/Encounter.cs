using System.Text.RegularExpressions;
using Practice.Domain.Common;

namespace Practice.Domain.Billing;

/// <summary>
/// The billable record of one service at one visit — the row a superbill is printed from
/// (presearch §16).
///
/// THIS TABLE SHIPS EMPTY, ON PURPOSE. There is no endpoint and no screen behind it, and
/// superbill generation is sequenced later (CLAUDE.md scope ledger). What is not deferred
/// is the SHAPE: adding a billing table to a live clinical database means backfilling every
/// historical appointment from notes, which is a data-archaeology exercise nobody should
/// have to do. One migration now costs a migration; one migration later costs the history.
///
/// NOT the same thing as an Appointment. An appointment is a slot in a diary — it can be
/// cancelled, it can be a free consultation, and it can carry no charge at all. An
/// encounter is a claim that a specific service was rendered and is owed for. A visit can
/// produce two of them (a therapy code and an evaluation code on the same afternoon), which
/// is why nothing here is unique on AppointmentId.
///
/// **No card data, ever.** presearch §17 keeps this practice outside PCI scope: payment
/// happens externally and this row records only that it happened. See EncounterTests.
/// </summary>
public sealed partial class Encounter : Entity
{
    private readonly List<EncounterDiagnosis> _diagnoses = [];

    // EF Core materialisation only.
    private Encounter() { }

    /// <summary>Tenancy discriminator, present from day one even at one provider.</summary>
    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    public long AppointmentId { get; private set; }

    /// <summary>
    /// Who actually delivered the service, which is NOT the same fact as who owns the row.
    ///
    /// They are the same clinician today and the columns will hold the same value for
    /// every row this practice writes for the foreseeable future. They are separate because
    /// ProviderId is a TENANCY discriminator — it answers "whose database is this" — and a
    /// superbill names the rendering clinician and prints their NPI. A clinical fellow
    /// working under supervision makes those two different answers, and merging them is the
    /// same conflation D073 refused between IsPrimaryContact and HasLegalAuthority.
    /// </summary>
    public long RenderingProviderId { get; private set; }

    /// <summary>
    /// The note version this encounter was CODED FROM. Set once, never re-pointed.
    ///
    /// A signed note is amended by inserting a new row (docs/DATA_MODEL.md), so a pointer
    /// that followed the current version would answer a different question every time the
    /// record is corrected. What this answers is "what did the clinician have in front of
    /// them when they chose this code" — which is the question an audit of a bill asks. The
    /// CURRENT note is always reachable through AppointmentId, so nothing is lost.
    ///
    /// Nullable because the coding can be recorded before the note is signed.
    /// </summary>
    public long? ClinicalNoteId { get; private set; }

    /// <summary>
    /// The calendar date of service, in the practice's timezone — a `date`, not an instant.
    ///
    /// Everything else in this system stores UTC. A date of service is not a moment: it is
    /// the day a payer will compare against, printed as one date on one document, and an
    /// in-home practice runs evening sessions where the UTC date has already rolled over.
    /// The same reasoning DateOfBirth already uses.
    /// </summary>
    public DateOnly ServiceDate { get; private set; }

    /// <summary>
    /// The CPT or HCPCS procedure code. Five characters, stored upper case.
    ///
    /// The code only. **No descriptor column** — CPT descriptors are licensed by the AMA,
    /// so shipping a lookup table of them is a licensing decision presearch §16 flags as
    /// unresolved research. A code is a fact about this encounter; a descriptor is somebody
    /// else's copyrighted text.
    /// </summary>
    public string CptCode { get; private set; } = string.Empty;

    /// <summary>
    /// Up to four two-character modifiers, comma separated, upper case. Null when none.
    ///
    /// A delimited column here and a child table for diagnoses, deliberately. A modifier is
    /// a coding qualifier on this line whose order comes from payer rules; a diagnosis is a
    /// clinical claim about the child whose FIRST entry means "the primary reason for this
    /// encounter". Only one of those is worth a table.
    /// </summary>
    public string? Modifiers { get; private set; }

    /// <summary>
    /// Where the service happened, as the CMS place-of-service code itself.
    ///
    /// The enum's values ARE the CMS numbers, so the stored value is what a superbill
    /// prints and the enum is what refuses 77. See EncounterTests for why the numbering is
    /// pinned by a test rather than left to whoever tidies the file next.
    /// </summary>
    public PlaceOfService PlaceOfService { get; private set; }

    public short Units { get; private set; }

    public decimal ChargeAmount { get; private set; }

    public decimal AmountPaid { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; } = PaymentStatus.Unpaid;

    /// <summary>How the family paid. Null until they have.</summary>
    public PaymentMethod? PaymentMethod { get; private set; }

    /// <summary>When the most recent payment landed. UTC.</summary>
    public DateTime? PaidAtUtc { get; private set; }

    /// <summary>
    /// When a superbill was last produced for this encounter.
    ///
    /// Deliberately "last", not "first": a family that loses the PDF asks for it again, and
    /// the document is regenerated from the same row rather than being a stored artefact.
    /// </summary>
    public DateTime? SuperbillGeneratedAtUtc { get; private set; }

    public IReadOnlyList<EncounterDiagnosis> Diagnoses => _diagnoses;

    /// <summary>
    /// A CMS-1500 line points at up to four diagnoses, and a superbill mirrors that form.
    /// </summary>
    public const int MaxDiagnoses = 4;

    /// <summary>
    /// Sixteen fifteen-minute units — the practice's four-hour appointment cap
    /// (Appointment.MaxDurationMinutes) expressed in the timed-code unit. More than that on
    /// one line is a data-entry error, not a long session.
    /// </summary>
    public const short MaxUnits = 16;

    public const int CptCodeLength = 5;
    public const int MaxModifiersLength = 11;
    public const int MaxDiagnosisCodeLength = 8;

    public static Encounter Record(
        long providerId,
        long patientId,
        long appointmentId,
        long renderingProviderId,
        DateTime serviceStartUtc,
        string cptCode,
        PlaceOfService placeOfService,
        short units,
        decimal chargeAmount,
        string? modifiers = null)
    {
        if (providerId <= 0)
        {
            throw new ArgumentException("An encounter needs a provider.", nameof(providerId));
        }

        if (patientId <= 0)
        {
            throw new ArgumentException("An encounter needs a patient.", nameof(patientId));
        }

        if (appointmentId <= 0)
        {
            throw new ArgumentException("An encounter needs a visit.", nameof(appointmentId));
        }

        if (renderingProviderId <= 0)
        {
            throw new ArgumentException(
                "An encounter needs a rendering clinician.", nameof(renderingProviderId));
        }

        /*
         * UTC or nothing, the same rule Appointment and ConsultationRequest hold — and it
         * matters more here, because the value is not stored: it is CONVERTED to a
         * practice-local date and the instant is thrown away. A Kind of Unspecified would
         * be converted from the wrong zone and the mistake would be unrecoverable from the
         * row afterwards.
         */
        if (serviceStartUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A service time must be UTC. Convert before calling.", nameof(serviceStartUtc));
        }

        if (!Enum.IsDefined(placeOfService))
        {
            /*
             * An integer cast into an enum type is not checked by the runtime, so
             * (PlaceOfService)77 is a legal value of that type — and here the stored number
             * is printed on a document a payer reads, so an unknown one is not merely
             * unreadable, it is wrong on a bill.
             */
            throw new ArgumentException(
                "That is not a CMS place of service.", nameof(placeOfService));
        }

        if (units < 1 || units > MaxUnits)
        {
            throw new ArgumentException(
                $"A line bills between 1 and {MaxUnits} units.", nameof(units));
        }

        if (chargeAmount < 0)
        {
            throw new ArgumentException("A charge cannot be negative.", nameof(chargeAmount));
        }

        return new Encounter
        {
            ProviderId = providerId,
            PatientId = patientId,
            AppointmentId = appointmentId,
            RenderingProviderId = renderingProviderId,
            ServiceDate = PracticeTime.LocalDateOf(serviceStartUtc),
            CptCode = ValidatedCptCode(cptCode),
            PlaceOfService = placeOfService,
            Units = units,
            ChargeAmount = chargeAmount,
            Modifiers = ValidatedModifiers(modifiers),
        };
    }

    /// <summary>
    /// Adds a diagnosis pointer, in order. The first one recorded is the primary.
    /// </summary>
    public void AddDiagnosis(string icd10Code)
    {
        var code = ValidatedIcd10(icd10Code);

        if (_diagnoses.Count >= MaxDiagnoses)
        {
            throw new InvalidOperationException(
                $"A line points at no more than {MaxDiagnoses} diagnoses.");
        }

        if (_diagnoses.Any(d => d.Code == code))
        {
            throw new InvalidOperationException(
                $"{code} is already recorded on this encounter.");
        }

        _diagnoses.Add(EncounterDiagnosis.For(
            ProviderId, (short)(_diagnoses.Count + 1), code));
    }

    /// <summary>
    /// Records which note version this coding was taken from. See ClinicalNoteId.
    /// </summary>
    public void LinkClinicalNote(long clinicalNoteId)
    {
        if (clinicalNoteId <= 0)
        {
            throw new ArgumentException(
                "A note link needs a note.", nameof(clinicalNoteId));
        }

        if (ClinicalNoteId is not null && ClinicalNoteId != clinicalNoteId)
        {
            throw new InvalidOperationException(
                "This encounter already records the note it was coded from. An amendment "
                + "creates a new version; it does not change what was read at the time.");
        }

        ClinicalNoteId = clinicalNoteId;
    }

    /// <summary>
    /// Records money received. Payments ACCUMULATE — a second one is not a correction of
    /// the first, and a family paying in two halves is ordinary.
    /// </summary>
    public void RecordPayment(decimal amount, PaymentMethod method, DateTime paidAtUtc)
    {
        if (amount <= 0)
        {
            throw new ArgumentException("A payment must be for something.", nameof(amount));
        }

        if (!Enum.IsDefined(method))
        {
            throw new ArgumentException("That is not a way of paying.", nameof(method));
        }

        if (paidAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A payment time must be UTC. Convert before calling.", nameof(paidAtUtc));
        }

        if (PaymentStatus is PaymentStatus.Waived)
        {
            throw new InvalidOperationException(
                "This charge was waived. There is nothing to pay against it.");
        }

        AmountPaid += amount;
        PaymentMethod = method;
        PaidAtUtc = paidAtUtc;

        /*
         * Greater-than-or-equal, not equal. An overpayment settles the charge and stays on
         * the row: refusing it would leave the practice unable to record what actually
         * arrived, and silently capping it would make the row disagree with the bank.
         */
        PaymentStatus = AmountPaid >= ChargeAmount
            ? PaymentStatus.Paid
            : PaymentStatus.PartiallyPaid;
    }

    /// <summary>
    /// The practice is not charging for this — a courtesy visit, a sliding scale, a session
    /// cut short. Distinct from Unpaid, which is money still expected.
    /// </summary>
    public void WaiveCharge()
    {
        if (AmountPaid > 0)
        {
            throw new InvalidOperationException(
                "Money has already been taken against this charge. Refund it before waiving.");
        }

        PaymentStatus = PaymentStatus.Waived;
    }

    /// <summary>
    /// Records that a superbill was produced. Repeatable — see SuperbillGeneratedAtUtc.
    ///
    /// NOTHING FREEZES HERE, and that is a decision rather than an omission. A document a
    /// family has already submitted to their insurer should not be silently editable, but a
    /// freeze with no correction path is the trap D069 describes: one wrong code, generated
    /// once, and the row can never be right again. The freeze and the void-and-replace path
    /// ship together with generation itself.
    /// </summary>
    public void MarkSuperbillGenerated(DateTime generatedAtUtc)
    {
        if (generatedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A generation time must be UTC. Convert before calling.", nameof(generatedAtUtc));
        }

        if (_diagnoses.Count == 0)
        {
            throw new InvalidOperationException(
                "A superbill needs at least one diagnosis code. The payer's first question "
                + "is what the service treated.");
        }

        SuperbillGeneratedAtUtc = generatedAtUtc;
    }

    /// <summary>
    /// Five alphanumerics, which covers CPT (92507) and HCPCS Level II (S9152) alike.
    ///
    /// SHAPE ONLY. Whether the code exists in this year's CPT set is not checked and cannot
    /// honestly be: sourcing that set is unresolved research (presearch §16), and a shape
    /// check dressed up as a validity check is worse than one that says what it is.
    /// </summary>
    private static string ValidatedCptCode(string cptCode)
    {
        var normalised = (cptCode ?? string.Empty).Trim().ToUpperInvariant();

        if (!CptCode5().IsMatch(normalised))
        {
            throw new ArgumentException(
                $"A procedure code is {CptCodeLength} letters or digits.", nameof(cptCode));
        }

        return normalised;
    }

    private static string? ValidatedModifiers(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers)) return null;

        var normalised = modifiers.Trim().ToUpperInvariant();

        if (!ModifierList().IsMatch(normalised))
        {
            throw new ArgumentException(
                "Modifiers are up to four two-character codes, comma separated, with no "
                + "spaces.",
                nameof(modifiers));
        }

        return normalised;
    }

    /// <summary>
    /// ICD-10-CM shape: a letter, a digit, a digit or letter, optionally a point and up to
    /// four more. Stored WITH the point, which is how a superbill prints it.
    ///
    /// Shape only, for the same reason as the procedure code above.
    /// </summary>
    private static string ValidatedIcd10(string icd10Code)
    {
        var normalised = (icd10Code ?? string.Empty).Trim().ToUpperInvariant();

        if (!Icd10Cm().IsMatch(normalised))
        {
            throw new ArgumentException(
                "That is not shaped like an ICD-10-CM code.", nameof(icd10Code));
        }

        return normalised;
    }

    [GeneratedRegex("^[0-9A-Z]{5}$")]
    private static partial Regex CptCode5();

    [GeneratedRegex("^[0-9A-Z]{2}(,[0-9A-Z]{2}){0,3}$")]
    private static partial Regex ModifierList();

    [GeneratedRegex(@"^[A-Z][0-9][0-9A-Z](\.[0-9A-Z]{1,4})?$")]
    private static partial Regex Icd10Cm();
}

/// <summary>
/// One diagnosis pointer on an encounter, in the order the clinician recorded it.
///
/// A row rather than an entry in a delimited column, because the ORDER is a clinical claim:
/// the first code is the primary reason for the encounter. A comma-separated string stores
/// an order it cannot enforce, cannot refuse a duplicate, and cannot answer "which children
/// carry F80.2" without scanning every row.
/// </summary>
public sealed class EncounterDiagnosis : Entity
{
    // EF Core materialisation only.
    private EncounterDiagnosis() { }

    /// <summary>
    /// Tenancy discriminator on a CHILD row, present from day one.
    ///
    /// It looks redundant — this row is only reachable through an encounter that carries
    /// one. It is not: a child row whose only protection is its parent's query filter is
    /// the exact defect D066 F4 and D073 both found, where a filter could be deleted
    /// outright and no test would notice because the parent was covering for it.
    /// </summary>
    public long ProviderId { get; private set; }

    public long EncounterId { get; private set; }

    /// <summary>1 is the primary diagnosis.</summary>
    public short Sequence { get; private set; }

    /// <summary>Upper case, with the decimal point. Shape-checked, not looked up.</summary>
    public string Code { get; private set; } = string.Empty;

    internal static EncounterDiagnosis For(long providerId, short sequence, string code) =>
        new()
        {
            ProviderId = providerId,
            Sequence = sequence,
            Code = code,
        };
}

/// <summary>
/// CMS place-of-service codes. **The values are the codes**, not an arbitrary sequence —
/// see Encounter.PlaceOfService.
/// </summary>
public enum PlaceOfService
{
    /// <summary>02 — telehealth, patient somewhere other than home.</summary>
    TelehealthOtherThanPatientHome = 2,

    /// <summary>03 — a school or early-intervention setting.</summary>
    School = 3,

    /// <summary>10 — telehealth, patient at home.</summary>
    TelehealthPatientHome = 10,

    /// <summary>11 — an office. The practice has none today.</summary>
    Office = 11,

    /// <summary>12 — the child's home. The ordinary case for this practice.</summary>
    Home = 12,

    /// <summary>99 — anywhere CMS has no code for.</summary>
    Other = 99,
}

public enum PaymentStatus
{
    /// <summary>Money is still expected.</summary>
    Unpaid = 1,

    PartiallyPaid = 2,

    Paid = 3,

    /// <summary>
    /// The practice is not charging. Distinct from Unpaid, which still owes: a waived
    /// charge that read as unpaid would sit on an aged-debt list forever.
    /// </summary>
    Waived = 4,
}

/// <summary>
/// How the family paid, recorded for the practice's own books.
///
/// **No card details behind any of these.** presearch §17: payment happens externally and
/// this system stays outside PCI scope.
/// </summary>
public enum PaymentMethod
{
    Cash = 1,
    Check = 2,
    Card = 3,
    BankTransfer = 4,
    Other = 5,
}
