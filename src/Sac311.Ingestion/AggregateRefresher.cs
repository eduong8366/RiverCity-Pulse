using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sac311.Data.Aggregates;
using Sac311.Data.Ingest;
using Sac311.Domain.Aggregates;
using Sac311.Domain.Cleaners;

namespace Sac311.Ingestion;

/// <summary>
/// Recomputes the <c>agg</c> tables the API reads: requests are classified first (<see cref="AggregateStore.ClassifyAsync"/>:
/// non-service requests and the clear-out label, docs/metrics.md), then every request still in the source goes through
/// <see cref="AggregateBuilder"/>, and <see cref="AggregateStore.PublishAsync"/> swaps the result in. Runs after each
/// successful ingestion run that changed rows, and at least once per Sacramento day (the windows end "today").
/// </summary>
public sealed partial class AggregateRefresher(AggregateStore store, TimeProvider time, ILogger<AggregateRefresher> logger)
{
    /// <summary>
    /// Refreshes after <paramref name="run"/> when it succeeded and either changed rows or the aggregates are from an
    /// earlier day. A failed refresh is logged, never thrown: the run itself is done, and the next one tries again.
    /// </summary>
    public Task RefreshAfterAsync(IngestRun run, CancellationToken cancellationToken) => RefreshAfterAsync(run, always: false, cancellationToken);

    /// <param name="always">
    /// Refresh after any successful run, changed rows or not. A reclean passes it: a seed edit such as
    /// <c>ref.non_service_type</c> changes no cleaned row and shows only in the classification the refresh runs.
    /// </param>
    public async Task RefreshAfterAsync(IngestRun run, bool always, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Status != RunStatus.Succeeded)
        {
            return;
        }

        try
        {
            var changed = run.RowsInserted + run.RowsUpdated + run.RowsRemoved + run.RowsRestored + run.RowsRecleaned;
            var latest = await store.LatestAsync(cancellationToken).ConfigureAwait(false);
            if (!always && !IsDue(changed, latest?.AsOfDate, Pacific.ToLocalDate(time.GetUtcNow().UtcDateTime)))
            {
                LogUpToDate(logger, latest!.AsOfDate);
                return;
            }

            await RefreshAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogError(logger, ex);
        }
    }

    /// <summary>Whether a run that changed <paramref name="changedRows"/> rows should refresh aggregates last computed for <paramref name="lastAsOfDate"/>.</summary>
    public static bool IsDue(int changedRows, DateOnly? lastAsOfDate, DateOnly todayLocal) =>
        changedRows > 0 || lastAsOfDate is not { } last || last < todayLocal;

    /// <summary>Recomputes and publishes the aggregates now, under <paramref name="runId"/> (null for an ad hoc refresh).</summary>
    public async Task<AggregateRefresh> RefreshAsync(long? runId, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var classification = await store.ClassifyAsync(cancellationToken).ConfigureAwait(false);
        LogClassified(logger, classification.ServiceChanged, classification.BulkChanged, classification.NonServiceRows, classification.BulkRows, classification.ClearOuts,
            classification.SweepMinutes, watch.ElapsedMilliseconds);

        watch.Restart();
        var builder = new AggregateBuilder(time.GetUtcNow().UtcDateTime);
        await foreach (var request in store.ReadRequestsAsync(cancellationToken).ConfigureAwait(false))
        {
            builder.Add(request);
        }

        var set = builder.Build();
        var buildMs = (int)watch.ElapsedMilliseconds;
        await store.PublishAsync(set, runId, buildMs, cancellationToken).ConfigureAwait(false);
        var refresh = await store.LatestAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The refresh was not recorded.");

        LogRefreshed(logger, set.AsOfDate, set.RequestCount, set.Windows.Count, set.Open.Count, set.Backlog.Count, buildMs, watch.ElapsedMilliseconds);
        return refresh;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Classified for metrics: {ServiceChanged:N0} service and {BulkChanged:N0} bulk-closure changes; {NonService:N0} non-service requests, {Bulk:N0} bulk closures in {ClearOuts:N0} clear-out days and {Sweeps:N0} sweep minutes ({ElapsedMs:N0} ms)")]
    private static partial void LogClassified(ILogger logger, int serviceChanged, int bulkChanged, int nonService, int bulk, int clearOuts, int sweeps, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Aggregates up to date (as of {AsOfDate:yyyy-MM-dd}); no rows changed")]
    private static partial void LogUpToDate(ILogger logger, DateOnly asOfDate);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Aggregates refreshed as of {AsOfDate:yyyy-MM-dd}: {Requests:N0} requests, {Windows:N0} window cells, {Open:N0} open cells, {Backlog:N0} backlog days; built in {BuildMs:N0} ms, {TotalMs:N0} ms with publish")]
    private static partial void LogRefreshed(ILogger logger, DateOnly asOfDate, int requests, int windows, int open, int backlog, int buildMs, long totalMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Aggregate refresh failed; the ingestion run itself is unaffected")]
    private static partial void LogError(ILogger logger, Exception ex);
}
