using Microsoft.AspNetCore.Identity;

namespace Practice.Infrastructure.Identity;

/// <summary>
/// The Identity user.
///
/// Deliberately thin. Everything about the clinician as a professional — display name,
/// credentials, licence, NPI — lives on the Provider domain entity. This type owns only
/// what Identity owns: the credential, the MFA secret, lockout state.
///
/// Splitting them means an Identity package upgrade cannot force a migration on a table
/// holding clinical data, and a clinical migration cannot break login.
/// </summary>
public sealed class PracticeUser : IdentityUser
{
    /// <summary>
    /// When the user last completed an MFA challenge.
    ///
    /// Recorded because re-authentication is required before changing a password,
    /// regenerating recovery codes, or disabling MFA (docs/SECURITY.md). An attacker who
    /// finds an unattended authenticated session must not be able to take the account
    /// over without proving possession of the second factor.
    /// </summary>
    public DateTime? LastMfaAtUtc { get; set; }
}
