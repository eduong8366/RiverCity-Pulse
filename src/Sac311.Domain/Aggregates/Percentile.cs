namespace Sac311.Domain.Aggregates;

public static class Percentile
{
    /// <summary>
    /// The continuous percentile of a sorted, non-empty list, defined as SQL Server's <c>PERCENTILE_CONT</c>: the value
    /// at 0-based position <c>p × (n − 1)</c>, interpolated linearly between the two neighbours. Values are in
    /// hundredths of a day; the result is in days, rounded to 2 decimals.
    /// </summary>
    public static decimal Cont(IReadOnlyList<int> sortedHundredths, decimal p)
    {
        ArgumentNullException.ThrowIfNull(sortedHundredths);
        if (sortedHundredths.Count == 0)
        {
            throw new ArgumentException("Expected at least one value.", nameof(sortedHundredths));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(p, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(p, 1m);

        var position = p * (sortedHundredths.Count - 1);
        var lo = (int)decimal.Floor(position);
        var hi = (int)decimal.Ceiling(position);
        var value = sortedHundredths[lo] + ((position - lo) * (sortedHundredths[hi] - sortedHundredths[lo]));
        return Math.Round(value / 100m, 2, MidpointRounding.AwayFromZero);
    }
}
