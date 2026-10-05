using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Sac311.Api.Data;
using Sac311.Data;

namespace Sac311.Api.Health;

internal static class HealthTags
{
    /// <summary>Checks that make <c>/api/health/ready</c>; <c>/api/health/live</c> runs none.</summary>
    public const string Ready = "ready";
}

/// <summary>The database answers a query. Unhealthy otherwise.</summary>
internal sealed class DatabaseHealthCheck(Sac311Db db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken)).ConfigureAwait(false);
            return HealthCheckResult.Healthy("The database answers.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database can't be reached.", ex);
        }
    }
}

/// <summary>
/// An ingestion run succeeded within <see cref="FreshnessOptions.MaxAge"/>. Degraded otherwise: the API still serves
/// the last good aggregates, so stale data is a warning, not an outage.
/// </summary>
internal sealed class FreshnessHealthCheck(FreshnessReader reader, TimeProvider time, IOptions<FreshnessOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var maxAge = options.Value.MaxAge;
        var last = await reader.LastSuccessAsync(cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow().UtcDateTime;
        if (last?.FinishedUtc is not { } finished)
        {
            return HealthCheckResult.Degraded("No ingestion run has succeeded yet.");
        }

        var age = now - finished;
        var data = new Dictionary<string, object>
        {
            ["lastSuccessUtc"] = finished,
            ["pipeline"] = last.Pipeline,
            ["ageMinutes"] = Math.Round(age.TotalMinutes, 1),
        };
        var minutes = string.Create(CultureInfo.InvariantCulture, $"{age.TotalMinutes:0} min ago (limit {maxAge.TotalMinutes:0} min)");
        return Freshness.Evaluate(finished, now, maxAge) == Freshness.Fresh
            ? HealthCheckResult.Healthy($"Last successful run {minutes}.", data)
            : HealthCheckResult.Degraded($"Data is stale: last successful run {minutes}.", data: data);
    }
}

/// <summary>
/// The newest finished ingestion run didn't stop on schema drift. Degraded otherwise, at once rather than after
/// <see cref="FreshnessOptions.MaxAge"/>: drift is the one outcome a retry can't fix, since every run checks the same
/// contract and stops. Failed runs are retried by the scheduler, so they surface through freshness instead.
/// </summary>
internal sealed class IngestionHealthCheck(FreshnessReader reader) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var last = await reader.LastFinishedRunAsync(cancellationToken).ConfigureAwait(false);
        if (last is null)
        {
            return HealthCheckResult.Healthy("No ingestion run has finished yet.");
        }

        var data = new Dictionary<string, object>
        {
            ["runId"] = last.RunId,
            ["pipeline"] = last.Pipeline,
            ["status"] = last.Status,
            ["startedUtc"] = last.StartedUtc,
        };
        if (last.Status != "SchemaDrift")
        {
            return HealthCheckResult.Healthy($"Last run {last.RunId} ({last.Pipeline}): {last.Status}.", data);
        }

        var error = await reader.RunErrorAsync(last.RunId, cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Degraded(
            $"Last run {last.RunId} ({last.Pipeline}) stopped on schema drift and wrote nothing. {error}".TrimEnd(), data: data);
    }
}

/// <summary>Writes a health report as JSON: overall status, then each check's status, description and data.</summary>
internal static class HealthResponse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                data = e.Value.Data.Count > 0 ? e.Value.Data : null,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }
}
