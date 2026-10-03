using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Sac311.Api.Data;
using Sac311.Api.Tests.Support;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Tests;

/// <summary>The endpoints over the <see cref="SeededApi"/> scenario. Expected figures are worked out by hand from it.</summary>
public class StatsEndpointTests(SeededApi api) : IClassFixture<SeededApi>
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly HttpClient _client = api.Factory.CreateClient();

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Neighborhood_median_and_p90_match_the_hand_computed_values()
    {
        var body = await GetAsync<NeighborhoodStatsResponse>("/api/neighborhoods/downtown/stats?window=30");

        // Closed in the last 30 days after 1, 2, 3 and 10 days: median (2 + 3) / 2 = 2.5; p90 at position 0.9 × 3 = 2.7:
        // 3 + 0.7 × (10 − 3) = 7.9. The one closed without a date is excluded. Opened: created 2, 2, 4, 4, 6, 8 and 14 days ago.
        Assert.Equal(new Neighborhood("downtown", "Downtown"), body.Neighborhood);
        Assert.Equal(30, body.WindowDays);
        Assert.Null(body.Category);
        Assert.Equal(new PeriodStats(new DateOnly(2026, 9, 3), Today, 7, 4, 1, 2.5m, 7.9m), body.Stats.Current);
        // The prior 30 days: the one closed 40 days ago after 100 days.
        Assert.Equal(new PeriodStats(new DateOnly(2026, 8, 4), new DateOnly(2026, 9, 2), 0, 1, 0, 100m, 100m), body.Stats.Prior);
        // Fewer than 30 closed in each period: no trend.
        Assert.Null(body.Stats.Trend);
        // Open requests created 2 and 4 days before the as-of time.
        Assert.Equal((2, 3m), (body.Stats.OpenBacklog, body.Stats.MedianOpenAgeDays));
        Assert.Equal(SeededApi.AsOfUtc, body.AsOf);
    }

    [Fact]
    public async Task Category_filter_matches_any_case_and_reports_the_stored_name()
    {
        var body = await GetAsync<NeighborhoodStatsResponse>("/api/neighborhoods/DOWNTOWN/stats?window=30&category=streets");

        Assert.Equal("Streets", body.Category);
        Assert.Equal((4, 2.5m), (body.Stats.Current.Closed, body.Stats.Current.MedianDays));

        var other = await GetAsync<NeighborhoodStatsResponse>("/api/neighborhoods/downtown/stats?window=30&category=Water");
        Assert.Equal((0, (decimal?)null, 0), (other.Stats.Current.Closed, other.Stats.Current.MedianDays, other.Stats.OpenBacklog));
    }

    [Fact]
    public async Task Trend_compares_medians_when_both_periods_have_30_requests()
    {
        var body = await GetAsync<NeighborhoodStatsResponse>("/api/neighborhoods/central-oak-park/stats?window=30");

        // Current: 1..30 days, median 15.5, p90 at 26.1: 27 + 0.1 × 1 = 27.1. Prior: 30 × 2 days. (15.5 − 2) / 2 = +675%.
        Assert.Equal((30, 15.5m, 27.1m), (body.Stats.Current.Closed, body.Stats.Current.MedianDays, body.Stats.Current.P90Days));
        Assert.Equal((30, 2m), (body.Stats.Prior.Closed, body.Stats.Prior.MedianDays));
        Assert.Equal(new TrendResult(Trend.Slower, 675.0m), body.Stats.Trend);
    }

    [Fact]
    public async Task Window_defaults_to_90_days()
    {
        var body = await GetAsync<NeighborhoodStatsResponse>("/api/neighborhoods/downtown/stats");

        // The 90-day window takes in the request closed 40 days ago too: 1, 2, 3, 10, 100 → median 3.
        Assert.Equal(90, body.WindowDays);
        Assert.Equal((5, 3m), (body.Stats.Current.Closed, body.Stats.Current.MedianDays));
    }

    [Fact]
    public async Task Category_summary_has_the_citywide_total_and_each_category()
    {
        var body = await GetAsync<CategorySummaryResponse>("/api/categories/summary?window=30");

        // All 35 closed in the window: 1..30 plus 1, 2, 3, 7, 10. The 18th value is 13; p90 at 30.6: 26 + 0.6 = 26.6.
        Assert.Equal((35, 13m, 26.6m), (body.Total.Current.Closed, body.Total.Current.MedianDays, body.Total.Current.P90Days));
        // Prior: 30 × 2 days plus 100: median 2, so +550%.
        Assert.Equal(new TrendResult(Trend.Slower, 550.0m), body.Total.Trend);
        Assert.Equal(["Solid Waste", "Streets", "Water"], body.Categories.Select(c => c.Category).Order(StringComparer.Ordinal));
        Assert.Equal(7m, body.Categories.Single(c => c.Category == "Water").Stats.Current.MedianDays);
        // Ordered by requests opened in the current period.
        Assert.Equal("Solid Waste", body.Categories[0].Category);
    }

    [Fact]
    public async Task Category_summary_for_a_district_leaves_out_other_districts_and_requests_outside_the_city()
    {
        var body = await GetAsync<CategorySummaryResponse>("/api/categories/summary?window=30&district=4");

        Assert.Equal(4, body.District);
        Assert.Equal((4, 2.5m), (body.Total.Current.Closed, body.Total.Current.MedianDays));
        Assert.Equal("Streets", Assert.Single(body.Categories).Category);
    }

    [Fact]
    public async Task Map_lists_neighborhoods_with_their_current_medians()
    {
        var body = await GetAsync<MapResponse>("/api/map/neighborhoods?window=30");

        Assert.Equal(["Central Oak Park", "Downtown"], body.Neighborhoods.Select(n => n.Name));
        var downtown = body.Neighborhoods.Single(n => n.Slug == "downtown");
        Assert.Equal((7, 4, 2.5m, 7.9m, 2), (downtown.Opened, downtown.Closed, downtown.MedianDays, downtown.P90Days, downtown.OpenBacklog));
        Assert.Equal(15.5m, body.Neighborhoods.Single(n => n.Slug == "central-oak-park").MedianDays);
    }

    [Fact]
    public async Task Map_filters_by_district_and_category()
    {
        var district5 = await GetAsync<MapResponse>("/api/map/neighborhoods?window=30&district=5");
        Assert.Equal("central-oak-park", Assert.Single(district5.Neighborhoods).Slug);

        var streets = await GetAsync<MapResponse>("/api/map/neighborhoods?window=30&category=Streets");
        Assert.Equal("Streets", streets.Category);
        Assert.Equal("downtown", Assert.Single(streets.Neighborhoods).Slug);
    }

    [Fact]
    public async Task Daily_backlog_ends_with_the_requests_open_now()
    {
        var body = await GetAsync<BacklogResponse>("/api/backlog?grain=day&from=2026-09-20");

        Assert.Equal((new DateOnly(2026, 9, 20), Today, "day"), (body.From, body.To, body.Grain));
        Assert.Equal(13, body.Points.Count);
        Assert.Equal(new BacklogPoint(Today, 0, 1, 2), body.Points[^1]);
        // Open at the end of each day = open the day before + opened − closed.
        var before = body.Points[0].Open - body.Points[0].Opened + body.Points[0].Closed;
        Assert.Equal(before + body.Points.Sum(p => p.Opened) - body.Points.Sum(p => p.Closed), body.Points[^1].Open);
    }

    [Fact]
    public async Task Weekly_backlog_is_the_default_and_starts_in_2024()
    {
        var body = await GetAsync<BacklogResponse>("/api/backlog?category=Streets&district=4");

        Assert.Equal((AggregateBuilder.BacklogStart, Today, "week"), (body.From, body.To, body.Grain));
        Assert.All(body.Points, p => Assert.Equal(DayOfWeek.Monday, p.Date.DayOfWeek));
        Assert.Equal(2, body.Points[^1].Open);
    }

    [Theory]
    [InlineData("/api/neighborhoods/downtown/stats?window=45", "window")]
    [InlineData("/api/neighborhoods/downtown/stats?category=nope", "category")]
    [InlineData("/api/categories/summary?district=9", "district")]
    [InlineData("/api/map/neighborhoods?district=0", "district")]
    [InlineData("/api/backlog?grain=month", "grain")]
    [InlineData("/api/backlog?from=2026-02-01&to=2026-01-01", "from")]
    public async Task Bad_parameters_are_a_validation_problem(string url, string parameter)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(parameter, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Unknown_neighborhood_is_not_found()
    {
        var response = await _client.GetAsync(new Uri("/api/neighborhoods/atlantis/stats", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Unknown neighborhood", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }

    [Fact]
    public async Task Neighborhoods_lists_the_boundary_file_names()
    {
        var body = await GetAsync<List<Neighborhood>>("/api/neighborhoods");

        Assert.Equal(129, body.Count);
        Assert.Contains(new Neighborhood("downtown", "Downtown"), body);
    }

    [Fact]
    public async Task Responses_are_served_from_the_output_cache()
    {
        const string url = "/api/neighborhoods/downtown/stats?window=365";
        var first = await _client.GetAsync(new Uri(url, UriKind.Relative));
        var second = await _client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.False(first.Headers.Contains("Age"));
        Assert.True(second.Headers.Contains("Age"));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Freshness_reports_the_last_run_watermark_count_and_dq_issues()
    {
        var body = await GetAsync<FreshnessResponse>("/api/meta/freshness");

        Assert.Equal("fresh", body.Status);
        Assert.Equal(45, body.MaxAgeMinutes);
        Assert.Equal(("Incremental", "Succeeded", SeededApi.AsOfUtc.AddMinutes(-10)), (body.LastSuccess!.Pipeline, body.LastSuccess.Status, body.LastSuccess.FinishedUtc));
        Assert.Equal(SeededApi.AsOfUtc.AddMinutes(-30), body.WatermarkUtc);
        Assert.Equal(69, body.RequestCount);
        Assert.Equal(Today, body.AggregatesAsOfDate);
        Assert.Equal((1, 1, 0), (body.Dq.Pass, body.Dq.Warn, body.Dq.Fail));
        Assert.Equal("null_rate.address", Assert.Single(body.Dq.Issues).Check);
    }

    [Fact]
    public async Task Ready_is_healthy_when_a_run_succeeded_recently()
    {
        var response = await _client.GetAsync(new Uri("/api/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("""{"status":"Healthy""", body, StringComparison.Ordinal);
        Assert.Contains("\"freshness\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Swagger_document_is_titled_rivercity_pulse_api()
    {
        var document = await _client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        var ui = await _client.GetAsync(new Uri("/swagger/index.html", UriKind.Relative));

        Assert.Contains("\"title\": \"RiverCity Pulse API\"", document, StringComparison.Ordinal);
        Assert.Contains("/api/neighborhoods/{slug}/stats", document, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }
}
