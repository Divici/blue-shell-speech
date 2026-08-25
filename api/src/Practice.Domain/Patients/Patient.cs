using Practice.Domain.Common;

namespace Practice.Domain.Patients;

/// <summary>
/// A child receiving therapy.
///
/// Every field here is PHI. The set is deliberately small: presearch §5.3 says store only
/// what the workflow requires, and every PHI column is one that must be protected,
/// audited, and justified in the risk analysis.
///
/// Deliberately absent: SSN, insurance member ID, race/ethnicity. This is a private-pay
/// practice with no claims submission. Do not add a field because an EHR would have one.
/// </summary>
public sealed class Patient : Entity
{
    private readonly List<Guardian> _guardians = [];
    private readonly List<PatientAddress> _addresses = [];

    private Patient() { }

    /// <summary>Tenancy discriminator, present from day one even at one provider.</summary>
    public long ProviderId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    /// <summary>
    /// Stored as a date, never an age.
    ///
    /// Age is computed at render. Storing it produces a row that is silently wrong from
    /// the next birthday onward — and in early intervention, age in months is the number
    /// every clinical decision hangs on.
    /// </summary>
    public DateOnly DateOfBirth { get; private set; }

    public PatientStatus Status { get; private set; } = PatientStatus.Active;

    /// <summary>Diagnosis context, precautions, relevant history. Free text, PHI.</summary>
    public string? ClinicalSummary { get; private set; }

    public DateTime? DischargedAtUtc { get; private set; }

    public IReadOnlyList<Guardian> Guardians => _guardians;

    public IReadOnlyList<PatientAddress> Addresses => _addresses;

    public static Patient Create(
        long providerId,
        string firstName,
        string lastName,
        DateOnly dateOfBirth,
        DateOnly today,
        string? clinicalSummary = null)
    {
        if (providerId <= 0)
        {
            throw new ArgumentException("A patient must belong to a provider.", nameof(providerId));
        }

        if (dateOfBirth > today)
        {
            throw new ArgumentException(
                "A date of birth cannot be in the future.", nameof(dateOfBirth));
        }

        /*
         * An implausibly old "child" is a data-entry error, not a patient.
         *
         * The practice serves birth to 5. A 1900 birthdate is a typo that would otherwise
         * sit in the record and quietly distort every age-based calculation.
         */
        if (dateOfBirth < today.AddYears(-25))
        {
            throw new ArgumentException(
                "That date of birth looks like a typo — this practice serves young children.",
                nameof(dateOfBirth));
        }

        return new Patient
        {
            ProviderId = providerId,
            FirstName = Guard.MaxLength(Guard.NotBlank(firstName, "firstName"), 100, "firstName"),
            LastName = Guard.MaxLength(Guard.NotBlank(lastName, "lastName"), 100, "lastName"),
            DateOfBirth = dateOfBirth,
            ClinicalSummary = string.IsNullOrWhiteSpace(clinicalSummary)
                ? null
                : clinicalSummary.Trim(),
        };
    }

    /// <summary>Age in whole months — the unit early-intervention eligibility uses.</summary>
    public int AgeInMonths(DateOnly asOf)
    {
        var months = ((asOf.Year - DateOfBirth.Year) * 12) + asOf.Month - DateOfBirth.Month;
        if (asOf.Day < DateOfBirth.Day) months--;
        return Math.Max(0, months);
    }

    public void Rename(string firstName, string lastName)
    {
        FirstName = Guard.MaxLength(Guard.NotBlank(firstName, "firstName"), 100, "firstName");
        LastName = Guard.MaxLength(Guard.NotBlank(lastName, "lastName"), 100, "lastName");
    }

    public void UpdateClinicalSummary(string? summary) =>
        ClinicalSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();

    /// <summary>
    /// Discharge, not delete.
    ///
    /// Clinical rows are never hard-deleted (docs/DATA_MODEL.md). A discharged patient's
    /// notes must remain intact and attributable — retention obligations outlive the
    /// therapeutic relationship by years.
    /// </summary>
    public void Discharge()
    {
        Status = PatientStatus.Discharged;
        DischargedAtUtc = DateTime.UtcNow;
    }

    public void Reactivate()
    {
        Status = PatientStatus.Active;
        DischargedAtUtc = null;
    }

    public void SetInactive() => Status = PatientStatus.Inactive;

    public Guardian AddGuardian(
        string firstName,
        string lastName,
        string relationship,
        string? phone,
        string? email,
        bool isPrimaryContact,
        bool hasLegalAuthority)
    {
        var guardian = Guardian.Create(
            ProviderId, Id, firstName, lastName, relationship,
            phone, email, isPrimaryContact, hasLegalAuthority);

        /*
         * At most one primary contact. Two "primary" numbers means whoever reads the
         * record picks one, and in a custody situation that is not a coin flip anyone
         * should be making by accident.
         */
        if (isPrimaryContact)
        {
            foreach (var existing in _guardians)
            {
                existing.ClearPrimaryContact();
            }
        }

        _guardians.Add(guardian);
        return guardian;
    }

    /// <summary>
    /// Edits a guardian already on this record. Null means there is no such guardian here —
    /// which the API turns into the same 404 an unreachable patient produces (D052).
    ///
    /// On the root rather than on Guardian because promoting one demotes another, and that
    /// invariant spans every guardian on the patient.
    ///
    /// <b>Legal authority is set from its own argument and from nothing else.</b> It is not
    /// implied by becoming the primary contact and it is not withdrawn by ceasing to be
    /// one. A stepparent can be the contact with no authority to consent; a non-custodial
    /// parent can hold authority without being the contact. Releasing a record to the wrong
    /// adult is a breach, so the two questions stay separate all the way down.
    /// </summary>
    public Guardian? UpdateGuardian(
        Guid guardianPublicId,
        string firstName,
        string lastName,
        string relationship,
        string? phone,
        string? email,
        bool isPrimaryContact,
        bool hasLegalAuthority)
    {
        var guardian = _guardians.SingleOrDefault(g => g.PublicId == guardianPublicId);
        if (guardian is null) return null;

        guardian.Rename(firstName, lastName);
        guardian.ChangeRelationship(relationship);

        /*
         * THE ORDER HERE IS LOAD-BEARING, in both directions.
         *
         * Demotion happens BEFORE the contact details change, so a guardian who is no
         * longer the person Michelle calls may have their number cleared — the rule is
         * about the role, not the person.
         *
         * Promotion happens AFTER, so MakePrimaryContact judges the details being SAVED
         * rather than the ones being replaced. Reversed, adding a phone number to a
         * guardian at the moment of promoting them would be refused for having none.
         */
        if (!isPrimaryContact)
        {
            guardian.ClearPrimaryContact();
        }

        guardian.UpdateContact(phone, email);

        if (isPrimaryContact)
        {
            foreach (var existing in _guardians)
            {
                if (!ReferenceEquals(existing, guardian)) existing.ClearPrimaryContact();
            }

            guardian.MakePrimaryContact();
        }

        guardian.SetLegalAuthority(hasLegalAuthority);

        return guardian;
    }

    public PatientAddress AddAddress(
        string line1,
        string? line2,
        string city,
        string state,
        string postalCode,
        AddressType type,
        string? notes,
        DateOnly effectiveFrom)
    {
        var address = PatientAddress.Create(
            ProviderId, Id, line1, line2, city, state, postalCode, type, notes, effectiveFrom);

        // Only one current address of each type; the previous one is closed, not removed.
        foreach (var existing in _addresses.Where(a => a.AddressType == type && a.IsCurrent))
        {
            existing.Supersede(effectiveFrom);
        }

        _addresses.Add(address);
        return address;
    }

    /// <summary>
    /// Fixes an address that was typed wrong. Null means there is no such address here.
    ///
    /// The counterpart to AddAddress, and the distinction is the whole point: AddAddress
    /// says <i>they live somewhere else now</i> and closes the previous row; this says
    /// <i>we wrote it down wrong</i> and touches one row in place. Using AddAddress for a
    /// typo would leave the mistyped address on the record as somewhere the family used to
    /// live; using this for a move would erase where they lived when last year's visits
    /// happened.
    ///
    /// Works on a superseded row too — a typo in an old address is still a typo — and
    /// leaves it superseded, because Correct writes neither EffectiveFrom nor EffectiveTo.
    /// </summary>
    public PatientAddress? CorrectAddress(
        Guid addressPublicId,
        string line1,
        string? line2,
        string city,
        string state,
        string postalCode,
        string? notes)
    {
        var address = _addresses.SingleOrDefault(a => a.PublicId == addressPublicId);
        if (address is null) return null;

        address.Correct(line1, line2, city, state, postalCode, notes);
        return address;
    }
}

public enum PatientStatus
{
    Active = 1,

    /// <summary>On the caseload but not currently being seen — a break, a pause, a wait.</summary>
    Inactive = 2,

    /// <summary>Therapy has ended. The record is retained.</summary>
    Discharged = 3,
}
