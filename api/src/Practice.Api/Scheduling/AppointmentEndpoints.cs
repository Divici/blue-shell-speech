using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
using Practice.Domain.Common;
using Practice.Domain.Scheduling;
using Practice.Infrastructure.Persistence;

namespace Practice.Api.Scheduling;

/// <summary>
/// Scheduling.
///
/// Same two rules as the patient endpoints: every query is provider-scoped by the global
/// filter, and anything belonging to another provider returns 404 rather than 403.
/// </summary>
public static class AppointmentEndpoints
{
    public static IEndpointRouteBuilder MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/appointments").WithTags("Scheduling");

        group.MapGet("/", ListAppointments);
        group.MapGet("/day/{date}", GetDay);
        group.MapPost("/", ScheduleAppointment);
        group.MapPost("/{publicId:guid}/complete", CompleteAppointment);
        group.MapPost("/{publicId:guid}/cancel", CancelAppointment);
        group.MapPost("/{publicId:guid}/no-show", MarkNoShow);
        group.MapPost("/{publicId:guid}/reschedule", RescheduleAppointment);

        return app;
    }

    private static async Task<IResult> ListAppointments(
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct,
        DateTime? fromUtc = null,
        DateTime? toUtc = null)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var from = fromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = toUtc ?? DateTime.UtcNow.AddDays(30);

        var appointments = await db.Appointments
            .AsNoTracking()
            .Where(a => a.StartUtc >= from && a.StartUtc < to)
            .OrderBy(a => a.StartUtc)
            .Join(db.Patients, a => a.PatientId, p => p.Id, (a, p) => new AppointmentSummary(
                a.PublicId, p.PublicId, p.FirstName, p.LastName,
                a.AppointmentType.ToString(), a.StartUtc, a.DurationMinutes,
                a.Status.ToString(), a.TravelBlockMinutes, a.Mileage))
            .ToListAsync(ct);

        return Results.Ok(appointments);
    }

    /// <summary>
    /// The daily visit view (presearch §5.6).
    ///
    /// Takes a DATE, and interprets it in America/New_York — because "today" for a
    /// clinician in Maryland is a local day, and a UTC day boundary would drop the
    /// evening's last visit into tomorrow between 20:00 and midnight.
    /// </summary>
    private static async Task<IResult> GetDay(
        DateOnly date,
        PracticeDbContext db,
        IProviderContext provider,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var practiceZone = PracticeTime.Zone;
        var localMidnight = date.ToDateTime(TimeOnly.MinValue);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), practiceZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight.AddDays(1), DateTimeKind.Unspecified), practiceZone);

        /*
         * The current note is resolved in the SAME query, as a correlated subquery.
         *
         * SQL Server compiles this to one OUTER APPLY, so the whole day still costs a
         * single round trip. The alternative — the day view rendering, then each card
         * asking "does this visit have a note?" — is a request per visit through the BFF
         * to a container that scales to zero. On a phone between houses that is the
         * difference between a page and a spinner.
         *
         * IsCurrent, not "the latest": after an amendment the day view must point at the
         * version the clinician stands behind, not the one it superseded.
         */
        var rows = await db.Appointments
            .AsNoTracking()
            .Where(a => a.StartUtc >= startUtc && a.StartUtc < endUtc)
            .OrderBy(a => a.StartUtc)
            .Join(db.Patients, a => a.PatientId, p => p.Id, (a, p) => new { a, p })
            // Columns, not entities. Selecting `x.a` and `x.p` whole would drag every
            // patient column across the wire for a schedule — ClinicalSummary included,
            // which is free-text PHI this screen never shows.
            .Select(x => new
            {
                VisitPublicId = x.a.PublicId,
                PatientPublicId = x.p.PublicId,
                x.p.FirstName,
                x.p.LastName,
                x.a.AppointmentType,
                x.a.StartUtc,
                x.a.DurationMinutes,
                VisitStatus = x.a.Status,
                x.a.TravelBlockMinutes,
                x.a.Mileage,
                x.a.Notes,
                Note = db.ClinicalNotes
                    .Where(n => n.AppointmentId == x.a.Id && n.IsCurrent)
                    .Select(n => new { n.PublicId, n.Status })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var visits = rows.Select(x => new DayVisit(
            x.VisitPublicId,
            x.PatientPublicId,
            x.FirstName,
            x.LastName,
            x.AppointmentType.ToString(),
            x.StartUtc,
            x.DurationMinutes,
            x.VisitStatus.ToString(),
            x.TravelBlockMinutes,
            x.Mileage,
            x.Notes,
            // Null means "not documented yet", and must stay distinguishable from a note
            // whose id failed to load. Guid.Empty would read as a real, broken link.
            x.Note?.PublicId,
            x.Note?.Status.ToString())).ToList();

        var totalMileage = visits.Sum(v => v.Mileage ?? 0m);

        return Results.Ok(new DaySchedule(date, visits, totalMileage));
    }

    private static async Task<IResult> ScheduleAppointment(
        ScheduleAppointmentRequest request,
        PracticeDbContext db,
        IProviderContext provider,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        // Resolved through the filter, so a patient belonging to someone else is simply
        // not found — the same answer as a patient that does not exist.
        var patient = await db.Patients
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.PublicId == request.PatientPublicId, ct);

        if (patient is null) return Results.NotFound();

        Appointment appointment;
        try
        {
            appointment = Appointment.Schedule(
                provider.ProviderId.Value,
                patient.Id,
                request.AppointmentType,
                DateTime.SpecifyKind(request.StartUtc, DateTimeKind.Utc),
                request.DurationMinutes,
                travelBlockMinutes: request.TravelBlockMinutes,
                notes: request.Notes);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        /*
         * Conflict detection, including travel time.
         *
         * Loaded as a narrow window rather than the whole calendar: only appointments that
         * could possibly overlap are candidates, and the domain decides.
         */
        var windowStart = appointment.StartUtc.AddHours(-8);
        var windowEnd = appointment.EndUtc.AddHours(8);

        var neighbours = await db.Appointments
            .Where(a => a.StartUtc >= windowStart && a.StartUtc <= windowEnd)
            .ToListAsync(ct);

        var conflict = neighbours.Find(appointment.ConflictsWith);
        if (conflict is not null)
        {
            return Results.Conflict(new
            {
                message = "That overlaps another visit, once travel time is counted.",
                conflictingAppointment = conflict.PublicId,
                conflictingStartUtc = conflict.StartUtc,
            });
        }

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/appointments/{appointment.PublicId}",
            new { appointment.PublicId, appointment.StartUtc, appointment.DurationMinutes });
    }

    private static Task<IResult> CompleteAppointment(
        Guid publicId, CompleteAppointmentRequest? request,
        PracticeDbContext db, IProviderContext provider, CancellationToken ct) =>
        Transition(publicId, db, provider, a => a.Complete(request?.Mileage), ct);

    private static Task<IResult> CancelAppointment(
        Guid publicId, CancelAppointmentRequest? request,
        PracticeDbContext db, IProviderContext provider, CancellationToken ct) =>
        Transition(publicId, db, provider, a => a.Cancel(request?.Reason), ct);

    private static Task<IResult> MarkNoShow(
        Guid publicId, PracticeDbContext db, IProviderContext provider, CancellationToken ct) =>
        Transition(publicId, db, provider, a => a.MarkNoShow(), ct);

    private static Task<IResult> RescheduleAppointment(
        Guid publicId, RescheduleRequest request,
        PracticeDbContext db, IProviderContext provider, CancellationToken ct) =>
        Transition(publicId, db, provider, a => a.Reschedule(
            DateTime.SpecifyKind(request.StartUtc, DateTimeKind.Utc), request.DurationMinutes), ct);

    /// <summary>
    /// Shared transition handling.
    ///
    /// Domain rules throw InvalidOperationException for an illegal transition — completing
    /// a cancelled visit, moving one that already happened. Those become 409 Conflict with
    /// the domain's own message, which is written for a clinician to read.
    /// </summary>
    private static async Task<IResult> Transition(
        Guid publicId,
        PracticeDbContext db,
        IProviderContext provider,
        Action<Appointment> transition,
        CancellationToken ct)
    {
        if (provider.ProviderId is null) return Results.Unauthorized();

        var appointment = await db.Appointments
            .SingleOrDefaultAsync(a => a.PublicId == publicId, ct);

        if (appointment is null) return Results.NotFound();

        try
        {
            transition(appointment);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            appointment.PublicId,
            Status = appointment.Status.ToString(),
            appointment.StartUtc,
            appointment.DurationMinutes,
            appointment.Mileage,
        });
    }

}

public sealed record AppointmentSummary(
    Guid PublicId, Guid PatientPublicId, string PatientFirstName, string PatientLastName,
    string AppointmentType, DateTime StartUtc, short DurationMinutes, string Status,
    short? TravelBlockMinutes, decimal? Mileage);

/// <summary>
/// A visit on the daily view, including whether it has been documented.
///
/// <see cref="NotePublicId"/> and <see cref="NoteStatus"/> describe the CURRENT clinical
/// note for this visit, or null if none exists yet. They are carried here so the day view
/// can offer "open the note" or "start one" per visit from a single request, rather than
/// one lookup per card.
/// </summary>
public sealed record DayVisit(
    Guid PublicId, Guid PatientPublicId, string PatientFirstName, string PatientLastName,
    string AppointmentType, DateTime StartUtc, short DurationMinutes, string Status,
    short? TravelBlockMinutes, decimal? Mileage, string? Notes,
    Guid? NotePublicId, string? NoteStatus);

public sealed record DaySchedule(DateOnly Date, IReadOnlyList<DayVisit> Visits, decimal TotalMileage);

public sealed record ScheduleAppointmentRequest(
    Guid PatientPublicId,
    AppointmentType AppointmentType,
    DateTime StartUtc,
    short DurationMinutes,
    short? TravelBlockMinutes,
    string? Notes);

public sealed record CompleteAppointmentRequest(decimal? Mileage);

public sealed record CancelAppointmentRequest(string? Reason);

public sealed record RescheduleRequest(DateTime StartUtc, short DurationMinutes);
