using System.Text.RegularExpressions;
using Practice.Domain.Common;

namespace Practice.Domain.Consultations;

/// <summary>
/// A parent's enquiry from the public site (presearch §4.1).
///
/// NOT PHI — the family are not patients and there is no treatment relationship — but it
/// is treated as PHI-adjacent and stored under the same controls (docs/DATA_MODEL.md). A
/// child's first name next to a parent's description of that child's difficulties is the
/// same category of information whatever the regulation calls it, and it becomes PHI the
/// moment Michelle acts on it.
///
/// THIS IS THE ONLY AGGREGATE CREATED BY AN UNAUTHENTICATED STRANGER. Everything else in
/// the system is reached through a session; this is reached by anyone who can load a web
/// page. Every bound below exists because the caller is assumed hostile, not because a
/// clinician might mistype — and every one of them REFUSES rather than truncating, so a
/// value that does not fit is a visible failure instead of a silently shortened record.
/// </summary>
public sealed partial class ConsultationRequest : Entity
{
    // EF Core materialisation only.
    private ConsultationRequest() { }

    /// <summary>
    /// Whose enquiry this is (CLAUDE.md conventions: every domain row carries one).
    ///
    /// A public submission has no session to take a provider from, so the API resolves it
    /// server-side and the caller never supplies one — a provider id in a public request
    /// body would be a visitor choosing whose records to write into. See
    /// ConsultationEndpoints for how it is resolved and why it refuses when the answer is
    /// ambiguous.
    /// </summary>
    public long ProviderId { get; private set; }

    /// <summary>
    /// When the PARENT sent it, which is not the same fact as when the row was created.
    ///
    /// They are the same instant today because the only writer is the form. Kept separate
    /// from Entity.CreatedAtUtc because "how long has this family been waiting" is a
    /// question about their act, and an enquiry taken over the phone and entered
    /// afterwards would answer the two differently.
    /// </summary>
    public DateTime SubmittedAtUtc { get; private set; }

    public string ParentName { get; private set; } = string.Empty;

    public string Email { get; private set; } = string.Empty;

    /// <summary>Optional unless the parent asked to be phoned.</summary>
    public string? Phone { get; private set; }

    /// <summary>First name only. The form never asks for a surname.</summary>
    public string ChildFirstName { get; private set; } = string.Empty;

    public short ChildAgeMonths { get; private set; }

    public string Concerns { get; private set; } = string.Empty;

    public PreferredContactMethod PreferredContactMethod { get; private set; }

    public ConsultationStatus Status { get; private set; } = ConsultationStatus.New;

    /// <summary>Set with the status, by ConvertTo, and never on its own.</summary>
    public long? ConvertedPatientId { get; private set; }

    /// <summary>
    /// SHA-256 of the submitting address, never the address (docs/DATA_MODEL.md).
    ///
    /// Spam correlation without retaining a visitor identifier: it answers "did these
    /// twelve enquiries come from one place" and nothing else. Null where the deployment
    /// cannot see a client address at all.
    /// </summary>
    public string? SourceIpHash { get; private set; }

    /// <summary>
    /// The practice serves birth to five; this is deliberately far wider.
    ///
    /// "Birth to five" is a product rule that changes when the practice does, and it lives
    /// on the form where the parent gets a sentence explaining it. What the aggregate
    /// refuses is a value that cannot describe a child at all.
    /// </summary>
    public const short MaxChildAgeMonths = 216;

    public const int MaxParentNameLength = 120;
    public const int MaxEmailLength = 254;
    public const int MaxPhoneLength = 32;
    public const int MaxChildFirstNameLength = 60;
    public const int MaxConcernsLength = 2000;

    public static ConsultationRequest Submit(
        long providerId,
        string parentName,
        string email,
        string? phone,
        string childFirstName,
        short childAgeMonths,
        string concerns,
        PreferredContactMethod preferredContactMethod,
        string? sourceIpHash,
        DateTime submittedAtUtc)
    {
        if (providerId <= 0)
        {
            throw new ArgumentException(
                "A consultation request needs a provider.", nameof(providerId));
        }

        /*
         * UTC or nothing, the same rule Appointment holds.
         *
         * A Kind of Unspecified reaching here means a caller parsed a local time and lost
         * the offset. This column is what tells Michelle how long a family has been
         * waiting for a reply, and a value that is wrong by four hours is not detectable
         * afterwards.
         */
        if (submittedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "A submission time must be UTC. Convert before calling.",
                nameof(submittedAtUtc));
        }

        if (!Enum.IsDefined(preferredContactMethod))
        {
            /*
             * An integer cast into an enum type is not checked by the runtime, so
             * (PreferredContactMethod)99 is a legal value of that type. Left alone it
             * persists as 99 and every future reader has to decide what that meant.
             */
            throw new ArgumentException(
                "That is not a way of being contacted.", nameof(preferredContactMethod));
        }

        if (childAgeMonths < 0 || childAgeMonths > MaxChildAgeMonths)
        {
            throw new ArgumentException(
                $"An age in months must be between 0 and {MaxChildAgeMonths}.",
                nameof(childAgeMonths));
        }

        var request = new ConsultationRequest
        {
            ProviderId = providerId,
            SubmittedAtUtc = submittedAtUtc,
            ParentName = Guard.MaxLength(
                Guard.NotBlank(parentName, "parentName"), MaxParentNameLength, "parentName"),
            Email = Guard.MaxLength(
                Guard.NotBlank(email, "email"), MaxEmailLength, "email"),
            Phone = Normalise(phone, MaxPhoneLength, "phone"),
            ChildFirstName = Guard.MaxLength(
                Guard.NotBlank(childFirstName, "childFirstName"),
                MaxChildFirstNameLength, "childFirstName"),
            ChildAgeMonths = childAgeMonths,
            Concerns = Guard.MaxLength(
                Guard.NotBlank(concerns, "concerns"), MaxConcernsLength, "concerns"),
            PreferredContactMethod = preferredContactMethod,
            SourceIpHash = NormaliseHash(sourceIpHash),
        };

        /*
         * Asking to be phoned without leaving a number is a record that looks complete and
         * is not — the same rule Guardian holds for a primary contact. Michelle reads this
         * row and picks up the phone; there has to be something to dial.
         */
        if (request.Phone is null
            && preferredContactMethod is PreferredContactMethod.Phone
                or PreferredContactMethod.Either)
        {
            throw new ArgumentException(
                "A request asking to be reached by phone needs a phone number.",
                nameof(phone));
        }

        return request;
    }

    /// <summary>
    /// Michelle has replied. Idempotent — a second reply is not a different state.
    /// </summary>
    public void MarkContacted()
    {
        if (Status is ConsultationStatus.Converted or ConsultationStatus.Declined)
        {
            throw new InvalidOperationException(
                "This enquiry has already been closed.");
        }

        Status = ConsultationStatus.Contacted;
    }

    /// <summary>
    /// The enquiry became a patient, and this records WHICH one.
    ///
    /// The status and the id are one operation because they must agree: a caller able to
    /// set the status alone would leave a row saying a patient exists without saying who,
    /// and no later reader could reconstruct it.
    /// </summary>
    public void ConvertTo(long patientId)
    {
        if (patientId <= 0)
        {
            throw new ArgumentException(
                "Converting an enquiry needs a patient.", nameof(patientId));
        }

        if (Status is ConsultationStatus.Converted or ConsultationStatus.Declined)
        {
            throw new InvalidOperationException(
                "This enquiry has already been closed.");
        }

        Status = ConsultationStatus.Converted;
        ConvertedPatientId = patientId;
    }

    /// <summary>
    /// Not going ahead — the family moved, or the practice is not the right fit.
    ///
    /// A CONVERTED enquiry cannot be declined. That child is on the caseload, and a row
    /// saying the practice declined them would contradict a clinical record that exists.
    /// </summary>
    public void Decline()
    {
        if (Status is ConsultationStatus.Converted)
        {
            throw new InvalidOperationException(
                "This enquiry became a patient. It cannot be declined afterwards.");
        }

        Status = ConsultationStatus.Declined;
    }

    private static string? Normalise(string? value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guard.MaxLength(value.Trim(), max, name);
    }

    /// <summary>
    /// Refuses anything not SHAPED like a SHA-256 digest.
    ///
    /// The aggregate cannot tell a hash from an address it has never seen — but it can
    /// tell that "203.0.113.7" is not 64 hex characters, and that check is what stops a
    /// caller passing the raw address straight through into the column whose whole purpose
    /// is that it is not there. Lowercase is required rather than normalised: two casings
    /// of the same digest would not correlate, which is the one thing this column is for.
    /// </summary>
    private static string? NormaliseHash(string? sourceIpHash)
    {
        if (string.IsNullOrWhiteSpace(sourceIpHash)) return null;

        var trimmed = sourceIpHash.Trim();

        if (!Sha256Hex().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "A source hash must be a lowercase SHA-256 hex digest. The raw address must "
                + "never be stored.",
                nameof(sourceIpHash));
        }

        return trimmed;
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();
}
