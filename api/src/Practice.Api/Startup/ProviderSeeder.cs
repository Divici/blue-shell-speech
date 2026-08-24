using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Startup;

/// <summary>
/// Creates the first provider account.
///
/// Accounts are seeded, not self-registered — there is no public sign-up, because there is
/// no scenario in which a stranger should be able to create a clinician login on a system
/// holding children's medical records.
///
/// This runs once, on startup, and only when all three conditions hold:
///   1. Seed:ProviderEmail and Seed:ProviderPassword are configured
///   2. no provider already exists
///   3. the account it would create does not already exist
///
/// It NEVER updates an existing account. A seeder that resets a password on every restart
/// would be a permanent backdoor with the credential sitting in configuration.
/// </summary>
public sealed partial class ProviderSeeder(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<ProviderSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration["Seed:ProviderEmail"];
        var password = configuration["Seed:ProviderPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            LogNoSeedConfigured(logger);
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();

        // Providers has no query filter — it is not patient data, and the filter is armed
        // by resolving a provider in the first place.
        if (await db.Providers.AnyAsync(cancellationToken))
        {
            LogProviderExists(logger);
            return;
        }

        if (await users.FindByEmailAsync(email) is not null)
        {
            LogOrphanSeedUser(logger);
            return;
        }

        var user = new PracticeUser { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            /*
             * Log the error CODES, never the password or the errors' full text — an
             * Identity failure description can echo the supplied value back.
             */
            LogSeedFailed(logger, string.Join(",", created.Errors.Select(e => e.Code)));
            return;
        }

        var provider = Provider.Create(
            user.Id,
            configuration["Seed:ProviderName"] ?? "Provider",
            configuration["Seed:ProviderCredentials"] ?? "M.S., CCC-SLP",
            configuration["Seed:ProviderLicense"] ?? "PENDING",
            configuration["Seed:ProviderLicenseState"] ?? "MD");

        db.Providers.Add(provider);
        await db.SaveChangesAsync(cancellationToken);

        /*
         * MFA is NOT enabled here.
         *
         * The account is created without a second factor, so the first sign-in is forced
         * into enrolment (PasswordOutcome.RequiresMfaEnrolment). That is deliberate: the
         * clinician enrols their own authenticator, and no shared secret ever exists in
         * configuration or in a deployment log.
         */
        LogSeeded(logger, provider.PublicId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /*
     * Source-generated logging.
     *
     * Required by CA1848, and worth having on its own terms: these become strongly typed
     * call sites, so a future edit cannot accidentally interpolate a password or a patient
     * name into a log message. Every parameter here is an id or an error code — never a
     * credential, never PHI (docs/SECURITY.md).
     */
    [LoggerMessage(Level = LogLevel.Information, Message = "No seed provider configured; skipping.")]
    private static partial void LogNoSeedConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "A provider already exists; skipping seed.")]
    private static partial void LogProviderExists(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seed user exists without a provider row. Not modifying it.")]
    private static partial void LogOrphanSeedUser(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create the seed provider: {Codes}")]
    private static partial void LogSeedFailed(ILogger logger, string codes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded provider {ProviderPublicId}. First sign-in requires MFA enrolment.")]
    private static partial void LogSeeded(ILogger logger, Guid providerPublicId);
}
