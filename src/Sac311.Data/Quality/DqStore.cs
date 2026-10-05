using System.Globalization;
using Dapper;
using Sac311.Domain;

namespace Sac311.Data.Quality;

/// <summary>Values of <c>ops.dq_result.status</c>.</summary>
public static class DqStatus
{
    public const string Pass = "Pass";
    public const string Warn = "Warn";
    public const string Fail = "Fail";
    public const string Info = "Info";
}

/// <summary>One row of <c>ops.dq_result</c>.</summary>
public sealed record DqResult(string CheckName, string Status, decimal? ObservedValue, decimal? BaselineValue, decimal? Threshold, string Message);

/// <summary>Null counts for one created-date period. Neighborhood is counted over city rows only.</summary>
public sealed record NullCounts(
    int Rows, int AddressNull, int ZipNull, int GeoNull, int CategoryNull, int SourceNull, int CityRows, int NeighborhoodNull);

/// <summary>The queries behind the data-quality checks, and <c>ops.dq_result</c> itself.</summary>
public sealed class DqStore(Sac311Db db)
{
    /// <summary>The single-bit <see cref="DqFlags"/> members, each counted by <see cref="CountFlagsAsync"/>.</summary>
    public static readonly IReadOnlyList<DqFlags> TrackedFlags =
        [.. Enum.GetValues<DqFlags>().Where(f => f != DqFlags.None && int.IsPow2((int)f))];

    /// <summary>
    /// Null counts for requests created in (<paramref name="todayLocal"/> − <paramref name="recentDays"/>, today] and in the
    /// <paramref name="baselineDays"/> before that, over requests still in the source.
    /// </summary>
    public async Task<(NullCounts Recent, NullCounts Baseline)> CountNullsAsync(DateOnly todayLocal, int recentDays, int baselineDays, CancellationToken cancellationToken)
    {
        var today = todayLocal.ToDateTime(TimeOnly.MinValue);
        var recentFrom = today.AddDays(-recentDays);
        var baselineFrom = recentFrom.AddDays(-baselineDays);

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<NullRow>(new CommandDefinition(
            """
            SELECT
                CAST(CASE WHEN created_date_local > @recentFrom THEN 1 ELSE 0 END AS bit) AS IsRecent,
                COUNT(*) AS Rows,
                SUM(CASE WHEN address IS NULL THEN 1 ELSE 0 END) AS AddressNull,
                SUM(CASE WHEN zip IS NULL THEN 1 ELSE 0 END) AS ZipNull,
                SUM(CASE WHEN latitude IS NULL THEN 1 ELSE 0 END) AS GeoNull,
                SUM(CASE WHEN category_level1 IS NULL THEN 1 ELSE 0 END) AS CategoryNull,
                SUM(CASE WHEN source_channel_raw IS NULL THEN 1 ELSE 0 END) AS SourceNull,
                SUM(CASE WHEN is_city = 1 THEN 1 ELSE 0 END) AS CityRows,
                SUM(CASE WHEN is_city = 1 AND neighborhood_slug IS NULL THEN 1 ELSE 0 END) AS NeighborhoodNull
            FROM dbo.service_request
            WHERE created_date_local > @baselineFrom AND created_date_local <= @today AND source_removed_utc IS NULL
            GROUP BY CASE WHEN created_date_local > @recentFrom THEN 1 ELSE 0 END;
            """,
            new { today, recentFrom, baselineFrom },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var empty = new NullCounts(0, 0, 0, 0, 0, 0, 0, 0);
        var list = rows.ToList();
        return (list.Find(r => r.IsRecent)?.ToCounts() ?? empty, list.Find(r => !r.IsRecent)?.ToCounts() ?? empty);
    }

    /// <summary>
    /// The <paramref name="top"/> addresses with the most requests created in (<paramref name="todayLocal"/> −
    /// <paramref name="recentDays"/>, today], busiest first, over requests still in the source.
    /// </summary>
    public async Task<IReadOnlyList<(string Address, int Count)>> BusiestAddressesAsync(DateOnly todayLocal, int recentDays, int top, CancellationToken cancellationToken)
    {
        var today = todayLocal.ToDateTime(TimeOnly.MinValue);
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string, int)>(new CommandDefinition(
            """
            SELECT TOP (@top) address, COUNT(*)
            FROM dbo.service_request
            WHERE created_date_local > @recentFrom AND created_date_local <= @today AND address IS NOT NULL AND source_removed_utc IS NULL
            GROUP BY address
            ORDER BY COUNT(*) DESC, address;
            """,
            new { top, today, recentFrom = today.AddDays(-recentDays) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    /// <summary>How many requests still in the source carry each of <see cref="TrackedFlags"/>, in one scan.</summary>
    public async Task<IReadOnlyDictionary<DqFlags, int>> CountFlagsAsync(CancellationToken cancellationToken)
    {
        var columns = string.Join(",\n    ", TrackedFlags.Select(f =>
            string.Create(CultureInfo.InvariantCulture, $"SUM(CASE WHEN dq_flags & {(int)f} <> 0 THEN 1 ELSE 0 END) AS [{f}]")));

        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = (IDictionary<string, object?>)await conn.QuerySingleAsync(new CommandDefinition(
            $"SELECT\n    {columns}\nFROM dbo.service_request\nWHERE source_removed_utc IS NULL;",
            commandTimeout: 300,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // SUM over no rows is NULL.
        return TrackedFlags.ToDictionary(f => f, f => row[f.ToString()] is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>The highest value recorded for <paramref name="checkName"/> since <paramref name="sinceUtc"/>, or null.</summary>
    public async Task<decimal?> MaxObservedAsync(string checkName, DateTime sinceUtc, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<decimal?>(new CommandDefinition(
            "SELECT MAX(observed_value) FROM ops.dq_result WHERE check_name = @checkName AND checked_utc >= @sinceUtc;",
            new { checkName, sinceUtc },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>The most recent value recorded for each check whose name starts with <paramref name="prefix"/>.</summary>
    public async Task<IReadOnlyDictionary<string, decimal>> LatestObservedAsync(string prefix, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<Observed>(new CommandDefinition(
            """
            SELECT check_name AS CheckName, observed_value AS ObservedValue
            FROM
            (
                SELECT check_name, observed_value,
                       ROW_NUMBER() OVER (PARTITION BY check_name ORDER BY checked_utc DESC, dq_result_id DESC) AS rn
                FROM ops.dq_result
                WHERE check_name LIKE @pattern AND observed_value IS NOT NULL
            ) AS x
            WHERE rn = 1;
            """,
            new { pattern = prefix + "%" },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.CheckName, r => r.ObservedValue, StringComparer.Ordinal);
    }

    public async Task<int> CountRejectsAsync(long runId, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM ops.ingest_reject WHERE run_id = @runId;", new { runId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SaveAsync(long? runId, IReadOnlyCollection<DqResult> results, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ops.dq_result (run_id, check_name, status, observed_value, baseline_value, threshold, message)
            VALUES (@runId, @CheckName, @Status, @ObservedValue, @BaselineValue, @Threshold, @Message);
            """,
            results.Select(r => new { runId, r.CheckName, r.Status, r.ObservedValue, r.BaselineValue, r.Threshold, r.Message }),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed record NullRow(
        bool IsRecent, int Rows, int AddressNull, int ZipNull, int GeoNull, int CategoryNull, int SourceNull, int CityRows, int NeighborhoodNull)
    {
        public NullCounts ToCounts() => new(Rows, AddressNull, ZipNull, GeoNull, CategoryNull, SourceNull, CityRows, NeighborhoodNull);
    }

    private sealed record Observed(string CheckName, decimal ObservedValue);
}
