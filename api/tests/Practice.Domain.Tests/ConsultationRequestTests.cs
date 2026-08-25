using Practice.Domain.Consultations;

namespace Practice.Domain.Tests;

/// <summary>
/// The public intake form's record, tested with no database and no framework.
///
/// This is the ONLY entity in the system created by somebody who is not authenticated, has
/// no account, and is not a patient. Every other aggregate is reached through a session;
/// this one is reached by anybody who can load a web page. The invariants here are
/// therefore not "a clinician might mistype" — they are the shape of the record when the
/// caller is assumed hostile.
///
/// SYNTHETIC DATA ONLY. Every name and description in this file is invented, and the
/// telephone numbers are in the 555-01xx range reserved for fiction.
/// </summary>
public sealed class ConsultationRequestTests
{
    private const long Provider = 7;

    private static readonly DateTime Submitted =
        new(2026, 8, 25, 14, 30, 0, DateTimeKind.Utc);

    /// <summary>A well-formed SHA-256 hex digest — the shape `hashClientId` produces.</summary>
    private const string Hash =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static ConsultationRequest Submit(
        string parentName = "Jordan Reyes",
        string email = "jordan@example.com",
        string? phone = "410-555-0142",
        string childFirstName = "Maya",
        short childAgeMonths = 30,
        string concerns = "She has about ten words but is not putting them together.",
        PreferredContactMethod preferredContact = PreferredContactMethod.Email,
        string? sourceIpHash = Hash) =>
        ConsultationRequest.Submit(
            Provider, parentName, email, phone, childFirstName, childAgeMonths,
            concerns, preferredContact, sourceIpHash, Submitted);

    // --------------------------------------------------------------- the record

    /// <summary>
    /// Control: the <c>= ConsultationStatus.New</c> initialiser on Status.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: New, Actual: 0" —
    /// an enum with no explicit start defaults to 0, which is not a member of this enum
    /// and would persist as a status nothing can read.
    /// </summary>
    [Fact]
    public void A_submitted_request_starts_as_new_and_converts_nothing()
    {
        var request = Submit();

        Assert.Equal(ConsultationStatus.New, request.Status);
        Assert.Null(request.ConvertedPatientId);
        Assert.Equal(Submitted, request.SubmittedAtUtc);
        Assert.NotEqual(Guid.Empty, request.PublicId);
    }

    /// <summary>
    /// Every row carries a provider from the moment it exists (CLAUDE.md conventions).
    ///
    /// A public submission has no session to take one from, so the API resolves it — and
    /// the aggregate refuses to exist without one rather than accepting a zero that would
    /// sit outside every tenancy filter in the system, visible to nobody and belonging to
    /// no one.
    ///
    /// Control: the providerId check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_request_cannot_exist_without_a_provider(long providerId)
    {
        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            providerId, "Jordan Reyes", "jordan@example.com", null, "Maya", 30,
            "Not combining words.", PreferredContactMethod.Email, null, Submitted));
    }

    /// <summary>
    /// Control: the <c>value.Trim()</c> Guard.NotBlank returns, which every required field
    /// here is passed through.
    /// Deleted → red, "Assert.Equal() Failure: Strings differ, Expected: \"Jordan Reyes\",
    /// Actual: \"  Jordan Reyes  \"".
    /// </summary>
    [Fact]
    public void Values_are_trimmed_so_a_stray_space_does_not_make_a_second_enquiry()
    {
        var request = Submit(parentName: "  Jordan Reyes  ", childFirstName: " Maya ");

        Assert.Equal("Jordan Reyes", request.ParentName);
        Assert.Equal("Maya", request.ChildFirstName);
    }

    /// <summary>
    /// Absent and blank are the same fact about a family, and storing both means every
    /// later reader has to test for two things.
    ///
    /// Control: the <c>IsNullOrWhiteSpace</c> early return in ConsultationRequest.Normalise.
    /// Deleted → red, "Assert.Null() Failure: Value is not null, Expected: null,
    /// Actual: \"\"".
    /// </summary>
    [Fact]
    public void An_empty_phone_number_is_stored_as_absent_rather_than_blank()
    {
        var request = Submit(phone: "   ", preferredContact: PreferredContactMethod.Email);

        Assert.Null(request.Phone);
    }

    // ----------------------------------------------------------- hostile input

    /// <summary>
    /// Every free-text field is bounded, and an over-long one is REFUSED rather than
    /// truncated.
    ///
    /// Truncation would silently discard the end of a parent's description of their child,
    /// which is the part carrying the specifics. Refusing is loud and the caller can fix
    /// it; truncating is quiet and nobody ever finds out.
    ///
    /// Control: the length check inside Guard.MaxLength, which Submit calls for every
    /// bounded field including the phone number reached through Normalise.
    /// Deleted → red on every case, "Assert.Throws() Failure: No exception was thrown,
    /// Expected: typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("parentName", 121)]
    [InlineData("email", 255)]
    [InlineData("phone", 33)]
    [InlineData("childFirstName", 61)]
    [InlineData("concerns", 2001)]
    public void An_over_long_field_is_refused(string field, int length)
    {
        var oversized = new string('x', length);

        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider,
            field == "parentName" ? oversized : "Jordan Reyes",
            field == "email" ? oversized : "jordan@example.com",
            field == "phone" ? oversized : "410-555-0142",
            field == "childFirstName" ? oversized : "Maya",
            30,
            field == "concerns" ? oversized : "Not combining words.",
            PreferredContactMethod.Email,
            null,
            Submitted));
    }

    /// <summary>
    /// Control: the blankness check inside Guard.NotBlank, which Submit calls for the
    /// parent's name, the email, the child's first name, and the concerns.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_required_field_is_refused(string blank)
    {
        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider, blank, "jordan@example.com", null, "Maya", 30,
            "Not combining words.", PreferredContactMethod.Email, null, Submitted));

        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider, "Jordan Reyes", blank, null, "Maya", 30,
            "Not combining words.", PreferredContactMethod.Email, null, Submitted));

        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider, "Jordan Reyes", "jordan@example.com", null, blank, 30,
            "Not combining words.", PreferredContactMethod.Email, null, Submitted));

        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider, "Jordan Reyes", "jordan@example.com", null, "Maya", 30,
            blank, PreferredContactMethod.Email, null, Submitted));
    }

    /// <summary>
    /// The age bound here is a SANITY bound, not the practice's population.
    ///
    /// "Birth to five" is a product rule that changes when the practice does, and it lives
    /// on the form, where the parent gets a sentence explaining it. What the aggregate
    /// refuses is a value that cannot describe a child at all — a negative number, or one
    /// past the age at which anybody is anyone's paediatric patient.
    ///
    /// Control: the childAgeMonths range check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData((short)-1)]
    [InlineData((short)217)]
    [InlineData(short.MaxValue)]
    public void An_age_that_cannot_describe_a_child_is_refused(short months)
    {
        Assert.Throws<ArgumentException>(() => Submit(childAgeMonths: months));
    }

    /// <summary>
    /// The companion to the theory above: it pins the bound as INCLUSIVE, which is the
    /// half a refusal test cannot state.
    ///
    /// Control: the <c>&gt;</c> in the childAgeMonths range check — the operator, not the
    /// check, because deleting the check outright leaves an acceptance test green (D070).
    /// Changed to <c>&gt;=</c> → red, "System.ArgumentException : An age in months must be
    /// between 0 and 216. (Parameter 'childAgeMonths')".
    /// </summary>
    [Theory]
    [InlineData((short)0)]
    [InlineData((short)216)]
    public void The_bounds_of_the_age_range_are_accepted(short months)
    {
        Assert.Equal(months, Submit(childAgeMonths: months).ChildAgeMonths);
    }

    /// <summary>
    /// The undefined enum value is what a hand-rolled request body produces: an integer
    /// cast into an enum type is not checked by the runtime, so `(PreferredContactMethod)99`
    /// is a legal value of that type and would persist as 99.
    ///
    /// Control: the Enum.IsDefined check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void A_contact_preference_outside_the_enum_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => Submit(preferredContact: (PreferredContactMethod)99));
    }

    /// <summary>
    /// Asking to be phoned without leaving a number is a record that looks complete and is
    /// not — the same rule Guardian holds for a primary contact.
    ///
    /// Control: the phone-required-for-phone-contact check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData(PreferredContactMethod.Phone)]
    [InlineData(PreferredContactMethod.Either)]
    public void Asking_to_be_reached_by_phone_requires_a_phone_number(
        PreferredContactMethod preference)
    {
        Assert.Throws<ArgumentException>(
            () => Submit(phone: null, preferredContact: preference));
    }

    // ------------------------------------------------------------ the source hash

    /// <summary>
    /// The source is stored HASHED, never raw (docs/DATA_MODEL.md).
    ///
    /// A raw address would turn this table into a log of who visited a paediatric
    /// speech-therapy site. The hash answers the only question the practice has of it —
    /// "did these twelve enquiries come from one place" — and answers nothing else.
    ///
    /// The aggregate cannot verify that what it is handed is a hash rather than an
    /// address, but it can refuse anything not SHAPED like one, which stops a caller
    /// passing the address straight through.
    ///
    /// Control: the sourceIpHash format check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")]
    [InlineData("9f86d081")]
    [InlineData("zzzzd081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void A_source_that_is_not_shaped_like_a_hash_is_refused(string notAHash)
    {
        Assert.Throws<ArgumentException>(() => Submit(sourceIpHash: notAHash));
    }

    /// <summary>
    /// Absent is a legitimate answer: not every deployment sits behind a proxy.
    ///
    /// Control: the <c>IsNullOrWhiteSpace</c> early return in NormaliseHash. Deleting it
    /// alone does not compile — the nullable analyser catches the dereference — so the
    /// deletion was run with <c>sourceIpHash!.Trim()</c> in its place, which is what the
    /// guard is standing in front of.
    /// Deleted → red, "System.NullReferenceException : Object reference not set to an
    /// instance of an object."
    /// </summary>
    [Fact]
    public void A_missing_source_hash_is_allowed()
    {
        Assert.Null(Submit(sourceIpHash: null).SourceIpHash);
    }

    // ------------------------------------------------------------------ triage

    /// <summary>
    /// Control: the <c>Status = ConsultationStatus.Contacted</c> assignment in MarkContacted.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Contacted,
    /// Actual: New".
    /// </summary>
    [Fact]
    public void Marking_contacted_is_idempotent()
    {
        var request = Submit();

        request.MarkContacted();
        request.MarkContacted();

        Assert.Equal(ConsultationStatus.Contacted, request.Status);
    }

    /// <summary>
    /// Converting records WHICH patient the enquiry became, in the same operation that
    /// changes the status.
    ///
    /// Two fields that must agree are one operation, or a caller sets the status, forgets
    /// the id, and the row says a patient exists without saying who.
    ///
    /// Control: the ConvertedPatientId assignment in ConsultationRequest.ConvertTo.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 41, Actual: null".
    /// </summary>
    [Fact]
    public void Converting_records_the_patient_it_became()
    {
        var request = Submit();

        request.ConvertTo(41);

        Assert.Equal(ConsultationStatus.Converted, request.Status);
        Assert.Equal(41, request.ConvertedPatientId);
    }

    /// <summary>
    /// A converted enquiry is a child on the caseload. Declining it afterwards would leave
    /// the practice's own record contradicting a real clinical one.
    ///
    /// Control: the Converted guard in ConsultationRequest.Decline.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_converted_enquiry_cannot_be_declined()
    {
        var request = Submit();
        request.ConvertTo(41);

        Assert.Throws<InvalidOperationException>(request.Decline);
    }

    /// <summary>
    /// Control: the closed-status guard in ConsultationRequest.ConvertTo.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.InvalidOperationException)".
    /// </summary>
    [Fact]
    public void A_declined_enquiry_cannot_be_converted()
    {
        var request = Submit();
        request.Decline();

        Assert.Throws<InvalidOperationException>(() => request.ConvertTo(41));
    }

    /// <summary>
    /// Control: the patientId check in ConsultationRequest.ConvertTo.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Fact]
    public void Converting_needs_a_real_patient()
    {
        Assert.Throws<ArgumentException>(() => Submit().ConvertTo(0));
    }

    /// <summary>
    /// UTC or nothing, the same rule Appointment holds.
    ///
    /// A DateTime with Kind Unspecified reaching here means a caller parsed a local time
    /// and lost the offset — undetectable afterwards, and this row is what tells Michelle
    /// how long a parent has been waiting for a reply.
    ///
    /// Control: the submittedAtUtc Kind check in ConsultationRequest.Submit.
    /// Deleted → red, "Assert.Throws() Failure: No exception was thrown, Expected:
    /// typeof(System.ArgumentException)".
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void A_submission_time_that_is_not_utc_is_refused(DateTimeKind kind)
    {
        var notUtc = DateTime.SpecifyKind(new DateTime(2026, 8, 25, 14, 30, 0), kind);

        Assert.Throws<ArgumentException>(() => ConsultationRequest.Submit(
            Provider, "Jordan Reyes", "jordan@example.com", null, "Maya", 30,
            "Not combining words.", PreferredContactMethod.Email, null, notUtc));
    }
}
