using System.Text.RegularExpressions;
using Practice.Domain.Common;

namespace Practice.Domain.Providers;

/// <summary>
/// The clinician.
///
/// Kept separate from ASP.NET Core Identity's own tables rather than extending
/// IdentityUser. Identity owns credentials, MFA secrets, and lockout state; this owns the
/// professional identity that appears on a clinical record. Keeping them apart means an
/// Identity upgrade never touches clinical tables, and a clinical migration never risks
/// the login path (docs/DATA_MODEL.md).
/// </summary>
public sealed partial class Provider : Entity
{
    // EF Core materialisation only.
    private Provider() { }

    /// <summary>Links to AspNetUsers.Id. Not a foreign key across the boundary.</summary>
    public string IdentityUserId { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>e.g. "M.S., CCC-SLP". Displayed on notes and superbills.</summary>
    public string Credentials { get; private set; } = string.Empty;

    /// <summary>National Provider Identifier. Optional — superbills are sequenced later.</summary>
    public string? Npi { get; private set; }

    public string LicenseNumber { get; private set; } = string.Empty;

    /// <summary>Two-letter state code, uppercase.</summary>
    public string LicenseState { get; private set; } = string.Empty;

    public bool IsActive { get; private set; } = true;

    public static Provider Create(
        string identityUserId,
        string displayName,
        string credentials,
        string licenseNumber,
        string licenseState)
    {
        var provider = new Provider
        {
            IdentityUserId = Guard.MaxLength(
                Guard.NotBlank(identityUserId, "identityUserId"), 450, "identityUserId"),
            DisplayName = Guard.MaxLength(
                Guard.NotBlank(displayName, "displayName"), 200, "displayName"),
            Credentials = Guard.MaxLength(
                Guard.NotBlank(credentials, "credentials"), 100, "credentials"),
            LicenseNumber = Guard.MaxLength(
                Guard.NotBlank(licenseNumber, "licenseNumber"), 50, "licenseNumber"),
            LicenseState = NormaliseState(licenseState),
        };

        return provider;
    }

    /// <summary>
    /// Sets or clears the NPI.
    ///
    /// Validated to exactly ten digits because it ends up on a superbill the parent
    /// submits to their insurer (presearch §16). A malformed identifier is rejected there
    /// — weeks later, by someone who cannot explain why, to a parent who is already out
    /// of pocket.
    /// </summary>
    public void SetNpi(string? npi)
    {
        if (npi is null)
        {
            Npi = null;
            return;
        }

        var trimmed = npi.Trim();
        if (!TenDigits().IsMatch(trimmed))
        {
            throw new ArgumentException("An NPI must be exactly 10 digits.", nameof(npi));
        }

        Npi = trimmed;
    }

    public void Rename(string displayName) =>
        DisplayName = Guard.MaxLength(Guard.NotBlank(displayName, "displayName"), 200, "displayName");

    /// <summary>
    /// Idempotent: deactivating an inactive provider is not an error.
    ///
    /// Clinical rows are never hard-deleted (docs/DATA_MODEL.md) — a provider who stops
    /// practising must still be attributable on every note they signed.
    /// </summary>
    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    private static string NormaliseState(string state)
    {
        var trimmed = Guard.NotBlank(state, "licenseState").ToUpperInvariant();

        if (!TwoLetters().IsMatch(trimmed))
        {
            throw new ArgumentException(
                "A licence state must be a two-letter code.", nameof(state));
        }

        return trimmed;
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex TenDigits();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex TwoLetters();
}
