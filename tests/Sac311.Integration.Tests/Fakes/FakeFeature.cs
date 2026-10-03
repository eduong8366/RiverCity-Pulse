using System.Globalization;

namespace Sac311.Integration.Tests.Fakes;

/// <summary>One 311 row served by <see cref="FakeArcGis"/>. Mutable, so a test can edit the "source" between runs.</summary>
internal sealed class FakeFeature
{
    public long ObjectId { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? CategoryLevel1 { get; set; } = "Animal Control";
    public string? CategoryLevel2 { get; set; } = "Dead";
    public string? CategoryName { get; set; } = "Animal Control Dead";
    public string? CouncilDistrictNumber { get; set; } = "District 3";
    public string? SourceLevel1 { get; set; } = "Phone";
    public string? Neighborhood { get; set; } = "South Natomas";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
    public string? CrossStreet { get; set; } = "MOSSY BANK DR";
    public string GlobalId { get; set; } = Guid.NewGuid().ToString();
    public string? Zip { get; set; } = "95833";
    public string? SfTicketId { get; set; } = "5005G00001pBuMbQAK";
    public string? Address { get; set; } = "WATERWHEEL DR & TRUXEL RD, SACRAMENTO, 95833";
    public string? PublicStatus { get; set; } = "NEW";
    public double? X { get; set; } = -121.49787697901314;
    public double? Y { get; set; } = 38.610925694510371;

    /// <summary>
    /// The <paramref name="i"/>th ordinary request: sparse OBJECTIDs, created <paramref name="i"/> + 10 days before
    /// <paramref name="nowUtc"/> and last edited an hour later (well outside the incremental overlap).
    /// </summary>
    public static FakeFeature Typical(int i, DateTime nowUtc)
    {
        var created = nowUtc.Date.AddDays(-(i + 10)).AddHours(9);
        return new FakeFeature
        {
            ObjectId = 2_000_000 + (i * 7L),
            ReferenceNumber = string.Create(CultureInfo.InvariantCulture, $"260901-{3_000_000 + i}"),
            CreatedUtc = created,
            UpdatedUtc = created.AddHours(1),
        };
    }

    /// <summary>Marks the request closed at <paramref name="closedUtc"/>, which is also its new DateUpdated.</summary>
    public FakeFeature Close(DateTime closedUtc)
    {
        PublicStatus = "CLOSED";
        ClosedUtc = closedUtc;
        UpdatedUtc = closedUtc;
        return this;
    }

    public Dictionary<string, object?> Attributes() => new()
    {
        ["OBJECTID"] = ObjectId,
        ["ReferenceNumber"] = ReferenceNumber,
        ["CategoryLevel1"] = CategoryLevel1,
        ["CategoryLevel2"] = CategoryLevel2,
        ["CategoryName"] = CategoryName,
        ["CouncilDistrictNumber"] = CouncilDistrictNumber,
        ["SourceLevel1"] = SourceLevel1,
        ["Neighborhood"] = Neighborhood,
        ["DateCreated"] = Epoch(CreatedUtc),
        ["DateUpdated"] = Epoch(UpdatedUtc),
        ["DateClosed"] = Epoch(ClosedUtc),
        ["CrossStreet"] = CrossStreet,
        ["GlobalID"] = GlobalId,
        ["ZIP"] = Zip,
        ["SFTicketID"] = SfTicketId,
        ["Address"] = Address,
        ["Data_Source"] = "311",
        ["PublicStatus"] = PublicStatus,
    };

    private static long? Epoch(DateTime? utc) =>
        utc is { } u ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;
}
