namespace Practice.Domain.Common;

/// <summary>
/// The practice's wall clock.
///
/// Everything is STORED in UTC (docs/DATA_MODEL.md) and read by people in Maryland, so
/// every "which day was that" question has to be asked in America/New_York. Most of the
/// system never needs to: an appointment is an instant and the browser renders it. The
/// exception is anything that is genuinely a CALENDAR fact rather than a moment — a date
/// of service on a superbill, the boundaries of "today" on the daily view — where UTC
/// gives the wrong answer for every evening visit the practice runs.
///
/// This lives in the domain, and only depends on the BCL, because the rule it encodes is a
/// domain rule. It was previously a private helper inside AppointmentEndpoints; a second
/// copy in the billing aggregate would have been two places for the platform fallback
/// below to drift apart.
/// </summary>
public static class PracticeTime
{
    /// <summary>
    /// America/New_York, resolved cross-platform.
    ///
    /// Windows and Linux disagree on time-zone ids ("Eastern Standard Time" vs
    /// "America/New_York"). .NET 8+ accepts IANA ids on Windows too, but the fallback is
    /// kept so a container on either platform resolves the same zone rather than throwing
    /// at runtime on one of them.
    /// </summary>
    public static TimeZoneInfo Zone { get; } = Resolve();

    /// <summary>
    /// The calendar date a UTC instant falls on in the practice's own timezone.
    ///
    /// 2026-03-10T01:30Z is the evening of 9 March in Maryland. Taking the date off the
    /// UTC value instead would bill an ordinary 8pm session on the following day, on every
    /// superbill the practice ever issues, with nothing downstream able to notice.
    /// </summary>
    public static DateOnly LocalDateOf(DateTime instantUtc)
    {
        if (instantUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                "Only a UTC instant has a practice-local date. Convert before calling.",
                nameof(instantUtc));
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instantUtc, Zone));
    }

    private static TimeZoneInfo Resolve()
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
