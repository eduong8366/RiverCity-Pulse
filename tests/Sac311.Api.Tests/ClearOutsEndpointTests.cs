using System.Net;
using System.Net.Http.Json;
using Sac311.Api.Endpoints;
using Sac311.Api.Tests.Support;

namespace Sac311.Api.Tests;

/// <summary><c>/api/meta/clear-outs</c> over the <see cref="SeededApi"/> scenario, and the note sentences.</summary>
public class ClearOutsEndpointTests(SeededApi api) : IClassFixture<SeededApi>
{
    private readonly HttpClient _client = api.Factory.CreateClient();

    private async Task<ClearOutsResponse> GetAsync(string url)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClearOutsResponse>())!;
    }

    [Fact]
    public async Task Lists_every_clear_out_since_2024_with_its_note()
    {
        var body = await GetAsync("/api/meta/clear-outs");

        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 2), (string?)null), (body.From, body.To, body.Category));
        Assert.Equal((SeededApi.AsOfUtc, MetaEndpoints.Definitions), (body.AsOf, body.Definitions));
        Assert.Equal(new ClearOutRules(100, 50, 180, 90, "/api/meta/clear-outs"), body.Rule);

        // The 100 Parking requests closed 3 days ago, all at 12:00 Sacramento time, after 200 days.
        Assert.Equal(100, body.Closed);
        var note = Assert.Single(body.ClearOuts);
        Assert.Equal(
            (new DateOnly(2026, 9, 29), "Parking", 100, 200m, 1, false),
            (note.Date, note.Category, note.Closed, note.AverageDaysToClose, note.MinutesSpanned, note.IsSweep));
        Assert.Empty(note.SweepCategories);
        Assert.Equal("On 2026-09-29, Parking closed 100 requests averaging 200 days old in one minute.", note.Note);
    }

    [Theory]
    [InlineData("/api/meta/clear-outs?category=parking", 1)]
    [InlineData("/api/meta/clear-outs?category=Streets", 0)]
    [InlineData("/api/meta/clear-outs?from=2026-09-29&to=2026-09-29", 1)]
    [InlineData("/api/meta/clear-outs?from=2026-09-30", 0)]
    [InlineData("/api/meta/clear-outs?to=2026-09-28", 0)]
    public async Task Filters_by_date_and_category(string url, int expected)
    {
        var body = await GetAsync(url);

        Assert.Equal(expected, body.ClearOuts.Count);
        Assert.Equal(expected * 100, body.Closed);
    }

    [Fact]
    public async Task Dates_are_clamped_to_the_data()
    {
        var body = await GetAsync("/api/meta/clear-outs?from=2020-01-01&to=2030-01-01&category=PARKING");

        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2026, 10, 2), "Parking"), (body.From, body.To, body.Category));
    }

    [Theory]
    [InlineData("/api/meta/clear-outs?category=Nope")]
    [InlineData("/api/meta/clear-outs?from=2026-09-30&to=2026-09-01")]
    public async Task Bad_parameters_are_a_validation_problem(string url)
    {
        var response = await _client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(10244, 680.4, 1, new string[0], "On 2026-08-10, Parking closed 10,244 requests averaging 680 days old in one minute.")]
    [InlineData(212, 400.5, 1, new[] { "Sewer", "Drains", "Solid Waste" },
        "On 2026-08-10, Parking closed 212 requests averaging 401 days old in one minute. In the same minute, Sewer, Drains and Solid Waste also closed old requests.")]
    [InlineData(805, 512, 47, new[] { "Animal Control" },
        "On 2026-08-10, Parking closed 805 requests averaging 512 days old over 47 minutes. In the same minute, Animal Control also closed old requests.")]
    [InlineData(1, 120, 1, new[] { "Water" },
        "On 2026-08-10, Parking closed 1 request 120 days old. In the same minute, Water also closed old requests.")]
    public void Notes_are_generated_from_the_figures(int closed, double averageDays, int minutes, string[] sweep, string expected) =>
        Assert.Equal(expected, ClearOutNotes.Sentence(new DateOnly(2026, 8, 10), "Parking", closed, (decimal)averageDays, minutes, sweep));
}
