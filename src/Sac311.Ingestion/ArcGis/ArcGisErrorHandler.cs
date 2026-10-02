using System.Net;
using System.Text.Json;

namespace Sac311.Ingestion.ArcGis;

/// <summary>
/// Sits inside the resilience handler. It buffers each body (so a body that fails mid-read is retried as one attempt)
/// and, when ArcGIS answers HTTP 200 with a transient <c>{"error": ...}</c> (code 5xx, 429 or none), rewrites the
/// status to 5xx so the standard retry policy picks it up. Permanent errors (a bad where clause is code 400) stay 200,
/// and <see cref="ArcGisClient"/> throws <see cref="ArcGisException"/> for them without retrying.
/// </summary>
internal sealed class ArcGisErrorHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return response;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var buffered = new ByteArrayContent(body);
        foreach (var header in response.Content.Headers)
        {
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Decompression already happened, so the encoding/length headers no longer describe these bytes.
        buffered.Headers.ContentEncoding.Clear();
        buffered.Headers.ContentLength = body.Length;
        response.Content.Dispose();
        response.Content = buffered;

        if (TryReadError(body, out var code) && IsTransient(code))
        {
            response.StatusCode = code is >= 500 and <= 599 ? (HttpStatusCode)code.Value : HttpStatusCode.ServiceUnavailable;
            response.ReasonPhrase = "ArcGIS error in a 200 response";
        }

        return response;
    }

    private static bool IsTransient(int? code) => code is null or 429 or >= 500;

    /// <summary>True when the body is an ArcGIS error; <paramref name="code"/> is its code, or null when absent.</summary>
    internal static bool TryReadError(ReadOnlySpan<byte> body, out int? code)
    {
        code = null;
        // Only the first property is read, so a 1 MB page of features costs a few bytes here.
        var reader = new Utf8JsonReader(body);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject
                || !reader.Read() || reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("error"u8))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        using var doc = JsonDocument.Parse(body.ToArray());
        if (doc.RootElement.GetProperty("error").TryGetProperty("code", out var c) && c.TryGetInt32(out var n))
        {
            code = n;
        }

        return true;
    }
}

