using Practice.Infrastructure.Persistence;

namespace Practice.Infrastructure.Health;

/// <summary>
/// How long a PROBE is allowed to take, and how often it is allowed to ask — which is a
/// different question from how long a REQUEST is allowed to take, and answering it with
/// <see cref="DatabaseTimeouts"/>'s numbers would be wrong by three orders of magnitude.
///
/// <see cref="DatabaseTimeouts.Request"/> is ten minutes and twenty seconds, and every
/// term in it is justified (D086, D090): a clinician is waiting for that answer, the
/// database may be resuming from auto-pause, and the retry policy that carries her through
/// it must not be truncated by the bound above it. NONE OF THAT IS TRUE OF A PROBE. Nobody
/// is waiting for a readiness answer; an orchestrator asked, and it will ask again in
/// seconds. An answer that arrives ten minutes later is an answer to a question that has
/// already been decided three ways.
///
/// Left on the default policy, <c>/health/ready</c> inherited that ten-minute bound —
/// which is why <see cref="EndpointTimeout"/> exists and why Program.cs applies it
/// explicitly to both health routes.
///
/// THE OTHER HALF IS COST, NOT LATENCY, AND IT IS THE HALF THAT IS EASY TO MISS. This
/// application runs against an Azure SQL Database on the free offer, which auto-pauses
/// when nothing is connected (CLAUDE.md, D001). A readiness check that opens a connection
/// on every probe holds that database online for as long as the container is up, and the
/// free offer is denominated in vCore-seconds. So the probe is CACHED
/// (<see cref="CachedReadinessCheck{TInner}"/>) and its connection is UNPOOLED — a probe
/// that dials once every <see cref="HealthyFor"/> and leaves no socket behind is a probe
/// that does not pay for its own reassurance.
/// </summary>
public static class HealthProbeBounds
{
    /// <summary>
    /// What ONE dependency check gets: two seconds.
    ///
    /// Anchored on the only statement in this repository of how long a probe of this
    /// container may take — <c>api/Dockerfile</c>'s <c>HEALTHCHECK --timeout=3s</c>. Two
    /// seconds for the dependency, the rest for the host and the response. Container Apps
    /// applies probe timings of its own and <c>infra/provision-apps.sh</c> configures
    /// none, so it is running platform defaults; those are deliberately NOT quoted here,
    /// because a number nobody in this repository has measured reads as a decision and is
    /// not one (D072, D086).
    ///
    /// Deliberately shorter than an Azure SQL resume, which takes tens of seconds. That is
    /// not a defect of this number — it is the reason a probe that runs out of time
    /// answers <c>Degraded</c> rather than <c>Unhealthy</c>. See
    /// <see cref="SqlReadinessCheck"/>.
    /// </summary>
    public static readonly TimeSpan Probe = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What the whole health ENDPOINT gets: five seconds.
    ///
    /// A backstop, not the bound that matters — <see cref="Probe"/> is. Checks run
    /// concurrently, so two of them cost <see cref="Probe"/>, and this leaves room for a
    /// check that ignores its cancellation token to be cut off by the pipeline instead of
    /// running to the default policy's ten minutes.
    ///
    /// It is applied per-endpoint in Program.cs. Without that, both health routes inherit
    /// <see cref="DatabaseTimeouts.Request"/>, which is the exact defect this class exists
    /// to name: a bound sized for a clinician waiting on a resuming database, silently
    /// governing a probe that will be repeated in ten seconds.
    /// </summary>
    public static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a SUCCESSFUL probe is reused before the dependency is dialled again: five
    /// minutes.
    ///
    /// The ratio is the point, not the number. Probes arrive on the order of seconds; this
    /// turns that into at most one dependency round trip per replica per five minutes. A
    /// replica that starts, serves a burst and scales away typically dials each dependency
    /// exactly once in its whole life.
    ///
    /// The claim being cached is narrow and holds well over that window: "this replica
    /// could reach that dependency". The case readiness genuinely gates is a REVISION
    /// ROLLOUT — a new replica with a connection string that does not work, or a managed
    /// identity that was never granted — and that is caught by the first probe, which is
    /// never cached because there is nothing to cache yet.
    /// </summary>
    public static readonly TimeSpan HealthyFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long anything OTHER than a success is reused: ten seconds.
    ///
    /// Much shorter than <see cref="HealthyFor"/>, and deliberately asymmetric. A replica
    /// that has recovered must return to rotation promptly rather than serving a stale
    /// refusal for five minutes; a replica that is healthy has nothing to gain from being
    /// asked again. Long enough that a burst of probes against a dependency that is down
    /// still collapses into one attempt.
    /// </summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(10);
}
