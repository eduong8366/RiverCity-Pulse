using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class ResolutionTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 21, 0, 0, DateTimeKind.Utc);

    private static ResolutionResult Compute(SourceRow r) => Resolution.Compute(
        Status.Group(r.PublicStatus).Value,
        EsriDate.ToUtc(r.DateCreated, Now).Value,
        EsriDate.ToUtc(r.DateUpdated, Now).Value,
        EsriDate.ToUtc(r.DateClosed, Now).Value);

    [Fact]
    public void Real_closed_row_gets_fractional_days_and_a_local_close_date()
    {
        // Created 2024-01-01 00:02 UTC, closed 2024-01-10 22:30 UTC: 9.936 days.
        var result = Compute(Fixture.Real("typical")[0]);
        Assert.Equal(new ResolutionResult(9.94m, new DateOnly(2024, 1, 10), DqFlags.None), result);
    }

    [Fact]
    public void Real_close_before_create_is_flagged_and_has_no_days()
    {
        Assert.All(Fixture.Real("closed-before-created"), r =>
        {
            var result = Compute(r);
            Assert.True(result.Flags.HasFlag(DqFlags.InvalidCloseOrder));
            Assert.Null(result.DaysToClose);
            // It still leaves the backlog, but never before the day it was created.
            Assert.Equal(Pacific.ToLocalDate(EsriDate.ToUtc(r.DateCreated, Now).Value), result.BacklogCloseDateLocal);
        });
    }

    [Fact]
    public void Real_closed_without_a_date_is_flagged_and_leaves_the_backlog_when_last_updated()
    {
        Assert.All(Fixture.Real("closed-missing-date"), r =>
        {
            var result = Compute(r);
            Assert.Equal(DqFlags.ClosedMissingDate, result.Flags);
            Assert.Null(result.DaysToClose);
            var updated = Pacific.ToLocalDate(EsriDate.ToUtc(r.DateUpdated, Now).Value)!.Value;
            var created = Pacific.ToLocalDate(EsriDate.ToUtc(r.DateCreated, Now).Value)!.Value;
            Assert.Equal(updated > created ? updated : created, result.BacklogCloseDateLocal);
        });
    }

    [Fact]
    public void Real_open_rows_stay_in_the_backlog()
    {
        var open = Fixture.Real("typical").Where(r => r.PublicStatus is "NEW" or "IN PROGRESS").ToList();
        Assert.Equal(2, open.Count);
        Assert.All(open, r => Assert.Equal(new ResolutionResult(null, null, DqFlags.None), Compute(r)));
    }

    [Fact]
    public void Real_cancelled_row_leaves_the_backlog_without_days()
    {
        var result = Compute(Fixture.Real("typical").Single(r => r.PublicStatus == "CANCELLED"));
        Assert.Null(result.DaysToClose);
        Assert.NotNull(result.BacklogCloseDateLocal);
        Assert.Equal(DqFlags.None, result.Flags & DqFlags.MetricExclusions);
    }

    [Fact]
    public void Same_minute_close_is_zero_days()
    {
        var t = new DateTime(2025, 5, 1, 17, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new ResolutionResult(0m, new DateOnly(2025, 5, 1), DqFlags.None), Resolution.Compute(StatusGroup.Closed, t, t, t));
    }

    [Fact]
    public void Unknown_status_leaves_the_backlog_only_with_a_close_date()
    {
        var created = new DateTime(2025, 5, 1, 17, 0, 0, DateTimeKind.Utc);
        Assert.Null(Resolution.Compute(StatusGroup.Unknown, created, created, null).BacklogCloseDateLocal);
        Assert.Equal(new DateOnly(2025, 5, 3), Resolution.Compute(StatusGroup.Unknown, created, created, created.AddDays(2)).BacklogCloseDateLocal);
    }
}
