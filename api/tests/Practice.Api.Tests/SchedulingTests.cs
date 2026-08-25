using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.ClinicalNotes;
using Practice.Api.Patients;
using Practice.Api.Scheduling;
using Practice.Domain.Providers;
using Practice.Domain.Scheduling;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// Scheduling, end to end against real SQL Server.
///
/// The DST and travel-conflict cases are the ones worth having: both produce a clinician
/// driving to a house where nobody is expecting her, and neither is visible by looking at
/// a calendar.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class SchedulingTests(SqlServerFixture sql) : IDisposable
{
    private readonly PracticeApiFactory _factory = new(sql.ConnectionString);

    public void Dispose() => _factory.Dispose();

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

    private HttpClient ClientFor(Guid providerPublicId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    private static async Task<Guid> CreatePatientAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        response.EnsureSuccessStatusCode();
        var patient = (await response.Content.ReadFromJsonAsync<PatientDetail>())!;
        return patient.PublicId;
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static ScheduleAppointmentRequest Visit(
        Guid patient, DateTime start, short duration = 60, short? travel = null) =>
        new(patient, AppointmentType.Therapy, start, duration, travel, null);

    // ------------------------------------------------------------- scheduling

    [Fact]
    public async Task An_appointment_can_be_scheduled_and_listed()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        using var created = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var list = await client.GetFromJsonAsync<List<AppointmentSummary>>(
            "/appointments?fromUtc=2026-08-01T00:00:00Z&toUtc=2026-10-01T00:00:00Z");

        Assert.NotNull(list);
        var only = Assert.Single(list);
        Assert.Equal("Reyes", only.PatientLastName);
        Assert.Equal("Scheduled", only.Status);
    }

    [Fact]
    public async Task Scheduling_for_another_providers_patient_is_not_found()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);

        using var response = await stranger.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Overlapping_visits_are_rejected()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        await client.PostAsJsonAsync("/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        using var clash = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 30)));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
    }

    /// <summary>
    /// The case a plain calendar misses: two visits that do not overlap in time but
    /// cannot both happen because of the drive between them.
    /// </summary>
    [Fact]
    public async Task Travel_time_is_counted_when_detecting_a_clash()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        // 14:00–15:00.
        await client.PostAsJsonAsync("/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        // Starts 15:15, but needs 30 minutes of driving — so it really begins at 14:45.
        using var clash = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 15, 15), travel: 30));

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        // With enough travel time, the same visit is fine.
        using var ok = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 15, 45), travel: 30));

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Back_to_back_visits_are_allowed()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        await client.PostAsJsonAsync("/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        using var next = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 15, 0)));

        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    [Fact]
    public async Task A_cancelled_visit_frees_its_slot()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        using var first = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));
        var created = await first.Content.ReadFromJsonAsync<ScheduledDto>();

        await client.PostAsJsonAsync($"/appointments/{created!.PublicId}/cancel",
            new CancelAppointmentRequest("Family unwell"));

        using var replacement = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));

        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
    }

    // ----------------------------------------------------------- transitions

    [Fact]
    public async Task Completing_a_visit_records_mileage()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        using var created = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));
        var appointment = await created.Content.ReadFromJsonAsync<ScheduledDto>();

        using var completed = await client.PostAsJsonAsync(
            $"/appointments/{appointment!.PublicId}/complete",
            new CompleteAppointmentRequest(12.4m));

        completed.EnsureSuccessStatusCode();
        var result = await completed.Content.ReadFromJsonAsync<TransitionDto>();

        Assert.Equal("Completed", result!.Status);
        Assert.Equal(12.4m, result.Mileage);
    }

    /// <summary>
    /// A completed visit is a record of what happened, and a clinical note attaches to it.
    /// The domain refuses; the API surfaces that as 409 with the domain's own wording.
    /// </summary>
    [Fact]
    public async Task A_completed_visit_cannot_be_rescheduled()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        using var created = await client.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));
        var appointment = await created.Content.ReadFromJsonAsync<ScheduledDto>();

        await client.PostAsJsonAsync($"/appointments/{appointment!.PublicId}/complete",
            new CompleteAppointmentRequest(null));

        using var moved = await client.PostAsJsonAsync(
            $"/appointments/{appointment.PublicId}/reschedule",
            new RescheduleRequest(Utc(2026, 9, 2, 14, 0), 60));

        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
        Assert.Contains("record of what happened", await moved.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transitions_on_another_providers_appointment_are_not_found()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var patient = await CreatePatientAsync(michelle);
        using var created = await michelle.PostAsJsonAsync(
            "/appointments", Visit(patient, Utc(2026, 9, 1, 14, 0)));
        var appointment = await created.Content.ReadFromJsonAsync<ScheduledDto>();

        using var response = await stranger.PostAsJsonAsync(
            $"/appointments/{appointment!.PublicId}/cancel",
            new CancelAppointmentRequest("not mine"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ------------------------------------------------------------- daily view

    /// <summary>
    /// The day boundary is LOCAL, not UTC.
    ///
    /// An evening visit at 19:00 Eastern is 23:00 UTC the same day in winter — but a
    /// 20:00 visit is 01:00 UTC the NEXT day. Slicing the day in UTC would silently drop
    /// evening appointments off the schedule the clinician is looking at.
    /// </summary>
    [Fact]
    public async Task The_daily_view_uses_the_practice_timezone_not_utc()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        // 2026-01-15 20:00 Eastern (EST, UTC-5) = 2026-01-16 01:00 UTC.
        await client.PostAsJsonAsync("/appointments",
            Visit(patient, Utc(2026, 1, 16, 1, 0)));

        var day = await client.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-01-15");

        Assert.NotNull(day);
        Assert.Single(day.Visits);
    }

    [Fact]
    public async Task The_daily_view_totals_mileage()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        // Two visits on the same local day, far enough apart not to clash.
        using var morning = await client.PostAsJsonAsync("/appointments",
            Visit(patient, Utc(2026, 6, 10, 14, 0)));
        using var afternoon = await client.PostAsJsonAsync("/appointments",
            Visit(patient, Utc(2026, 6, 10, 17, 0)));

        var first = await morning.Content.ReadFromJsonAsync<ScheduledDto>();
        var second = await afternoon.Content.ReadFromJsonAsync<ScheduledDto>();

        await client.PostAsJsonAsync($"/appointments/{first!.PublicId}/complete",
            new CompleteAppointmentRequest(8.2m));
        await client.PostAsJsonAsync($"/appointments/{second!.PublicId}/complete",
            new CompleteAppointmentRequest(4.3m));

        var day = await client.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-10");

        Assert.NotNull(day);
        Assert.Equal(12.5m, day.TotalMileage);
    }

    [Fact]
    public async Task The_daily_view_shows_only_the_callers_visits()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var theirPatient = await CreatePatientAsync(stranger);
        await stranger.PostAsJsonAsync("/appointments", Visit(theirPatient, Utc(2026, 6, 10, 14, 0)));

        var day = await michelle.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-10");

        Assert.NotNull(day);
        Assert.Empty(day.Visits);
    }

    // -------------------------------------------------- the note on each visit

    /// <summary>
    /// ONE request answers "which of today's visits have a note" for the whole day.
    ///
    /// The day view exists to be looked at on a phone between houses. Asking the note
    /// endpoint once per card would be a request per visit — on a container that scales
    /// to zero, over rural cellular. The answer belongs in the payload the clinician
    /// already asked for.
    /// </summary>
    [Fact]
    public async Task The_daily_view_says_which_visits_have_a_note()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        var documented = await ScheduleAsync(client, patient, Utc(2026, 6, 10, 14, 0));
        var undocumented = await ScheduleAsync(client, patient, Utc(2026, 6, 10, 17, 0));

        var note = await CreateNoteAsync(client, documented);

        var day = await client.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-10");

        Assert.NotNull(day);
        Assert.Equal(2, day.Visits.Count);

        var withNote = day.Visits.Single(v => v.PublicId == documented);
        Assert.Equal(note.PublicId, withNote.NotePublicId);
        Assert.Equal("Draft", withNote.NoteStatus);

        // Null, not an empty guid. "Not documented yet" must be distinguishable from a
        // note whose id failed to load.
        var withoutNote = day.Visits.Single(v => v.PublicId == undocumented);
        Assert.Null(withoutNote.NotePublicId);
        Assert.Null(withoutNote.NoteStatus);
    }

    /// <summary>
    /// The status is carried too, because "which of today's notes still need signing" is
    /// the question at the end of a day, and it is unanswerable from an id alone.
    /// </summary>
    [Fact]
    public async Task The_daily_view_reports_the_notes_status()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        var visit = await ScheduleAsync(client, patient, Utc(2026, 6, 11, 14, 0));
        var note = await CreateNoteAsync(client, visit);

        using var signed = await client.PostAsync($"/notes/{note.PublicId}/sign", null);
        signed.EnsureSuccessStatusCode();

        var day = await client.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-11");

        Assert.Equal("Signed", Assert.Single(day!.Visits).NoteStatus);
    }

    /// <summary>
    /// After an amendment the day view must point at the CURRENT version.
    ///
    /// Following a stale id would open a superseded note, and the editor would offer to
    /// amend a version that has already been amended.
    /// </summary>
    [Fact]
    public async Task The_daily_view_points_at_the_current_version_after_an_amendment()
    {
        using var client = ClientFor(await SeedProviderAsync("Michelle"));
        var patient = await CreatePatientAsync(client);

        var visit = await ScheduleAsync(client, patient, Utc(2026, 6, 12, 14, 0));
        var original = await CreateNoteAsync(client, visit);
        await client.PostAsync($"/notes/{original.PublicId}/sign", null);

        using var amended = await client.PostAsJsonAsync($"/notes/{original.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        var day = await client.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-12");

        var only = Assert.Single(day!.Visits);
        Assert.Equal(v2.PublicId, only.NotePublicId);
        Assert.NotEqual(original.PublicId, only.NotePublicId);
    }

    /// <summary>
    /// A note belonging to another provider must not surface on this schedule, even when
    /// it hangs off a visit this provider can legitimately see.
    ///
    /// The old version of this test asserted <c>Assert.Empty(day.Visits)</c>, which is the
    /// APPOINTMENT filter's work — it removes every visit a foreign note could hang from,
    /// leaving the ClinicalNote filter with nothing to do. Deleting that filter outright
    /// left the test green while its comment claimed two independent scopes.
    ///
    /// The ClinicalNote filter is load-bearing in exactly one shape: a note owned by one
    /// provider on ANOTHER provider's visit. No API path produces that row — CreateDraft
    /// stamps the caller's provider onto whatever it writes — so it is planted directly.
    /// Delete the filter and this test goes red; that is the point of it.
    /// </summary>
    [Fact]
    public async Task The_daily_view_carries_no_note_from_another_provider()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var strangerPublicId = await SeedProviderAsync("Stranger");

        var herPatient = await CreatePatientAsync(michelle);
        var herVisit = await ScheduleAsync(michelle, herPatient, Utc(2026, 6, 13, 16, 0));
        await PlantForeignNoteAsync(herVisit, strangerPublicId);

        var day = await michelle.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-13");

        Assert.NotNull(day);
        var hers = Assert.Single(day.Visits);
        Assert.Equal(herVisit, hers.PublicId);

        // Her own visit reads as undocumented, because the note sitting on it is not hers.
        Assert.Null(hers.NotePublicId);
        Assert.Null(hers.NoteStatus);
    }

    /// <summary>
    /// No visit from another provider reaches this schedule either — the other half of
    /// the pair, kept as its own test so each one names a single filter.
    ///
    /// Two shapes, because they are stopped by different things. The stranger's visit for
    /// the stranger's own patient is stopped twice: by the Appointment filter, and again
    /// by the day query's join to Patients, which is filtered as well. On its own it
    /// cannot tell you whether the Appointment filter is still there. The planted row can
    /// — a visit the stranger owns on MICHELLE's patient joins to a patient she can see,
    /// which leaves the Appointment filter as the only thing between it and her day.
    /// </summary>
    [Fact]
    public async Task The_daily_view_carries_no_visit_from_another_provider()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        var strangerPublicId = await SeedProviderAsync("Stranger");
        using var stranger = ClientFor(strangerPublicId);

        var theirPatient = await CreatePatientAsync(stranger);
        await ScheduleAsync(stranger, theirPatient, Utc(2026, 6, 14, 14, 0));

        var herPatient = await CreatePatientAsync(michelle);
        var herVisit = await ScheduleAsync(michelle, herPatient, Utc(2026, 6, 14, 16, 0));
        await PlantForeignVisitAsync(herPatient, strangerPublicId, Utc(2026, 6, 14, 18, 0));

        var day = await michelle.GetFromJsonAsync<DayScheduleDto>("/appointments/day/2026-06-14");

        Assert.NotNull(day);
        Assert.Equal(herVisit, Assert.Single(day.Visits).PublicId);
    }

    /// <summary>
    /// Writes a clinical note owned by one provider onto another provider's visit.
    ///
    /// Deliberately raw SQL: the API cannot produce this row, and it is the only shape in
    /// which the ClinicalNote tenancy filter is the thing standing between a foreign
    /// clinical record and the schedule.
    /// </summary>
    private async Task PlantForeignNoteAsync(Guid visitPublicId, Guid ownerPublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing.
        var visit = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.PublicId == visitPublicId)
            .Select(a => new { a.Id, a.PatientId })
            .SingleAsync();

        var ownerId = await ProviderIdAsync(db, ownerPublicId);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.ClinicalNotes
                (PublicId, ProviderId, PatientId, AppointmentId, VersionNumber,
                 IsCurrent, Status, Subjective, Objective, Assessment, [Plan],
                 Origin, CreatedAtUtc)
            VALUES
                (NEWID(), {ownerId}, {visit.PatientId}, {visit.Id}, 1,
                 1, 1, N'Not hers to read.', N'', N'', N'', 1, SYSUTCDATETIME())
            """);
    }

    /// <summary>
    /// Writes a visit owned by one provider against another provider's patient.
    ///
    /// Also unreachable through the API — scheduling resolves the patient through the
    /// caller's own filter — and, like the planted note, the only shape that isolates one
    /// filter from the one that would otherwise cover for it.
    /// </summary>
    private async Task PlantForeignVisitAsync(
        Guid patientPublicId, Guid ownerPublicId, DateTime startUtc)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var patientId = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.PublicId == patientPublicId)
            .Select(p => p.Id)
            .SingleAsync();

        var ownerId = await ProviderIdAsync(db, ownerPublicId);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.Appointments
                (PublicId, ProviderId, PatientId, AppointmentType, StartUtc,
                 DurationMinutes, [Status], CreatedAtUtc)
            VALUES
                (NEWID(), {ownerId}, {patientId}, 1, {startUtc}, 60, 1, SYSUTCDATETIME())
            """);
    }

    private static Task<long> ProviderIdAsync(PracticeDbContext db, Guid providerPublicId) =>
        db.Providers.AsNoTracking()
            .Where(p => p.PublicId == providerPublicId)
            .Select(p => p.Id)
            .SingleAsync();

    private static async Task<Guid> ScheduleAsync(HttpClient client, Guid patient, DateTime startUtc)
    {
        using var response = await client.PostAsJsonAsync("/appointments", Visit(patient, startUtc));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScheduledDto>())!.PublicId;
    }

    private static async Task<NoteDto> CreateNoteAsync(HttpClient client, Guid visitPublicId)
    {
        using var response = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visitPublicId, "Mum reports steady progress.", "", "", ""));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    private sealed record ScheduledDto(Guid PublicId, DateTime StartUtc, short DurationMinutes);

    private sealed record TransitionDto(
        Guid PublicId, string Status, DateTime StartUtc, short DurationMinutes, decimal? Mileage);

    private sealed record DayScheduleDto(
        DateOnly Date, List<DayVisit> Visits, decimal TotalMileage);
}
