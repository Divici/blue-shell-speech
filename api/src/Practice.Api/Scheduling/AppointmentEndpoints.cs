using Microsoft.EntityFrameworkCore;
using Practice.Application.Providers;
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

        var practiceZone = PracticeTimeZone();
        var localMidnight = date.ToDateTime(TimeOnly.MinValue);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified), practiceZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(localMidnight.AddDays(1), DateTimeKind.Unspecified), practiceZone);

        var visits = await db.Appointments
            .AsNoTracking()
            .Where(a => a.StartUtc >= startUtc && a.StartUtc < endUtc)
            .OrderBy(a => a.StartUtc)
            .Join(db.Patients, a => a.PatientId, p => p.Id, (a, p) => new { a, p })
            .Select(x => new DayVisit(
                x.a.PublicId,
                x.p.PublicId,
                x.p.FirstName,
                x.p.LastName,
                x.a.AppointmentType.ToString(),
                x.a.StartUtc,
                x.a.DurationMinutes,
                x.a.Status.ToString(),
                x.a.TravelBlockMinutes,
                x.a.Mileage,
                x.a.Notes))
            .ToListAsync(ct);

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

    /// <summary>
    /// America/New_York, resolved cross-platform.
    ///
    /// Windows and Linux disagree on time-zone ids ("Eastern Standard Time" vs
    /// "America/New_York"). .NET 8+ accepts IANA ids on Windows too, but the fallback is
    /// kept so a container on either platform resolves the same zone rather than throwing
    /// at runtime on one of them.
    /// </summary>
    private static TimeZoneInfo PracticeTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }
}

public sealed record AppointmentSummary(
    Guid PublicId, Guid PatientPublicId, string PatientFirstName, string PatientLastName,
    string AppointmentType, DateTime StartUtc, short DurationMinutes, string Status,
    short? TravelBlockMinutes, decimal? Mileage);

public sealed record DayVisit(
    Guid PublicId, Guid PatientPublicId, string PatientFirstName, string PatientLastName,
    string AppointmentType, DateTime StartUtc, short DurationMinutes, string Status,
    short? TravelBlockMinutes, decimal? Mileage, string? Notes);

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
