using Sac311.Domain.Cleaners;

namespace Sac311.Domain.Tests.Cleaners;

public class StatusTests
{
    [Fact]
    public void Real_statuses_map_to_their_groups()
    {
        var groups = Fixture.Real("typical").DistinctBy(r => r.PublicStatus).ToDictionary(r => r.PublicStatus!, r => Status.Group(r.PublicStatus));
        Assert.Equal(new Cleaned<StatusGroup>(StatusGroup.Closed, DqFlags.None), groups["CLOSED"]);
        Assert.Equal(new Cleaned<StatusGroup>(StatusGroup.Open, DqFlags.None), groups["NEW"]);
        Assert.Equal(new Cleaned<StatusGroup>(StatusGroup.Open, DqFlags.None), groups["IN PROGRESS"]);
        Assert.Equal(new Cleaned<StatusGroup>(StatusGroup.Cancelled, DqFlags.None), groups["CANCELLED"]);
    }

    [Theory]
    [InlineData("closed", StatusGroup.Closed)]
    [InlineData(" In Progress ", StatusGroup.Open)]
    [InlineData("In_Progress", StatusGroup.Open)]
    [InlineData("Canceled", StatusGroup.Cancelled)]
    public void Spelling_variants_are_tolerated(string value, StatusGroup expected) =>
        Assert.Equal(new Cleaned<StatusGroup>(expected, DqFlags.None), Status.Group(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ON HOLD")]
    public void Anything_else_is_unknown_and_flagged(string? value) =>
        Assert.Equal(new Cleaned<StatusGroup>(StatusGroup.Unknown, DqFlags.UnknownStatus), Status.Group(value));
}
