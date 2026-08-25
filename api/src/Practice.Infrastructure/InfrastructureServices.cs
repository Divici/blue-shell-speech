using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Application.Authentication;
using Practice.Application.Consultations;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Notifications;
using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure;

public static class InfrastructureServices
{
    /// <summary>
    /// Registers persistence and ASP.NET Core Identity.
    ///
    /// The Identity options here are the ones docs/SECURITY.md specifies, and several are
    /// deliberately weaker-looking than a default corporate policy. See the comments.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<PracticeDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                /*
                 * Azure SQL serverless auto-pauses; the first query after a pause can fail
                 * while the database resumes. Without a retry policy that surfaces to
                 * Michelle as an error on an app that is merely waking up.
                 *
                 * The two arguments come from DatabaseTimeouts because the REQUEST timeout
                 * is derived from them. They were literals here once, and the request bound
                 * was chosen separately and came out shorter than this policy's own worst
                 * case — so the middleware cancelled the wake-up this line exists to
                 * survive. Whoever changes either number now changes the request bound with
                 * it, and a test reads all of it back off the running application.
                 */
                sql.EnableRetryOnFailure(
                    DatabaseTimeouts.MaxRetryCount, DatabaseTimeouts.MaxRetryDelay, null);

                /*
                 * Stated, rather than inherited from SqlClient's default.
                 *
                 * It was absent — while DesignTimeDbContextFactory sets 180 twenty lines
                 * away — so the bound on a command was whatever the driver happened to
                 * use, and a `Command Timeout` keyword in a connection string this
                 * application does not own could have replaced it with anything, zero
                 * included.
                 *
                 * THIS COMMENT USED TO SAY THIS AND THE RETRY POLICY WERE "THE ONLY THINGS
                 * BOUNDING" AN AUDIT WRITE, AND THAT STOPPED BEING TRUE IN THE COMMIT THAT
                 * INTRODUCED UncancellableWriteDeadline. It is D072's class — a claim about
                 * a control, in prose, with nothing able to notice it going stale — and it
                 * had the same sentence's sibling in docs/SECURITY.md §Audit for company.
                 * What actually bounds an uncancellable write now is the deadline
                 * registered below; this command timeout bounds one STATEMENT, and
                 * DatabaseTimeouts.RetryBudget is what the retry policy above can spend on
                 * one retried operation. Three bounds, three jobs.
                 */
                sql.CommandTimeout(DatabaseTimeouts.CommandSeconds);
            }));

        services
            .AddIdentityCore<PracticeUser>(options =>
            {
                /*
                 * 12 characters, and NO composition rules.
                 *
                 * This is intentional and follows NIST SP 800-63B. Requiring a digit, a
                 * symbol, and mixed case pushes people toward predictable patterns
                 * (Passw0rd!) and toward writing them down. Length is what actually
                 * resists guessing.
                 */
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;

                /*
                 * Lockout after 5 failures.
                 *
                 * This account holds every patient record in the practice, so credential
                 * stuffing is the highest-impact attack available (docs/THREAT_MODEL.md).
                 * There is exactly one legitimate user, who will not trip this.
                 */
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.User.RequireUniqueEmail = true;

                // No email confirmation flow: accounts are seeded, not self-registered.
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<PracticeDbContext>()
            /*
             * Only the authenticator provider is registered.
             *
             * AddDefaultTokenProviders() also wires email and phone providers for
             * confirmation and password-reset-by-link. Neither exists here: accounts are
             * seeded rather than self-registered, and notifications carry no content
             * (docs/DATA_MODEL.md), so there is no emailed-link flow to support.
             *
             * It also lives in the ASP.NET Core shared framework, which this class
             * library deliberately does not reference.
             */
            .AddTokenProvider<AuthenticatorTokenProvider<PracticeUser>>(
                TokenOptions.DefaultAuthenticatorProvider)
            /*
             * THE ONE REASON A DERIVED UserManager EXISTS: none of its methods takes a
             * CancellationToken, so every Identity store call on the login path observed
             * neither the request timeout nor the uncancellable-write deadline.
             *
             * PracticeUserManager overrides the single protected property they all pass
             * through. Registered here rather than as a bare AddScoped because
             * AddIdentityCore has already registered UserManager<PracticeUser>, and this is
             * the call that makes that resolution land on ours — see IdentityBuilder.
             */
            .AddUserManager<PracticeUserManager>();

        /*
         * SCOPED, so one deadline covers every uncancellable write in a request.
         *
         * Per-write would let a path with three audit writes spend three graces, and
         * "how many audit writes can a path have after cancellation" is exactly the kind
         * of enumeration this repository keeps getting wrong. One shared deadline makes
         * DatabaseTimeouts.Ceiling true by construction instead of by counting.
         *
         * ProviderContextMiddleware binds it to HttpContext.RequestAborted at the top of
         * every request; the ceiling argument is the fallback for a scope nothing binds.
         */
        services.AddScoped(_ => new UncancellableWriteDeadline(
            DatabaseTimeouts.Ceiling, DatabaseTimeouts.UncancellableGrace));

        services.AddScoped<IAuditWriter, AuditWriter>();

        /*
         * The login's own writes, off UserManager and onto single statements.
         *
         * UserManager's failure-count methods are read-modify-write behind an optimistic
         * ConcurrencyStamp, and UserStore.UpdateAsync turns the resulting
         * DbUpdateConcurrencyException into an IdentityResult rather than raising it — so
         * concurrent wrong passwords counted as one and nothing anywhere went red. See
         * ILoginBookkeeping for the statement and for the two alternatives that lost.
         */
        services.AddScoped<ILoginBookkeeping, LoginBookkeeping>();
        services.AddScoped<IProviderAuthenticator, ProviderAuthenticator>();
        services.AddScoped<IConsultationNotifier, LoggingConsultationNotifier>();

        return services;
    }
}
