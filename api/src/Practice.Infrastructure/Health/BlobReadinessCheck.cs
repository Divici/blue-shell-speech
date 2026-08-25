using Azure.Storage.Blobs;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Practice.Infrastructure.Storage;

namespace Practice.Infrastructure.Health;

/// <summary>
/// Can this replica reach the container a dictation lands in.
///
/// Same two-failure split as <see cref="SqlReadinessCheck"/> and for the same reasons —
/// refused is a deployment defect and answers <c>Unhealthy</c>, slow is a hiccup and
/// answers <c>Degraded</c> — with one difference: blob storage does not auto-pause, so the
/// slow window here is narrow. It is kept anyway, because the alternative is taking a
/// working replica out of rotation over a lost packet.
///
/// NO SECRET IS REQUIRED TO ASK. In production the client is built from the account's https
/// endpoint and <c>DefaultAzureCredential</c>, which resolves to the container's
/// system-assigned managed identity (<see cref="ReadinessChecks.SessionAudioContainer"/>,
/// D028). That is also what makes this probe worth running: an identity that was never
/// granted a role on the storage account fails HERE, on the rollout, rather than on the
/// first dictation Michelle records.
///
/// AND IT SAYS NOTHING ABOUT WHAT IT ASKED. An Azure SDK failure carries the full request
/// URI — account and container — and this check's description reaches both an
/// unauthenticated endpoint and the health service's log line. So every sentence below is a
/// constant, the exception is never attached, and
/// <see cref="StorageContainers.SessionAudio"/> is never interpolated into one. Neither
/// name is secret (both are in <c>infra/provision-platform.sh</c> in a public repository),
/// which is beside the point: an unauthenticated endpoint should not be the thing that
/// publishes the practice's infrastructure layout to whoever asks.
/// </summary>
public sealed class BlobReadinessCheck(BlobContainerClient? container, TimeSpan probeTimeout)
    : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        /*
         * Unconfigured is an ANSWER, not a reason to skip the check.
         *
         * A probe that quietly drops a dependency it could not configure reports 200 having
         * asserted nothing — which is the empty-readiness-probe state this whole task
         * existed to end, one layer in. So the registration is unconditional and this is
         * what it says.
         */
        if (container is null)
        {
            return HealthCheckResult.Unhealthy("No blob storage endpoint is configured.");
        }

        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(probeTimeout);

        try
        {
            /*
             * Container metadata, not account metadata.
             *
             * Reading the container proves the grant the application actually needs — the
             * data-plane role on the container it writes audio to. Asking the account for
             * its service properties is a different permission, held by a different role,
             * and a probe that passes on a permission nothing uses is worse than no probe.
             *
             * ExistsAsync answers false on a 404 rather than throwing, so "the container is
             * not there" is a refusal with its own sentence instead of a generic one.
             */
            var exists = await container.ExistsAsync(probe.Token);

            return exists.Value
                ? HealthCheckResult.Healthy("Blob storage answered.")
                : HealthCheckResult.Unhealthy("The configured blob container does not exist.");
        }
        catch (Exception) when (probe.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded(
                "Blob storage did not answer within the probe's budget.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Blob storage refused the request.");
        }
    }
}
