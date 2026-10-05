using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Sac311.Api.Data;
using Sac311.Data.Aggregates;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Endpoints;

/// <summary>What's getting slower: the cells whose trend (<see cref="Trend.Compare"/>) is "slower", worst first.</summary>
internal static class TrendEndpoints
{
    public const string ByNeighborhood = "neighborhood";
    public const string ByCategory = "category";
    public const int DefaultLimit = 10;
    public const int MaxLimit = 50;

    public static RouteGroupBuilder MapTrendEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/trends/slower", SlowerAsync)
            .WithTags("Trends")
            .WithSummary("Neighborhoods or categories whose median days to close went up against the prior period, ranked by days added.");
        return api;
    }

    private static async Task<Results<Ok<SlowerResponse>, ValidationProblem, ProblemHttpResult>> SlowerAsync(
        [Description("'neighborhood' (default) or 'category'.")] string? by,
        [Description("Window in days: 30, 90 (default) or 365. Compares the last N days with the N days before.")] int? window,
        [Description("Category group, e.g. 'Parking' (any case): rank neighborhoods within it. Only with by=neighborhood.")] string? category,
        [Description("Council district 1-8. Omit for the whole city.")] int? district,
        [Description("How many to list, 1-50 (default 10). The counts in 'compared' cover all of them.")] int? limit,
        StatsReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var dimension = (by ?? ByNeighborhood).Trim().ToLowerInvariant();
        if (dimension is not (ByNeighborhood or ByCategory))
        {
            errors["by"] = [$"by must be '{ByNeighborhood}' or '{ByCategory}'."];
        }

        var windowDays = QueryRules.Window(window, errors);
        var districtNumber = QueryRules.District(district, errors);
        var group = await QueryRules.CategoryAsync(category, reader, errors, cancellationToken).ConfigureAwait(false);
        if (group is not null && dimension == ByCategory)
        {
            errors["category"] = ["category ranks neighborhoods within one category, so it can't be combined with by=category."];
        }

        var count = limit ?? DefaultLimit;
        if (count is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be from 1 to {MaxLimit}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return StatsEndpoints.NoAggregates();
        }

        IReadOnlyList<(string Key, string Name, CellStats Stats)> compared;
        if (dimension == ByNeighborhood)
        {
            var names = (await reader.NeighborhoodsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(n => n.Slug, n => n.Name, StringComparer.Ordinal);
            var cells = await reader.CellsAsync(windowDays, districtNumber, null, group ?? AggregateCell.All, cancellationToken).ConfigureAwait(false);
            compared = [.. cells.Select(c => (c.NeighborhoodSlug, names.GetValueOrDefault(c.NeighborhoodSlug, c.NeighborhoodSlug), CellStats.Of(c, refresh.AsOfDate, windowDays)))];
        }
        else
        {
            var cells = await reader.CellsAsync(windowDays, districtNumber, AggregateCell.All, null, cancellationToken).ConfigureAwait(false);
            compared = [.. cells.Where(c => c.CategoryGroup != AggregateCell.All).Select(c => (c.CategoryGroup, c.CategoryGroup, CellStats.Of(c, refresh.AsOfDate, windowDays)))];
        }

        var counts = new TrendCounts(
            compared.Count(c => c.Stats.Trend?.Direction == Trend.Slower),
            compared.Count(c => c.Stats.Trend?.Direction == Trend.Steady),
            compared.Count(c => c.Stats.Trend?.Direction == Trend.Faster),
            compared.Count(c => c.Stats.Trend is null));
        return TypedResults.Ok(new SlowerResponse(dimension, windowDays, group, district, counts, Rank(compared, count), refresh.AsOfUtc));
    }

    /// <summary>
    /// The slower cells, most days added first (then the larger change, then by name). Days added rather than percent:
    /// a median going from 0.5 to 1.5 days is +200% but rarely what someone waiting notices; 20 to 40 days is.
    /// </summary>
    internal static IReadOnlyList<SlowerItem> Rank(IEnumerable<(string Key, string Name, CellStats Stats)> cells, int limit) =>
    [
        .. cells
            .Where(c => c.Stats.Trend?.Direction == Trend.Slower)
            .Select(c => new SlowerItem(
                c.Key, c.Name, c.Stats.Current, c.Stats.Prior, c.Stats.Current.MedianDays!.Value - c.Stats.Prior.MedianDays!.Value, c.Stats.Trend!, c.Stats.OpenBacklog))
            .OrderByDescending(i => i.DaysAdded)
            .ThenByDescending(i => i.Trend.MedianChangePct ?? decimal.MaxValue)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .Take(limit),
    ];
}
