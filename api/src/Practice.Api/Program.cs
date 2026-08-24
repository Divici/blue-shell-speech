using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

/*
 * Two probes with different jobs (docs/ARCHITECTURE.md).
 *
 *   live  — is the process up? Failing restarts the container.
 *   ready — can it serve traffic? Failing removes it from rotation.
 *
 * `ready` checks SQL and blob storage. It deliberately does NOT check Azure OpenAI:
 * presearch §19 requires patient records, scheduling, and manual notes to keep working
 * when AI is unavailable, so AI being down must never take the app out of rotation.
 */
builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

/*
 * No HTTPS redirection.
 *
 * This container runs behind Container Apps ingress, which terminates TLS and forwards
 * over the internal network. Redirecting here would bounce healthy internal traffic to a
 * port the container does not listen on. TLS enforcement lives at ingress, and HSTS is set
 * by `web` — the only tier a browser ever reaches.
 */

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

app.Run();

/// <summary>
/// Exposed so Practice.Api.Tests can drive the real pipeline through WebApplicationFactory
/// rather than testing a re-declared one.
/// </summary>
public partial class Program;
