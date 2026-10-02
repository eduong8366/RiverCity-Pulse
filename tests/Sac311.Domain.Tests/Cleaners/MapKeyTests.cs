using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class MapKeyTests
{
    [Theory]
    [InlineData("GoogleAI", "googleai")]
    [InlineData("Google AI", "googleai")]
    [InlineData("Homeless Camp - Primary", "homelesscampprimary")]
    [InlineData("Public_Works_Facilities_Work_Order_Request_Form", "publicworksfacilitiesworkorderrequestform")]
    [InlineData("Mayor's Office", "mayorsoffice")]
    [InlineData("College/Glen", "collegeglen")]
    [InlineData("Midtown / Winn Park / Capital Avenue", "midtownwinnparkcapitalavenue")]
    public void Keeps_lowercase_letters_and_digits(string value, string expected) =>
        Assert.Equal(expected, MapKey.For(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  -/ ")]
    public void Nothing_left_is_null(string? value) => Assert.Null(MapKey.For(value));

    [Fact]
    public void Real_google_ai_spellings_share_a_key()
    {
        var rows = Fixture.Real("source-google-ai");
        Assert.Contains(rows, r => r.SourceLevel1 == "GoogleAI");
        Assert.Contains(rows, r => r.SourceLevel1 == "Google AI");
        Assert.All(rows, r => Assert.Equal("googleai", MapKey.For(r.SourceLevel1)));
    }

    [Fact]
    public void Real_homeless_camp_variants_have_their_own_keys_for_the_seed_to_merge()
    {
        var keys = Fixture.Real("homeless-camp").Select(r => MapKey.For(r.CategoryLevel1)).Distinct().Order().ToList();
        Assert.Equal(["homelesscamp", "homelesscampprimary"], keys);
    }

    [Fact]
    public void Real_blank_category_and_source_have_no_key()
    {
        Assert.All(Fixture.Real("category-blank"), r => Assert.Null(MapKey.For(r.CategoryLevel1)));
        Assert.All(Fixture.Real("source-blank"), r => Assert.Null(MapKey.For(r.SourceLevel1)));
    }
}
