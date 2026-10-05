namespace Sac311.Domain.Tests;

public class BulkClosureRuleTests
{
    [Fact]
    public void Rule_values_are_the_published_ones()
    {
        Assert.Equal((100, 50, 180, 90), (BulkClosureRule.MinCount, BulkClosureRule.SweepMinCount, BulkClosureRule.DetectAgeDays, BulkClosureRule.MemberAgeDays));
        Assert.True(BulkClosureRule.MemberAgeDays < BulkClosureRule.DetectAgeDays);
    }

    [Fact]
    public void Only_date_problems_are_metric_exclusions()
    {
        Assert.Equal(15, (int)DqFlags.DateProblems);
        Assert.Equal(DqFlags.DateProblems, DqFlags.MetricExclusions);
        Assert.False(DqFlags.MetricExclusions.HasFlag(DqFlags.BulkClosure));
    }

    [Fact]
    public void The_clear_out_query_in_docs_uses_the_rule_values()
    {
        var doc = File.ReadAllText(Repo.Path("docs", "metrics.md"));

        Assert.Contains($"@min_count int = {BulkClosureRule.MinCount}", doc, StringComparison.Ordinal);
        Assert.Contains($"@sweep_min_count int = {BulkClosureRule.SweepMinCount}", doc, StringComparison.Ordinal);
        Assert.Contains($"@detect_age_days int = {BulkClosureRule.DetectAgeDays}", doc, StringComparison.Ordinal);
        Assert.Contains($"@member_age_days int = {BulkClosureRule.MemberAgeDays}", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hand_check_query_in_docs_uses_the_metric_exclusion_mask()
    {
        var doc = File.ReadAllText(Repo.Path("docs", "metrics.md"));

        Assert.Contains($"dq_flags & {(int)DqFlags.MetricExclusions} = 0", doc, StringComparison.Ordinal);
    }
}
