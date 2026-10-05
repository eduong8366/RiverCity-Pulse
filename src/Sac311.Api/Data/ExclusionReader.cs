using Dapper;
using Sac311.Data;

namespace Sac311.Api.Data;

/// <summary>One row of <c>agg.exclusion</c> (see usp_build_exclusions for what each kind holds).</summary>
internal sealed record ExclusionRow(
    string Period, string Kind, string CategoryGroup, string Label, DateTime? Day, string? Reason, int Opened, int Closed, int Open, decimal? AvgAgeDays);

/// <summary>Reads the exclusion breakdown the worker publishes with each refresh.</summary>
internal sealed class ExclusionReader(Sac311Db db)
{
    /// <summary>The current and prior rows of one window, plus the open-now rows.</summary>
    public async Task<IReadOnlyList<ExclusionRow>> RowsAsync(int window, CancellationToken cancellationToken)
    {
        await using var conn = await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ExclusionRow>(new CommandDefinition(
            """
            SELECT period AS Period, kind AS Kind, category_group AS CategoryGroup, label AS Label, day AS Day, reason AS Reason,
                   opened_count AS Opened, closed_count AS Closed, open_count AS [Open], avg_age_days AS AvgAgeDays
            FROM agg.exclusion
            WHERE window_days = @window OR period = 'now';
            """,
            new { window },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
