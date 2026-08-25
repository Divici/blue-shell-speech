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
/// THREE THINGS HERE ARE DELIBERATE AND EASY TO GET WRONG:
///
/// 1. A correct password NEVER produces a session. It returns RequiresMfa. MFA is
///    mandatory (docs/SECURITY.md) — this account holds every patient record in the
///    practice, so a stolen password must not be sufficient on its own.
///
/// 2. Failure reasons are distinguished for the AUDIT LOG, not for the caller to show.
///    "No such user" versus "wrong password" tells an attacker which emails are real.
///    The BFF collapses them into one message.
///
/// 3. THE AUDIT ROW IS WRITTEN AS SOON AS THE OUTCOME IS DECIDED, BEFORE THE BOOKKEEPING
///    THAT FOLLOWS IT. Every failure branch below reads "audit, then AccessFailedAsync",
///    which looks like an arbitrary ordering and is not.
///
///    Both writes are uncancellable by design — a failed login has already happened, and
///    the caller going away does not un-happen it — so both draw on the same
///    UncancellableWriteDeadline, which gives the whole request ONE grace period after the
///    request bound fires (D090). They therefore COMPETE, and the order decides which one
///    survives a database that is refusing work. Written the obvious way round, a
///    failure-count UPDATE carrying a resume from auto-pause spent the entire grace and
///    the LoginFailed row that followed it hit an already-cancelled token — a cancelled
///    token stays cancelled, so the save threw before issuing anything and the row was
///    lost outright rather than merely being late.
///
///    THE AUDIT ROW WINS THAT COMPETITION, and the reason is asymmetric rather than a
///    preference. Lose the row and there is no evidence the attempt ever happened; the
///    response is deliberately indistinguishable from every other refusal, so nothing else
///    in the system records it. Lose the increment and the attempt is still on file — the
///    LoginFailed rows can be counted, and a lockout that failed to fire is visible after
///    the fact. One loss is recoverable and the other is not.
///
///    It is not a complete defence and should not be read as one: if the grace is gone
///    before the outcome is even decided, the lookup is cancelled and there is no
///    established fact to audit. That is the honest boundary — this application does not
///    fabricate a row for an attempt it never finished evaluating.
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
            // AUDIT FIRST. See point 3 on the class: both writes are uncancellable and
            // share one grace, so the order is which of them survives a wedged database.
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-password"));

            // Increments the failure count; this is what eventually triggers lockout.
            await userManager.AccessFailedAsync(user);

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
            // Audit first, then the failure count — see point 3 on the class.
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-mfa-code"));

            await userManager.AccessFailedAsync(user);

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
            // Audit first, then the failure count — see point 3 on the class.
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                actorUserId: user.Id, metadata: "reason=bad-recovery-code"));

            await userManager.AccessFailedAsync(user);

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
        var provider = await db.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdentityUserId == user.Id, ct);

        /*
         * BEFORE the Identity bookkeeping, for the reason on the class.
         *
         * The provider lookup has to come first because the row carries the provider id,
         * and it runs on the REQUEST's token — it is a read, and a read the caller has
         * abandoned is abandonable. Everything after this point is uncancellable and
         * competing for one grace, so the row that records the sign-in goes first.
         */
        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.LoginSucceeded, AuditOutcome.Success,
            actorUserId: user.Id, providerId: provider?.Id));

        await userManager.ResetAccessFailedCountAsync(user);

        user.LastMfaAtUtc = DateTime.UtcNow;
        await userManager.UpdateAsync(user);

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
/// THE COST, AND THE BOUND THAT REPLACED THE SENTENCE ABOUT IT. A write that ignores the
/// request's token cannot be stopped by the request timeout either — that policy cancels
/// HttpContext.RequestAborted and then AWAITS the pipeline, so it bounds work that
/// observes a token and nothing else. This paragraph has now been wrong about that bound
/// twice, which is worth recording because both versions read as decisions:
///
///   * "holds its connection until the command timeout" named a number no configuration
///     set at all — AddInfrastructure configured none, so it was SqlClient's default.
///   * "bounded by DatabaseTimeouts.RetryBudget ... on a request nobody is waiting for"
///     was arithmetic rather than a control. Nothing enforced it, and it composed the
///     wrong way round: the request bound and this budget ADD, so the tier's real ceiling
///     was 260 + 230 seconds against a BFF that gave up at 300. The nesting the repository
///     had written down was false by its own numbers.
///
/// So the bound is a mechanism now, not a sentence. AuditWriter saves on
/// UncancellableWriteDeadline.Token: a per-request deadline that does not move when the
/// caller goes away, gives every remaining uncancellable write DatabaseTimeouts
/// .UncancellableGrace ONCE between them from the moment the request bound fires, and caps
/// itself at DatabaseTimeouts.Ceiling regardless. The request bound plus that grace IS
/// DatabaseTimeouts.Ceiling, and RequestBoundsTests measures it on a real DELETE rather
/// than deriving it here.
///
/// It is still the right trade, and the durability it buys is unchanged: cancelling the
/// caller's token does not cancel this write, which is the property
/// A_refused_discard_is_audited_even_when_the_caller_disconnects pins. What is given up is
/// the tail: a database still refusing work a grace period after a request has burned its
/// entire budget loses the row, where an unbounded write might eventually have landed it.
/// A bounded loss beats an audit trail with a survivorship bias toward uninterrupted
/// requests, which is not an audit trail (docs/SECURITY.md §Audit).
///
/// AND THE GRACE IS SHARED WITH MORE THAN AUDIT WRITES, which is the part that turned out
/// to have teeth. Identity's store calls draw on the same deadline (PracticeUserManager) —
/// they have to, because none of UserManager's methods takes a token and an unbounded
/// login is outside every bound this tier states. So on the authentication path an audit
/// write and a failure-count UPDATE COMPETE for one grace, and whichever runs first is the
/// one that survives a database refusing work. ProviderAuthenticator therefore audits
/// before it does its bookkeeping, deliberately; see point 3 on that class for why the row
/// is the half worth keeping. Ordering is a real control here, not a style choice.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEvent auditEvent);
}

public sealed class AuditWriter(PracticeDbContext db, UncancellableWriteDeadline deadline)
    : IAuditWriter
{
    public async Task WriteAsync(AuditEvent auditEvent)
    {
        db.AuditEvents.Add(auditEvent);

        /*
         * NOT the caller's token, and NOT CancellationToken.None.
         *
         * The caller's token would abandon the row the moment a phone locks, which is the
         * defect D075 closed by deleting the parameter. CancellationToken.None was the
         * first answer to that and it left this write outside every bound the application
         * sets — the request timeout cancels RequestAborted and then waits, so an
         * uncancellable save simply runs on past it and ADDS to the tier's ceiling.
         *
         * The deadline is per request and shared: it does not move when the caller goes
         * away, and it expires a fixed grace after the request bound does. See the
         * interface, and DatabaseTimeouts.Ceiling for the arithmetic it makes true.
         */
        await db.SaveChangesAsync(deadline.Token);
    }
}

