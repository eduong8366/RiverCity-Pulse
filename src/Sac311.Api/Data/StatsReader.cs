using Dapper;
using Sac311.Data;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Data;

/// <summary>A neighborhood as named in the boundary file.</summary>
internal sealed record Neighborhood(string Slug, string Name);

/// <summary>One period's row of <c>agg.stats_window</c>.</summary>
internal sealed record PeriodRow(int Opened, int Closed, int Excluded, decimal? MedianDays, decimal? P90Days);

/// <summary>A cell of the aggregate cube with both periods and the open backlog; missing parts had no requests.</summary>
internal sealed record Cell(string NeighborhoodSlug, string CategoryGroup, PeriodRow? Current, PeriodRow? Prior, int OpenCount, decimal? MedianOpenAgeDays);

/// <summary>One day of <c>agg.backlog_daily</c>.</summary>
internal sealed record BacklogPoint(DateOnly Date, int Opened, int Closed, int Open);

/// <summary>Point lookups on the precomputed <c>agg</c> tables, and the reference lists the API validates against.</summary>
internal sealed class StatsReader(Sac311Db db)
{
    public async Task<IReadOnlyList<Neighborhood>> NeighborhoodsAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Neighborhood>(new CommandDefinition(
            "SELECT DISTINCT neighborhood_slug AS Slug, neighborhood AS Name FROM ref.neighborhood_alias ORDER BY Name;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>Every category group a request can have: the seeded groups plus <c>Unmapped</c>.</summary>
    public async Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT category_group FROM ref.category_map UNION SELECT N'Unmapped' ORDER BY 1;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>
    /// The cells for one window and district (<see cref="AggregateCell.AllDistricts"/> for all).
    /// <paramref name="slug"/>: one neighborhood, <see cref="AggregateCell.All"/> for all of them together, or null for
    /// each neighborhood separately. <paramref name="category"/>: one group, <see cref="AggregateCell.All"/>, or null for
    /// every group and the all-categories cell.
    /// </summary>
    public async Task<IReadOnlyList<Cell>> CellsAsync(int window, byte district, string? slug, string? category, CancellationToken cancellationToken)
    {
        const string filter =
            """
            district_number = @district
              AND ((@slug IS NULL AND neighborhood_slug <> '') OR neighborhood_slug = @slug)
              AND (@category IS NULL OR category_group = @category)
            """;

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var results = await conn.QueryMultipleAsync(new CommandDefinition(
            $"""
            SELECT neighborhood_slug AS NeighborhoodSlug, category_group AS CategoryGroup, period AS Period, opened_count AS Opened,
                   closed_count AS Closed, excluded_count AS Excluded, median_days AS MedianDays, p90_days AS P90Days
            FROM agg.stats_window
            WHERE window_days = @window AND {filter};

            SELECT neighborhood_slug AS NeighborhoodSlug, category_group AS CategoryGroup, open_count AS OpenCount,
                   median_open_age_days AS MedianOpenAgeDays
            FROM agg.open_backlog
            WHERE {filter};
            """,
            new { window, district, slug = new DbString { Value = slug, IsAnsi = true, Length = 100 }, category },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var stats = (await results.ReadAsync<StatsRow>().ConfigureAwait(false)).AsList();
        var open = (await results.ReadAsync<OpenRow>().ConfigureAwait(false)).ToDictionary(o => (o.NeighborhoodSlug, o.CategoryGroup));

        var keys = stats.Select(s => (s.NeighborhoodSlug, s.CategoryGroup)).Concat(open.Keys).Distinct();
        return
        [
            .. keys.Select(k =>
            {
                var current = stats.Find(s => (s.NeighborhoodSlug, s.CategoryGroup) == k && s.Period == AggregatePeriod.Current);
                var prior = stats.Find(s => (s.NeighborhoodSlug, s.CategoryGroup) == k && s.Period == AggregatePeriod.Prior);
                open.TryGetValue(k, out var o);
                return new Cell(k.NeighborhoodSlug, k.CategoryGroup, current?.ToPeriod(), prior?.ToPeriod(), o?.OpenCount ?? 0, o?.MedianOpenAgeDays);
            }),
        ];
    }

    /// <summary>Daily backlog for a district and category (either may be "all"), from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<IReadOnlyList<BacklogPoint>> BacklogAsync(byte district, string category, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<BacklogRow>(new CommandDefinition(
            """
            SELECT day AS Day, opened_count AS Opened, closed_count AS Closed, open_count AS OpenCount
            FROM agg.backlog_daily
            WHERE district_number = @district AND category_group = @category AND day BETWEEN @from AND @to
            ORDER BY day;
            """,
            new { district, category, from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => new BacklogPoint(DateOnly.FromDateTime(r.Day), r.Opened, r.Closed, r.OpenCount))];
    }

    private sealed record StatsRow(
        string NeighborhoodSlug, string CategoryGroup, string Period, int Opened, int Closed, int Excluded, decimal? MedianDays, decimal? P90Days)
    {
        public PeriodRow ToPeriod() => new(Opened, Closed, Excluded, MedianDays, P90Days);
    }

    private sealed record OpenRow(string NeighborhoodSlug, string CategoryGroup, int OpenCount, decimal? MedianOpenAgeDays);

    private sealed record BacklogRow(DateTime Day, int Opened, int Closed, int OpenCount);
}
