namespace Sac311.Domain.Cleaners;

/// <param name="Longitude">WGS84, rounded to 6 decimals (about 0.1 m), or null.</param>
/// <param name="Latitude">WGS84, rounded to 6 decimals, or null.</param>
public sealed record GeoResult(decimal? Longitude, decimal? Latitude, DqFlags Flags);

public static class Geo
{
    // Sacramento bounding box (WGS84). The verify-source report uses the same box.
    public const double MinLongitude = -122.0;
    public const double MinLatitude = 38.2;
    public const double MaxLongitude = -120.9;
    public const double MaxLatitude = 39.0;

    /// <summary>A missing point stays null with no flag. A point outside the bbox becomes null with <see cref="DqFlags.GeoOutOfBounds"/>.</summary>
    public static GeoResult Validate(double? x, double? y)
    {
        if (x is not { } lon || y is not { } lat || !double.IsFinite(lon) || !double.IsFinite(lat))
        {
            return new(null, null, DqFlags.None);
        }

        var inside = lon is >= MinLongitude and <= MaxLongitude && lat is >= MinLatitude and <= MaxLatitude;
        return inside
            ? new(Math.Round((decimal)lon, 6, MidpointRounding.AwayFromZero), Math.Round((decimal)lat, 6, MidpointRounding.AwayFromZero), DqFlags.None)
            : new(null, null, DqFlags.GeoOutOfBounds);
    }
}
