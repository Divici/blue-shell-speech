using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Practice.Api.Auth;
using Practice.Api.ClinicalNotes;
using Practice.Api.Patients;
using Practice.Api.Scheduling;
using Practice.Domain.Auditing;
using Practice.Domain.Providers;
using Practice.Domain.Scheduling;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Tests;

/// <summary>
/// Clinical note immutability, against real SQL Server.
///
/// The domain tests prove the aggregate refuses to edit a signed note. These prove the
/// DATABASE refuses too — which is the half that matters when the write does not come
/// through the application at all.
/// </summary>
[Collection(UsesSqlServer.Name)]
public sealed class NoteImmutabilityTests(SqlServerFixture sql) : IDisposable
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

    /// <summary>Patient + appointment + draft note, the setup every test here needs.</summary>
    private static async Task<NoteDto> SeedDraftAsync(HttpClient client)
    {
        var patientResponse = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDetail>())!;

        var visitResponse = await client.PostAsJsonAsync("/appointments",
            new ScheduleAppointmentRequest(
                patient.PublicId, AppointmentType.Therapy,
                new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc), 60, null, null));
        visitResponse.EnsureSuccessStatusCode();
        var visit = (await visitResponse.Content.ReadFromJsonAsync<ScheduledDto>())!;

        var noteResponse = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(
                visit.PublicId,
                "Mum reports Maya used 'want juice' at home.",
                "Independent requesting 60%, 80% with minimal verbal cues.",
                "Progressing toward two-word combinations.",
                "Increase requesting opportunities during play."));

        noteResponse.EnsureSuccessStatusCode();
        return (await noteResponse.Content.ReadFromJsonAsync<NoteDto>())!;
    }

    /// <summary>
    /// The visit a note hangs off. Read straight from the database because the note DTO
    /// deliberately does not carry it — a note's public id is the only handle the UI needs.
    /// </summary>
    private async Task<Guid> AppointmentPublicIdAsync(Guid notePublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing.
        var appointmentId = await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.PublicId == notePublicId)
            .Select(n => n.AppointmentId)
            .SingleAsync();

        return await db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Id == appointmentId)
            .Select(a => a.PublicId)
            .SingleAsync();
    }

    // --------------------------------------------------------------- lifecycle

    [Fact]
    public async Task A_draft_can_be_edited_then_signed()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);

        Assert.Equal("Draft", draft.Status);
        Assert.Equal(1, draft.VersionNumber);

        using var edited = await client.PutAsJsonAsync($"/notes/{draft.PublicId}",
            new UpdateNoteRequest("Revised subjective.", "Objective.", "Assessment.", "Plan."));
        edited.EnsureSuccessStatusCode();

        using var signed = await client.PostAsync($"/notes/{draft.PublicId}/sign", null);
        signed.EnsureSuccessStatusCode();

        var result = (await signed.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal("Signed", result.Status);
        Assert.NotNull(result.SignedAtUtc);
        Assert.Equal("Michelle", result.SignedBy);
        Assert.True(result.IntegrityVerified);
    }

    [Fact]
    public async Task A_signed_note_cannot_be_edited_through_the_api()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var attempt = await client.PutAsJsonAsync($"/notes/{draft.PublicId}",
            new UpdateNoteRequest("Tampered.", "Tampered.", "Tampered.", "Tampered."));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("amendment", await attempt.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE test this whole design exists for.
    ///
    /// Bypasses the aggregate, the API, and EF's change tracking entirely, and issues a
    /// raw UPDATE straight at the table — the same thing a migration script, a bulk
    /// operation, or someone in SSMS would do. The database must refuse.
    ///
    /// Without the trigger this passes silently, and a signed clinical record has been
    /// rewritten with no trace.
    /// </summary>
    [Fact]
    public async Task A_signed_note_cannot_be_edited_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.ClinicalNotes SET Subjective = N'Tampered by raw SQL' WHERE PublicId = {draft.PublicId}"));

        Assert.Contains("cannot be modified", ex.Message, StringComparison.OrdinalIgnoreCase);

        // And the content is intact.
        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing. This read is
        // deliberately looking past it at the raw row.
        var note = await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(n => n.PublicId == draft.PublicId);

        Assert.StartsWith("Mum reports Maya", note.Subjective, StringComparison.Ordinal);
        Assert.True(note.VerifyIntegrity());
    }

    /// <summary>The signature itself must be immovable, not just the prose.</summary>
    [Fact]
    public async Task The_signature_cannot_be_altered_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"UPDATE dbo.ClinicalNotes SET SignedBy = N'Someone Else' WHERE PublicId = {draft.PublicId}"));
    }

    // -------------------------------------------------------------- amendments

    [Fact]
    public async Task Amending_creates_a_version_and_retains_the_original()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var amended = await client.PostAsJsonAsync($"/notes/{draft.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));

        Assert.Equal(HttpStatusCode.Created, amended.StatusCode);
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        Assert.Equal(2, v2.VersionNumber);
        Assert.True(v2.IsCurrent);
        Assert.Equal("Corrected the accuracy figure.", v2.AmendmentReason);

        var history = await client.GetFromJsonAsync<List<NoteDto>>($"/notes/{v2.PublicId}/history");
        Assert.NotNull(history);
        Assert.Equal(2, history.Count);

        var original = history.Single(n => n.VersionNumber == 1);
        Assert.Equal("Amended", original.Status);
        Assert.False(original.IsCurrent);
        Assert.StartsWith("Mum reports Maya", original.Subjective, StringComparison.Ordinal);
        Assert.True(original.IntegrityVerified);
    }

    /// <summary>
    /// The filtered unique index. Two current notes for one visit must be impossible even
    /// under a race, because there would be no way to say which one the clinician stands
    /// behind.
    /// </summary>
    [Fact]
    public async Task A_visit_cannot_have_two_current_notes()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);

        /*
         * A second note for the same appointment is refused by the API…
         *
         * This POST used to be missing: the comment claimed the API refused while only
         * the database assertion below ran. The UI now offers a single "start or open"
         * action, which makes this the layer that has to hold when two taps race.
         */
        var appointmentPublicId = await AppointmentPublicIdAsync(draft.PublicId);

        using var second = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(appointmentPublicId, "Second attempt.", "", "", ""));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("already has a note", await second.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing. This read is
        // deliberately looking past it at the raw row.
        var note = await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(n => n.PublicId == draft.PublicId);

        // …and by the database, when inserted directly.
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO dbo.ClinicalNotes
                    (PublicId, ProviderId, PatientId, AppointmentId, VersionNumber,
                     IsCurrent, Status, Subjective, Objective, Assessment, [Plan],
                     Origin, CreatedAtUtc)
                VALUES
                    (NEWID(), {note.ProviderId}, {note.PatientId}, {note.AppointmentId}, 99,
                     1, 1, N'x', N'x', N'x', N'x', 1, SYSUTCDATETIME())
                """));

        Assert.Contains("UX_ClinicalNotes_OneCurrentPerAppointment", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An amendment with no reason is refused by the database, not just the API.</summary>
    [Fact]
    public async Task An_amendment_without_a_reason_is_refused_by_the_database()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        // IgnoreQueryFilters: a test scope has no request and therefore no provider
        // context, so the tenancy filter correctly matches nothing. This read is
        // deliberately looking past it at the raw row.
        var note = await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(n => n.PublicId == draft.PublicId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO dbo.ClinicalNotes
                    (PublicId, ProviderId, PatientId, AppointmentId, VersionNumber,
                     SupersedesNoteId, IsCurrent, Status, Subjective, Objective,
                     Assessment, [Plan], Origin, CreatedAtUtc)
                VALUES
                    (NEWID(), {note.ProviderId}, {note.PatientId}, {note.AppointmentId}, 2,
                     {note.Id}, 0, 1, N'x', N'x', N'x', N'x', 1, SYSUTCDATETIME())
                """));

        Assert.Contains("CK_ClinicalNotes_AmendmentsHaveAReason", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_note_cannot_be_signed()
    {
        using var client = ClientFor(await SeedProviderAsync());

        var patientResponse = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDetail>())!;

        var visitResponse = await client.PostAsJsonAsync("/appointments",
            new ScheduleAppointmentRequest(
                patient.PublicId, AppointmentType.Therapy,
                new DateTime(2026, 9, 2, 14, 0, 0, DateTimeKind.Utc), 60, null, null));
        var visit = (await visitResponse.Content.ReadFromJsonAsync<ScheduledDto>())!;

        var noteResponse = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit.PublicId, "", "", "", ""));
        var note = (await noteResponse.Content.ReadFromJsonAsync<NoteDto>())!;

        using var signed = await client.PostAsync($"/notes/{note.PublicId}/sign", null);

        Assert.Equal(HttpStatusCode.Conflict, signed.StatusCode);
        Assert.Contains("empty", await signed.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Signing_and_amending_are_audited()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);
        await client.PostAsJsonAsync($"/notes/{draft.PublicId}/amend",
            new AmendNoteRequest("Corrected a figure."));

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == nameof(Practice.Domain.ClinicalNotes.ClinicalNote))
            .Select(e => e.EventType)
            .ToListAsync();

        Assert.Contains(AuditEventType.NoteSigned, events);
        Assert.Contains(AuditEventType.NoteAmended, events);
    }

    /// <summary>Clinical prose must never reach the audit log.</summary>
    [Fact]
    public async Task Note_audit_rows_contain_no_clinical_content()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var metadata = await db.AuditEvents.AsNoTracking()
            .Select(e => e.Metadata).ToListAsync();

        Assert.DoesNotContain(metadata,
            m => m is not null && m.Contains("want juice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(metadata,
            m => m is not null && m.Contains("Maya", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------- isolation

    [Fact]
    public async Task A_provider_cannot_read_or_sign_another_providers_note()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var draft = await SeedDraftAsync(michelle);

        using var signAttempt = await stranger.PostAsync($"/notes/{draft.PublicId}/sign", null);
        using var historyAttempt = await stranger.GetAsync($"/notes/{draft.PublicId}/history");

        Assert.Equal(HttpStatusCode.NotFound, signAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, historyAttempt.StatusCode);
    }

    /// <summary>
    /// Starting a note on someone else's visit must be BYTE-IDENTICAL to starting one on
    /// a visit that does not exist (D052).
    ///
    /// 403 would confirm the visit is real, turning the note entry point into an
    /// enumeration oracle: guess identifiers, read the status codes, learn which ones
    /// belong to a patient.
    /// </summary>
    [Fact]
    public async Task Starting_a_note_on_another_providers_visit_reveals_nothing()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var draft = await SeedDraftAsync(michelle);
        var realVisit = await AppointmentPublicIdAsync(draft.PublicId);

        using var foreign = await stranger.PostAsJsonAsync("/notes",
            new CreateNoteRequest(realVisit, "", "", "", ""));

        using var absent = await stranger.PostAsJsonAsync("/notes",
            new CreateNoteRequest(Guid.NewGuid(), "", "", "", ""));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());

        // And nothing was written to the real visit.
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();
        var count = await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(n => n.PublicId == draft.PublicId);

        Assert.Equal(1, count);
    }

    private sealed record ScheduledDto(Guid PublicId, DateTime StartUtc, short DurationMinutes);
}
