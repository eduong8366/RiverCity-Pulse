using System.Text.Json;

namespace Sac311.Ingestion.ArcGis;

/// <summary>What <see cref="SchemaContract.Check"/> found. Missing or retyped fields are drift; added fields are only reported.</summary>
public sealed record SchemaCheckResult(IReadOnlyList<string> Missing, IReadOnlyList<string> Retyped, IReadOnlyList<string> Added, string? GeometryProblem)
{
    public bool IsDrift => Missing.Count > 0 || Retyped.Count > 0 || GeometryProblem is not null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Missing.Count > 0)
        {
            parts.Add("missing " + string.Join(", ", Missing));
        }

        if (Retyped.Count > 0)
        {
            parts.Add("retyped " + string.Join(", ", Retyped));
        }

        if (GeometryProblem is not null)
        {
            parts.Add(GeometryProblem);
        }

        if (Added.Count > 0)
        {
            parts.Add("added " + string.Join(", ", Added));
        }

        return parts.Count == 0 ? "matches" : string.Join("; ", parts);
    }
}

/// <summary>
/// The fields the pipeline reads from the 311 layer (see the Fields table in docs/source-profile.md). Every run checks
/// the live layer description against it first and stops with <c>SchemaDrift</c>, writing nothing, if a field the
/// pipeline reads is gone or changed type. A new field is harmless and only reported.
/// </summary>
public static class SchemaContract
{
    public const string ExpectedGeometryType = "esriGeometryPoint";

    public static readonly IReadOnlyList<(string Name, string Type)> Fields =
    [
        ("OBJECTID", "esriFieldTypeOID"),
        ("ReferenceNumber", "esriFieldTypeString"),
        ("CategoryLevel1", "esriFieldTypeString"),
        ("CategoryLevel2", "esriFieldTypeString"),
        ("CategoryName", "esriFieldTypeString"),
        ("CouncilDistrictNumber", "esriFieldTypeString"),
        ("SourceLevel1", "esriFieldTypeString"),
        ("Neighborhood", "esriFieldTypeString"),
        ("DateCreated", "esriFieldTypeDate"),
        ("DateUpdated", "esriFieldTypeDate"),
        ("DateClosed", "esriFieldTypeDate"),
        ("CrossStreet", "esriFieldTypeString"),
        ("GlobalID", "esriFieldTypeGlobalID"),
        ("ZIP", "esriFieldTypeString"),
        ("SFTicketID", "esriFieldTypeString"),
        ("Address", "esriFieldTypeString"),
        ("Data_Source", "esriFieldTypeString"),
        ("PublicStatus", "esriFieldTypeString"),
    ];

    /// <summary>Compares a layer description (<c>layer?f=json</c>) with <see cref="Fields"/>. Names match case-insensitively, as ArcGIS does.</summary>
    public static SchemaCheckResult Check(JsonElement layer)
    {
        var live = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (layer.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fields.EnumerateArray())
            {
                live[f.GetProperty("name").GetString() ?? ""] = f.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            }
        }

        var missing = new List<string>();
        var retyped = new List<string>();
        foreach (var (name, type) in Fields)
        {
            if (!live.TryGetValue(name, out var liveType))
            {
                missing.Add(name);
            }
            else if (!string.Equals(liveType, type, StringComparison.Ordinal))
            {
                retyped.Add($"{name} ({type} -> {liveType})");
            }
        }

        var expected = Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = live.Keys.Where(n => !expected.Contains(n)).Order(StringComparer.Ordinal).ToList();

        var geometry = layer.TryGetProperty("geometryType", out var g) ? g.GetString() : null;
        var geometryProblem = geometry == ExpectedGeometryType ? null : $"geometry {geometry ?? "(none)"} instead of {ExpectedGeometryType}";
        return new SchemaCheckResult(missing, retyped, added, geometryProblem);
    }
}
