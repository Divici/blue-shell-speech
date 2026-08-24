using System.Net;
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
}
