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
    /// Control: NoteEndpoints.DiscardDraft — the `note.SupersedesNoteId is not null`
    /// branch.
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
    /// Control: TR_ClinicalNotes_PreventDeletingRealNotes — the SupersedesNoteId clause.
    /// Deleted → red on Assert.ThrowsAnyAsync, "Assert.ThrowsAny() Failure: No exception
    /// was thrown".
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
    /// Control: TR_ClinicalNotes_PreventDeletingRealNotes — the Status clause.
    /// Deleted → red on Assert.ThrowsAnyAsync, "No exception was thrown".
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
    /// An IAuditWriter that cannot write, to force the failure the atomicity claim is
    /// about. Nothing in a passing run can distinguish "committed together" from
    /// "committed one after the other" — only a broken second write can.
    /// </summary>
    private sealed class UnwritableAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default) =>
            throw new InvalidOperationException("The audit table is unavailable.");
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
    /// Control: DiscardDraft's BeginTransactionAsync / CommitAsync pair.
    /// Deleted → red on Assert.Equal(1, NoteCountForVisitAsync), "Expected: 1, Actual: 0"
    /// — the note is destroyed and nothing records it.
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
    /// Control: DiscardDraft's AuditRefusedDiscardAsync calls.
    /// Deleted → red on Assert.Single(events), "The collection was empty".
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
