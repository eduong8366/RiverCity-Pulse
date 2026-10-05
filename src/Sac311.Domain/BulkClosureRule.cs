namespace Sac311.Domain;

/// <summary>
/// When a day's closures in one category are a clear-out of old requests rather than work done that day
/// (<see cref="DqFlags.BulkClosure"/>). A (closed date, category group) is a clear-out when at least
/// <see cref="MinCount"/> of its closures are older than <see cref="DetectAgeDays"/>; in a clear-out, every closure
/// older than <see cref="MemberAgeDays"/> is flagged. Only service requests with no date problem count.
/// <c>usp_classify_for_metrics</c> applies it with these values; docs/metrics.md explains them.
/// </summary>
public static class BulkClosureRule
{
    /// <summary>Old closures on one day in one category that make it a clear-out.</summary>
    public const int MinCount = 100;

    /// <summary>Days to close above which a closure counts toward <see cref="MinCount"/>.</summary>
    public const int DetectAgeDays = 180;

    /// <summary>
    /// Days to close above which a closure in a clear-out is flagged. Lower than <see cref="DetectAgeDays"/> because a
    /// clear-out sweeps up younger requests in the same minute; same-day closures younger than this stay counted.
    /// </summary>
    public const int MemberAgeDays = 90;
}
