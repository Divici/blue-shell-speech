using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Auditing;
using Practice.Domain.ClinicalNotes;
using Practice.Domain.Goals;
using Practice.Infrastructure.Identity;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.ClinicalNotes;

/// <summary>
/// Goals and clinical notes.
///
/// Provider-scoped like everything else, and 404 rather than 403 for anything belonging to
/// someone else.
/// </summary>
public static class NoteEndpoints
{
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var goals = app.MapGroup("/patients/{patientPublicId:guid}/goals").WithTags("Goals");
        goals.MapGet("/", ListGoals);
        goals.MapPost("/", CreateGoal);
        goals.MapPost("/{goalPublicId:guid}/met", MarkGoalMet);
        goals.MapPost("/{goalPublicId:guid}/discontinue", DiscontinueGoal);

        var notes = app.MapGroup("/notes").WithTags("Clinical notes");
        notes.MapGet("/appointment/{appointmentPublicId:guid}", GetNoteForAppointment);
        notes.MapGet("/{publicId:guid}/history", GetNoteHistory);
        notes.MapPost("/", CreateDraft);
        notes.MapPut("/{publicId:guid}", UpdateDraft);
        notes.MapDelete("/{publicId:guid}", DiscardDraft);
        notes.MapPost("/{publicId:guid}/sign", SignNote);
        notes.MapPost("/{publicId:guid}/amend", AmendNote);

        return app;
    }

    // ------------------------------------------------------------------ goals

    private static async Task<IResult> ListGoals(
        Guid patientPublicId,
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct,
        bool activeOnly = false)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PublicId == patientPublicId, ct);
        if (patient is null) return Results.NotFound();

        var query = db.Goals.AsNoTracking().Where(g => g.PatientId == patient.Id);
        if (activeOnly) query = query.Where(g => g.Status == GoalStatus.Active);

        /*
         * Projected to the columns in SQL, named in memory.
         *
         * Nullable<TEnum>.ToString() returns string.EMPTY, not null — so translating the
         * whole projection into SQL turned "no cue level was specified" into "" and the
         * DTO's own `string?` became a lie. A consumer checking for null then reads an
         * empty string as a real value.
         *
         * The two-step keeps the column list narrow in the query and lets `?.ToString()`
         * do the nullable-aware thing on the way out.
         */
        var rows = await query
            .OrderBy(g => g.Status).ThenByDescending(g => g.StartDate)
            .Select(g => new
            {
                g.PublicId,
                g.GoalText,
                g.Domain,
                g.TargetCriteria,
                g.CueLevelExpected,
                g.Status,
                g.StartDate,
                g.EndDate,
                g.AacModality,
                g.AacDeviceNotes,
            })
            .ToListAsync(ct);

        var results = rows.Select(g => new GoalDto(
            g.PublicId, g.GoalText, g.Domain.ToString(), g.TargetCriteria,
            g.CueLevelExpected?.ToString(), g.Status.ToString(),
            g.StartDate, g.EndDate,
            g.AacModality?.ToString(), g.AacDeviceNotes)).ToList();

        return Results.Ok(results);
    }

    private static async Task<IResult> CreateGoal(
        Guid patientPublicId,
        CreateGoalRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var patient = await db.Patients.AsNoTracking()
            .SingleOrDefaultAsync(p => p.PublicId == patientPublicId, ct);
        if (patient is null) return Results.NotFound();

        Goal goal;
        try
        {
            goal = Goal.Create(
                provider.ProviderId.Value,
                patient.Id,
                request.GoalText,
                request.Domain,
                request.StartDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime),
                request.TargetCriteria,
                request.CueLevelExpected,
                request.AacModality,
                request.AacDeviceNotes);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        db.Goals.Add(goal);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/patients/{patientPublicId}/goals/{goal.PublicId}",
            new { goal.PublicId });
    }

    private static Task<IResult> MarkGoalMet(
        Guid patientPublicId, Guid goalPublicId,
        PracticeDbContext db, IProviderContext provider, TimeProvider clock,
        CancellationToken ct) =>
        TransitionGoal(goalPublicId, db, provider,
            g => g.MarkMet(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), ct);

    private static Task<IResult> DiscontinueGoal(
        Guid patientPublicId, Guid goalPublicId,
        PracticeDbContext db, IProviderContext provider, TimeProvider clock,
        CancellationToken ct) =>
        TransitionGoal(goalPublicId, db, provider,
            g => g.Discontinue(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)), ct);

    private static async Task<IResult> TransitionGoal(
        Guid goalPublicId,
        PracticeDbContext db,
        IProviderContext provider,
        Action<Goal> transition,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var goal = await db.Goals.SingleOrDefaultAsync(g => g.PublicId == goalPublicId, ct);
        if (goal is null) return Results.NotFound();

        try
        {
            transition(goal);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { goal.PublicId, Status = goal.Status.ToString() });
    }

    // ------------------------------------------------------------------ notes

    private static async Task<IResult> GetNoteForAppointment(
        Guid appointmentPublicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var appointment = await db.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PublicId == appointmentPublicId, ct);
        if (appointment is null) return Results.NotFound();

        var note = await db.ClinicalNotes.AsNoTracking()
            .SingleOrDefaultAsync(n => n.AppointmentId == appointment.Id && n.IsCurrent, ct);

        if (note is null) return Results.NotFound();

        // Reading a clinical note is access to ePHI, and therefore auditable.
        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientViewed, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: note.PublicId), ct);

        return Results.Ok(NoteDto.From(note));
    }

    /// <summary>
    /// Every version of a note, newest first.
    ///
    /// The whole point of versioning is that the history is readable — an amended record
    /// where only the latest version can be retrieved is not an audit trail.
    ///
    /// THIS IS THE READ PATH THE PRODUCT USES. The note screen opens here, so an
    /// unaudited version of it means there is no record that a clinical note was ever
    /// opened — the gap docs/SECURITY.md §Audit exists to close, and the one D012 counts
    /// on when it accepts TDE over Always Encrypted.
    /// </summary>
    private static async Task<IResult> GetNoteHistory(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var note = await db.ClinicalNotes.AsNoTracking()
            .SingleOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (note is null) return Results.NotFound();

        var versions = await db.ClinicalNotes.AsNoTracking()
            .Where(n => n.AppointmentId == note.AppointmentId)
            .OrderByDescending(n => n.VersionNumber)
            .ToListAsync(ct);

        /*
         * Written AFTER the read resolved, and describing what actually came back.
         *
         * This endpoint returns full S/O/A/P for every version, so "a note was viewed"
         * understates it — the row records how many versions were disclosed. A count is
         * not clinical content; the four sections never touch this table.
         */
        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.PatientViewed, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: publicId,
            metadata: $"versions={versions.Count}"), ct);

        return Results.Ok(versions.Select(NoteDto.From).ToList());
    }

    private static async Task<IResult> CreateDraft(
        CreateNoteRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var appointment = await db.Appointments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.PublicId == request.AppointmentPublicId, ct);
        if (appointment is null) return Results.NotFound();

        var existing = await db.ClinicalNotes.AsNoTracking()
            .AnyAsync(n => n.AppointmentId == appointment.Id && n.IsCurrent, ct);

        if (existing)
        {
            return Results.Conflict(new
            {
                message = "This visit already has a note. Amend it rather than starting another.",
            });
        }

        /*
         * Checked SECOND, so "this visit already has a note" stays the first-class
         * conflict: the day view's start-or-open action turns that one into "open the one
         * that exists" (D061), and a note written before a visit was cancelled must stay
         * reachable.
         *
         * Checked at all because the day view is read on a phone between houses, and a
         * mis-tap on a cancelled, no-show, or future card used to create an empty draft
         * that Sign() refuses. Runs only after the provider filter resolved the
         * appointment, so someone else's visit is still 404 rather than a 409 confirming
         * it exists (D052).
         */
        var blocked = appointment.DocumentationBlockedReason(clock.GetUtcNow().UtcDateTime);
        if (blocked is not null) return Results.Conflict(new { message = blocked });

        var note = ClinicalNote.CreateDraft(
            provider.ProviderId.Value, appointment.PatientId, appointment.Id);

        note.UpdateContent(
            request.Subjective ?? "", request.Objective ?? "",
            request.Assessment ?? "", request.Plan ?? "");

        db.ClinicalNotes.Add(note);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/notes/{note.PublicId}", NoteDto.From(note));
    }

    private static async Task<IResult> UpdateDraft(
        Guid publicId,
        UpdateNoteRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var note = await db.ClinicalNotes.SingleOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (note is null) return Results.NotFound();

        try
        {
            note.UpdateContent(
                request.Subjective ?? "", request.Objective ?? "",
                request.Assessment ?? "", request.Plan ?? "");
        }
        catch (InvalidOperationException ex)
        {
            // A signed note. 409 with the domain's own wording, which is written for a
            // clinician: "create an amendment instead".
            return Results.Conflict(new { message = ex.Message });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(NoteDto.From(note));
    }

    /// <summary>
    /// Discards an empty draft.
    ///
    /// THE ONLY DELETE IN THIS API, and narrow on purpose: an unsigned note with nothing
    /// in any of the four sections. Anything else is a clinical record and answers 409.
    ///
    /// It exists because the alternative was worse. "Start note" creates the row, Sign()
    /// refuses an empty one, and UX_ClinicalNotes_OneCurrentPerAppointment blocks a
    /// replacement — so a mis-tap left a permanent "Draft" badge on a child's chart that
    /// could only be cleared by writing content onto it and signing it into immutability.
    ///
    /// The rule is enforced here, in the aggregate (ClinicalNote.CanBeDiscarded), and in
    /// the database (TR_ClinicalNotes_PreventDeletingRealNotes), on the D058 principle
    /// that application-layer rules hold until someone opens SSMS.
    /// </summary>
    private static async Task<IResult> DiscardDraft(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var note = await db.ClinicalNotes.SingleOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (note is null) return Results.NotFound();

        if (!note.CanBeDiscarded)
        {
            // Two reasons, two sentences. "That is not allowed" tells a clinician nothing
            // about which rule she met.
            return Results.Conflict(new
            {
                message = note.Status == NoteStatus.Draft
                    ? "This note has something written in it, so it is kept. Clear the sections and save if you meant to start again."
                    : "This note is signed. A signed clinical record is never deleted — amend it instead.",
            });
        }

        var version = note.VersionNumber;
        db.ClinicalNotes.Remove(note);
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.NoteDiscarded, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: publicId,
            metadata: $"version={version}"), ct);

        /*
         * A body, not 204.
         *
         * The BFF's request helper maps 404 to null, so a 204 would be indistinguishable
         * from "that note is not yours" and the UI would report a delete that never
         * happened. Same reason the goal transitions answer with one.
         */
        return Results.Ok(new { PublicId = publicId });
    }

    private static async Task<IResult> SignNote(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var note = await db.ClinicalNotes.SingleOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (note is null) return Results.NotFound();

        var signer = await db.Providers.AsNoTracking()
            .Where(p => p.Id == provider.ProviderId)
            .Select(p => p.DisplayName)
            .SingleAsync(ct);

        try
        {
            note.Sign(signer, clock.GetUtcNow().UtcDateTime);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.NoteSigned, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: note.PublicId,
            metadata: $"version={note.VersionNumber}"), ct);

        return Results.Ok(NoteDto.From(note));
    }

    /// <summary>
    /// Creates the next version. The previous one is retained in full.
    /// </summary>
    private static async Task<IResult> AmendNote(
        Guid publicId,
        AmendNoteRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        IAuditWriter audit,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var note = await db.ClinicalNotes.SingleOrDefaultAsync(n => n.PublicId == publicId, ct);
        if (note is null) return Results.NotFound();

        ClinicalNote amendment;
        try
        {
            amendment = note.Amend(request.Reason);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "reason"] = [ex.Message] });
        }

        db.ClinicalNotes.Add(amendment);

        /*
         * Both rows in ONE SaveChanges.
         *
         * The previous version's IsCurrent flip and the new row's insert must commit
         * together, or the filtered unique index sees two current notes — or none.
         */
        await db.SaveChangesAsync(ct);

        await audit.WriteAsync(AuditEvent.Record(
            AuditEventType.NoteAmended, AuditOutcome.Success,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: amendment.PublicId,
            metadata: $"version={amendment.VersionNumber};supersedes={note.PublicId}"), ct);

        return Results.Created($"/notes/{amendment.PublicId}", NoteDto.From(amendment));
    }
}

// --------------------------------------------------------------------- DTOs

public sealed record GoalDto(
    Guid PublicId, string GoalText, string Domain, string? TargetCriteria,
    string? CueLevelExpected, string Status, DateOnly StartDate, DateOnly? EndDate,
    string? AacModality, string? AacDeviceNotes);

public sealed record CreateGoalRequest(
    string GoalText, GoalDomain Domain, DateOnly? StartDate, string? TargetCriteria,
    CueLevel? CueLevelExpected, AacModality? AacModality, string? AacDeviceNotes);

public sealed record NoteDto(
    Guid PublicId, int VersionNumber, bool IsCurrent, string Status,
    string Subjective, string Objective, string Assessment, string Plan,
    string Origin, DateTime? SignedAtUtc, string? SignedBy, string? AmendmentReason,
    bool IntegrityVerified)
{
    public static NoteDto From(ClinicalNote note) => new(
        note.PublicId, note.VersionNumber, note.IsCurrent, note.Status.ToString(),
        note.Subjective, note.Objective, note.Assessment, note.Plan,
        note.Origin.ToString(), note.SignedAtUtc, note.SignedBy, note.AmendmentReason,
        note.VerifyIntegrity());
}

public sealed record CreateNoteRequest(
    Guid AppointmentPublicId, string? Subjective, string? Objective,
    string? Assessment, string? Plan);

public sealed record UpdateNoteRequest(
    string? Subjective, string? Objective, string? Assessment, string? Plan);

public sealed record AmendNoteRequest(string Reason);
