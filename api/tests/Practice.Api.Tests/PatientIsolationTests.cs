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

    // ------------------------------------------- guardians: who may consent

    private static AddGuardianRequest NewGuardian(
        string first = "Jordan",
        string last = "Reyes",
        bool primary = true,
        bool? authority = false) =>
        new(first, last, "Mother", "410-555-0142", null, primary, authority);

    private static async Task<GuardianDto> AddGuardianAsync(
        HttpClient client, Guid patient, AddGuardianRequest request)
    {
        using var response = await client.PostAsJsonAsync(
            $"/patients/{patient}/guardians", request);
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
        return detail.Guardians.Single(g => g.FirstName == request.FirstName);
    }

    /// <summary>
    /// Plants a guardian OWNED BY ANOTHER PROVIDER on this patient, by raw INSERT.
    ///
    /// The API cannot produce this row — every guardian it writes inherits the patient's
    /// ProviderId — and that is precisely why it has to be constructed directly (D066).
    /// Without it the Patient filter and the Guardian filter each cover for the other on
    /// every guardian test: a foreign guardian is only ever reachable through a foreign
    /// patient, which the Patient filter has already removed, so the Guardian filter could
    /// be deleted outright and nothing would go red.
    /// </summary>
    private async Task<Guid> PlantForeignGuardianAsync(Guid patientPublicId, Guid ownerPublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        // IgnoreQueryFilters: a test scope carries no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing. These reads are
        // deliberately looking past it at the raw rows.
        var patientId = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PublicId == patientPublicId).Select(p => p.Id).SingleAsync();
        var ownerId = await db.Providers.AsNoTracking()
            .Where(p => p.PublicId == ownerPublicId).Select(p => p.Id).SingleAsync();

        var publicId = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.Guardians
                (PublicId, ProviderId, PatientId, FirstName, LastName, Relationship,
                 Phone, Email, IsPrimaryContact, HasLegalAuthority, CreatedAtUtc)
            VALUES
                ({publicId}, {ownerId}, {patientId}, N'Mallory', N'Stranger', N'Mother',
                 N'410-555-0199', NULL, 0, 1, SYSUTCDATETIME())
            """);

        return publicId;
    }

    /// <summary>Same technique, same reason, for the address filter.</summary>
    private async Task<Guid> PlantForeignAddressAsync(Guid patientPublicId, Guid ownerPublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var patientId = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PublicId == patientPublicId).Select(p => p.Id).SingleAsync();
        var ownerId = await db.Providers.AsNoTracking()
            .Where(p => p.PublicId == ownerPublicId).Select(p => p.Id).SingleAsync();

        var publicId = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.PatientAddresses
                (PublicId, ProviderId, PatientId, Line1, Line2, City, State, PostalCode,
                 AddressType, Notes, EffectiveFrom, EffectiveTo, CreatedAtUtc)
            VALUES
                ({publicId}, {ownerId}, {patientId}, N'99 Foreign Way', NULL, N'Towson',
                 'MD', N'21204', 1, NULL, '2025-01-01', NULL, SYSUTCDATETIME())
            """);

        return publicId;
    }

    /// <summary>
    /// A guardian on another provider's patient is not editable, and the refusal is the
    /// one an absent record produces.
    ///
    /// The foreign guardian is planted with the STRANGER's own ProviderId so that the
    /// Guardian filter is satisfied for them. That leaves the Patient filter as the only
    /// thing in the way, which is what this test is about — see PlantForeignGuardianAsync.
    ///
    /// Control: the Patient global query filter in PracticeDbContext.
    /// Deleted → red on the first assertion, "Assert.Equal() Failure: Values differ /
    /// Expected: NotFound / Actual: OK" — the stranger's edit lands on another
    /// clinician's record.
    /// </summary>
    [Fact]
    public async Task A_provider_cannot_edit_a_guardian_on_another_providers_patient()
    {
        var strangerId = await SeedProviderAsync("Stranger");
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(strangerId);

        var patient = await CreatePatientAsync(michelle);
        await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());
        var reachable = await PlantForeignGuardianAsync(patient.PublicId, strangerId);

        using var response = await stranger.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians/{reachable}",
            new UpdateGuardianRequest(
                "Mallory", "Stranger", "Mother", "410-555-0199", null, false, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var absent = await stranger.PutAsJsonAsync(
            $"/patients/{Guid.NewGuid()}/guardians/{Guid.NewGuid()}",
            new UpdateGuardianRequest(
                "Mallory", "Stranger", "Mother", "410-555-0199", null, false, true));

        Assert.Equal(absent.StatusCode, response.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The Guardian query filter, tested where it is the ONLY control in the way: a
    /// patient the caller genuinely owns, carrying a guardian row owned by somebody else.
    /// The Patient filter cannot cover here, because the patient is Michelle's.
    ///
    /// It matters beyond tidiness. A guardian is the answer to "who may receive this
    /// child's records"; a row from another practice appearing in that list is a name and
    /// a phone number crossing a tenancy boundary, on the one screen where the reader is
    /// deciding who to release a file to.
    ///
    /// Control: the Guardian global query filter in PracticeDbContext.
    /// Deleted → red on Assert.Single, "Assert.Single() Failure: The collection contained
    /// 2 items".
    /// </summary>
    [Fact]
    public async Task A_guardian_owned_by_another_provider_is_invisible_on_a_patient_the_caller_owns()
    {
        var strangerId = await SeedProviderAsync("Stranger");
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        var patient = await CreatePatientAsync(michelle);
        await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());
        await PlantForeignGuardianAsync(patient.PublicId, strangerId);

        var detail = await michelle.GetFromJsonAsync<PatientDetail>($"/patients/{patient.PublicId}");

        var only = Assert.Single(detail!.Guardians);
        Assert.Equal("Jordan", only.FirstName);
    }

    /// <summary>
    /// The subtler shape: a real session, a real patient of the caller's own, and a
    /// guardian id belonging to a DIFFERENT patient. The query filter is no help here —
    /// both rows may belong to the same provider — so the aggregate has to scope the
    /// lookup to the guardians of the patient in the URL.
    ///
    /// Control: Patient.UpdateGuardian — the g.PublicId == guardianPublicId lookup over
    /// this patient's own collection. Replaced with FirstOrDefault() → red on the status
    /// code, "Assert.Equal() Failure: Values differ / Expected: NotFound / Actual: OK" —
    /// the edit lands on whichever guardian happens to be first on the OTHER patient.
    /// </summary>
    [Fact]
    public async Task A_guardian_id_from_another_patient_is_not_reachable_through_this_ones_url()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        var maya = await CreatePatientAsync(michelle, last: "Reyes");
        var noah = await CreatePatientAsync(michelle, last: "Okafor");

        var mayasGuardian = await AddGuardianAsync(michelle, maya.PublicId, NewGuardian());
        await AddGuardianAsync(michelle, noah.PublicId, NewGuardian(first: "Sam"));

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{noah.PublicId}/guardians/{mayasGuardian.PublicId}",
            new UpdateGuardianRequest(
                "Overwritten", "Reyes", "Mother", "410-555-0142", null, true, true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var noahsRecord = await michelle.GetFromJsonAsync<PatientDetail>($"/patients/{noah.PublicId}");
        Assert.Equal("Sam", noahsRecord!.Guardians.Single().FirstName);
    }

    /// <summary>
    /// Editing works for the owner, and the details that change are the ones that were sent.
    ///
    /// Control: PatientEndpoints — the MapPut route registration for this endpoint.
    /// Deleted → red on EnsureSuccessStatusCode, "System.Net.Http.HttpRequestException :
    /// Response status code does not indicate success: 404 (Not Found)."
    /// </summary>
    [Fact]
    public async Task A_guardian_can_be_edited_by_the_provider_who_owns_the_record()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);
        var guardian = await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians/{guardian.PublicId}",
            new UpdateGuardianRequest(
                "Jordan", "Okafor", "Stepmother", "410-555-0155",
                "jordan.okafor@example.com", true, false));

        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
        var updated = detail.Guardians.Single();

        Assert.Equal("Okafor", updated.LastName);
        Assert.Equal("Stepmother", updated.Relationship);
        Assert.Equal("jordan.okafor@example.com", updated.Email);
        Assert.True(updated.IsPrimaryContact);
        Assert.False(updated.HasLegalAuthority);
    }

    /// <summary>
    /// Promoting on EDIT has to survive UX_Guardians_OnePrimaryPerPatient, which is a
    /// filtered UNIQUE index and therefore checked per statement rather than at commit.
    /// The aggregate demotes and promotes in one SaveChanges, so this test is the only
    /// thing that says the two updates reach SQL Server in an order it accepts.
    ///
    /// Control: Patient.UpdateGuardian — the loop clearing the flag on the other guardians.
    /// Deleted → red on EnsureSuccessStatusCode, "System.Net.Http.HttpRequestException :
    /// Response status code does not indicate success: 500 (Internal Server Error)." — the
    /// unique index rejects the second primary, which is the index doing its job and the
    /// aggregate failing to do its own.
    /// </summary>
    [Fact]
    public async Task Promoting_a_guardian_on_edit_demotes_the_previous_primary_contact()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());
        var father = await AddGuardianAsync(
            michelle, patient.PublicId, NewGuardian(first: "Sam", primary: false));

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians/{father.PublicId}",
            new UpdateGuardianRequest("Sam", "Reyes", "Father", "410-555-0143", null, true, false));

        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;

        Assert.Single(detail.Guardians, g => g.IsPrimaryContact);
        Assert.Equal("Sam", detail.Guardians.Single(g => g.IsPrimaryContact).FirstName);
    }

    /// <summary>
    /// THE HEADLINE RULE OF THIS SLICE, at the API boundary.
    ///
    /// Being the primary contact grants nothing. A stepparent can be the adult who brings
    /// the child every week and hold no authority to consent to treatment or to receive
    /// the file; a non-custodial parent can hold that authority and never appear at a
    /// session. Saving a guardian must not move the answer to a question nobody asked.
    ///
    /// Control: PatientEndpoints.UpdateGuardian — request.HasLegalAuthority reaching the
    /// aggregate. Replaced with request.IsPrimaryContact → red on the first assertion,
    /// "Assert.False() Failure / Expected: False / Actual: True": the stepparent gained
    /// the right to the file by being the person who brings the child.
    /// </summary>
    [Fact]
    public async Task Becoming_the_primary_contact_does_not_grant_legal_authority()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        // A stepparent: the contact, and nothing more.
        var stepparent = await AddGuardianAsync(
            michelle, patient.PublicId, NewGuardian(first: "Alex", primary: true, authority: false));

        // A non-custodial parent: the authority, and not the contact.
        await AddGuardianAsync(
            michelle, patient.PublicId,
            new AddGuardianRequest("Sam", "Reyes", "Father", "410-555-0143", null, false, true));

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians/{stepparent.PublicId}",
            new UpdateGuardianRequest(
                "Alex", "Reyes", "Stepmother", "410-555-0142", null, true, false));

        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;

        Assert.False(detail.Guardians.Single(g => g.FirstName == "Alex").HasLegalAuthority);
        Assert.True(detail.Guardians.Single(g => g.FirstName == "Sam").HasLegalAuthority);
        Assert.False(detail.Guardians.Single(g => g.FirstName == "Sam").IsPrimaryContact);
    }

    /// <summary>
    /// A body that never mentions legal authority is REFUSED, not defaulted.
    ///
    /// The column is a bit and cannot hold "nobody said". System.Text.Json would turn a
    /// missing bool into false, so silence would be persisted as a decision that the child's
    /// parent may not have the file — and be indistinguishable from someone deciding that.
    /// Rejecting it keeps the two apart at the only layer that can still tell them apart.
    ///
    /// Control: PatientEndpoints — the `request.HasLegalAuthority is null` checks together
    /// with the bool? on both request records. Reverted to a plain bool → red on the first
    /// assertion, "Assert.Equal() Failure: Values differ / Expected: BadRequest /
    /// Actual: OK" — silence was accepted and written down as a decision.
    /// </summary>
    [Fact]
    public async Task A_guardian_saved_without_saying_anything_about_legal_authority_is_refused()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        var silent = JsonContent.Create(new
        {
            firstName = "Jordan",
            lastName = "Reyes",
            relationship = "Mother",
            phone = "410-555-0142",
            email = (string?)null,
            isPrimaryContact = true,
        });

        using var created = await michelle.PostAsync($"/patients/{patient.PublicId}/guardians", silent);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var guardian = await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());

        var silentEdit = JsonContent.Create(new
        {
            firstName = "Jordan",
            lastName = "Reyes",
            relationship = "Mother",
            phone = "410-555-0142",
            email = (string?)null,
            isPrimaryContact = true,
        });

        using var edited = await michelle.PutAsync(
            $"/patients/{patient.PublicId}/guardians/{guardian.PublicId}", silentEdit);

        Assert.Equal(HttpStatusCode.BadRequest, edited.StatusCode);
    }

    /// <summary>
    /// Who may receive a child's records, and when that changed, is the question the audit
    /// log exists to answer about this table. The metadata names the guardian by opaque id
    /// and says granted or withheld — no names, no clinical content.
    ///
    /// Control: PatientEndpoints.UpdateGuardian — the audit.WriteAsync call.
    /// Deleted → red on Assert.Contains, "Assert.Contains() Failure: Filter not matched in
    /// collection".
    /// </summary>
    [Fact]
    public async Task Granting_legal_authority_is_audited_without_naming_anybody()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);
        var guardian = await AddGuardianAsync(michelle, patient.PublicId, NewGuardian());

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/guardians/{guardian.PublicId}",
            new UpdateGuardianRequest("Jordan", "Reyes", "Mother", "410-555-0142", null, true, true));
        response.EnsureSuccessStatusCode();

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var metadata = await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityPublicId == patient.PublicId
                     && e.EventType == AuditEventType.PatientUpdated)
            .Select(e => e.Metadata)
            .ToListAsync();

        Assert.Contains(metadata, m => m is not null
            && m.Contains("guardian-updated", StringComparison.Ordinal)
            && m.Contains("legalAuthority=granted", StringComparison.Ordinal));

        Assert.DoesNotContain(metadata, m => m is not null
            && m.Contains("Jordan", StringComparison.Ordinal));
    }

    // -------------------------------------------- addresses: where they live

    private static AddAddressRequest NewAddress(
        string line1 = "14 Elm Street",
        AddressType type = AddressType.Session,
        DateOnly? from = null) =>
        new(line1, null, "Towson", "MD", "21204", type, "Gate code 4821", from);

    private static async Task<AddressDto> AddAddressAsync(
        HttpClient client, Guid patient, AddAddressRequest request)
    {
        using var response = await client.PostAsJsonAsync(
            $"/patients/{patient}/addresses", request);
        response.EnsureSuccessStatusCode();
        var detail = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
        return detail.Addresses.Single(a => a.Line1 == request.Line1);
    }

    /// <summary>
    /// Same construction as the guardian case, and for the same reason: the foreign address
    /// carries the stranger's own ProviderId, so the PatientAddress filter lets it through
    /// and the Patient filter is the only control left standing.
    ///
    /// Control: the Patient global query filter in PracticeDbContext.
    /// Deleted → red on the first assertion, "Assert.Equal() Failure: Values differ /
    /// Expected: NotFound / Actual: OK".
    /// </summary>
    [Fact]
    public async Task A_provider_cannot_correct_an_address_on_another_providers_patient()
    {
        var strangerId = await SeedProviderAsync("Stranger");
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(strangerId);

        var patient = await CreatePatientAsync(michelle);
        await AddAddressAsync(michelle, patient.PublicId, NewAddress());
        var reachable = await PlantForeignAddressAsync(patient.PublicId, strangerId);

        using var response = await stranger.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/addresses/{reachable}",
            new CorrectAddressRequest("1 Nowhere Road", null, "Towson", "MD", "21204", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The PatientAddress query filter, where it is the only control in the way.
    ///
    /// An address is where Michelle drives to and what a past note refers to. Another
    /// practice's address appearing on her patient would be a home address crossing a
    /// tenancy boundary — and, on a page that offers "correct this address", one she could
    /// edit.
    ///
    /// Control: the PatientAddress global query filter in PracticeDbContext.
    /// Deleted → red on Assert.Single, "Assert.Single() Failure: The collection contained
    /// 2 items".
    /// </summary>
    [Fact]
    public async Task An_address_owned_by_another_provider_is_invisible_on_a_patient_the_caller_owns()
    {
        var strangerId = await SeedProviderAsync("Stranger");
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        var patient = await CreatePatientAsync(michelle);
        await AddAddressAsync(michelle, patient.PublicId, NewAddress());
        await PlantForeignAddressAsync(patient.PublicId, strangerId);

        var detail = await michelle.GetFromJsonAsync<PatientDetail>($"/patients/{patient.PublicId}");

        var only = Assert.Single(detail!.Addresses);
        Assert.Equal("14 Elm Street", only.Line1);
    }

    /// <summary>
    /// Control: Patient.CorrectAddress — the a.PublicId == addressPublicId lookup over this
    /// patient's own collection. Replaced with FirstOrDefault() → red on the status code,
    /// "Assert.Equal() Failure: Values differ / Expected: NotFound / Actual: OK".
    /// </summary>
    [Fact]
    public async Task An_address_id_from_another_patient_is_not_reachable_through_this_ones_url()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));

        var maya = await CreatePatientAsync(michelle, last: "Reyes");
        var noah = await CreatePatientAsync(michelle, last: "Okafor");

        var mayasAddress = await AddAddressAsync(michelle, maya.PublicId, NewAddress());
        await AddAddressAsync(michelle, noah.PublicId, NewAddress(line1: "8 Oak Lane"));

        using var response = await michelle.PutAsJsonAsync(
            $"/patients/{noah.PublicId}/addresses/{mayasAddress.PublicId}",
            new CorrectAddressRequest("1 Nowhere Road", null, "Towson", "MD", "21204", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var noahsRecord = await michelle.GetFromJsonAsync<PatientDetail>($"/patients/{noah.PublicId}");
        Assert.Equal("8 Oak Lane", noahsRecord!.Addresses.Single().Line1);
    }

    /// <summary>
    /// A CORRECTION IS NOT A MOVE, at the API boundary.
    ///
    /// Fixing a typo leaves one row. Recording a move leaves two, the older one closed —
    /// because a note describing a visit last spring refers to where the family lived then.
    /// The endpoints are different verbs for exactly that reason.
    ///
    /// Control: PatientEndpoints — the MapPut route registration for this endpoint.
    /// Deleted → red on EnsureSuccessStatusCode, "System.Net.Http.HttpRequestException :
    /// Response status code does not indicate success: 404 (Not Found)."
    /// </summary>
    [Fact]
    public async Task Correcting_an_address_fixes_the_row_while_a_move_adds_one()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(michelle);

        var address = await AddAddressAsync(
            michelle, patient.PublicId,
            NewAddress(line1: "14 Elm Streat", from: new DateOnly(2025, 1, 1)));

        using var corrected = await michelle.PutAsJsonAsync(
            $"/patients/{patient.PublicId}/addresses/{address.PublicId}",
            new CorrectAddressRequest(
                "14 Elm Street", null, "Towson", "MD", "21204", "Gate code 4821"));

        corrected.EnsureSuccessStatusCode();
        var afterFix = (await corrected.Content.ReadFromJsonAsync<PatientDetail>())!;

        var only = Assert.Single(afterFix.Addresses);
        Assert.Equal("14 Elm Street", only.Line1);
        Assert.True(only.IsCurrent);
        Assert.Equal(new DateOnly(2025, 1, 1), only.EffectiveFrom);
        Assert.Null(only.EffectiveTo);

        // The family then actually moves.
        await AddAddressAsync(
            michelle, patient.PublicId,
            NewAddress(line1: "8 Oak Lane", from: new DateOnly(2026, 3, 1)));

        var afterMove = await michelle.GetFromJsonAsync<PatientDetail>($"/patients/{patient.PublicId}");

        Assert.Equal(2, afterMove!.Addresses.Count);
        Assert.Single(afterMove.Addresses, a => a.IsCurrent);
        Assert.Equal(
            new DateOnly(2026, 3, 1),
            afterMove.Addresses.Single(a => !a.IsCurrent).EffectiveTo);
    }
}
