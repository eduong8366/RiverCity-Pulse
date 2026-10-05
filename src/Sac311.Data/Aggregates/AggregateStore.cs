using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.SqlClient;
using Sac311.Domain;
using Sac311.Domain.Aggregates;
using Sac311.Domain.Cleaners;

namespace Sac311.Data.Aggregates;

/// <summary>One row of <c>agg.refresh</c>.</summary>
public sealed record AggregateRefresh(int RefreshId, long? RunId, DateTime AsOfUtc, DateOnly AsOfDate, int RequestCount, int BuildMs, DateTime RefreshedUtc);

/// <summary>What <see cref="AggregateStore.ClassifyAsync"/> changed, and the totals after it.</summary>
/// <param name="ServiceChanged">Requests whose <c>is_service</c> flipped.</param>
/// <param name="BulkChanged">Requests whose bulk-closure flag was set or cleared.</param>
/// <param name="ClearOuts">(Day, category) groups that are clear-outs by the day clause.</param>
/// <param name="SweepMinutes">Closed minutes that are clear-outs by the sweep clause.</param>
public sealed record Classification(int ServiceChanged, int BulkChanged, int ClearOuts, int SweepMinutes, int NonServiceRows, int BulkRows);

/// <summary>Reads what the aggregates are computed from, and publishes them to the <c>agg</c> tables.</summary>
public sealed class AggregateStore(Sac311Db db)
{
    /// <summary>Every request still in the source, streamed in the shape <see cref="AggregateBuilder"/> takes.</summary>
    public async IAsyncEnumerable<AggregateRequest> ReadRequestsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            """
            SELECT neighborhood_slug, district_number, category_group, status_group, created_utc, created_date_local,
                   closed_date_local, backlog_close_date_local, days_to_close, is_metric_eligible, is_service,
                   CAST(CASE WHEN dq_flags & @bulk <> 0 THEN 1 ELSE 0 END AS bit)
            FROM dbo.service_request
            WHERE source_removed_utc IS NULL;
            """,
            conn) { CommandTimeout = 300 };
        cmd.Parameters.Add("@bulk", SqlDbType.Int).Value = (int)DqFlags.BulkClosure;
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new AggregateRequest(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetByte(1),
                reader.GetString(2),
                ParseStatus(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11));
        }
    }

    /// <summary>
    /// Runs <c>usp_classify_for_metrics</c> with <see cref="BulkClosureRule"/>: sets <c>is_service</c> from the ref maps and
    /// the <see cref="DqFlags.BulkClosure"/> label from the current closures. Run before reading for a refresh.
    /// </summary>
    public async Task<Classification> ClassifyAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QuerySingleAsync<Classification>(new CommandDefinition(
            "dbo.usp_classify_for_metrics",
            new
            {
                bulk_flag = (int)DqFlags.BulkClosure,
                date_problems = (int)DqFlags.DateProblems,
                min_count = BulkClosureRule.MinCount,
                sweep_min_count = BulkClosureRule.SweepMinCount,
                detect_age_days = BulkClosureRule.DetectAgeDays,
                member_age_days = BulkClosureRule.MemberAgeDays,
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Bulk-copies <paramref name="set"/> into the <c>stg.agg_*</c> tables, builds the exclusion breakdown and the
    /// clear-out notes with <c>usp_build_exclusions</c> and <c>usp_build_clear_outs</c>, then <c>usp_refresh_aggregates</c> swaps them into the <c>agg</c> tables in one
    /// transaction. Callers hold the ingest lock, so no other refresh shares staging. Returns the new <c>agg.refresh</c> id.
    /// </summary>
    public async Task<int> PublishAsync(AggregateSet set, long? runId, int buildMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            "TRUNCATE TABLE stg.agg_stats_window; TRUNCATE TABLE stg.agg_open_backlog; TRUNCATE TABLE stg.agg_backlog_daily;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        using (var t = StatsTable(set.Windows))
        {
            await BulkCopyAsync(conn, "stg.agg_stats_window", t, cancellationToken).ConfigureAwait(false);
        }

        using (var t = OpenTable(set.Open))
        {
            await BulkCopyAsync(conn, "stg.agg_open_backlog", t, cancellationToken).ConfigureAwait(false);
        }

        using (var t = BacklogTable(set.Backlog))
        {
            await BulkCopyAsync(conn, "stg.agg_backlog_daily", t, cancellationToken).ConfigureAwait(false);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "dbo.usp_build_exclusions",
            new
            {
                as_of_date = set.AsOfDate.ToDateTime(TimeOnly.MinValue),
                windows = string.Join(',', AggregateBuilder.Windows),
                date_problems = (int)DqFlags.DateProblems,
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await conn.ExecuteAsync(new CommandDefinition(
            "dbo.usp_build_clear_outs",
            new
            {
                from_date = AggregateBuilder.BacklogStart.ToDateTime(TimeOnly.MinValue),
                bulk_flag = (int)DqFlags.BulkClosure,
                sweep_min_count = BulkClosureRule.SweepMinCount,
                detect_age_days = BulkClosureRule.DetectAgeDays,
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.usp_refresh_aggregates",
            new
            {
                run_id = runId,
                as_of_utc = set.AsOfUtc,
                as_of_date = set.AsOfDate.ToDateTime(TimeOnly.MinValue),
                request_count = set.RequestCount,
                build_ms = buildMs,
            },
            commandType: CommandType.StoredProcedure,
            commandTimeout: 300,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The newest refresh, or null if the aggregates were never computed.</summary>
    public async Task<AggregateRefresh?> LatestAsync(CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<RefreshRow>(new CommandDefinition(
            """
            SELECT TOP (1) refresh_id AS RefreshId, run_id AS RunId, as_of_utc AS AsOfUtc, as_of_date AS AsOfDate,
                   request_count AS RequestCount, build_ms AS BuildMs, refreshed_utc AS RefreshedUtc
            FROM agg.refresh
            ORDER BY refresh_id DESC;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToRefresh();
    }

    private static StatusGroup ParseStatus(string value) => value switch
    {
        "Open" => StatusGroup.Open,
        "Closed" => StatusGroup.Closed,
        "Cancelled" => StatusGroup.Cancelled,
        _ => StatusGroup.Unknown,
    };

    private static async Task BulkCopyAsync(SqlConnection conn, string table, DataTable rows, CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.CheckConstraints, null)
        {
            DestinationTableName = table,
            BatchSize = 0,
            BulkCopyTimeout = 300,
        };
        foreach (DataColumn c in rows.Columns)
        {
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        }

        await bulk.WriteToServerAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    private static DataTable NewTable(params (string Name, Type Type)[] columns)
    {
        var t = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in columns)
        {
            t.Columns.Add(name, type);
        }

        return t;
    }

    private static DataTable StatsTable(IReadOnlyList<WindowStats> rows)
    {
        var t = NewTable(
            ("window_days", typeof(short)), ("period", typeof(string)), ("neighborhood_slug", typeof(string)), ("district_number", typeof(byte)),
            ("category_group", typeof(string)), ("opened_count", typeof(int)), ("closed_count", typeof(int)), ("excluded_count", typeof(int)),
            ("bulk_closed_count", typeof(int)), ("median_days", typeof(decimal)), ("p90_days", typeof(decimal)));
        foreach (var r in rows)
        {
            t.Rows.Add((short)r.WindowDays, r.Period, r.Cell.NeighborhoodSlug, r.Cell.DistrictNumber, r.Cell.CategoryGroup,
                r.Opened, r.Closed, r.Excluded, r.BulkClosed, (object?)r.MedianDays ?? DBNull.Value, (object?)r.P90Days ?? DBNull.Value);
        }

        return t;
    }

    private static DataTable OpenTable(IReadOnlyList<OpenStats> rows)
    {
        var t = NewTable(
            ("neighborhood_slug", typeof(string)), ("district_number", typeof(byte)), ("category_group", typeof(string)),
            ("open_count", typeof(int)), ("median_open_age_days", typeof(decimal)));
        foreach (var r in rows)
        {
            t.Rows.Add(r.Cell.NeighborhoodSlug, r.Cell.DistrictNumber, r.Cell.CategoryGroup, r.Open, (object?)r.MedianAgeDays ?? DBNull.Value);
        }

        return t;
    }

    private static DataTable BacklogTable(IReadOnlyList<BacklogDay> rows)
    {
        var t = NewTable(
            ("district_number", typeof(byte)), ("category_group", typeof(string)), ("day", typeof(DateTime)),
            ("opened_count", typeof(int)), ("closed_count", typeof(int)), ("open_count", typeof(int)));
        foreach (var r in rows)
        {
            t.Rows.Add(r.DistrictNumber, r.CategoryGroup, r.Day.ToDateTime(TimeOnly.MinValue), r.Opened, r.Closed, r.Open);
        }

        return t;
    }

    private sealed record RefreshRow(int RefreshId, long? RunId, DateTime AsOfUtc, DateTime AsOfDate, int RequestCount, int BuildMs, DateTime RefreshedUtc)
    {
        public AggregateRefresh ToRefresh() => new(RefreshId, RunId, DateTime.SpecifyKind(AsOfUtc, DateTimeKind.Utc), DateOnly.FromDateTime(AsOfDate),
            RequestCount, BuildMs, DateTime.SpecifyKind(RefreshedUtc, DateTimeKind.Utc));
    }
}
