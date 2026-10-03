using Dapper;

namespace Sac311.Data.Ingest;

/// <summary>Values of <c>ops.ingest_run.pipeline</c> and <c>ops.ingest_checkpoint.pipeline</c>.</summary>
public static class Pipeline
{
    public const string Backfill = "Backfill";
    public const string Incremental = "Incremental";
    public const string Reconcile = "Reconcile";
    public const string Reclean = "Reclean";
}

/// <summary>Values of <c>ops.ingest_run.status</c>.</summary>
public static class RunStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string SchemaDrift = "SchemaDrift";
}

/// <summary>One row of <c>ops.ingest_run</c>.</summary>
public sealed class IngestRun
{
    public long RunId { get; init; }
    public string Pipeline { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public int? DurationMs { get; init; }
    public int RowsFetched { get; init; }
    public int RowsInserted { get; init; }
    public int RowsUpdated { get; init; }
    public int RowsUnchanged { get; init; }
    public int RowsRejected { get; init; }
    public int RowsHistory { get; init; }
    public DateTime? WatermarkFromUtc { get; init; }
    public DateTime? WatermarkToUtc { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The run log in <c>ops.ingest_run</c>. A run is inserted as Running, its row counters grow inside each page's
/// transaction (<see cref="PageWriter"/>), and it is closed with a final status. A crashed process leaves a Running row.
/// </summary>
public sealed class RunLog(Sac311Db db)
{
    public async Task<long> StartAsync(string pipeline, DateTime? watermarkFromUtc, DateTime? watermarkToUtc, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO ops.ingest_run (pipeline, status, watermark_from_utc, watermark_to_utc, host_name)
            OUTPUT inserted.run_id
            VALUES (@pipeline, @status, @watermarkFromUtc, @watermarkToUtc, @hostName);
            """,
            new { pipeline, status = RunStatus.Running, watermarkFromUtc, watermarkToUtc, hostName = Environment.MachineName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Sets the final status, finish time and duration, and returns the finished row.</summary>
    public async Task<IngestRun> FinishAsync(long runId, string status, string? error, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleAsync<IngestRun>(new CommandDefinition(
            """
            UPDATE ops.ingest_run
            SET status = @status,
                error = @error,
                finished_utc = SYSUTCDATETIME(),
                duration_ms = DATEDIFF_BIG(millisecond, started_utc, SYSUTCDATETIME())
            WHERE run_id = @runId;
            """ + SelectRun,
            new { runId, status, error },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IngestRun?> GetAsync(long runId, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<IngestRun>(new CommandDefinition(SelectRun, new { runId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private const string SelectRun =
        """
        SELECT run_id AS RunId, pipeline AS Pipeline, status AS Status, started_utc AS StartedUtc, finished_utc AS FinishedUtc,
               duration_ms AS DurationMs, rows_fetched AS RowsFetched, rows_inserted AS RowsInserted, rows_updated AS RowsUpdated,
               rows_unchanged AS RowsUnchanged, rows_rejected AS RowsRejected, rows_history AS RowsHistory,
               watermark_from_utc AS WatermarkFromUtc, watermark_to_utc AS WatermarkToUtc, error AS Error
        FROM ops.ingest_run
        WHERE run_id = @runId;
        """;
}
