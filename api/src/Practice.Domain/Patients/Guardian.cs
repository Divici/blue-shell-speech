using Practice.Domain.Common;

namespace Practice.Domain.Patients;

/// <summary>
/// A parent or carer.
///
/// A separate entity, not columns on Patient. Children routinely have two parents at
/// different addresses, and the adult who brings the child is often not the one who
/// answers the phone.
/// </summary>
public sealed class Guardian : Entity
{
    private Guardian() { }

    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    /// <summary>Mother, Father, Grandparent, Legal guardian, Foster carer…</summary>
    public string Relationship { get; private set; } = string.Empty;

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    /// <summary>Who to call first. At most one per patient.</summary>
    public bool IsPrimaryContact { get; private set; }

    /// <summary>
    /// Whether this person may receive the child's records.
    ///
    /// SEPARATE FROM IsPrimaryContact, deliberately. The adult who brings the child to
    /// sessions is not always the adult entitled to the record. Custody disputes are not
    /// an edge case in paediatrics, and releasing a record to the wrong parent is a breach
    /// — so this is its own field, checked on every export or share path, never inferred.
    /// </summary>
    public bool HasLegalAuthority { get; private set; }

    internal static Guardian Create(
        long providerId,
        long patientId,
        string firstName,
        string lastName,
        string relationship,
        string? phone,
        string? email,
        bool isPrimaryContact,
        bool hasLegalAuthority)
    {
        var guardian = new Guardian
        {
            ProviderId = providerId,
            PatientId = patientId,
            FirstName = Guard.MaxLength(Guard.NotBlank(firstName, "firstName"), 100, "firstName"),
            LastName = Guard.MaxLength(Guard.NotBlank(lastName, "lastName"), 100, "lastName"),
            Relationship = Guard.MaxLength(
                Guard.NotBlank(relationship, "relationship"), 50, "relationship"),
            Phone = Normalise(phone, 50),
            Email = Normalise(email, 256),
            IsPrimaryContact = isPrimaryContact,
            HasLegalAuthority = hasLegalAuthority,
        };

        /*
         * A contactable guardian needs at least one way to be contacted.
         *
         * The primary contact is who Michelle calls when a session has to move. A primary
         * contact with no phone and no email is a record that looks complete and is not.
         */
        if (isPrimaryContact && guardian.Phone is null && guardian.Email is null)
        {
            throw new ArgumentException(
                "The primary contact needs a phone number or an email address.",
                nameof(isPrimaryContact));
        }

        return guardian;
    }

    /// <summary>People marry, and a relationship on a record can simply be wrong.</summary>
    public void Rename(string firstName, string lastName)
    {
        FirstName = Guard.MaxLength(Guard.NotBlank(firstName, "firstName"), 100, "firstName");
        LastName = Guard.MaxLength(Guard.NotBlank(lastName, "lastName"), 100, "lastName");
    }

    public void ChangeRelationship(string relationship) =>
        Relationship = Guard.MaxLength(
            Guard.NotBlank(relationship, "relationship"), 50, "relationship");

    /*
     * INTERNAL, not public.
     *
     * "Exactly one primary contact" spans every guardian on the patient, so only the root
     * can hold it — a caller who could promote a guardian directly would leave two primary
     * numbers on the record and discover it as a unique-index violation. Patient.AddGuardian
     * and Patient.UpdateGuardian are the only two callers, and both demote the others first.
     */
    internal void ClearPrimaryContact() => IsPrimaryContact = false;

    internal void MakePrimaryContact()
    {
        if (Phone is null && Email is null)
        {
            throw new InvalidOperationException(
                "The primary contact needs a phone number or an email address.");
        }

        IsPrimaryContact = true;
    }

    public void UpdateContact(string? phone, string? email)
    {
        var newPhone = Normalise(phone, 50);
        var newEmail = Normalise(email, 256);

        if (IsPrimaryContact && newPhone is null && newEmail is null)
        {
            throw new InvalidOperationException(
                "The primary contact needs a phone number or an email address.");
        }

        Phone = newPhone;
        Email = newEmail;
    }

    /// <summary>
    /// Grants or withdraws the right to receive this child's records.
    ///
    /// Takes the answer as an argument and derives it from nothing. It is deliberately NOT
    /// a side effect of MakePrimaryContact, and MakePrimaryContact is deliberately not a
    /// side effect of it: the two roles are decided by different facts about a family, and
    /// a UI or a caller that conflated them would release a record to the wrong adult.
    /// </summary>
    public void SetLegalAuthority(bool hasAuthority) => HasLegalAuthority = hasAuthority;

    private static string? Normalise(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Guard.MaxLength(value.Trim(), max, "value");
    }
}
