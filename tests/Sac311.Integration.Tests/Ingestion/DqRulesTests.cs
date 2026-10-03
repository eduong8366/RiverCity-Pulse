using Sac311.Data.Quality;
using Sac311.Ingestion;

namespace Sac311.Integration.Tests.Ingestion;

public class DqRulesTests
{
    [Theory]
    [InlineData(1_000_000, 1_000_000, DqStatus.Pass)]
    [InlineData(980_000, 1_000_000, DqStatus.Pass)]
    [InlineData(979_999, 1_000_000, DqStatus.Fail)]
    [InlineData(1_200_000, 1_000_000, DqStatus.Pass)]
    public void Source_count_fails_on_a_drop_of_more_than_2_percent(long observed, int high, string status) =>
        Assert.Equal(status, DqRules.SourceCount(observed, high).Status);

    [Fact]
    public void Source_count_without_history_is_info() =>
        Assert.Equal(DqStatus.Info, DqRules.SourceCount(1_000, null).Status);

    [Theory]
    [InlineData(20, 100, 15, 100, DqStatus.Pass)]  // 20% vs 15%: exactly 5 points
    [InlineData(21, 100, 15, 100, DqStatus.Warn)]  // 21% vs 15%
    [InlineData(0, 100, 50, 100, DqStatus.Pass)]   // falling is fine
    [InlineData(29, 29, 0, 100, DqStatus.Info)]    // too few recent rows
    [InlineData(10, 100, 0, 0, DqStatus.Info)]     // no baseline rows
    public void Null_rate_warns_more_than_5_points_above_the_baseline(int recentNull, int recentRows, int baselineNull, int baselineRows, string status)
    {
        var result = DqRules.NullRate("address", recentNull, recentRows, baselineNull, baselineRows);

        Assert.Equal(status, result.Status);
        Assert.Equal("null_rate.address", result.CheckName);
    }

    [Theory]
    [InlineData(100, null, DqStatus.Info)]
    [InlineData(150, 100, DqStatus.Pass)]        // +50: the minimum jump
    [InlineData(151, 100, DqStatus.Warn)]
    [InlineData(15_100, 15_000, DqStatus.Pass)]  // +100 is 0.67%
    [InlineData(15_200, 15_000, DqStatus.Warn)]  // +200 is 1.3%
    [InlineData(10, 500, DqStatus.Pass)]         // falling is fine
    public void Flag_count_warns_on_a_jump(int observed, int? previous, string status) =>
        Assert.Equal(status, DqRules.FlagCount("InvalidCloseOrder", observed, previous).Status);

    [Fact]
    public void Any_reject_warns()
    {
        Assert.Equal(DqStatus.Pass, DqRules.Rejects(0).Status);
        Assert.Equal(DqStatus.Warn, DqRules.Rejects(1).Status);
    }
}
