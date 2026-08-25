using Practice.Domain.Billing;

namespace Practice.Domain.Tests;

/// <summary>
/// The billable record of a visit, tested with no database and no framework.
///
/// The entity ships now with no feature attached to it (CLAUDE.md scope ledger): adding a
/// billing table to a live clinical database later means backfilling every historical
/// appointment. What is being pinned here is therefore not a workflow — there is no
/// endpoint and no screen — but the SHAPE a superbill will need, and the invariants that
/// would be expensive to discover after a year of rows exist.
///
/// SYNTHETIC DATA ONLY. Every code, charge and date below is invented.
/// </summary>
public sealed class EncounterTests
{
    private const long Provider = 7;
    private const long Patient = 21;
    private const long Appointment = 34;

    /// <summary>
    /// 01:30 UTC on 10 March 2026 is 20:30 on 9 March in America/New_York.
    ///
    /// An evening visit is the ordinary case for an in-home paediatric practice, so the
    /// date of service and the UTC date disagree on most weekday evenings — not on an
    /// edge case somebody has to go looking for.
    /// </summary>
    private static readonly DateTime EveningVisitUtc =
        new(2026, 3, 10, 1, 30, 0, DateTimeKind.Utc);

    private static Encounter Record(
        string cptCode = "92507",
        PlaceOfService placeOfService = PlaceOfService.Home,
        short units = 1,
        decimal chargeAmount = 150m,
        string? modifiers = "GN",
        DateTime? serviceStartUtc = null) =>
        Encounter.Record(
            Provider, Patient, Appointment, Provider,
            serviceStartUtc ?? EveningVisitUtc,
            cptCode, placeOfService, units, chargeAmount, modifiers);

    // ------------------------------------------------------------- date of service

    /// <summary>
    /// A date of service is a CALENDAR fact in the practice's own timezone, not a UTC
    /// instant sliced at midnight Greenwich.
    ///
    /// This is the one place the "store UTC" convention does not settle the question, and
    /// getting it wrong is invisible: a 7pm session would be billed as the following day
    /// on every superbill the practice ever issues, and a parent's insurer would compare
    /// that date against a claim that says otherwise.
    ///
    /// Control: Encounter.Record — the PracticeTime.LocalDateOf(serviceStartUtc) call.
    /// Replaced with DateOnly.FromDateTime(serviceStartUtc) → red, "Assert.Equal()
    /// Failure: Values differ / Expected: 3/9/2026 / Actual: 3/10/2026".
    /// </summary>
    [Fact]
    public void The_date_of_service_is_the_practice_local_calendar_date_of_the_visit()
    {
        var encounter = Record();

        Assert.Equal(new DateOnly(2026, 3, 9), encounter.ServiceDate);
    }

    /// <summary>
    /// The assertion is on ParamName rather than merely on the throw, and that is not
    /// decoration: PracticeTime.LocalDateOf refuses a non-UTC instant too, so deleting this
    /// guard still throws — about a parameter the caller has never heard of. A test
    /// asserting only that SOMETHING was thrown would have stayed green here, which is
    /// D077's two-clauses-covering-for-each-other shape.
    ///
    /// Control: Encounter.Record — the serviceStartUtc.Kind check.
    /// Deleted → red, "Assert.Equal() Failure: Strings differ / Expected:
    /// "serviceStartUtc" / Actual: "instantUtc".
    /// </summary>
    [Fact]
    public void A_visit_time_that_is_not_utc_is_refused()
    {
        var unspecified = new DateTime(2026, 3, 10, 1, 30, 0, DateTimeKind.Unspecified);

        var error = Assert.Throws<ArgumentException>(
            () => Record(serviceStartUtc: unspecified));

        Assert.Equal("serviceStartUtc", error.ParamName);
    }

    // ------------------------------------------------------------- coding

    /// <summary>
    /// Control: Encounter.Record — the CptCode5() regex check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("9250")]      // four characters
    [InlineData("925077")]    // six
    [InlineData("925-7")]     // punctuation
    [InlineData("")]
    public void A_procedure_code_that_is_not_five_characters_is_refused(string cptCode)
    {
        var error = Assert.Throws<ArgumentException>(() => Record(cptCode: cptCode));

        Assert.Equal("cptCode", error.ParamName);
    }

    /// <summary>
    /// The stored value IS the CMS place-of-service code, so a superbill prints it without
    /// a lookup table — and an integer cast into the enum type is not checked by the
    /// runtime, so 99 would otherwise persist as a place of service nothing can read.
    ///
    /// Control: Encounter.Record — the Enum.IsDefined(placeOfService) check.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_place_of_service_outside_the_cms_set_is_refused()
    {
        var error = Assert.Throws<ArgumentException>(
            () => Record(placeOfService: (PlaceOfService)77));

        Assert.Equal("placeOfService", error.ParamName);
    }

    /// <summary>
    /// The enum's VALUES are the CMS place-of-service codes, not an arbitrary 1, 2, 3.
    ///
    /// That is the whole reason the enum can be both the validation and the printed value:
    /// a superbill writes the stored tinyint as two digits and is done. Renumbering these
    /// for tidiness would silently reprint every historical encounter as a different place
    /// of service, so the numbers are asserted rather than assumed.
    ///
    /// This test names no control. It pins constants whose values are the point.
    /// </summary>
    [Fact]
    public void The_place_of_service_values_are_the_cms_codes()
    {
        Assert.Equal(2, (int)PlaceOfService.TelehealthOtherThanPatientHome);
        Assert.Equal(3, (int)PlaceOfService.School);
        Assert.Equal(10, (int)PlaceOfService.TelehealthPatientHome);
        Assert.Equal(11, (int)PlaceOfService.Office);
        Assert.Equal(12, (int)PlaceOfService.Home);
        Assert.Equal(99, (int)PlaceOfService.Other);
    }

    /// <summary>
    /// Control: Encounter.Record — the `units &lt; 1 || units &gt; MaxUnits` guard.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)-1)]
    [InlineData((short)17)]
    public void A_line_must_bill_between_one_unit_and_a_full_day(short units)
    {
        var error = Assert.Throws<ArgumentException>(() => Record(units: units));

        Assert.Equal("units", error.ParamName);
    }

    /// <summary>
    /// Control: Encounter.Record — the ModifierList() regex check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("G")]
    [InlineData("GN 95")]
    [InlineData("GN,95,GT,59,KX")]
    public void A_modifier_list_that_is_not_two_character_codes_is_refused(string modifiers)
    {
        var error = Assert.Throws<ArgumentException>(() => Record(modifiers: modifiers));

        Assert.Equal("modifiers", error.ParamName);
    }

    // ------------------------------------------------------------- diagnoses

    /// <summary>
    /// Order is a clinical claim, not a set: the first code is the primary reason for the
    /// encounter. That is why diagnoses are rows with a sequence rather than a delimited
    /// column, which can store an order it cannot enforce.
    ///
    /// Control: Encounter.AddDiagnosis — the (short)(_diagnoses.Count + 1) sequence.
    /// Replaced with a constant 1 → red, "Assert.Collection() Failure: Item comparison
    /// failure … Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 1".
    /// </summary>
    [Fact]
    public void Diagnoses_are_numbered_in_the_order_they_are_recorded()
    {
        var encounter = Record();

        encounter.AddDiagnosis("F80.2");
        encounter.AddDiagnosis("F80.1");

        Assert.Collection(
            encounter.Diagnoses,
            first => { Assert.Equal("F80.2", first.Code); Assert.Equal(1, first.Sequence); },
            second => { Assert.Equal("F80.1", second.Code); Assert.Equal(2, second.Sequence); });
    }

    /// <summary>
    /// Control: Encounter.AddDiagnosis — the duplicate check over _diagnoses.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void The_same_diagnosis_cannot_be_recorded_twice_on_one_encounter()
    {
        var encounter = Record();
        encounter.AddDiagnosis("F80.2");

        // Case differs; the code does not. A superbill carrying F80.2 twice is a coding
        // error a payer rejects, and normalising to upper case is what makes them equal.
        Assert.Throws<InvalidOperationException>(() => encounter.AddDiagnosis("f80.2"));
    }

    /// <summary>
    /// Control: Encounter.AddDiagnosis — the MaxDiagnoses guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_line_carries_at_most_four_diagnosis_pointers()
    {
        var encounter = Record();
        encounter.AddDiagnosis("F80.0");
        encounter.AddDiagnosis("F80.1");
        encounter.AddDiagnosis("F80.2");
        encounter.AddDiagnosis("F80.4");

        Assert.Throws<InvalidOperationException>(() => encounter.AddDiagnosis("R62.50"));
    }

    /// <summary>
    /// The shape is checked; membership in the ICD-10-CM code set is NOT — sourcing that
    /// set is presearch §16 future research, and a shape check that pretends to be a
    /// validity check is worse than one that says what it is.
    ///
    /// Control: Encounter.AddDiagnosis — the Icd10Cm() regex check.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was
    /// thrown / Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("F8")]         // too short
    [InlineData("FF0.2")]      // second character must be a digit
    [InlineData("F80.")]       // trailing separator, no subcategory
    [InlineData("F80.23456")]  // more than four characters after the point
    public void A_diagnosis_code_that_is_not_icd10_shaped_is_refused(string code)
    {
        var encounter = Record();

        var error = Assert.Throws<ArgumentException>(() => encounter.AddDiagnosis(code));

        Assert.Equal("icd10Code", error.ParamName);
    }

    // ------------------------------------------------------------- the superbill

    /// <summary>
    /// A superbill with no diagnosis is a document a parent cannot submit — the payer's
    /// first question is what the service treated.
    ///
    /// Control: Encounter.MarkSuperbillGenerated — the _diagnoses.Count guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_superbill_cannot_be_generated_for_an_encounter_with_no_diagnosis()
    {
        var encounter = Record();

        Assert.Throws<InvalidOperationException>(
            () => encounter.MarkSuperbillGenerated(EveningVisitUtc));
    }

    /// <summary>
    /// ClinicalNoteId is the note version this encounter was CODED FROM, and it is set
    /// once.
    ///
    /// A signed note is amended by inserting a new row (docs/DATA_MODEL.md), so a pointer
    /// that followed the current version would silently stop answering "what did the
    /// clinician read when they chose this code" — which is the only question it exists
    /// to answer. The current note is always reachable through AppointmentId.
    ///
    /// Control: Encounter.LinkClinicalNote — the already-linked guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void An_encounter_is_never_repointed_at_a_later_note_version()
    {
        var encounter = Record();
        encounter.LinkClinicalNote(101);

        Assert.Throws<InvalidOperationException>(() => encounter.LinkClinicalNote(102));
        Assert.Equal(101, encounter.ClinicalNoteId);
    }

    // ------------------------------------------------------------- payment

    /// <summary>
    /// Control: Encounter.RecordPayment — the AmountPaid >= ChargeAmount comparison.
    /// Replaced with `AmountPaid > 0` → red, "Assert.Equal() Failure: Values differ /
    /// Expected: PartiallyPaid / Actual: Paid".
    /// </summary>
    [Fact]
    public void A_part_payment_leaves_the_balance_outstanding()
    {
        var encounter = Record(chargeAmount: 150m);

        encounter.RecordPayment(50m, PaymentMethod.Check, EveningVisitUtc);

        Assert.Equal(PaymentStatus.PartiallyPaid, encounter.PaymentStatus);
        Assert.Equal(50m, encounter.AmountPaid);
        Assert.Equal(EveningVisitUtc, encounter.PaidAtUtc);
    }

    /// <summary>
    /// Payments accumulate. Two half payments settle the charge; the second does not
    /// replace the first.
    ///
    /// Control: Encounter.RecordPayment — the `AmountPaid += amount` accumulation.
    /// Replaced with `AmountPaid = amount` → red, "Assert.Equal() Failure: Values differ
    /// / Expected: 150 / Actual: 75".
    /// </summary>
    [Fact]
    public void Payments_accumulate_until_the_charge_is_settled()
    {
        var encounter = Record(chargeAmount: 150m);

        encounter.RecordPayment(75m, PaymentMethod.Cash, EveningVisitUtc);
        encounter.RecordPayment(75m, PaymentMethod.Cash, EveningVisitUtc.AddDays(7));

        Assert.Equal(150m, encounter.AmountPaid);
        Assert.Equal(PaymentStatus.Paid, encounter.PaymentStatus);
    }

    /// <summary>
    /// Control: Encounter.WaiveCharge — the `AmountPaid > 0` guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_charge_that_has_been_part_paid_cannot_then_be_waived()
    {
        var encounter = Record(chargeAmount: 150m);
        encounter.RecordPayment(50m, PaymentMethod.Card, EveningVisitUtc);

        Assert.Throws<InvalidOperationException>(encounter.WaiveCharge);
    }

    /// <summary>
    /// Control: Encounter.RecordPayment — the PaymentStatus.Waived guard.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown / Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_waived_charge_cannot_then_take_a_payment()
    {
        var encounter = Record();
        encounter.WaiveCharge();

        Assert.Throws<InvalidOperationException>(
            () => encounter.RecordPayment(150m, PaymentMethod.Cash, EveningVisitUtc));
    }

    /// <summary>
    /// No card number, no last four digits, no processor reference — presearch §17 keeps
    /// this practice outside PCI scope, and a column is how that decision gets reversed by
    /// accident.
    ///
    /// This asserts on the SHAPE of the type rather than on behaviour, because the claim
    /// is about what does not exist. It fails the moment somebody adds a property whose
    /// name looks like card data.
    /// </summary>
    [Fact]
    public void An_encounter_carries_no_card_data()
    {
        var names = typeof(Encounter).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain(names, n => n.Contains("Card", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Pan", StringComparison.Ordinal)
            || n.Contains("LastFour", StringComparison.OrdinalIgnoreCase));
    }
}
