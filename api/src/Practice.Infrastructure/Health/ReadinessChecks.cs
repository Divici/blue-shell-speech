using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Practice.Infrastructure.Storage;

namespace Practice.Infrastructure.Health;

/// <summary>
/// The dependency checks behind <c>/health/ready</c>, and the wiring that keeps them off
/// <c>/health/live</c>.
///
/// LIVE AND READY ARE DIFFERENT QUESTIONS AND THE ANSWERS HAVE DIFFERENT CONSEQUENCES.
/// A failing liveness probe RESTARTS the container; a failing readiness probe REMOVES IT
/// FROM ROTATION. So nothing that depends on another machine may be tagged
/// <see cref="LiveTag"/>: a liveness check that dialled Azure SQL would restart a perfectly
/// healthy process because a database was asleep, and a restart cannot wake a database —
/// it just puts a cold start in front of the resume. The converse is the reason this class
/// exists at all: a readiness probe that asserts nothing reports 200 while the replica
/// cannot reach anything, and traffic is routed to it on the strength of that.
///
/// AZURE OPENAI IS DELIBERATELY NOT HERE, and that is a decision rather than an omission.
/// presearch §19 requires patient records, scheduling and manual notes to keep working when
/// AI is unavailable, so AI being down must never take this app out of rotation.
/// <see cref="StorageContainers.SessionAudio"/> is a different matter: audio is where a
/// dictation lands before anything else happens to it, and a replica that cannot write it
/// cannot take a dictation at all.
/// </summary>
public static class ReadinessChecks
{
    /// <summary>Tagged onto checks that answer "is this process up".</summary>
    public const string LiveTag = "live";

    /// <summary>Tagged onto checks that answer "can this replica serve traffic".</summary>
    public const string ReadyTag = "ready";

    /// <summary>Name of the SQL check, as it appears in the health response.</summary>
    public const string SqlCheckName = "sql";

    /// <summary>Name of the blob check, as it appears in the health response.</summary>
    public const string BlobCheckName = "blob";

    /// <summary>
    /// Registers the SQL and blob probes under <see cref="ReadyTag"/>.
    ///
    /// Both are wrapped in <see cref="CachedReadinessCheck{TInner}"/> and registered as
    /// SINGLETONS, which is load-bearing rather than incidental:
    /// <c>HealthCheckService</c> resolves a check from a fresh scope on every probe, so a
    /// scoped or transient decorator would hold a cache exactly one probe long and dial the
    /// dependency every time — which is the behaviour the cache exists to stop.
    ///
    /// <paramref name="storageConnectionString"/> is nullable because it legitimately is:
    /// <c>docker compose</c> supplies Azurite's key-free shorthand, production supplies the
    /// account's https endpoint and the container authenticates with its managed identity,
    /// and a bare <c>dotnet run</c> supplies neither. Absent, the blob check reports
    /// unready with a fixed sentence rather than being silently skipped — a probe that
    /// quietly drops a check it could not configure is the empty probe this whole file is
    /// a reaction to.
    ///
    /// <c>failureStatus</c> is <see cref="HealthStatus.Unhealthy"/> for both. It only
    /// applies when a check THROWS, and these checks do not: they translate every failure
    /// they understand into a status of their own, so a throw here means a defect in the
    /// probe rather than in the dependency, and a defect should be loud.
    /// </summary>
    public static IHealthChecksBuilder AddReadinessChecks(
        this IHealthChecksBuilder builder,
        string sqlConnectionString,
        string? storageConnectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);

        builder.Services.TryAddSingleton(
            _ => new SqlReadinessCheck(sqlConnectionString, HealthProbeBounds.Probe));

        builder.Services.TryAddSingleton(
            _ => new BlobReadinessCheck(
                SessionAudioContainer(storageConnectionString), HealthProbeBounds.Probe));

        builder.Services.TryAddSingleton<CachedReadinessCheck<SqlReadinessCheck>>();
        builder.Services.TryAddSingleton<CachedReadinessCheck<BlobReadinessCheck>>();

        return builder
            .AddCheck<CachedReadinessCheck<SqlReadinessCheck>>(
                SqlCheckName, HealthStatus.Unhealthy, [ReadyTag])
            .AddCheck<CachedReadinessCheck<BlobReadinessCheck>>(
                BlobCheckName, HealthStatus.Unhealthy, [ReadyTag]);
    }

    /// <summary>
    /// The container client the blob probe reads, or null when no storage is configured.
    ///
    /// NO SECRET, IN EITHER SHAPE. An https endpoint is paired with
    /// <see cref="DefaultAzureCredential"/>, which in Container Apps resolves to the
    /// container's system-assigned managed identity — the same credential story as SQL
    /// (D028), and the reason nothing key-shaped appears in this repository. Anything else
    /// is treated as an emulator connection string, which is how <c>docker compose</c>
    /// passes Azurite's <c>UseDevelopmentStorage=true</c>: a shorthand that carries no key
    /// at all.
    ///
    /// A PROBE DOES NOT RETRY. <c>MaxRetries = 0</c> is deliberate — the SDK's default
    /// three attempts with exponential backoff would spend far more than
    /// <see cref="HealthProbeBounds.Probe"/> and turn one probe into three requests. The
    /// orchestrator's next probe IS the retry, and it is already scheduled.
    /// </summary>
    public static BlobContainerClient? SessionAudioContainer(string? storageConnectionString)
    {
        if (string.IsNullOrWhiteSpace(storageConnectionString))
        {
            return null;
        }

        var options = new BlobClientOptions
        {
            Retry =
            {
                MaxRetries = 0,
                NetworkTimeout = HealthProbeBounds.Probe,
            },
        };

        var service =
            Uri.TryCreate(storageConnectionString, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttps
                ? new BlobServiceClient(endpoint, new DefaultAzureCredential(), options)
                : new BlobServiceClient(storageConnectionString, options);

        return service.GetBlobContainerClient(StorageContainers.SessionAudio);
    }
}
