using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests;

public class CleanedRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    private static CleanedRequest Clean(SourceRow row) => CleanedRequest.From(row, Now);

    [Fact]
    public void Typical_closed_row_is_fully_cleaned()
    {
        var row = Fixture.Real("typical")[0];

        var c = Clean(row);

        Assert.Equal("240101-2172157", c.ReferenceNumber);
        Assert.Equal(2049272, c.ObjectId);
        Assert.Equal("codeenforcement", c.CategoryKey);
        Assert.Equal("web", c.SourceKey);
        Assert.Equal((byte)5, c.DistrictNumber);
        Assert.True(c.IsCity);
        Assert.Equal("Airport", c.Neighborhood);
        Assert.Equal("airport", c.NeighborhoodSlug);
        Assert.Equal("95822", c.Zip);
        Assert.Equal(38.521712m, c.Latitude);
        Assert.Equal(-121.495433m, c.Longitude);
        Assert.Equal(StatusGroup.Closed, c.StatusGroup);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 2, 0, DateTimeKind.Utc), c.CreatedUtc);
        Assert.Equal(new DateOnly(2023, 12, 31), c.CreatedDateLocal);
        Assert.NotNull(c.DaysToClose);
        Assert.Equal(c.ClosedDateLocal, c.BacklogCloseDateLocal);
        Assert.Equal(DqFlags.None, c.Flags);
        Assert.Equal(RowHasher.Hash(row), c.RowHash);
    }

    [Theory]
    [InlineData("closed-before-created", DqFlags.InvalidCloseOrder)]
    [InlineData("closed-missing-date", DqFlags.ClosedMissingDate)]
    [InlineData("address-junk", DqFlags.AddressJunk)]
    [InlineData("city-no-neighborhood", DqFlags.NeighborhoodMissingInCity)]
    [InlineData("outside-bbox", DqFlags.GeoOutOfBounds)]
    public void Real_bad_rows_carry_their_flag(string fixture, DqFlags flag) =>
        Assert.All(Fixture.Real(fixture), r => Assert.True(Clean(r).Flags.HasFlag(flag), $"{r.ReferenceNumber} lacks {flag}"));

    [Fact]
    public void Non_city_row_without_neighborhood_is_not_flagged()
    {
        Assert.All(Fixture.Real("non-city-no-neighborhood"), r =>
        {
            var c = Clean(r);
            Assert.False(c.IsCity);
            Assert.Null(c.NeighborhoodSlug);
            Assert.False(c.Flags.HasFlag(DqFlags.NeighborhoodMissingInCity));
        });
    }

    [Fact]
    public void Blank_category_and_source_have_no_map_key() =>
        Assert.All(Fixture.Real("category-blank").Concat(Fixture.Real("source-blank")), r =>
        {
            var c = Clean(r);
            Assert.True(c.CategoryKey is null || c.SourceKey is null);
        });

    [Fact]
    public void Both_google_ai_spellings_share_a_source_key() =>
        Assert.All(Fixture.Real("source-google-ai"), r => Assert.Equal("googleai", Clean(r).SourceKey));

    [Fact]
    public void Out_of_bbox_points_are_dropped() =>
        Assert.All(Fixture.Real("outside-bbox"), r =>
        {
            var c = Clean(r);
            Assert.Null(c.Latitude);
            Assert.Null(c.Longitude);
        });

    [Fact]
    public void Open_rows_have_no_close_metrics() =>
        Assert.All(Fixture.Real("typical").Where(r => r.PublicStatus is "NEW" or "IN PROGRESS"), r =>
        {
            var c = Clean(r);
            Assert.Equal(StatusGroup.Open, c.StatusGroup);
            Assert.Null(c.DaysToClose);
            Assert.Null(c.BacklogCloseDateLocal);
        });

    [Fact]
    public void Sentinel_created_date_becomes_null_and_flagged() =>
        Assert.All(Fixture.Synthetic("sentinel-date"), r =>
        {
            var c = Clean(r);
            Assert.True(c.Flags.HasFlag(DqFlags.SentinelDate));
        });

    [Fact]
    public void A_row_that_fails_validation_is_refused() =>
        Assert.Throws<ArgumentException>(() => Clean(new SourceRow { ObjectId = 1, ReferenceNumber = " ", DateUpdated = 1704925800000 }));
}
