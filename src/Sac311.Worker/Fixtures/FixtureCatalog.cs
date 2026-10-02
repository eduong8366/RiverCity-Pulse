using Sac311.Domain.Cleaners;

namespace Sac311.Worker.Fixtures;

/// <summary>A WGS84 envelope; the query keeps points that intersect it.</summary>
internal sealed record Envelope(double XMin, double YMin, double XMax, double YMax);

internal sealed record FixtureQuery(string Where, int Count = 3, Envelope? Within = null);

internal sealed record FixtureSpec(string Name, string Description, IReadOnlyList<FixtureQuery> Queries);

/// <summary>Real rows from the live feed that exercise each cleaning rule. Saved by <c>worker capture-fixture</c>.</summary>
internal static class FixtureCatalog
{
    public static readonly IReadOnlyList<FixtureSpec> All =
    [
        new("address-empty", "Address is '' (ArcGIS counts it as non-null).",
            [new("Address = ''")]),
        new("address-junk", "Address is a placeholder (N/A, TBD, Ok, Zoom) or 1-2 characters.",
        [
            new("Address = 'N/A'", 1), new("Address = 'TBD'", 1), new("UPPER(Address) = 'OK'", 1),
            new("UPPER(Address) = 'ZOOM'", 1), new("CHAR_LENGTH(Address) BETWEEN 1 AND 2 AND UPPER(Address) <> 'OK'", 2),
        ]),
        new("closed-before-created", "DateClosed is earlier than DateCreated.",
            [new("DateClosed < DateCreated")]),
        new("closed-missing-date", "PublicStatus is CLOSED but DateClosed is null.",
            [new("PublicStatus = 'CLOSED' AND DateClosed IS NULL")]),
        new("source-google-ai", "Both spellings of the Google AI channel.",
            [new("SourceLevel1 = 'GoogleAI'", 2), new("SourceLevel1 = 'Google AI'", 1)]),
        new("homeless-camp", "Homeless Camp - Primary, which merges into Homeless Camp.",
            [new("CategoryLevel1 = 'Homeless Camp - Primary'", 2), new("CategoryLevel1 = 'Homeless Camp'", 1)]),
        new("non-city-no-neighborhood", "Outside the city: Non City district and a null neighborhood.",
            [new("CouncilDistrictNumber = 'Non City' AND Neighborhood IS NULL")]),
        new("city-no-neighborhood", "Inside a council district but with a null neighborhood.",
            [new("CouncilDistrictNumber <> 'Non City' AND Neighborhood IS NULL")]),
        new("neighborhood-slashes", "Neighborhood names with slashes, with and without spaces.",
            [new("Neighborhood = 'College/Glen'", 2), new("Neighborhood = 'Midtown / Winn Park / Capital Avenue'", 1)]),
        new("outside-bbox", "Points outside the Sacramento bounding box (one query per side).",
        [
            new("1=1", 1, new(-180, -90, Edge(Geo.MinLongitude, -1), 90)),
            new("1=1", 1, new(Edge(Geo.MaxLongitude, 1), -90, 180, 90)),
            new("1=1", 1, new(Geo.MinLongitude, -90, Geo.MaxLongitude, Edge(Geo.MinLatitude, -1))),
            new("1=1", 1, new(Geo.MinLongitude, Edge(Geo.MaxLatitude, 1), Geo.MaxLongitude, 90)),
        ]),
        new("category-blank", "CategoryLevel1 is '', so it can't be mapped.",
            [new("CategoryLevel1 = ''", 2)]),
        new("source-blank", "SourceLevel1 is '', so it can't be mapped.",
            [new("SourceLevel1 = ''", 2)]),
        new("typical", "Ordinary rows in each status, for happy-path tests.",
        [
            new("PublicStatus = 'CLOSED' AND DateClosed >= DateCreated AND Address <> '' AND Neighborhood IS NOT NULL", 2),
            new("PublicStatus = 'NEW'", 1),
            new("PublicStatus = 'IN PROGRESS'", 1),
            new("PublicStatus = 'CANCELLED'", 1),
        ]),
    ];

    // One micro-degree past the bbox edge, rounded so the saved fixture shows clean numbers.
    private static double Edge(double bound, int direction) => Math.Round(bound + (direction * 1e-6), 6);
}
