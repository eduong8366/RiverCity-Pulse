using System.Globalization;
using Sac311.Api.Data;

namespace Sac311.Api.Endpoints;

/// <summary>
/// The note for a clear-out: one factual sentence generated from its row, never written by hand (docs/metrics.md),
/// e.g. "On 2026-08-10, Parking closed 10,820 requests averaging 564 days old in one minute."
/// </summary>
internal static class ClearOutNotes
{
    public static ClearOutNote Of(ClearOutRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var date = DateOnly.FromDateTime(row.Day);
        var others = string.IsNullOrEmpty(row.SweepCategories)
            ? []
            : row.SweepCategories.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        return new ClearOutNote(date, row.CategoryGroup, row.Closed, row.AvgAgeDays, row.MinutesSpanned, row.IsSweep, others,
            Sentence(date, row.CategoryGroup, row.Closed, row.AvgAgeDays, row.MinutesSpanned, others));
    }

    public static string Sentence(DateOnly date, string category, int closed, decimal averageDays, int minutesSpanned, IReadOnlyList<string> sweepCategories)
    {
        ArgumentNullException.ThrowIfNull(sweepCategories);
        var c = CultureInfo.InvariantCulture;
        var days = Math.Round(averageDays, MidpointRounding.AwayFromZero).ToString("N0", c);
        var what = closed == 1
            ? $"1 request {days} days old"
            : string.Create(c, $"{closed:N0} requests averaging {days} days old");
        var when = closed == 1 ? "" : minutesSpanned <= 1 ? " in one minute" : string.Create(c, $" over {minutesSpanned:N0} minutes");
        var sentence = $"On {date.ToString("yyyy-MM-dd", c)}, {category} closed {what}{when}.";
        return sweepCategories.Count == 0
            ? sentence
            : $"{sentence} In the same minute, {Join(sweepCategories)} also closed old requests.";
    }

    private static string Join(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
}
