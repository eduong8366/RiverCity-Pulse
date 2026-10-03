using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Sac311.Ingestion.ArcGis;

/// <summary>
/// Talks to one ArcGIS feature layer. Every call is a form-encoded POST (long GET URLs get an IIS 404 from this
/// service), and a response carrying an <c>{"error": ...}</c> body is turned into an <see cref="ArcGisException"/>.
/// Retries, timeouts and the circuit breaker live in the HTTP pipeline (see <c>AddArcGisClient</c>).
/// </summary>
public sealed class ArcGisClient(HttpClient http, IOptions<ArcGisOptions> options)
{
    private readonly ArcGisOptions _options = options.Value;

    /// <summary>The layer description (<c>layer?f=json</c>): fields, max record count, extent.</summary>
    public async Task<JsonDocument> GetLayerAsync(CancellationToken cancellationToken)
    {
        using var response = await PostAsync(string.Empty, new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);
        return response.DetachJson();
    }

    /// <summary>Runs <c>layer/query</c> with the given parameters; <c>f=json</c> is added.</summary>
    public async Task<JsonDocument> QueryAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        using var response = await PostAsync("query", parameters, cancellationToken).ConfigureAwait(false);
        return response.DetachJson();
    }

    /// <summary>The number of rows matching <paramref name="where"/>.</summary>
    public async Task<long> CountAsync(string where, CancellationToken cancellationToken)
    {
        using var doc = await QueryAsync(new Dictionary<string, string> { ["where"] = where, ["returnCountOnly"] = "true" }, cancellationToken)
            .ConfigureAwait(false);
        return doc.RootElement.GetProperty("count").GetInt64();
    }

    /// <summary>
    /// POSTs to any ArcGIS REST endpoint: <paramref name="url"/> is relative to the layer (<c>"query"</c>) or absolute
    /// (another layer, the item API). <c>f=json</c> is added unless <paramref name="parameters"/> sets <c>f</c>.
    /// </summary>
    public async Task<ArcGisResponse> PostAsync(string url, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var form = new Dictionary<string, string>(parameters);
        form.TryAdd("f", "json");
        using var content = new FormUrlEncodedContent(form);
        var sw = Stopwatch.StartNew();
        using var response = await http.PostAsync(new Uri(url, UriKind.RelativeOrAbsolute), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        // After the retries ran out, a transient ArcGIS error arrives here with a 5xx status: report ArcGIS's message.
        if (ArcGisErrorHandler.TryReadError(body, out _))
        {
            using var doc = JsonDocument.Parse(body);
            throw ArcGisException.From(doc.RootElement.GetProperty("error"), form.GetValueOrDefault("where"));
        }

        response.EnsureSuccessStatusCode();
        return new ArcGisResponse(body, JsonDocument.Parse(body), sw.Elapsed);
    }

    /// <summary>
    /// Keyset paging: <c>(where) AND OBJECTID &gt; cursor ORDER BY OBJECTID</c>, one page of <see cref="ArcGisOptions.PageSize"/>
    /// rows at a time, starting after <paramref name="afterObjectId"/>. Offset paging gets slow deep into the table
    /// (2.6 s at offset 1.5M), and OBJECTIDs are sparse, so the cursor is the last OBJECTID seen. The caller disposes each page.
    /// </summary>
    public IAsyncEnumerable<ArcGisPage> GetPagesAsync(string where, long afterObjectId, CancellationToken cancellationToken) =>
        GetPagesAsync(where, afterObjectId, "*", returnGeometry: true, cancellationToken);

    /// <summary>Keyset paging as above, returning only <paramref name="outFields"/> (comma-separated; OBJECTID is always added).</summary>
    public async IAsyncEnumerable<ArcGisPage> GetPagesAsync(
        string where, long afterObjectId, string outFields, bool returnGeometry, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        ArgumentException.ThrowIfNullOrWhiteSpace(outFields);
        if (outFields != "*" && !outFields.Split(',').Contains("OBJECTID", StringComparer.OrdinalIgnoreCase))
        {
            outFields = "OBJECTID," + outFields;
        }

        var cursor = afterObjectId;
        for (var first = true; ; first = false)
        {
            if (!first && _options.PageDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.PageDelay, cancellationToken).ConfigureAwait(false);
            }

            var pageWhere = string.Create(CultureInfo.InvariantCulture, $"({where}) AND OBJECTID > {cursor}");
            var parameters = new Dictionary<string, string>
            {
                ["where"] = pageWhere,
                ["outFields"] = outFields,
                ["orderByFields"] = "OBJECTID",
                ["resultRecordCount"] = _options.PageSize.ToString(CultureInfo.InvariantCulture),
                ["outSR"] = "4326",
                ["returnGeometry"] = returnGeometry ? "true" : "false",
            };

            var response = await PostAsync("query", parameters, cancellationToken).ConfigureAwait(false);
            var page = new ArcGisPage(response, pageWhere, cursor);
            if (page.Features.Count == 0)
            {
                page.Dispose();
                yield break;
            }

            cursor = page.LastObjectId;
            // A short page with no transfer-limit flag is the last one; otherwise ask again (an empty page ends it).
            var more = page.ExceededTransferLimit || page.Features.Count >= _options.PageSize;
            yield return page;
            if (!more)
            {
                yield break;
            }
        }
    }
}

/// <summary>A parsed response plus its exact bytes (stored in <c>raw.page</c>) and round-trip time.</summary>
public sealed class ArcGisResponse(byte[] body, JsonDocument json, TimeSpan elapsed) : IDisposable
{
    private JsonDocument? _json = json;

    /// <summary>The decompressed response body.</summary>
#pragma warning disable CA1819 // The payload is handed to gzip and SHA-256 as is; copying 1 MB per page buys nothing.
    public byte[] Body { get; } = body;
#pragma warning restore CA1819

    public JsonDocument Json => _json ?? throw new ObjectDisposedException(nameof(ArcGisResponse));

    public TimeSpan Elapsed { get; } = elapsed;

    /// <summary>Hands ownership of the parsed document to the caller.</summary>
    public JsonDocument DetachJson()
    {
        var doc = Json;
        _json = null;
        return doc;
    }

    public void Dispose()
    {
        _json?.Dispose();
        _json = null;
    }
}

/// <summary>One keyset page of features.</summary>
public sealed class ArcGisPage : IDisposable
{
    private readonly ArcGisResponse _response;

    internal ArcGisPage(ArcGisResponse response, string where, long cursor)
    {
        _response = response;
        Where = where;
        CursorObjectId = cursor;
        var root = response.Json.RootElement;
        Features = root.TryGetProperty("features", out var f) ? [.. f.EnumerateArray()] : [];
        ExceededTransferLimit = root.TryGetProperty("exceededTransferLimit", out var x) && x.ValueKind == JsonValueKind.True;
        LastObjectId = Features.Count == 0 ? cursor : Features[^1].GetProperty("attributes").GetProperty("OBJECTID").GetInt64();
    }

    /// <summary>The full where clause sent, cursor included.</summary>
    public string Where { get; }

    /// <summary>The OBJECTID this page starts after.</summary>
    public long CursorObjectId { get; }

    /// <summary>The highest OBJECTID on the page: the next page's cursor.</summary>
    public long LastObjectId { get; }

    public bool ExceededTransferLimit { get; }

    /// <summary>Elements of the response's <c>features</c> array; valid until the page is disposed.</summary>
    public IReadOnlyList<JsonElement> Features { get; }

#pragma warning disable CA1819 // See ArcGisResponse.Body.
    public byte[] Payload => _response.Body;
#pragma warning restore CA1819

    public TimeSpan Elapsed => _response.Elapsed;

    public void Dispose() => _response.Dispose();
}
