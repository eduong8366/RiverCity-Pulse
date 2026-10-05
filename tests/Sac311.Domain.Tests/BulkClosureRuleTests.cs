namespace Sac311.Domain.Tests;

public class BulkClosureRuleTests
{
    [Fact]
    public void Rule_values_are_the_published_ones()
    {
        Assert.Equal((100, 180, 90), (BulkClosureRule.MinCount, BulkClosureRule.DetectAgeDays, BulkClosureRule.MemberAgeDays));
        Assert.True(BulkClosureRule.MemberAgeDays < BulkClosureRule.DetectAgeDays);
    }

    [Fact]
    public void Bulk_closures_are_a_metric_exclusion_next_to_the_date_problems()
    {
        Assert.Equal(15, (int)DqFlags.DateProblems);
        Assert.Equal(DqFlags.DateProblems | DqFlags.BulkClosure, DqFlags.MetricExclusions);
    }

    [Fact]
    public void The_hand_check_query_in_docs_uses_the_rule_values()
    {
        var doc = File.ReadAllText(Repo.Path("docs", "metrics.md"));

        Assert.Contains($"@min_count int = {BulkClosureRule.MinCount}", doc, StringComparison.Ordinal);
        Assert.Contains($"@detect_age_days int = {BulkClosureRule.DetectAgeDays}", doc, StringComparison.Ordinal);
        Assert.Contains($"@member_age_days int = {BulkClosureRule.MemberAgeDays}", doc, StringComparison.Ordinal);
    }
}
