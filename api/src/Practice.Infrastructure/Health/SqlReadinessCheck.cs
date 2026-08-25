using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Practice.Infrastructure.Health;

/// <summary>
/// Can this replica reach its database — asked in a way that does not keep the database
/// awake, and answered in a way that does not punish it for being asleep.
///
/// TWO FAILURES, TWO ANSWERS, AND CONFLATING THEM BREAKS THE APPLICATION IN OPPOSITE
/// DIRECTIONS.
///
///   REFUSED  — a connection string that does not work, a managed identity that was never
///              granted, a database that is not there. A deployment defect, and a revision
///              rollout is the one place it can still be caught. <c>Unhealthy</c>, so the
///              revision does not take traffic.
///
///   SLOW     — Azure SQL on the free offer auto-pauses, and a resume takes tens of
///              seconds against a probe budget of two. <c>Degraded</c>, which the health
///              middleware answers 200, so the replica STAYS IN ROTATION.
///
/// The second is not leniency, it is the only answer that terminates. The single thing
/// that resumes an auto-paused Azure SQL is a connection, and the connections come from
/// requests; a probe that pulls the replica out of rotation while the database wakes
/// removes the traffic that would have woken it, and the replica never becomes ready. Short
/// of that, it flaps out and back on every rollout and every idle morning.
///
/// THE SPLIT IS ON THE SHAPE OF THE FAILURE, NOT ON A LIST OF ERROR NUMBERS. A
/// misconfiguration is refused immediately; a resuming database is not refused at all, it
/// simply does not answer inside <see cref="HealthProbeBounds.Probe"/>. So the classifier
/// is "did our own budget run out", which is a fact this class owns, rather than a table of
/// SQL error codes to go stale — an enumeration being this repository's recurring way of
/// being wrong (D081→D087, D088, D090).
///
/// AND IT LEAVES NOTHING BEHIND. <c>Pooling=false</c> is the cost-model half: a pooled
/// connection is a live SQL session for as long as the pool holds it, a live session is a
/// database that cannot auto-pause, and the free offer is denominated in vCore-seconds
/// (CLAUDE.md, D001). A probe that dialled once every <see cref="HealthProbeBounds.HealthyFor"/>
/// and then held the socket would keep the database online for the whole life of the
/// replica — billing for the privilege of reassuring an orchestrator. Its own connection
/// string also means the probe never takes a connection out of the pool the application
/// serves requests from.
/// </summary>
public sealed class SqlReadinessCheck : IHealthCheck
{
    /// <summary>
    /// What the probe calls itself on the server, so its sessions are identifiable in
    /// <c>sys.dm_exec_sessions</c> — by an operator, and by the test that asserts it leaves
    /// none open. Not a credential and not PHI.
    /// </summary>
    public const string ProbeApplicationName = "blue-shell-readiness";

    private readonly string _connectionString;
    private readonly TimeSpan _probeTimeout;
    private readonly int _commandSeconds;

    public SqlReadinessCheck(string connectionString, TimeSpan probeTimeout)
    {
        /*
         * ConnectTimeout is set ABOVE the probe budget on purpose, so the linked token is
         * always what fires first. If SqlClient's own login timeout won the race it would
         * surface as a SqlException, land in the refusal branch, and report a resuming
         * database as a broken one — which is the exact confusion this class exists to
         * avoid.
         */
        _connectionString = new SqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = ProbeApplicationName,
            ConnectTimeout = SecondsAtLeastOne(probeTimeout) + 5,
        }.ConnectionString;

        _probeTimeout = probeTimeout;
        _commandSeconds = SecondsAtLeastOne(probeTimeout);
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(_probeTimeout);

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(probe.Token);

            /*
             * SELECT 1 rather than opening and closing.
             *
             * Opening proves a login; it does not prove the database will answer. On a
             * serverless tier those are genuinely different states, and readiness is a
             * claim about the second.
             */
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.CommandTimeout = _commandSeconds;
            await command.ExecuteScalarAsync(probe.Token);

            return HealthCheckResult.Healthy("The database answered.");
        }
        catch (Exception) when (probe.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded(
                "The database did not answer within the probe's budget.");
        }
        catch (Exception)
        {
            /*
             * The exception is DROPPED, not attached and not interpolated.
             *
             * /health/ready is unauthenticated, and the health service logs a check's
             * description and exception. A SqlException carries the server name, the
             * database name and, on a command failure, the statement — so the honest
             * sentence here is a fixed one, and diagnosing the failure is a job for the
             * application's own logs on a request that actually did something.
             */
            return HealthCheckResult.Unhealthy("The database refused the connection.");
        }
    }

    /// <summary>
    /// SqlClient reads both timeouts as whole seconds and treats ZERO as "no limit", so a
    /// sub-second probe budget must not be rounded down into one.
    /// </summary>
    private static int SecondsAtLeastOne(TimeSpan value) =>
        Math.Max(1, (int)Math.Ceiling(value.TotalSeconds));
}
