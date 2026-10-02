using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sac311.Ingestion.ArcGis;
using Sac311.Worker.Fixtures;

namespace Sac311.Worker.Verbs;

/// <summary>
/// Saves real rows from the live feed as test fixtures (tests/Sac311.Domain.Tests/Fixtures/real/&lt;name&gt;.json),
/// one file per <see cref="FixtureCatalog"/> entry. Features are stored exactly as the query returned them.
/// </summary>
internal sealed partial class CaptureFixtureVerb(ArcGisClient client, IOptions<ArcGisOptions> options, ILogger<CaptureFixtureVerb> logger) : IVerb
{
    public string Name => "capture-fixture";

    public string Usage => "capture-fixture [name ...] [--out <dir>] [--list]   save real bad rows as unit-test fixtures";

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? outDir = null;
        var names = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list":
                    foreach (var f in FixtureCatalog.All)
                    {
                        Console.WriteLine($"{f.Name,-26} {f.Description}");
                    }

                    return 0;
                case "--out" when i + 1 < args.Length:
                    outDir = args[++i];
                    break;
                default:
                    names.Add(args[i]);
                    break;
            }
        }

        var unknown = names.Except(FixtureCatalog.All.Select(f => f.Name)).ToList();
        if (unknown.Count > 0)
        {
            LogUnknown(logger, string.Join(", ", unknown));
            return 2;
        }

        outDir ??= RepoPaths.FindRoot() is { } root ? Path.Combine(root, "tests", "Sac311.Domain.Tests", "Fixtures", "real") : null;
        if (outDir is null)
        {
            LogNoRepo(logger);
            return 2;
        }

        Directory.CreateDirectory(outDir);
        var specs = names.Count == 0 ? FixtureCatalog.All : FixtureCatalog.All.Where(f => names.Contains(f.Name)).ToList();
        var failed = 0;
        foreach (var spec in specs)
        {
            var features = await FetchAsync(spec, cancellationToken).ConfigureAwait(false);
            if (features.Count == 0)
            {
                LogEmpty(logger, spec.Name);
                failed++;
                continue;
            }

            var path = Path.Combine(outDir, spec.Name + ".json");
            await File.WriteAllBytesAsync(path, Serialize(spec, features), cancellationToken).ConfigureAwait(false);
            LogSaved(logger, spec.Name, features.Count, path);
        }

        return failed == 0 ? 0 : 1;
    }

    private async Task<List<JsonElement>> FetchAsync(FixtureSpec spec, CancellationToken cancellationToken)
    {
        var features = new List<JsonElement>();
        var seen = new HashSet<long>();
        foreach (var q in spec.Queries)
        {
            var parameters = new Dictionary<string, string>
            {
                ["where"] = q.Where,
                ["outFields"] = "*",
                ["orderByFields"] = "OBJECTID",
                ["resultRecordCount"] = q.Count.ToString(CultureInfo.InvariantCulture),
                ["outSR"] = "4326",
                ["returnGeometry"] = "true",
            };
            if (q.Within is { } e)
            {
                parameters["geometry"] = string.Create(CultureInfo.InvariantCulture, $"{e.XMin},{e.YMin},{e.XMax},{e.YMax}");
                parameters["geometryType"] = "esriGeometryEnvelope";
                parameters["inSR"] = "4326";
                parameters["spatialRel"] = "esriSpatialRelIntersects";
            }

            using var doc = await client.QueryAsync(parameters, cancellationToken).ConfigureAwait(false);
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                if (seen.Add(f.GetProperty("attributes").GetProperty("OBJECTID").GetInt64()))
                {
                    features.Add(f.Clone());
                }
            }
        }

        return features;
    }

    private byte[] Serialize(FixtureSpec spec, List<JsonElement> features)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("name", spec.Name);
            w.WriteString("description", spec.Description);
            w.WriteBoolean("synthetic", false);
            w.WriteString("capturedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("layer", options.Value.BaseUrl.ToString());
            w.WriteStartArray("queries");
            foreach (var q in spec.Queries)
            {
                w.WriteStartObject();
                w.WriteString("where", q.Where);
                w.WriteNumber("count", q.Count);
                if (q.Within is { } e)
                {
                    w.WriteStartArray("envelope");
                    w.WriteNumberValue(e.XMin);
                    w.WriteNumberValue(e.YMin);
                    w.WriteNumberValue(e.XMax);
                    w.WriteNumberValue(e.YMax);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("features");
            foreach (var f in features)
            {
                f.WriteTo(w);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unknown fixture(s): {Names}. Use --list to see them.")]
    private static partial void LogUnknown(ILogger logger, string names);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't find the repository root (Sac311.slnx). Pass --out <dir>.")]
    private static partial void LogNoRepo(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fixture {Name}: the live feed returned no rows; nothing saved.")]
    private static partial void LogEmpty(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fixture {Name}: saved {Count} row(s) to {Path}")]
    private static partial void LogSaved(ILogger logger, string name, int count, string path);
}
