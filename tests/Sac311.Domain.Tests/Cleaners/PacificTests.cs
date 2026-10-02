using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

// The live feed can't be queried for "a row on a DST boundary", so these instants are synthetic.
public class PacificTests
{
    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Real_first_row_of_2024_utc_is_new_years_eve_locally()
    {
        var row = Fixture.Real("typical")[0];
        var created = EsriDate.ToUtc(row.DateCreated, DateTime.UtcNow).Value!.Value;
        Assert.Equal(new DateOnly(2023, 12, 31), Pacific.ToLocalDate(created));
    }

    [Theory]
    [InlineData(2026, 1, 15, 7, 59, 2026, 1, 14)]  // PST, UTC-8
    [InlineData(2026, 1, 15, 8, 0, 2026, 1, 15)]
    [InlineData(2026, 7, 1, 6, 59, 2026, 6, 30)]   // PDT, UTC-7
    [InlineData(2026, 7, 1, 7, 0, 2026, 7, 1)]
    public void Local_midnight_moves_with_daylight_saving(int y, int mo, int d, int h, int mi, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), Pacific.ToLocalDate(Utc(y, mo, d, h, mi)));

    [Fact]
    public void Spring_forward_skips_two_am()
    {
        // 2026-03-08: 01:59 PST is followed by 03:00 PDT.
        Assert.Equal(new DateTime(2026, 3, 8, 1, 59, 0), Pacific.ToLocal(Utc(2026, 3, 8, 9, 59)));
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), Pacific.ToLocal(Utc(2026, 3, 8, 10, 0)));
    }

    [Fact]
    public void Fall_back_repeats_one_am_on_the_same_date()
    {
        // 2026-11-01: 01:59 PDT is followed by 01:00 PST; both are Nov 1.
        Assert.Equal(new DateTime(2026, 11, 1, 1, 59, 0), Pacific.ToLocal(Utc(2026, 11, 1, 8, 59)));
        Assert.Equal(new DateTime(2026, 11, 1, 1, 0, 0), Pacific.ToLocal(Utc(2026, 11, 1, 9, 0)));
        Assert.Equal(new DateOnly(2026, 11, 1), Pacific.ToLocalDate(Utc(2026, 11, 1, 9, 30)));
    }

    [Fact]
    public void Null_stays_null() => Assert.Null(Pacific.ToLocalDate((DateTime?)null));

    [Fact]
    public void Local_kind_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Pacific.ToLocal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local)));
}
