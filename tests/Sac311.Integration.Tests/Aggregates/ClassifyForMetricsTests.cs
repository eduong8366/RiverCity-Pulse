using System.Globalization;
using Dapper;
using Sac311.Data.Aggregates;
using Sac311.Data.Ingest;
using Sac311.Domain;
using Sac311.Domain.Cleaners;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Aggregates;

/// <summary><c>usp_classify_for_metrics</c>: the clear-out label (day and sweep clauses) and the non-service classification (docs/metrics.md).</summary>
public class ClassifyForMetricsTests(SqlServerFixture db) : DatabaseTest(db)
{
    private static readonly DateOnly ClearOutDay = new(2026, 8, 10);
    private int _rows;

    private Task<Classification> ClassifyAsync() => Worker.Get<AggregateStore>().ClassifyAsync(CancellationToken.None);

    /// <summary>
    /// Writes <paramref name="count"/> requests straight into dbo.service_request, closed at 17:<paramref name="minute"/>
    /// UTC, or spread over 17:00 to 17:29 (at most a few per minute, so no sweep) when it is null.
    /// </summary>
    private async Task InsertAsync(
        int count, decimal? days, string group = "Parking", DateOnly? closed = null, string? level1 = null, string? level2 = "Meter",
        DqFlags flags = DqFlags.None, string status = "Closed", string prefix = "T", int? minute = null)
    {
        var day = closed ?? ClearOutDay;
        var rows = Enumerable.Range(0, count).Select(_ =>
        {
            var n = ++_rows;
            var closedUtc = day.ToDateTime(new TimeOnly(17, minute ?? n % 30), DateTimeKind.Utc);
            var createdUtc = closedUtc.AddDays(-(double)(days ?? 1m));
            return new
            {
                Ref = string.Create(CultureInfo.InvariantCulture, $"{prefix}-{n:D6}"),
                ObjectId = (long)n,
                Level1 = level1 ?? group,
                Level2 = level2,
                Group = group,
                Status = status,
                CreatedUtc = createdUtc,
                ClosedUtc = status == "Closed" ? closedUtc : (DateTime?)null,
                CreatedLocal = Pacific.ToLocalDate(createdUtc).ToDateTime(TimeOnly.MinValue),
                ClosedLocal = status == "Closed" ? day.ToDateTime(TimeOnly.MinValue) : (DateTime?)null,
                Days = status == "Closed" ? days : null,
                Flags = (int)flags,
            };
        }).ToList();

        await using var conn = await Db.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO dbo.service_request
                (reference_number, object_id, category_level1, category_level2, category_group, source_channel, status_group,
                 created_utc, updated_utc, closed_utc, created_date_local, closed_date_local, backlog_close_date_local, days_to_close,
                 dq_flags, row_hash, first_seen_utc, last_seen_utc, last_changed_utc)
            VALUES
                (@Ref, @ObjectId, @Level1, @Level2, @Group, N'Phone', @Status,
                 @CreatedUtc, ISNULL(@ClosedUtc, @CreatedUtc), @ClosedUtc, @CreatedLocal, @ClosedLocal, @ClosedLocal, @Days,
                 @Flags, 0x00, @CreatedUtc, @CreatedUtc, @CreatedUtc);
            """,
            rows);
    }

    private Task<int> BulkAsync(string where = "1=1") => CountAsync("dbo.service_request", $"dq_flags & 4096 <> 0 AND {where}");

    [Fact]
    public async Task One_hundred_old_closures_in_a_category_on_one_day_are_a_clear_out()
    {
        await InsertAsync(100, 400m);

        var result = await ClassifyAsync();

        Assert.Equal((100, 1, 0, 100), (result.BulkChanged, result.ClearOuts, result.SweepMinutes, result.BulkRows));

        // A label only: the clear-out stays in the metrics.
        Assert.Equal(100, await CountAsync("dbo.service_request", "is_metric_eligible = 1"));
    }

    [Fact]
    public async Task Ninety_nine_are_not()
    {
        await InsertAsync(99, 400m);

        Assert.Equal(0, (await ClassifyAsync()).BulkRows);
    }

    [Fact]
    public async Task A_clear_out_flags_closures_over_90_days_and_leaves_younger_ones_counted()
    {
        await InsertAsync(100, 400m);
        await InsertAsync(1, 120m, prefix: "MID");
        await InsertAsync(1, 30m, prefix: "YOUNG");
        await InsertAsync(1, 120m, closed: ClearOutDay.AddDays(1), prefix: "NORMAL");

        await ClassifyAsync();

        Assert.Equal(101, await BulkAsync());
        Assert.Equal(1, await BulkAsync("reference_number LIKE 'MID-%'"));
        Assert.Equal(0, await BulkAsync("reference_number LIKE 'YOUNG-%'"));
        Assert.Equal(0, await BulkAsync("reference_number LIKE 'NORMAL-%'"));
    }

    [Fact]
    public async Task Two_categories_do_not_combine()
    {
        await InsertAsync(60, 400m, group: "Parking");
        await InsertAsync(60, 400m, group: "Streets");

        Assert.Equal(0, (await ClassifyAsync()).BulkRows);
    }

    [Fact]
    public async Task Fifty_old_closures_in_one_minute_across_two_categories_are_a_sweep()
    {
        await InsertAsync(25, 400m, group: "Water", minute: 0);
        await InsertAsync(25, 200m, group: "Sewer", minute: 0);

        var result = await ClassifyAsync();

        Assert.Equal((0, 1, 50), (result.ClearOuts, result.SweepMinutes, result.BulkRows));
        Assert.Equal(25, await BulkAsync("category_group = 'Sewer'"));
    }

    [Fact]
    public async Task Forty_nine_in_one_minute_are_not()
    {
        await InsertAsync(25, 400m, group: "Water", minute: 0);
        await InsertAsync(24, 400m, group: "Sewer", minute: 0);

        var result = await ClassifyAsync();

        Assert.Equal((0, 0), (result.SweepMinutes, result.BulkRows));
    }

    [Fact]
    public async Task A_sweep_labels_closures_over_90_days_in_its_minute_only()
    {
        await InsertAsync(50, 400m, group: "Water", minute: 0);
        await InsertAsync(1, 120m, group: "Sewer", minute: 0, prefix: "MID");
        await InsertAsync(1, 30m, group: "Sewer", minute: 0, prefix: "YOUNG");
        await InsertAsync(1, 400m, group: "Sewer", minute: 1, prefix: "NEXT");

        await ClassifyAsync();

        Assert.Equal(51, await BulkAsync());
        Assert.Equal(1, await BulkAsync("reference_number LIKE 'MID-%'"));
        Assert.Equal(0, await BulkAsync("(reference_number LIKE 'YOUNG-%' OR reference_number LIKE 'NEXT-%')"));
    }

    [Fact]
    public async Task The_same_minute_on_another_day_does_not_combine()
    {
        await InsertAsync(25, 400m, group: "Water", minute: 5);
        await InsertAsync(25, 400m, group: "Sewer", closed: ClearOutDay.AddDays(1), minute: 5);

        Assert.Equal(0, (await ClassifyAsync()).BulkRows);
    }

    [Fact]
    public async Task Rows_with_a_date_problem_or_non_service_rows_do_not_count_toward_the_100()
    {
        await InsertAsync(98, 400m);
        await InsertAsync(1, 400m, flags: DqFlags.FutureDate, prefix: "DATE");
        await InsertAsync(1, 400m, level1: "Review", level2: "Email Review", group: "Parking", prefix: "INBOX");

        var result = await ClassifyAsync();

        Assert.Equal(1, result.NonServiceRows);
        Assert.Equal(0, result.BulkRows);
    }

    [Fact]
    public async Task The_flag_clears_when_the_group_drops_below_the_threshold()
    {
        await InsertAsync(100, 400m);
        await ClassifyAsync();

        await Db.ScalarAsync<int>("DELETE TOP (1) FROM dbo.service_request; SELECT @@ROWCOUNT;");
        var result = await ClassifyAsync();

        Assert.Equal((99, 0), (result.BulkChanged, result.BulkRows));
    }

    [Fact]
    public async Task A_rerun_changes_nothing()
    {
        await InsertAsync(100, 400m);
        await InsertAsync(3, 0.01m, level1: "Other", level2: "Information", group: "Other", prefix: "INFO");
        await ClassifyAsync();

        var rerun = await ClassifyAsync();

        Assert.Equal((0, 0), (rerun.ServiceChanged, rerun.BulkChanged));
        Assert.Equal((3, 100), (rerun.NonServiceRows, rerun.BulkRows));
    }

    [Fact]
    public async Task Non_service_types_are_matched_by_category_and_line_in_any_spelling()
    {
        await InsertAsync(1, 0.01m, level1: "Other", level2: "Information", group: "Other", prefix: "L1");
        await InsertAsync(1, 0.01m, level1: "Review", level2: null, group: "Process/Unclassified", prefix: "L1NULL");
        await InsertAsync(1, 0.01m, level1: "Parking", level2: "General", prefix: "L2");
        await InsertAsync(1, 0.01m, level1: "PARKING", level2: "general ", prefix: "VARIANT");
        await InsertAsync(1, 0.01m, level1: "Parking", level2: "Meter", prefix: "SERVICE");
        await InsertAsync(1, 0.01m, level1: "Brand New Department", level2: "General", group: "Unmapped", prefix: "UNMAPPED");

        await ClassifyAsync();

        var nonService = await Db.QueryAsync<string>("SELECT LEFT(reference_number, CHARINDEX('-', reference_number) - 1) FROM dbo.service_request WHERE is_service = 0;");
        Assert.Equal(["L1", "L1NULL", "L2", "VARIANT"], nonService.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_seed_change_applies_at_the_next_classification()
    {
        await InsertAsync(2, 0.01m, level1: "Streets", level2: "Alley Gate", group: "Streets");
        await ClassifyAsync();
        Assert.Equal(0, await CountAsync("dbo.service_request", "is_service = 0"));

        try
        {
            await Db.ScalarAsync<int>("""
                INSERT INTO ref.non_service_type (category_key, category_level2_key, source_level1, source_level2, reason)
                VALUES ('streets', 'alleygate', N'Streets', N'Alley Gate', N'Test');
                SELECT 0;
                """);
            Assert.Equal(2, (await ClassifyAsync()).ServiceChanged);
            Assert.Equal(2, await CountAsync("dbo.service_request", "is_service = 0"));
        }
        finally
        {
            await Db.ScalarAsync<int>("DELETE FROM ref.non_service_type WHERE category_key = 'streets' AND category_level2_key = 'alleygate'; SELECT 0;");
        }

        Assert.Equal(2, (await ClassifyAsync()).ServiceChanged);
        Assert.Equal(0, await CountAsync("dbo.service_request", "is_service = 0"));
    }

    [Theory]
    [InlineData("Homeless Camp - Primary")]
    [InlineData("General - Spay/ Neuter")]
    [InlineData("  Google AI ")]
    [InlineData("Ünïcödé café 311")]
    [InlineData("---")]
    [InlineData("")]
    public async Task The_sql_map_key_matches_the_csharp_one(string value) =>
        Assert.Equal(MapKey.For(value), await Db.ScalarAsync<string?>("SELECT dbo.fn_map_key(@value);", new { value }));

    private sealed record ExclusionCheck(short WindowDays, string Period, string Kind, string CategoryGroup, string Label, DateTime? Day, int Opened, int Closed, int Open, decimal? AvgAgeDays);

    private sealed record ClearOutCheck(DateTime Day, string CategoryGroup, int Closed, decimal AvgAgeDays, int MinutesSpanned, bool IsSweep, string? SweepCategories);

    [Fact]
    public async Task A_refresh_counts_clear_outs_leaves_out_non_service_and_reports_both_with_counts()
    {
        var today = Pacific.ToLocalDate(NowUtc);
        await InsertAsync(100, 400m, closed: today.AddDays(-3), prefix: "BULK");
        await InsertAsync(30, 300m, group: "Water", closed: today.AddDays(-5), minute: 45, prefix: "SWEEPW");
        await InsertAsync(25, 200m, group: "Sewer", closed: today.AddDays(-5), minute: 45, prefix: "SWEEPS");
        await InsertAsync(10, 3m, group: "Streets", closed: today.AddDays(-2), prefix: "OK");
        await InsertAsync(1, 2m, group: "Streets", closed: today.AddDays(-4), flags: DqFlags.FutureDate, prefix: "DATE");
        await InsertAsync(2, 0.01m, level1: "Other", level2: "Information", group: "Other", closed: today.AddDays(-1), prefix: "INFO");
        await InsertAsync(1, 0.01m, level1: "Parking", level2: "General", closed: today.AddDays(-2), prefix: "LINE");
        await InsertAsync(1, 0m, level1: "Review", level2: "Email Review", group: "Process/Unclassified", closed: today.AddDays(-5), status: "Open", prefix: "INBOX");

        await Worker.Get<Sac311.Ingestion.AggregateRefresher>().RefreshAsync(null, CancellationToken.None);

        // Headline cells: 11 service requests opened; 165 timed closures (10 after 3 days, 155 in clear-outs, so the median
        // is 400); only the date problem is left out of timing; nothing non-service anywhere.
        var all = await Db.QueryAsync<(int Opened, int Closed, int Excluded, int Bulk, decimal Median)>(
            "SELECT opened_count, closed_count, excluded_count, bulk_closed_count, median_days FROM agg.stats_window WHERE window_days = 30 AND period = 'current' AND neighborhood_slug = '' AND district_number = 0 AND category_group = '';");
        Assert.Equal((11, 165, 1, 155, 400m), (all[0].Opened, all[0].Closed, all[0].Excluded, all[0].Bulk, all[0].Median));
        Assert.Equal(0, await CountAsync("agg.stats_window", "category_group IN ('Other', 'Process/Unclassified')"));
        Assert.Equal(0, await CountAsync("agg.open_backlog"));
        Assert.Equal(0, await CountAsync("agg.backlog_daily", "category_group IN ('Other', 'Process/Unclassified')"));

        var rows = await Db.QueryAsync<ExclusionCheck>(
            """
            SELECT window_days AS WindowDays, period AS Period, kind AS Kind, category_group AS CategoryGroup, label AS Label, day AS Day,
                   opened_count AS Opened, closed_count AS Closed, open_count AS [Open], avg_age_days AS AvgAgeDays
            FROM agg.exclusion WHERE window_days IN (0, 30);
            """);
        Assert.Equal(
            [
                new ExclusionCheck(30, "current", "dq_flag", "", "FutureDate", null, 0, 1, 0, null),
                new ExclusionCheck(30, "current", "non_service", "Other", "Other", null, 2, 2, 0, null),
                new ExclusionCheck(30, "current", "non_service", "Parking", "Parking / General", null, 1, 1, 0, null),
                new ExclusionCheck(30, "current", "non_service", "Process/Unclassified", "Review", null, 1, 0, 0, null),
                new ExclusionCheck(0, "now", "non_service", "Process/Unclassified", "Review", null, 0, 0, 1, null),
            ],
            rows.OrderBy(r => r.Period, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Label, StringComparer.Ordinal));

        // The notes: the Parking day spread over 17:00–17:29, and the Water/Sewer sweep in one minute.
        var clearOuts = await Db.QueryAsync<ClearOutCheck>(
            """
            SELECT day AS Day, category_group AS CategoryGroup, closed_count AS Closed, avg_age_days AS AvgAgeDays,
                   minutes_spanned AS MinutesSpanned, is_sweep AS IsSweep, sweep_categories AS SweepCategories
            FROM agg.clear_out ORDER BY day, category_group;
            """);
        Assert.Equal(
            [
                new ClearOutCheck(today.AddDays(-5).ToDateTime(TimeOnly.MinValue), "Sewer", 25, 200m, 1, true, "Water"),
                new ClearOutCheck(today.AddDays(-5).ToDateTime(TimeOnly.MinValue), "Water", 30, 300m, 1, true, "Sewer"),
                new ClearOutCheck(today.AddDays(-3).ToDateTime(TimeOnly.MinValue), "Parking", 100, 400m, 30, false, null),
            ],
            clearOuts);
    }

    [Fact]
    public async Task An_apply_batch_update_followed_by_a_refresh_restores_the_flag()
    {
        // 100 Parking requests closed today after 200 days: a clear-out.
        for (var i = 0; i < 100; i++)
        {
            var f = FakeFeature.Typical(i, NowUtc);
            f.CategoryLevel1 = "Parking";
            f.CategoryLevel2 = "Meter";
            f.CreatedUtc = NowUtc.AddDays(-200);
            f.Close(NowUtc.AddHours(-1));
            Source.Features.Add(f);
        }

        AssertStatus(RunStatus.Succeeded, await Worker.BackfillAsync());
        Assert.Equal(100, await BulkAsync());

        // An edit in the source: usp_apply_batch overwrites dq_flags on that row, then the refresh sets the bit again.
        Source.Features[0].Address = "915 I ST, SACRAMENTO, 95814";
        Source.Features[0].UpdatedUtc = NowUtc.AddMinutes(-1);
        var run = await Worker.IncrementalAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal(1, run.RowsUpdated);
        Assert.Equal(100, await BulkAsync());
    }
}
