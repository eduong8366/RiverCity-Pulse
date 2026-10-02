using System.Text.Json;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Integration.Tests.ArcGis;

public class SchemaContractTests
{
    private static JsonElement Layer(IEnumerable<(string Name, string Type)> fields, string geometryType = "esriGeometryPoint")
    {
        var json = JsonSerializer.Serialize(new
        {
            name = "SalesForce311",
            geometryType,
            fields = fields.Select(f => new { name = f.Name, type = f.Type }),
        });
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void The_expected_fields_match()
    {
        var result = SchemaContract.Check(Layer(SchemaContract.Fields));

        Assert.False(result.IsDrift);
        Assert.Equal("matches", result.ToString());
    }

    [Fact]
    public void A_renamed_field_is_drift()
    {
        // The M5 incident drill: PublicStatus is served as Status.
        var fields = SchemaContract.Fields.Select(f => f.Name == "PublicStatus" ? ("Status", f.Type) : f);

        var result = SchemaContract.Check(Layer(fields));

        Assert.True(result.IsDrift);
        Assert.Equal(["PublicStatus"], result.Missing);
        Assert.Equal(["Status"], result.Added);
    }

    [Fact]
    public void A_retyped_field_is_drift()
    {
        var fields = SchemaContract.Fields.Select(f => f.Name == "DateUpdated" ? (f.Name, "esriFieldTypeString") : f);

        var result = SchemaContract.Check(Layer(fields));

        Assert.True(result.IsDrift);
        Assert.Equal(["DateUpdated (esriFieldTypeDate -> esriFieldTypeString)"], result.Retyped);
    }

    [Fact]
    public void An_added_field_is_reported_but_not_drift()
    {
        var result = SchemaContract.Check(Layer([.. SchemaContract.Fields, ("Priority", "esriFieldTypeString")]));

        Assert.False(result.IsDrift);
        Assert.Equal(["Priority"], result.Added);
    }

    [Fact]
    public void Field_names_match_case_insensitively() =>
        Assert.False(SchemaContract.Check(Layer(SchemaContract.Fields.Select(f => (f.Name.ToUpperInvariant(), f.Type)))).IsDrift);

    [Fact]
    public void A_non_point_layer_is_drift() =>
        Assert.True(SchemaContract.Check(Layer(SchemaContract.Fields, "esriGeometryPolygon")).IsDrift);
}
