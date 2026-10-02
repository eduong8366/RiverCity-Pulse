using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Integration.Tests.ArcGis;

public class ArcGisClientTests
{
    private static string Page(bool exceeded, params long[] objectIds)
    {
        var features = string.Join(",", objectIds.Select(id => string.Create(CultureInfo.InvariantCulture,
            $$$"""{"attributes":{"OBJECTID":{{{id}}},"ReferenceNumber":"R{{{id}}}"},"geometry":{"x":-121.5,"y":38.5}}""")));
        return $$"""{"objectIdFieldName":"OBJECTID","exceededTransferLimit":{{(exceeded ? "true" : "false")}},"features":[{{features}}]}""";
    }

    [Fact]
    public async Task Pages_follow_the_objectid_cursor_until_a_short_page()
    {
        var stub = new StubHandler(
            _ => StubHandler.Json(Page(true, 10, 20)),
            _ => StubHandler.Json(Page(true, 25, 31)),
            _ => StubHandler.Json(Page(false, 40)));
        using var sp = stub.BuildClient(pageSize: 2);
        var client = sp.GetRequiredService<ArcGisClient>();

        var seen = new List<long>();
        await foreach (var page in client.GetPagesAsync("DateUpdated >= TIMESTAMP '2026-09-01 00:00:00'", 0, CancellationToken.None))
        {
            using (page)
            {
                seen.AddRange(page.Features.Select(f => f.GetProperty("attributes").GetProperty("OBJECTID").GetInt64()));
                Assert.NotEmpty(page.Payload);
            }
        }

        Assert.Equal([10, 20, 25, 31, 40], seen);
        Assert.Equal(3, stub.Requests.Count);
        Assert.All(stub.Requests, r => Assert.Equal(HttpMethod.Post, r.Method));
        Assert.Equal("(DateUpdated >= TIMESTAMP '2026-09-01 00:00:00') AND OBJECTID > 0", stub.Requests[0].Form["where"]);
        Assert.Equal("(DateUpdated >= TIMESTAMP '2026-09-01 00:00:00') AND OBJECTID > 20", stub.Requests[1].Form["where"]);
        Assert.Equal("(DateUpdated >= TIMESTAMP '2026-09-01 00:00:00') AND OBJECTID > 31", stub.Requests[2].Form["where"]);
        Assert.All(stub.Requests, r =>
        {
            Assert.Equal("json", r.Form["f"]);
            Assert.Equal("OBJECTID", r.Form["orderByFields"]);
            Assert.Equal("4326", r.Form["outSR"]);
            Assert.Equal("2", r.Form["resultRecordCount"]);
        });
    }

    [Fact]
    public async Task Paging_resumes_after_the_given_cursor_and_stops_on_an_empty_page()
    {
        var stub = new StubHandler(
            _ => StubHandler.Json(Page(false, 501, 502)),
            _ => StubHandler.Json(Page(false)));
        using var sp = stub.BuildClient(pageSize: 2);
        var client = sp.GetRequiredService<ArcGisClient>();

        var pages = 0;
        await foreach (var page in client.GetPagesAsync("1=1", 500, CancellationToken.None))
        {
            page.Dispose();
            pages++;
        }

        // A full page without the transfer-limit flag still asks once more; the empty answer ends it.
        Assert.Equal(1, pages);
        Assert.Equal(2, stub.Requests.Count);
        Assert.EndsWith("OBJECTID > 500", stub.Requests[0].Form["where"], StringComparison.Ordinal);
        Assert.EndsWith("OBJECTID > 502", stub.Requests[1].Form["where"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_error_in_a_200_response_is_retried()
    {
        var stub = new StubHandler(
            _ => StubHandler.Json("""{"error":{"code":500,"message":"Error performing query operation","details":[]}}"""),
            _ => StubHandler.Json("""{"count":42}"""));
        using var sp = stub.BuildClient();

        var count = await sp.GetRequiredService<ArcGisClient>().CountAsync("1=1", CancellationToken.None);

        Assert.Equal(42, count);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_transient_error_that_persists_throws_after_the_retries()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"error":{"code":504,"message":"Your request has timed out."}}"""));
        using var sp = stub.BuildClient(maxRetries: 2);

        var ex = await Assert.ThrowsAsync<ArcGisException>(() => sp.GetRequiredService<ArcGisClient>().CountAsync("1=1", CancellationToken.None));

        Assert.Equal(504, ex.Code);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Fact]
    public async Task A_bad_query_error_is_not_retried()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"error":{"code":400,"message":"Unable to complete operation.","details":["'Invalid field: Nope' parameter is invalid"]}}"""));
        using var sp = stub.BuildClient();

        var ex = await Assert.ThrowsAsync<ArcGisException>(() => sp.GetRequiredService<ArcGisClient>().CountAsync("Nope = 1", CancellationToken.None));

        Assert.Equal(400, ex.Code);
        Assert.Contains("where=Nope = 1", ex.Message, StringComparison.Ordinal);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Http_5xx_is_retried()
    {
        var stub = new StubHandler(
            _ => StubHandler.Json("busy", System.Net.HttpStatusCode.BadGateway),
            _ => StubHandler.Json("""{"count":7}"""));
        using var sp = stub.BuildClient();

        Assert.Equal(7, await sp.GetRequiredService<ArcGisClient>().CountAsync("1=1", CancellationToken.None));
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Explicit_f_parameter_is_kept()
    {
        var stub = new StubHandler(_ => StubHandler.Json("""{"type":"FeatureCollection","features":[]}"""));
        using var sp = stub.BuildClient();

        using var response = await sp.GetRequiredService<ArcGisClient>()
            .PostAsync("https://example.test/other/FeatureServer/0/query", new Dictionary<string, string> { ["f"] = "geojson" }, CancellationToken.None);

        Assert.Equal("geojson", stub.Requests[0].Form["f"]);
        Assert.Equal("FeatureCollection", response.Json.RootElement.GetProperty("type").GetString());
    }
}
