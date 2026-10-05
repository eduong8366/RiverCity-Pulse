using System.Globalization;
using Sac311.Data.Quality;

namespace Sac311.Ingestion;

/// <summary>
/// The pass/warn/fail rules of the data-quality checks, kept free of I/O so the thresholds are unit-tested.
/// <see cref="DqRunner"/> gathers the numbers and records what these return.
/// </summary>
public static class DqRules
{
    /// <summary>A source count below this share of the recent high is a Fail (a drop of more than 2%).</summary>
    public const decimal SourceCountFloor = 0.98m;

    /// <summary>A recent null rate more than this many percentage points above the baseline is a Warn.</summary>
    public const decimal NullRateTolerancePoints = 5m;

    /// <summary>Fewer rows than this in either period make a null rate too noisy to judge (the API's trend rule uses 30 too).</summary>
    public const int MinNullRateRows = 30;

    /// <summary>A flag count rising by more than this share of its previous value (and <see cref="MinFlagJump"/>) is a Warn.</summary>
    public const decimal FlagJumpShare = 0.01m;

    public const int MinFlagJump = 50;

    /// <summary>One address with at least this many new requests in <see cref="DqRunner.RecentDays"/> days is a Warn (a form default or a stuck integration, most likely).</summary>
    public const int RepeatAddressMin = 100;

    public static DqResult SourceCount(long observed, decimal? recentHigh)
    {
        const string name = "source_count";
        if (recentHigh is not { } high || high <= 0)
        {
            return new(name, DqStatus.Info, observed, null, null, Invariant($"Source serves {observed:N0} rows; no recent count to compare with."));
        }

        var floor = Math.Round(high * SourceCountFloor, 0);
        return observed < floor
            ? new(name, DqStatus.Fail, observed, high, floor, Invariant($"Source serves {observed:N0} rows, down {1 - (observed / high):P1} from the recent high of {high:N0}."))
            : new(name, DqStatus.Pass, observed, high, floor, Invariant($"Source serves {observed:N0} rows (recent high {high:N0})."));
    }

    /// <param name="column">Short column name for the check name (<c>null_rate.address</c>).</param>
    public static DqResult NullRate(string column, int recentNull, int recentRows, int baselineNull, int baselineRows)
    {
        var name = "null_rate." + column;
        decimal? recent = recentRows > 0 ? Math.Round(100m * recentNull / recentRows, 2) : null;
        decimal? baseline = baselineRows > 0 ? Math.Round(100m * baselineNull / baselineRows, 2) : null;
        if (recentRows < MinNullRateRows || baselineRows < MinNullRateRows)
        {
            return new(name, DqStatus.Info, recent, baseline, null,
                Invariant($"Too few rows to judge ({recentRows:N0} recent, {baselineRows:N0} baseline; at least {MinNullRateRows} each)."));
        }

        var limit = baseline!.Value + NullRateTolerancePoints;
        return recent > limit
            ? new(name, DqStatus.Warn, recent, baseline, limit, Invariant($"{column} is null in {recent}% of recent requests, up from {baseline}%."))
            : new(name, DqStatus.Pass, recent, baseline, limit, Invariant($"{column} is null in {recent}% of recent requests ({baseline}% before)."));
    }

    /// <param name="flag">The <see cref="Domain.DqFlags"/> member name.</param>
    public static DqResult FlagCount(string flag, int observed, decimal? previous)
    {
        var name = "flag." + flag;
        if (previous is not { } p)
        {
            return new(name, DqStatus.Info, observed, null, null, Invariant($"{observed:N0} requests flagged {flag}; first count."));
        }

        var limit = p + Math.Max(MinFlagJump, Math.Round(p * FlagJumpShare, 0));
        return observed > limit
            ? new(name, DqStatus.Warn, observed, p, limit, Invariant($"{observed:N0} requests flagged {flag}, up {observed - p:N0} since the last check."))
            : new(name, DqStatus.Pass, observed, p, limit, Invariant($"{observed:N0} requests flagged {flag} ({p:N0} at the last check)."));
    }

    /// <param name="busiest">The addresses with the most requests created in the recent days, busiest first.</param>
    public static DqResult RepeatAddress(IReadOnlyList<(string Address, int Count)> busiest)
    {
        ArgumentNullException.ThrowIfNull(busiest);
        const string name = "repeat_address";
        if (busiest.Count == 0)
        {
            return new(name, DqStatus.Pass, 0, null, RepeatAddressMin, "No recent requests have an address.");
        }

        var over = busiest.Where(b => b.Count >= RepeatAddressMin).ToList();
        return over.Count > 0
            ? new(name, DqStatus.Warn, busiest[0].Count, null, RepeatAddressMin, Invariant($"{over.Count} address(es) with {RepeatAddressMin}+ requests in {DqRunner.RecentDays} days: {string.Join("; ", over.Select(b => Invariant($"{b.Address} ({b.Count:N0})")))}."))
            : new(name, DqStatus.Pass, busiest[0].Count, null, RepeatAddressMin, Invariant($"The busiest address had {busiest[0].Count:N0} requests in {DqRunner.RecentDays} days ({busiest[0].Address})."));
    }

    public static DqResult Rejects(int rejected) => rejected > 0
        ? new("rejects", DqStatus.Warn, rejected, null, 0, Invariant($"The run rejected {rejected:N0} rows; see ops.ingest_reject."))
        : new("rejects", DqStatus.Pass, 0, null, 0, "The run rejected no rows.");

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
