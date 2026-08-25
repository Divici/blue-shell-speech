using Practice.Domain.Scheduling;

namespace Practice.Domain.Tests;

/// <summary>
/// Scheduling invariants.
///
/// The DST cases matter more than they look: this practice books in-home visits across
/// two clock changes a year, and a scheduling bug means a clinician driving to a house
/// where nobody is expecting her.
/// </summary>
public sealed class AppointmentTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static Appointment Schedule(
        DateTime? start = null,
        short duration = 60,
        short? travel = null) =>
        Appointment.Schedule(
            providerId: 1,
            patientId: 2,
            type: AppointmentType.Therapy,
            startUtc: start ?? Utc(2026, 9, 1, 14, 0),
            durationMinutes: duration,
            travelBlockMinutes: travel);

    [Fact]
    public void Schedule_creates_a_scheduled_appointment()
    {
        var appointment = Schedule();

        Assert.Equal(AppointmentStatus.Scheduled, appointment.Status);
        Assert.NotEqual(Guid.Empty, appointment.PublicId);
    }

    [Fact]
    public void Schedule_requires_a_provider_and_a_patient()
    {
        Assert.Throws<ArgumentException>(() =>
            Appointment.Schedule(0, 2, AppointmentType.Therapy, Utc(2026, 9, 1, 14, 0), 60));
        Assert.Throws<ArgumentException>(() =>
            Appointment.Schedule(1, 0, AppointmentType.Therapy, Utc(2026, 9, 1, 14, 0), 60));
    }

    /// <summary>
    /// A DateTime with Kind=Unspecified means someone parsed a local time and lost the
    /// offset. Storing it would be silently wrong by the writer's UTC offset, and
    /// undetectable afterwards.
    /// </summary>
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Schedule_rejects_a_non_utc_start(DateTimeKind kind)
    {
        var start = DateTime.SpecifyKind(new DateTime(2026, 9, 1, 14, 0, 0), kind);

        var ex = Assert.Throws<ArgumentException>(() =>
            Appointment.Schedule(1, 2, AppointmentType.Therapy, start, 60));

        Assert.Contains("UTC", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void Schedule_rejects_a_non_positive_duration(short duration)
    {
        Assert.Throws<ArgumentException>(() => Schedule(duration: duration));
    }

    [Fact]
    public void Schedule_rejects_an_implausibly_long_visit()
    {
        Assert.Throws<ArgumentException>(() =>
            Schedule(duration: Appointment.MaxDurationMinutes + 1));
    }

    // ------------------------------------------------------------------- DST

    /// <summary>
    /// The reason duration is stored instead of an end time.
    ///
    /// 2026-11-01 is the US autumn clock change. A visit starting 05:30 UTC (01:30 EDT)
    /// and lasting an hour ends at 06:30 UTC — which is 01:30 EST, the same wall-clock
    /// time it started. Storing "ends at 02:30 local" would be wrong; a duration is not.
    /// </summary>
    [Fact]
    public void Duration_is_unambiguous_across_the_autumn_clock_change()
    {
        var appointment = Schedule(start: Utc(2026, 11, 1, 5, 30), duration: 60);

        Assert.Equal(Utc(2026, 11, 1, 6, 30), appointment.EndUtc);
        Assert.Equal(60, appointment.DurationMinutes);
    }

    [Fact]
    public void Duration_is_unambiguous_across_the_spring_clock_change()
    {
        // 2026-03-08, 06:30 UTC = 01:30 EST; the clocks jump at 07:00 UTC.
        var appointment = Schedule(start: Utc(2026, 3, 8, 6, 30), duration: 60);

        Assert.Equal(Utc(2026, 3, 8, 7, 30), appointment.EndUtc);
    }

    // -------------------------------------------------------------- conflicts

    [Fact]
    public void Overlapping_appointments_conflict()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 14, 30), duration: 60);

        Assert.True(first.ConflictsWith(second));
        Assert.True(second.ConflictsWith(first));
    }

    /// <summary>Back-to-back is not a conflict — people schedule that deliberately.</summary>
    [Fact]
    public void Touching_appointments_do_not_conflict()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 15, 0), duration: 60);

        Assert.False(first.ConflictsWith(second));
    }

    /// <summary>
    /// Travel is part of the conflict.
    ///
    /// Two visits thirty minutes apart on opposite sides of the county do not overlap on
    /// a calendar and cannot both happen. This is the difference between a scheduling
    /// tool and a diary.
    /// </summary>
    [Fact]
    public void Travel_time_creates_a_conflict_that_the_calendar_alone_would_miss()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 15, 15), duration: 60, travel: 30);

        // 15:15 minus 30 minutes travel = 14:45, which is inside the first visit.
        Assert.True(second.ConflictsWith(first));
        Assert.True(first.ConflictsWith(second));
    }

    [Fact]
    public void Enough_travel_time_removes_the_conflict()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 15, 30), duration: 60, travel: 30);

        Assert.False(second.ConflictsWith(first));
    }

    /// <summary>A cancelled visit frees its slot — that is the point of cancelling.</summary>
    [Fact]
    public void A_cancelled_appointment_does_not_conflict()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 14, 30), duration: 60);
        first.Cancel("Family unwell");

        Assert.False(first.ConflictsWith(second));
        Assert.False(second.ConflictsWith(first));
    }

    [Fact]
    public void A_no_show_does_not_conflict()
    {
        var first = Schedule(start: Utc(2026, 9, 1, 14, 0), duration: 60);
        var second = Schedule(start: Utc(2026, 9, 1, 14, 30), duration: 60);
        first.MarkNoShow();

        Assert.False(second.ConflictsWith(first));
    }

    // ------------------------------------------------------------ transitions

    [Fact]
    public void Complete_records_the_visit_and_its_mileage()
    {
        var appointment = Schedule();

        appointment.Complete(mileage: 12.4m);

        Assert.Equal(AppointmentStatus.Completed, appointment.Status);
        Assert.Equal(12.4m, appointment.Mileage);
    }

    /// <summary>
    /// A completed visit is a record of what happened. Moving it would rewrite history —
    /// and a clinical note is attached to it.
    /// </summary>
    [Fact]
    public void A_completed_appointment_cannot_be_rescheduled()
    {
        var appointment = Schedule();
        appointment.Complete();

        Assert.Throws<InvalidOperationException>(() =>
            appointment.Reschedule(Utc(2026, 9, 2, 14, 0), 60));
    }

    [Fact]
    public void A_completed_appointment_cannot_be_cancelled()
    {
        var appointment = Schedule();
        appointment.Complete();

        Assert.Throws<InvalidOperationException>(() => appointment.Cancel("changed my mind"));
    }

    [Fact]
    public void A_completed_appointment_cannot_become_a_no_show()
    {
        var appointment = Schedule();
        appointment.Complete();

        Assert.Throws<InvalidOperationException>(appointment.MarkNoShow);
    }

    [Fact]
    public void A_cancelled_appointment_cannot_be_completed()
    {
        var appointment = Schedule();
        appointment.Cancel("Family unwell");

        Assert.Throws<InvalidOperationException>(() => appointment.Complete());
    }

    [Fact]
    public void Rescheduling_moves_a_scheduled_appointment()
    {
        var appointment = Schedule();

        appointment.Reschedule(Utc(2026, 9, 2, 15, 0), 45);

        Assert.Equal(Utc(2026, 9, 2, 15, 0), appointment.StartUtc);
        Assert.Equal(45, appointment.DurationMinutes);
    }

    [Fact]
    public void Mileage_cannot_be_negative()
    {
        var appointment = Schedule();

        Assert.Throws<ArgumentException>(() => appointment.RecordMileage(-1m));
    }

    [Fact]
    public void Cancelling_records_the_reason()
    {
        var appointment = Schedule();

        appointment.Cancel("  Family unwell  ");

        Assert.Equal("Family unwell", appointment.CancellationReason);
    }

    // ------------------------------------------------- documenting a visit

    /*
     * A note documents what happened.
     *
     * A cancelled visit and a no-show did not happen; a visit that has not started has
     * produced nothing to record. Offering the note entry point on any of the three turns
     * one mis-tap into a draft on a child's chart that cannot be signed and, before the
     * discard path existed, could not be removed either.
     */

    [Fact]
    public void A_cancelled_visit_cannot_be_documented()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));
        appointment.Cancel("Family unwell");

        var reason = appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 16, 0));

        Assert.NotNull(reason);
        Assert.Contains("cancelled", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_no_show_cannot_be_documented()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));
        appointment.MarkNoShow();

        var reason = appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 16, 0));

        Assert.NotNull(reason);
        Assert.Contains("no-show", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_visit_that_has_not_started_cannot_be_documented()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));

        var reason = appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 13, 59));

        Assert.NotNull(reason);
        Assert.Contains("not started", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Documentation opens when the session does, not when it ends — an in-home visit is
    /// typed up on a tablet while it is happening, not afterwards in a car.
    /// </summary>
    [Fact]
    public void A_visit_in_progress_can_be_documented()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));

        Assert.Null(appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 14, 0)));
        Assert.Null(appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 14, 30)));
    }

    /// <summary>
    /// A visit left as Scheduled because nobody pressed "complete" is still a visit that
    /// happened. Notes must not depend on a status transition the clinician may never make.
    /// </summary>
    [Fact]
    public void A_past_visit_still_marked_scheduled_can_be_documented()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));

        Assert.Null(appointment.DocumentationBlockedReason(Utc(2026, 9, 8, 9, 0)));
    }

    /// <summary>
    /// A visit marked complete happened, whatever the clock says.
    ///
    /// Without this, a session closed a couple of minutes early — or closed on a device
    /// whose clock runs slow — would refuse its own note for the rest of the hour.
    /// </summary>
    [Fact]
    public void A_completed_visit_can_be_documented_even_before_its_start_time()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));
        appointment.Complete();

        Assert.Null(appointment.DocumentationBlockedReason(Utc(2026, 9, 1, 13, 55)));
    }

    /// <summary>
    /// The same rule as every other time argument in this aggregate: a Kind of Unspecified
    /// means someone lost an offset, and comparing against it would answer confidently and
    /// wrongly.
    /// </summary>
    [Fact]
    public void Documentation_requires_a_utc_clock_reading()
    {
        var appointment = Schedule(start: Utc(2026, 9, 1, 14, 0));

        Assert.Throws<ArgumentException>(() =>
            appointment.DocumentationBlockedReason(new DateTime(2026, 9, 1, 16, 0, 0)));
    }
}
