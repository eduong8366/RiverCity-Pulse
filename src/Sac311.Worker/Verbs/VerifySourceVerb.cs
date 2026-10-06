using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sac311.Domain;
using Sac311.Domain.Cleaners;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Worker.Verbs;

/// <summary>
/// Profiles the live 311 layer and writes docs/source-profile.md: row count, key uniqueness, paging cost, date ranges,
/// sentinel and inconsistent dates, blank strings, category and channel values, geometry bounds and the schema contract.
/// It also saves the neighborhood boundaries to data/geo/ and measures how well the 311 names match them. Read-only
/// against the source. The section between the <c>manual</c> markers in an existing profile is kept.
/// Exit code 3 means the layer no longer matches <see cref="SchemaContract"/> (the report is still written).
/// <para>
/// With <c>--check</c> it is the nightly source check instead (<c>live-contract.yml</c>): it fetches only the layer
/// metadata and one count, writes nothing, and exits 0 when the contract matches and the count is within
/// <see cref="SourceCountCheck"/>'s tolerance of the profile's "Row count", 3 on drift or a count change past it, 1 on error.
/// </para>
/// </summary>
internal sealed partial class VerifySourceVerb(ArcGisClient client, IOptions<ArcGisOptions> options, ILogger<VerifySourceVerb> logger) : IVerb
{
    private const string ItemId = "5b9a9448663f41b1898643b6d91201c4";
    private const string NeighborhoodsUrl = "https://services5.arcgis.com/54falWtcpty3V47Z/arcgis/rest/services/Neighborhoods/FeatureServer/0";
    private const string NeighborhoodsItemId = "49f20f1612ae4f0a9292eb65f8bd4013";
    private const string ManualStart = "<!-- manual:start -->";
    private const string ManualEnd = "<!-- manual:end -->";

    // Share of non-null neighborhood rows that must match a polygon to use the neighborhood map.
    private const double MatchThreshold = 0.95;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DateFields = ["DateCreated", "DateUpdated", "DateClosed"];
    private static readonly string[] TextFields =
        ["Address", "CrossStreet", "CategoryLevel1", "CategoryLevel2", "CategoryName", "SourceLevel1", "Neighborhood", "ZIP", "CouncilDistrictNumber", "SFTicketID"];

    public string Name => "verify-source";

    public string Usage => "verify-source [--out <file>] [--geojson-out <file>] [--check]   profile the live feed into docs/source-profile.md; --check only compares the schema and row count with it";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? outFile = null;
        string? geoJsonOut = null;
        var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    check = true;
                    break;
                case "--out" when i + 1 < args.Length:
                    outFile = args[++i];
                    break;
                case "--geojson-out" when i + 1 < args.Length:
                    geoJsonOut = args[++i];
                    break;
                default:
                    LogUnknownArgument(logger, args[i]);
                    return 2;
            }
        }

        var root = RepoPaths.FindRoot();
        outFile ??= root is null ? null : Path.Combine(root, "docs", "source-profile.md");
        if (check)
        {
            // The profile at --out is the baseline here: read, never written.
            if (outFile is null)
            {
                LogNoRepo(logger);
                return 2;
            }

            return await CheckAsync(outFile, cancellationToken).ConfigureAwait(false);
        }

        geoJsonOut ??= root is null ? null : Path.Combine(root, "data", "geo", "sacramento-neighborhoods.geojson");
        if (outFile is null || geoJsonOut is null)
        {
            LogNoRepo(logger);
            return 2;
        }

        var started = DateTime.UtcNow;
        var serviceUrl = options.Value.BaseUrl.ToString().TrimEnd('/');
        LogStart(logger, serviceUrl);
        var md = new Markdown();
        var drift = await ProfileAsync(md, serviceUrl, started, outFile, geoJsonOut, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        await File.WriteAllTextAsync(outFile, md.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        LogDone(logger, (DateTime.UtcNow - started).TotalSeconds, outFile);

        if (drift.IsDrift)
        {
            LogDrift(logger, drift);
            return 3;
        }

        return 0;
    }

    /// <summary>The nightly check: schema contract and row count against the committed profile, nothing written.</summary>
    private async Task<int> CheckAsync(string profilePath, CancellationToken ct)
    {
        string profile;
        try
        {
            profile = await File.ReadAllTextAsync(profilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCheckError(logger, ex.Message);
            return 1;
        }

        if (!SourceCountCheck.TryParseBaseline(profile, out var baseline))
        {
            LogNoBaseline(logger, profilePath);
            return 1;
        }

        var serviceUrl = options.Value.BaseUrl.ToString().TrimEnd('/');
        LogCheckStart(logger, serviceUrl, profilePath);
        SchemaCheckResult contract;
        long count;
        try
        {
            using (var layer = await client.GetLayerAsync(ct).ConfigureAwait(false))
            {
                contract = SchemaContract.Check(layer.RootElement);
            }

            count = await client.CountAsync("1=1", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckError(logger, ex.Message);
            return 1;
        }

        var result = SourceCountCheck.Compare(baseline, count);
        LogCheckCount(logger, result.Count, result.Baseline, result.Change);
        var exitCode = 0;
        if (contract.IsDrift)
        {
            LogDrift(logger, contract);
            exitCode = 3;
        }
        else
        {
            LogContract(logger, contract);
        }

        switch (result.Outcome)
        {
            case SourceCountOutcome.Grew:
                LogCountGrew(logger, result.Change, SourceCountCheck.Tolerance);
                exitCode = 3;
                break;
            case SourceCountOutcome.Dropped:
                LogCountDropped(logger, result.Change, SourceCountCheck.Tolerance);
                exitCode = 3;
                break;
        }

        return exitCode;
    }

    private async Task<SchemaCheckResult> ProfileAsync(Markdown md, string serviceUrl, DateTime started, string outFile, string geoJsonOut, CancellationToken ct)
    {
        // ---------------------------------------------------------------- service + layer metadata
        using var layerDoc = await client.GetLayerAsync(ct).ConfigureAwait(false);
        var layer = layerDoc.RootElement;
        using var itemResponse = await client.PostAsync($"https://www.arcgis.com/sharing/rest/content/items/{ItemId}", Empty, ct).ConfigureAwait(false);
        using var nbItemResponse = await client.PostAsync($"https://www.arcgis.com/sharing/rest/content/items/{NeighborhoodsItemId}", Empty, ct).ConfigureAwait(false);
        var item = itemResponse.Json.RootElement;
        var nbItem = nbItemResponse.Json.RootElement;
        var lastEdit = layer.TryGetProperty("editingInfo", out var editing) ? FromEsri(Number(Prop(editing, "lastEditDate"))) : null;
        var contract = SchemaContract.Check(layer);

        // ---------------------------------------------------------------- counts and keys
        LogStep(logger, "counts and keys");
        var total = await Count("1=1", ct).ConfigureAwait(false);
        var refBlank = await Count("ReferenceNumber IS NULL OR ReferenceNumber = ''", ct).ConfigureAwait(false);
        long? distinctRef = null;
        try
        {
            using var distinct = await client.QueryAsync(new Dictionary<string, string>
            {
                ["where"] = "1=1", ["outFields"] = "ReferenceNumber", ["returnDistinctValues"] = "true", ["returnCountOnly"] = "true",
            }, ct).ConfigureAwait(false);
            distinctRef = distinct.RootElement.GetProperty("count").GetInt64();
        }
        catch (ArcGisException ex)
        {
            LogUnsupported(logger, "Distinct ReferenceNumber count", ex.Message);
        }

        var oid = await Stats("1=1", Stat("min", "OBJECTID", "lo"), Stat("max", "OBJECTID", "hi"), ct).ConfigureAwait(false);
        var oidLo = (long)Number(Prop(oid, "lo"))!;
        var oidHi = (long)Number(Prop(oid, "hi"))!;
        string? sample;
        using (var s = await client.QueryAsync(new Dictionary<string, string>
               {
                   ["where"] = "1=1", ["outFields"] = "ReferenceNumber", ["resultRecordCount"] = "1", ["returnGeometry"] = "false",
               }, ct).ConfigureAwait(false))
        {
            sample = Text(Prop(s.RootElement.GetProperty("features")[0].GetProperty("attributes"), "ReferenceNumber"));
        }

        // ---------------------------------------------------------------- paging cost
        LogStep(logger, "paging cost");
        var pageSize = layer.GetProperty("maxRecordCount").GetInt32();
        var pageParams = new Dictionary<string, string>
        {
            ["outFields"] = "*", ["orderByFields"] = "OBJECTID", ["resultRecordCount"] = I(pageSize), ["outSR"] = "4326",
        };
        using var keyset = await client.PostAsync("query", new Dictionary<string, string>(pageParams) { ["where"] = $"OBJECTID > {I(oidLo - 1)}" }, ct)
            .ConfigureAwait(false);
        var keysetRows = keyset.Json.RootElement.GetProperty("features").GetArrayLength();
        var deepOffset = (long)Math.Floor(total * 0.95);
        using var offset = await client.PostAsync("query", new Dictionary<string, string>(pageParams) { ["where"] = "1=1", ["resultOffset"] = I(deepOffset) }, ct)
            .ConfigureAwait(false);
        var pages = (long)Math.Ceiling((double)total / pageSize);

        // ---------------------------------------------------------------- dates
        LogStep(logger, "dates");
        var nowUtc = DateTime.UtcNow;
        var dateRows = new List<string[]>();
        foreach (var f in DateFields)
        {
            var st = await Stats("1=1", Stat("min", f, "lo"), Stat("max", f, "hi"), ct).ConfigureAwait(false);
            dateRows.Add(
            [
                f, Date(FromEsri(Number(Prop(st, "lo")))), Date(FromEsri(Number(Prop(st, "hi")))),
                N(await Count($"{f} IS NULL", ct).ConfigureAwait(false)),
                N(await Count($"{f} < TIMESTAMP '2000-01-01 00:00:00'", ct).ConfigureAwait(false)),
                N(await Count($"{f} > {SqlUtc(nowUtc.AddDays(1))}", ct).ConfigureAwait(false)),
            ]);
        }

        var updated24h = await Count($"DateUpdated > {SqlUtc(nowUtc.AddDays(-1))}", ct).ConfigureAwait(false);
        var closedBeforeCreated = await Count("DateClosed < DateCreated", ct).ConfigureAwait(false);
        var closedNoDate = await Count("PublicStatus = 'CLOSED' AND DateClosed IS NULL", ct).ConfigureAwait(false);
        var openWithDate = await Count("PublicStatus IN ('NEW','IN PROGRESS') AND DateClosed IS NOT NULL", ct).ConfigureAwait(false);

        // ---------------------------------------------------------------- strings
        LogStep(logger, "strings and categories");
        var blankRows = new List<(string Field, long Nulls, long Empty)>();
        foreach (var f in TextFields)
        {
            blankRows.Add((f, await Count($"{f} IS NULL", ct).ConfigureAwait(false), await Count($"{f} = ''", ct).ConfigureAwait(false)));
        }

        var junkAddress = await Count("UPPER(Address) IN ('N/A','NA','TBD','OK','ZOOM','NONE','UNKNOWN')", ct).ConfigureAwait(false);
        long? zipBad = null;
        try
        {
            zipBad = await Count("ZIP IS NOT NULL AND ZIP <> '' AND CHAR_LENGTH(ZIP) <> 5", ct).ConfigureAwait(false);
        }
        catch (ArcGisException ex)
        {
            LogUnsupported(logger, "ZIP length check", ex.Message);
        }

        var status = await GroupCounts("PublicStatus", ct).ConfigureAwait(false);
        var district = await GroupCounts("CouncilDistrictNumber", ct).ConfigureAwait(false);
        var cat1 = await GroupCounts("CategoryLevel1", ct).ConfigureAwait(false);
        var cat2 = await GroupCounts("CategoryLevel2", ct).ConfigureAwait(false);
        var source = await GroupCounts("SourceLevel1", ct).ConfigureAwait(false);
        var dataSource = await GroupCounts("Data_Source", ct).ConfigureAwait(false);
        var hoods = await GroupCounts("Neighborhood", ct).ConfigureAwait(false);

        // ---------------------------------------------------------------- geometry
        LogStep(logger, "geometry");
        var envelope = string.Create(Inv,
            $$$"""{"xmin":{{{Geo.MinLongitude}}},"ymin":{{{Geo.MinLatitude}}},"xmax":{{{Geo.MaxLongitude}}},"ymax":{{{Geo.MaxLatitude}}},"spatialReference":{"wkid":4326}}""");
        long inBbox;
        using (var b = await client.QueryAsync(new Dictionary<string, string>
               {
                   ["where"] = "1=1", ["geometry"] = envelope, ["geometryType"] = "esriGeometryEnvelope", ["inSR"] = "4326",
                   ["spatialRel"] = "esriSpatialRelIntersects", ["returnCountOnly"] = "true",
               }, ct).ConfigureAwait(false))
        {
            inBbox = b.RootElement.GetProperty("count").GetInt64();
        }

        // ---------------------------------------------------------------- neighborhood boundaries
        LogStep(logger, "neighborhood boundaries");
        using var geo = await client.PostAsync(NeighborhoodsUrl + "/query", new Dictionary<string, string>
        {
            ["where"] = "1=1", ["outFields"] = "NAME", ["outSR"] = "4326", ["geometryPrecision"] = "6", ["f"] = "geojson",
        }, ct).ConfigureAwait(false);
        var polygons = geo.Json.RootElement.GetProperty("features").EnumerateArray().ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(geoJsonOut))!);
        await File.WriteAllBytesAsync(geoJsonOut, Compact(geo.Json.RootElement), ct).ConfigureAwait(false);

        var polyNames = polygons.Select(p => Text(Prop(p.GetProperty("properties"), "NAME"))).OfType<string>().ToList();
        var polyKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in polyNames)
        {
            if (MapKey.For(n) is { } k)
            {
                polyKeys[k] = n;
            }
        }

        var polyExact = polyNames.ToHashSet(StringComparer.Ordinal);
        var named = hoods.Where(h => !string.IsNullOrWhiteSpace(h.Value)).ToList();
        var namedRows = named.Sum(h => h.Count);
        var hoodMatch = named.Select(h =>
        {
            var exact = polyExact.Contains(h.Value!);
            var key = MapKey.For(h.Value);
            var polygon = key is not null && polyKeys.TryGetValue(key, out var p) ? p : null;
            return (h.Value, h.Count, Exact: exact, Normalized: exact || polygon is not null, Polygon: polygon);
        }).ToList();
        var exactNames = hoodMatch.Count(m => m.Exact);
        var normNames = hoodMatch.Count(m => m.Normalized);
        var normRows = hoodMatch.Where(m => m.Normalized).Sum(m => m.Count);
        var matchRate = namedRows > 0 ? (double)normRows / namedRows : 0;
        var usedPolygons = hoodMatch.Select(m => m.Polygon).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var orphanPolygons = polyNames.Where(n => !usedPolygons.Contains(n)).Order(StringComparer.InvariantCultureIgnoreCase).ToList();
        var mapDecision = matchRate >= MatchThreshold
            ? $"**Use the neighborhood choropleth.** {Pct(normRows, namedRows)} of rows with a neighborhood match a polygon after normalization (threshold {Pct(MatchThreshold, 1)}). Unmatched names go in `ref.neighborhood_alias`."
            : $"**Fall back to council districts.** Only {Pct(normRows, namedRows)} of rows with a neighborhood match a polygon after normalization (threshold {Pct(MatchThreshold, 1)}).";

        // ---------------------------------------------------------------- write markdown
        LogStep(logger, "writing report");
        var manual = ReadManualSection(outFile) ?? $"{ManualStart}\n## License and terms (manual)\n\n_Not reviewed yet._\n{ManualEnd}";

        md.Line("# Source profile: Sacramento 311");
        md.Line();
        md.Line($"Generated by `worker verify-source` on {started.ToString("yyyy-MM-dd HH:mm", Inv)} UTC. Rerun `dotnet run --project src/Sac311.Worker -- verify-source` to refresh; only the section between the `manual` markers is hand-written and is kept across runs.");
        md.Line();

        md.Line("## Service");
        md.Line();
        md.Table(["Property", "Value"],
        [
            ["Item", $"[{Text(Prop(item, "title"))}](https://www.arcgis.com/home/item.html?id={ItemId})"],
            ["Layer URL", $"`{serviceUrl}`"],
            ["Layer name", Text(Prop(layer, "name")) ?? ""],
            ["Owner", Text(Prop(item, "owner")) ?? ""],
            ["Item description", (Text(Prop(item, "snippet")) ?? "").Replace("|", "/", StringComparison.Ordinal)],
            ["Max records per page", I(pageSize)],
            ["Supports pagination", Bool(Prop(Prop(layer, "advancedQueryCapabilities"), "supportsPagination"))],
            ["Supports statistics", Bool(Prop(Prop(layer, "advancedQueryCapabilities"), "supportsStatistics"))],
            ["Native spatial reference", Text(Prop(Prop(Prop(layer, "extent"), "spatialReference"), "latestWkid")) ?? ""],
            ["Last edit", Date(lastEdit)],
        ]);

        md.Line("### Fields (schema contract)");
        md.Line();
        md.Table(["Field", "Type", "Length"], layer.GetProperty("fields").EnumerateArray().Select(f => new[]
        {
            $"`{Text(Prop(f, "name"))}`",
            (Text(Prop(f, "type")) ?? "").Replace("esriFieldType", "", StringComparison.Ordinal),
            Text(Prop(f, "length")) ?? "",
        }));
        md.Line(contract.IsDrift
            ? $"Schema contract (`SchemaContract`): **drift**, {contract}. Ingestion runs stop with `SchemaDrift` until the contract is updated."
            : $"Schema contract (`SchemaContract`): **{contract}** ({SchemaContract.Fields.Count} fields, point geometry).");
        md.Line();

        md.Line("## Volume and keys");
        md.Line();
        md.Table(["Check", "Result"],
        [
            ["Row count", N(total)],
            ["ReferenceNumber null or blank", N(refBlank)],
            ["Distinct ReferenceNumber", distinctRef is { } d ? $"{N(d)} ({Pct(d, total)} of rows)" : "not supported"],
            ["Sample ReferenceNumber", $"`{sample}`"],
            ["OBJECTID range", $"{N(oidLo)} to {N(oidHi)} (span {N(oidHi - oidLo + 1)}, density {Pct(total, oidHi - oidLo + 1)})"],
            ["Updated in the last 24 h", N(updated24h)],
        ]);

        md.Line("## Paging cost");
        md.Line();
        md.Table(["Query", "Rows", "Time", "Payload (decompressed)"],
        [
            ["Keyset `OBJECTID > min-1`", I(keysetRows), $"{Ms(keyset)} ms", $"{Mb(keyset)} MB"],
            [$"Offset {N(deepOffset)}", I(offset.Json.RootElement.GetProperty("features").GetArrayLength()), $"{Ms(offset)} ms", $"{Mb(offset)} MB"],
        ]);
        md.Line($"A full keyset backfill is about **{N(pages)} pages** of {I(pageSize)} rows, roughly {D(Math.Round(pages * Ms(keyset) / 60000.0, 1))} minutes of fetch time at the measured keyset speed.");
        md.Line();

        md.Line("## Dates");
        md.Line();
        md.Table(["Field", "Min", "Max", "NULL", "Before 2000", "Future (> now + 1 day)"], dateRows);
        md.Table(["Consistency check", "Rows"],
        [
            ["DateClosed < DateCreated", N(closedBeforeCreated)],
            ["CLOSED with no DateClosed", N(closedNoDate)],
            ["NEW / IN PROGRESS with a DateClosed", N(openWithDate)],
        ]);

        md.Line("## Status");
        md.Line();
        md.Table(["PublicStatus", "Rows", "Share"], status.Select(s => new[] { Value(s.Value), N(s.Count), Pct(s.Count, total) }));

        md.Line("## Blank and junk strings");
        md.Line();
        md.Line("ArcGIS counts '' as non-null, so both are listed.");
        md.Line();
        md.Table(["Field", "NULL", "Empty ('')", "Blank share"], blankRows.Select(r => new[] { $"`{r.Field}`", N(r.Nulls), N(r.Empty), Pct(r.Nulls + r.Empty, total) }));
        md.Line($"Address junk tokens (N/A, NA, TBD, OK, ZOOM, NONE, UNKNOWN): **{N(junkAddress)}** rows.");
        md.Line();
        md.Line($"ZIP values that are not 5 characters: **{(zipBad is { } z ? N(z) : "check not supported")}**.");
        md.Line();

        md.Line("## Council district");
        md.Line();
        md.Table(["CouncilDistrictNumber", "Rows"], district.Select(r => new[] { Value(r.Value), N(r.Count) }));

        md.Line($"## Categories ({cat1.Count} level-1 values, {cat2.Count} level-2 values)");
        md.Line();
        md.Table(["CategoryLevel1", "Rows"], cat1.Select(r => new[] { Value(r.Value), N(r.Count) }));

        md.Line($"## Source channel ({source.Count} values)");
        md.Line();
        md.Table(["SourceLevel1", "Rows"], source.Select(r => new[] { Value(r.Value), N(r.Count) }));
        md.Table(["Data_Source", "Rows"], dataSource.Select(r => new[] { Value(r.Value), N(r.Count) }));

        md.Line("## Geometry");
        md.Line();
        md.Line($"Rows inside the bbox ({D(Geo.MinLongitude)}, {D(Geo.MinLatitude)}, {D(Geo.MaxLongitude)}, {D(Geo.MaxLatitude)}): **{N(inBbox)}** of {N(total)} ({Pct(inBbox, total)}). The rest have no point or a point outside it.");
        md.Line();

        md.Line("## Neighborhoods vs. boundary file");
        md.Line();
        md.Line($"Boundaries: [{Text(Prop(nbItem, "title"))}](https://www.arcgis.com/home/item.html?id={NeighborhoodsItemId}) ({polygons.Count} polygons, saved to `data/geo/{Path.GetFileName(geoJsonOut)}`, WGS84, 6-decimal precision).");
        md.Line();
        md.Table(["Check", "Result"],
        [
            ["Distinct 311 neighborhood names (non-blank)", I(named.Count)],
            ["Rows with a neighborhood", N(namedRows)],
            ["Rows with NULL or blank neighborhood", N(total - namedRows)],
            ["Names matching a polygon exactly", $"{exactNames} of {named.Count}"],
            ["Names matching after normalization (lowercase, alphanumerics only)", $"{normNames} of {named.Count}"],
            ["Row-weighted match rate (normalized)", $"**{Pct(normRows, namedRows)}**"],
            ["Polygons with no 311 name", I(orphanPolygons.Count)],
        ]);
        md.Line($"Decision: {mapDecision}");
        md.Line();

        var unmatched = hoodMatch.Where(m => !m.Normalized).OrderByDescending(m => m.Count).ToList();
        if (unmatched.Count > 0)
        {
            md.Line("### 311 names with no polygon");
            md.Line();
            md.Table(["Neighborhood", "Rows"], unmatched.Select(m => new[] { Value(m.Value), N(m.Count) }));
        }

        var normOnly = hoodMatch.Where(m => m.Normalized && !m.Exact).OrderBy(m => m.Value, StringComparer.InvariantCultureIgnoreCase).ToList();
        if (normOnly.Count > 0)
        {
            md.Line("### Names that match only after normalization");
            md.Line();
            md.Table(["311 name", "Polygon NAME", "Rows"], normOnly.Select(m => new[] { Value(m.Value), Value(m.Polygon), N(m.Count) }));
        }

        if (orphanPolygons.Count > 0)
        {
            md.Line("### Polygons with no 311 name");
            md.Line();
            md.Line(string.Join(", ", orphanPolygons.Select(Value)));
            md.Line();
        }

        md.Line("## Item metadata (license fields)");
        md.Line();
        md.Table(["Item", "licenseInfo", "accessInformation", "Tags"],
        [
            ["311 calls", LicenseInfo(item), AccessInformation(item), Tags(item)],
            ["Neighborhoods", LicenseInfo(nbItem), AccessInformation(nbItem), Tags(nbItem)],
        ]);

        md.Line(manual);
        LogSummary(logger, total, matchRate, contract);
        return contract;
    }

    // ---------------------------------------------------------------- queries

    private static readonly Dictionary<string, string> Empty = [];

    private Task<long> Count(string where, CancellationToken ct) => client.CountAsync(where, ct);

    private static string Stat(string type, string field, string name) =>
        $$"""{"statisticType":"{{type}}","onStatisticField":"{{field}}","outStatisticFieldName":"{{name}}"}""";

    private async Task<JsonElement> Stats(string where, string stat1, string stat2, CancellationToken ct)
    {
        using var doc = await client.QueryAsync(new Dictionary<string, string> { ["where"] = where, ["outStatistics"] = $"[{stat1},{stat2}]" }, ct)
            .ConfigureAwait(false);
        return doc.RootElement.GetProperty("features")[0].GetProperty("attributes").Clone();
    }

    private async Task<List<(string? Value, long Count)>> GroupCounts(string field, CancellationToken ct)
    {
        using var doc = await client.QueryAsync(new Dictionary<string, string>
        {
            ["where"] = "1=1",
            ["groupByFieldsForStatistics"] = field,
            ["outStatistics"] = """[{"statisticType":"count","onStatisticField":"OBJECTID","outStatisticFieldName":"n"}]""",
            ["orderByFields"] = "n DESC",
        }, ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("attributes"))
            .Select(a => (Text(Prop(a, field)), (long)(Number(Prop(a, "n")) ?? 0)))
            .ToList();
    }

    // ---------------------------------------------------------------- JSON helpers (ArcGIS echoes statistic names in varying case)

    private static JsonElement? Prop(JsonElement? obj, string name)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o)
        {
            return null;
        }

        if (o.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var p in o.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement? e) => e switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { } v => v.GetRawText(),
    };

    private static double? Number(JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n ? n.GetDouble() : null;

    private static string Bool(JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        _ => "",
    };

    private static DateTime? FromEsri(double? ms) => ms is { } v ? DateTimeOffset.FromUnixTimeMilliseconds((long)v).UtcDateTime : null;

    private static string LicenseInfo(JsonElement item) => string.IsNullOrEmpty(Text(Prop(item, "licenseInfo"))) ? "*(blank)*" : "set (see item page)";

    private static string AccessInformation(JsonElement item) => Text(Prop(item, "accessInformation")) is { Length: > 0 } a ? a : "*(blank)*";

    private static string Tags(JsonElement item) => Prop(item, "tags") is { ValueKind: JsonValueKind.Array } tags
        ? string.Join(", ", tags.EnumerateArray().Take(12).Select(t => t.GetString()))
        : "";

    // Same escaping as the PowerShell original (ConvertTo-Json): non-ASCII and HTML-sensitive characters as \uXXXX.
    private static byte[] Compact(JsonElement root)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            root.WriteTo(w);
        }

        return buffer.ToArray();
    }

    // ---------------------------------------------------------------- formatting

    private static string I(long n) => n.ToString(Inv);

    private static string D(double d) => d.ToString(Inv);

    private static string N(long n) => n.ToString("N0", Inv);

    private static string Pct(double part, double whole) => whole == 0 ? "n/a" : (part / whole).ToString("P1", Inv);

    private static string Date(DateTime? d) => d is { } v ? v.ToString("yyyy-MM-dd HH:mm", Inv) + " UTC" : "null";

    private static string Value(string? v) => v switch
    {
        null => "*(null)*",
        "" => "*(empty)*",
        _ => "`" + v.Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal) + "`",
    };

    private static string SqlUtc(DateTime d) => "TIMESTAMP '" + d.ToString("yyyy-MM-dd HH:mm:ss", Inv) + "'";

    private static long Ms(ArcGisResponse r) => (long)r.Elapsed.TotalMilliseconds;

    private static string Mb(ArcGisResponse r) => D(Math.Round(r.Body.Length / 1048576.0, 2));

    private static string? ReadManualSection(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var existing = File.ReadAllText(path);
        var i = existing.IndexOf(ManualStart, StringComparison.Ordinal);
        var j = existing.IndexOf(ManualEnd, StringComparison.Ordinal);
        return i >= 0 && j > i ? existing[i..(j + ManualEnd.Length)] : null;
    }

    private sealed class Markdown
    {
        private readonly StringBuilder _sb = new();

        public void Line(string s = "") => _sb.Append(s).Append('\n');

        public void Table(string[] headers, IEnumerable<string[]> rows)
        {
            Line("| " + string.Join(" | ", headers) + " |");
            Line("|" + string.Join("|", headers.Select(_ => "---")) + "|");
            foreach (var r in rows)
            {
                Line("| " + string.Join(" | ", r) + " |");
            }

            Line();
        }

        public override string ToString() => _sb.ToString();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unknown argument '{Argument}'.")]
    private static partial void LogUnknownArgument(ILogger logger, string argument);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't find the repository root (Sac311.slnx). Pass --out and --geojson-out.")]
    private static partial void LogNoRepo(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Profiling {Url}")]
    private static partial void LogStart(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "  {Step}")]
    private static partial void LogStep(ILogger logger, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Check} not supported: {Error}")]
    private static partial void LogUnsupported(ILogger logger, string check, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rows {Rows:N0}, neighborhood match {Match:P1}, schema contract {Contract}.")]
    private static partial void LogSummary(ILogger logger, long rows, double match, SchemaCheckResult contract);

    [LoggerMessage(Level = LogLevel.Information, Message = "Done in {Seconds:N0} s. Wrote {Path}")]
    private static partial void LogDone(ILogger logger, double seconds, string path);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Schema drift: {Drift}")]
    private static partial void LogDrift(ILogger logger, SchemaCheckResult drift);

    [LoggerMessage(Level = LogLevel.Information, Message = "Checking {Url} against the baseline in {Profile}")]
    private static partial void LogCheckStart(ILogger logger, string url, string profile);

    [LoggerMessage(Level = LogLevel.Error, Message = "No \"Row count\" baseline in {Path}. Rerun `worker verify-source` to write the profile.")]
    private static partial void LogNoBaseline(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Source check failed: {Error}")]
    private static partial void LogCheckError(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Row count {Count:N0}, baseline {Baseline:N0}, change {Change:+0.0%;-0.0%;0.0%}.")]
    private static partial void LogCheckCount(ILogger logger, long count, long baseline, double change);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema contract {Contract}.")]
    private static partial void LogContract(ILogger logger, SchemaCheckResult contract);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Row count grew {Change:+0.0%} since the baseline, past ±{Tolerance:0%}. Usually normal growth (~1,500 rows a day): rerun `dotnet run --project src/Sac311.Worker -- verify-source` and commit docs/source-profile.md to refresh the baseline.")]
    private static partial void LogCountGrew(ILogger logger, double change, double tolerance);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Row count dropped {Change:0.0%} since the baseline, past ±{Tolerance:0%}. The feed lost rows or was republished; check it before the next reconcile (its 95% guard stops a truncated feed).")]
    private static partial void LogCountDropped(ILogger logger, double change, double tolerance);
}
