using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class AddressTests
{
    [Fact]
    public void Real_empty_addresses_become_null_without_a_flag()
    {
        Assert.All(Fixture.Real("address-empty"), r =>
            Assert.Equal(new Cleaned<string?>(null, DqFlags.None), Address.Normalize(r.Address)));
    }

    [Fact]
    public void Real_placeholder_addresses_become_null_with_the_junk_flag()
    {
        var rows = Fixture.Real("address-junk");
        Assert.Equal(["N/A", "TBD", "Ok", "Zoom", "Oi", "e"], rows.Select(r => r.Address));
        Assert.All(rows, r => Assert.Equal(new Cleaned<string?>(null, DqFlags.AddressJunk), Address.Normalize(r.Address)));
    }

    [Fact]
    public void Real_street_addresses_are_kept()
    {
        var row = Fixture.Real("typical")[0];
        Assert.Equal(new Cleaned<string?>("1924 34TH AVE, SACRAMENTO, 95822", DqFlags.None), Address.Normalize(row.Address));
    }

    [Theory]
    [InlineData("NONE")]
    [InlineData("unknown")]
    [InlineData("n.a.")]
    [InlineData("1234")]
    [InlineData("# 5100")]
    [InlineData("--")]
    public void Other_placeholders_are_junk(string value) =>
        Assert.Equal(DqFlags.AddressJunk, Address.Normalize(value).Flags);

    [Theory]
    [InlineData("  915 I   ST ", "915 I ST")]
    [InlineData("J St & 10th St", "J St & 10th St")]
    public void Real_looking_addresses_are_cleaned_not_dropped(string value, string expected) =>
        Assert.Equal(new Cleaned<string?>(expected, DqFlags.None), Address.Normalize(value));
}
