using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Sac311.Api.Tests.Support;

namespace Sac311.Api.Tests;

/// <summary>Health, freshness and the endpoints before any data: a database emptied before each test.</summary>
public class HealthTests(SqlServerFixture db) : IClassFixture<SqlServerFixture>, IAsyncLifetime, IDisposable
{
    private static readonly DateTime NowUtc = new(2026, 10, 2, 20, 0, 0, DateTimeKind.Utc);

    private readonly ApiFactory _factory = new(db.ConnectionString, NowUtc);

    private HttpClient Client => _factory.CreateClient();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SucceededRunAsync(DateTime finishedUtc)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO ops.ingest_run (pipeline, status, started_utc, finished_utc) VALUES ('Incremental', 'Succeeded', @started, @finishedUtc);",
            new { started = finishedUtc.AddMinutes(-1), finishedUtc });
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReadyAsync()
    {
        var response = await Client.GetAsync(new Uri("/api/health/ready", UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static string CheckStatus(JsonElement body, string name) =>
        body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name).GetProperty("status").GetString()!;

    [Fact]
    public async Task Live_runs_no_checks()
    {
        var response = await Client.GetAsync(new Uri("/api/health/live", UriKind.Relative));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Ready_is_healthy_within_45_minutes_of_a_successful_run()
    {
        await SucceededRunAsync(NowUtc.AddMinutes(-44));

        var (status, body) = await ReadyAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", CheckStatus(body, "database"));
    }

    [Fact]
    public async Task Ready_is_degraded_but_still_200_when_the_data_is_stale()
    {
        await SucceededRunAsync(NowUtc.AddMinutes(-46));

        var (status, body) = await ReadyAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Degraded", body.GetProperty("status").GetString());
        Assert.Equal("Degraded", CheckStatus(body, "freshness"));
        Assert.Equal("Healthy", CheckStatus(body, "database"));
    }

    [Fact]
    public async Task Ready_is_degraded_before_any_run_succeeded()
    {
        var (_, body) = await ReadyAsync();

        Assert.Equal("Degraded", CheckStatus(body, "freshness"));
    }

    [Fact]
    public async Task Ready_is_unhealthy_when_the_database_is_unreachable()
    {
        var missing = new SqlConnectionStringBuilder(db.ConnectionString) { InitialCatalog = "Sac311_Missing_" + Guid.NewGuid().ToString("N"), ConnectTimeout = 5 };
        await using var broken = new ApiFactory(missing.ConnectionString, NowUtc);
        var response = await broken.CreateClient().GetAsync(new Uri("/api/health/ready", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", CheckStatus(body, "database"));
    }

    [Fact]
    public async Task Freshness_is_stale_with_no_runs()
    {
        var body = await Client.GetFromJsonAsync<FreshnessResponse>(new Uri("/api/meta/freshness", UriKind.Relative));

        Assert.Equal("stale", body!.Status);
        Assert.Null(body.LastSuccess);
        Assert.Null(body.RequestCount);
    }

    [Theory]
    [InlineData("/api/neighborhoods/downtown/stats")]
    [InlineData("/api/categories/summary")]
    [InlineData("/api/map/neighborhoods")]
    [InlineData("/api/backlog")]
    [InlineData("/api/meta/exclusions")]
    [InlineData("/api/meta/clear-outs")]
    public async Task Stats_before_the_first_refresh_are_unavailable(string url)
    {
        var response = await Client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("No aggregates yet", (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);
    }
}
