using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Sac311.Api.Data;
using Sac311.Data.Aggregates;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Endpoints;

/// <summary>The dashboard's data: point lookups on the <c>agg</c> tables the worker refreshes after each run.</summary>
internal static class StatsEndpoints
{
    private const string WindowHelp = "Window in days: 30, 90 (default) or 365. The current period is the last N days through the as-of date; the prior period is the N days before.";
    private const string CategoryHelp = "Category group, e.g. 'Solid Waste' (any case). Omit for all categories.";
    private const string DistrictHelp = "Council district 1-8. Omit for the whole city, including requests outside city limits.";

    public static RouteGroupBuilder MapStatsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/neighborhoods", ListNeighborhoodsAsync)
            .WithTags("Neighborhoods")
            .WithSummary("The 129 neighborhoods, with the slugs the other endpoints take.");

        api.MapGet("/neighborhoods/{slug}/stats", NeighborhoodStatsAsync)
            .WithTags("Neighborhoods")
            .WithSummary("Days to close in one neighborhood: median and p90, the prior period and trend, and the open backlog.");

        api.MapGet("/categories/summary", CategorySummaryAsync)
            .WithTags("Categories")
            .WithSummary("The same figures per category group, plus the total, citywide or for one district, optionally inside one neighborhood.");

        api.MapGet("/map/neighborhoods", MapNeighborhoodsAsync)
            .WithTags("Map")
            .WithSummary("Current-period figures per neighborhood, for the choropleth.");

        api.MapGet("/backlog", BacklogAsync)
            .WithTags("Backlog")
            .WithSummary("Requests opened, closed and still open over time, from 2024-01-01.");

        return api;
    }

    private static async Task<Ok<IReadOnlyList<Neighborhood>>> ListNeighborhoodsAsync(StatsReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.NeighborhoodsAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Results<Ok<NeighborhoodStatsResponse>, ValidationProblem, ProblemHttpResult>> NeighborhoodStatsAsync(
        [Description("Neighborhood slug from /api/neighborhoods, e.g. 'downtown'.")] string slug,
        [Description(CategoryHelp)] string? category,
        [Description(WindowHelp)] int? window,
        StatsReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var windowDays = QueryRules.Window(window, errors);
        var group = await QueryRules.CategoryAsync(category, reader, errors, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var neighborhoods = await reader.NeighborhoodsAsync(cancellationToken).ConfigureAwait(false);
        var neighborhood = neighborhoods.FirstOrDefault(n => string.Equals(n.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (neighborhood is null)
        {
            return TypedResults.Problem($"No neighborhood '{slug}'. See /api/neighborhoods.", statusCode: StatusCodes.Status404NotFound, title: "Unknown neighborhood");
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return NoAggregates();
        }

        var cells = await reader.CellsAsync(windowDays, AggregateCell.AllDistricts, neighborhood.Slug, group ?? AggregateCell.All, cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok(new NeighborhoodStatsResponse(
            neighborhood, group, windowDays, CellStats.Of(cells.Count > 0 ? cells[0] : null, refresh.AsOfDate, windowDays), refresh.AsOfUtc));
    }

    private static async Task<Results<Ok<CategorySummaryResponse>, ValidationProblem, ProblemHttpResult>> CategorySummaryAsync(
        [Description(WindowHelp)] int? window,
        [Description(DistrictHelp)] int? district,
        [Description("Neighborhood slug from /api/neighborhoods, e.g. 'downtown'. Omit for all neighborhoods (and requests with none).")] string? neighborhood,
        StatsReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var windowDays = QueryRules.Window(window, errors);
        var districtNumber = QueryRules.District(district, errors);
        var place = await QueryRules.NeighborhoodAsync(neighborhood, reader, errors, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return NoAggregates();
        }

        var cells = await reader.CellsAsync(windowDays, districtNumber, place?.Slug ?? AggregateCell.All, null, cancellationToken).ConfigureAwait(false);
        var total = CellStats.Of(cells.FirstOrDefault(c => c.CategoryGroup == AggregateCell.All), refresh.AsOfDate, windowDays);
        var categories = cells
            .Where(c => c.CategoryGroup != AggregateCell.All)
            .Select(c => new CategorySummary(c.CategoryGroup, CellStats.Of(c, refresh.AsOfDate, windowDays)))
            .OrderByDescending(c => c.Stats.Current.Opened)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok(new CategorySummaryResponse(windowDays, district, place, total, categories, refresh.AsOfUtc));
    }

    private static async Task<Results<Ok<MapResponse>, ValidationProblem, ProblemHttpResult>> MapNeighborhoodsAsync(
        [Description(WindowHelp)] int? window,
        [Description(CategoryHelp)] string? category,
        [Description(DistrictHelp + " With a district, only neighborhoods with requests in it are listed.")] int? district,
        StatsReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var windowDays = QueryRules.Window(window, errors);
        var districtNumber = QueryRules.District(district, errors);
        var group = await QueryRules.CategoryAsync(category, reader, errors, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return NoAggregates();
        }

        var names = (await reader.NeighborhoodsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(n => n.Slug, n => n.Name, StringComparer.Ordinal);
        var cells = await reader.CellsAsync(windowDays, districtNumber, null, group ?? AggregateCell.All, cancellationToken).ConfigureAwait(false);
        var neighborhoods = cells
            .Select(c =>
            {
                var stats = CellStats.Of(c, refresh.AsOfDate, windowDays);
                return new MapNeighborhood(
                    c.NeighborhoodSlug, names.GetValueOrDefault(c.NeighborhoodSlug, c.NeighborhoodSlug), stats.Current.Opened, stats.Current.Closed, stats.Current.BulkClosed,
                    stats.Current.MedianDays, stats.Current.P90Days, stats.Trend, stats.OpenBacklog);
            })
            .OrderBy(n => n.Name, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok(new MapResponse(windowDays, group, district, neighborhoods, refresh.AsOfUtc));
    }

    private static async Task<Results<Ok<BacklogResponse>, ValidationProblem, ProblemHttpResult>> BacklogAsync(
        [Description("First day (yyyy-MM-dd). Default and earliest: 2024-01-01.")] DateOnly? from,
        [Description("Last day (yyyy-MM-dd). Default and latest: the as-of date.")] DateOnly? to,
        [Description(CategoryHelp)] string? category,
        [Description(DistrictHelp)] int? district,
        [Description("'day' or 'week' (default). A week starts on Monday and is labelled with that date; its open count is the one at the end of its last day.")] string? grain,
        StatsReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var districtNumber = QueryRules.District(district, errors);
        var group = await QueryRules.CategoryAsync(category, reader, errors, cancellationToken).ConfigureAwait(false);
        var weekly = (grain ?? BacklogWeeks.Week).ToUpperInvariant() switch
        {
            "WEEK" => true,
            "DAY" => false,
            _ => (bool?)null,
        };
        if (weekly is null)
        {
            errors["grain"] = ["grain must be 'day' or 'week'."];
        }

        if (from > to)
        {
            errors["from"] = ["from must not be after to."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return NoAggregates();
        }

        var first = from is { } f && f > AggregateBuilder.BacklogStart ? f : AggregateBuilder.BacklogStart;
        var last = to is { } t && t < refresh.AsOfDate ? t : refresh.AsOfDate;
        var days = await reader.BacklogAsync(districtNumber, group ?? AggregateCell.All, first, last, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new BacklogResponse(
            first, last, weekly!.Value ? BacklogWeeks.Week : BacklogWeeks.Day, group, district, weekly.Value ? BacklogWeeks.Group(days) : days, refresh.AsOfUtc));
    }

    internal static ProblemHttpResult NoAggregates() => TypedResults.Problem(
        "The aggregates haven't been computed yet. Load data with the worker's backfill, or run 'worker aggregates'.",
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "No aggregates yet");
}

internal static class BacklogWeeks
{
    public const string Day = "day";
    public const string Week = "week";

    /// <summary>The Monday on or before <paramref name="day"/>.</summary>
    public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Sums opened and closed per Monday-to-Sunday week; the open count is the one at the end of the week's last day.</summary>
    public static IReadOnlyList<BacklogPoint> Group(IReadOnlyList<BacklogPoint> days) =>
    [
        .. days
            .GroupBy(d => WeekStart(d.Date))
            .Select(w => new BacklogPoint(w.Key, w.Sum(d => d.Opened), w.Sum(d => d.Closed), w.MaxBy(d => d.Date)!.Open)),
    ];
}
