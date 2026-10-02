using System.Text.Json;

namespace Sac311.Domain.Tests;

public class SourceRowTests
{
    private static SourceRow Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SourceRow.FromFeature(doc.RootElement);
    }

    [Fact]
    public void Real_feature_maps_every_field()
    {
        var r = Fixture.Real("typical")[0];
        Assert.Equal(2049272, r.ObjectId);
        Assert.Equal("240101-2172157", r.ReferenceNumber);
        Assert.Equal("Code Enforcement", r.CategoryLevel1);
        Assert.Equal("General", r.CategoryLevel2);
        Assert.Equal("Code Enforcement General", r.CategoryName);
        Assert.Equal("District 5", r.CouncilDistrictNumber);
        Assert.Equal("Web", r.SourceLevel1);
        Assert.Equal("Airport", r.Neighborhood);
        Assert.Equal(1704067320000, r.DateCreated);
        Assert.Equal(1704925800000, r.DateUpdated);
        Assert.Equal(1704925800000, r.DateClosed);
        Assert.Equal("LA CAMPANA WAY", r.CrossStreet);
        Assert.Equal("ff64ac47-56c6-4fc6-952c-8131024b2d46", r.GlobalId);
        Assert.Equal("95822", r.Zip);
        Assert.Equal("5005G00001pC9TtQAK", r.SfTicketId);
        Assert.Equal("1924 34TH AVE, SACRAMENTO, 95822", r.Address);
        Assert.Equal("311", r.DataSource);
        Assert.Equal("CLOSED", r.PublicStatus);
        Assert.Equal(-121.49543309783193, r.X);
        Assert.Equal(38.521712206751893, r.Y);
    }

    [Fact]
    public void Real_empty_strings_are_kept_raw() =>
        Assert.All(Fixture.Real("address-empty"), r => Assert.Equal("", r.Address));

    [Theory]
    [InlineData("""{"attributes":{"OBJECTID":1}}""")]
    [InlineData("""{"attributes":{"OBJECTID":1},"geometry":null}""")]
    [InlineData("""{"attributes":{"OBJECTID":1},"geometry":{"x":"NaN","y":"NaN"}}""")]
    [InlineData("""{"attributes":{"OBJECTID":1},"geometry":{"x":null,"y":38.5}}""")]
    public void Missing_or_empty_geometry_is_null(string json)
    {
        var r = Parse(json);
        Assert.Null(r.X);
        Assert.True(r.Y is null || json.Contains("38.5", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_attributes_are_null()
    {
        var r = Parse("""{"attributes":{"OBJECTID":7,"DateClosed":null}}""");
        Assert.Equal(7, r.ObjectId);
        Assert.Null(r.ReferenceNumber);
        Assert.Null(r.DateClosed);
    }
}
