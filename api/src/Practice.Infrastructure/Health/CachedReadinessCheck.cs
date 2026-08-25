using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Practice.Infrastructure.Health;

/// <summary>
/// One dependency round trip per window, however often the orchestrator asks.
///
/// A PROBE THAT DIALS EVERY TIME IS THE COST MODEL, NOT THE LATENCY. Probes arrive every few
/// seconds for as long as a replica is up. Azure SQL on the free offer auto-pauses and is
/// billed in vCore-seconds (CLAUDE.md, D001), so a readiness check that opened a connection
/// on every probe would hold the database online for the replica's whole life — and buy
/// nothing, because the answer cannot have changed since a few seconds ago.
///
/// SINGLETON, WHICH IS THE ENTIRE POINT. <c>HealthCheckService</c> resolves each check from
/// a fresh scope on every probe, so a scoped or transient decorator would hold a cache
/// exactly one probe long and dial the dependency every time. It is registered as a
/// singleton in <see cref="ReadinessChecks.AddReadinessChecks"/>, and that registration is
/// load-bearing rather than tidy.
///
/// THE WINDOW IS ASYMMETRIC, ON PURPOSE. A success is reused for
/// <see cref="HealthProbeBounds.HealthyFor"/> — the claim being cached is narrow ("this
/// replica could reach that dependency") and holds well over minutes. Anything else is
/// re-probed after <see cref="HealthProbeBounds.RecheckAfter"/>, because a replica that has
/// recovered must return to rotation promptly rather than serving a stale refusal for five
/// minutes. The first probe of a replica's life is never cached, which is the case
/// readiness genuinely gates: a revision whose configuration does not work.
///
/// The gate collapses CONCURRENT probes as well as consecutive ones — liveness and
/// readiness arrive independently, and two misses at once should still be one round trip.
/// </summary>
public sealed class CachedReadinessCheck<TInner>(TInner inner, TimeProvider clock)
    : IHealthCheck, IDisposable
    where TInner : IHealthCheck
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Probe? _last;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _last);

        if (IsFresh(cached, clock.GetUtcNow()))
        {
            return cached!.Result;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Re-read inside the gate: whoever held it may have just refreshed this.
            cached = Volatile.Read(ref _last);

            if (IsFresh(cached, clock.GetUtcNow()))
            {
                return cached!.Result;
            }

            var result = await inner.CheckHealthAsync(context, cancellationToken);

            Volatile.Write(ref _last, new Probe(result, clock.GetUtcNow()));

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsFresh(Probe? probe, DateTimeOffset now) =>
        probe is not null && now - probe.TakenAt < Reuse(probe.Result.Status);

    private static TimeSpan Reuse(HealthStatus status) =>
        status == HealthStatus.Healthy
            ? HealthProbeBounds.HealthyFor
            : HealthProbeBounds.RecheckAfter;

    private sealed record Probe(HealthCheckResult Result, DateTimeOffset TakenAt);
}
