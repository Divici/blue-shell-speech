using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Practice.Application.Authentication;
using Practice.Domain.Auditing;
using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure.Identity;

/// <summary>
/// Identity-backed implementation of the authentication flow.
///
/// TWO THINGS HERE ARE DELIBERATE AND EASY TO GET WRONG:
///
/// 1. A correct password NEVER produces a session. It returns RequiresMfa. MFA is
///    mandatory (docs/SECURITY.md) — this account holds every patient record in the
///    practice, so a stolen password must not be sufficient on its own.
///
/// 2. Failure reasons are distinguished for the AUDIT LOG, not for the caller to show.
///    "No such user" versus "wrong password" tells an attacker which emails are real.
///    The BFF collapses them into one message.
/// </summary>
public sealed class ProviderAuthenticator(
    UserManager<PracticeUser> userManager,
    PracticeDbContext db,
    IAuditWriter audit) : IProviderAuthenticator
{
    public async Task<PasswordResult> VerifyPasswordAsync(
        string email, string password, CancellationToken ct = default)
    {
        var user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            /*
             * Deliberate: no early return before doing hashing work.
             *
             * Returning immediately for an unknown email makes the response measurably
             * faster than for a known one, which turns the login endpoint into an account
             * enumeration oracle. Hashing a dummy password keeps the timings comparable.
             */
            await Task.Run(() => userManager.PasswordHasher.HashPassword(
                new PracticeUser(), password), ct);

            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                metadata: "reason=unknown-email"));

            return new PasswordResult(PasswordOutcome.InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            var until = await userManager.GetLockoutEndDateAsync(user);
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=locked-out"));

            return new PasswordResult(
                PasswordOutcome.LockedOut,
                LockoutRemaining: until.HasValue ? until.Value - DateTimeOffset.UtcNow : null);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            // Increments the failure count; this is what eventually triggers lockout.
            await userManager.AccessFailedAsync(user);

            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-password"));

            return new PasswordResult(PasswordOutcome.InvalidCredentials);
        }

        var provider = await db.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdentityUserId == user.Id, ct);

        if (provider is null || !provider.IsActive)
        {
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Denied,
                actorUserId: user.Id, metadata: "reason=inactive-provider"));

            return new PasswordResult(PasswordOutcome.Inactive);
        }

        await userManager.ResetAccessFailedCountAsync(user);

        if (!await userManager.GetTwoFactorEnabledAsync(user))
        {
            // Correct password, no second factor configured. The only permitted next step
            // is enrolment — there is no path into the app without MFA.
            return new PasswordResult(PasswordOutcome.RequiresMfaEnrolment, user.Id);
        }

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.MfaChallenged, AuditOutcome.Success, actorUserId: user.Id));

        return new PasswordResult(PasswordOutcome.RequiresMfa, user.Id);
    }

    public async Task<MfaResult> VerifyMfaAsync(
        string userId, string code, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return new MfaResult(false);

        // Strips spaces and dashes: authenticator apps display "123 456", and people
        // paste what they see.
        var normalised = code.Replace(" ", string.Empty, StringComparison.Ordinal)
                             .Replace("-", string.Empty, StringComparison.Ordinal);

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, TokenOptions.DefaultAuthenticatorProvider, normalised);

        if (!valid)
        {
            await userManager.AccessFailedAsync(user);
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-mfa-code"));

            return new MfaResult(false);
        }

        return await CompleteSignInAsync(user, usedRecoveryCode: false, ct);
    }

    public async Task<MfaResult> RedeemRecoveryCodeAsync(
        string userId, string code, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null) return new MfaResult(false);

        // Single-use: Identity removes the code as part of redemption.
        var result = await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Trim());

        if (!result.Succeeded)
        {
            await userManager.AccessFailedAsync(user);
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-recovery-code"));

            return new MfaResult(false);
        }

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.RecoveryCodeUsed, AuditOutcome.Success, actorUserId: user.Id));

        return await CompleteSignInAsync(user, usedRecoveryCode: true, ct);
    }

    public async Task<MfaEnrolment> BeginMfaEnrolmentAsync(
        string userId, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException("Unknown user.");

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        return new MfaEnrolment(key!, BuildAuthenticatorUri(user.Email!, key!));
    }

    public async Task<MfaEnrolmentResult> CompleteMfaEnrolmentAsync(
        string userId, string code, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new InvalidOperationException("Unknown user.");

        var normalised = code.Replace(" ", string.Empty, StringComparison.Ordinal)
                             .Replace("-", string.Empty, StringComparison.Ordinal);

        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, TokenOptions.DefaultAuthenticatorProvider, normalised);

        if (!valid)
        {
            return new MfaEnrolmentResult(false, []);
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);

        /*
         * Ten single-use recovery codes, shown ONCE.
         *
         * Identity stores them hashed, so they cannot be shown again — which is correct
         * and must be made obvious in the UI. They are credentials equivalent to the
         * second factor itself (docs/SECURITY.md): an attacker who obtains them does not
         * need to defeat TOTP at all.
         */
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.MfaEnrolled, AuditOutcome.Success, actorUserId: user.Id));

        return new MfaEnrolmentResult(true, codes?.ToList() ?? []);
    }

    private async Task<MfaResult> CompleteSignInAsync(
        PracticeUser user, bool usedRecoveryCode, CancellationToken ct)
    {
        await userManager.ResetAccessFailedCountAsync(user);

        user.LastMfaAtUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

        var provider = await db.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdentityUserId == user.Id, ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.LoginSucceeded, AuditOutcome.Success,
            actorUserId: user.Id, providerId: provider?.Id));

        var remaining = await userManager.CountRecoveryCodesAsync(user);

        return new MfaResult(
            Succeeded: true,
            UserId: user.Id,
            ProviderPublicId: provider?.PublicId,
            DisplayName: provider?.DisplayName,
            UsedRecoveryCode: usedRecoveryCode,
            RecoveryCodesRemaining: remaining);
    }

    /// <summary>
    /// otpauth:// URI, rendered as a QR code by the browser.
    ///
    /// The issuer is what appears in the authenticator app. "Blue Shell Speech" rather
    /// than a hostname, because Michelle will read it on a phone next to a dozen other
    /// six-digit codes.
    /// </summary>
    private static string BuildAuthenticatorUri(string email, string key) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6",
            UrlEncoder.Default.Encode("Blue Shell Speech"),
            UrlEncoder.Default.Encode(email),
            key);
}

/// <summary>
/// Writes audit rows.
///
/// Separated so the authenticator does not depend on a DbContext for auditing, and so a
/// test can assert exactly which events an authentication attempt produced.
///
/// NO CANCELLATION TOKEN, AND THAT IS THE CONTROL.
///
/// An audit row records something that already happened — a record was read, a deletion
/// was refused, a password was wrong. The caller going away does not un-happen it, so the
/// write must not be abandonable, and the surest way to stop a call site handing over the
/// request's token is to leave it nothing to hand over.
///
/// It was a parameter, defaulted, and every one of the twenty-odd call sites passed the
/// endpoint's `ct` — because that is what you do with a token in scope. D071 changed the
/// one call site it was looking at and left the rest, which is how a client that sends
/// `DELETE /notes/{guid}` and drops the connection could walk ten thousand ids and leave
/// AuditEvents empty. The refusal paths are the worst of them: they write an audit row and
/// nothing else, so there is no clinical write whose absence would show the loss.
///
/// "Fix the call sites" was never going to hold, and the build says so: CA2016 is an error
/// here, so a call site inside a method that has a token MUST forward it or suppress the
/// rule one line at a time. With the parameter present, the analyzer enforces the defect.
/// Removing it is the only version of this fix the toolchain agrees with.
///
/// The cost is real and small: a write that cannot be cancelled holds its connection until
/// the command timeout if the database is wedged, on a request nobody is waiting for. The
/// alternative is an audit trail with a survivorship bias toward uninterrupted requests,
/// which is not an audit trail (docs/SECURITY.md §Audit).
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent);
}

public sealed class AuditWriter(PracticeDbContext db) : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent)
    {
        db.AuditEvents.Add(auditEvent);

        // Spelled out rather than left to the default, because it is a decision and not an
        // omission. See the interface.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}

