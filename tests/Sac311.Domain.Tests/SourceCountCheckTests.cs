namespace Sac311.Domain.Tests;

public class SourceCountCheckTests
{
    [Theory]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(1_000_000, 1_030_000)]
    [InlineData(1_000_000, 970_000)]
    [InlineData(1_000_000, 1_050_000)] // exactly +5 % passes
    [InlineData(1_000_000, 950_000)] // exactly -5 % passes
    [InlineData(1_573_259, 1_577_658)] // the live count on 2026-10-05 against the committed baseline (+0.3 %)
    public void Counts_within_five_percent_pass(long baseline, long count)
    {
        var result = SourceCountCheck.Compare(baseline, count);

        Assert.Equal(SourceCountOutcome.Within, result.Outcome);
        Assert.Equal((baseline, count), (result.Baseline, result.Count));
    }

    [Fact]
    public void Just_over_five_percent_growth_is_grew()
    {
        var result = SourceCountCheck.Compare(1_000_000, 1_050_001);

        Assert.Equal(SourceCountOutcome.Grew, result.Outcome);
        Assert.Equal(0.050001, result.Change, 6);
    }

    [Fact]
    public void Just_over_five_percent_drop_is_dropped()
    {
        var result = SourceCountCheck.Compare(1_000_000, 949_999);

        Assert.Equal(SourceCountOutcome.Dropped, result.Outcome);
        Assert.Equal(-0.050001, result.Change, 6);
    }

    [Fact]
    public void An_empty_feed_is_dropped()
    {
        Assert.Equal(SourceCountOutcome.Dropped, SourceCountCheck.Compare(1_573_259, 0).Outcome);
    }

    [Fact]
    public void A_baseline_that_is_not_positive_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceCountCheck.Compare(0, 10));
    }

    [Fact]
    public void Baseline_is_read_from_the_row_count_line()
    {
        const string profile = """
            ## Volume and keys

            | Check | Result |
            |---|---|
            | Row count | 1,573,259 |
            | ReferenceNumber null or blank | 0 |
            """;

        Assert.True(SourceCountCheck.TryParseBaseline(profile, out var baseline));
        Assert.Equal(1_573_259, baseline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("| Check | Result |\n|---|---|\n| ReferenceNumber null or blank | 0 |")]
    [InlineData("| Row count | not supported |")]
    [InlineData("| Row count | 0 |")]
    [InlineData("Row count 1,573,259")]
    public void Unparsable_baseline_is_an_error(string profile)
    {
        Assert.False(SourceCountCheck.TryParseBaseline(profile, out _));
    }

    [Fact]
    public void The_committed_profile_has_a_baseline()
    {
        var profile = File.ReadAllText(Repo.Path("docs", "source-profile.md"));

        Assert.True(SourceCountCheck.TryParseBaseline(profile, out var baseline));
        Assert.InRange(baseline, 1_000_000, 10_000_000);
    }
}
