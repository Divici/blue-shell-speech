using System.Text.Json;
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
 * `ready` will check SQL and blob storage. It deliberately will NOT check Azure OpenAI:
 * presearch §19 requires patient records, scheduling, and manual notes to keep working
 * when AI is unavailable, so AI being down must never take the app out of rotation.
 *
 * TODO(slice 3): register the SQL and blob checks with the "ready" tag once EF Core and
 * the storage client exist. Until then the readiness probe reports zero dependency
 * checks — see the response writer below, which makes that visible rather than
 * reporting a bare, meaningless 200.
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
 * port the container does not listen on. TLS enforcement lives at ingress, and HSTS is
 * set by `web` — the only tier a browser ever reaches.
 */

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse,
});

app.Run();

/// <summary>
/// Writes which checks actually ran, not just an aggregate status.
///
/// A health endpoint whose predicate matches nothing returns 200 Healthy — indistinguishable
/// from one where every dependency passed. That is how a readiness probe ends up asserting
/// nothing while an orchestrator routes traffic to a replica that cannot reach its database.
/// Naming the checks makes an empty probe obvious to a human and assertable by a test.
/// </summary>
static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        checkCount = report.Entries.Count,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
        }),
    };

    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

/// <summary>
/// Exposed so Practice.Api.Tests can drive the real pipeline through WebApplicationFactory
/// rather than testing a re-declared one.
/// </summary>
public partial class Program;
