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
}

public enum PatientStatus
{
    Active = 1,

    /// <summary>On the caseload but not currently being seen — a break, a pause, a wait.</summary>
    Inactive = 2,

    /// <summary>Therapy has ended. The record is retained.</summary>
    Discharged = 3,
}
