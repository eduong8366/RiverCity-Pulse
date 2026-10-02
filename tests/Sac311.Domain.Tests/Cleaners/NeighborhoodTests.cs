using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class NeighborhoodTests
{
    [Fact]
    public void Real_slashed_names_get_dash_slugs()
    {
        var result = Fixture.Real("neighborhood-slashes").Select(r => Neighborhood.Normalize(r.Neighborhood, true)).ToList();
        Assert.Equal(new NeighborhoodResult("College/Glen", "collegeglen", "college-glen", DqFlags.None), result[0]);
        Assert.Equal(
            new NeighborhoodResult("Midtown / Winn Park / Capital Avenue", "midtownwinnparkcapitalavenue", "midtown-winn-park-capital-avenue", DqFlags.None),
            result[^1]);
    }

    [Theory]
    [InlineData("College / Glen")]
    [InlineData("College\\Glen")]
    [InlineData("  college/glen ")]
    public void Slash_and_spacing_variants_share_key_and_slug(string value)
    {
        var result = Neighborhood.Normalize(value, true);
        Assert.Equal("collegeglen", result.Key);
        Assert.Equal("college-glen", result.Slug);
    }

    [Fact]
    public void Real_city_row_without_a_neighborhood_is_flagged()
    {
        Assert.All(Fixture.Real("city-no-neighborhood"), r =>
        {
            var isCity = District.Parse(r.CouncilDistrictNumber).IsCity;
            Assert.Equal(new NeighborhoodResult(null, null, null, DqFlags.NeighborhoodMissingInCity), Neighborhood.Normalize(r.Neighborhood, isCity));
        });
    }

    [Fact]
    public void Real_non_city_row_without_a_neighborhood_is_not_flagged()
    {
        Assert.All(Fixture.Real("non-city-no-neighborhood"), r =>
        {
            var isCity = District.Parse(r.CouncilDistrictNumber).IsCity;
            Assert.Equal(new NeighborhoodResult(null, null, null, DqFlags.None), Neighborhood.Normalize(r.Neighborhood, isCity));
        });
    }

    [Fact]
    public void Unknown_district_without_a_neighborhood_is_not_flagged() =>
        Assert.Equal(DqFlags.None, Neighborhood.Normalize(null, null).Flags);

    [Theory]
    [InlineData("Z'berg Park", "zberg-park")]
    [InlineData("Z’berg Park", "zberg-park")]
    [InlineData("Valley Hi / North Laguna", "valley-hi-north-laguna")]
    [InlineData("--", null)]
    public void Slugs_drop_apostrophes_and_dash_everything_else(string name, string? expected) =>
        Assert.Equal(expected, Neighborhood.Slug(name));
}
