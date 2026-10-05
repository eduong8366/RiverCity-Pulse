namespace Sac311.Domain;

/// <summary>
/// When closures are a clear-out of old requests (<see cref="DqFlags.BulkClosure"/>). A label for the published notes
/// only: clear-outs are counted in every figure as recorded (docs/metrics.md). Two clauses, either is enough:
/// <list type="bullet">
/// <item>day: a (closed date, category group) with at least <see cref="MinCount"/> closures older than
/// <see cref="DetectAgeDays"/>;</item>
/// <item>sweep: one closed minute (UTC) with at least <see cref="SweepMinCount"/> closures older than
/// <see cref="DetectAgeDays"/>, across any categories.</item>
/// </list>
/// The members of a clear-out are its closures older than <see cref="MemberAgeDays"/>. Only service requests with no
/// date problem count. <c>usp_classify_for_metrics</c> applies it with these values.
/// </summary>
public static class BulkClosureRule
{
    /// <summary>Old closures on one day in one category that make it a clear-out.</summary>
    public const int MinCount = 100;

    /// <summary>Old closures in one minute, across any categories, that make it a clear-out (a sweep).</summary>
    public const int SweepMinCount = 50;

    /// <summary>Days to close above which a closure counts toward <see cref="MinCount"/> and <see cref="SweepMinCount"/>.</summary>
    public const int DetectAgeDays = 180;

    /// <summary>
    /// Days to close above which a closure in a clear-out is a member. Lower than <see cref="DetectAgeDays"/> because a
    /// clear-out sweeps up younger requests in the same minute; same-day closures younger than this aren't labelled.
    /// </summary>
    public const int MemberAgeDays = 90;
}
