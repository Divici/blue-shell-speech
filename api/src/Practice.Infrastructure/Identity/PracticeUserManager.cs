using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure.Identity;

/// <summary>
/// The application's <see cref="UserManager{TUser}"/>, and it exists for ONE LINE.
///
/// EVERY DATABASE CALL ON THE LOGIN PATH GOES THROUGH THIS TYPE, AND NOT ONE OF ITS
/// METHODS TAKES A CancellationToken. <c>FindByEmailAsync</c>, <c>CheckPasswordAsync</c>,
/// <c>AccessFailedAsync</c>, <c>ResetAccessFailedCountAsync</c>,
/// <c>GetTwoFactorEnabledAsync</c>, <c>VerifyTwoFactorTokenAsync</c>, <c>UpdateAsync</c> —
/// eighty-two asynchronous methods on the base class, none of them with an overload that
/// accepts one. So <see cref="ProviderAuthenticator"/> observed NEITHER of this
/// application's two bounds: not <c>HttpContext.RequestAborted</c>, and not the
/// uncancellable-write deadline that <c>DatabaseTimeouts.Ceiling</c> is made of.
///
/// Both consequences were real and they pointed in opposite directions:
///
///   * a login issued against a database resuming from auto-pause ran on with no bound at
///     all — past the request timeout, past the ceiling, holding a pooled connection on a
///     container that scales to zero;
///   * and because that unbounded work spent the shared grace, the audit row written after
///     it found a deadline that had ALREADY expired. A cancelled token stays cancelled, so
///     <c>SaveChangesAsync</c> threw before issuing anything: the LoginFailed row recording
///     a credential attempt was lost precisely when somebody was attacking the account,
///     which is the only occasion it matters.
///
/// FIXING TWENTY CALL SITES WAS NEVER THE ANSWER, for the reason D075 gives about audit
/// writes: a rule that every call site must remember holds until the first one that does
/// not, and there is no analyzer that can help — CA2016 has nothing to forward. UserManager
/// funnels every store call through one protected property, so overriding it binds all of
/// them at once, INCLUDING THE ONES NOBODY HAS WRITTEN YET. That is the whole design: not a
/// list of methods, but the single place they all pass through.
///
/// WHY THE DEADLINE AND NOT THE REQUEST'S TOKEN — this is the part that is easy to get
/// backwards. Handing UserManager <c>RequestAborted</c> would bound it too, and would also
/// hand an attacker the lockout: send a password guess, close the socket before
/// <c>AccessFailedAsync</c> commits, and the five-failure lockout <c>AddInfrastructure</c>
/// configures never counts to five. A failure count is the same category of write as an
/// audit row — a record that something already happened, which the caller going away does
/// not un-happen (D075). Reads travel on the same token because there is only one property,
/// and that costs nothing: the deadline does not fire while a request is inside its own
/// bound.
/// </summary>
public sealed class PracticeUserManager(
    UncancellableWriteDeadline deadline,
    IUserStore<PracticeUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<PracticeUser> passwordHasher,
    IEnumerable<IUserValidator<PracticeUser>> userValidators,
    IEnumerable<IPasswordValidator<PracticeUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<PracticeUser>> logger)
    : UserManager<PracticeUser>(
        store,
        optionsAccessor,
        passwordHasher,
        userValidators,
        passwordValidators,
        keyNormalizer,
        errors,
        services,
        logger)
{
    /// <summary>
    /// The token every Identity store call runs on.
    ///
    /// The base returns <c>CancellationToken.None</c>. This is the one line this class
    /// exists for, and <c>AuthenticationTests.Every_identity_store_call_is_bounded_by_the_deadline</c>
    /// reads it back off the resolved manager rather than trusting the registration.
    /// </summary>
    protected override CancellationToken CancellationToken => deadline.Token;
}
