using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class ZipTests
{
    [Fact]
    public void Real_zips_are_kept()
    {
        var rows = Fixture.Real("typical").Where(r => r.Zip is not null).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(new Cleaned<string?>(r.Zip, DqFlags.None), Zip.Normalize(r.Zip)));
    }

    [Theory]
    [InlineData("95814-1234", "95814")]
    [InlineData("958141234", "95814")]
    [InlineData(" 95822 ", "95822")]
    public void Zip_plus_four_is_cut_to_five(string value, string expected) =>
        Assert.Equal(new Cleaned<string?>(expected, DqFlags.None), Zip.Normalize(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_is_null_without_a_flag(string? value) =>
        Assert.Equal(new Cleaned<string?>(null, DqFlags.None), Zip.Normalize(value));

    [Theory]
    [InlineData("9581")]
    [InlineData("CA 95814")]
    [InlineData("N/A")]
    public void Malformed_is_null_with_a_flag(string value) =>
        Assert.Equal(new Cleaned<string?>(null, DqFlags.InvalidZip), Zip.Normalize(value));
}
