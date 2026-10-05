using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Sac311.Api.Data;
using Sac311.Api.Endpoints;
using Sac311.Api.Tests.Support;
using Sac311.Data.Aggregates;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Tests;

/// <summary>/api/trends/slower and the neighborhood filter of /api/categories/summary over the <see cref="SeededApi"/> scenario.</summary>
public class TrendEndpointTests(SeededApi api) : IClassFixture<SeededApi>
{
    private readonly HttpClient _client = api.Factory.CreateClient();

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Slower_neighborhoods_are_those_with_a_slower_trend()
    {
        var body = await GetAsync<SlowerResponse>("/api/trends/slower?window=30");

        // Central Oak Park: median 15.5 against 2 (30 closed in each period). Downtown has too few closed for a trend.
        Assert.Equal(("neighborhood", 30, (string?)null, (int?)null), (body.By, body.WindowDays, body.Category, body.District));
        Assert.Equal(new TrendCounts(1, 0, 0, 1), body.Compared);
        var item = Assert.Single(body.Items);
        Assert.Equal(("central-oak-park", "Central Oak Park", 13.5m, 30, 30), (item.Key, item.Name, item.DaysAdded, item.Current.Closed, item.Prior.Closed));
        Assert.Equal(new TrendResult(Trend.Slower, 675.0m), item.Trend);
    }

    [Fact]
    public async Task Slower_categories_rank_the_category_groups()
    {
        var body = await GetAsync<SlowerResponse>("/api/trends/slower?by=CATEGORY&window=30");

        // Solid Waste is the only category with 30 closed in both periods; Parking's clear-out has no prior period.
        Assert.Equal("category", body.By);
        Assert.Equal(new TrendCounts(1, 0, 0, 3), body.Compared);
        Assert.Equal(("Solid Waste", 13.5m), (Assert.Single(body.Items).Key, body.Items[0].DaysAdded));
    }

    [Fact]
    public async Task Slower_neighborhoods_filter_by_category_and_district()
    {
        var streets = await GetAsync<SlowerResponse>("/api/trends/slower?window=30&category=streets");
        Assert.Equal(("Streets", new TrendCounts(0, 0, 0, 1)), (streets.Category, streets.Compared));
        Assert.Empty(streets.Items);

        var district4 = await GetAsync<SlowerResponse>("/api/trends/slower?window=30&district=4");
        Assert.Equal((4, new TrendCounts(0, 0, 0, 1)), (district4.District, district4.Compared));
    }

    [Theory]
    [InlineData("/api/trends/slower?by=district", "by")]
    [InlineData("/api/trends/slower?by=category&category=Parking", "category")]
    [InlineData("/api/trends/slower?limit=0", "limit")]
    [InlineData("/api/trends/slower?limit=51", "limit")]
    [InlineData("/api/trends/slower?window=7", "window")]
    [InlineData("/api/categories/summary?neighborhood=atlantis", "neighborhood")]
    public async Task Bad_parameters_are_a_validation_problem(string url, string parameter)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.Contains(parameter, problem!.Errors.Keys);
    }

    [Fact]
    public async Task Category_summary_for_a_neighborhood_has_its_total_and_categories()
    {
        var body = await GetAsync<CategorySummaryResponse>("/api/categories/summary?window=30&neighborhood=Downtown");

        // Downtown's Streets requests (as in the neighborhood stats); its "Parking / General" call is non-service.
        Assert.Equal(new Neighborhood("downtown", "Downtown"), body.Neighborhood);
        Assert.Equal((4, 2.5m, 2), (body.Total.Current.Closed, body.Total.Current.MedianDays, body.Total.OpenBacklog));
        Assert.Equal("Streets", Assert.Single(body.Categories).Category);

        var citywide = await GetAsync<CategorySummaryResponse>("/api/categories/summary?window=30");
        Assert.Null(citywide.Neighborhood);
    }

    [Fact]
    public void Rank_orders_by_days_added_then_by_change_and_takes_the_limit()
    {
        var asOf = new DateOnly(2026, 10, 2);
        (string, string, CellStats) Cell(string name, decimal prior, decimal current) =>
            (name.ToLowerInvariant(), name, CellStats.Of(
                new Cell(name, AggregateCell.All, new PeriodRow(40, 40, 0, 0, current, current), new PeriodRow(40, 40, 0, 0, prior, prior), 0, null),
                asOf, 30));

        var ranked = TrendEndpoints.Rank(
            [Cell("Small", 0.5m, 1.5m), Cell("Big", 20m, 40m), Cell("Same", 30m, 50m), Cell("Faster", 10m, 5m), Cell("Steady", 10m, 10.2m)], 2);

        // Big and Same both add 20 days; Big's +100% beats Same's +66.7%. Small (+200%) adds only 1 day.
        Assert.Equal(["Big", "Same"], ranked.Select(r => r.Name));
    }
}
