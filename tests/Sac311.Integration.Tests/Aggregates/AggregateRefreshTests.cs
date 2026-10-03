using Sac311.Data.Ingest;
using Sac311.Ingestion;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Aggregates;

public class AggregateRefreshTests(SqlServerFixture db) : DatabaseTest(db)
{
    private sealed record CellCheck(
        short WindowDays, string Period, string NeighborhoodSlug, byte DistrictNumber, string CategoryGroup,
        int ClosedCount, decimal? MedianDays, decimal? P90Days, int HandCount, double? HandMedian, double? HandP90);

    /// <summary>40 requests over two neighborhoods, two districts and two categories; every fifth one stays open.</summary>
    private void SeedVariedSource()
    {
        Source.Seed(40, NowUtc);
        for (var i = 0; i < Source.Features.Count; i++)
        {
            var f = Source.Features[i];
            if (i % 3 == 0)
            {
                f.Neighborhood = "Central Oak Park";
                f.CouncilDistrictNumber = "District 5";
            }

            if (i % 4 == 0)
            {
                f.CategoryLevel1 = "Solid Waste";
            }

            if (i % 5 != 4)
            {
                // Created i + 10 days ago; closed 3 to 99 hours later, so both periods of the 30-day window have rows.
                f.Close(f.CreatedUtc!.Value.AddHours(3 + (i * i % 97)));
            }
        }
    }

    [Fact]
    public async Task Backfill_refreshes_aggregates_whose_percentiles_match_percentile_cont()
    {
        SeedVariedSource();

        var run = await Worker.BackfillAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal(1, await CountAsync("agg.refresh", $"run_id = {run.RunId} AND request_count = 40"));

        // Every cell's count, median and p90 against PERCENTILE_CONT over the matching requests, run by hand in SQL.
        var cells = await Db.QueryAsync<CellCheck>("""
            DECLARE @asOf date = (SELECT TOP (1) as_of_date FROM agg.refresh ORDER BY refresh_id DESC);
            SELECT a.window_days AS WindowDays, a.period AS Period, a.neighborhood_slug AS NeighborhoodSlug, a.district_number AS DistrictNumber,
                   a.category_group AS CategoryGroup, a.closed_count AS ClosedCount, a.median_days AS MedianDays, a.p90_days AS P90Days,
                   ISNULL(h.n, 0) AS HandCount, h.med AS HandMedian, h.p90 AS HandP90
            FROM agg.stats_window AS a
            OUTER APPLY
            (
                SELECT TOP (1) COUNT(*) OVER () AS n,
                       PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY r.days_to_close) OVER () AS med,
                       PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY r.days_to_close) OVER () AS p90
                FROM dbo.service_request AS r
                WHERE r.is_metric_eligible = 1 AND r.days_to_close IS NOT NULL AND r.source_removed_utc IS NULL
                  AND (a.neighborhood_slug = '' OR r.neighborhood_slug = a.neighborhood_slug)
                  AND (a.district_number = 0 OR r.district_number = a.district_number)
                  AND (a.category_group = '' OR r.category_group = a.category_group)
                  AND r.closed_date_local > DATEADD(day, -a.window_days * CASE a.period WHEN 'current' THEN 1 ELSE 2 END, @asOf)
                  AND r.closed_date_local <= DATEADD(day, -a.window_days * CASE a.period WHEN 'current' THEN 0 ELSE 1 END, @asOf)
            ) AS h;
            """);

        Assert.Contains(cells, c => c is { WindowDays: 30, Period: "prior", NeighborhoodSlug: "central-oak-park", CategoryGroup: "Solid Waste" } && c.ClosedCount > 0);
        Assert.All(cells, c =>
        {
            Assert.Equal(c.HandCount, c.ClosedCount);
            if (c.ClosedCount > 0)
            {
                // Stored rounded to 2 decimals.
                Assert.Equal(c.HandMedian!.Value, (double)c.MedianDays!.Value, 0.0051);
                Assert.Equal(c.HandP90!.Value, (double)c.P90Days!.Value, 0.0051);
            }
        });

        Assert.Equal(8, await Db.ScalarAsync<int>(
            "SELECT open_count FROM agg.open_backlog WHERE neighborhood_slug = '' AND district_number = 0 AND category_group = '';"));
        Assert.Equal(8, await Db.ScalarAsync<int>(
            "SELECT TOP (1) open_count FROM agg.backlog_daily WHERE district_number = 0 AND category_group = '' ORDER BY day DESC;"));
    }

    [Fact]
    public async Task A_run_that_changes_nothing_keeps_todays_aggregates()
    {
        Source.Seed(10, NowUtc);
        await Worker.BackfillAsync();

        var unchanged = await Worker.IncrementalAsync();
        Assert.Equal(1, await CountAsync("agg.refresh"));

        Source.Features[2].Close(NowUtc.AddMinutes(-5));
        var changed = await Worker.IncrementalAsync();

        AssertStatus(RunStatus.Succeeded, unchanged);
        AssertStatus(RunStatus.Succeeded, changed);
        Assert.Equal(1, changed.RowsUpdated);
        Assert.Equal(2, await CountAsync("agg.refresh"));
        Assert.Equal(1, await CountAsync("agg.refresh", $"run_id = {changed.RunId}"));
    }

    [Fact]
    public async Task A_failed_run_does_not_refresh()
    {
        Source.Seed(10, NowUtc);
        Source.RenamedField = ("PublicStatus", "Status");

        var run = await Worker.BackfillAsync();

        AssertStatus(RunStatus.SchemaDrift, run);
        Assert.Equal(0, await CountAsync("agg.refresh"));
    }
}

public class AggregateRefreshRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    [Theory]
    [InlineData(5, "2026-10-02", true)]
    [InlineData(0, "2026-10-02", false)]
    [InlineData(0, "2026-10-01", true)]
    [InlineData(0, null, true)]
    public void Refresh_is_due_when_rows_changed_or_the_aggregates_are_from_an_earlier_day(int changed, string? lastAsOf, bool due)
    {
        var last = lastAsOf is null ? (DateOnly?)null : DateOnly.Parse(lastAsOf, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(due, AggregateRefresher.IsDue(changed, last, Today));
    }
}
