using Dapper;
using Sac311.Data;

namespace Sac311.Api.Data;

/// <summary>An ingestion run, as the freshness endpoint and health check report it.</summary>
internal sealed record RunSummary(
    long RunId, string Pipeline, string Status, DateTime StartedUtc, DateTime? FinishedUtc, int RowsFetched, int RowsInserted, int RowsUpdated);

/// <summary>The latest result of one data-quality check.</summary>
internal sealed record DqCheck(string Check, string Status, string? Message, DateTime CheckedUtc);

/// <summary>Reads the run log, the incremental watermark and the data-quality results.</summary>
internal sealed class FreshnessReader(Sac311Db db)
{
    private const string RunColumns =
        """
        run_id AS RunId, pipeline AS Pipeline, status AS Status, started_utc AS StartedUtc, finished_utc AS FinishedUtc,
        rows_fetched AS RowsFetched, rows_inserted AS RowsInserted, rows_updated AS RowsUpdated
        """;

    /// <summary>The newest successful ingestion run of any pipeline, by finish time.</summary>
    public async Task<RunSummary?> LastSuccessAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var run = await conn.QuerySingleOrDefaultAsync<RunSummary>(new CommandDefinition(
            $"SELECT TOP (1) {RunColumns} FROM ops.ingest_run WHERE status = 'Succeeded' ORDER BY finished_utc DESC;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return Utc(run);
    }

    /// <summary>The newest run that wasn't skipped, whatever its outcome (it may still be running).</summary>
    public async Task<RunSummary?> LastRunAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var run = await conn.QuerySingleOrDefaultAsync<RunSummary>(new CommandDefinition(
            $"SELECT TOP (1) {RunColumns} FROM ops.ingest_run WHERE status <> 'Skipped' ORDER BY run_id DESC;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return Utc(run);
    }

    public async Task<DateTime?> WatermarkAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var watermark = await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "SELECT watermark_utc FROM ops.ingest_checkpoint WHERE pipeline = 'Incremental';",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return watermark is { } w ? DateTime.SpecifyKind(w, DateTimeKind.Utc) : null;
    }

    /// <summary>The latest result of every data-quality check.</summary>
    public async Task<IReadOnlyList<DqCheck>> LatestDqAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<DqCheck>(new CommandDefinition(
            """
            SELECT check_name AS [Check], status AS Status, message AS Message, checked_utc AS CheckedUtc
            FROM
            (
                SELECT check_name, status, message, checked_utc,
                       ROW_NUMBER() OVER (PARTITION BY check_name ORDER BY checked_utc DESC, dq_result_id DESC) AS rn
                FROM ops.dq_result
            ) AS x
            WHERE rn = 1
            ORDER BY check_name;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r with { CheckedUtc = DateTime.SpecifyKind(r.CheckedUtc, DateTimeKind.Utc) })];
    }

    private static RunSummary? Utc(RunSummary? run) => run is null
        ? null
        : run with
        {
            StartedUtc = DateTime.SpecifyKind(run.StartedUtc, DateTimeKind.Utc),
            FinishedUtc = run.FinishedUtc is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
        };
}
