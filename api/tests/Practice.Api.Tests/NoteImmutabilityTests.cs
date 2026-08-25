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

    /// <summary>
    /// The database half of the discard rule, like the UPDATE trigger above.
    ///
    /// A DELETE that never went through the application — a cleanup script, a bulk
    /// operation, SSMS — must not be able to remove a real clinical record.
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

    private sealed record ScheduledDto(Guid PublicId, DateTime StartUtc, short DurationMinutes);
}
