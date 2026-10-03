using Sac311.Api.Data;
using Sac311.Domain.Aggregates;

namespace Sac311.Api;

/// <summary>
/// One period of a window. <c>MedianDays</c> and <c>P90Days</c> are over the <c>Closed</c> requests (closed in the
/// period and not excluded by a DQ flag); <c>Excluded</c> counts the closed requests left out.
/// </summary>
internal sealed record PeriodStats(DateOnly From, DateOnly To, int Opened, int Closed, int Excluded, decimal? MedianDays, decimal? P90Days)
{
    /// <summary>The current period (<paramref name="current"/>) or the one before it, of a window ending on <paramref name="asOfDate"/>.</summary>
    public static PeriodStats Of(PeriodRow? row, DateOnly asOfDate, int windowDays, bool current)
    {
        var to = current ? asOfDate : asOfDate.AddDays(-windowDays);
        return new PeriodStats(to.AddDays(1 - windowDays), to, row?.Opened ?? 0, row?.Closed ?? 0, row?.Excluded ?? 0, row?.MedianDays, row?.P90Days);
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
