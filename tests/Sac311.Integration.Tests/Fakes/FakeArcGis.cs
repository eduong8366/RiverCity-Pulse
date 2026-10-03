using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sac311.Ingestion.ArcGis;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Sac311.Integration.Tests.Fakes;

/// <summary>
/// A WireMock.Net stand-in for the ArcGIS 311 layer, served over real HTTP so the worker's whole client pipeline
/// (resilience handler, 200-with-error detection, keyset paging) runs against it. It answers <c>layer?f=json</c> and
/// <c>layer/query</c> from <see cref="Features"/>, evaluating the where clauses the pipeline sends, and can inject the
/// failures the tests need: schema drift, transient errors and a hard failure part-way through paging.
/// </summary>
internal sealed partial class FakeArcGis : IDisposable
{
    public const string LayerPath = "/arcgis/rest/services/SalesForce311_View/FeatureServer/0";

    private readonly WireMockServer _server;
    private readonly Lock _gate = new();
    private int _pagesServed;

    public FakeArcGis()
    {
        _server = WireMockServer.Start();
        _server.Given(Request.Create().WithPath(LayerPath, LayerPath + "/").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(_ => Layer()));
        _server.Given(Request.Create().WithPath(LayerPath + "/query").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(r => Query(r.Body ?? "")));
    }

    public Uri BaseUrl => new(_server.Url + LayerPath);

    /// <summary>The rows the layer serves. Edit freely between runs.</summary>
    public List<FakeFeature> Features { get; } = [];

    /// <summary>When set, the layer description lists this field under another name (the M5 drill renames PublicStatus).</summary>
    public (string From, string To)? RenamedField { get; set; }

    /// <summary>The next this-many queries answer HTTP 200 with a transient <c>{"error":{"code":500}}</c> body.</summary>
    public int TransientErrors { get; set; }

    /// <summary>When set, feature pages after this many more served pages fail with a non-retryable error (code 400).</summary>
    public int? FailPagesAfter
    {
        get;
        set
        {
            field = value;
            _pagesServed = 0;
        }
    }

    /// <summary>Every query's form fields, in order.</summary>
    public List<Dictionary<string, string>> Queries { get; } = [];

    public void Seed(int count, DateTime nowUtc)
    {
        for (var i = 0; i < count; i++)
        {
            Features.Add(FakeFeature.Typical(i, nowUtc));
        }
    }

    public FakeFeature Feature(string referenceNumber) => Features.Single(f => f.ReferenceNumber == referenceNumber);

    public void Dispose() => _server.Stop();

    private string Layer()
    {
        var fields = SchemaContract.Fields.Select(f => new
        {
            name = RenamedField is { } r && r.From == f.Name ? r.To : f.Name,
            type = f.Type,
        });
        return JsonSerializer.Serialize(new { name = "SalesForce311", geometryType = SchemaContract.ExpectedGeometryType, maxRecordCount = 2000, fields });
    }

    private string Query(string body)
    {
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));

        lock (_gate)
        {
            Queries.Add(form);
            if (TransientErrors > 0)
            {
                TransientErrors--;
                return Error(500, "Error performing query operation");
            }

            var match = Where(form.GetValueOrDefault("where") ?? "1=1");
            var rows = Features.Where(match).OrderBy(f => f.ObjectId).ToList();
            if (form.GetValueOrDefault("returnCountOnly") == "true")
            {
                return JsonSerializer.Serialize(new { count = rows.Count });
            }

            if (FailPagesAfter is { } limit && _pagesServed >= limit)
            {
                return Error(400, "Unable to complete operation (injected failure).");
            }

            _pagesServed++;
            var size = int.Parse(form.GetValueOrDefault("resultRecordCount") ?? "2000", CultureInfo.InvariantCulture);
            var outFields = form.GetValueOrDefault("outFields") ?? "*";
            var geometry = form.GetValueOrDefault("returnGeometry") != "false";
            var features = rows.Take(size).Select(f => Serialize(f, outFields, geometry)).ToList();
            return JsonSerializer.Serialize(new { objectIdFieldName = "OBJECTID", exceededTransferLimit = rows.Count > size, features });
        }
    }

    private static object Serialize(FakeFeature f, string outFields, bool geometry)
    {
        var attributes = f.Attributes();
        if (outFields != "*")
        {
            var wanted = outFields.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
            attributes = attributes.Where(a => wanted.Contains(a.Key)).ToDictionary();
        }

        return geometry && f.X is { } x && f.Y is { } y
            ? new { attributes, geometry = new { x, y } }
            : new { attributes };
    }

    private static string Error(int code, string message) =>
        JsonSerializer.Serialize(new { error = new { code, message, details = Array.Empty<string>() } });

    /// <summary>
    /// The where clauses the pipeline sends, as a predicate: <c>1=1</c>, <c>OBJECTID &gt; n</c>,
    /// <c>DateUpdated (&gt;=|&gt;|&lt;) TIMESTAMP '...'</c> and <c>ReferenceNumber IN (...)</c>, joined with AND.
    /// Anything else throws, so a new query shape can't silently match everything.
    /// </summary>
    private static Func<FakeFeature, bool> Where(string where)
    {
        var tests = new List<Func<FakeFeature, bool>>();
        var rest = where;

        rest = ObjectIdRegex().Replace(rest, m =>
        {
            var n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            tests.Add(f => f.ObjectId > n);
            return "";
        });
        rest = DateUpdatedRegex().Replace(rest, m =>
        {
            var op = m.Groups[1].Value;
            var t = DateTime.ParseExact(m.Groups[2].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            tests.Add(op switch
            {
                ">=" => f => f.UpdatedUtc >= t,
                ">" => f => f.UpdatedUtc > t,
                "<" => f => f.UpdatedUtc < t,
                _ => throw new NotSupportedException(op),
            });
            return "";
        });
        rest = ReferenceInRegex().Replace(rest, m =>
        {
            var refs = QuotedRegex().Matches(m.Groups[1].Value).Select(q => q.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).ToHashSet();
            tests.Add(f => f.ReferenceNumber is { } r && refs.Contains(r));
            return "";
        });

        var leftover = rest.Replace("1=1", "", StringComparison.Ordinal).Replace("AND", "", StringComparison.Ordinal)
            .Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal).Trim();
        if (leftover.Length > 0)
        {
            throw new NotSupportedException($"FakeArcGis can't evaluate '{leftover}' in: {where}");
        }

        return f => tests.TrueForAll(t => t(f));
    }

    [GeneratedRegex(@"OBJECTID > (\d+)")]
    private static partial Regex ObjectIdRegex();

    [GeneratedRegex(@"DateUpdated (>=|>|<) TIMESTAMP '([^']+)'")]
    private static partial Regex DateUpdatedRegex();

    [GeneratedRegex(@"ReferenceNumber IN \(((?:'(?:[^']|'')*',?)*)\)")]
    private static partial Regex ReferenceInRegex();

    [GeneratedRegex(@"'((?:[^']|'')*)'")]
    private static partial Regex QuotedRegex();
}
