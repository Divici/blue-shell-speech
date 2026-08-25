using Practice.Api.Auth;
using Practice.Api.ClinicalNotes;
using Practice.Api.Patients;
using Practice.Api.Scheduling;
using Practice.Api.Startup;
using Practice.Application.Providers;
using Practice.Infrastructure;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

/*
 * Persistence and Identity.
 *
 * The connection string carries NO password. Azure SQL is configured for Entra-only
 * authentication (DECISIONS.md D028) and the container authenticates with its managed
 * identity, so "Authentication=Active Directory Default" is the whole credential story.
 * Locally, docker compose supplies a throwaway SQL login that exists only on a
 * developer's machine.
 */
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Sql")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Sql is not configured. The API cannot start without a database."));

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

/*
 * The provider context is SCOPED: one per request, resolved by middleware from the
 * forwarded public id. Registering it as a singleton would leak one clinician's scope
 * into another request — which, with a global query filter built on it, is the worst
 * possible bug in this system.
 */
builder.Services.AddScoped<RequestProviderContext>();
builder.Services.AddScoped<IProviderContext>(sp => sp.GetRequiredService<RequestProviderContext>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<ProviderSeeder>();

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

app.UseMiddleware<ProviderContextMiddleware>();

app.MapAuthEndpoints();
app.MapPatientEndpoints();
app.MapAppointmentEndpoints();
app.MapNoteEndpoints();

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
