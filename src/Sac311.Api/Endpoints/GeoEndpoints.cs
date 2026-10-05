using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using NeighborhoodCleaner = Sac311.Domain.Cleaners.Neighborhood;

namespace Sac311.Api.Endpoints;

/// <summary>
/// The neighborhood boundaries for the dashboard's map: <c>data/geo/sacramento-neighborhoods.geojson</c>, embedded at
/// build, with each feature's <c>slug</c> added so the map joins on the same key as the other endpoints.
/// </summary>
internal static class GeoEndpoints
{
    public const string ResourceName = "sacramento-neighborhoods.geojson";
    public const string ContentType = "application/geo+json";

    private static readonly Lazy<byte[]> Neighborhoods = new(Load);

    public static RouteGroupBuilder MapGeoEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/geo/neighborhoods", NeighborhoodBoundaries)
            .WithTags("Map")
            .WithSummary("The 129 neighborhood boundaries (GeoJSON, WGS84), each with the slug the other endpoints use.");
        return api;
    }

    private static FileContentHttpResult NeighborhoodBoundaries() => TypedResults.File(Neighborhoods.Value, ContentType);

    /// <summary>The GeoJSON with <c>properties.slug</c> set from <c>properties.NAME</c> (<see cref="NeighborhoodCleaner.Slug"/>).</summary>
    internal static byte[] Load()
    {
        using var stream = typeof(GeoEndpoints).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        var geo = JsonNode.Parse(stream) ?? throw new InvalidOperationException($"'{ResourceName}' is empty.");
        foreach (var feature in geo["features"]!.AsArray())
        {
            var properties = feature!["properties"]!.AsObject();
            properties["slug"] = NeighborhoodCleaner.Slug(properties["NAME"]?.GetValue<string>());
        }

        return JsonSerializer.SerializeToUtf8Bytes(geo);
    }
}
