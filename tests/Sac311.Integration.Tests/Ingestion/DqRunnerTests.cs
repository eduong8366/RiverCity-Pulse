using Sac311.Data.Quality;
using Sac311.Integration.Tests.Fakes;

namespace Sac311.Integration.Tests.Ingestion;

public class DqRunnerTests(SqlServerFixture db) : DatabaseTest(db)
{
    private sealed record Row(long? RunId, string CheckName, string Status, decimal? ObservedValue, decimal? BaselineValue);

    private Task<List<Row>> ResultsAsync(long runId) => Db.QueryAsync<Row>(
        "SELECT run_id AS RunId, check_name AS CheckName, status AS Status, observed_value AS ObservedValue, baseline_value AS BaselineValue FROM ops.dq_result WHERE run_id = @runId;",
        new { runId });

    [Fact]
    public async Task Every_successful_run_records_its_checks()
    {
        Source.Seed(20, NowUtc);

        var first = await ResultsAsync((await Worker.BackfillAsync()).RunId);
        var second = await ResultsAsync((await Worker.IncrementalAsync()).RunId);

        var names = first.Select(r => r.CheckName).ToHashSet();
        Assert.Contains("source_count", names);
        Assert.Contains("null_rate.address", names);
        Assert.Contains("null_rate.neighborhood", names);
        Assert.Contains("flag.SentinelDate", names);
        Assert.Contains("flag.UnmappedCategory", names);
        Assert.Contains("rejects", names);
        Assert.Equal(DqStore.TrackedFlags.Count, names.Count(n => n.StartsWith("flag.", StringComparison.Ordinal)));

        // Nothing to compare with the first time; the second run compares with the first.
        Assert.Equal(DqStatus.Info, first.Single(r => r.CheckName == "source_count").Status);
        Assert.Equal(DqStatus.Info, first.Single(r => r.CheckName == "flag.SentinelDate").Status);
        Assert.All(second.Where(r => r.CheckName == "source_count" || r.CheckName.StartsWith("flag.", StringComparison.Ordinal)),
            r => Assert.Equal(DqStatus.Pass, r.Status));
        Assert.Equal(20m, second.Single(r => r.CheckName == "source_count").BaselineValue);
    }

    [Fact]
    public async Task A_failed_or_skipped_run_records_no_checks()
    {
        Source.Seed(5, NowUtc);
        Source.RenamedField = ("DateUpdated", "LastEdited");

        await Worker.BackfillAsync();

        Assert.Equal(0, await CountAsync("ops.dq_result"));
    }

    [Fact]
    public async Task A_source_count_drop_of_more_than_2_percent_fails()
    {
        Source.Seed(50, NowUtc);
        await Worker.BackfillAsync();
        Source.Features.RemoveRange(0, 2);

        var results = await ResultsAsync((await Worker.IncrementalAsync()).RunId);

        var check = results.Single(r => r.CheckName == "source_count");
        Assert.Equal(DqStatus.Fail, check.Status);
        Assert.Equal((48m, 50m), (check.ObservedValue, check.BaselineValue));
    }

    [Fact]
    public async Task A_jump_in_recent_null_addresses_warns()
    {
        Source.Seed(40, NowUtc);
        for (var i = 0; i < 40; i++)
        {
            var recent = FakeFeature.Typical(100 + i, NowUtc);
            recent.CreatedUtc = NowUtc.AddDays(-2);
            recent.UpdatedUtc = NowUtc.AddDays(-2);
            recent.Address = "";
            Source.Features.Add(recent);
        }

        var results = await ResultsAsync((await Worker.BackfillAsync()).RunId);

        var address = results.Single(r => r.CheckName == "null_rate.address");
        Assert.Equal(DqStatus.Warn, address.Status);
        Assert.Equal((100m, 0m), (address.ObservedValue, address.BaselineValue));
        Assert.Equal(DqStatus.Pass, results.Single(r => r.CheckName == "null_rate.zip").Status);
    }

    [Fact]
    public async Task One_address_with_100_new_requests_in_a_week_warns_and_99_passes()
    {
        FakeFeature Recent(int i, DateTime updatedUtc)
        {
            var f = FakeFeature.Typical(i, NowUtc);
            f.CreatedUtc = NowUtc.AddDays(-2);
            f.UpdatedUtc = updatedUtc;
            f.Address = "6005 WARDELL WAY";
            return f;
        }

        Source.Features.AddRange(Enumerable.Range(0, 99).Select(i => Recent(i, NowUtc.AddDays(-2))));
        var at99 = (await ResultsAsync((await Worker.BackfillAsync()).RunId)).Single(r => r.CheckName == "repeat_address");

        Source.Features.Add(Recent(99, NowUtc.AddMinutes(-1)));
        var at100 = (await ResultsAsync((await Worker.IncrementalAsync()).RunId)).Single(r => r.CheckName == "repeat_address");

        Assert.Equal((DqStatus.Pass, 99m), (at99.Status, at99.ObservedValue));
        Assert.Equal((DqStatus.Warn, 100m), (at100.Status, at100.ObservedValue));
    }

    [Fact]
    public async Task Rejected_rows_warn()
    {
        Source.Seed(6, NowUtc);
        Source.Features[2].ReferenceNumber = " ";

        var run = await Worker.BackfillAsync();
        var results = await ResultsAsync(run.RunId);

        Assert.Equal(1, run.RowsRejected);
        var rejects = results.Single(r => r.CheckName == "rejects");
        Assert.Equal((DqStatus.Warn, 1m), (rejects.Status, rejects.ObservedValue));
    }
}
