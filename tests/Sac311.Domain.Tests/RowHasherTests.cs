namespace Sac311.Domain.Tests;

public class RowHasherTests
{
    // A fixed row, so the golden value doesn't depend on fixture files.
    private static readonly SourceRow Golden = new()
    {
        ObjectId = 2049272,
        ReferenceNumber = "240101-2172157",
        CategoryLevel1 = "Code Enforcement",
        CategoryLevel2 = "General",
        CategoryName = "Code Enforcement General",
        CouncilDistrictNumber = "District 5",
        SourceLevel1 = "Web",
        Neighborhood = "Airport",
        DateCreated = 1704067320000,
        DateUpdated = 1704925800000,
        DateClosed = 1704925800000,
        CrossStreet = "LA CAMPANA WAY",
        GlobalId = "ff64ac47-56c6-4fc6-952c-8131024b2d46",
        Zip = "95822",
        SfTicketId = "5005G00001pC9TtQAK",
        Address = "1924 34TH AVE, SACRAMENTO, 95822",
        DataSource = "311",
        PublicStatus = "CLOSED",
        X = -121.49543309783193,
        Y = 38.521712206751893,
    };

    [Fact]
    public void Golden_hash_is_stable()
    {
        // If this fails, every stored row_hash changes and the next run rewrites all history. Only update it on purpose.
        Assert.Equal("bf30486b48e261261e97a6e095809adf4c41fb1215541dc58773df4a218f46f7", RowHasher.ToHex(RowHasher.Hash(Golden)));
    }

    [Fact]
    public void Real_fixture_row_matches_the_golden_row() =>
        Assert.Equal(RowHasher.Hash(Golden), RowHasher.Hash(Fixture.Real("typical")[0]));

    [Fact]
    public void Object_id_and_global_id_are_ignored() =>
        Assert.Equal(RowHasher.Hash(Golden), RowHasher.Hash(Golden with { ObjectId = 9, GlobalId = "other" }));

    [Fact]
    public void Any_source_change_changes_the_hash()
    {
        var baseline = RowHasher.Hash(Golden);
        Assert.NotEqual(baseline, RowHasher.Hash(Golden with { PublicStatus = "NEW" }));
        Assert.NotEqual(baseline, RowHasher.Hash(Golden with { DateUpdated = Golden.DateUpdated + 1 }));
        Assert.NotEqual(baseline, RowHasher.Hash(Golden with { X = -121.4954 }));
        Assert.NotEqual(baseline, RowHasher.Hash(Golden with { Address = Golden.Address + " " }));
    }

    [Fact]
    public void Null_and_empty_hash_differently() =>
        Assert.NotEqual(RowHasher.Hash(Golden with { CrossStreet = null }), RowHasher.Hash(Golden with { CrossStreet = "" }));

    [Fact]
    public void Values_cannot_bleed_across_fields() =>
        Assert.NotEqual(
            RowHasher.Hash(Golden with { CategoryLevel1 = "ab", CategoryLevel2 = "c" }),
            RowHasher.Hash(Golden with { CategoryLevel1 = "a", CategoryLevel2 = "bc" }));

    [Fact]
    public void Every_real_fixture_row_hashes_uniquely()
    {
        var rows = Fixture.AllRealNames().SelectMany(Fixture.Real).DistinctBy(r => r.ReferenceNumber).ToList();
        Assert.Equal(rows.Count, rows.Select(r => RowHasher.ToHex(RowHasher.Hash(r))).Distinct().Count());
    }
}
