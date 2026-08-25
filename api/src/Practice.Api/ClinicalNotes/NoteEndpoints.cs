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
            entityType: nameof(ClinicalNote), entityPublicId: note.PublicId));

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
            metadata: $"versions={versions.Count}"));

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

        if (note is null)
        {
            /*
             * A DELETE that finds nothing is still an attempt to delete something.
             *
             * This is the row that answers "did somebody walk the note ids with DELETE".
             * The response stays byte-identical to every other 404 — a note belonging to
             * another provider and one that never existed must remain indistinguishable
             * (D052) — so the audit table is the only place the attempt can be recorded
             * at all. The id written down is one the caller already held.
             */
            await AuditRefusedDiscardAsync(audit, provider, publicId, "not-found");
            return Results.NotFound();
        }

        /*
         * A CHEAP FIRST PASS, so a refusal never opens a transaction.
         *
         * The answer that counts is the one taken inside the write below, against the row
         * actually being deleted. This one exists so the ordinary refusals — a signed
         * note, an amendment, a draft with content — cost one indexed read rather than a
         * transaction and a connection held across it. Same reasoning as the throwaway
         * validation in SubmitConsultationRequest: the cheapest way to hold a connection
         * open against a scale-to-zero container should not be to ask for something the
         * API was always going to refuse.
         */
        var refusal = RefusalToDiscard(note);

        if (refusal is not null)
        {
            await AuditRefusedDiscardAsync(audit, provider, publicId, refusal.Value.Reason);
            return Results.Conflict(new { message = refusal.Value.Message });
        }

        /*
         * THE DELETE AND ITS AUDIT ROW COMMIT TOGETHER, OR NEITHER DOES.
         *
         * They used to be two SaveChangesAsync calls on the request's cancellation token.
         * Backgrounding the PWA mid-request — walking out of a house, taking a call —
         * cancelled the second one, and the row was already gone with nothing in
         * AuditEvents naming who removed it. An audit trail with a survivorship bias
         * toward requests that were not interrupted is not an audit trail.
         *
         * This is the FIRST explicit transaction in the API, and it is here rather than
         * everywhere because this is the only place a row leaves a clinical table. The
         * other multi-write endpoint that needs atomicity — AmendNote — gets it from EF
         * putting one SaveChanges in one implicit transaction. That does not work here:
         * IAuditWriter owns its own save, deliberately, so that an audit row is never
         * silently batched in with whatever else the caller happened to be tracking.
         *
         * WriteAtomicallyAsync rather than an inline BeginTransactionAsync, because the
         * retry boundary, the change-tracker reset, the retry token and the commit token
         * are four separate ways to get this wrong and none of them shows up in a passing
         * run. The reasoning for each is on the helper; the contract it places on this
         * block is that the block RUNS MORE THAN ONCE and must own everything it writes —
         * including the decisions.
         */
        (string Reason, string Message)? lateRefusal = null;

        await db.WriteAtomicallyAsync(async attempt =>
        {
            // Reset per attempt: this is a conclusion, and a conclusion from a previous
            // attempt is exactly what the helper's contract says may not survive.
            lateRefusal = null;

            /*
             * RE-READ, rather than reusing the entity read above.
             *
             * The change tracker is cleared at the top of every attempt, so `note` is
             * detached in here — and it has to be. A previous attempt may have deleted
             * this row, had its audit save fail, and rolled the delete back; carrying its
             * tracked entities forward is exactly how one deletion produced two audit
             * rows in a table nothing can UPDATE or DELETE.
             */
            var doomed = await db.ClinicalNotes
                .SingleOrDefaultAsync(n => n.PublicId == publicId, attempt);

            /*
             * Already gone. The only route to this state is a second DELETE for the same
             * note committing while this one waited on its lock — a double tap, in other
             * words — and that request wrote the NoteDiscarded row for the removal. One
             * deletion, one row: writing a second here would say it happened twice.
             */
            if (doomed is null) return;

            /*
             * ASKED AGAIN, OF THE ROW BEING DELETED. This is the guard, not the pass above.
             *
             * The check above was about `note`, which the change-tracker reset detached
             * before this body ever ran. What gets removed is `doomed`, and the two are
             * the same record but not necessarily the same CONTENT: Michelle taps Discard
             * on a draft she has not written in, and the editor's autosave lands
             * PUT /notes/{id} with a child's session in it while this request is between
             * its two reads.
             *
             * Before D075's helper, EF's DELETE carried the RowVersion of the row that had
             * been checked, so a WHERE clause that no longer matched raised
             * DbUpdateConcurrencyException and nothing was destroyed. The re-read carries
             * the CURRENT RowVersion, so the DELETE matches — optimistic concurrency
             * stopped defending the check, because the check was no longer about the row
             * being deleted.
             *
             * What was left was TR_ClinicalNotes_PreventDeletingRealNotes, one layer of
             * the three D064 deliberately built, answering with a rolled-back transaction
             * and a 500 — and with nothing at all in AuditEvents, because the success row
             * is inside the rollback and the refusal helper is not on that path. The
             * clinician sees a failure with a trace id where the honest answer is "this
             * note has something in it now".
             */
            lateRefusal = RefusalToDiscard(doomed);

            if (lateRefusal is not null)
            {
                /*
                 * INSIDE the transaction, which then commits carrying only this row.
                 *
                 * The refusal is the whole of the write, so there is nothing for it to be
                 * atomic with — but it must not be rolled back either, and returning here
                 * is what leaves the transaction with one audit row to commit. The near
                 * miss is the interesting row: a clinical record was one statement away
                 * from being deleted.
                 */
                await AuditRefusedDiscardAsync(
                    audit, provider, publicId, lateRefusal.Value.Reason);
                return;
            }

            var version = doomed.VersionNumber;

            db.ClinicalNotes.Remove(doomed);
            await db.SaveChangesAsync(attempt);

            /*
             * Constructed HERE, on each attempt, and not hoisted above the lambda.
             *
             * Hoisting looks like the tidier fix and closes only half the hole. It stops
             * the double insert after a failed SAVE — the same instance is re-Added and
             * stays one Added entry — but a commit that fails after a successful save
             * leaves the row with its store-generated key already populated, and EF
             * inserts an explicit identity value the next time round rather than a new
             * row. The reset change tracker is the control; a fresh entity is what makes
             * the reset safe.
             */
            await audit.WriteAsync(AuditEvent.Record(
                AuditEventType.NoteDiscarded, AuditOutcome.Success,
                providerId: provider.ProviderId,
                entityType: nameof(ClinicalNote), entityPublicId: publicId,
                metadata: $"version={version}"));
        }, ct);

        // The same 409 the first pass would have returned, decided a moment later. A race
        // the clinician caused herself reads as a refusal she can act on, not as a fault.
        if (lateRefusal is not null)
        {
            return Results.Conflict(new { message = lateRefusal.Value.Message });
        }

        /*
         * A body, not 204.
         *
         * The BFF's request helper maps 404 to null, so a 204 would be indistinguishable
         * from "that note is not yours" and the UI would report a delete that never
         * happened. Same reason the goal transitions answer with one.
         */
        return Results.Ok(new { PublicId = publicId });
    }

    /// <summary>
    /// Why this row may not be discarded — the audit reason and the sentence the clinician
    /// reads — or null when it may.
    ///
    /// ONE PREDICATE, ASKED TWICE. The endpoint asks it before opening a transaction and
    /// again inside, against the row actually being removed. Two copies of these branches
    /// would be two copies to keep in step, and the branch that drifted would be the one
    /// standing between a DELETE and a child's clinical record.
    ///
    /// THE ORDER OF THE THREE REFUSALS IS THE ANSWER TO TWO SEPARATE QUESTIONS.
    ///
    /// Status first, then lineage, then content — because the audit vocabulary describes
    /// WHAT THE ROW IS and the sentence describes WHAT TO DO NEXT, and asking about
    /// lineage first got both wrong for the same note.
    ///
    ///   status  — signed or superseded. A signed amendment is a signed clinical record;
    ///             it used to audit as `amendment`, so a query for "attempts to delete a
    ///             signed record" was short by exactly the set of amended — i.e. contested
    ///             — records, and the copy asked a clinician to correct and sign a note
    ///             that was already signed.
    ///   lineage — a DRAFT that supersedes something: an amendment being written. This
    ///             still has to come before the content branch, because clearing an
    ///             amendment is an ordinary edit and leaves a Draft with four empty
    ///             sections — the content branch would then tell her the note "has
    ///             something written in it" and ask her to clear what is already clear.
    ///             The sequence D069 closed: sign v1, amend, empty, delete.
    ///   content — everything left: a plain draft somebody has written in.
    ///
    /// Written HERE as well as in the aggregate on the D064 principle that the rule exists
    /// in three places so no single loosening removes it.
    /// </summary>
    private static (string Reason, string Message)? RefusalToDiscard(ClinicalNote note)
    {
        if (note.Status != NoteStatus.Draft)
        {
            /*
             * The advice has to be one the API will accept.
             *
             * Amend() refuses a version that has already been superseded — the corrections
             * go on the current one — so telling a superseded v1 to "amend it instead"
             * walks a clinician straight into a second refusal, at which point the record
             * looks broken rather than the version looking wrong.
             */
            return ("signed", note.IsCurrent
                ? "This note is signed. A signed clinical record is never deleted — amend it instead."
                : "This version was signed and has since been replaced by a later one. It is kept exactly as it was, and never deleted — open the current version if something still needs correcting.");
        }

        if (note.SupersedesNoteId is not null)
        {
            return ("amendment",
                "This is an amendment to a signed note, so it is kept. Discarding it would leave the visit with no current note while the signed version stays on file — correct this one and sign it instead.");
        }

        if (!note.CanBeDiscarded)
        {
            // Everything else has been ruled out above, so this is a plain draft with
            // something written in it. "That is not allowed" would tell a clinician
            // nothing about which rule she met.
            return ("has-content",
                "This note has something written in it, so it is kept. Clear the sections and save if you meant to start again.");
        }

        return null;
    }

    /// <summary>
    /// Records a discard that was REFUSED.
    ///
    /// The log used to hold only the deletions that succeeded, which makes it useless for
    /// the question anyone actually brings to it: did someone try to remove records they
    /// were not allowed to remove. A refusal is the more interesting row of the two — a
    /// successful discard is an empty draft nobody will miss, and a refused one is an
    /// attempt on a clinical record.
    ///
    /// <paramref name="reason"/> is a fixed vocabulary — not-found, amendment, has-content,
    /// signed — so the table can be counted by reason rather than read as prose. It is
    /// deliberately NOT the sentence returned to the caller: that wording is written for a
    /// clinician and will be rewritten, and an audit row that changes shape when the copy
    /// changes cannot be queried across a year.
    ///
    /// Nothing here is clinical content. The note's own public id is a value the caller
    /// supplied, and "has-content" says a section was non-empty without saying what was in
    /// it (docs/SECURITY.md §Audit).
    /// </summary>
    private static Task AuditRefusedDiscardAsync(
        IAuditWriter audit,
        IProviderContext provider,
        Guid publicId,
        string reason) =>
        audit.WriteAsync(AuditEvent.Record(
            AuditEventType.NoteDiscarded, AuditOutcome.Failure,
            providerId: provider.ProviderId,
            entityType: nameof(ClinicalNote), entityPublicId: publicId,
            metadata: $"refused;reason={reason}"));

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
            metadata: $"version={note.VersionNumber}"));

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
            metadata: $"version={amendment.VersionNumber};supersedes={note.PublicId}"));

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

/// <summary>
/// A note as the UI sees it.
///
/// <see cref="IsAmendment"/> is a BOOLEAN, not the id it derives from. The screen needs to
/// know whether this row supersedes a signed version — it must not offer to discard one —
/// and SupersedesNoteId is a clustered key that never leaves the server (Entity). Deriving
/// it here means the client asks the same question the aggregate, the endpoint, and the
/// trigger ask, rather than inferring it from VersionNumber and hoping the two stay in
/// step (the precedent D062 set for the AAC fields).
/// </summary>
public sealed record NoteDto(
    Guid PublicId, int VersionNumber, bool IsCurrent, string Status,
    string Subjective, string Objective, string Assessment, string Plan,
    string Origin, DateTime? SignedAtUtc, string? SignedBy, string? AmendmentReason,
    bool IsAmendment, bool IntegrityVerified)
{
    public static NoteDto From(ClinicalNote note) => new(
        note.PublicId, note.VersionNumber, note.IsCurrent, note.Status.ToString(),
        note.Subjective, note.Objective, note.Assessment, note.Plan,
        note.Origin.ToString(), note.SignedAtUtc, note.SignedBy, note.AmendmentReason,
        note.SupersedesNoteId is not null,
        note.VerifyIntegrity());
}

public sealed record CreateNoteRequest(
    Guid AppointmentPublicId, string? Subjective, string? Objective,
    string? Assessment, string? Plan);

public sealed record UpdateNoteRequest(
    string? Subjective, string? Objective, string? Assessment, string? Plan);

public sealed record AmendNoteRequest(string Reason);
