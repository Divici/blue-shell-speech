using Practice.Domain.Common;

namespace Practice.Domain.Patients;

/// <summary>
/// Where a session happens, or where a bill goes.
///
/// Its own entity because a session address and a billing address differ, and because
/// families move — history matters when a note records a visit at a previous address.
///
/// NEVER sent to a mapping provider without an evaluated data flow (presearch §5.6). A
/// patient address plus a therapy appointment is PHI, and geocoding it hands both to a
/// third party.
/// </summary>
public sealed class PatientAddress : Entity
{
    private PatientAddress() { }

    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    public string Line1 { get; private set; } = string.Empty;

    public string? Line2 { get; private set; }

    public string City { get; private set; } = string.Empty;

    /// <summary>Two-letter state code, uppercase.</summary>
    public string State { get; private set; } = string.Empty;

    public string PostalCode { get; private set; } = string.Empty;

    public AddressType AddressType { get; private set; }

    /// <summary>Gate code, parking, "dog in the yard" — practical, not clinical.</summary>
    public string? Notes { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    /// <summary>Null means current.</summary>
    public DateOnly? EffectiveTo { get; private set; }

    public bool IsCurrent => EffectiveTo is null;

    internal static PatientAddress Create(
        long providerId,
        long patientId,
        string line1,
        string? line2,
        string city,
        string state,
        string postalCode,
        AddressType type,
        string? notes,
        DateOnly effectiveFrom)
    {
        var normalisedState = Guard.NotBlank(state, "state").ToUpperInvariant();
        if (normalisedState.Length != 2)
        {
            throw new ArgumentException("A state must be a two-letter code.", nameof(state));
        }

        return new PatientAddress
        {
            ProviderId = providerId,
            PatientId = patientId,
            Line1 = Guard.MaxLength(Guard.NotBlank(line1, "line1"), 200, "line1"),
            Line2 = string.IsNullOrWhiteSpace(line2) ? null : line2.Trim(),
            City = Guard.MaxLength(Guard.NotBlank(city, "city"), 100, "city"),
            State = normalisedState,
            PostalCode = Guard.MaxLength(Guard.NotBlank(postalCode, "postalCode"), 20, "postalCode"),
            AddressType = type,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            EffectiveFrom = effectiveFrom,
        };
    }

    /// <summary>Closes this address as of a date. The row is kept — history matters.</summary>
    internal void Supersede(DateOnly asOf)
    {
        if (asOf < EffectiveFrom)
        {
            throw new ArgumentException(
                "An address cannot end before it began.", nameof(asOf));
        }

        EffectiveTo = asOf;
    }
}

public enum AddressType
{
    /// <summary>Where therapy takes place.</summary>
    Session = 1,

    Billing = 2,
}
