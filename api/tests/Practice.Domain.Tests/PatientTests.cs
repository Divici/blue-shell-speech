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
}
