using Sac311.Domain.Cleaners;
using Record = Sac311.Domain.Cleaners.Record;

namespace Sac311.Domain.Tests.Cleaners;

public class RecordTests
{
    public static TheoryData<string> RealFixtures() => [.. Fixture.AllRealNames()];

    [Theory]
    [MemberData(nameof(RealFixtures))]
    public void Every_real_fixture_row_is_loadable(string fixture) =>
        Assert.All(Fixture.Real(fixture), r => Assert.Null(Record.Validate(r)));

    [Fact]
    public void Synthetic_sentinel_created_date_is_flagged_not_rejected() =>
        Assert.Null(Record.Validate(Fixture.Synthetic("sentinel-date").Single()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_reference_number_is_rejected(string? reference) =>
        Assert.Equal(RejectReason.MissingReferenceNumber, Record.Validate(Valid() with { ReferenceNumber = reference }));

    [Fact]
    public void Over_long_reference_number_is_rejected() =>
        Assert.Equal(RejectReason.ReferenceNumberTooLong, Record.Validate(Valid() with { ReferenceNumber = new string('9', 51) }));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_object_id_is_rejected(long objectId) =>
        Assert.Equal(RejectReason.InvalidObjectId, Record.Validate(Valid() with { ObjectId = objectId }));

    [Fact]
    public void Missing_date_updated_is_rejected() =>
        Assert.Equal(RejectReason.MissingDateUpdated, Record.Validate(Valid() with { DateUpdated = null }));

    [Fact]
    public void Sentinel_date_updated_is_rejected() =>
        Assert.Equal(RejectReason.InvalidDateUpdated, Record.Validate(Valid() with { DateUpdated = -2209161600000 }));

    private static SourceRow Valid() => Fixture.Real("typical")[0];
}
