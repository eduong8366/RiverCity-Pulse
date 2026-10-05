using Sac311.Api.Data;
using Sac311.Domain.Aggregates;

namespace Sac311.Api;

/// <summary>
/// One period of a window, over service requests only (docs/metrics.md). <c>MedianDays</c> and <c>P90Days</c> are over
/// the <c>Closed</c> requests (closed in the period with a trustworthy time to close); <c>Excluded</c> counts the closed
/// requests left out of timing, and <c>BulkClosed</c> the part of those closed in a clear-out.
/// </summary>
internal sealed record PeriodStats(DateOnly From, DateOnly To, int Opened, int Closed, int Excluded, int BulkClosed, decimal? MedianDays, decimal? P90Days)
{
    /// <summary>The current period (<paramref name="current"/>) or the one before it, of a window ending on <paramref name="asOfDate"/>.</summary>
    public static PeriodStats Of(PeriodRow? row, DateOnly asOfDate, int windowDays, bool current)
    {
        var to = current ? asOfDate : asOfDate.AddDays(-windowDays);
        return new PeriodStats(to.AddDays(1 - windowDays), to, row?.Opened ?? 0, row?.Closed ?? 0, row?.Excluded ?? 0, row?.BulkClosed ?? 0, row?.MedianDays, row?.P90Days);
    }
}

/// <summary>Both periods of a cell, the trend between them, and the requests open now.</summary>
internal sealed record CellStats(PeriodStats Current, PeriodStats Prior, TrendResult? Trend, int OpenBacklog, decimal? MedianOpenAgeDays)
{
    public static CellStats Of(Cell? cell, DateOnly asOfDate, int windowDays)
    {
        var current = PeriodStats.Of(cell?.Current, asOfDate, windowDays, current: true);
        var prior = PeriodStats.Of(cell?.Prior, asOfDate, windowDays, current: false);
        return new CellStats(
            current, prior, Domain.Aggregates.Trend.Compare(current.Closed, current.MedianDays, prior.Closed, prior.MedianDays),
            cell?.OpenCount ?? 0, cell?.MedianOpenAgeDays);
    }
}

internal sealed record NeighborhoodStatsResponse(Neighborhood Neighborhood, string? Category, int WindowDays, CellStats Stats, DateTime AsOf);

internal sealed record CategorySummary(string Category, CellStats Stats);

internal sealed record CategorySummaryResponse(int WindowDays, int? District, CellStats Total, IReadOnlyList<CategorySummary> Categories, DateTime AsOf);

/// <summary>One neighborhood on the map: its current-period figures, the trend, and its open backlog.</summary>
internal sealed record MapNeighborhood(
    string Slug, string Name, int Opened, int Closed, decimal? MedianDays, decimal? P90Days, TrendResult? Trend, int OpenBacklog);

internal sealed record MapResponse(int WindowDays, string? Category, int? District, IReadOnlyList<MapNeighborhood> Neighborhoods, DateTime AsOf);

internal sealed record BacklogResponse(
    DateOnly From, DateOnly To, string Grain, string? Category, int? District, IReadOnlyList<BacklogPoint> Points, DateTime AsOf);

internal sealed record DqSummary(int Pass, int Warn, int Fail, int Info, IReadOnlyList<DqCheck> Issues);

/// <param name="Status"><c>fresh</c> when an ingestion run succeeded within <c>MaxAgeMinutes</c>, else <c>stale</c>.</param>
/// <param name="RequestCount">Requests still in the source, as of the last aggregate refresh.</param>
internal sealed record FreshnessResponse(
    string Status,
    int MaxAgeMinutes,
    RunSummary? LastSuccess,
    RunSummary? LastRun,
    DateTime? WatermarkUtc,
    int? RequestCount,
    DateOnly? AggregatesAsOfDate,
    DateTime? AggregatesRefreshedUtc,
    DqSummary Dq);

/// <summary>A non-service type: a whole category ("Review") or one line inside a service category ("Parking / General").</summary>
internal sealed record NonServiceType(string CategoryGroup, string Type, string Reason, int Opened, int Closed);

internal sealed record NonServiceSummary(int Opened, int Closed, IReadOnlyList<NonServiceType> Types);

/// <summary>One clear-out: service requests in a category closed on one day with the bulk-closure flag, and their average age.</summary>
internal sealed record BulkClosureDay(DateOnly Date, string Category, int Closed, decimal? AverageDaysToClose);

/// <param name="Closed">Every bulk closure in the period (the sum of the citywide <c>bulkClosed</c>).</param>
/// <param name="LargestDays">Up to 10 of the period's clear-outs, largest first.</param>
internal sealed record BulkClosureSummary(int Closed, IReadOnlyList<BulkClosureDay> LargestDays);

/// <summary>Closed service requests left out of timing for one date problem (a request can have two).</summary>
internal sealed record DateProblemExclusion(string Flag, string Description, int Closed);

internal sealed record ExclusionPeriod(
    DateOnly From, DateOnly To, NonServiceSummary NonService, BulkClosureSummary BulkClosures, IReadOnlyList<DateProblemExclusion> DateProblems);

internal sealed record NonServiceOpen(string CategoryGroup, string Type, string Reason, int Open);

/// <summary>Non-service requests open at the as-of time (left out of the open backlog).</summary>
internal sealed record NonServiceOpenNow(int Open, IReadOnlyList<NonServiceOpen> Types);

/// <summary>The bulk-closure rule's values (Sac311.Domain.BulkClosureRule).</summary>
internal sealed record BulkClosureRules(int MinCount, int DetectAgeDays, int MemberAgeDays);

/// <summary>Everything the headline figures leave out, citywide, and why. <c>Definitions</c> links the public write-up.</summary>
internal sealed record ExclusionsResponse(
    int WindowDays, ExclusionPeriod Current, ExclusionPeriod Prior, NonServiceOpenNow OpenNow, BulkClosureRules BulkClosureRule, string Definitions, DateTime AsOf);
