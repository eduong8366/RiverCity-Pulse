using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Aggregates;

/// <summary>One request as the aggregates read it from <c>dbo.service_request</c> (requests still in the source only).</summary>
/// <param name="IsService">False for a non-service request (an information call, an inbox item...): left out of every figure.</param>
/// <param name="IsBulkClosure">Closed in a clear-out (<see cref="DqFlags.BulkClosure"/>): a label; still metric-eligible.</param>
public readonly record struct AggregateRequest(
    string? NeighborhoodSlug,
    byte? DistrictNumber,
    string CategoryGroup,
    StatusGroup Status,
    DateTime? CreatedUtc,
    DateOnly? CreatedLocal,
    DateOnly? ClosedLocal,
    DateOnly? BacklogCloseLocal,
    decimal? DaysToClose,
    bool IsMetricEligible,
    bool IsService = true,
    bool IsBulkClosure = false);

/// <summary>
/// One cell of the aggregate cube: a neighborhood, a council district and a category group, any of which may be "all"
/// (<see cref="All"/> or <see cref="AllDistricts"/>). A request with no neighborhood (or district) only counts in the
/// cells that are "all" for it.
/// </summary>
public readonly record struct AggregateCell(string NeighborhoodSlug, byte DistrictNumber, string CategoryGroup)
{
    public const string All = "";
    public const byte AllDistricts = 0;
}

/// <summary>Values of <c>agg.stats_window.period</c>.</summary>
public static class AggregatePeriod
{
    /// <summary>The last N days through the as-of date.</summary>
    public const string Current = "current";

    /// <summary>The N days before <see cref="Current"/>.</summary>
    public const string Prior = "prior";
}

/// <param name="Opened">Requests created in the period.</param>
/// <param name="Closed">Metric-eligible requests closed in the period; the median and p90 are over these.</param>
/// <param name="Excluded">Closed requests that left the backlog in the period but are kept out of the metrics (date problems).</param>
/// <param name="BulkClosed">The part of <paramref name="Closed"/> closed in a clear-out (<see cref="DqFlags.BulkClosure"/>); counted, shown as a note.</param>
public sealed record WindowStats(
    int WindowDays, string Period, AggregateCell Cell, int Opened, int Closed, int Excluded, int BulkClosed, decimal? MedianDays, decimal? P90Days);

/// <summary>Requests open at the as-of time, and the median of their ages in days.</summary>
public sealed record OpenStats(AggregateCell Cell, int Open, decimal? MedianAgeDays);

/// <summary>Requests opened and closed on one local day, and the number open at the end of it.</summary>
public sealed record BacklogDay(DateOnly Day, byte DistrictNumber, string CategoryGroup, int Opened, int Closed, int Open);

/// <summary>Everything one refresh publishes to the <c>agg</c> tables.</summary>
public sealed record AggregateSet(
    DateTime AsOfUtc,
    DateOnly AsOfDate,
    int RequestCount,
    IReadOnlyList<WindowStats> Windows,
    IReadOnlyList<OpenStats> Open,
    IReadOnlyList<BacklogDay> Backlog);
