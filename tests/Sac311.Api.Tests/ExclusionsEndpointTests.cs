using System.Net;
using System.Net.Http.Json;
using Sac311.Api.Endpoints;
using Sac311.Api.Tests.Support;

namespace Sac311.Api.Tests;

/// <summary><c>/api/meta/exclusions</c> over the <see cref="SeededApi"/> scenario; counts are worked out by hand from it.</summary>
public class ExclusionsEndpointTests(SeededApi api) : IClassFixture<SeededApi>
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private readonly HttpClient _client = api.Factory.CreateClient();

    [Fact]
    public async Task Every_exclusion_is_listed_with_its_count_and_reason()
    {
        var response = await _client.GetAsync(new Uri("/api/meta/exclusions?window=30", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ExclusionsResponse>())!;

        Assert.Equal((30, SeededApi.AsOfUtc, MetaEndpoints.Definitions), (body.WindowDays, body.AsOf, body.Definitions));
        Assert.Equal(new ClearOutRules(100, 50, 180, 90, "/api/meta/clear-outs"), body.ClearOutRule);

        var current = body.Current;
        Assert.Equal((new DateOnly(2026, 9, 3), Today), (current.From, current.To));

        // Two information calls opened and closed, one "Parking / General" call, one inbox item opened and still open.
        Assert.Equal((4, 3), (current.NonService.Opened, current.NonService.Closed));
        Assert.Equal(
            [("Other", "Other", 2, 2), ("Parking", "Parking / General", 1, 1), ("Process/Unclassified", "Review", 1, 0)],
            current.NonService.Types.Select(t => (t.CategoryGroup, t.Type, t.Opened, t.Closed)));
        Assert.All(current.NonService.Types, t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));

        // The downtown request closed without a close date. The clear-out is counted, so it isn't listed here.
        var date = Assert.Single(current.DateProblems);
        Assert.Equal(("ClosedMissingDate", 1), (date.Flag, date.Closed));

        Assert.Equal((0, 0), (body.Prior.NonService.Opened, body.Prior.DateProblems.Count));

        Assert.Equal(1, body.OpenNow.Open);
        Assert.Equal(("Process/Unclassified", "Review", 1), Assert.Single(body.OpenNow.Types) is var t ? (t.CategoryGroup, t.Type, t.Open) : default);
    }

    [Fact]
    public async Task Bad_window_is_a_validation_problem()
    {
        var response = await _client.GetAsync(new Uri("/api/meta/exclusions?window=7", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
