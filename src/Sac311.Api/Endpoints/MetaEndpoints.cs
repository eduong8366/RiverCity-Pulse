using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Sac311.Api.Data;
using Sac311.Api.Health;
using Sac311.Data.Aggregates;

namespace Sac311.Api.Endpoints;

internal static class MetaEndpoints
{
    public static RouteGroupBuilder MapMetaEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/meta/freshness", FreshnessAsync)
            .WithTags("Meta")
            .WithSummary("How current the data is: last runs, the incremental watermark, request count and data-quality results.");
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
}
