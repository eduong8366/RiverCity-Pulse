using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class DistrictTests
{
    [Fact]
    public void Real_non_city_rows_are_outside_the_city()
    {
        Assert.All(Fixture.Real("non-city-no-neighborhood"), r =>
            Assert.Equal(new DistrictResult(null, false, DqFlags.None), District.Parse(r.CouncilDistrictNumber)));
    }

    [Fact]
    public void Real_district_rows_parse_to_their_number()
    {
        var row = Fixture.Real("typical")[0];
        Assert.Equal("District 5", row.CouncilDistrictNumber);
        Assert.Equal(new DistrictResult(5, true, DqFlags.None), District.Parse(row.CouncilDistrictNumber));
    }

    [Theory]
    [InlineData("District 1", 1)]
    [InlineData("District 8", 8)]
    [InlineData("district 04", 4)]
    [InlineData("3", 3)]
    public void Variants_parse(string value, int expected) =>
        Assert.Equal(new DistrictResult((byte)expected, true, DqFlags.None), District.Parse(value));

    [Theory]
    [InlineData("Non-City")]
    [InlineData("NON CITY")]
    public void Non_city_variants_parse(string value) =>
        Assert.Equal(new DistrictResult(null, false, DqFlags.None), District.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("District 9")]
    [InlineData("District 12")]
    [InlineData("County")]
    public void Anything_else_is_unknown_and_flagged(string? value) =>
        Assert.Equal(new DistrictResult(null, null, DqFlags.UnknownDistrict), District.Parse(value));
}
