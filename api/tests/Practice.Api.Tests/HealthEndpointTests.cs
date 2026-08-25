using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Practice.Infrastructure.Health;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// Slice 0 acceptance: "/health/live and /health/ready return 200".
///
/// Container Apps uses these to decide whether to restart a container and whether to send
/// it traffic. A broken probe does not fail loudly — it produces a container that silently
/// never receives requests, or one that restarts forever.
///
/// The two answer DIFFERENT QUESTIONS and the consequences point in opposite directions,
/// which is what most of the tests below are about: a liveness failure restarts the
/// process, a readiness failure removes it from rotation, and a check placed on the wrong
/// one turns a sleeping database into a restart loop or a broken replica into a healthy
/// one.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class HealthEndpointTests : IDisposable
{
    /*
     * Uses the containerised database, not a bare WebApplicationFactory.
     *
     * The API refuses to start without ConnectionStrings:Sql — deliberately, since an app
     * that boots without a database only fails later and less clearly. These tests
     * previously passed locally only because appsettings.Development.json supplied one,
     * and that file is GITIGNORED: CI had no connection string and every health test
     * failed there while passing on the developer's machine.
     *
     * And the containerised blob emulator, for the same reason one layer along: the
     * readiness probe reads storage, so a factory with no ConnectionStrings:Storage
     * answers 503 and every assertion below would be about a misconfigured test rather
     * than about the application.
     */
    private readonly string _sqlConnectionString;
    private readonly string _storageConnectionString;
    private readonly PracticeApiFactory _factory;

    public HealthEndpointTests(SqlServerFixture sql, AzuriteFixture azurite)
    {
        _sqlConnectionString = sql.ConnectionString;
        _storageConnectionString = azurite.ConnectionString;
        _factory = new PracticeApiFactory(
            _sqlConnectionString, storageConnectionString: _storageConnectionString);
    }

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_return_ok(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Health responses must never be cached. A cached "Healthy" outlives the thing it
    /// described, and the orchestrator then routes traffic to a container that is not.
    /// </summary>
    [Fact]
    public async Task Health_responses_are_not_cached()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/live");

        Assert.True(
            response.Headers.CacheControl?.NoStore == true
            || response.Headers.CacheControl?.NoCache == true,
            $"Expected a no-store/no-cache directive, got: {response.Headers.CacheControl?.ToString() ?? "(none)"}");
    }

    /// <summary>
    /// Liveness must actually run a check.
    ///
    /// A MapHealthChecks predicate that matches no registration returns 200 Healthy — which
    /// is indistinguishable from every dependency passing. Asserting only the status code
    /// produces a test that cannot fail, which is exactly what happened here: the readiness
    /// probe matched zero checks for the whole of slice 0 and 1 while its test stayed green.
    /// </summary>
    [Fact]
    public async Task Liveness_runs_at_least_one_check()
    {
        using var client = _factory.CreateClient();

        var names = await CheckNamesAt("/health/live");

        Assert.True(names.Count > 0, "/health/live ran no checks — it is asserting nothing.");
    }

    /// <summary>
    /// Readiness probes the dependencies it cannot serve without.
    ///
    /// This test used to be <c>Readiness_has_no_dependency_checks_yet</c> and asserted
    /// <c>Assert.Equal(0, count)</c> — a deliberate pin on an empty probe, written so that
    /// registering the checks would break it and the break would be the reminder. It did.
    ///
    /// Asserts a FLOOR on the names rather than an exact set, per docs/TEST_STRATEGY.md:
    /// an exact count is the hard-coded list again, and a third dependency added later
    /// should not have to come here to be allowed.
    ///
    /// Control: the <c>.AddReadinessChecks(...)</c> call in Program.cs.
    /// Deleted → red, "Assert.Contains() Failure: Item not found in set / Set: [] /
    /// Not found: \"sql\"" — the predicate matches nothing and the probe answers 200 while
    /// asserting nothing at all, which is the state this task existed to end.
    /// </summary>
    [Fact]
    public async Task Readiness_runs_the_dependency_checks()
    {
        var names = await CheckNamesAt("/health/ready");

        Assert.Contains(ReadinessChecks.SqlCheckName, names);
        Assert.Contains(ReadinessChecks.BlobCheckName, names);
    }

    /// <summary>
    /// LIVENESS ASKS NOTHING OF ANOTHER MACHINE, and this is the test that keeps it that
    /// way.
    ///
    /// A failing liveness probe restarts the container. Azure SQL on the free offer
    /// auto-pauses, so a liveness check that dialled it would restart a perfectly healthy
    /// process because a database was asleep — and a restart cannot wake a database, it
    /// only puts a cold start in front of the resume. Same for blob storage, minus the
    /// pausing.
    ///
    /// Derived rather than listed: it asks the running application what each probe actually
    /// ran and asserts the two sets are disjoint, so a dependency check that later acquires
    /// a "live" tag fails here without anyone having remembered to add it to a list
    /// (docs/TEST_STRATEGY.md, "a guard over a SET enumerates the set").
    ///
    /// Control: the <c>tags: [ReadinessChecks.ReadyTag]</c> argument on the two
    /// registrations in ReadinessChecks.AddReadinessChecks, replaced with
    /// <c>[ReadinessChecks.ReadyTag, ReadinessChecks.LiveTag]</c> — a dependency check that
    /// also answers liveness, which is the mistake this test is about.
    /// Deleted → red, "Liveness ran a dependency check: sql, blob. A failing liveness probe
    /// RESTARTS the container.".
    /// </summary>
    [Fact]
    public async Task Liveness_asks_nothing_of_a_dependency()
    {
        var live = await CheckNamesAt("/health/live");
        var ready = await CheckNamesAt("/health/ready");

        Assert.True(ready.Count > 0, "Readiness ran no checks, so this test proves nothing.");

        var both = live.Intersect(ready, StringComparer.Ordinal).ToList();

        Assert.True(
            both.Count == 0,
            $"Liveness ran a dependency check: {string.Join(", ", both)}. "
            + "A failing liveness probe RESTARTS the container.");
    }

    /// <summary>
    /// A PROBE IS BOUNDED AT PROBE SCALE, NOT AT REQUEST SCALE.
    ///
    /// The default RequestTimeoutPolicy is DatabaseTimeouts.Request — ten minutes and
    /// twenty seconds — and every term in it is justified for a request a clinician is
    /// waiting on against a database resuming from auto-pause (D086, D090). None of it is
    /// true of a probe: an orchestrator asked, it will ask again in seconds, and an answer
    /// that arrives ten minutes later answers a question already decided three ways. Both
    /// health routes inherited that bound until this task.
    ///
    /// Walks the route table rather than naming the two routes, so <c>/health/startup</c>
    /// added later arrives bounded or arrives red. Asserts a floor on what the walk found,
    /// because a walk that finds nothing registers nothing and passes.
    ///
    /// This asserts the metadata RequestTimeoutsMiddleware reads, not the cut-off itself —
    /// <c>RequestBoundsTests.A_request_that_outlives_its_caller_is_stopped</c> is the test
    /// that proves the middleware acts on it, and duplicating that here would cost five
    /// seconds of wall clock to re-prove somebody else's claim. What is genuinely specific
    /// to these two routes is WHICH bound governs them, which is what this reads.
    ///
    /// Control: the <c>.WithRequestTimeout(HealthProbeBounds.EndpointTimeout)</c> call on
    /// the two MapHealthChecks calls in Program.cs.
    /// Deleted → red, "/health/live carries no request-timeout metadata, so it inherits the
    /// default policy — 00:10:20.".
    /// </summary>
    [Fact]
    public void A_health_probe_is_bounded_at_probe_scale_not_request_scale()
    {
        var health = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/health", StringComparison.Ordinal) == true)
            .ToList();

        Assert.True(
            health.Count >= 2,
            $"Found {health.Count} /health routes. This walk is meant to find every probe, "
            + "and finding none would leave it green while guarding nothing.");

        foreach (var endpoint in health)
        {
            /*
             * Either shape counts. WithRequestTimeout has three overloads and they express
             * the same claim through different metadata; the assertion is that the endpoint
             * carries a bound of its OWN, not that it was written one particular way.
             */
            var bound =
                endpoint.Metadata.GetMetadata<RequestTimeoutPolicy>()?.Timeout
                ?? endpoint.Metadata.GetMetadata<RequestTimeoutAttribute>()?.Timeout;

            Assert.True(
                bound is not null,
                $"{endpoint.RoutePattern.RawText} carries no request-timeout metadata, so it "
                + $"inherits the default policy — {DatabaseTimeouts.Request}.");

            Assert.Equal(HealthProbeBounds.EndpointTimeout, bound);

            Assert.True(
                bound < DatabaseTimeouts.Request,
                $"{endpoint.RoutePattern.RawText} is bounded at {bound}, which is not below "
                + $"the request bound of {DatabaseTimeouts.Request}.");
        }
    }

    /// <summary>
    /// A NAME AND A STATUS, AND NOTHING ELSE — on an UNAUTHENTICATED endpoint.
    ///
    /// Both health routes are open, so whatever the response writer emits is emitted to
    /// whoever asks. The framework offers Description, Exception, Data and Duration on
    /// every entry, and the interesting one is Exception: an Azure SDK failure carries the
    /// full request URI (account and container) and a SqlException carries the server name.
    ///
    /// An ALLOWLIST over the property names actually present, not a denylist of the four
    /// fields that exist today — "anything but X" grows with the framework and "only Y"
    /// does not (docs/TEST_STRATEGY.md, D090).
    ///
    /// Control: the payload projection inside <c>WriteHealthResponse</c> in Program.cs,
    /// with <c>description = entry.Value.Description</c> added to the per-check object —
    /// the smallest realistic widening, and one whose values are this application's own
    /// fixed sentences rather than anything leaked.
    /// Deleted → red, "/health/ready emitted a property this test does not recognise:
    /// 'description'. This endpoint is unauthenticated.".
    /// </summary>
    [Fact]
    public async Task Health_output_carries_a_name_and_a_status_and_nothing_else()
    {
        string[] allowed = ["status", "checkCount", "checks", "name"];

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var property in PropertyNames(json.RootElement))
        {
            Assert.True(
                allowed.Contains(property, StringComparer.Ordinal),
                $"/health/ready emitted a property this test does not recognise: "
                + $"'{property}'. This endpoint is unauthenticated.");
        }
    }

    /// <summary>
    /// A CHECK THAT FAILS SAYS THAT IT FAILED, AND NOT WHY.
    ///
    /// The half the allowlist above cannot reach: on a healthy run the framework populates
    /// nothing interesting, so a writer that emitted <c>Exception</c> would look correct
    /// until the day something broke. This forces the failure and reads the body.
    ///
    /// The marker is shaped like what a real failure would actually carry — the storage
    /// account and container out of an Azure SDK request URI, and a server and database
    /// name out of a SqlException. Synthetic; no such account exists.
    ///
    /// Control: the payload projection inside <c>WriteHealthResponse</c> in Program.cs,
    /// with <c>detail = entry.Value.Exception?.Message</c> added to the per-check object.
    /// Deleted → red, "The failing check published: blueshelldevstorage.blob.core.windows.net".
    /// </summary>
    [Fact]
    public async Task A_failing_check_says_nothing_about_why()
    {
        string[] markers =
        [
            "blueshelldevstorage.blob.core.windows.net",
            "session-audio",
            "tcp:blueshell-dev-sql.database.windows.net,1433",
        ];

        using var broken = new PracticeApiFactory(
            _sqlConnectionString,
            services => services
                .AddHealthChecks()
                .AddCheck(
                    "leaks",
                    new ThrowsWith(string.Join(" ", markers)),
                    tags: [ReadinessChecks.ReadyTag]),
            _storageConnectionString);

        using var client = broken.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        foreach (var marker in markers)
        {
            Assert.False(
                body.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"The failing check published: {marker}");
        }
    }

    private async Task<HashSet<string>> CheckNamesAt(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return json.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every property name anywhere in the document, however deeply nested.</summary>
    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A check that fails the way a real dependency fails: by throwing something whose
    /// message names the infrastructure it was talking to.
    /// </summary>
    private sealed class ThrowsWith(string message) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }
}
