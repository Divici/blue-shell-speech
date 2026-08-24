namespace Practice.Application.Authentication;

/// <summary>
/// Authentication, expressed without any Identity types.
///
/// ASP.NET Core Identity lives in Infrastructure. This interface is the seam that keeps it
/// there: Application orchestrates the flow, Infrastructure knows how passwords are hashed
/// and how TOTP is verified, and the architecture tests fail the build if that reverses.
///
/// It also makes the flow testable without a database or a UserManager.
/// </summary>
public interface IProviderAuthenticator
{
    /// <summary>Step one: the password. Never signs anyone in on its own.</summary>
    Task<PasswordResult> VerifyPasswordAsync(
        string email, string password, CancellationToken ct = default);

    /// <summary>Step two: the TOTP code from the authenticator app.</summary>
    Task<MfaResult> VerifyMfaAsync(
        string userId, string code, CancellationToken ct = default);

    /// <summary>Step two, alternative: a single-use recovery code.</summary>
    Task<MfaResult> RedeemRecoveryCodeAsync(
        string userId, string code, CancellationToken ct = default);

    /// <summary>Generates an enrolment secret and the otpauth:// URI for a QR code.</summary>
    Task<MfaEnrolment> BeginMfaEnrolmentAsync(string userId, CancellationToken ct = default);

    /// <summary>Confirms enrolment with a code, enables MFA, and issues recovery codes.</summary>
    Task<MfaEnrolmentResult> CompleteMfaEnrolmentAsync(
        string userId, string code, CancellationToken ct = default);
}

/// <summary>
/// Why a password check did not succeed.
///
/// These are NOT returned to the browser individually. "Which of these happened" tells an
/// attacker whether an account exists and whether they are close — the API distinguishes
/// them for the audit log, and the BFF collapses them to one message.
/// </summary>
public enum PasswordOutcome
{
    /// <summary>Correct. MFA is still required before any session exists.</summary>
    RequiresMfa = 1,

    InvalidCredentials = 2,
    LockedOut = 3,

    /// <summary>Correct password, but MFA is not yet set up. Must enrol before proceeding.</summary>
    RequiresMfaEnrolment = 4,

    /// <summary>The account exists but is disabled.</summary>
    Inactive = 5,
}

public sealed record PasswordResult(
    PasswordOutcome Outcome,
    string? UserId = null,
    /// <summary>How long a lockout has left, so the UI can say something concrete.</summary>
    TimeSpan? LockoutRemaining = null);

public sealed record MfaResult(
    bool Succeeded,
    string? UserId = null,
    Guid? ProviderPublicId = null,
    string? DisplayName = null,
    /// <summary>True when a recovery code was used, so the UI can urge regeneration.</summary>
    bool UsedRecoveryCode = false,
    int RecoveryCodesRemaining = 0);

public sealed record MfaEnrolment(string SharedKey, string AuthenticatorUri);

public sealed record MfaEnrolmentResult(
    bool Succeeded,
    IReadOnlyList<string> RecoveryCodes);
