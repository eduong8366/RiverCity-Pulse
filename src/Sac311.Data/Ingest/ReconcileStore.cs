using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Sac311.Data.Ingest;

/// <summary>One (ReferenceNumber, DateUpdated) pair served by the source.</summary>
public sealed record SourceKey(string ReferenceNumber, DateTime UpdatedUtc);

/// <summary>What <c>usp_reconcile</c> did, and the requests it wants fetched again.</summary>
public sealed record ReconcileResult(int Removed, int Restored, IReadOnlyList<string> Refetch);

/// <summary>The daily reconcile's tables: the source keys in <c>stg.reconcile_key</c> and <c>usp_reconcile</c>.</summary>
public sealed class ReconcileStore(Sac311Db db)
{
    public async Task ClearKeysAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition("TRUNCATE TABLE stg.reconcile_key;", cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task AddKeysAsync(IReadOnlyCollection<SourceKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return;
        }

        using var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add("reference_number", typeof(string));
        table.Columns.Add("updated_utc", typeof(DateTime));
        foreach (var k in keys)
        {
            table.Rows.Add(k.ReferenceNumber, k.UpdatedUtc);
        }

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "stg.reconcile_key", BatchSize = 0 };
        bulk.ColumnMappings.Add("reference_number", "reference_number");
        bulk.ColumnMappings.Add("updated_utc", "updated_utc");
        await bulk.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the 95% guard compares with: the key count of the last successful reconcile or, before the first one,
    /// the requests in the cleaned table not already marked removed. Null when there is nothing to compare with.
    /// </summary>
    public async Task<int?> GetGuardBaselineAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var baseline = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT COALESCE(
                (SELECT TOP (1) source_count FROM ops.ingest_run
                 WHERE pipeline = 'Reconcile' AND status = 'Succeeded' AND source_count IS NOT NULL
                 ORDER BY started_utc DESC, run_id DESC),
                (SELECT COUNT(*) FROM dbo.service_request WHERE source_removed_utc IS NULL));
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return baseline is > 0 ? baseline : null;
    }

    /// <summary>Runs <c>usp_reconcile</c> over the loaded keys.</summary>
    public async Task<ReconcileResult> ApplyAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var grid = await conn.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_reconcile", commandType: CommandType.StoredProcedure, commandTimeout: 600, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var counts = await grid.ReadSingleAsync<Counts>().ConfigureAwait(false);
        var refetch = (await grid.ReadAsync<string>().ConfigureAwait(false)).AsList();
        return new ReconcileResult(counts.Removed, counts.Restored, refetch);
    }

    private sealed record Counts(int Removed, int Restored);
}
