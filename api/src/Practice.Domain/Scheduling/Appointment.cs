using Practice.Domain.Common;

namespace Practice.Domain.Scheduling;

/// <summary>
/// A scheduled visit.
///
/// Stored as a UTC start plus a DURATION, never a start and an end.
///
/// That is deliberate. An in-home practice schedules across DST boundaries every spring
/// and autumn, and "9:00 to 10:00 local" is not one hour on the day the clocks change. A
/// duration is unambiguous; a stored end time is a bug waiting for March.
/// </summary>
public sealed class Appointment : Entity
{
    private Appointment() { }

    public long ProviderId { get; private set; }

    public long PatientId { get; private set; }

    /// <summary>Where the session happens. Null for a phone consultation.</summary>
    public long? AddressId { get; private set; }

    public AppointmentType AppointmentType { get; private set; }

    /// <summary>UTC. Rendered America/New_York; never stored that way.</summary>
    public DateTime StartUtc { get; private set; }

    public short DurationMinutes { get; private set; }

    public DateTime EndUtc => StartUtc.AddMinutes(DurationMinutes);

    public AppointmentStatus Status { get; private set; } = AppointmentStatus.Scheduled;

    /// <summary>Travel time to allow before this visit (presearch §5.6).</summary>
    public short? TravelBlockMinutes { get; private set; }

    /// <summary>Miles driven. Recorded for the practice's own mileage deduction.</summary>
    public decimal? Mileage { get; private set; }

    public string? Notes { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Longest visit the practice books. Anything more is a data-entry error.</summary>
    public const int MaxDurationMinutes = 240;

    public static Appointment Schedule(
        long providerId,
        long patientId,
        AppointmentType type,
        DateTime startUtc,
        short durationMinutes,
        long? addressId = null,
        short? travelBlockMinutes = null,
        string? notes = null)
    {
        if (providerId <= 0)
        {
            throw new ArgumentException("An appointment needs a provider.", nameof(providerId));
        }

        if (patientId <= 0)
        {
            throw new ArgumentException("An appointment needs a patient.", nameof(patientId));
        }

        /*
         * UTC or nothing.
         *
         * A DateTime with Kind=Unspecified reaching this constructor means someone parsed
         * a local time and lost the offset. Accepting it would store a time that is wrong
         * by however many hours the writer's machine is from UTC — and be undetectable
         * afterwards.
         */
        if (startUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "Appointment times must be UTC. Convert before calling.", nameof(startUtc));
        }

        if (durationMinutes <= 0)
        {
            throw new ArgumentException(
                "An appointment must last at least a minute.", nameof(durationMinutes));
        }

        if (durationMinutes > MaxDurationMinutes)
        {
            throw new ArgumentException(
                $"An appointment cannot be longer than {MaxDurationMinutes / 60} hours.",
                nameof(durationMinutes));
        }

        if (travelBlockMinutes is < 0)
        {
            throw new ArgumentException(
                "Travel time cannot be negative.", nameof(travelBlockMinutes));
        }

        return new Appointment
        {
            ProviderId = providerId,
            PatientId = patientId,
            AppointmentType = type,
            StartUtc = startUtc,
            DurationMinutes = durationMinutes,
            AddressId = addressId,
            TravelBlockMinutes = travelBlockMinutes,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
    }

    /// <summary>
    /// Does this appointment overlap another, counting travel time?
    ///
    /// Travel is part of the conflict: two visits twenty minutes apart on opposite sides
    /// of the county do not overlap on the calendar and cannot both happen. The travel
    /// block is treated as occupied time before the visit starts.
    /// </summary>
    public bool ConflictsWith(Appointment other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow) return false;
        if (other.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow) return false;

        var thisStart = StartUtc.AddMinutes(-(TravelBlockMinutes ?? 0));
        var otherStart = other.StartUtc.AddMinutes(-(other.TravelBlockMinutes ?? 0));

        // Touching is not overlapping: a visit ending at 10:00 and one starting at 10:00
        // are back to back, which is a real thing people schedule.
        return thisStart < other.EndUtc && otherStart < EndUtc;
    }

    public void Reschedule(DateTime startUtc, short durationMinutes)
    {
        if (Status != AppointmentStatus.Scheduled)
        {
            throw new InvalidOperationException(
                "Only a scheduled appointment can be moved. Completed visits are a record of what happened.");
        }

        if (startUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Appointment times must be UTC.", nameof(startUtc));
        }

        if (durationMinutes <= 0 || durationMinutes > MaxDurationMinutes)
        {
            throw new ArgumentException("Invalid duration.", nameof(durationMinutes));
        }

        StartUtc = startUtc;
        DurationMinutes = durationMinutes;
    }

    /// <summary>
    /// Marks the visit as happened.
    ///
    /// This is what a clinical note attaches to, so it cannot be undone by rescheduling —
    /// see Reschedule.
    /// </summary>
    public void Complete(decimal? mileage = null)
    {
        if (Status == AppointmentStatus.Cancelled)
        {
            throw new InvalidOperationException("A cancelled appointment did not happen.");
        }

        if (mileage is < 0)
        {
            throw new ArgumentException("Mileage cannot be negative.", nameof(mileage));
        }

        Status = AppointmentStatus.Completed;
        Mileage = mileage ?? Mileage;
    }

    public void Cancel(string? reason)
    {
        if (Status == AppointmentStatus.Completed)
        {
            throw new InvalidOperationException(
                "A completed visit cannot be cancelled — it already happened.");
        }

        Status = AppointmentStatus.Cancelled;
        CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    /// <summary>
    /// The family was not there.
    ///
    /// Distinct from Cancelled because it is clinically and commercially different: a
    /// no-show is a pattern worth seeing, a cancellation is a rescheduling.
    /// </summary>
    public void MarkNoShow()
    {
        if (Status == AppointmentStatus.Completed)
        {
            throw new InvalidOperationException("A completed visit cannot be a no-show.");
        }

        Status = AppointmentStatus.NoShow;
    }

    public void RecordMileage(decimal miles)
    {
        if (miles < 0)
        {
            throw new ArgumentException("Mileage cannot be negative.", nameof(miles));
        }

        Mileage = miles;
    }

    public void UpdateNotes(string? notes) =>
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
}

public enum AppointmentType
{
    Therapy = 1,

    /// <summary>Ships now; formal report authoring is sequenced later.</summary>
    Evaluation = 2,

    /// <summary>The free first conversation with a prospective family.</summary>
    Consultation = 3,

    Reassessment = 4,
}

public enum AppointmentStatus
{
    Scheduled = 1,
    Completed = 2,
    Cancelled = 3,
    NoShow = 4,
}
