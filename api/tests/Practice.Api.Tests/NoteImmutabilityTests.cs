using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>
    /// A visit that has already begun, in a slot of its own.
    ///
    /// Derived from the clock rather than hardcoded, because a note can only be started
    /// for a visit that has started (Appointment.DocumentationBlockedReason). A fixture
    /// pinned to a calendar date silently changes meaning the day it passes. Slots are two
    /// hours apart so two visits in one test never trip conflict detection.
    /// </summary>
    private static DateTime PastVisitUtc(int slot = 0) =>
        DateTime.UtcNow.Date.AddDays(-7).AddHours(9 + (slot * 2));

    /// <summary>Patient + appointment, with the visit already in the past.</summary>
    private static async Task<Guid> SeedVisitAsync(HttpClient client, int slot = 0)
    {
        var patientResponse = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDetail>())!;

        var visitResponse = await client.PostAsJsonAsync("/appointments",
            new ScheduleAppointmentRequest(
                patient.PublicId, AppointmentType.Therapy,
                PastVisitUtc(slot), 60, null, null));
        visitResponse.EnsureSuccessStatusCode();

        return (await visitResponse.Content.ReadFromJsonAsync<ScheduledDto>())!.PublicId;
    }

    /// <summary>Patient + appointment + draft note, the setup every test here needs.</summary>
    private static async Task<NoteDto> SeedDraftAsync(HttpClient client)
    {
        var visit = await SeedVisitAsync(client);

        var noteResponse = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(
                visit,
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

    /// <summary>
    /// How many clinical notes hang off a VISIT, looking past tenancy at the raw rows.
    ///
    /// Counting on the visit rather than on a known note's PublicId is what makes a row
    /// somebody else wrote visible. A count keyed on a unique PublicId answers 1 whatever
    /// else was written, so it cannot fail.
    /// </summary>
    private async Task<int> NoteCountForVisitAsync(Guid visitPublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var appointmentId = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.PublicId == visitPublicId)
            .Select(a => a.Id)
            .SingleAsync();

        return await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(n => n.AppointmentId == appointmentId);
    }

    /// <summary>Audited reads of one clinical note.</summary>
    private async Task<List<AuditEvent>> NoteReadEventsAsync(Guid notePublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.EventType == AuditEventType.PatientViewed
                        && e.EntityType == nameof(Practice.Domain.ClinicalNotes.ClinicalNote)
                        && e.EntityPublicId == notePublicId)
            .ToListAsync();
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
        var visit = await SeedVisitAsync(client);

        var noteResponse = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
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

    /// <summary>
    /// Reading a note is access to ePHI, and the history endpoint hands back every
    /// version's full S/O/A/P — the largest single disclosure this API makes.
    ///
    /// docs/SECURITY.md §Audit: "Reads are audited, not just writes." Every note read in
    /// the product goes through this endpoint, so if it writes nothing there is no record
    /// that a clinical note was ever opened. Remove the audit write from GetNoteHistory
    /// and this goes red.
    /// </summary>
    [Fact]
    public async Task Reading_a_notes_history_is_audited()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var amended = await client.PostAsJsonAsync($"/notes/{draft.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        // Writing the note produced no read event; only reading it may.
        Assert.Empty(await NoteReadEventsAsync(v2.PublicId));

        var history = await client.GetFromJsonAsync<List<NoteDto>>($"/notes/{v2.PublicId}/history");
        Assert.Equal(2, history!.Count);

        var read = Assert.Single(await NoteReadEventsAsync(v2.PublicId));

        Assert.Equal(AuditOutcome.Success, read.Outcome);

        /*
         * The row must describe the read that ACTUALLY happened.
         *
         * Two versions were disclosed, not one. An audit entry that records "a note was
         * viewed" without saying how much of it came back understates the disclosure, and
         * the whole point of the log is to answer that question later.
         */
        Assert.Equal("versions=2", read.Metadata);
    }

    /// <summary>Clinical prose must never reach the audit log.</summary>
    [Fact]
    public async Task Note_audit_rows_contain_no_clinical_content()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        // Including the read path, which is the newest writer of audit metadata.
        await client.GetAsync($"/notes/{draft.PublicId}/history");

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
        var documented = await AppointmentPublicIdAsync(draft.PublicId);

        /*
         * The stranger aims at an UNDOCUMENTED visit, which is the case that is actually
         * exploitable.
         *
         * On a visit that already has a note the API's own one-current-note rule would
         * refuse the POST whatever tenancy did, so a green assertion there proves nothing
         * about the filter. An undocumented visit has no second line of defence: if the
         * appointment lookup is not provider-scoped, this POST writes a clinical record
         * onto another provider's patient.
         */
        var undocumented = await SeedVisitAsync(michelle, slot: 1);

        using var foreign = await stranger.PostAsJsonAsync("/notes",
            new CreateNoteRequest(undocumented, "", "", "", ""));

        using var absent = await stranger.PostAsJsonAsync("/notes",
            new CreateNoteRequest(Guid.NewGuid(), "", "", "", ""));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());

        /*
         * Counted on the VISIT, not on the seeded note's own PublicId.
         *
         * The previous version of this test counted rows matching draft.PublicId — a
         * unique key, so the answer was 1 whatever the stranger's POST had done, and the
         * assertion could not fail. Counting on the appointment is what makes a written
         * row visible.
         */
        Assert.Equal(0, await NoteCountForVisitAsync(undocumented));
        Assert.Equal(1, await NoteCountForVisitAsync(documented));
    }

    // ------------------------------------------------ where a note may start

    /*
     * A note documents what happened.
     *
     * The entry point sits on the day view, which is read on a phone between houses, and
     * before this gate existed a mis-tap on any card at all created an empty draft that
     * Sign() refuses and nothing could remove.
     *
     * Every check below runs AFTER the provider filter has resolved the appointment, so a
     * visit belonging to someone else is still 404 (D052). A 409 only ever describes a
     * visit the caller can already see.
     */

    [Fact]
    public async Task A_note_cannot_be_started_for_a_cancelled_visit()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var visit = await SeedVisitAsync(client);

        using var cancelled = await client.PostAsJsonAsync($"/appointments/{visit}/cancel",
            new CancelAppointmentRequest("Family unwell"));
        cancelled.EnsureSuccessStatusCode();

        using var attempt = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("cancelled", await attempt.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await NoteCountForVisitAsync(visit));
    }

    [Fact]
    public async Task A_note_cannot_be_started_for_a_no_show()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var visit = await SeedVisitAsync(client);

        using var noShow = await client.PostAsync($"/appointments/{visit}/no-show", null);
        noShow.EnsureSuccessStatusCode();

        using var attempt = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("no-show", await attempt.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await NoteCountForVisitAsync(visit));
    }

    /// <summary>
    /// Next week's visit has produced nothing to write down, and a draft sitting on it
    /// reads on the schedule as "documented" when it is not.
    /// </summary>
    [Fact]
    public async Task A_note_cannot_be_started_for_a_visit_that_has_not_happened()
    {
        using var client = ClientFor(await SeedProviderAsync());

        var patientResponse = await client.PostAsJsonAsync("/patients",
            new CreatePatientRequest("Maya", "Reyes", new DateOnly(2024, 2, 24), null));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDetail>())!;

        using var visitResponse = await client.PostAsJsonAsync("/appointments",
            new ScheduleAppointmentRequest(
                patient.PublicId, AppointmentType.Therapy,
                DateTime.UtcNow.Date.AddDays(7).AddHours(14), 60, null, null));
        visitResponse.EnsureSuccessStatusCode();
        var visit = (await visitResponse.Content.ReadFromJsonAsync<ScheduledDto>())!.PublicId;

        using var attempt = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Contains("not started", await attempt.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await NoteCountForVisitAsync(visit));
    }

    // -------------------------------------------------------------- discarding

    /*
     * The escape hatch for a mis-tap on a visit that IS documentable.
     *
     * Sign() refuses an empty note and UX_ClinicalNotes_OneCurrentPerAppointment blocks a
     * replacement, so without this an empty draft could only be cleared by writing content
     * onto that child's chart and signing it into immutability.
     */

    [Fact]
    public async Task An_empty_draft_can_be_discarded()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        using var discarded = await client.DeleteAsync($"/notes/{note.PublicId}");

        discarded.EnsureSuccessStatusCode();
        Assert.Equal(0, await NoteCountForVisitAsync(visit));

        // And the visit is documentable again: the filtered unique index no longer has a
        // current note to collide with.
        using var second = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "Mum reports steady progress.", "", "", ""));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task A_draft_with_content_cannot_be_discarded()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(draft.PublicId);

        using var attempt = await client.DeleteAsync($"/notes/{draft.PublicId}");

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    [Fact]
    public async Task A_signed_note_cannot_be_discarded()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(draft.PublicId);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var attempt = await client.DeleteAsync($"/notes/{draft.PublicId}");

        Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    /// <summary>404, and the row survives — the same answer as a note that never existed.</summary>
    [Fact]
    public async Task Another_providers_empty_draft_cannot_be_discarded()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var visit = await SeedVisitAsync(michelle);
        using var created = await michelle.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        using var foreign = await stranger.DeleteAsync($"/notes/{note.PublicId}");
        using var absent = await stranger.DeleteAsync($"/notes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(absent.StatusCode, foreign.StatusCode);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    /// <summary>Removing a row from a clinical database is an auditable act.</summary>
    [Fact]
    public async Task Discarding_an_empty_draft_is_audited()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        using var discarded = await client.DeleteAsync($"/notes/{note.PublicId}");
        discarded.EnsureSuccessStatusCode();

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityPublicId == note.PublicId)
            .Select(e => e.EventType)
            .ToListAsync();

        Assert.Contains(AuditEventType.NoteDiscarded, events);
    }

    // ------------------------------------------- discarding an amendment (F1)

    /*
     * THE SEQUENCE THAT STRANDED A SIGNED NOTE, and the reason this section exists.
     *
     * Sign v1 · POST /notes/{v1}/amend · PUT /notes/{v2} with four empty strings ·
     * DELETE /notes/{v2}. Every one of those is a supported call answering exactly as
     * designed, and together they used to leave v1 as Amended with IsCurrent = 0 and
     * nothing current on the visit: the day card offered "Start note" again,
     * GET /notes/appointment/{visit} answered 404, and a signed clinical record was
     * unreachable through the product's only navigation path.
     *
     * All four guards passed independently because each asks about Status and emptiness
     * and none asked about SupersedesNoteId. That is why the fix is in three places and
     * why there are three tests below rather than one.
     */

    /// <summary>
    /// The endpoint layer.
    ///
    /// Control: NoteEndpoints.RefusalToDiscard — the `note.SupersedesNoteId is not null`
    /// branch. (It was inline in DiscardDraft when this line was written; the three
    /// refusals moved into one predicate so the transaction body could ask them again, and
    /// the deletion was re-run against the new home rather than assumed — D077.)
    /// Deleted → red on Assert.Contains("amendment", …), because the aggregate still
    /// refuses but the 409 then explains itself as "this note has something written in
    /// it", telling a clinician to clear four sections that are already clear.
    /// Deleted TOGETHER WITH the aggregate's clause → red on
    /// Assert.Equal(Conflict, discard.StatusCode), "Expected: Conflict, Actual: OK".
    /// </summary>
    [Fact]
    public async Task A_cleared_amendment_cannot_be_discarded()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var v1 = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(v1.PublicId);

        using var signed = await client.PostAsync($"/notes/{v1.PublicId}/sign", null);
        signed.EnsureSuccessStatusCode();

        using var amended = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        amended.EnsureSuccessStatusCode();
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        // Blanking a draft stays permitted. It is an edit, it destroys nothing, and the
        // signed version it supersedes still holds every byte. The DELETE is the harm.
        using var cleared = await client.PutAsJsonAsync($"/notes/{v2.PublicId}",
            new UpdateNoteRequest("", "", "", ""));
        cleared.EnsureSuccessStatusCode();

        using var discard = await client.DeleteAsync($"/notes/{v2.PublicId}");

        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);
        Assert.Contains("amendment", await discard.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(2, await NoteCountForVisitAsync(visit));

        // The visit still resolves to a current note rather than reading as undocumented.
        using var current = await client.GetAsync($"/notes/appointment/{visit}");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);

        // And the day view still offers the way back in, rather than "Start note".
        var day = await client.GetFromJsonAsync<DaySchedule>(
            $"/appointments/day/{DateOnly.FromDateTime(PastVisitUtc()):yyyy-MM-dd}");
        var card = day!.Visits.Single(v => v.PublicId == visit);
        Assert.NotNull(card.NotePublicId);

        // The signed version is intact, and reachable, and still verifies.
        var history = await client.GetFromJsonAsync<List<NoteDto>>($"/notes/{v2.PublicId}/history");
        var original = history!.Single(n => n.VersionNumber == 1);

        Assert.Equal("Amended", original.Status);
        Assert.StartsWith("Mum reports Maya", original.Subjective, StringComparison.Ordinal);
        Assert.True(original.IntegrityVerified);
    }

    /// <summary>
    /// The database layer, for the same sequence.
    ///
    /// A cleared amendment is Status 1 with four empty sections, so it satisfied every
    /// clause TR_ClinicalNotes_PreventDeletingRealNotes tested. The trigger is the guard
    /// that has to hold when the other two are loosened, which means it cannot rely on
    /// them having asked the question first.
    ///
    /// Control: the `OR d.[SupersedesNoteId] IS NOT NULL` clause in the trigger body inside
    /// **AmendmentDeletionGuard.Up** — the LAST migration that defines
    /// TR_ClinicalNotes_PreventDeletingRealNotes, and therefore the only definition the
    /// test database ends up with.
    /// Deleted → red on Assert.ThrowsAnyAsync, "Assert.ThrowsAny() Failure: No exception
    /// was thrown, Expected: typeof(System.Exception)".
    ///
    /// NAMING THE MIGRATION IS NOT PEDANTRY HERE. The trigger is created with
    /// CREATE OR ALTER in ClinicalNoteDeletionGuard and re-created with CREATE OR ALTER in
    /// AmendmentDeletionGuard, and the test database is built by running migrations in
    /// order — so a clause deleted from the FIRST one is put straight back by the second
    /// and every one of these four tests stays green. Verified: the emptiness clauses
    /// removed from ClinicalNoteDeletionGuard leave all four passing. There is a third copy
    /// in AmendmentDeletionGuard.Down, which never runs here at all.
    /// </summary>
    [Fact]
    public async Task A_cleared_amendment_cannot_be_deleted_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var v1 = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(v1.PublicId);

        await client.PostAsync($"/notes/{v1.PublicId}/sign", null);

        using var amended = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        using var cleared = await client.PutAsJsonAsync($"/notes/{v2.PublicId}",
            new UpdateNoteRequest("", "", "", ""));
        cleared.EnsureSuccessStatusCode();

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM dbo.ClinicalNotes WHERE PublicId = {v2.PublicId}"));

        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await NoteCountForVisitAsync(visit));
    }

    /// <summary>
    /// The database half of the discard rule, like the UPDATE trigger above.
    ///
    /// A DELETE that never went through the application — a cleanup script, a bulk
    /// operation, SSMS — must not be able to remove a real clinical record.
    ///
    /// Control: the `WHERE d.[Status] &lt;&gt; 1` clause AND all four ISNULL comparisons in
    /// AmendmentDeletionGuard.Up, deleted together.
    /// Deleted together → red on Assert.ThrowsAnyAsync, "Assert.ThrowsAny() Failure: No
    /// exception was thrown, Expected: typeof(System.Exception)".
    /// Either one deleted on its own → STILL GREEN.
    ///
    /// THIS TEST CANNOT ISOLATE EITHER CLAUSE, AND SAYING SO IS THE HONEST VERSION.
    ///
    /// A signed note the API can produce is Status 2 with content in it, so the Status
    /// clause and the emptiness clauses each answer for the whole case and each covers for
    /// the other — in both directions. The line here previously claimed the emptiness
    /// clauses were the control and that the Status clause could be deleted with this test
    /// still green. The second half was verified; the first half was not, and it is false.
    /// The D066 F4 shape, one level in: the correction to a two-clauses-covering finding
    /// asserted the converse without running it.
    ///
    /// The two clauses ARE isolated, by the two tests that construct cases only one of them
    /// answers: An_empty_signed_note_cannot_be_deleted_by_raw_sql (Status, against a row
    /// planted by raw INSERT) and A_draft_with_content_cannot_be_deleted_by_raw_sql
    /// (emptiness). This test earns its place as the case that actually occurs, not as the
    /// control for a clause.
    ///
    /// On why the migration is named rather than the trigger, see
    /// A_cleared_amendment_cannot_be_deleted_by_raw_sql above.
    /// </summary>
    [Fact]
    public async Task A_signed_note_cannot_be_deleted_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(draft.PublicId);
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM dbo.ClinicalNotes WHERE PublicId = {draft.PublicId}"));

        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    /// <summary>
    /// The Status clause on its own, against a row the application cannot create.
    ///
    /// Signed, and empty in all four sections. Sign() refuses that combination, so the only
    /// way to reach it is a raw INSERT — the same move D066 needed for the tenancy filters,
    /// and for the same reason: a test that can only construct legal states cannot prove
    /// what happens to an illegal one. The row is what a bad migration, a bulk load, or a
    /// script run at 11pm would leave behind, and the trigger is the guard that has to hold
    /// when the aggregate and the endpoint were never involved.
    ///
    /// CK_ClinicalNotes_SignedNotesAreAttested still applies, so the planted row carries a
    /// signature time, a signer, and a hash. They are synthetic and the hash is a single
    /// zero byte — this row exists to be refused, not to be read.
    ///
    /// Control: the `WHERE d.[Status] &lt;&gt; 1` clause in AmendmentDeletionGuard.Up — the
    /// last migration to define the trigger, and so the one the test database keeps.
    /// Deleted (replaced with `WHERE 1 = 0`) → red on Assert.ThrowsAnyAsync,
    /// "Assert.ThrowsAny() Failure: No exception was thrown, Expected:
    /// typeof(System.Exception)". The other three tests in this group stay green, which is
    /// what isolation means.
    /// </summary>
    [Fact]
    public async Task An_empty_signed_note_cannot_be_deleted_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var visit = await SeedVisitAsync(client);
        var planted = await PlantEmptySignedNoteAsync(visit);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM dbo.ClinicalNotes WHERE PublicId = {planted}"));

        Assert.Contains("cannot be deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    /// <summary>
    /// Writes a signed note with nothing in any section, which Sign() will not do.
    ///
    /// Column list written by hand, so it breaks when the table changes shape. That is the
    /// price of testing a state the application refuses to create (D066), and it is the
    /// right price.
    /// </summary>
    private async Task<Guid> PlantEmptySignedNoteAsync(Guid visitPublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        var visit = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.PublicId == visitPublicId)
            .Select(a => new { a.Id, a.ProviderId, a.PatientId })
            .SingleAsync();

        var publicId = Guid.NewGuid();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.ClinicalNotes
                (PublicId, ProviderId, PatientId, AppointmentId, VersionNumber,
                 IsCurrent, Status, Subjective, Objective, Assessment, [Plan],
                 Origin, CreatedAtUtc, SignedAtUtc, SignedBy, ContentHash)
            VALUES
                ({publicId}, {visit.ProviderId}, {visit.PatientId}, {visit.Id}, 1,
                 1, 2, N'', N'', N'', N'',
                 1, SYSUTCDATETIME(), SYSUTCDATETIME(), N'Michelle', 0x00)
            """);

        return publicId;
    }

    /// <summary>
    /// The emptiness clauses on their own — the draft is Status 1, so nothing else in the
    /// predicate has an opinion about it.
    ///
    /// Control: the four ISNULL comparisons in AmendmentDeletionGuard.Up — the last
    /// migration to define the trigger, and so the one the test database keeps.
    /// Deleted → red on Assert.ThrowsAnyAsync, "Assert.ThrowsAny() Failure: No exception
    /// was thrown, Expected: typeof(System.Exception)". The same four deleted from
    /// ClinicalNoteDeletionGuard instead leave it green, because AmendmentDeletionGuard
    /// re-creates the trigger afterwards with CREATE OR ALTER.
    /// </summary>
    [Fact]
    public async Task A_draft_with_content_cannot_be_deleted_by_raw_sql()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);
        var visit = await AppointmentPublicIdAsync(draft.PublicId);

        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM dbo.ClinicalNotes WHERE PublicId = {draft.PublicId}"));

        Assert.Equal(1, await NoteCountForVisitAsync(visit));
    }

    // ------------------------------------------- the audit around the delete

    /*
     * The only DELETE in this application, and therefore the one whose evidence matters
     * most. Two separate claims, tested separately:
     *
     *   F3  a discard and its audit row commit together, or neither does
     *   F4  a REFUSED discard leaves evidence too
     *
     * The second is the one an investigation actually needs. A log that records the
     * deletions that succeeded and nothing about the ones that were stopped cannot answer
     * "did someone walk this table with DELETE", which is the question you ask it.
     */

    /// <summary>Every audited discard attempt against one note, successful or not.</summary>
    private async Task<List<AuditEvent>> DiscardEventsAsync(Guid notePublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.EventType == AuditEventType.NoteDiscarded
                        && e.EntityPublicId == notePublicId)
            .ToListAsync();
    }

    /// <summary>
    /// F3: the delete and its audit row are one transaction.
    ///
    /// They were two `SaveChangesAsync` calls on the request's cancellation token, so the
    /// row was gone before the audit write was attempted. Background the PWA between them
    /// — an ordinary thing to do walking out of a house — and the note is deleted with
    /// nothing in AuditEvents to say who did it or when.
    ///
    /// Forced here by an audit writer that throws, which is the same window with the
    /// timing removed. If the delete is still committed after the audit write fails, the
    /// two were never atomic.
    ///
    /// Control: AtomicWrites.WriteAtomicallyAsync — the BeginTransactionAsync /
    /// CommitAsync pair the discard's writes run between. (It was inline in DiscardDraft
    /// when this test was written; the transaction moved to the shared helper with F2, and
    /// this line was re-verified against the new home rather than assumed.)
    /// Deleted → red on Assert.Equal(1, NoteCountForVisitAsync), "Assert.Equal() Failure:
    /// Values differ, Expected: 1, Actual: 0" — the note is destroyed and nothing records
    /// it.
    /// </summary>
    [Fact]
    public async Task A_note_survives_when_its_audit_row_cannot_be_written()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        using var broken = new PracticeApiFactory(sql.ConnectionString,
            services => services.AddScoped<IAuditWriter, UnwritableAuditWriter>());
        using var brokenClient = broken.CreateClient();
        brokenClient.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());

        using var discard = await brokenClient.DeleteAsync($"/notes/{note.PublicId}");

        Assert.False(discard.IsSuccessStatusCode);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
        Assert.Empty(await DiscardEventsAsync(note.PublicId));
    }

    /// <summary>
    /// F4, and the reason exception handling is part of this finding.
    ///
    /// The API had no UseExceptionHandler and no AddProblemDetails, so anything that threw
    /// — a raw THROW 50002 from the DELETE trigger included — reached the BFF as whatever
    /// the host decided to render. In Development that is a developer exception page: SQL
    /// text, parameter values, and a stack trace, from an application whose parameters are
    /// patient identifiers. THREAT_MODEL.md boundary 2 is the `web` → `api` hop, and PHI
    /// must not cross it in an error body any more than in a log line.
    ///
    /// Control: Program.cs — AddProblemDetails + UseExceptionHandler.
    /// Deleted → red on the content-type assertion, which reads "text/plain": the
    /// developer exception page, rendered as text because the caller sent no Accept
    /// header, with the exception type and the full stack in the body.
    /// </summary>
    [Fact]
    public async Task An_unhandled_failure_answers_with_problem_details_and_no_stack_trace()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var draft = await SeedDraftAsync(client);

        using var broken = new PracticeApiFactory(sql.ConnectionString,
            services => services.AddScoped<IAuditWriter, UnwritableAuditWriter>());
        using var brokenClient = broken.CreateClient();
        brokenClient.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());

        // The read path audits every disclosure (D065), so a broken audit writer breaks it.
        using var response = await brokenClient.GetAsync($"/notes/{draft.PublicId}/history");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();

        // The one handle a human gets. Asserted because the comment on AddProblemDetails
        // says the traceId is what Michelle can quote — and a claim about a mechanism is
        // only worth what pins it (D072).
        Assert.Contains("traceId", body, StringComparison.Ordinal);

        Assert.DoesNotContain("UnwritableAuditWriter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Practice.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Maya", body, StringComparison.Ordinal);
        Assert.DoesNotContain("want juice", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F4: a refused discard is evidence.
    ///
    /// Each of the three refusals the endpoint decides, checked separately, because each
    /// is a different sentence about what someone tried to remove: a draft with clinical
    /// content in it, a signed note, and an amendment to one.
    ///
    /// Control: DiscardDraft's FIRST AuditRefusedDiscardAsync call — the one on the cheap
    /// pass, taken before the transaction is opened.
    /// Deleted → red on the first Assert.Single, "Assert.Single() Failure: The collection
    /// was empty".
    ///
    /// NAMED PRECISELY, BECAUSE THE COUNT WAS WRONG. This line used to say "one now that
    /// the three branches are one predicate". There were two calls when it said that and
    /// there are three now — the not-found path, this one, and the late refusal written in
    /// the `finally` around WriteAtomicallyAsync — and deleting either of the other two
    /// leaves this test green. Checked, rather than counted from memory: with the late call
    /// disabled this test still passes.
    ///
    /// The other two have tests of their own, which is why the miscount mattered rather
    /// than merely being untidy: A_discard_of_a_note_that_is_not_there_is_audited covers
    /// not-found, and the three interleave tests plus
    /// A_refusal_decided_inside_a_transaction_survives_the_commit_failing cover the late
    /// one. A `Control:` line that overstates its reach is the D070 defect pointed at the
    /// convention itself.
    /// </summary>
    [Fact]
    public async Task A_refused_discard_is_audited_as_a_failure()
    {
        using var client = ClientFor(await SeedProviderAsync());

        // 1. A draft somebody has written in.
        var draft = await SeedDraftAsync(client);
        using var withContent = await client.DeleteAsync($"/notes/{draft.PublicId}");
        Assert.Equal(HttpStatusCode.Conflict, withContent.StatusCode);

        var contentEvent = Assert.Single(await DiscardEventsAsync(draft.PublicId));
        Assert.Equal(AuditOutcome.Failure, contentEvent.Outcome);
        Assert.Contains("has-content", contentEvent.Metadata!, StringComparison.Ordinal);

        // 2. The same note, signed.
        await client.PostAsync($"/notes/{draft.PublicId}/sign", null);
        using var signedAttempt = await client.DeleteAsync($"/notes/{draft.PublicId}");
        Assert.Equal(HttpStatusCode.Conflict, signedAttempt.StatusCode);

        var signedEvent = (await DiscardEventsAsync(draft.PublicId))
            .Single(e => e.Metadata!.Contains("signed", StringComparison.Ordinal));
        Assert.Equal(AuditOutcome.Failure, signedEvent.Outcome);

        // 3. Its amendment, cleared — the F1 sequence, now leaving a record of the attempt.
        using var amended = await client.PostAsJsonAsync($"/notes/{draft.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        using var cleared = await client.PutAsJsonAsync($"/notes/{v2.PublicId}",
            new UpdateNoteRequest("", "", "", ""));
        cleared.EnsureSuccessStatusCode();

        using var amendmentAttempt = await client.DeleteAsync($"/notes/{v2.PublicId}");
        Assert.Equal(HttpStatusCode.Conflict, amendmentAttempt.StatusCode);

        var amendmentEvent = Assert.Single(await DiscardEventsAsync(v2.PublicId));
        Assert.Equal(AuditOutcome.Failure, amendmentEvent.Outcome);
        Assert.Contains("amendment", amendmentEvent.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// F4's other half: a DELETE that finds nothing is still an attempt.
    ///
    /// This is the one that answers "did someone walk the note ids with DELETE". Both
    /// shapes are audited — a note belonging to another provider and a public id that
    /// exists nowhere — because the response deliberately cannot tell them apart (D052)
    /// and the audit row is where the difference is allowed to be recorded at all.
    ///
    /// The row names the id that was asked for, which is the evidence. It is a GUID the
    /// caller already had, so writing it down discloses nothing new.
    ///
    /// Control: DiscardDraft's AuditRefusedDiscardAsync call on the not-found path.
    /// Deleted → red on Assert.Single for the absent id, "The collection was empty".
    /// </summary>
    [Fact]
    public async Task A_discard_of_a_note_that_is_not_there_is_audited()
    {
        using var michelle = ClientFor(await SeedProviderAsync("Michelle"));
        using var stranger = ClientFor(await SeedProviderAsync("Stranger"));

        var visit = await SeedVisitAsync(michelle);
        using var created = await michelle.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        var absentId = Guid.NewGuid();

        using var foreign = await stranger.DeleteAsync($"/notes/{note.PublicId}");
        using var absent = await stranger.DeleteAsync($"/notes/{absentId}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        var foreignEvent = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Failure, foreignEvent.Outcome);
        Assert.Contains("not-found", foreignEvent.Metadata!, StringComparison.Ordinal);

        var absentEvent = Assert.Single(await DiscardEventsAsync(absentId));
        Assert.Equal(AuditOutcome.Failure, absentEvent.Outcome);

        // The refusal itself stays byte-identical. Auditing changes the record, not the
        // answer — a 403 here would confirm the note exists (D052).
        Assert.Equal(
            await absent.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
    }


    // ------------------------------ the retry the transaction is wrapped in (F2)

    /*
     * THE TRANSACTION RUNS INSIDE A RETRY, AND A RETRY RUNS THE BODY AGAIN.
     *
     * AddInfrastructure enables EnableRetryOnFailure because Azure SQL serverless
     * auto-pauses and the first query after a pause fails while the database wakes up.
     * That is the NORMAL case for this deployment, not an exotic one — which makes "what
     * does the second attempt do" a question about ordinary Tuesday behaviour.
     *
     * A retry is only safe if the body is idempotent, and a DbContext carries state
     * across attempts: a SaveChanges that FAILED never calls AcceptAllChanges, so
     * everything it staged is still tracked, still Added, and gets inserted again by the
     * next attempt. Two audit rows for one deletion, in a table the application principal
     * cannot UPDATE or DELETE.
     *
     * Forced here rather than waited for: a strategy that retries on one marker exception
     * and an audit writer that raises it once, so the second attempt is a certainty
     * instead of a Tuesday. Both live in FailureHarness.cs — shared, because the
     * consultation write needs the same forced retry and a second copy would drift.
     */

    /// <summary>
    /// F2: one deletion leaves one audit row, however many attempts it took.
    ///
    /// AuditEvents is append-only by grant — the application principal has no UPDATE and
    /// no DELETE on it (docs/SECURITY.md) — so a duplicate row is not a cosmetic defect
    /// that a cleanup script tidies later. It is permanent, and it says a clinician
    /// deleted the same note twice.
    ///
    /// Control: AtomicWrites.WriteAtomicallyAsync — the db.ChangeTracker.Clear() at the
    /// top of each attempt.
    /// Deleted → red on Assert.Single, "Assert.Single() Failure: The collection contained
    /// 2 items".
    ///
    /// The re-read and the per-attempt AuditEvent.Record in DiscardDraft are the other
    /// half of the same rule and are NOT what this test pins: with the tracker cleared,
    /// re-attaching the stale note still deletes the right row, and the failed save left
    /// the audit entity without a key. They are named in the helper's contract because the
    /// next caller's shape will not be this one.
    /// </summary>
    [Fact]
    public async Task A_retried_discard_writes_exactly_one_audit_row()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        using var retrying = new PracticeApiFactory(sql.ConnectionString,
            services => FailureHarness.RetryOnceOnATransientBlip(sql.ConnectionString, services));
        using var retryingClient = retrying.CreateClient();
        retryingClient.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());

        using var discard = await retryingClient.DeleteAsync($"/notes/{note.PublicId}");

        // The retry is meant to be invisible to the caller: the discard succeeds.
        discard.EnsureSuccessStatusCode();
        Assert.Equal(0, await NoteCountForVisitAsync(visit));

        var only = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Success, only.Outcome);
        Assert.Contains("version=1", only.Metadata!, StringComparison.Ordinal);
    }

    // --------------------- discarding the row that was actually checked (1.14 F1)

    /*
     * THE ROW THAT WAS VALIDATED AND THE ROW THAT IS DELETED MUST BE THE SAME ROW.
     *
     * A regression, and the fix that caused it is one file away. WriteAtomicallyAsync
     * clears the change tracker at the top of every attempt, which is what stopped one
     * deletion writing two audit rows (D075) — and it also detaches the note this endpoint
     * read and checked. The body re-reads, so the checks and the delete stopped being
     * about the same object.
     *
     * The sequence: Michelle taps Discard on a draft she has not written in, and the
     * editor's autosave lands PUT /notes/{id} with real clinical text in the gap between
     * the two reads. Before the helper existed, EF's DELETE carried the RowVersion from
     * the entity the endpoint had checked, so the WHERE clause no longer matched and the
     * save raised DbUpdateConcurrencyException. After it, the re-read carries the CURRENT
     * RowVersion and the DELETE matches — the guard that used to catch this now agrees
     * with it.
     *
     * What was left standing was the trigger, one layer of the three D064 built, answering
     * a race with a 500 and — because the success audit row is inside the transaction the
     * trigger rolls back, and the refusal helper never runs on that path — with NOTHING in
     * AuditEvents. A clinical note nearly deleted, and no record that anything was tried.
     */

    /// <summary>
    /// The autosave lands mid-discard: the note survives, the caller is told why, and the
    /// attempt is on file.
    ///
    /// Forced with an interceptor rather than raced, for the reason on the harness: two
    /// live requests reproduce this ordering once in thousands of runs and never in CI.
    /// The PUT goes through a factory of its own so its own reads are not counted.
    ///
    /// All three assertions are the finding. Today's code answers 500 with an empty
    /// AuditEvents table; the note survives either way, because the trigger is doing the
    /// work the endpoint stopped doing.
    ///
    /// Control: NoteEndpoints.DiscardDraft — the RefusalToDiscard(doomed) re-check inside
    /// the WriteAtomicallyAsync body, now the local function DiscardTheRow.
    /// Deleted → red on the first assertion, "Assert.Equal() Failure: Values differ,
    /// Expected: Conflict, Actual: InternalServerError" — the DELETE is issued, the trigger
    /// rolls the transaction back, and AuditEvents holds nothing at all.
    ///
    /// Re-run after the body grew a catch and moved into a local function (D077), and the
    /// message is unchanged — deliberately. The autosave lands BEFORE the body's read here,
    /// so the re-read carries the current RowVersion and the DELETE matches: what stops it
    /// is the trigger, raising a DbUpdateException that the concurrency catch neither sees
    /// nor should. That catch answers the NEXT window along, and
    /// An_autosave_landing_between_the_read_and_the_delete_is_refused is its test.
    /// </summary>
    [Fact]
    public async Task An_autosave_landing_mid_discard_is_refused_rather_than_deleted()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        const string Autosaved = "Mum reports Maya used 'want juice' at home.";

        async Task TheAutosaveLands()
        {
            using var saved = await client.PutAsJsonAsync($"/notes/{note.PublicId}",
                new UpdateNoteRequest(Autosaved, "", "", ""));
            saved.EnsureSuccessStatusCode();
        }

        var interleave =
            new InterleavesOneWriteBeforeTheSecondRead("ClinicalNotes", TheAutosaveLands);

        using var racing = new PracticeApiFactory(
            sql.ConnectionString, FailureHarness.With(sql.ConnectionString, interleave));

        using var racingClient = ClientFor(racing, providerPublicId);

        // After the host is up, so nothing it read on startup is counted.
        interleave.Arm();

        using var discard = await racingClient.DeleteAsync($"/notes/{note.PublicId}");

        // A race the clinician caused herself, answered as a refusal she can read — not as
        // a failure with a trace id.
        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);

        // The note, and every character the autosave put in it.
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
        Assert.Equal(Autosaved, await SubjectiveAsync(note.PublicId));

        // The near-miss is on file. This is the row that answers "was a clinical record
        // nearly removed", and it is the one the trigger path never wrote.
        var refusal = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Failure, refusal.Outcome);
        Assert.Contains("has-content", refusal.Metadata!, StringComparison.Ordinal);
    }

    // ------------------- the window one round trip later than the re-check (1.15 F2)

    /*
     * THE RE-CHECK CLOSED THE WINDOW IT WAS SHOWN, AND THE NEXT ONE ALONG STAYED OPEN.
     *
     * D081 answered "an autosave lands before the transaction's second read" and recorded
     * the class — 500 with zero audit rows on a DELETE of a clinical note — as closed. It
     * was not. The interceptor that forced the reproduction fires BEFORE the second read,
     * so the test could not reach the gap between that read and the DELETE it decides on;
     * an autosave landing there moves the RowVersion the DELETE carries in its WHERE
     * clause, the statement matches nothing, EF raises DbUpdateConcurrencyException, and
     * nothing caught DbUpdate anything. Same outcome, one round trip later, and reported
     * by a reviewer rather than by the suite.
     *
     * THREE INTERLEAVINGS REACH IT, and they are three tests because they want three
     * different answers:
     *
     *   the autosave writes content       → the note is no longer discardable: the same
     *                                       409 and the same `has-content` audit reason
     *                                       the earlier window produces
     *   another DELETE wins the race      → the row is gone and its own audit row is
     *                                       written; this request answers 200 and adds
     *                                       nothing, exactly as the `doomed is null`
     *                                       branch does
     *   the autosave writes and clears    → the row moved and is STILL an empty draft:
     *                                       `contended`, the one reason that describes the
     *                                       race rather than the row
     *
     * Enumerated rather than assumed, because the instruction that produced this task is
     * that a class is not closed until every window with the same outcome has been listed
     * and tested. The two remaining ways to reach a 500 here are deliberate and covered
     * elsewhere: a broken audit table rolls the delete back (A_broken_audit_write_rolls_
     * the_delete_back), and a DELETE the TRIGGER refuses is a disagreement between the
     * endpoint's predicate and the database's, which is a defect and should be loud rather
     * than dressed up as a refusal.
     */

    /// <summary>
    /// The autosave lands after the re-read, in the gap before the DELETE: refused, not
    /// deleted, and on file.
    ///
    /// Control: NoteEndpoints.DiscardDraft — the <c>catch (DbUpdateConcurrencyException)</c>
    /// around the DELETE's SaveChangesAsync.
    /// Deleted → red on the first assertion, "Assert.Equal() Failure: Values differ,
    /// Expected: Conflict, Actual: InternalServerError" — the exception escapes to
    /// UseExceptionHandler, the transaction rolls back, and AuditEvents holds nothing.
    /// </summary>
    [Fact]
    public async Task An_autosave_landing_between_the_read_and_the_delete_is_refused()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        const string Autosaved = "Mum reports Maya used 'want juice' at home.";

        async Task TheAutosaveLands()
        {
            using var saved = await client.PutAsJsonAsync($"/notes/{note.PublicId}",
                new UpdateNoteRequest(Autosaved, "", "", ""));
            saved.EnsureSuccessStatusCode();
        }

        using var discard = await DiscardWithAnInterleavedWriteAsync(
            providerPublicId, note.PublicId, TheAutosaveLands);

        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);

        // The note, and every character the autosave put in it.
        Assert.Equal(1, await NoteCountForVisitAsync(visit));
        Assert.Equal(Autosaved, await SubjectiveAsync(note.PublicId));

        // The SAME reason the earlier window produces. Two adjacent races that end the
        // same way must be countable as one thing.
        var refusal = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Failure, refusal.Outcome);
        Assert.Contains("has-content", refusal.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A double tap: a second DELETE commits in the same gap.
    ///
    /// The row is gone and the request that removed it wrote the NoteDiscarded row for the
    /// removal. This one answers as though it had done it — one deletion, one audit row —
    /// which is what the <c>doomed is null</c> branch already decides when the other
    /// request wins a moment earlier. A second Success row here would say the note was
    /// deleted twice, in a table nothing can UPDATE or DELETE.
    ///
    /// Control: the <c>if (current is null) return;</c> branch inside DiscardDraft's
    /// DbUpdateConcurrencyException handler.
    /// Deleted → red on the status assertion, "Assert.Equal() Failure: Values differ,
    /// Expected: OK, Actual: Conflict" — the request falls through to `contended` and
    /// tells the clinician to open a note that is not there.
    /// </summary>
    [Fact]
    public async Task A_second_discard_landing_between_the_read_and_the_delete_is_not_an_error()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        async Task TheOtherTapLands()
        {
            using var first = await client.DeleteAsync($"/notes/{note.PublicId}");
            first.EnsureSuccessStatusCode();
        }

        using var discard = await DiscardWithAnInterleavedWriteAsync(
            providerPublicId, note.PublicId, TheOtherTapLands);

        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
        Assert.Equal(0, await NoteCountForVisitAsync(visit));

        // One deletion, one row — written by whichever request actually removed it.
        var only = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Success, only.Outcome);
    }

    /// <summary>
    /// The autosave writes and then clears again: the row moved, and is still an empty
    /// draft.
    ///
    /// This is the interleaving the other two do not cover, and it is ordinary — she typed
    /// a line, thought better of it, and the editor saved twice. `RefusalToDiscard` has
    /// nothing to object to, so a vocabulary that only described rows would have had to
    /// invent an answer. `contended` describes the race instead, and the request writes
    /// nothing: the note is kept, and one more tap removes it.
    ///
    /// Deliberately NOT retried in place. A DELETE that keeps re-reading and re-attempting
    /// against a row somebody is actively writing to is a loop with a child's clinical
    /// record at the end of it, and the honest bound on that loop is "ask again".
    ///
    /// Control: the <c>?? ("contended", …)</c> fallback in DiscardDraft's
    /// DbUpdateConcurrencyException handler.
    /// Deleted — `lateRefusal = RefusalToDiscard(current)` alone → red on the status
    /// assertion, "Assert.Equal() Failure: Values differ, Expected: Conflict, Actual: OK"
    /// — the endpoint reports a deletion that its own DELETE statement did not perform.
    /// </summary>
    [Fact]
    public async Task A_note_that_moved_and_is_still_empty_is_kept_and_the_race_recorded()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        async Task TheAutosaveWritesAndClears()
        {
            // Two writes, because one PUT of four empty strings onto an already-empty note
            // changes no property and EF issues no UPDATE at all — the RowVersion would
            // not move and there would be no race to observe.
            using var typed = await client.PutAsJsonAsync($"/notes/{note.PublicId}",
                new UpdateNoteRequest("Mum reports", "", "", ""));
            typed.EnsureSuccessStatusCode();

            using var cleared = await client.PutAsJsonAsync($"/notes/{note.PublicId}",
                new UpdateNoteRequest("", "", "", ""));
            cleared.EnsureSuccessStatusCode();
        }

        using var discard = await DiscardWithAnInterleavedWriteAsync(
            providerPublicId, note.PublicId, TheAutosaveWritesAndClears);

        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);
        Assert.Equal(1, await NoteCountForVisitAsync(visit));

        var refusal = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Failure, refusal.Outcome);
        Assert.Contains("contended", refusal.Metadata!, StringComparison.Ordinal);
    }

    // ------------- the refusal row a rollback used to be able to erase (1.15 F3)

    /// <summary>
    /// The commit fails after the refusal has been decided, and the refusal is still on
    /// file.
    ///
    /// Four refusals leave this endpoint — not-found, and the three RefusalToDiscard
    /// returns — and three of them were written outside any transaction, on a writer with
    /// no cancellation token, where nothing could take them back. The fourth, the one
    /// decided inside the transaction, was written inside it. That made the row the code
    /// itself calls "the interesting row" the only one a rollback could erase, which
    /// inverts D075 for exactly the case D075 exists for.
    ///
    /// Forced with an interceptor that refuses to commit. Nothing else distinguishes a row
    /// written inside a transaction from one written outside it: both are in the table
    /// afterwards on every run that succeeds.
    ///
    /// The request still fails — the caller gets a 500, because a commit that will not
    /// commit is a fault and not a refusal — and that is the point. 500 with the attempt
    /// recorded is a different outcome from 500 with an empty table.
    ///
    /// Control: the <c>finally</c> around WriteAtomicallyAsync in DiscardDraft, which is
    /// what writes the refusal row whatever the transaction does.
    /// Moved back inside the body — the AuditRefusedDiscardAsync call placed before the
    /// `return` on the late-refusal branch — → red on Assert.Single, "The collection was
    /// empty" — the INSERT joins the transaction the interceptor refuses to commit, and
    /// the near miss leaves no trace at all.
    /// </summary>
    [Fact]
    public async Task A_refusal_decided_inside_a_transaction_survives_the_commit_failing()
    {
        var providerPublicId = await SeedProviderAsync();

        using var client = ClientFor(providerPublicId);
        var visit = await SeedVisitAsync(client);

        using var created = await client.PostAsJsonAsync("/notes",
            new CreateNoteRequest(visit, "", "", "", ""));
        var note = (await created.Content.ReadFromJsonAsync<NoteDto>())!;

        async Task TheAutosaveLands()
        {
            using var saved = await client.PutAsJsonAsync($"/notes/{note.PublicId}",
                new UpdateNoteRequest("Mum reports Maya used 'want juice'.", "", "", ""));
            saved.EnsureSuccessStatusCode();
        }

        // Before the body's read, so the refusal is decided INSIDE the transaction — the
        // only refusal of the four that ever was.
        var interleave = new InterleavesOneWriteBeforeTheSecondRead(
            "ClinicalNotes", TheAutosaveLands);

        using var racing = new PracticeApiFactory(sql.ConnectionString,
            FailureHarness.With(sql.ConnectionString, interleave, new FailsEveryCommit()));

        using var racingClient = ClientFor(racing, providerPublicId);
        interleave.Arm();

        using var discard = await racingClient.DeleteAsync($"/notes/{note.PublicId}");

        // A commit that will not commit is a fault, and it answers like one.
        Assert.Equal(HttpStatusCode.InternalServerError, discard.StatusCode);

        // The note is untouched, and the attempt on it is recorded — which is the whole
        // difference between this and the same failure a commit ago.
        Assert.Equal(1, await NoteCountForVisitAsync(visit));

        var refusal = Assert.Single(await DiscardEventsAsync(note.PublicId));
        Assert.Equal(AuditOutcome.Failure, refusal.Outcome);
        Assert.Contains("has-content", refusal.Metadata!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sends DELETE /notes/{id} with <paramref name="interleavedWrite"/> landing in the gap
    /// between the transaction's re-read and its DELETE statement.
    ///
    /// The interleaved write runs through the ordinary endpoints on a factory of its own,
    /// so its statements are not the ones being watched for.
    /// </summary>
    private async Task<HttpResponseMessage> DiscardWithAnInterleavedWriteAsync(
        Guid providerPublicId, Guid notePublicId, Func<Task> interleavedWrite)
    {
        var interleave =
            new InterleavesOneWriteBeforeTheDelete("ClinicalNotes", interleavedWrite);

        using var racing = new PracticeApiFactory(
            sql.ConnectionString, FailureHarness.With(sql.ConnectionString, interleave));

        using var racingClient = ClientFor(racing, providerPublicId);

        // After the host is up, so nothing it wrote on startup is counted.
        interleave.Arm();

        return await racingClient.DeleteAsync($"/notes/{notePublicId}");
    }

    /// <summary>The note's Subjective section, read past tenancy at the raw row.</summary>
    private async Task<string> SubjectiveAsync(Guid notePublicId)
    {
        using var scope = _factory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PracticeDbContext>();

        return await db.ClinicalNotes.IgnoreQueryFilters().AsNoTracking()
            .Where(n => n.PublicId == notePublicId)
            .Select(n => n.Subjective)
            .SingleAsync();
    }

    // -------------------------- the audit that outlives the connection (F1)

    /*
     * AN AUDIT ROW MUST NOT DEPEND ON THE CLIENT STAYING CONNECTED.
     *
     * D071 fixed this on the success path and left it on every other one. A DELETE that is
     * REFUSED writes its row and nothing else — there is no clinical write to carry it —
     * so with that write on the request's cancellation token, a caller who sends the
     * request and drops the connection leaves nothing behind at all. Walk ten thousand
     * note ids that way and AuditEvents is empty, which is precisely the question
     * docs/SECURITY.md §Audit says the not-found rows exist to answer.
     *
     * The same shape was on every other audit write in this API — reads, signatures,
     * amendments, patient and guardian writes, and every login outcome. It is closed at
     * the seam rather than at the call sites: IAuditWriter.WriteAsync takes no
     * CancellationToken, so there is no token left for a caller to hand it.
     */

    /*
     * DroppableConnection and DroppableConnectionFilter used to live here, privately.
     *
     * They moved to FailureHarness.cs when the rate limiter needed the same shape — a
     * control that only shows itself once the caller has gone — for the reason that file's
     * header gives: two copies of a harness drift, and the sibling nobody updated is this
     * repository's most repeated defect. Nothing else about this test changed.
     */

    /// <summary>
    /// Drops the connection at the moment an audit row is about to be written, then hands
    /// off to the REAL writer.
    ///
    /// Delegating rather than reimplementing is the point: what is under test is
    /// AuditWriter's own behaviour once the request has gone, so the test must not be
    /// holding the pen.
    /// </summary>
    private sealed class DropsTheConnectionThenWrites(
        PracticeDbContext db, IHttpContextAccessor http, UncancellableWriteDeadline deadline)
        : IAuditWriter
    {
        // The application's own writer, on the application's own deadline. Substituting a
        // fresh unbound deadline here would quietly restore the pre-fix behaviour and this
        // test would go on passing while proving less.
        private readonly AuditWriter _real = new(db, deadline);

        public Task WriteAsync(AuditEvent auditEvent)
        {
            http.HttpContext!.Abort();
            return _real.WriteAsync(auditEvent);
        }
    }

    private static void DropTheConnectionOnEveryAuditWrite(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<IStartupFilter, DroppableConnectionFilter>();
        services.AddScoped<IAuditWriter, DropsTheConnectionThenWrites>();
    }

    private PracticeApiFactory DisconnectingFactory() =>
        new(sql.ConnectionString, DropTheConnectionOnEveryAuditWrite);

    private static HttpClient ClientFor(PracticeApiFactory factory, Guid providerPublicId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RequestProviderContext.HeaderName, providerPublicId.ToString());
        return client;
    }

    /// <summary>
    /// F1: all four refusals, each recorded after the caller has gone.
    ///
    /// Every reason in the fixed vocabulary is exercised, because each is a separate call
    /// site and the defect this closes is exactly that a fix landed on one path and not on
    /// its siblings. `not-found` comes first for the reason SECURITY.md gives: a 404
    /// cannot distinguish "not yours" from "never existed" (D052), so the audit row is the
    /// only place that attempt is recorded at all.
    ///
    /// Control: UncancellableWriteDeadline.BindTo starting a GRACE rather than cancelling.
    /// Changed to `_expiry.Cancel()` on the request token — which is what "the audit write
    /// observes the caller's cancellation" means once the write is on a deadline rather
    /// than on CancellationToken.None — → red on the first assertion, "Assert.Single()
    /// Failure: The collection was empty".
    ///
    /// Re-run because the mechanism moved (D077). The line used to name the absence of a
    /// CancellationToken parameter on IAuditWriter.WriteAsync, with AuditWriter saving on
    /// CancellationToken.None; the parameter is still absent and the save is still not the
    /// caller's, but None was replaced by a bounded per-request deadline in D090, so the
    /// deletion that isolates this property is now the one above.
    /// </summary>
    [Fact]
    public async Task A_refused_discard_is_audited_even_when_the_caller_disconnects()
    {
        var providerPublicId = await SeedProviderAsync();
        using var client = ClientFor(providerPublicId);

        var draft = await SeedDraftAsync(client);

        using var factory = DisconnectingFactory();
        using var dropping = ClientFor(factory, providerPublicId);

        // not-found: an id that exists nowhere. Indistinguishable, by design, from one
        // belonging to another clinician.
        var absentId = Guid.NewGuid();
        (await dropping.DeleteAsync($"/notes/{absentId}")).Dispose();

        Assert.Contains("not-found",
            Assert.Single(await DiscardEventsAsync(absentId)).Metadata!,
            StringComparison.Ordinal);

        // has-content: a draft somebody has written in.
        (await dropping.DeleteAsync($"/notes/{draft.PublicId}")).Dispose();

        Assert.Contains("has-content",
            Assert.Single(await DiscardEventsAsync(draft.PublicId)).Metadata!,
            StringComparison.Ordinal);

        // signed: the same note, attested to.
        using (var signed = await client.PostAsync($"/notes/{draft.PublicId}/sign", null))
        {
            signed.EnsureSuccessStatusCode();
        }

        (await dropping.DeleteAsync($"/notes/{draft.PublicId}")).Dispose();

        Assert.Contains(await DiscardEventsAsync(draft.PublicId),
            e => e.Metadata!.Contains("reason=signed", StringComparison.Ordinal));

        // amendment: its next version, cleared — the sequence D069 closed.
        using var amended = await client.PostAsJsonAsync($"/notes/{draft.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        using (var cleared = await client.PutAsJsonAsync($"/notes/{v2.PublicId}",
            new UpdateNoteRequest("", "", "", "")))
        {
            cleared.EnsureSuccessStatusCode();
        }

        (await dropping.DeleteAsync($"/notes/{v2.PublicId}")).Dispose();

        Assert.Contains("reason=amendment",
            Assert.Single(await DiscardEventsAsync(v2.PublicId)).Metadata!,
            StringComparison.Ordinal);

        // Every one of them a refusal, and none of them a deletion.
        Assert.All(await DiscardEventsAsync(draft.PublicId),
            e => Assert.Equal(AuditOutcome.Failure, e.Outcome));
    }

    /// <summary>
    /// The same property on a different endpoint, which is what makes this a class rather
    /// than a case.
    ///
    /// Opening a note discloses full S/O/A/P for every version and writes PatientViewed
    /// (D065). The disclosure has already happened by the time that row is written, so a
    /// caller who disconnects has still read the record — and the row saying so was the
    /// thing being abandoned.
    ///
    /// Control: UncancellableWriteDeadline.BindTo starting a GRACE rather than cancelling.
    /// Changed to `_expiry.Cancel()` on the request token → red on Assert.Single,
    /// "Assert.Single() Failure: The collection was empty".
    ///
    /// Re-run for the reason given on the sibling above: the property is unchanged and the
    /// mechanism holding it is not (D077, D090). Both tests were re-run against the same
    /// single-line change, which is what makes this a class rather than a case in the
    /// direction that matters — one deletion, two endpoints, two reds.
    /// </summary>
    [Fact]
    public async Task A_note_read_is_audited_even_when_the_caller_disconnects()
    {
        var providerPublicId = await SeedProviderAsync();
        using var client = ClientFor(providerPublicId);

        var draft = await SeedDraftAsync(client);

        using var factory = DisconnectingFactory();
        using var dropping = ClientFor(factory, providerPublicId);

        (await dropping.GetAsync($"/notes/{draft.PublicId}/history")).Dispose();

        var read = Assert.Single(await NoteReadEventsAsync(draft.PublicId));
        Assert.Equal(AuditOutcome.Success, read.Outcome);
        Assert.Contains("versions=1", read.Metadata!, StringComparison.Ordinal);
    }

    // ------------------- what a refusal is called, and what it tells her (F3)

    /*
     * A REFUSAL HAS TWO AUDIENCES AND THEY NEED DIFFERENT THINGS.
     *
     * The audit row is counted a year later by someone asking "did anyone try to delete a
     * signed clinical record". The sentence is read now, by a clinician who has to be told
     * what to do next. They were being decided by one branch, and both were wrong for the
     * same two notes:
     *
     *   a SIGNED amendment  audited as `refused;reason=amendment`, so the count of
     *                       attempts on signed records was short by exactly the set of
     *                       amended — i.e. contested — records, and the sentence asked her
     *                       to correct and sign a note that was already signed;
     *
     *   a SUPERSEDED v1     told "amend it instead", which Amend() then refuses, because a
     *                       version that has been replaced is not the one to amend.
     */

    /// <summary>
    /// A signed amendment is a signed clinical record. That is what the row must say.
    ///
    /// Reached by asking about Status before asking about lineage. The amendment branch
    /// still has to come before the emptiness one — a cleared amendment is a Draft with
    /// four empty sections — so the order is status, then lineage, then content.
    ///
    /// Control: NoteEndpoints.RefusalToDiscard — the `note.Status != NoteStatus.Draft`
    /// branch standing ahead of the `note.SupersedesNoteId is not null` branch. (Inline in
    /// DiscardDraft when this line was written; deletion re-run against the predicate the
    /// two are now in — D077.)
    /// Deleted → red on Assert.Contains("reason=signed", …), the metadata reading
    /// "refused;reason=amendment".
    /// </summary>
    [Fact]
    public async Task A_signed_amendment_is_refused_as_signed_rather_than_as_an_amendment()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var v1 = await SeedDraftAsync(client);

        using (var signed = await client.PostAsync($"/notes/{v1.PublicId}/sign", null))
        {
            signed.EnsureSuccessStatusCode();
        }

        using var amended = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        var v2 = (await amended.Content.ReadFromJsonAsync<NoteDto>())!;

        // The amendment is signed in its turn. From here it is a clinical record like any
        // other, and its lineage is history rather than status.
        using (var signedAgain = await client.PostAsync($"/notes/{v2.PublicId}/sign", null))
        {
            signedAgain.EnsureSuccessStatusCode();
        }

        using var discard = await client.DeleteAsync($"/notes/{v2.PublicId}");
        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);

        var refusal = Assert.Single(await DiscardEventsAsync(v2.PublicId));
        Assert.Equal(AuditOutcome.Failure, refusal.Outcome);
        Assert.Contains("reason=signed", refusal.Metadata!, StringComparison.Ordinal);

        // v2 is current, so amending it IS the way forward and the copy may say so.
        var message = await discard.Content.ReadAsStringAsync();
        Assert.Contains("amend it instead", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A superseded version is never told to amend itself.
    ///
    /// The check is not on the wording but on the ADVICE: the test performs the action the
    /// old sentence named and asserts the API refuses it. A refusal that sends a clinician
    /// to a second refusal is worse than a bare "no" — she now believes the record is
    /// broken rather than that she is on the wrong version of it.
    ///
    /// Control: NoteEndpoints.RefusalToDiscard — the `note.IsCurrent` branch choosing
    /// between the two signed sentences. (Inline in DiscardDraft when this line was
    /// written; deletion re-run against its new home — D077.)
    /// Deleted → red on Assert.DoesNotContain("amend it instead", …), which reads
    /// "String.DoesNotContain() Failure: Sub-string found".
    /// </summary>
    [Fact]
    public async Task A_superseded_version_is_not_told_to_amend_itself()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var v1 = await SeedDraftAsync(client);

        using (var signed = await client.PostAsync($"/notes/{v1.PublicId}/sign", null))
        {
            signed.EnsureSuccessStatusCode();
        }

        using var amended = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        amended.EnsureSuccessStatusCode();

        using var discard = await client.DeleteAsync($"/notes/{v1.PublicId}");
        Assert.Equal(HttpStatusCode.Conflict, discard.StatusCode);

        var message = await discard.Content.ReadAsStringAsync();

        // The action the old sentence recommended, taken at its word.
        using var followingTheAdvice = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Following the message on the screen."));
        Assert.Equal(HttpStatusCode.Conflict, followingTheAdvice.StatusCode);

        Assert.DoesNotContain("amend it instead", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaced", message, StringComparison.OrdinalIgnoreCase);

        // Still audited as an attempt on a signed record, which is what it is.
        Assert.Contains("reason=signed",
            Assert.Single(await DiscardEventsAsync(v1.PublicId)).Metadata!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same sentence, in the other place a clinician can meet it.
    ///
    /// PUT /notes/{superseded} answers 409 with the domain's own wording, and the domain
    /// said "Create an amendment instead" whether or not this version could be amended.
    /// One finding, two call sites — the editing refusal is the sibling of the discard
    /// refusal and had the identical defect.
    ///
    /// Control: NoteEndpoints.UpdateDraft, via ClinicalNote.UpdateContent — the
    /// NoteStatus.Amended branch of the refusal message.
    /// Deleted → red on Assert.DoesNotContain("amendment instead", …), "Sub-string found".
    /// </summary>
    [Fact]
    public async Task A_superseded_version_is_not_told_to_amend_itself_when_edited()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var v1 = await SeedDraftAsync(client);

        using (var signed = await client.PostAsync($"/notes/{v1.PublicId}/sign", null))
        {
            signed.EnsureSuccessStatusCode();
        }

        using var amended = await client.PostAsJsonAsync($"/notes/{v1.PublicId}/amend",
            new AmendNoteRequest("Corrected the accuracy figure."));
        amended.EnsureSuccessStatusCode();

        using var edit = await client.PutAsJsonAsync($"/notes/{v1.PublicId}",
            new UpdateNoteRequest("Rewritten.", "", "", ""));

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);

        var message = await edit.Content.ReadAsStringAsync();
        Assert.DoesNotContain("amendment instead", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replaced", message, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>
    /// A signature time is an instant, and must reach the browser as one.
    ///
    /// The note screen renders SignedAtUtc through Intl in America/New_York. With no Z the
    /// BFF parses it as local, and a note signed at 20:00 UTC displays as having been
    /// signed at 20:00 Eastern — four hours out, on the timestamp that says when a
    /// clinician attested to a child's record.
    ///
    /// Asserted on the raw body for the same reason as the scheduling case: DateTime
    /// parses a designator-less value without complaint, so only the bytes show it.
    ///
    /// Control: PracticeDbContext.OnModelCreating — the UTC value converter.
    /// Deleted → red on "signedAtUtc … 2026-…T…", with no Z.
    /// </summary>
    [Fact]
    public async Task A_signature_time_reaches_the_client_marked_utc()
    {
        using var client = ClientFor(await SeedProviderAsync());
        var draft = await SeedDraftAsync(client);

        using var signed = await client.PostAsync($"/notes/{draft.PublicId}/sign", null);
        signed.EnsureSuccessStatusCode();

        // The history endpoint, because that is the read path the product uses (D065) and
        // the one that reads the timestamp back out of the database rather than echoing
        // the entity it just signed.
        using var history = await client.GetAsync($"/notes/{draft.PublicId}/history");
        history.EnsureSuccessStatusCode();

        var payload = await history.Content.ReadAsStringAsync();

        Assert.Contains("signedAtUtc", payload, StringComparison.Ordinal);
        SchedulingTests.AssertEveryUtcFieldEndsWithZ(payload, "GET /notes/{id}/history");
    }

    private sealed record ScheduledDto(Guid PublicId, DateTime StartUtc, short DurationMinutes);
}
