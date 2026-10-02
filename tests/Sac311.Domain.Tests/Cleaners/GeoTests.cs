using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class GeoTests
{
    [Fact]
    public void Real_points_outside_the_bbox_become_null_with_a_flag()
    {
        var rows = Fixture.Real("outside-bbox");
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.X);
            Assert.Equal(new GeoResult(null, null, DqFlags.GeoOutOfBounds), Geo.Validate(r.X, r.Y));
        });
    }

    [Fact]
    public void Real_sacramento_point_is_kept_and_rounded_to_six_decimals()
    {
        var row = Fixture.Real("typical")[0];
        Assert.Equal(new GeoResult(-121.495433m, 38.521712m, DqFlags.None), Geo.Validate(row.X, row.Y));
    }

    [Fact]
    public void Real_rows_without_a_point_stay_null_without_a_flag()
    {
        var rows = Fixture.Real("category-blank").Concat(Fixture.Real("source-blank")).Where(r => r.X is null).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(new GeoResult(null, null, DqFlags.None), Geo.Validate(r.X, r.Y)));
    }

    [Theory]
    [InlineData(double.NaN, 38.5)]
    [InlineData(-121.5, double.PositiveInfinity)]
    public void Non_finite_coordinates_are_missing(double x, double y) =>
        Assert.Equal(new GeoResult(null, null, DqFlags.None), Geo.Validate(x, y));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-121.5, 39.01)]
    [InlineData(-122.01, 38.5)]
    public void Points_off_the_map_are_flagged(double x, double y) =>
        Assert.Equal(DqFlags.GeoOutOfBounds, Geo.Validate(x, y).Flags);

    [Fact]
    public void Bbox_edges_are_inside() =>
        Assert.Equal(DqFlags.None, Geo.Validate(Geo.MinLongitude, Geo.MaxLatitude).Flags);
}
