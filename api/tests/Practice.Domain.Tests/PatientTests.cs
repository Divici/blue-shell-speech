using Practice.Domain.Patients;

namespace Practice.Domain.Tests;

/// <summary>
/// Patient invariants, with no database and no framework.
///
/// These are the rules that protect a child's record. They run in milliseconds, so there
/// is no reason for any of them to be checked only at the API boundary.
/// </summary>
public sealed class PatientTests
{
    private static readonly DateOnly Today = new(2026, 8, 24);

    private static Patient CreateValid(DateOnly? dob = null) =>
        Patient.Create(
            providerId: 1,
            firstName: "Maya",
            lastName: "Reyes",
            dateOfBirth: dob ?? new DateOnly(2024, 2, 24),
            today: Today);

    [Fact]
    public void Create_requires_a_provider()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Patient.Create(0, "Maya", "Reyes", new DateOnly(2024, 2, 24), Today));

        Assert.Contains("provider", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_starts_active_with_a_public_id()
    {
        var patient = CreateValid();

        Assert.Equal(PatientStatus.Active, patient.Status);
        Assert.NotEqual(Guid.Empty, patient.PublicId);
        Assert.Null(patient.DischargedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_both_names(string blank)
    {
        Assert.Throws<ArgumentException>(() =>
            Patient.Create(1, blank, "Reyes", new DateOnly(2024, 2, 24), Today));
        Assert.Throws<ArgumentException>(() =>
            Patient.Create(1, "Maya", blank, new DateOnly(2024, 2, 24), Today));
    }

    [Fact]
    public void Create_rejects_a_future_date_of_birth()
    {
        Assert.Throws<ArgumentException>(() =>
            Patient.Create(1, "Maya", "Reyes", Today.AddDays(1), Today));
    }

    /// <summary>
    /// A 1900 birthdate is a typo. Left in, it silently distorts every age calculation —
    /// and in early intervention, age in months drives eligibility.
    /// </summary>
    [Fact]
    public void Create_rejects_an_implausible_date_of_birth()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Patient.Create(1, "Maya", "Reyes", new DateOnly(1900, 1, 1), Today));

        Assert.Contains("typo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_accepts_a_newborn()
    {
        var patient = CreateValid(Today);

        Assert.Equal(0, patient.AgeInMonths(Today));
    }

    // ------------------------------------------------------------------ age

    [Theory]
    [InlineData(2024, 2, 24, 2026, 8, 24, 30)]  // exactly 30 months
    [InlineData(2024, 2, 24, 2026, 8, 23, 29)]  // one day short of the 30th month
    [InlineData(2026, 8, 1, 2026, 8, 24, 0)]    // under a month old
    [InlineData(2021, 8, 24, 2026, 8, 24, 60)]  // the top of the served range
    public void AgeInMonths_counts_whole_months(
        int by, int bm, int bd, int ay, int am, int ad, int expected)
    {
        var patient = Patient.Create(1, "Maya", "Reyes", new DateOnly(by, bm, bd), new DateOnly(ay, am, ad));

        Assert.Equal(expected, patient.AgeInMonths(new DateOnly(ay, am, ad)));
    }

    // ------------------------------------------------------------ discharge

    /// <summary>
    /// Discharge, never delete. Notes must stay intact and attributable — retention
    /// obligations outlive the therapeutic relationship by years.
    /// </summary>
    [Fact]
    public void Discharge_marks_the_record_without_removing_it()
    {
        var patient = CreateValid();

        patient.Discharge();

        Assert.Equal(PatientStatus.Discharged, patient.Status);
        Assert.NotNull(patient.DischargedAtUtc);
        Assert.Equal("Maya", patient.FirstName);
    }

    [Fact]
    public void Reactivate_clears_the_discharge()
    {
        var patient = CreateValid();
        patient.Discharge();

        patient.Reactivate();

        Assert.Equal(PatientStatus.Active, patient.Status);
        Assert.Null(patient.DischargedAtUtc);
    }

    // ------------------------------------------------------------- guardians

    [Fact]
    public void AddGuardian_attaches_a_carer()
    {
        var patient = CreateValid();

        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        var guardian = Assert.Single(patient.Guardians);
        Assert.True(guardian.IsPrimaryContact);
        Assert.True(guardian.HasLegalAuthority);
    }

    /// <summary>
    /// Two "primary" contacts means whoever reads the record picks one — not a decision
    /// to make by accident in a custody situation.
    /// </summary>
    [Fact]
    public void AddGuardian_demotes_any_previous_primary_contact()
    {
        var patient = CreateValid();
        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        patient.AddGuardian("Sam", "Reyes", "Father", "410-555-0143", null, true, true);

        Assert.Single(patient.Guardians, g => g.IsPrimaryContact);
        Assert.Equal("Sam", patient.Guardians.Single(g => g.IsPrimaryContact).FirstName);
    }

    /// <summary>A primary contact with no phone and no email looks complete and is not.</summary>
    [Fact]
    public void A_primary_contact_must_be_contactable()
    {
        var patient = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            patient.AddGuardian("Jordan", "Reyes", "Mother", null, null, true, true));
    }

    [Fact]
    public void A_non_primary_guardian_may_have_no_contact_details()
    {
        var patient = CreateValid();

        patient.AddGuardian("Sam", "Reyes", "Father", null, null, false, false);

        Assert.Single(patient.Guardians);
    }

    /// <summary>
    /// Legal authority is independent of being the primary contact. The adult who brings
    /// the child is not always the adult entitled to the record.
    /// </summary>
    [Fact]
    public void Legal_authority_is_independent_of_being_the_primary_contact()
    {
        var patient = CreateValid();

        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, false);
        patient.AddGuardian("Sam", "Reyes", "Father", "410-555-0143", null, false, true);

        var primary = patient.Guardians.Single(g => g.IsPrimaryContact);
        var authorised = patient.Guardians.Single(g => g.HasLegalAuthority);

        Assert.NotEqual(primary.FirstName, authorised.FirstName);
    }

    [Fact]
    public void Clearing_contact_details_on_a_primary_contact_is_rejected()
    {
        var patient = CreateValid();
        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        Assert.Throws<InvalidOperationException>(() =>
            patient.Guardians[0].UpdateContact(null, null));
    }

    // ------------------------------------------------------------- addresses

    [Fact]
    public void AddAddress_records_a_session_address()
    {
        var patient = CreateValid();

        patient.AddAddress("14 Elm Street", null, "Towson", "md", "21204",
            AddressType.Session, "Gate code 4821", Today);

        var address = Assert.Single(patient.Addresses);
        Assert.Equal("MD", address.State);
        Assert.True(address.IsCurrent);
    }

    /// <summary>
    /// A move closes the previous address rather than overwriting it — a note recording a
    /// visit last year refers to where the family lived then.
    /// </summary>
    [Fact]
    public void A_new_address_supersedes_the_previous_one_of_the_same_type()
    {
        var patient = CreateValid();
        patient.AddAddress("14 Elm Street", null, "Towson", "MD", "21204",
            AddressType.Session, null, new DateOnly(2025, 1, 1));

        patient.AddAddress("8 Oak Lane", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        Assert.Equal(2, patient.Addresses.Count);
        Assert.Single(patient.Addresses, a => a.IsCurrent);
        Assert.Equal("8 Oak Lane", patient.Addresses.Single(a => a.IsCurrent).Line1);
    }

    [Fact]
    public void A_billing_address_does_not_supersede_a_session_address()
    {
        var patient = CreateValid();
        patient.AddAddress("14 Elm Street", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        patient.AddAddress("PO Box 12", null, "Towson", "MD", "21204",
            AddressType.Billing, null, Today);

        Assert.Equal(2, patient.Addresses.Count(a => a.IsCurrent));
    }

    [Fact]
    public void An_address_requires_a_two_letter_state()
    {
        var patient = CreateValid();

        Assert.Throws<ArgumentException>(() =>
            patient.AddAddress("14 Elm Street", null, "Towson", "Maryland", "21204",
                AddressType.Session, null, Today));
    }

    // --------------------------------------------------- editing a guardian

    /// <summary>
    /// Details change: people marry, move house, change a number.
    ///
    /// The edit goes through the aggregate rather than through the Guardian directly,
    /// because promoting one guardian has to demote another — an invariant spanning two
    /// entities, which therefore belongs to the root.
    ///
    /// Control: Patient.UpdateGuardian — the whole method.
    /// Deleted → the test project does not compile, "Patient does not contain a definition
    /// for UpdateGuardian".
    /// </summary>
    [Fact]
    public void UpdateGuardian_changes_an_existing_guardians_details()
    {
        var patient = CreateValid();
        var guardian = patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        var updated = patient.UpdateGuardian(
            guardian.PublicId, "Jordan", "Okafor", "Mother", "410-555-0155",
            "jordan.okafor@example.com", isPrimaryContact: true, hasLegalAuthority: true);

        Assert.NotNull(updated);
        Assert.Equal("Okafor", updated.LastName);
        Assert.Equal("410-555-0155", updated.Phone);
        Assert.Equal("jordan.okafor@example.com", updated.Email);
        Assert.Single(patient.Guardians);
    }

    /// <summary>
    /// Promoting on EDIT demotes the previous primary, exactly as promoting on ADD does.
    ///
    /// Without it the filtered unique index UX_Guardians_OnePrimaryPerPatient is the only
    /// thing between the record and two "primary" numbers — and it would report the
    /// problem as a database error instead of doing the demotion.
    ///
    /// Control: Patient.UpdateGuardian — the loop clearing the flag on the other
    /// guardians. Deleted → red on Assert.Single, "Assert.Single() Failure: The collection
    /// contained 2 matching items".
    /// </summary>
    [Fact]
    public void UpdateGuardian_promoting_one_guardian_demotes_the_previous_primary()
    {
        var patient = CreateValid();
        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, false);
        var father = patient.AddGuardian("Sam", "Reyes", "Father", "410-555-0143", null, false, false);

        patient.UpdateGuardian(
            father.PublicId, "Sam", "Reyes", "Father", "410-555-0143", null,
            isPrimaryContact: true, hasLegalAuthority: false);

        Assert.Single(patient.Guardians, g => g.IsPrimaryContact);
        Assert.Equal("Sam", patient.Guardians.Single(g => g.IsPrimaryContact).FirstName);
    }

    /// <summary>
    /// THE RULE THIS ENTITY EXISTS FOR, on the edit path.
    ///
    /// Legal authority is read from its own argument and from nothing else. A stepparent
    /// can be the primary contact with no authority to consent; a non-custodial parent can
    /// hold authority without being the contact. Releasing a record to the wrong adult is
    /// a breach, so "she is the one who brings him" must never become "she may have the
    /// file".
    ///
    /// Control: Patient.UpdateGuardian — the SetLegalAuthority(hasLegalAuthority) call.
    /// Deleted → red on the last assertion, "Assert.False() Failure / Expected: False /
    /// Actual: True" — i.e. the authority granted at creation survived a save that
    /// withdrew it.
    /// </summary>
    [Fact]
    public void Legal_authority_is_withdrawn_from_a_guardian_who_stays_the_primary_contact()
    {
        var patient = CreateValid();
        var guardian = patient.AddGuardian(
            "Jordan", "Reyes", "Mother", "410-555-0142", null, true, hasLegalAuthority: true);

        var updated = patient.UpdateGuardian(
            guardian.PublicId, "Jordan", "Reyes", "Mother", "410-555-0142", null,
            isPrimaryContact: true, hasLegalAuthority: false);

        Assert.NotNull(updated);
        Assert.True(updated.IsPrimaryContact);
        Assert.False(updated.HasLegalAuthority);
    }

    /// <summary>
    /// The same independence in the other direction: a guardian who stops being the
    /// contact keeps the authority nobody withdrew.
    ///
    /// Control: Patient.UpdateGuardian — the `if (!isPrimaryContact) ClearPrimaryContact()`
    /// branch. Deleted → red on IsPrimaryContact, "Assert.False() Failure / Expected:
    /// False / Actual: True".
    /// </summary>
    [Fact]
    public void A_guardian_who_stops_being_the_primary_contact_keeps_their_legal_authority()
    {
        var patient = CreateValid();
        var guardian = patient.AddGuardian(
            "Jordan", "Reyes", "Mother", "410-555-0142", null, true, hasLegalAuthority: true);

        var updated = patient.UpdateGuardian(
            guardian.PublicId, "Jordan", "Reyes", "Mother", "410-555-0142", null,
            isPrimaryContact: false, hasLegalAuthority: true);

        Assert.NotNull(updated);
        Assert.False(updated.IsPrimaryContact);
        Assert.True(updated.HasLegalAuthority);
    }

    /// <summary>
    /// A guardian id belonging to nobody on this patient resolves to null, which the
    /// endpoint turns into the same 404 an unreachable patient produces (D052).
    ///
    /// Control: Patient.UpdateGuardian — the g.PublicId == guardianPublicId predicate.
    /// Replaced with FirstOrDefault() → red on Assert.Null, "Assert.Null() Failure: Value
    /// is not null".
    /// </summary>
    [Fact]
    public void UpdateGuardian_finds_nothing_for_a_guardian_on_a_different_record()
    {
        var patient = CreateValid();
        patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        var result = patient.UpdateGuardian(
            Guid.NewGuid(), "Mallory", "Stranger", "Mother", "410-555-0199", null, false, true);

        Assert.Null(result);
        Assert.Equal("Jordan", patient.Guardians[0].FirstName);
    }

    /// <summary>
    /// Editing cannot reach the state Create() refuses: a primary contact with no way to
    /// be contacted. That record looks complete and is useless the first time a session
    /// has to move.
    ///
    /// Control: Guardian.UpdateContact — the IsPrimaryContact guard.
    /// Deleted → red on the SECOND assertion, "Assert.Equal() Failure: Strings differ /
    /// Expected: "410-555-0142" / Actual: null".
    ///
    /// Worth recording, because it is the shape D066 warns about: Assert.Throws still
    /// PASSED with the guard gone, because MakePrimaryContact carries a guard of its own
    /// and threw a line later — a second control covering for the first. What the deletion
    /// actually changes is that the number is wiped on the way past, which is why the
    /// closing assertion is here and not decoration.
    /// </summary>
    [Fact]
    public void UpdateGuardian_refuses_to_leave_the_primary_contact_uncontactable()
    {
        var patient = CreateValid();
        var guardian = patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        Assert.Throws<InvalidOperationException>(() => patient.UpdateGuardian(
            guardian.PublicId, "Jordan", "Reyes", "Mother", null, null,
            isPrimaryContact: true, hasLegalAuthority: true));

        Assert.Equal("410-555-0142", patient.Guardians[0].Phone);
    }

    /// <summary>
    /// Clearing the details of a guardian who is being demoted IS allowed — the rule is
    /// about the role, not about the person. This pins the order of operations inside
    /// UpdateGuardian: the flag moves before the details do.
    ///
    /// Control: Patient.UpdateGuardian — the demotion running BEFORE UpdateContact.
    /// Moved after it → red with an unexpected throw, "System.InvalidOperationException :
    /// The primary contact needs a phone number or an email address."
    /// </summary>
    [Fact]
    public void A_guardian_being_demoted_may_have_their_contact_details_cleared()
    {
        var patient = CreateValid();
        var guardian = patient.AddGuardian("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true);

        var updated = patient.UpdateGuardian(
            guardian.PublicId, "Jordan", "Reyes", "Mother", null, null,
            isPrimaryContact: false, hasLegalAuthority: true);

        Assert.NotNull(updated);
        Assert.Null(updated.Phone);
        Assert.False(updated.IsPrimaryContact);
    }

    // ------------------------------------------------- correcting an address

    /// <summary>
    /// A CORRECTION IS NOT A MOVE.
    ///
    /// AddAddress records a family living somewhere new and closes the previous row.
    /// CorrectAddress fixes a row written down wrong — the family never lived at the
    /// mistyped address, so there is no history to keep, and superseding would invent a
    /// move that never happened.
    ///
    /// Control: Patient.CorrectAddress — the whole method.
    /// Deleted → the test project does not compile, "Patient does not contain a definition
    /// for CorrectAddress".
    /// </summary>
    [Fact]
    public void CorrectAddress_fixes_the_row_rather_than_adding_one()
    {
        var patient = CreateValid();
        var address = patient.AddAddress("14 Elm Streat", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        var corrected = patient.CorrectAddress(
            address.PublicId, "14 Elm Street", null, "Towson", "md", "21204", "Gate code 4821");

        Assert.NotNull(corrected);
        Assert.Single(patient.Addresses);
        Assert.Equal("14 Elm Street", corrected.Line1);
        Assert.Equal("MD", corrected.State);
        Assert.Equal("Gate code 4821", corrected.Notes);
    }

    /// <summary>
    /// A correction changes WHERE, never WHICH or WHEN. The type decides what supersedes
    /// what and the dates decide which address a past visit happened at; neither is a typo
    /// anyone is fixing, and moving them would rewrite history underneath a note that
    /// already refers to it.
    ///
    /// Control: PatientAddress.Correct — the absence of AddressType and EffectiveFrom
    /// parameters. Given an AddressType parameter that assigns the field, and passed
    /// Billing → red on Assert.Equal, "Assert.Equal() Failure: Values differ / Expected:
    /// Session / Actual: Billing".
    /// </summary>
    [Fact]
    public void CorrectAddress_leaves_the_type_and_the_dates_alone()
    {
        var patient = CreateValid();
        var address = patient.AddAddress("14 Elm Street", null, "Towson", "MD", "21204",
            AddressType.Session, null, new DateOnly(2025, 1, 1));

        var corrected = patient.CorrectAddress(
            address.PublicId, "16 Elm Street", "Apt 2", "Towson", "MD", "21204", null);

        Assert.NotNull(corrected);
        Assert.Equal(AddressType.Session, corrected.AddressType);
        Assert.Equal(new DateOnly(2025, 1, 1), corrected.EffectiveFrom);
        Assert.Null(corrected.EffectiveTo);
    }

    /// <summary>
    /// A typo in an address the family has already left is still a typo. Fixing it must
    /// not resurrect that address as the current one.
    ///
    /// Control: PatientAddress.Correct — the absence of any write to EffectiveTo.
    /// Given EffectiveTo = null in the body → red on IsCurrent, "Assert.False() Failure /
    /// Expected: False / Actual: True".
    /// </summary>
    [Fact]
    public void Correcting_a_superseded_address_leaves_it_superseded()
    {
        var patient = CreateValid();
        var old = patient.AddAddress("14 Elm Streat", null, "Towson", "MD", "21204",
            AddressType.Session, null, new DateOnly(2025, 1, 1));
        patient.AddAddress("8 Oak Lane", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        var corrected = patient.CorrectAddress(
            old.PublicId, "14 Elm Street", null, "Towson", "MD", "21204", null);

        Assert.NotNull(corrected);
        Assert.False(corrected.IsCurrent);
        Assert.Single(patient.Addresses, a => a.IsCurrent);
        Assert.Equal("8 Oak Lane", patient.Addresses.Single(a => a.IsCurrent).Line1);
    }

    /// <summary>
    /// Control: Patient.CorrectAddress — the a.PublicId == addressPublicId predicate.
    /// Replaced with FirstOrDefault() → red on Assert.Null, "Assert.Null() Failure: Value
    /// is not null".
    /// </summary>
    [Fact]
    public void CorrectAddress_finds_nothing_for_an_address_on_a_different_record()
    {
        var patient = CreateValid();
        patient.AddAddress("14 Elm Street", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        var result = patient.CorrectAddress(
            Guid.NewGuid(), "1 Nowhere Road", null, "Towson", "MD", "21204", null);

        Assert.Null(result);
        Assert.Equal("14 Elm Street", patient.Addresses[0].Line1);
    }

    /// <summary>
    /// Control: PatientAddress.Correct — the two-letter state check.
    /// Deleted → red on Assert.Throws, "Assert.Throws() Failure: No exception was thrown".
    /// </summary>
    [Fact]
    public void A_corrected_address_still_requires_a_two_letter_state()
    {
        var patient = CreateValid();
        var address = patient.AddAddress("14 Elm Street", null, "Towson", "MD", "21204",
            AddressType.Session, null, Today);

        Assert.Throws<ArgumentException>(() => patient.CorrectAddress(
            address.PublicId, "14 Elm Street", null, "Towson", "Maryland", "21204", null));
    }
}
