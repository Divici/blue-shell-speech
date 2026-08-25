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
/// 3. AN AUDIT ROW IS WRITTEN AT THE EARLIEST POINT AT WHICH THE FACT IT ASSERTS IS
///    ALREADY TRUE, AND NOT BEFORE. That one rule puts the failure rows FIRST and the
///    success row LAST, which looks like an inconsistency and is the opposite of one.
///
///    ON A FAILURE the fact is established the moment the credential check returns, so
///    every failure branch reads "audit, then the bookkeeping". Both writes are
///    uncancellable by design — a failed login has already happened, and the caller going
///    away does not un-happen it — so both draw on the same UncancellableWriteDeadline,
///    which gives the whole request ONE grace period after the request bound fires (D090).
///    They therefore COMPETE, and the order decides which survives a database that is
///    refusing work. Written the obvious way round, a failure-count UPDATE carrying a
///    resume from auto-pause spent the entire grace and the LoginFailed row that followed
///    it hit an already-cancelled token — a cancelled token stays cancelled, so the save
///    threw before issuing anything and the row was lost outright rather than merely late.
///    THE AUDIT ROW WINS THAT COMPETITION: lose the row and there is no evidence the
///    attempt happened at all, because the response is deliberately indistinguishable from
///    every other refusal and nothing else records it; lose the increment and the attempt
///    is still on file, so a lockout that failed to fire is visible after the fact. One
///    loss is recoverable and the other is not (D092).
///
///    ON A SUCCESS THAT ARGUMENT INVERTS, and reading it as a general rule about audit
///    rows put LoginSucceeded in AuditEvents for a request that answered 504 and produced
///    no session, with LastMfaAtUtc still null. Nothing about a sign-in is established
///    until the writes the session depends on have landed, so a row written before them is
///    a PREDICTION — and LoginSucceeded is the row an investigator uses to decide which
///    sessions a breach has to be scoped to. A missing success row can be reconstructed
///    from what a session leaves behind: the next request carries the provider context, and
///    every read of a record writes PatientViewed with the actor on it. A false one is not
///    falsifiable by anything. So CompleteSignInAsync writes it LAST, after everything that
///    can still fail — the row and the caller's "you are signed in" then fail together.
///
///    Neither ordering is a complete defence and neither is sold as one: if the grace is
///    gone before the outcome is decided, the lookup is cancelled and there is no
///    established fact to audit. That is the honest boundary — this application does not
///    fabricate a row for an attempt it never finished evaluating.
///
/// 4. THE BOOKKEEPING DOES NOT GO THROUGH UserManager, and every result it returns is
///    checked. UserManager's failure-count methods are read-modify-write behind an
///    optimistic ConcurrencyStamp, and UserStore.UpdateAsync swallows the resulting
///    DbUpdateConcurrencyException into an IdentityResult that this class used to discard —
///    so twenty simultaneous wrong passwords counted as one. See ILoginBookkeeping.
/// </summary>
public sealed class ProviderAuthenticator(
    UserManager<PracticeUser> userManager,
    ILoginBookkeeping bookkeeping,
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
             * DELIBERATE: NO EARLY RETURN, AND NOTHING HERE OBSERVES `ct`.
             *
             * Returning immediately for an unknown email makes the response measurably
             * faster than for a known one, which turns this endpoint into an account
             * enumeration oracle. So this branch pays what the wrong-password branch below
             * pays, in both dimensions that cost anything:
             *
             *   * one password hash, against a user that exists only here;
             *   * one audit row;
             *   * one statement against AspNetUsers — the same failure count, against an id
             *     that matches no row. On a database across a network the ROUND TRIP is the
             *     larger of the two costs, and this branch used to skip it entirely.
             *
             * `absent` carries both halves: IdentityUser generates its own id, so the id
             * this counts against is a fresh GUID that cannot collide with a real row.
             *
             * AND NONE OF IT IS ON THE REQUEST'S TOKEN. `Task.Run(action, ct)` refuses to
             * START when ct is already cancelled, so once the request bound had fired this
             * branch threw before its audit write while the branch below — bound to the
             * deadline through PracticeUserManager — ran to completion and answered
             * normally. Measured under a stalled users table: unknown email 504, empty body,
             * 1527 ms and no audit row; wrong password 200, {"status":"invalid"}, 4696 ms.
             * An oracle in the status, the body, the clock and the audit trail at once,
             * produced by a token that was doing nothing useful in the first place — the
             * hash is CPU-bound and Task.Run's token cannot interrupt it, only decline to
             * begin it.
             */
            var absent = new PracticeUser();

            await Task.Run(() => userManager.PasswordHasher.HashPassword(absent, password));

            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.LoginFailed, AuditOutcome.Failure,
                metadata: "reason=unknown-email"));

            // Counts nothing, by construction. The round trip is the point.
            await bookkeeping.CountFailureAsync(absent.Id);

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

            await CountFailureAsync(user);

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

        await ClearFailuresAsync(user);

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

            await CountFailureAsync(user);

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

            await CountFailureAsync(user);

            return new MfaResult(false);
        }

        /*
         * BEFORE the sign-in, and that is NOT the defect point 3 describes.
         *
         * This row asserts that a recovery code was consumed, and it was: redemption above
         * has already committed its removal, single-use is irreversible, and the code stays
         * spent whether or not the sign-in that follows completes. The fact is established,
         * so the row goes as early as it can — where an interrupted request still leaves
         * evidence that one of ten one-shot credentials was burned. It says nothing about a
         * session; LoginSucceeded is the row that does, and that one goes last.
         */
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
            /*
             * The result is checked, like every IdentityResult on this path.
             *
             * Discarded, a failed reset left `key` null and the caller received a QR code
             * built from `key!` — a NullReferenceException at best, and at worst an
             * enrolment screen for a secret the database does not hold.
             */
            Succeeded(
                await userManager.ResetAuthenticatorKeyAsync(user),
                "generate an authenticator key");

            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                "Identity reported the authenticator key was stored and then returned "
                + "none. Enrolment cannot continue: the shared key on the screen has to be "
                + "the one the verification below will check against.");
        }

        return new MfaEnrolment(key, BuildAuthenticatorUri(user.Email!, key));
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

        /*
         * CHECKED, and this is the one on this path where discarding it was worst.
         *
         * A failed SetTwoFactorEnabled that nobody looks at returns Succeeded: true with
         * ten recovery codes and an MfaEnrolled row, to an account where MFA is still OFF.
         * Michelle then believes the second factor protecting every record in the practice
         * is on, and the next password alone signs her in. Point 3's rule applied to a
         * write rather than to a row: the claim goes out after the thing it claims is true.
         */
        Succeeded(
            await userManager.SetTwoFactorEnabledAsync(user, true),
            "enable two-factor authentication");

        /*
         * Ten single-use recovery codes, shown ONCE.
         *
         * Identity stores them hashed, so they cannot be shown again — which is correct
         * and must be made obvious in the UI. They are credentials equivalent to the
         * second factor itself (docs/SECURITY.md): an attacker who obtains them does not
         * need to defeat TOTP at all.
         */
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);

        if (codes is null)
        {
            throw new InvalidOperationException(
                "Two-factor authentication is enabled and Identity generated no recovery "
                + "codes. An account with a second factor and no way round a lost phone is "
                + "a lockout waiting to happen, so enrolment fails rather than reporting "
                + "success with an empty list.");
        }

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.MfaEnrolled, AuditOutcome.Success, actorUserId: user.Id));

        return new MfaEnrolmentResult(true, [.. codes]);
    }

    private async Task<MfaResult> CompleteSignInAsync(
        PracticeUser user, bool usedRecoveryCode, CancellationToken ct)
    {
        var provider = await db.Providers
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IdentityUserId == user.Id, ct);

        /*
         * THE SIGN-IN'S OWN STATE FIRST, AND ITS AUDIT ROW LAST. The exact opposite of
         * every failure branch above, for the reason point 3 on the class gives: this row
         * asserts a session, and a session is not a fact until the writes it depends on
         * have landed.
         *
         * WRITTEN THE OTHER WAY ROUND — which is how it shipped, on the strength of D092's
         * argument about failures — it read: audit LoginSucceeded, reset the failure count,
         * stamp LastMfaAtUtc. With the AspNetUsers UPDATE stalled past the grace, that
         * produced 504 with no session and an audit table holding
         * MfaEnrolled, MfaChallenged, LoginSucceeded, with LastMfaAtUtc null. The row was a
         * forecast, and forecasts belong nowhere near the table an investigator scopes a
         * breach from.
         *
         * Ordered this way the two fail TOGETHER. If any write below throws, this method
         * never returns, the endpoint answers an error, `web` mints no cookie, and no row
         * claims otherwise. If they all land, the response is the success the row describes.
         *
         * WHAT IT COSTS, stated because it is the mirror of what D092 bought on the failure
         * paths: a sign-in whose grace is exhausted by its own bookkeeping now loses the
         * LoginSucceeded row. That loss is survivable in a way a false row is not — the
         * session does not exist to be recorded, and had it existed the next request's
         * PatientViewed rows would carry the actor. The row that must never be lost on this
         * path is MfaChallenged, and that one is written on the password step, before any
         * of this.
         */
        await ClearFailuresAsync(user);

        if (!await bookkeeping.RecordMfaAsync(user.Id, DateTime.UtcNow))
        {
            throw new InvalidOperationException(
                "The second factor was proved and the moment it happened was not recorded. "
                + "Re-authentication before a password change or an MFA reset is measured "
                + "from that timestamp (docs/SECURITY.md §Authentication), so a session "
                + "must not be issued without it.");
        }

        var remaining = await userManager.CountRecoveryCodesAsync(user);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.LoginSucceeded, AuditOutcome.Success,
            actorUserId: user.Id, providerId: provider?.Id));

        return new MfaResult(
            Succeeded: true,
            UserId: user.Id,
            ProviderPublicId: provider?.PublicId,
            DisplayName: provider?.DisplayName,
            UsedRecoveryCode: usedRecoveryCode,
            RecoveryCodesRemaining: remaining);
    }

    /// <summary>
    /// Counts a failed attempt, and refuses to answer as though it had been counted.
    ///
    /// THIS IS WHAT "STOP DISCARDING THE RESULT" MEANS IN PRACTICE. A refusal that is not
    /// counted is a guess that cost the attacker nothing, and the five-failure lockout is
    /// the whole of what stands between this account — which holds every record in the
    /// practice — and an offline password list. Answering "invalid" while the counter sat
    /// still is the failure mode that let eighty concurrent guesses register as four.
    ///
    /// It surfaces as a 500 with a trace id (Program.cs's problem-details handler), which
    /// is a worse experience than "invalid" and the correct one: the alternative is a login
    /// endpoint that silently stops locking out.
    /// </summary>
    private async Task CountFailureAsync(PracticeUser user)
    {
        if (!await bookkeeping.CountFailureAsync(user.Id))
        {
            throw new InvalidOperationException(
                "The failed-attempt count did not move for a user this request had just "
                + "read. Refusing a credential without counting it would leave the lockout "
                + "in docs/SECURITY.md unable to reach five.");
        }
    }

    /// <summary>
    /// Zeroes the failure count, and refuses to continue if it did not.
    ///
    /// Less severe than the increment and the same class: a reset that quietly fails leaves
    /// a genuine user carrying somebody else's failures toward a lockout she did not earn.
    /// </summary>
    private async Task ClearFailuresAsync(PracticeUser user)
    {
        if (!await bookkeeping.ClearFailuresAsync(user.Id))
        {
            throw new InvalidOperationException(
                "The failed-attempt count could not be cleared for a user this request had "
                + "just authenticated.");
        }
    }

    /// <summary>
    /// An IdentityResult, read rather than dropped.
    ///
    /// Identity reports failure by RETURN VALUE, so every one of these is a silent no-op
    /// when discarded — and this class discarded seven of them. The errors are Identity's
    /// own codes and descriptions: no credential, no email, nothing about the user beyond
    /// what went wrong, so this is safe to put in an exception that reaches a log.
    /// </summary>
    private static void Succeeded(IdentityResult result, string what)
    {
        if (result.Succeeded) return;

        throw new InvalidOperationException(
            $"Identity refused to {what}: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Description}")));
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

