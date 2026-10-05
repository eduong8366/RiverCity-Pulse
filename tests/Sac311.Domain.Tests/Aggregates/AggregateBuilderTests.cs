using Sac311.Domain.Aggregates;
using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Aggregates;

public class AggregateBuilderTests
{
    // 13:00 in Sacramento (PDT), so the as-of date is 2026-10-02.
    private static readonly DateTime AsOfUtc = new(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static readonly AggregateCell Everything = new(AggregateCell.All, AggregateCell.AllDistricts, AggregateCell.All);
    private static readonly AggregateCell Downtown = new("downtown", AggregateCell.AllDistricts, AggregateCell.All);
    private static readonly AggregateCell DowntownStreetsD4 = new("downtown", 4, "Streets");

    private static AggregateRequest Closed(
        int closedDaysAgo, decimal days, string? slug = "downtown", byte? district = 4, string category = "Streets", bool eligible = true)
    {
        var closed = Today.AddDays(-closedDaysAgo);
        var created = closed.AddDays(-(int)days);
        return new AggregateRequest(slug, district, category, StatusGroup.Closed,
            created.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), created, closed, closed, days, eligible);
    }

    private static AggregateRequest Open(int createdDaysAgo, string? slug = "downtown", byte? district = 4, string category = "Streets") =>
        new(slug, district, category, StatusGroup.Open, AsOfUtc.AddDays(-createdDaysAgo), Pacific.ToLocalDate(AsOfUtc.AddDays(-createdDaysAgo)),
            null, null, null, true);

    private static AggregateSet Build(params AggregateRequest[] requests)
    {
        var builder = new AggregateBuilder(AsOfUtc);
        foreach (var r in requests)
        {
            builder.Add(r);
        }

        return builder.Build();
    }

    private static WindowStats? Stats(AggregateSet set, int window, string period, AggregateCell cell) =>
        set.Windows.SingleOrDefault(s => s.WindowDays == window && s.Period == period && s.Cell == cell);

    [Fact]
    public void As_of_date_is_the_sacramento_date()
    {
        var set = Build(Closed(0, 1m), Open(3));
        Assert.Equal(Today, set.AsOfDate);
        Assert.Equal(AsOfUtc, set.AsOfUtc);
        Assert.Equal(2, set.RequestCount);
    }

    [Fact]
    public void Current_period_is_the_last_n_days_through_today_and_prior_the_n_before()
    {
        var set = Build(Closed(0, 1m), Closed(29, 2m), Closed(30, 3m), Closed(59, 4m), Closed(60, 5m));

        Assert.Equal(2, Stats(set, 30, AggregatePeriod.Current, Everything)!.Closed);
        Assert.Equal(2, Stats(set, 30, AggregatePeriod.Prior, Everything)!.Closed);
        Assert.Equal(5, Stats(set, 90, AggregatePeriod.Current, Everything)!.Closed);
        Assert.Null(Stats(set, 90, AggregatePeriod.Prior, Everything));
    }

    [Fact]
    public void Median_and_p90_are_over_requests_closed_in_the_period()
    {
        var set = Build(Closed(1, 1m), Closed(2, 2m), Closed(3, 3m), Closed(4, 10m), Closed(40, 100m));

        var current = Stats(set, 30, AggregatePeriod.Current, DowntownStreetsD4)!;
        Assert.Equal((4, 2.5m, 7.9m), (current.Closed, current.MedianDays, current.P90Days));
        var prior = Stats(set, 30, AggregatePeriod.Prior, DowntownStreetsD4)!;
        Assert.Equal((1, 100m, 100m), (prior.Closed, prior.MedianDays, prior.P90Days));
    }

    [Fact]
    public void Every_grouping_set_gets_a_cell()
    {
        var set = Build(Closed(1, 1m));

        var cells = set.Windows.Where(s => s.WindowDays == 30 && s.Period == AggregatePeriod.Current).Select(s => s.Cell).ToHashSet();
        Assert.Equal(8, cells.Count);
        Assert.Contains(Everything, cells);
        Assert.Contains(DowntownStreetsD4, cells);
        Assert.Contains(new AggregateCell(AggregateCell.All, 4, "Streets"), cells);
        Assert.Contains(new AggregateCell("downtown", 4, AggregateCell.All), cells);
    }

    [Fact]
    public void Requests_without_neighborhood_or_district_only_count_in_all_cells()
    {
        var set = Build(Closed(1, 1m), Closed(1, 5m, slug: null, district: null));

        var current = set.Windows.Where(s => s.WindowDays == 30 && s.Period == AggregatePeriod.Current).ToList();
        Assert.Equal(2, current.Single(s => s.Cell == Everything).Closed);
        Assert.Equal(2, current.Single(s => s.Cell == new AggregateCell(AggregateCell.All, AggregateCell.AllDistricts, "Streets")).Closed);
        Assert.Equal(1, current.Single(s => s.Cell == Downtown).Closed);
        Assert.Equal(1, current.Single(s => s.Cell == new AggregateCell(AggregateCell.All, 4, AggregateCell.All)).Closed);
        Assert.Equal(3m, current.Single(s => s.Cell == Everything).MedianDays);
    }

    [Fact]
    public void Flagged_closed_requests_are_excluded_not_counted()
    {
        // Closed without a close date: it left the backlog (on its last update) but has no days to close.
        var missingDate = Closed(5, 0m) with { ClosedLocal = null, DaysToClose = null, IsMetricEligible = false };
        var set = Build(Closed(1, 2m), missingDate, Closed(2, 9m, eligible: false));

        var s = Stats(set, 30, AggregatePeriod.Current, Downtown)!;
        Assert.Equal((1, 2, 2m), (s.Closed, s.Excluded, s.MedianDays));
    }

    [Fact]
    public void Bulk_closures_are_excluded_and_counted_as_bulk_closed()
    {
        var bulk = Closed(3, 400m, eligible: false) with { IsBulkClosure = true };
        var set = Build(Closed(1, 2m), Closed(2, 4m), bulk, Closed(5, 0m) with { ClosedLocal = null, DaysToClose = null, IsMetricEligible = false });

        var s = Stats(set, 30, AggregatePeriod.Current, Downtown)!;
        Assert.Equal((2, 2, 1, 3m, 3.8m), (s.Closed, s.Excluded, s.BulkClosed, s.MedianDays, s.P90Days));
        Assert.Equal(1, Stats(set, 30, AggregatePeriod.Current, Everything)!.BulkClosed);

        // A clear-out still empties the backlog: the bulk closure isn't open.
        Assert.DoesNotContain(set.Open, o => o.Cell == Downtown);
    }

    [Fact]
    public void Non_service_requests_are_left_out_of_every_figure_but_the_request_count()
    {
        var info = Closed(1, 0.01m) with { IsService = false };
        var inbox = Open(3) with { IsService = false };
        var set = Build(Closed(2, 4m), info, inbox);

        Assert.Equal(3, set.RequestCount);
        var s = Stats(set, 30, AggregatePeriod.Current, Everything)!;
        Assert.Equal((1, 1, 0, 4m), (s.Opened, s.Closed, s.Excluded, s.MedianDays));
        Assert.Empty(set.Open);
        Assert.Equal(1, set.Backlog.Where(b => b.CategoryGroup == AggregateCell.All && b.DistrictNumber == AggregateCell.AllDistricts).Sum(b => b.Opened));
    }

    [Fact]
    public void Opened_counts_by_created_date()
    {
        var set = Build(Open(1), Open(10), Open(45), Closed(0, 50m));

        Assert.Equal(2, Stats(set, 30, AggregatePeriod.Current, Everything)!.Opened);
        Assert.Equal(2, Stats(set, 30, AggregatePeriod.Prior, Everything)!.Opened);
    }

    [Fact]
    public void Period_with_only_openings_has_no_median()
    {
        var s = Stats(Build(Open(1)), 30, AggregatePeriod.Current, Everything)!;
        Assert.Equal((1, 0, null, null), (s.Opened, s.Closed, s.MedianDays, s.P90Days));
    }

    [Fact]
    public void Open_snapshot_counts_open_requests_and_their_median_age()
    {
        var set = Build(Open(2), Open(4), Open(10), Open(7, slug: "oak-park"), Closed(1, 3m));

        Assert.Equal(new OpenStats(Downtown, 3, 4m), set.Open.Single(o => o.Cell == Downtown));
        Assert.Equal(new OpenStats(Everything, 4, 5.5m), set.Open.Single(o => o.Cell == Everything));
    }

    [Fact]
    public void Backlog_series_starts_2024_with_requests_still_open_from_before()
    {
        var before = new AggregateRequest("downtown", 4, "Streets", StatusGroup.Closed, new DateTime(2023, 12, 15, 17, 0, 0, DateTimeKind.Utc),
            new DateOnly(2023, 12, 15), new DateOnly(2024, 1, 3), new DateOnly(2024, 1, 3), 19m, true);
        var after = new AggregateRequest("downtown", null, "Streets", StatusGroup.Open, new DateTime(2024, 1, 2, 17, 0, 0, DateTimeKind.Utc),
            new DateOnly(2024, 1, 2), null, null, null, true);
        var set = Build(before, after);

        var all = set.Backlog.Where(b => b.DistrictNumber == AggregateCell.AllDistricts && b.CategoryGroup == AggregateCell.All)
            .ToDictionary(b => b.Day);
        Assert.Equal(Today.DayNumber - AggregateBuilder.BacklogStart.DayNumber + 1, all.Count);
        Assert.Equal(new BacklogDay(new DateOnly(2024, 1, 1), 0, "", 0, 0, 1), all[new DateOnly(2024, 1, 1)]);
        Assert.Equal(new BacklogDay(new DateOnly(2024, 1, 2), 0, "", 1, 0, 2), all[new DateOnly(2024, 1, 2)]);
        Assert.Equal(new BacklogDay(new DateOnly(2024, 1, 3), 0, "", 0, 1, 1), all[new DateOnly(2024, 1, 3)]);
        Assert.Equal(1, all[Today].Open);
        Assert.Equal(set.Open.Single(o => o.Cell == Everything).Open, all[Today].Open);

        // The request with no district is only in the all-district series.
        var district4 = set.Backlog.Where(b => b.DistrictNumber == 4 && b.CategoryGroup == AggregateCell.All).ToDictionary(b => b.Day);
        Assert.Equal(0, district4[new DateOnly(2024, 1, 2)].Opened);
        Assert.Equal(0, district4[Today].Open);
        Assert.Equal(4, set.Backlog.Select(b => (b.DistrictNumber, b.CategoryGroup)).Distinct().Count());
    }
}
