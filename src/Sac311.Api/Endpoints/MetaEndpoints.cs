using System.ComponentModel;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Sac311.Api.Data;
using Sac311.Api.Health;
using Sac311.Data.Aggregates;
using Sac311.Domain;
using Sac311.Domain.Aggregates;

namespace Sac311.Api.Endpoints;

internal static class MetaEndpoints
{
    public static RouteGroupBuilder MapMetaEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meta/freshness", FreshnessAsync)
            .WithTags("Meta")
            .WithSummary("How current the data is: last runs, the incremental watermark, request count and data-quality results.");

        api.MapGet("/meta/exclusions", ExclusionsAsync)
            .WithTags("Meta")
            .WithSummary("What the headline figures leave out and why: non-service requests by type and date problems, with counts.");

        api.MapGet("/meta/clear-outs", ClearOutsAsync)
            .WithTags("Meta")
            .WithSummary("Clear-outs of old requests, counted in every figure as recorded, each with a one-sentence note.");
        return api;
    }

    private static async Task<Ok<FreshnessResponse>> FreshnessAsync(
        FreshnessReader reader, AggregateStore store, TimeProvider time, IOptions<FreshnessOptions> options, CancellationToken cancellationToken)
    {
        var maxAge = options.Value.MaxAge;
        var lastSuccess = await reader.LastSuccessAsync(cancellationToken).ConfigureAwait(false);
        var lastRun = await reader.LastRunAsync(cancellationToken).ConfigureAwait(false);
        var watermark = await reader.WatermarkAsync(cancellationToken).ConfigureAwait(false);
        var refresh = await store.LatestAsync(cancellationToken).ConfigureAwait(false);
        var checks = await reader.LatestDqAsync(cancellationToken).ConfigureAwait(false);

        var dq = new DqSummary(
            checks.Count(c => c.Status == "Pass"),
            checks.Count(c => c.Status == "Warn"),
            checks.Count(c => c.Status == "Fail"),
            checks.Count(c => c.Status == "Info"),
            [.. checks.Where(c => c.Status is "Warn" or "Fail")]);

        return TypedResults.Ok(new FreshnessResponse(
            Freshness.Evaluate(lastSuccess?.FinishedUtc, time.GetUtcNow().UtcDateTime, maxAge),
            (int)maxAge.TotalMinutes,
            lastSuccess,
            lastRun,
            watermark,
            refresh?.RequestCount,
            refresh?.AsOfDate,
            refresh?.RefreshedUtc,
            dq));
    }

    private static async Task<Results<Ok<ExclusionsResponse>, ValidationProblem, ProblemHttpResult>> ExclusionsAsync(
        [Description("Window in days: 30, 90 (default) or 365, as in the other endpoints.")] int? window,
        ExclusionReader reader,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var windowDays = QueryRules.Window(window, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await store.LatestAsync(cancellationToken).ConfigureAwait(false) is not { } refresh)
        {
            return StatsEndpoints.NoAggregates();
        }

        var rows = await reader.RowsAsync(windowDays, cancellationToken).ConfigureAwait(false);
        var open = rows.Where(r => r is { Period: "now", Kind: "non_service" })
            .Select(r => new NonServiceOpen(r.CategoryGroup, r.Label, r.Reason ?? "", r.Open))
            .OrderByDescending(t => t.Open).ThenBy(t => t.Type, StringComparer.Ordinal)
            .ToList();

        return TypedResults.Ok(new ExclusionsResponse(
            windowDays,
            Period(rows, AggregatePeriod.Current, refresh.AsOfDate, windowDays),
            Period(rows, AggregatePeriod.Prior, refresh.AsOfDate, windowDays),
            new NonServiceOpenNow(open.Sum(t => t.Open), open),
            ClearOutRule,
            Definitions,
            refresh.AsOfUtc));
    }

    private static async Task<Results<Ok<ClearOutsResponse>, ValidationProblem, ProblemHttpResult>> ClearOutsAsync(
        [Description("First day (yyyy-MM-dd). Default and earliest: 2024-01-01.")] DateOnly? from,
        [Description("Last day (yyyy-MM-dd). Default and latest: the as-of date.")] DateOnly? to,
        [Description("Category group, e.g. 'Parking' (any case). Omit for all categories.")] string? category,
        ClearOutReader reader,
        StatsReader stats,
        AggregateStore store,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var group = await QueryRules.CategoryAsync(category, stats, errors, cancellationToken).ConfigureAwait(false);
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
            return StatsEndpoints.NoAggregates();
        }

        var first = from is { } f && f > AggregateBuilder.BacklogStart ? f : AggregateBuilder.BacklogStart;
        var last = to is { } t && t < refresh.AsOfDate ? t : refresh.AsOfDate;
        var notes = (await reader.RowsAsync(first, last, group, cancellationToken).ConfigureAwait(false))
            .Select(ClearOutNotes.Of)
            .ToList();
        return TypedResults.Ok(new ClearOutsResponse(first, last, group, notes.Sum(n => n.Closed), notes, ClearOutRule, Definitions, refresh.AsOfUtc));
    }

    private static ClearOutRules ClearOutRule => new(
        BulkClosureRule.MinCount, BulkClosureRule.SweepMinCount, BulkClosureRule.DetectAgeDays, BulkClosureRule.MemberAgeDays, "/api/meta/clear-outs");

    private static ExclusionPeriod Period(IReadOnlyList<ExclusionRow> rows, string period, DateOnly asOfDate, int windowDays)
    {
        var to = period == AggregatePeriod.Current ? asOfDate : asOfDate.AddDays(-windowDays);
        var mine = rows.Where(r => r.Period == period).ToList();

        var types = mine.Where(r => r.Kind == "non_service")
            .Select(r => new NonServiceType(r.CategoryGroup, r.Label, r.Reason ?? "", r.Opened, r.Closed))
            .OrderByDescending(t => t.Opened + t.Closed).ThenBy(t => t.Type, StringComparer.Ordinal)
            .ToList();
        var dates = mine.Where(r => r.Kind == "dq_flag")
            .Select(r => new DateProblemExclusion(r.Label, r.Reason ?? "", r.Closed))
            .OrderByDescending(d => d.Closed).ThenBy(d => d.Flag, StringComparer.Ordinal)
            .ToList();

        return new ExclusionPeriod(
            to.AddDays(1 - windowDays),
            to,
            new NonServiceSummary(types.Sum(t => t.Opened), types.Sum(t => t.Closed), types),
            dates);
    }

    /// <summary>The public write-up of every exclusion.</summary>
    public const string Definitions = "https://github.com/eduong8366/RiverCity-Pulse/blob/main/docs/metrics.md";}
