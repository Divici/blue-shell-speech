using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.Patients;
using Practice.Domain.Auditing;
using Practice.Domain.Patients;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// Tenancy isolation, against real SQL Server.
///
/// This is the most important test class in the project so far. The practice has one
/// clinician today, so none of it can be verified by using the app — and by the time a
/// second provider exists, a leak would already have happened.
///
/// The rule being tested: <b>a provider can reach their own records and nothing else, and
/// cannot tell whether anything else exists.</b>
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class PatientIsolationTests(SqlServerFixture sql) : IDisposable
{
    private readonly PracticeApiFactory _factory = new(sql.ConnectionString);

    public void Dispose() => _factory.Dispose();

    /// <summary>Creates a provider and returns the public id the BFF would forward.</summary>
    private async Task<Guid> SeedProviderAsync(string name)
    {
        using var scope = _factory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PracticeUser>>();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        var user = new PracticeUser { UserName = email, Email = email };
        await users.CreateAsync(user, "correct-horse-battery-staple");

        var provider = Provider.Create(user.Id, name, "M.S., CCC-SLP", "SLP-1", "MD");
        db.Providers.Add(provider);
        await db.SaveChangesAsync();

        return provider.PublicId;
    }

    /// <summary>A client that presents a provider identity, as the BFF does.</summary>
    private HttpClient ClientFor(Guid providerPublicId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    private static CreatePatientRequest NewPatient(string first = "Maya", string last = "Reyes") =>
        new(first, last, new DateOnly(2024, 2, 24), "Expressive language delay.");

    private static async Task<PatientDetail> CreatePatientAsync(HttpClient client, string last = "Reyes")
    {
        var response = await client.PostAsJsonAsync("/patients", NewPatient(last: last));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
    }

    // ------------------------------------------------------------- isolation

    /// <summary>
    /// The headline rule. Another provider's patient must be indistinguishable from a
    /// patient that does not exist.
    /// </summary>
    [Fact]
    public async Task A_provider_cannot_read_another_providers_patient()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.GetAsync($"/patients/{patient.PublicId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 404, never 403.
    ///
    /// A 403 says "this exists and you may not have it", which is an enumeration oracle:
    /// an attacker learns which identifiers are real. A record they cannot reach must be
    /// identical to a record that is not there.
    /// </summary>
    [Fact]
    public async Task An_inaccessible_patient_is_indistinguishable_from_a_missing_one()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var foreign = await stranger.GetAsync($"/patients/{patient.PublicId}");
        using var absent = await stranger.GetAsync($"/patients/{Guid.NewGuid()}");

        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_provider_cannot_update_another_providers_patient()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.PutAsJsonAsync(
            $"/patients/{patient.PublicId}",
            new UpdatePatientRequest("Hacked", "Name", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the record is untouched.
        using var reread = await michelle.GetAsync($"/patients/{patient.PublicId}");
        var current = (await reread.Content.ReadFromJsonAsync<PatientDetail>())!;
        Assert.Equal("Maya", current.FirstName);
    }

    [Fact]
    public async Task A_provider_cannot_discharge_another_providers_patient()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.PostAsync(
            $"/patients/{patient.PublicId}/discharge", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_cannot_attach_a_guardian_to_another_providers_patient()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.PostAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians",
            new AddGuardianRequest("Mallory", "Stranger", "Mother", "410-555-0199", null, true, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_own_patients()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        await CreatePatientAsync(michelle, last: "Reyes");
        await CreatePatientAsync(stranger, last: "Nakamura");

        var mine = await michelle.GetFromJsonAsync<List<PatientSummary>>("/patients");

        Assert.NotNull(mine);
        Assert.All(mine, p => Assert.Equal("Reyes", p.LastName));
    }

    /// <summary>
    /// Search must not become a side channel. A prefix that matches another provider's
    /// patient must return nothing rather than a hit count.
    /// </summary>
    [Fact]
    public async Task Search_cannot_reach_another_providers_patients()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        await CreatePatientAsync(stranger, last: "Nakamura");

        var results = await michelle.GetFromJsonAsync<List<PatientSummary>>(
            "/patients?search=Naka");

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    /// <summary>
    /// No provider header at all must behave like no access — not like full access.
    ///
    /// The query filter treats a null provider as matching nothing, which is the safe
    /// direction. This asserts the endpoint agrees.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_provider_identity_is_rejected()
    {
        using var anonymous = _factory.CreateClient();

        using var list = await anonymous.GetAsync("/patients");
        using var get = await anonymous.GetAsync($"/patients/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
    }

    /// <summary>An unknown provider id resolves to null, and null reaches nothing.</summary>
    [Fact]
    public async Task An_unknown_provider_identity_reaches_nothing()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        using var forged = ClientFor(Guid.NewGuid());
        using var response = await forged.GetAsync($"/patients/{patient.PublicId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------- behaviour

    [Fact]
    public async Task A_patient_can_be_created_read_and_updated_by_its_owner()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        var created = await CreatePatientAsync(michelle);
        Assert.Equal("Maya", created.FirstName);
        Assert.Equal("Active", created.Status);

        using var updated = await michelle.PutAsJsonAsync(
            $"/patients/{created.PublicId}",
            new UpdatePatientRequest("Maya", "Reyes-Smith", "Updated summary."));

        var detail = (await updated.Content.ReadFromJsonAsync<PatientDetail>())!;
        Assert.Equal("Reyes-Smith", detail.LastName);
        Assert.Equal("Updated summary.", detail.ClinicalSummary);
    }

    /// <summary>Discharge retains the record. Clinical rows are never removed.</summary>
    [Fact]
    public async Task Discharge_retains_the_record()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        using var discharged = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/discharge", null);
        discharged.EnsureSuccessStatusCode();

        // Hidden from the default list…
        var active = await michelle.GetFromJsonAsync<List<PatientSummary>>("/patients");
        Assert.DoesNotContain(active!, p => p.PublicId == patient.PublicId);

        // …but still retrievable, and still there.
        using var stillThere = await michelle.GetAsync($"/patients/{patient.PublicId}");
        stillThere.EnsureSuccessStatusCode();
        var detail = (await stillThere.Content.ReadFromJsonAsync<PatientDetail>())!;
        Assert.Equal("Discharged", detail.Status);
        Assert.Equal("Maya", detail.FirstName);
    }

    [Fact]
    public async Task A_typo_in_the_date_of_birth_is_rejected_with_a_readable_reason()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        using var response = await michelle.PostAsJsonAsync(
            "/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(1900, 1, 1), null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("typo", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The database enforces one primary contact per patient, not just the aggregate.
    /// </summary>
    [Fact]
    public async Task Adding_a_second_primary_contact_demotes_the_first()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        await michelle.PostAsJsonAsync($"/patients/{patient.PublicId}/guardians",
            new AddGuardianRequest("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true));

        using var second = await michelle.PostAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians",
            new AddGuardianRequest("Sam", "Reyes", "Father", "410-555-0143", null, true, true));

        second.EnsureSuccessStatusCode();
        var detail = (await second.Content.ReadFromJsonAsync<PatientDetail>())!;

        Assert.Single(detail.Guardians, g => g.IsPrimaryContact);
        Assert.Equal("Sam", detail.Guardians.Single(g => g.IsPrimaryContact).FirstName);
    }

    // ----------------------------------------------------------------- audit

    /// <summary>
    /// Reading a record is auditable under HIPAA, not only changing one.
    /// </summary>
    [Fact]
    public async Task Viewing_a_patient_writes_an_audit_row()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        using var _ = await michelle.GetAsync($"/patients/{patient.PublicId}");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var viewed = await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityPublicId == patient.PublicId
                     && e.EventType == AuditEventType.PatientViewed)
            .ToListAsync();

        Assert.NotEmpty(viewed);
    }

    /// <summary>
    /// The audit log must never carry clinical content — it is the table most likely to be
    /// exported to a SIEM or read by a third party during an investigation.
    /// </summary>
    [Fact]
    public async Task Audit_rows_never_contain_patient_names_or_clinical_text()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);
        using var _ = await michelle.GetAsync($"/patients/{patient.PublicId}");

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var metadata = await db.AuditEvents.AsNoTracking()
            .Select(e => e.Metadata).ToListAsync();

        Assert.DoesNotContain(metadata, m => m is not null && m.Contains("Maya", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata, m => m is not null && m.Contains("Reyes", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata,
            m => m is not null && m.Contains("Expressive language delay", StringComparison.Ordinal));
    }
}
