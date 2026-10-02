using System.Globalization;
using System.Text.Json;

namespace Sac311.Domain;

/// <summary>
/// One feature from the ArcGIS 311 layer exactly as served: strings untrimmed, dates as epoch milliseconds,
/// geometry in WGS84 (the query asks for <c>outSR=4326</c>). Cleaning happens elsewhere.
/// </summary>
public sealed record SourceRow
{
    public long ObjectId { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? CategoryLevel1 { get; init; }
    public string? CategoryLevel2 { get; init; }
    public string? CategoryName { get; init; }
    public string? CouncilDistrictNumber { get; init; }
    public string? SourceLevel1 { get; init; }
    public string? Neighborhood { get; init; }
    public long? DateCreated { get; init; }
    public long? DateUpdated { get; init; }
    public long? DateClosed { get; init; }
    public string? CrossStreet { get; init; }
    public string? GlobalId { get; init; }
    public string? Zip { get; init; }
    public string? SfTicketId { get; init; }
    public string? Address { get; init; }
    public string? DataSource { get; init; }
    public string? PublicStatus { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }

    /// <summary>Reads one element of a query response's <c>features</c> array.</summary>
    public static SourceRow FromFeature(JsonElement feature)
    {
        var a = feature.GetProperty("attributes");
        var hasGeometry = feature.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object;
        return new SourceRow
        {
            ObjectId = Int64(a, "OBJECTID") ?? 0,
            ReferenceNumber = String(a, "ReferenceNumber"),
            CategoryLevel1 = String(a, "CategoryLevel1"),
            CategoryLevel2 = String(a, "CategoryLevel2"),
            CategoryName = String(a, "CategoryName"),
            CouncilDistrictNumber = String(a, "CouncilDistrictNumber"),
            SourceLevel1 = String(a, "SourceLevel1"),
            Neighborhood = String(a, "Neighborhood"),
            DateCreated = Int64(a, "DateCreated"),
            DateUpdated = Int64(a, "DateUpdated"),
            DateClosed = Int64(a, "DateClosed"),
            CrossStreet = String(a, "CrossStreet"),
            GlobalId = String(a, "GlobalID"),
            Zip = String(a, "ZIP"),
            SfTicketId = String(a, "SFTicketID"),
            Address = String(a, "Address"),
            DataSource = String(a, "Data_Source"),
            PublicStatus = String(a, "PublicStatus"),
            X = hasGeometry ? Double(g, "x") : null,
            Y = hasGeometry ? Double(g, "y") : null,
        };
    }

    private static string? String(JsonElement obj, string name) =>
        !obj.TryGetProperty(name, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => v.GetString(),
            _ => v.GetRawText(),
        };

    private static long? Int64(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return v.TryGetInt64(out var n) ? n : (long)v.GetDouble();
    }

    // Empty points come back as null, a missing member, or the string "NaN".
    private static double? Double(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return null;
        }

        var d = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => double.NaN,
        };
        return double.IsFinite(d) ? d : null;
    }
}
