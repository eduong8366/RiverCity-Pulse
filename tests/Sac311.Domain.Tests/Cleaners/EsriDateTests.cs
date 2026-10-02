using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class EsriDateTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Real_dates_convert_from_epoch_milliseconds_to_utc()
    {
        var row = Fixture.Real("typical")[0];
        var created = EsriDate.ToUtc(row.DateCreated, Now);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 2, 0, DateTimeKind.Utc), created.Value);
        Assert.Equal(DateTimeKind.Utc, created.Value!.Value.Kind);
        Assert.Equal(DqFlags.None, created.Flags);
    }

    [Fact]
    public void Synthetic_1899_sentinel_becomes_null_with_a_flag()
    {
        var row = Fixture.Synthetic("sentinel-date").Single();
        Assert.Equal(new Cleaned<DateTime?>(null, DqFlags.SentinelDate), EsriDate.ToUtc(row.DateCreated, Now));
        Assert.Equal(new Cleaned<DateTime?>(null, DqFlags.SentinelDate), EsriDate.ToUtc(row.DateClosed, Now));
    }

    [Fact]
    public void Null_stays_null_without_a_flag() =>
        Assert.Equal(new Cleaned<DateTime?>(null, DqFlags.None), EsriDate.ToUtc(null, Now));

    [Fact]
    public void Out_of_range_milliseconds_are_treated_as_sentinels() =>
        Assert.Equal(DqFlags.SentinelDate, EsriDate.ToUtc(long.MinValue, Now).Flags);

    [Fact]
    public void Dates_more_than_a_day_ahead_are_kept_and_flagged()
    {
        var withinTolerance = new DateTimeOffset(Now.AddHours(23)).ToUnixTimeMilliseconds();
        var future = new DateTimeOffset(Now.AddDays(2)).ToUnixTimeMilliseconds();

        Assert.Equal(DqFlags.None, EsriDate.ToUtc(withinTolerance, Now).Flags);
        var result = EsriDate.ToUtc(future, Now);
        Assert.Equal(Now.AddDays(2), result.Value);
        Assert.Equal(DqFlags.FutureDate, result.Flags);
    }
}
