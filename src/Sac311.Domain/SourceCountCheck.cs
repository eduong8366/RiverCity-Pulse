using System.Globalization;
using System.Text.RegularExpressions;

namespace Sac311.Domain;

/// <summary>How the live row count compares with the baseline.</summary>
public enum SourceCountOutcome
{
    /// <summary>Within ±<see cref="SourceCountCheck.Tolerance"/> of the baseline.</summary>
    Within,

    /// <summary>More than the tolerance above the baseline: usually normal growth, so the baseline needs a refresh.</summary>
    Grew,

    /// <summary>More than the tolerance below the baseline: the feed lost rows or was republished.</summary>
    Dropped,
}

/// <summary>The live count against the baseline. <see cref="Change"/> is a fraction (0.031 is +3.1 %).</summary>
public sealed record SourceCountResult(long Baseline, long Count, double Change, SourceCountOutcome Outcome);

/// <summary>
/// The nightly source check's count rule (<c>worker verify-source --check</c>): the live row count must stay within
/// ±<see cref="Tolerance"/> of the "Row count" line in docs/source-profile.md. The baseline lives in the repo so the
/// check needs no database or saved state; the feed grows about 1,500 rows a day, so the full profile is rerun and
/// committed every month or two.
/// </summary>
public static partial class SourceCountCheck
{
    /// <summary>Largest change, either way, that still passes (inclusive).</summary>
    public const double Tolerance = 0.05;

    /// <summary>Reads the baseline from the profile's "Row count" table row. False when it's missing, unparsable or not positive.</summary>
    public static bool TryParseBaseline(string profileMarkdown, out long baseline)
    {
        baseline = 0;
        var match = RowCountLine().Match(profileMarkdown);
        return match.Success
            && long.TryParse(match.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out baseline)
            && baseline > 0;
    }

    public static SourceCountResult Compare(long baseline, long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baseline);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var change = (double)(count - baseline) / baseline;
        // Integer comparison so exactly 5 % passes without a floating-point edge: |count - baseline| * 20 <= baseline.
        var outcome = Math.Abs(count - baseline) * 20 <= baseline ? SourceCountOutcome.Within
            : count > baseline ? SourceCountOutcome.Grew
            : SourceCountOutcome.Dropped;
        return new SourceCountResult(baseline, count, change, outcome);
    }

    [GeneratedRegex(@"^\|\s*Row count\s*\|\s*([0-9][0-9,]*)\s*\|", RegexOptions.Multiline)]
    private static partial Regex RowCountLine();
}
