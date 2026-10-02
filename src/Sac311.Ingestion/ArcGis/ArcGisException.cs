using System.Text.Json;

namespace Sac311.Ingestion.ArcGis;

/// <summary>An error ArcGIS reported inside an HTTP 200 response.</summary>
public sealed class ArcGisException : Exception
{
    public ArcGisException(int? code, string message)
        : base(message) => Code = code;

    public int? Code { get; }

    internal static ArcGisException From(JsonElement error, string? where)
    {
        int? code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : null;
        var text = error.TryGetProperty("message", out var m) ? m.GetString() : null;
        var details = error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array
            ? string.Join("; ", d.EnumerateArray().Select(x => x.ToString()))
            : null;
        var message = $"ArcGIS error {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}: {text ?? "(no message)"}"
            + (string.IsNullOrEmpty(details) ? "" : $" ({details})")
            + (where is null ? "" : $" where={where}");
        return new ArcGisException(code, message);
    }
}
