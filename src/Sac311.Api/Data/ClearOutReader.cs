using Dapper;
using Sac311.Data;

namespace Sac311.Api.Data;

/// <summary>One row of <c>agg.clear_out</c> (see usp_build_clear_outs).</summary>
internal sealed record ClearOutRow(
    DateTime Day, string CategoryGroup, int Closed, decimal AvgAgeDays, int MinutesSpanned, bool IsSweep, string? SweepCategories);

/// <summary>Reads the clear-out notes the worker publishes with each refresh.</summary>
internal sealed class ClearOutReader(Sac311Db db)
{
    /// <summary>Clear-outs closed from <paramref name="from"/> to <paramref name="to"/>, in one category group or all (null), newest first.</summary>
    public async Task<IReadOnlyList<ClearOutRow>> RowsAsync(DateOnly from, DateOnly to, string? category, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ClearOutRow>(new CommandDefinition(
            """
            SELECT day AS Day, category_group AS CategoryGroup, closed_count AS Closed, avg_age_days AS AvgAgeDays,
                   minutes_spanned AS MinutesSpanned, is_sweep AS IsSweep, sweep_categories AS SweepCategories
            FROM agg.clear_out
            WHERE day BETWEEN @from AND @to AND (@category IS NULL OR category_group = @category)
            ORDER BY day DESC, closed_count DESC, category_group;
            """,
            new { from = from.ToDateTime(TimeOnly.MinValue), to = to.ToDateTime(TimeOnly.MinValue), category = new DbString { Value = category, IsAnsi = false, Length = 100 } },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
