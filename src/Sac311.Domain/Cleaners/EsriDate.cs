namespace Sac311.Domain.Cleaners;

public static class EsriDate
{
    /// <summary>Anything earlier is a placeholder (the layer's field default is 1899-12-30).</summary>
    public static readonly DateTime MinValidUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far past "now" a date may be before it counts as <see cref="DqFlags.FutureDate"/>.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromDays(1);

    /// <summary>
    /// Converts epoch milliseconds to a UTC <see cref="DateTime"/>. Dates before 2000 (or out of range) become
    /// null with <see cref="DqFlags.SentinelDate"/>; dates more than a day after <paramref name="nowUtc"/> are kept
    /// but get <see cref="DqFlags.FutureDate"/>.
    /// </summary>
    public static Cleaned<DateTime?> ToUtc(long? epochMilliseconds, DateTime nowUtc)
    {
        if (epochMilliseconds is not { } ms)
        {
            return new(null, DqFlags.None);
        }

        DateTime utc;
        try
        {
            utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return new(null, DqFlags.SentinelDate);
        }

        if (utc < MinValidUtc)
        {
            return new(null, DqFlags.SentinelDate);
        }

        return new(utc, utc > nowUtc + FutureTolerance ? DqFlags.FutureDate : DqFlags.None);
    }
}
