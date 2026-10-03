using Sac311.Domain.Aggregates;

namespace Sac311.Domain.Tests.Aggregates;

public class PercentileTests
{
    [Fact]
    public void Even_count_median_interpolates_between_the_middle_two()
    {
        // Days 1, 2, 3, 10: median (2 + 3) / 2 = 2.5.
        Assert.Equal(2.5m, Percentile.Cont([100, 200, 300, 1000], 0.5m));
    }

    [Fact]
    public void P90_interpolates_like_percentile_cont()
    {
        // Position 0.9 × 3 = 2.7: 3 + 0.7 × (10 − 3) = 7.9.
        Assert.Equal(7.9m, Percentile.Cont([100, 200, 300, 1000], 0.9m));
    }

    [Fact]
    public void Odd_count_median_is_the_middle_value()
    {
        Assert.Equal(0.25m, Percentile.Cont([5, 25, 9000], 0.5m));
    }

    [Fact]
    public void Single_value_is_every_percentile()
    {
        Assert.Equal(4.2m, Percentile.Cont([420], 0.5m));
        Assert.Equal(4.2m, Percentile.Cont([420], 0.9m));
    }

    [Fact]
    public void Result_is_rounded_to_two_decimals()
    {
        // Position 0.9 × 1 = 0.9: 0.01 + 0.9 × 0.01 = 0.019 days.
        Assert.Equal(0.02m, Percentile.Cont([1, 2], 0.9m));
    }

    [Fact]
    public void Empty_list_throws()
    {
        Assert.Throws<ArgumentException>(() => Percentile.Cont([], 0.5m));
    }
}
