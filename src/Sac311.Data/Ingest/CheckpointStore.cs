using Dapper;

namespace Sac311.Data.Ingest;

/// <summary>
/// A pipeline's resume point (<c>ops.ingest_checkpoint</c>). Backfill uses <see cref="LastObjectId"/> (null when no
/// backfill is in progress), <see cref="WhereClause"/> and <see cref="WatermarkUtc"/> (when the unfinished backfill
/// first started); incremental uses <see cref="WatermarkUtc"/> (the newest DateUpdated loaded).
/// </summary>
public sealed record Checkpoint(string Pipeline, long? LastObjectId, DateTime? WatermarkUtc, string? WhereClause);

public sealed class CheckpointStore(Sac311Db db)
{
    public async Task<Checkpoint?> GetAsync(string pipeline, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<Checkpoint>(new CommandDefinition(
            """
            SELECT pipeline AS Pipeline, last_object_id AS LastObjectId, watermark_utc AS WatermarkUtc, where_clause AS WhereClause
            FROM ops.ingest_checkpoint
            WHERE pipeline = @pipeline;
            """,
            new { pipeline },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Writes a checkpoint on its own (outside a page transaction), e.g. to mark a backfill finished.</summary>
    public async Task SaveAsync(Checkpoint checkpoint, long runId, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(UpsertSql, Parameters(checkpoint, runId), cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    internal const string UpsertSql =
        """
        UPDATE ops.ingest_checkpoint
        SET last_object_id = @lastObjectId, watermark_utc = @watermarkUtc, where_clause = @whereClause,
            run_id = @runId, updated_utc = SYSUTCDATETIME()
        WHERE pipeline = @pipeline;
        IF @@ROWCOUNT = 0
            INSERT INTO ops.ingest_checkpoint (pipeline, last_object_id, watermark_utc, where_clause, run_id)
            VALUES (@pipeline, @lastObjectId, @watermarkUtc, @whereClause, @runId);
        """;

    internal static object Parameters(Checkpoint c, long runId) =>
        new { pipeline = c.Pipeline, lastObjectId = c.LastObjectId, watermarkUtc = c.WatermarkUtc, whereClause = c.WhereClause, runId };
}
