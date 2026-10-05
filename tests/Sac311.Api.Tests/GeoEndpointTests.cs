using System.Net;
using System.Text.Json;
using Sac311.Api.Endpoints;
using Sac311.Api.Tests.Support;

namespace Sac311.Api.Tests;

/// <summary><c>/api/geo/neighborhoods</c>: the boundary file with the API's slugs, served without the database.</summary>
public class GeoEndpointTests(SeededApi api) : IClassFixture<SeededApi>
{
    private readonly HttpClient _client = api.Factory.CreateClient();

    [Fact]
    public async Task Serves_the_129_boundaries_with_their_slugs()
    {
        var response = await _client.GetAsync(new Uri("/api/geo/neighborhoods", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GeoEndpoints.ContentType, response.Content.Headers.ContentType?.MediaType);
        using var geo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = geo.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("properties")).ToList();
        Assert.Equal(129, properties.Count);

        var slugs = properties.ToDictionary(p => p.GetProperty("NAME").GetString()!, p => p.GetProperty("slug").GetString()!);
        Assert.Equal(129, slugs.Values.Distinct().Count());
        Assert.Equal("college-glen", slugs["College/Glen"]);
        Assert.Equal("zberg-park", slugs["Z'berg Park"]);
        Assert.Equal("downtown", slugs["Downtown"]);
    }
}
