using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.ClinicalNotes;
using Practice.Api.Patients;
using Practice.Domain.Goals;
using Practice.Domain.Providers;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// Treatment goals, against real SQL Server.
///
/// Two properties the goals UI is built on and cannot verify for itself:
///
/// 1. <b>A goal on another provider's patient is unreachable, and indistinguishable from
///    one that does not exist.</b> The UI shows one message for both cases (D052); that is
///    only honest if the API really does refuse to tell them apart.
/// 2. <b>Closing a goal is a transition, not a delete.</b> Nothing on the screen removes a
///    goal because no endpoint removes one, and the row keeps its text.
///
/// Plus the AAC rule, asserted at both layers it is enforced at: the aggregate, and
/// CK_Goals_AacFieldsOnlyOnAacGoals.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class GoalTests(SqlServerFixture sql) : IDisposable
{
    private readonly PracticeApiFactory _factory = new(sql.ConnectionString);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedProviderAsync(string name = "Michelle")
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

    private HttpClient ClientFor(Guid providerPublicId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    private static async Task<PatientDetail> CreatePatientAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
    }

    /// <summary>Synthetic, like every fixture in this repo.</summary>
    private static CreateGoalRequest ArticulationGoal() => new(
        "Produce /s/ in the initial position of words.",
        GoalDomain.Articulation,
        new DateOnly(2026, 6, 1),
        "80% accuracy over 3 consecutive sessions",
        CueLevel.Verbal,
        null,
        null);

    private static async Task<Guid> CreateGoalAsync(
        HttpClient client, Guid patientPublicId, CreateGoalRequest? request = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/patients/{patientPublicId}/goals", request ?? ArticulationGoal());

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreatedGoal>();
        return created!.PublicId;
    }

    private sealed record CreatedGoal(Guid PublicId);

    private static Task<List<GoalDto>?> ListGoalsAsync(
        HttpClient client, Guid patientPublicId, bool activeOnly = false) =>
        client.GetFromJsonAsync<List<GoalDto>>(
            $"/patients/{patientPublicId}/goals{(activeOnly ? "?activeOnly=true" : "")}");

    // ------------------------------------------------------------- isolation

    [Fact]
    public async Task A_provider_cannot_list_another_providers_goals()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);
        await CreateGoalAsync(michelle, patient.PublicId);

        using var response = await stranger.GetAsync($"/patients/{patient.PublicId}/goals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_provider_cannot_write_a_goal_on_another_providers_patient()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.PostAsJsonAsync(
            $"/patients/{patient.PublicId}/goals", ArticulationGoal());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And nothing was written.
        var goals = await ListGoalsAsync(michelle, patient.PublicId);
        Assert.Empty(goals!);
    }

    /// <summary>
    /// 404, never 403 — and byte-identical.
    ///
    /// The BFF collapses both into one message, so if these two responses differed at all
    /// the UI would be free to start distinguishing them and hand an attacker an
    /// enumeration oracle for real goal identifiers.
    /// </summary>
    [Theory]
    [InlineData("met")]
    [InlineData("discontinue")]
    public async Task An_unreachable_goal_is_indistinguishable_from_a_missing_one(string transition)
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);
        var goal = await CreateGoalAsync(michelle, patient.PublicId);

        var strangerPatient = await CreatePatientAsync(stranger);

        using var foreign = await stranger.PostAsync(
            $"/patients/{strangerPatient.PublicId}/goals/{goal}/{transition}", null);
        using var absent = await stranger.PostAsync(
            $"/patients/{strangerPatient.PublicId}/goals/{Guid.NewGuid()}/{transition}", null);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());

        // The goal itself is untouched — refusing must not half-apply.
        var mine = await ListGoalsAsync(michelle, patient.PublicId);
        Assert.Equal("Active", Assert.Single(mine!).Status);
    }

    [Fact]
    public async Task A_request_with_no_provider_identity_reaches_no_goals()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);
        var goal = await CreateGoalAsync(michelle, patient.PublicId);

        using var anonymous = _factory.CreateClient();

        using var list = await anonymous.GetAsync($"/patients/{patient.PublicId}/goals");
        using var met = await anonymous.PostAsync(
            $"/patients/{patient.PublicId}/goals/{goal}/met", null);

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, met.StatusCode);
    }

    // ----------------------------------------------------------- transitions

    /// <summary>
    /// Marking met closes the goal without destroying it.
    ///
    /// A met goal is the record of what therapy accomplished — the thing families and
    /// payers ask about — so it keeps its text and gains an end date.
    /// </summary>
    [Fact]
    public async Task Marking_a_goal_met_keeps_it_on_the_record()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);
        var goal = await CreateGoalAsync(michelle, patient.PublicId);

        using var response = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/goals/{goal}/met", null);
        response.EnsureSuccessStatusCode();

        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);

        Assert.Equal("Met", stored.Status);
        Assert.NotNull(stored.EndDate);
        Assert.Equal("Produce /s/ in the initial position of words.", stored.GoalText);
    }

    [Fact]
    public async Task Discontinuing_a_goal_keeps_it_on_the_record()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);
        var goal = await CreateGoalAsync(michelle, patient.PublicId);

        using var response = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/goals/{goal}/discontinue", null);
        response.EnsureSuccessStatusCode();

        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);

        Assert.Equal("Discontinued", stored.Status);
        Assert.NotNull(stored.EndDate);
        Assert.Equal("Produce /s/ in the initial position of words.", stored.GoalText);
    }

    /// <summary>
    /// A closed goal stays closed, and the refusal is worded for a clinician.
    ///
    /// The UI hides the buttons on a closed goal, which is worth nothing on its own — a
    /// second tab or a page held open defeats it. This 409 is the control that holds, and
    /// the BFF surfaces its message rather than flattening it, so the rule is explained
    /// rather than reported as a malfunction.
    /// </summary>
    [Fact]
    public async Task A_closed_goal_cannot_be_closed_again()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);
        var goal = await CreateGoalAsync(michelle, patient.PublicId);

        using var first = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/goals/{goal}/met", null);
        first.EnsureSuccessStatusCode();

        using var second = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/goals/{goal}/discontinue", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("closed", await second.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        // Still met. A refused transition must not partially apply.
        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);
        Assert.Equal("Met", stored.Status);
    }

    /// <summary>
    /// The dictation pipeline's query: what is this child currently working on.
    ///
    /// Extraction classifies against ACTIVE goals (presearch §5.4), so a closed goal
    /// leaking into that list would have an observation attributed to something therapy
    /// stopped targeting months ago.
    /// </summary>
    [Fact]
    public async Task Active_only_excludes_a_goal_that_has_been_closed()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        var kept = await CreateGoalAsync(michelle, patient.PublicId);
        var closed = await CreateGoalAsync(michelle, patient.PublicId, ArticulationGoal() with
        {
            GoalText = "Produce /r/ in conversation.",
        });

        using var _ = await michelle.PostAsync(
            $"/patients/{patient.PublicId}/goals/{closed}/met", null);

        var active = await ListGoalsAsync(michelle, patient.PublicId, activeOnly: true);
        var all = await ListGoalsAsync(michelle, patient.PublicId);

        Assert.Equal(kept, Assert.Single(active!).PublicId);
        Assert.Equal(2, all!.Count);
    }

    // ------------------------------------------------------------- AAC rule

    [Fact]
    public async Task An_aac_goal_keeps_its_aac_details()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        await CreateGoalAsync(michelle, patient.PublicId, new CreateGoalRequest(
            "Request a break using a core board.",
            GoalDomain.Aac,
            new DateOnly(2026, 6, 1),
            "4 of 5 opportunities across 3 sessions",
            CueLevel.Gestural,
            AacModality.LowTech,
            "Twelve-cell core board, laminated."));

        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);

        Assert.Equal("Aac", stored.Domain);
        Assert.Equal("LowTech", stored.AacModality);
        Assert.Equal("Twelve-cell core board, laminated.", stored.AacDeviceNotes);
    }

    /// <summary>
    /// The aggregate's half of the AAC rule, through the API.
    ///
    /// The form never offers this combination — it unmounts the AAC fields on a non-AAC
    /// domain — and the BFF rejects it before sending. This is the layer that holds when
    /// neither of those is involved.
    /// </summary>
    [Fact]
    public async Task Aac_details_are_refused_on_a_goal_that_is_not_aac()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        using var response = await michelle.PostAsJsonAsync(
            $"/patients/{patient.PublicId}/goals",
            ArticulationGoal() with { AacModality = AacModality.HighTech });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AAC", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Empty((await ListGoalsAsync(michelle, patient.PublicId))!);
    }

    /// <summary>
    /// The database's half, proven by going around the aggregate entirely.
    ///
    /// A raw INSERT is what a migration script, a bulk import, or someone in SSMS at 11pm
    /// actually does. Without CK_Goals_AacFieldsOnlyOnAacGoals this succeeds, and an
    /// articulation goal now carries an AAC modality that the dictation pipeline will read
    /// when deciding how to interpret what Michelle said.
    /// </summary>
    [Fact]
    public async Task Aac_details_on_a_non_aac_goal_are_refused_by_the_database_too()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing.
        var row = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(p => p.PublicId == patient.PublicId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO dbo.Goals
                    (PublicId, ProviderId, PatientId, GoalText, Domain, Status, StartDate,
                     AacModality, AacDeviceNotes, CreatedAtUtc)
                VALUES
                    ({Guid.NewGuid()}, {row.ProviderId}, {row.Id},
                     N'Produce /s/ in the initial position of words.',
                     {(int)GoalDomain.Articulation}, {(int)GoalStatus.Active},
                     {new DateOnly(2026, 6, 1)}, {(int)AacModality.HighTech}, NULL,
                     {DateTime.UtcNow})
                """));

        Assert.Contains("CK_Goals_AacFieldsOnlyOnAacGoals", ex.Message, StringComparison.Ordinal);
        Assert.Empty((await ListGoalsAsync(michelle, patient.PublicId))!);
    }

    /// <summary>
    /// An AAC goal with no device chosen yet is legitimate — both columns are nullable.
    ///
    /// And "not specified" must arrive as NULL, not as "". Nullable&lt;TEnum&gt;.ToString()
    /// returns string.Empty, so a projection translated wholly into SQL quietly turns
    /// every unset enum into an empty string and the DTO's `string?` stops being true.
    /// </summary>
    [Fact]
    public async Task An_aac_goal_may_exist_before_a_device_is_chosen()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        await CreateGoalAsync(michelle, patient.PublicId, new CreateGoalRequest(
            "Use a communication system to request.",
            GoalDomain.Aac, new DateOnly(2026, 6, 1), null, null, null, null));

        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);

        Assert.Equal("Aac", stored.Domain);
        Assert.Null(stored.AacModality);
        Assert.Null(stored.AacDeviceNotes);
        Assert.Null(stored.CueLevelExpected);
        Assert.Null(stored.TargetCriteria);
        Assert.Null(stored.EndDate);
    }

    /// <summary>
    /// The start date is whatever the caller sent, not whatever the server's UTC clock
    /// said. The BFF resolves it in America/New_York, and an 8pm Eastern goal would
    /// otherwise be dated a day ahead (D057).
    /// </summary>
    [Fact]
    public async Task The_start_date_is_taken_from_the_request()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        await CreateGoalAsync(michelle, patient.PublicId,
            ArticulationGoal() with { StartDate = new DateOnly(2026, 3, 8) });

        var stored = Assert.Single((await ListGoalsAsync(michelle, patient.PublicId))!);

        Assert.Equal(new DateOnly(2026, 3, 8), stored.StartDate);
    }

    /// <summary>An empty goal is not a goal. The aggregate's Guard, surfaced as a 400.</summary>
    [Fact]
    public async Task A_blank_goal_is_rejected_with_a_readable_reason()
    {
        using var michelle = ClientFor(await SeedProviderAsync());
        var patient = await CreatePatientAsync(michelle);

        using var response = await michelle.PostAsJsonAsync(
            $"/patients/{patient.PublicId}/goals",
            ArticulationGoal() with { GoalText = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
