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
                // Azure SQL serverless auto-pauses; the first query after a pause can fail
                // while the database resumes. Without a retry policy that surfaces to
                // Michelle as an error on an app that is merely waking up.
                sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(10), null);

                /*
                 * Stated, rather than inherited from SqlClient's default.
                 *
                 * It was absent — while DesignTimeDbContextFactory sets 180 twenty lines
                 * away — so the bound on a command was whatever the driver happened to
                 * use, and a `Command Timeout` keyword in a connection string this
                 * application does not own could have replaced it with anything, zero
                 * included. That matters most where nothing else can intervene: an audit
                 * write does not observe the request token by design (D075), so this and
                 * the retry budget above are the ONLY things bounding it. The value and
                 * the arithmetic are on DatabaseTimeouts.
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
                TokenOptions.DefaultAuthenticatorProvider);

        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IProviderAuthenticator, ProviderAuthenticator>();
        services.AddScoped<IConsultationNotifier, LoggingConsultationNotifier>();

        return services;
    }
}
