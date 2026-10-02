using System.Text.Json;

namespace Sac311.Ingestion.ArcGis;

/// <summary>
/// Talks to one ArcGIS feature layer. Every call is a form-encoded POST (long GET URLs get an IIS 404 from this
/// service), and a 200 response carrying an <c>{"error": ...}</c> body is turned into an <see cref="ArcGisException"/>.
/// </summary>
public sealed class ArcGisClient(HttpClient http)
{
    /// <summary>The layer description (<c>layer?f=json</c>): fields, max record count, extent.</summary>
    public Task<JsonDocument> GetLayerAsync(CancellationToken cancellationToken) =>
        PostAsync(string.Empty, new Dictionary<string, string>(), cancellationToken);

    /// <summary>Runs <c>layer/query</c> with the given parameters; <c>f=json</c> is added.</summary>
    public Task<JsonDocument> QueryAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
        PostAsync("query", parameters, cancellationToken);

    private async Task<JsonDocument> PostAsync(string path, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(parameters) { ["f"] = "json" };
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                var ex = ArcGisException.From(error, form.GetValueOrDefault("where"));
                doc.Dispose();
                throw ex;
            }

            return doc;
        }
    }
}
