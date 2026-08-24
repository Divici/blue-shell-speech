using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Practice.Api.Tests;

/// <summary>
/// Slice 0 acceptance: "/health/live and /health/ready return 200".
///
/// Container Apps uses these to decide whether to restart a container and whether to send
/// it traffic. A broken probe does not fail loudly — it produces a container that silently
/// never receives requests, or one that restarts forever.
/// </summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

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

        using var response = await client.GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var count = json.RootElement.GetProperty("checkCount").GetInt32();
        Assert.True(count > 0, $"/health/live ran {count} checks — it is asserting nothing.");
    }

    /// <summary>
    /// Readiness currently runs NO dependency checks, because EF Core and the storage
    /// client do not exist yet.
    ///
    /// This test pins that state deliberately rather than pretending the probe is
    /// meaningful. When slice 3 registers the SQL and blob checks, this test fails and
    /// must be replaced by <c>Assert.True(count > 0)</c> — a failure that is the reminder.
    /// </summary>
    [Fact]
    public async Task Readiness_has_no_dependency_checks_yet()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        var count = json.RootElement.GetProperty("checkCount").GetInt32();
        Assert.Equal(0, count);
    }
}
