using Dapper;
using Sac311.Data.Ingest;
using Sac311.Domain;
using Sac311.Ingestion;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Ingestion;

public class RecleanTests(SqlServerFixture db) : DatabaseTest(db)
{
    // Rows loaded under an older cleaner: same source version (row_hash), different cleaned values.
    private async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await using var conn = await Db.OpenAsync();
        return await conn.ExecuteAsync(sql, param);
    }

    private Task<int> CleanWithOldRulesAsync(IEnumerable<FakeFeature> features) => ExecuteAsync(
        "UPDATE dbo.service_request SET address = N'OLD CLEANER' WHERE reference_number IN @refs;",
        new { refs = features.Select(f => f.ReferenceNumber).ToList() });

    private Task<int> OldCleaningAsync() => CountAsync("dbo.service_request", "address = N'OLD CLEANER'");

    [Fact]
    public async Task A_changed_seed_mapping_reaches_exactly_the_affected_rows_without_history()
    {
        Source.Seed(12, NowUtc);
        foreach (var f in Source.Features.Take(3))
        {
            f.CategoryLevel1 = "Reclean Test Category";
        }

        await Worker.BackfillAsync();
        Assert.Equal(3, await CountAsync("dbo.service_request", $"category_group = N'Unmapped' AND dq_flags & {(int)DqFlags.UnmappedCategory} <> 0"));
        var history = await CountAsync("dbo.service_request_history");
        var changed = await Db.QueryAsync<DateTime>("SELECT last_changed_utc FROM dbo.service_request ORDER BY reference_number;");

        await ExecuteAsync("INSERT INTO ref.category_map (category_key, source_value, category_group) VALUES ('recleantestcategory', N'Reclean Test Category', N'Streets');");
        try
        {
            var run = await Worker.RecleanAsync();

            AssertStatus(RunStatus.Succeeded, run);
            Assert.Equal(Pipeline.Reclean, run.Pipeline);
            Assert.Equal((12, 3, 0, 0, 9, 0), (run.RowsFetched, run.RowsRecleaned, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsHistory));
            Assert.Equal(3, await CountAsync("dbo.service_request", $"category_group = N'Streets' AND dq_flags & {(int)DqFlags.UnmappedCategory} = 0"));
            Assert.Equal(0, await CountAsync("dbo.service_request", "category_group = N'Unmapped'"));
            Assert.Equal(history, await CountAsync("dbo.service_request_history"));
            // Not a source change, so last_changed_utc stays.
            Assert.Equal(changed, await Db.QueryAsync<DateTime>("SELECT last_changed_utc FROM dbo.service_request ORDER BY reference_number;"));
            // It ends with an aggregate refresh under its own run.
            Assert.Equal(1, await CountAsync("agg.refresh", $"run_id = {run.RunId}"));
        }
        finally
        {
            await ExecuteAsync("DELETE FROM ref.category_map WHERE category_key = 'recleantestcategory';");
        }
    }

    [Fact]
    public async Task A_second_reclean_changes_nothing()
    {
        Source.Seed(12, NowUtc);
        await Worker.BackfillAsync();
        await CleanWithOldRulesAsync(Source.Features.Skip(2).Take(4));

        var first = await Worker.RecleanAsync();
        var history = await CountAsync("dbo.service_request_history");
        var second = await Worker.RecleanAsync();

        Assert.Equal((4, 8), (first.RowsRecleaned, first.RowsUnchanged));
        AssertStatus(RunStatus.Succeeded, second);
        Assert.Equal((12, 0, 0, 0, 12, 0), (second.RowsFetched, second.RowsRecleaned, second.RowsInserted, second.RowsUpdated, second.RowsUnchanged, second.RowsHistory));
        Assert.Equal(0, await OldCleaningAsync());
        Assert.Equal(history, await CountAsync("dbo.service_request_history"));
        // Each finished reclean replaces the previous one's raw pages: 12 rows in pages of 5.
        Assert.Equal(3, await CountAsync("raw.page", $"pipeline = '{Pipeline.Reclean}'"));
        Assert.Equal(3, await CountAsync("raw.page", $"pipeline = '{Pipeline.Reclean}' AND run_id = {second.RunId}"));
    }

    [Fact]
    public async Task Labels_set_after_the_fact_are_not_a_cleaning_difference()
    {
        Source.Seed(6, NowUtc);
        await Worker.BackfillAsync();
        await ExecuteAsync($"UPDATE dbo.service_request SET dq_flags |= {(int)DqFlags.BulkClosure} WHERE reference_number = @r;",
            new { r = Source.Features[1].ReferenceNumber });

        var run = await Worker.RecleanAsync();

        Assert.Equal((0, 6), (run.RowsRecleaned, run.RowsUnchanged));
    }

    [Fact]
    public async Task A_source_edit_during_a_reclean_is_an_ordinary_update_with_history()
    {
        Source.Seed(8, NowUtc);
        await Worker.BackfillAsync();
        await CleanWithOldRulesAsync([Source.Features[2]]);
        Source.Features[5].Close(NowUtc.AddMinutes(-10));

        var run = await Worker.RecleanAsync();

        Assert.Equal((1, 1, 1, 6), (run.RowsRecleaned, run.RowsUpdated, run.RowsHistory, run.RowsUnchanged));
        Assert.Equal(2, await CountAsync("dbo.service_request_history", $"reference_number = '{Source.Features[5].ReferenceNumber}'"));
    }

    [Fact]
    public async Task Requests_the_source_no_longer_serves_keep_their_old_cleaning()
    {
        Source.Seed(8, NowUtc);
        await Worker.BackfillAsync();
        var gone = Source.Features[3];
        await CleanWithOldRulesAsync([gone, Source.Features[4]]);
        Source.Features.Remove(gone);

        var run = await Worker.RecleanAsync();

        Assert.Equal((7, 1), (run.RowsFetched, run.RowsRecleaned));
        Assert.Equal(1, await CountAsync("dbo.service_request", $"reference_number = '{gone.ReferenceNumber}' AND address = N'OLD CLEANER'"));
    }

    [Fact]
    public async Task A_reclean_resumed_after_a_failure_matches_a_clean_run()
    {
        Source.Seed(23, NowUtc);
        await Worker.BackfillAsync();
        // One old-cleaned row on each of the five pages.
        await CleanWithOldRulesAsync(Source.Features.Where((_, i) => i % TestPipeline.PageSize == 1));

        Source.FailPagesAfter = 2;
        var failed = await Worker.RecleanAsync();
        var checkpoint = await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Reclean, CancellationToken.None);
        var leftAfterFailure = await OldCleaningAsync();
        Source.FailPagesAfter = null;
        var resumed = await Worker.RecleanAsync();

        AssertStatus(RunStatus.Failed, failed);
        Assert.Contains("injected failure", failed.Error, StringComparison.Ordinal);
        Assert.Equal((2 * TestPipeline.PageSize, 2), (failed.RowsFetched, failed.RowsRecleaned));
        Assert.Equal(Source.Features[(2 * TestPipeline.PageSize) - 1].ObjectId, checkpoint!.LastObjectId);
        Assert.Equal(3, leftAfterFailure);

        AssertStatus(RunStatus.Succeeded, resumed);
        Assert.Equal((23 - (2 * TestPipeline.PageSize), 3, 0), (resumed.RowsFetched, resumed.RowsRecleaned, resumed.RowsHistory));
        Assert.Equal(0, await OldCleaningAsync());
        Assert.Equal(23, await CountAsync("dbo.service_request_history"));
        Assert.Null((await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Reclean, CancellationToken.None))!.LastObjectId);
        // The resumed run kept the failed run's pages: together they are one full copy of the feed.
        Assert.Equal(5, await CountAsync("raw.page", $"pipeline = '{Pipeline.Reclean}'"));
    }

    [Fact]
    public async Task A_reclean_started_while_another_job_holds_the_lock_is_skipped()
    {
        Source.Seed(5, NowUtc);
        await Worker.BackfillAsync();
        Source.Queries.Clear();

        await using (await Worker.Get<IngestLock>().TryAcquireAsync(CancellationToken.None))
        {
            var run = await Worker.RecleanAsync();

            Assert.Equal(RunStatus.Skipped, run.Status);
            Assert.Equal(Pipeline.Reclean, run.Pipeline);
        }

        Assert.Empty(Source.Queries);
        Assert.Equal(0, await CountAsync("raw.page", $"pipeline = '{Pipeline.Reclean}'"));
    }

    [Fact]
    public async Task Backfill_and_incremental_leave_cleaning_differences_alone()
    {
        Source.Seed(6, NowUtc);
        await Worker.BackfillAsync();
        await CleanWithOldRulesAsync(Source.Features.Take(2));

        var backfill = await Worker.BackfillAsync();

        Assert.Equal((0, 6), (backfill.RowsRecleaned, backfill.RowsUnchanged));
        Assert.Equal(2, await OldCleaningAsync());
    }
}
