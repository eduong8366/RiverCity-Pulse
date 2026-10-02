using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class TextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Blank_becomes_null(string? value) => Assert.Null(Text.NullIfBlank(value));

    [Theory]
    [InlineData("  LA CAMPANA WAY ", "LA CAMPANA WAY")]
    [InlineData("1924   34TH\tAVE", "1924 34TH AVE")]
    [InlineData("Phone", "Phone")]
    public void Trims_and_collapses_whitespace(string value, string expected) =>
        Assert.Equal(expected, Text.NullIfBlank(value));

    [Fact]
    public void Real_empty_addresses_become_null()
    {
        var rows = Fixture.Real("address-empty");
        Assert.All(rows, r => Assert.Equal(string.Empty, r.Address));
        Assert.All(rows, r => Assert.Null(Text.NullIfBlank(r.Address)));
    }
}
