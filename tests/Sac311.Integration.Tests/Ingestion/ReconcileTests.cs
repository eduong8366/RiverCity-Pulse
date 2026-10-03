using Sac311.Data.Ingest;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Ingestion;

public class ReconcileTests(SqlServerFixture db) : DatabaseTest(db)
{
    private Task<int> RemovedAsync() => CountAsync("dbo.service_request", "source_removed_utc IS NOT NULL");

    [Fact]
    public async Task Reconcile_marks_missing_requests_and_fetches_missed_or_stale_ones()
    {
        Source.Seed(30, NowUtc);
        await Worker.BackfillAsync();

        var gone = Source.Features[3];
        Source.Features.Remove(gone);
        // Edited at the source, but no incremental ran.
        var stale = Source.Features[7].Close(NowUtc.AddMinutes(-5));
        // In the source with an old DateUpdated, so no incremental would ever pick it up.
        var missed = FakeFeature.Typical(30, NowUtc);
        Source.Features.Add(missed);

        var run = await Worker.ReconcileAsync();

        AssertStatus(RunStatus.Succeeded, run);
        Assert.Equal(30, run.SourceCount);
        Assert.Equal((1, 0), (run.RowsRemoved, run.RowsRestored));
        Assert.Equal((2, 1, 1), (run.RowsFetched, run.RowsInserted, run.RowsUpdated));
        // Never deleted: the removed request stays, marked.
        Assert.Equal(31, await CountAsync("dbo.service_request"));
        Assert.Equal(1, await RemovedAsync());
        Assert.Equal(1, await CountAsync("dbo.service_request", $"reference_number = '{gone.ReferenceNumber}' AND source_removed_utc IS NOT NULL"));
        Assert.Equal("Closed", await Db.ScalarAsync<string>("SELECT status_group FROM dbo.service_request WHERE reference_number = @r;", new { r = stale.ReferenceNumber }));

        Source.Features.Add(gone);
        var again = await Worker.ReconcileAsync();

        AssertStatus(RunStatus.Succeeded, again);
        Assert.Equal((0, 1, 0), (again.RowsRemoved, again.RowsRestored, again.RowsFetched));
        Assert.Equal(0, await RemovedAsync());
    }

    [Fact]
    public async Task A_request_marked_removed_is_cleared_when_an_incremental_sees_it_again()
    {
        Source.Seed(30, NowUtc);
        await Worker.BackfillAsync();
        var gone = Source.Features[0];
        Source.Features.Remove(gone);
        await Worker.ReconcileAsync();

        Source.Features.Add(gone.Close(NowUtc.AddMinutes(-3)));
        await Worker.IncrementalAsync();

        Assert.Equal(0, await RemovedAsync());
    }

    [Fact]
    public async Task The_95_percent_guard_stops_a_reconcile_of_a_truncated_feed()
    {
        Source.Seed(40, NowUtc);
        await Worker.BackfillAsync();
        AssertStatus(RunStatus.Succeeded, await Worker.ReconcileAsync());

        // 36 of 40 keys: 90%, below the guard.
        Source.Features.RemoveRange(0, 4);
        var run = await Worker.ReconcileAsync();

        AssertStatus(RunStatus.Failed, run);
        Assert.Contains("Reconcile guard", run.Error, StringComparison.Ordinal);
        Assert.Equal(36, run.SourceCount);
        Assert.Equal(0, run.RowsRemoved);
        Assert.Equal(0, await RemovedAsync());
    }

    [Fact]
    public async Task The_guard_compares_with_the_last_successful_reconcile_not_a_failed_one()
    {
        Source.Seed(40, NowUtc);
        await Worker.BackfillAsync();
        await Worker.ReconcileAsync();
        Source.Features.RemoveRange(0, 4);
        await Worker.ReconcileAsync();

        // Still 36: the baseline is still the successful run's 40, so it fails again rather than accepting the drop.
        var run = await Worker.ReconcileAsync();

        AssertStatus(RunStatus.Failed, run);
        Assert.Equal(0, await RemovedAsync());
    }
}
