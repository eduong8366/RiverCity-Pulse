namespace Sac311.Domain.Cleaners;

/// <summary>Sacramento local time. Daily buckets (created/closed dates, backlog) use the local calendar day.</summary>
public static class Pacific
{
    public static readonly TimeZoneInfo Zone = FindZone();

    public static DateTime ToLocal(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("Expected a UTC (or unspecified) DateTime.", nameof(utc));
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
    }

    /// <summary>The local calendar date of a UTC instant. DST-safe: the conversion goes through the zone rules.</summary>
    public static DateOnly ToLocalDate(DateTime utc) => DateOnly.FromDateTime(ToLocal(utc));

    public static DateOnly? ToLocalDate(DateTime? utc) => utc is { } u ? ToLocalDate(u) : null;

    // IANA id works on Linux and on Windows with ICU; the Windows id is the fallback.
    private static TimeZoneInfo FindZone() =>
        TimeZoneInfo.TryFindSystemTimeZoneById("America/Los_Angeles", out var tz) ? tz
            : TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
}
