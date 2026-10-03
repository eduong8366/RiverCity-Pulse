namespace Sac311.Domain.Aggregates;

/// <param name="Direction">"slower", "faster" or "steady" (current median within <see cref="Trend.SteadyBand"/> of the prior one).</param>
/// <param name="MedianChangePct">Change of the median against the prior period in percent; null when the prior median is 0.</param>
public sealed record TrendResult(string Direction, decimal? MedianChangePct);

public static class Trend
{
    /// <summary>Each period needs at least this many closed requests for a trend.</summary>
    public const int MinRequests = 30;

    /// <summary>A median change smaller than this (5%) is "steady".</summary>
    public const decimal SteadyBand = 0.05m;

    public const string Slower = "slower";
    public const string Faster = "faster";
    public const string Steady = "steady";

    /// <summary>
    /// Compares the median days to close of the current period with the prior one. Null when either period has fewer
    /// than <see cref="MinRequests"/> closed requests: a median of a handful of requests swings too much to call a trend.
    /// </summary>
    public static TrendResult? Compare(int currentCount, decimal? currentMedian, int priorCount, decimal? priorMedian)
    {
        if (currentCount < MinRequests || priorCount < MinRequests || currentMedian is not { } current || priorMedian is not { } prior)
        {
            return null;
        }

        if (prior == 0m)
        {
            return new TrendResult(current == 0m ? Steady : Slower, null);
        }

        var change = (current - prior) / prior;
        var direction = Math.Abs(change) < SteadyBand ? Steady : change > 0 ? Slower : Faster;
        return new TrendResult(direction, Math.Round(change * 100m, 1, MidpointRounding.AwayFromZero));
    }
}
