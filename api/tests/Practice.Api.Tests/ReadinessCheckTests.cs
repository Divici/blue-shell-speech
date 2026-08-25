using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Azure.Storage.Blobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Practice.Infrastructure.Health;
using Practice.Infrastructure.Storage;

namespace Practice.Api.Tests;

/// <summary>
/// What each readiness probe actually does when its dependency misbehaves.
///
/// THE TWO FAILURES A READINESS PROBE MUST TELL APART, because answering them the same way
/// breaks the application in opposite directions:
///
///   the dependency REFUSED       a connection string that does not work, a managed
///                                identity that was never granted, a container that does
///                                not exist. A deployment defect, and the only place it is
///                                caught is the revision rollout. Unhealthy → 503 → the
///                                revision does not take traffic.
///
///   the dependency was SLOW      Azure SQL on the free offer auto-pauses, and a resume
///                                takes tens of seconds — far longer than a probe waits.
///                                Degraded → 200 → the replica stays in rotation, because
///                                the only thing that resumes an auto-paused database is a
///                                connection, and a probe that pulls the replica out of
///                                rotation removes the requests that would have woken it.
///                                A strict answer here flaps on every rollout and can
///                                wedge outright.
///
/// The split is on the SHAPE of the failure rather than on a list of error numbers: a
/// misconfiguration is refused immediately, and a resuming database is not refused at all —
/// it simply does not answer inside HealthProbeBounds.Probe. Deriving it that way means
/// there is no enumeration to go stale, which is this repository's recurring way of being
/// wrong (D081→D087, D088, D090).
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class ReadinessCheckTests(SqlServerFixture sql, AzuriteFixture azurite)
{
    private static readonly HealthCheckContext Context = new();

    /// <summary>
    /// A dependency that REFUSES is a deployment defect, and readiness is where it is
    /// caught.
    ///
    /// Forced with the real database and the wrong password, which is the shape of every
    /// misconfiguration that matters here — a connection string that does not work, or an
    /// identity that was never granted. It is refused at once rather than slowly, which is
    /// precisely what separates it from the sleeping database below.
    ///
    /// Control: the <c>catch (Exception) { return HealthCheckResult.Unhealthy(...) }</c>
    /// branch in SqlReadinessCheck.CheckHealthAsync.
    /// Deleted → red, "Microsoft.Data.SqlClient.SqlException : Login failed for user 'sa'."
    /// — the exception escapes the probe instead of being translated into a status, which
    /// at the endpoint would be answered by the registration's failureStatus and, in a log,
    /// by a stack trace naming the server.
    /// </summary>
    [Fact]
    public async Task A_database_that_refuses_the_login_is_unready()
    {
        var wrongPassword = new SqlConnectionStringBuilder(sql.ConnectionString)
        {
            Password = "NotTheP4ssword!",
        }.ConnectionString;

        var check = new SqlReadinessCheck(wrongPassword, TimeSpan.FromSeconds(10));

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    /// <summary>
    /// A DATABASE THAT IS MERELY ASLEEP IS NOT A REASON TO PULL THE REPLICA OUT OF
    /// ROTATION.
    ///
    /// Azure SQL on the free offer auto-pauses and a resume takes tens of seconds, so the
    /// probe running out of time is the NORMAL first answer of the morning rather than a
    /// fault. Answering it Unhealthy would flap the replica out and back on every rollout,
    /// and can wedge: the only thing that resumes a paused database is a connection, and
    /// the connections come from the traffic readiness has just refused.
    ///
    /// Forced with a socket that ACCEPTS AND NEVER ANSWERS, which is the shape of a resume
    /// as SqlClient experiences it — the connection is dialled, the login handshake goes
    /// out, and nothing comes back — without waiting out a real one and without depending
    /// on how fast a local database happens to be. A short budget with a real database
    /// answers Healthy about half the time; a socket that never speaks answers the same way
    /// every run.
    ///
    /// The upper bound is a second claim, and it is the one the constructor comment makes:
    /// the PROBE's budget fires, not SqlClient's own Connect Timeout, which is deliberately
    /// set above it. If the connect timeout won this race the failure would surface as a
    /// SqlException, land in the refusal branch, and report a sleeping database as a broken
    /// one.
    ///
    /// Control: the <c>catch (Exception) when (probe.IsCancellationRequested)</c> branch in
    /// SqlReadinessCheck.CheckHealthAsync.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Degraded, Actual:
    /// Unhealthy" — it falls through to the refusal branch below it, and a sleeping
    /// database is reported as a broken one.
    /// </summary>
    [Fact]
    public async Task A_database_that_does_not_answer_in_time_is_degraded_rather_than_unready()
    {
        using var silent = new SilentPort();

        var budget = TimeSpan.FromMilliseconds(300);
        var check = new SqlReadinessCheck(
            $"Server=127.0.0.1,{silent.Port};Database=BlueShell;User Id=sa;"
            + "Password=NotUsed!123;TrustServerCertificate=True",
            budget);

        var started = Stopwatch.GetTimestamp();
        var result = await check.CheckHealthAsync(Context, CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(HealthStatus.Degraded, result.Status);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"The probe took {elapsed.TotalSeconds:0.00}s against a {budget.TotalSeconds:0.0}s "
            + "budget, so something other than the probe's own bound ended it.");
    }

    /// <summary>
    /// THE PROBE LEAVES NO CONNECTION BEHIND, WHICH IS WHAT KEEPS IT OFF THE BILL.
    ///
    /// A pooled connection is a live SQL session for as long as the pool holds it, and a
    /// live session is a database that cannot auto-pause. The free Azure SQL offer is
    /// denominated in vCore-seconds (CLAUDE.md, D001), so a probe that opened one pooled
    /// connection every five minutes would hold the database online for the whole life of
    /// the replica and quietly bill for the privilege of reassuring an orchestrator.
    ///
    /// Observable rather than asserted about the code: the probe stamps its own
    /// Application Name, so <c>sys.dm_exec_sessions</c> can be asked directly whether
    /// anything is still connected under it.
    ///
    /// Control: <c>Pooling = false</c> in SqlReadinessCheck's connection-string builder.
    /// Deleted → red, "The probe left 1 session(s) open on the database. A pooled connection
    /// is a session, and a session is a database that cannot auto-pause." — the connection
    /// returns to the pool on Dispose rather than closing, and the session survives it.
    /// </summary>
    [Fact]
    public async Task The_probe_leaves_no_session_open_on_the_database()
    {
        var check = new SqlReadinessCheck(sql.ConnectionString, TimeSpan.FromSeconds(10));

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @name;";
        command.Parameters.AddWithValue("@name", SqlReadinessCheck.ProbeApplicationName);

        var open = (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;

        Assert.True(
            open == 0,
            $"The probe left {open} session(s) open on the database. A pooled connection is "
            + "a session, and a session is a database that cannot auto-pause.");
    }

    /// <summary>
    /// A BURST OF PROBES IS ONE ROUND TRIP.
    ///
    /// The other half of the cost argument. Probes arrive every few seconds for as long as
    /// the replica is up; dialling the dependency each time is what would hold an
    /// auto-pausing database online, and it buys nothing — the answer cannot have changed
    /// in the gap.
    ///
    /// Control: <c>CachedReadinessCheck.IsFresh</c>, its body replaced with <c>false</c>.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 1, Actual: 5" —
    /// every probe reaches the dependency.
    ///
    /// IsFresh rather than "the early return", deliberately: there are TWO cache-hit
    /// returns, one before the gate and one inside it, and deleting either alone leaves
    /// this green because the other still answers. Naming one of them would have been a
    /// control that verifies nothing (D077).
    /// </summary>
    [Fact]
    public async Task A_burst_of_probes_is_one_round_trip()
    {
        var inner = new CountsCalls(HealthCheckResult.Healthy());
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var cached = new CachedReadinessCheck<CountsCalls>(inner, clock);

        for (var i = 0; i < 5; i++)
        {
            clock.Now += TimeSpan.FromSeconds(10);
            await cached.CheckHealthAsync(Context, CancellationToken.None);
        }

        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// A SUCCESS IS REUSED FOR MINUTES; ANYTHING ELSE IS RETRIED IN SECONDS.
    ///
    /// The asymmetry is the point. A replica that has recovered has to return to rotation
    /// promptly, so a refusal must not be sticky; a replica that is fine has nothing to
    /// gain from being asked again, so a success can be held for as long as the claim
    /// plausibly holds.
    ///
    /// Control: the <c>HealthProbeBounds.RecheckAfter</c> arm of
    /// CachedReadinessCheck.Reuse, replaced with <c>HealthProbeBounds.HealthyFor</c> so
    /// every result is cached for five minutes.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 2, Actual: 1" —
    /// the replica keeps reporting a refusal it has already recovered from.
    /// </summary>
    [Fact]
    public async Task A_probe_that_did_not_succeed_is_retried_within_seconds()
    {
        var inner = new CountsCalls(HealthCheckResult.Degraded());
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var cached = new CachedReadinessCheck<CountsCalls>(inner, clock);

        await cached.CheckHealthAsync(Context, CancellationToken.None);
        clock.Now += HealthProbeBounds.RecheckAfter + TimeSpan.FromSeconds(1);
        await cached.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    /// <summary>
    /// The other side of the asymmetry, which the test above cannot see.
    ///
    /// Written separately because one test asserting both directions would pass with the
    /// two windows collapsed into a single middling number, and the whole claim is that
    /// they are different (D077: two clauses covering for each other are two tests).
    ///
    /// Control: the <c>HealthProbeBounds.HealthyFor</c> arm of
    /// CachedReadinessCheck.Reuse, replaced with <c>HealthProbeBounds.RecheckAfter</c> so
    /// every result is re-probed within seconds.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: 1, Actual: 2" — a
    /// healthy replica dials its dependencies on nearly every probe, which is the cost this
    /// cache exists to avoid.
    /// </summary>
    [Fact]
    public async Task A_probe_that_succeeded_is_not_retried_within_seconds()
    {
        var inner = new CountsCalls(HealthCheckResult.Healthy());
        var clock = new FixedClock(DateTimeOffset.UnixEpoch);
        var cached = new CachedReadinessCheck<CountsCalls>(inner, clock);

        await cached.CheckHealthAsync(Context, CancellationToken.None);
        clock.Now += HealthProbeBounds.RecheckAfter + TimeSpan.FromSeconds(1);
        await cached.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// Storage that is not configured is unready, AND SAYS SO IN ITS OWN WORDS.
    ///
    /// A probe that drops a check it could not configure returns 200 having asserted
    /// nothing — which is the state this whole task existed to end, one layer in. The
    /// registration is unconditional and the missing configuration is an answer.
    ///
    /// THE SENTENCE IS PART OF THE CLAIM, and asserting only the status was not enough:
    /// this test was written that way, went GREEN with its control deleted, and the reason
    /// is D077's shape — the generic refusal catch below the guard answers Unhealthy for a
    /// NullReferenceException just as readily, so the two covered for each other and the
    /// test could not tell them apart. Neither could an operator reading the response:
    /// "storage refused the request" sends somebody to look at an account, and nothing is
    /// wrong with the account.
    ///
    /// Control: the <c>if (container is null)</c> guard at the top of
    /// BlobReadinessCheck.CheckHealthAsync.
    /// Deleted outright → the BUILD fails first, "error CS8602: Dereference of a possibly
    /// null reference" — nullable analysis is a real second layer here and worth recording.
    /// Deleted with the dereference suppressed (<c>container!</c>) → red,
    /// "Assert.Contains() Failure: Sub-string not found / String: "Blob storage refused the
    /// request." / Not found: "configured"".
    /// </summary>
    [Fact]
    public async Task Storage_that_is_not_configured_is_unready()
    {
        var check = new BlobReadinessCheck(null, HealthProbeBounds.Probe);

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);

        Assert.Contains(
            "configured",
            result.Description ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A container the identity cannot reach is unready — AND THE ANSWER NAMES NEITHER THE
    /// ACCOUNT NOR THE CONTAINER.
    ///
    /// This is the failure that would leak. An Azure SDK exception message carries the full
    /// request URI, so the account and container names travel inside it; the check's own
    /// Description is what reaches the unauthenticated endpoint AND the log line the health
    /// service writes, so neither may echo it.
    ///
    /// Two assertions because there are two claims: that a missing container is refused,
    /// and that refusing it says nothing about the infrastructure it was refused by.
    ///
    /// Control: the <c>: HealthCheckResult.Unhealthy("The configured blob container does
    /// not exist.")</c> arm of the <c>exists.Value</c> ternary in
    /// BlobReadinessCheck.CheckHealthAsync.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Unhealthy, Actual:
    /// Healthy" — a replica with no container to write audio to is routed traffic.
    /// </summary>
    [Fact]
    public async Task A_container_that_does_not_exist_is_unready_and_names_nothing()
    {
        const string missing = "no-such-container";

        var container = new BlobServiceClient(azurite.ConnectionString)
            .GetBlobContainerClient(missing);

        var check = new BlobReadinessCheck(container, TimeSpan.FromSeconds(10));

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);

        Assert.False(
            (result.Description ?? string.Empty).Contains(missing, StringComparison.OrdinalIgnoreCase)
            || (result.Description ?? string.Empty).Contains(
                container.AccountName, StringComparison.OrdinalIgnoreCase),
            $"The refusal named the infrastructure it was refused by: {result.Description}");

        Assert.Null(result.Exception);
    }

    /// <summary>
    /// The success case, through the same factory the application uses.
    ///
    /// <c>ReadinessChecks.SessionAudioContainer</c> rather than a client built here, so the
    /// connection-string branch, the no-retry options and the container name are the ones
    /// production runs — a test that builds its own client proves the check works against a
    /// client the application never constructs.
    ///
    /// Control: the <c>await container.ExistsAsync(probe.Token)</c> call in
    /// BlobReadinessCheck.CheckHealthAsync, replaced with a stubbed
    /// <c>Azure.Response.FromValue(false, null!)</c> so nothing is asked of storage.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Healthy, Actual:
    /// Unhealthy" — the probe reports unready without having looked.
    /// </summary>
    [Fact]
    public async Task A_reachable_container_is_ready()
    {
        var check = new BlobReadinessCheck(
            ReadinessChecks.SessionAudioContainer(azurite.ConnectionString),
            TimeSpan.FromSeconds(10));

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// Storage that is slow is degraded, not unready — the same argument as the sleeping
    /// database, for the same reason.
    ///
    /// Blob storage does not auto-pause, so this window is narrower than SQL's; it is still
    /// the difference between "the network hiccuped" and "this replica is misconfigured",
    /// and answering both with 503 takes a working replica out of rotation over a hiccup.
    ///
    /// Same socket that accepts and never answers, for the same reason: Azurite on loopback
    /// replies in well under a millisecond, so a short budget against the real emulator is
    /// a coin toss rather than a test.
    ///
    /// Control: the <c>catch (Exception) when (probe.IsCancellationRequested)</c> branch in
    /// BlobReadinessCheck.CheckHealthAsync.
    /// Deleted → red, "Assert.Equal() Failure: Values differ, Expected: Degraded, Actual:
    /// Unhealthy".
    /// </summary>
    [Fact]
    public async Task Storage_that_does_not_answer_in_time_is_degraded_rather_than_unready()
    {
        using var silent = new SilentPort();

        var container = new BlobContainerClient(new Uri(
            $"http://127.0.0.1:{silent.Port}/devstoreaccount1/{StorageContainers.SessionAudio}"));

        var check = new BlobReadinessCheck(container, TimeSpan.FromMilliseconds(300));

        var result = await check.CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    /// <summary>
    /// A socket that ACCEPTS AND NEVER ANSWERS — the shape of a dependency that is present
    /// but not talking, which is what an Azure SQL resume looks like from the client side.
    ///
    /// The alternative was a very short budget against the real dependency, and that is a
    /// coin toss: Azurite on loopback answers in well under a millisecond and the emulated
    /// SQL Server is on the same machine. A test whose result depends on which of two
    /// sub-millisecond events happens first is not a test of the branch it names.
    ///
    /// Accepted connections are HELD rather than closed, because closing one turns the wait
    /// into a refusal and a refusal is the other branch entirely.
    /// </summary>
    private sealed class SilentPort : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _accepted = [];
        private readonly CancellationTokenSource _stopping = new();

        public SilentPort()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptAndSayNothingAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();

            lock (_accepted)
            {
                foreach (var client in _accepted)
                {
                    client.Dispose();
                }

                _accepted.Clear();
            }

            _stopping.Dispose();
        }

        private async Task AcceptAndSayNothingAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stopping.Token);

                    lock (_accepted)
                    {
                        _accepted.Add(client);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed while waiting for a connection, which is the ordinary exit.
            }
            catch (ObjectDisposedException)
            {
                // Same, from the other side of the race with Stop().
            }
            catch (SocketException)
            {
                // Same again, on platforms that report it this way.
            }
        }
    }

    /// <summary>
    /// A clock a test can move. <c>TimeProvider.System</c> is what the application resolves;
    /// a five-minute cache window is not something a test can wait out.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// An inner check that records how often the cache let a probe through to it. The count
    /// IS the claim in every cache test here — the returned status is incidental.
    /// </summary>
    private sealed class CountsCalls(HealthCheckResult answer) : IHealthCheck
    {
        public int Calls { get; private set; }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }
}
