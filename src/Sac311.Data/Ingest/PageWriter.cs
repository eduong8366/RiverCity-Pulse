using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using Sac311.Domain;

namespace Sac311.Data.Ingest;

/// <summary>A fetched page as stored in <c>raw.page</c>.</summary>
public sealed record RawPage(long RunId, string Pipeline, string WhereClause, long? CursorObjectId, int RecordCount, int HttpMs, ReadOnlyMemory<byte> Payload);

/// <summary>A row that failed <c>Record.Validate</c>, kept with its raw JSON in <c>ops.ingest_reject</c>.</summary>
public sealed record IngestReject(long? ObjectId, string? ReferenceNumber, string Reason, string RawJson);

/// <summary>What <c>usp_apply_batch</c> did with one page.</summary>
public sealed record BatchCounts(int Inserted, int Updated, int Unchanged, int History, int Duplicates);

/// <summary>Writes one page: the raw payload first, then the cleaned rows, rejects, checkpoint and run counters in one transaction.</summary>
public sealed class PageWriter(Sac311Db db)
{
    /// <summary>
    /// Stores the payload gzip-compressed with its SHA-256, so <c>reclean</c> can rebuild the cleaned tables without
    /// the source. It commits on its own: a page that fails to apply is still on record.
    /// </summary>
    public async Task<long> SaveRawAsync(RawPage page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var sha = SHA256.HashData(page.Payload.Span);
        using var gz = new MemoryStream();
        using (var zip = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(page.Payload.Span);
        }

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO raw.page (run_id, pipeline, where_clause, cursor_object_id, record_count, http_ms, payload_sha256, payload_gzip)
            OUTPUT inserted.page_id
            VALUES (@runId, @pipeline, @whereClause, @cursorObjectId, @recordCount, @httpMs, @sha, @gzip);
            """,
            new { page.RunId, page.Pipeline, page.WhereClause, page.CursorObjectId, page.RecordCount, page.HttpMs, sha, gzip = gz.ToArray() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// In one transaction: reload <c>stg.request</c> with the page (SqlBulkCopy), run <c>usp_apply_batch</c>, record the
    /// rejects, move the checkpoint (when one is given) and add to the run's counters. A crash before the commit leaves
    /// nothing behind, and the next run replays the page from the old checkpoint.
    /// </summary>
    public async Task<BatchCounts> ApplyAsync(
        long runId, IReadOnlyList<CleanedRequest> rows, IReadOnlyList<IngestReject> rejects, Checkpoint? checkpoint, int fetched,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rejects);

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition("TRUNCATE TABLE stg.request;", transaction: tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var counts = new BatchCounts(0, 0, 0, 0, 0);
        if (rows.Count > 0)
        {
            using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tx) { DestinationTableName = "stg.request", BatchSize = 0 };
            using var table = ToStagingTable(rows);
            foreach (DataColumn c in table.Columns)
            {
                bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            }

            await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
            counts = await conn.QuerySingleAsync<BatchCounts>(new CommandDefinition(
                "dbo.usp_apply_batch", new { run_id = runId }, tx, commandType: CommandType.StoredProcedure, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (rejects.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO ops.ingest_reject (run_id, object_id, reference_number, reason, raw_json)
                VALUES (@runId, @ObjectId, @ReferenceNumber, @Reason, @RawJson);
                """,
                rejects.Select(r => new { runId, r.ObjectId, r.ReferenceNumber, r.Reason, r.RawJson }),
                tx, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (checkpoint is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition(CheckpointStore.UpsertSql, CheckpointStore.Parameters(checkpoint, runId), tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE ops.ingest_run
            SET rows_fetched += @fetched, rows_inserted += @Inserted, rows_updated += @Updated,
                rows_unchanged += @Unchanged, rows_history += @History, rows_rejected += @rejected
            WHERE run_id = @runId;
            """,
            new { runId, fetched, counts.Inserted, counts.Updated, counts.Unchanged, counts.History, rejected = rejects.Count },
            tx, cancellationToken: cancellationToken)).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    /// <summary>Deletes a pipeline's raw pages fetched before <paramref name="beforeUtc"/>. Returns the number deleted.</summary>
    public async Task<int> PruneRawAsync(string pipeline, DateTime beforeUtc, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM raw.page WHERE pipeline = @pipeline AND fetched_utc < @beforeUtc;",
            new { pipeline, beforeUtc },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    // Column names and types match stg.request; SqlBulkCopy maps by name.
    private static DataTable ToStagingTable(IReadOnlyList<CleanedRequest> rows)
    {
        var t = new DataTable();
        t.Locale = System.Globalization.CultureInfo.InvariantCulture;
        void Col<T>(string name) => t.Columns.Add(name, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
        Col<string>("reference_number");
        Col<long>("object_id");
        Col<string>("sf_ticket_id");
        Col<string>("category_level1");
        Col<string>("category_level2");
        Col<string>("category_name");
        Col<string>("category_key");
        Col<string>("source_channel_raw");
        Col<string>("source_key");
        Col<byte>("district_number");
        Col<bool>("is_city");
        Col<string>("neighborhood");
        Col<string>("neighborhood_key");
        Col<string>("neighborhood_slug");
        Col<string>("address");
        Col<string>("cross_street");
        Col<string>("zip");
        Col<decimal>("latitude");
        Col<decimal>("longitude");
        Col<string>("public_status");
        Col<string>("status_group");
        Col<DateTime>("created_utc");
        Col<DateTime>("updated_utc");
        Col<DateTime>("closed_utc");
        Col<DateTime>("created_date_local");
        Col<DateTime>("closed_date_local");
        Col<DateTime>("backlog_close_date_local");
        Col<decimal>("days_to_close");
        Col<int>("dq_flags");
        Col<byte[]>("row_hash");

        foreach (var r in rows)
        {
            t.Rows.Add(
                r.ReferenceNumber, r.ObjectId, Db(r.SfTicketId),
                Db(r.CategoryLevel1), Db(r.CategoryLevel2), Db(r.CategoryName), Db(r.CategoryKey),
                Db(r.SourceChannelRaw), Db(r.SourceKey), Db(r.DistrictNumber), Db(r.IsCity),
                Db(r.Neighborhood), Db(r.NeighborhoodKey), Db(r.NeighborhoodSlug),
                Db(r.Address), Db(r.CrossStreet), Db(r.Zip), Db(r.Latitude), Db(r.Longitude),
                Db(r.PublicStatus), r.StatusGroup.ToString(),
                Db(r.CreatedUtc), r.UpdatedUtc, Db(r.ClosedUtc),
                Db(r.CreatedDateLocal), Db(r.ClosedDateLocal), Db(r.BacklogCloseDateLocal),
                Db(r.DaysToClose), (int)r.Flags, r.RowHash);
        }

        return t;
    }

    private static object Db<T>(T? value) where T : class => value ?? (object)DBNull.Value;

    private static object Db<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    private static object Db(DateOnly? value) => value is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
}
