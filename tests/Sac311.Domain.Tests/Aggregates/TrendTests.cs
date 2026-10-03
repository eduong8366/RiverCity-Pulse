using Sac311.Domain.Aggregates;

namespace Sac311.Domain.Tests.Aggregates;

public class TrendTests
{
    [Fact]
    public void Higher_median_is_slower()
    {
        Assert.Equal(new TrendResult(Trend.Slower, 25.0m), Trend.Compare(40, 5m, 40, 4m));
    }

    [Fact]
    public void Lower_median_is_faster()
    {
        Assert.Equal(new TrendResult(Trend.Faster, -50.0m), Trend.Compare(40, 2m, 40, 4m));
    }

    [Fact]
    public void Change_under_five_percent_is_steady()
    {
        Assert.Equal(new TrendResult(Trend.Steady, 4.0m), Trend.Compare(40, 5.2m, 40, 5m));
        Assert.Equal(Trend.Slower, Trend.Compare(40, 5.25m, 40, 5m)!.Direction);
    }

    [Theory]
    [InlineData(29, 40)]
    [InlineData(40, 29)]
    [InlineData(0, 0)]
    public void Fewer_than_30_requests_in_either_period_has_no_trend(int current, int prior)
    {
        Assert.Null(Trend.Compare(current, 5m, prior, 4m));
    }

    [Fact]
    public void Exactly_30_in_each_period_is_enough()
    {
        Assert.NotNull(Trend.Compare(30, 5m, 30, 4m));
    }

    [Fact]
    public void Zero_prior_median_has_a_direction_but_no_percentage()
    {
        Assert.Equal(new TrendResult(Trend.Slower, null), Trend.Compare(40, 1m, 40, 0m));
        Assert.Equal(new TrendResult(Trend.Steady, null), Trend.Compare(40, 0m, 40, 0m));
    }
}
