using Microsoft.Extensions.Logging.Abstractions;
using Sac311.Data.Ingest;
using Sac311.Data.Migrations;
using Sac311.Ingestion;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Ingestion;

public class IngestionTests(SqlServerFixture db) : DatabaseTest(db)
{
    private sealed record Version(string ReferenceNumber, byte[] RowHash, int DqFlags, string StatusGroup);

    private Task<List<Version>> SnapshotAsync() => Db.QueryAsync<Version>(
        "SELECT reference_number AS ReferenceNumber, row_hash AS RowHash, dq_flags AS DqFlags, status_group AS StatusGroup FROM dbo.service_request ORDER BY reference_number;");

    [Fact]
    public async Task Migrations_apply_again_without_changes()
    {
        var objects = await CountAsync("sys.objects", "is_ms_shipped = 0");
        var journal = await CountAsync("dbo.schema_version");

        var again = DatabaseMigrator.Migrate(Db.ConnectionString, NullLogger.Instance);

        Assert.NotEmpty(Db.FirstMigration!.Migrations);
        Assert.Empty(again.Migrations);
        Assert.Equal(journal, await CountAsync("dbo.schema_version"));
        Assert.Equal(objects, await CountAsync("sys.objects", "is_ms_shipped = 0"));
    }

    [Fact]
    public async Task A_second_backfill_changes_nothing()
    {
        Source.Seed(23, NowUtc);

        var first = await Worker.BackfillAsync();
        var afterFirst = await SnapshotAsync();
        var second = await Worker.BackfillAsync();

        AssertStatus(RunStatus.Succeeded, first);
        Assert.Equal((23, 23, 0, 23), (first.RowsFetched, first.RowsInserted, first.RowsUpdated, first.RowsHistory));
        AssertStatus(RunStatus.Succeeded, second);
        Assert.Equal((23, 0, 0, 23, 0), (second.RowsFetched, second.RowsInserted, second.RowsUpdated, second.RowsUnchanged, second.RowsHistory));
        Assert.Equal(afterFirst, await SnapshotAsync(), new VersionComparer());
        Assert.Equal(23, await CountAsync("dbo.service_request_history"));
        // 23 rows in pages of 5: five pages per run.
        Assert.Equal(10, await CountAsync("raw.page"));
    }

    [Fact]
    public async Task A_changed_row_gives_one_update_and_one_history_row()
    {
        Source.Seed(12, NowUtc);
        await Worker.BackfillAsync();
        var changed = Source.Features[4].Close(NowUtc.AddMinutes(-10));

        var run = await Worker.IncrementalAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal((1, 0, 1, 0, 1), (run.RowsFetched, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsHistory));
        Assert.Equal("Closed", await Db.ScalarAsync<string>(
            "SELECT status_group FROM dbo.service_request WHERE reference_number = @r;", new { r = changed.ReferenceNumber }));
        Assert.Equal(2, await CountAsync("dbo.service_request_history", $"reference_number = '{changed.ReferenceNumber}'"));
    }

    [Fact]
    public async Task Rows_inside_the_watermark_overlap_count_as_unchanged()
    {
        Source.Seed(10, NowUtc);
        // Edited 90 minutes ago: before the seeded watermark (backfill start − 60 min) but inside its 60-minute overlap.
        foreach (var f in Source.Features.Take(3))
        {
            f.UpdatedUtc = NowUtc.AddMinutes(-90);
        }

        await Worker.BackfillAsync();
        var watermark = (await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Incremental, CancellationToken.None))!.WatermarkUtc;

        var run = await Worker.IncrementalAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal((3, 0, 0, 3, 0), (run.RowsFetched, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsHistory));
        // Rows older than the watermark never move it back.
        Assert.Equal(watermark, (await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Incremental, CancellationToken.None))!.WatermarkUtc);
    }

    [Fact]
    public async Task Incremental_loads_new_rows_and_moves_the_watermark_to_the_newest_DateUpdated()
    {
        Source.Seed(10, NowUtc);
        await Worker.BackfillAsync();
        var added = FakeFeature.Typical(10, NowUtc);
        added.UpdatedUtc = new DateTime(NowUtc.Ticks - (NowUtc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddMinutes(-2);
        Source.Features.Add(added);

        var run = await Worker.IncrementalAsync();

        Assert.Equal((1, 1, 1), (run.RowsFetched, run.RowsInserted, run.RowsHistory));
        Assert.Equal(added.UpdatedUtc, (await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Incremental, CancellationToken.None))!.WatermarkUtc);
        Assert.Equal(11, await CountAsync("dbo.service_request"));
    }

    [Fact]
    public async Task A_backfill_resumed_after_a_failure_matches_a_clean_run()
    {
        Source.Seed(23, NowUtc);
        Source.Features[7].Close(NowUtc.AddDays(-3));
        await Worker.BackfillAsync();
        var clean = await SnapshotAsync();
        await Db.ResetAsync();

        Source.FailPagesAfter = 2;
        var failed = await Worker.BackfillAsync();
        var checkpoint = await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Backfill, CancellationToken.None);
        Source.FailPagesAfter = null;
        var resumed = await Worker.BackfillAsync();

        AssertStatus(RunStatus.Failed, failed);
        Assert.Contains("injected failure", failed.Error, StringComparison.Ordinal);
        Assert.Equal(2 * TestPipeline.PageSize, failed.RowsInserted);
        Assert.Equal(Source.Features[(2 * TestPipeline.PageSize) - 1].ObjectId, checkpoint!.LastObjectId);

        AssertStatus(RunStatus.Succeeded, resumed);
        Assert.Equal(23 - (2 * TestPipeline.PageSize), resumed.RowsFetched);
        Assert.Equal(clean, await SnapshotAsync(), new VersionComparer());
        Assert.Equal(23, await CountAsync("dbo.service_request_history"));
        Assert.Null((await Worker.Get<CheckpointStore>().GetAsync(Pipeline.Backfill, CancellationToken.None))!.LastObjectId);
    }

    [Fact]
    public async Task Schema_drift_stops_the_run_before_any_row_is_written()
    {
        Source.Seed(8, NowUtc);
        Source.RenamedField = ("PublicStatus", "Status");

        var run = await Worker.BackfillAsync();

        AssertStatus(RunStatus.SchemaDrift, run);
        Assert.Contains("missing PublicStatus", run.Error, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("dbo.service_request"));
        Assert.Equal(0, await CountAsync("dbo.service_request_history"));
        Assert.Equal(0, await CountAsync("raw.page"));
        Assert.Empty(Source.Queries);
    }

    [Fact]
    public async Task A_200_response_carrying_an_error_is_retried()
    {
        Source.Seed(8, NowUtc);
        Source.TransientErrors = 2;

        var run = await Worker.BackfillAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal(8, run.RowsInserted);
        // The count query was answered twice with an error and retried each time (MaxRetries is 2).
        Assert.Equal(3, Source.Queries.Count(q => q.GetValueOrDefault("returnCountOnly") == "true" && q["where"].EndsWith("OBJECTID > 0", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_run_started_while_another_holds_the_lock_is_recorded_as_skipped()
    {
        Source.Seed(5, NowUtc);
        await using (await Worker.Get<IngestLock>().TryAcquireAsync(CancellationToken.None))
        {
            var backfill = await Worker.BackfillAsync();
            var incremental = await Worker.IncrementalAsync();
            var reconcile = await Worker.ReconcileAsync();

            Assert.All(new[] { backfill, incremental, reconcile }, r => Assert.Equal(RunStatus.Skipped, r.Status));
        }

        Assert.Empty(Source.Queries);
        Assert.Equal(3, await CountAsync("ops.ingest_run", "status = 'Skipped'"));
        Assert.Equal(0, await CountAsync("dbo.service_request"));

        // Released: the next run goes ahead.
        AssertStatus(RunStatus.Succeeded, await Worker.BackfillAsync());
    }

    private sealed class VersionComparer : IEqualityComparer<Version>
    {
        public bool Equals(Version? x, Version? y) =>
            x is not null && y is not null && x.ReferenceNumber == y.ReferenceNumber && x.RowHash.AsSpan().SequenceEqual(y.RowHash)
            && x.DqFlags == y.DqFlags && x.StatusGroup == y.StatusGroup;

        public int GetHashCode(Version obj) => obj.ReferenceNumber.GetHashCode(StringComparison.Ordinal);
    }
}
