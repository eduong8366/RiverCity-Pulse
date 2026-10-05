using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sac311.Data.Ingest;
using Sac311.Domain;
using Sac311.Domain.Cleaners;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <summary>
/// The daily reconcile. Pages through every (ReferenceNumber, DateUpdated) pair the source serves into
/// <c>stg.reconcile_key</c>, then <c>usp_reconcile</c> marks requests gone from the source with
/// <c>source_removed_utc</c> (never deleting them), clears it for requests that came back, and lists the requests that
/// are missing here or newer in the source; those are fetched again through <see cref="PageProcessor"/>.
/// <para>
/// Guard: when the key count falls below <see cref="IngestOptions.ReconcileGuardRatio"/> of the last successful
/// reconcile's, the run fails before marking anything, since a truncated feed would otherwise mark most requests removed.
/// </para>
/// </summary>
public sealed partial class ReconcileJob(
    ArcGisClient client, IngestLock ingestLock, RunLog runLog, ReconcileStore store, PageProcessor processor, DqRunner dq, AggregateRefresher aggregates,
    IOptions<IngestOptions> options, ILogger<ReconcileJob> logger)
{
    private readonly IngestOptions _options = options.Value;

    public async Task<IngestRun> RunAsync(CancellationToken cancellationToken)
    {
        var held = await ingestLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            LogSkipped(logger);
            return await runLog.SkipAsync(Pipeline.Reconcile, cancellationToken).ConfigureAwait(false);
        }

        await using (held.ConfigureAwait(false))
        {
            var run = await RunLockedAsync(cancellationToken).ConfigureAwait(false);
            // Refresh before DQ: the refresh sets the bulk-closure flags that DQ counts.
            await aggregates.RefreshAfterAsync(run, cancellationToken).ConfigureAwait(false);
            await dq.RunAfterAsync(run, cancellationToken).ConfigureAwait(false);
            return run;
        }
    }

    private async Task<IngestRun> RunLockedAsync(CancellationToken cancellationToken)
    {
        var abandoned = await runLog.CloseAbandonedAsync(cancellationToken).ConfigureAwait(false);
        if (abandoned > 0)
        {
            LogAbandoned(logger, abandoned);
        }

        var runId = await runLog.StartAsync(Pipeline.Reconcile, null, null, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Pipeline"] = Pipeline.Reconcile });
        LogStarted(logger, runId);

        try
        {
            var contract = await SchemaGuard.CheckAsync(client, logger, cancellationToken).ConfigureAwait(false);
            if (contract.IsDrift)
            {
                return await runLog.FinishAsync(runId, RunStatus.SchemaDrift, "Schema drift: " + contract, CancellationToken.None).ConfigureAwait(false);
            }

            var keys = await LoadKeysAsync(cancellationToken).ConfigureAwait(false);
            var baseline = await store.GetGuardBaselineAsync(cancellationToken).ConfigureAwait(false);
            if (GuardFails(keys, baseline, _options.ReconcileGuardRatio))
            {
                var message = string.Create(CultureInfo.InvariantCulture,
                    $"Reconcile guard: the source served {keys:N0} keys, below {_options.ReconcileGuardRatio:P0} of the baseline {baseline:N0}. Nothing was marked.");
                LogGuard(logger, message);
                await runLog.SetReconcileCountsAsync(runId, keys, 0, 0, CancellationToken.None).ConfigureAwait(false);
                return await runLog.FinishAsync(runId, RunStatus.Failed, message, CancellationToken.None).ConfigureAwait(false);
            }

            var result = await store.ApplyAsync(cancellationToken).ConfigureAwait(false);
            await runLog.SetReconcileCountsAsync(runId, keys, result.Removed, result.Restored, cancellationToken).ConfigureAwait(false);
            LogMarked(logger, keys, result.Removed, result.Restored, result.Refetch.Count);

            foreach (var batch in result.Refetch.Chunk(_options.ReconcileRefetchBatch))
            {
                await foreach (var page in client.GetPagesAsync(InClause(batch), 0, cancellationToken).ConfigureAwait(false))
                {
                    using (page)
                    {
                        await processor.ProcessAsync(runId, Pipeline.Reconcile, page, null, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var run = await runLog.FinishAsync(runId, RunStatus.Succeeded, null, CancellationToken.None).ConfigureAwait(false);
            LogFinished(logger, run.Status, keys, run.RowsRemoved, run.RowsRestored, run.RowsFetched, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged);
            return run;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            var error = ex is OperationCanceledException ? "Cancelled." : ex.ToString();
            return await runLog.FinishAsync(runId, RunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Loads every source key into <c>stg.reconcile_key</c>, page by page. Returns the number of keys.</summary>
    private async Task<int> LoadKeysAsync(CancellationToken cancellationToken)
    {
        await store.ClearKeysAsync(cancellationToken).ConfigureAwait(false);
        var total = 0;
        await foreach (var page in client.GetPagesAsync("1=1", 0, "ReferenceNumber,DateUpdated", returnGeometry: false, cancellationToken).ConfigureAwait(false))
        {
            using (page)
            {
                var keys = new List<SourceKey>(page.Features.Count);
                foreach (var feature in page.Features)
                {
                    // Rows the pipeline would reject are not keys: they never made it into the cleaned table either.
                    var row = SourceRow.FromFeature(feature);
                    if (Record.Validate(row) is null)
                    {
                        var updated = DateTimeOffset.FromUnixTimeMilliseconds(row.DateUpdated!.Value).UtcDateTime;
                        keys.Add(new SourceKey(Text.NullIfBlank(row.ReferenceNumber)!, updated));
                    }
                }

                await store.AddKeysAsync(keys, cancellationToken).ConfigureAwait(false);
                total += keys.Count;
            }
        }

        return total;
    }

    /// <summary>True when <paramref name="keys"/> is below <paramref name="ratio"/> of a known baseline.</summary>
    public static bool GuardFails(int keys, int? baseline, double ratio) => baseline is { } b && keys < b * ratio;

    /// <summary>The filter for fetching the given requests again, quotes escaped.</summary>
    public static string InClause(IEnumerable<string> referenceNumbers) =>
        "ReferenceNumber IN (" + string.Join(",", referenceNumbers.Select(r => "'" + r.Replace("'", "''", StringComparison.Ordinal) + "'")) + ")";

    /// <summary>
    /// The first time after <paramref name="nowUtc"/> when the Sacramento clock reads <paramref name="localTime"/>,
    /// as UTC. DST-safe: a local time that doesn't exist that day (spring forward) moves an hour later.
    /// </summary>
    public static DateTime NextRunUtc(DateTime nowUtc, TimeSpan localTime)
    {
        var today = Pacific.ToLocal(nowUtc).Date;
        for (var day = today; ; day = day.AddDays(1))
        {
            var local = DateTime.SpecifyKind(day + localTime, DateTimeKind.Unspecified);
            if (Pacific.Zone.IsInvalidTime(local))
            {
                local = local.AddHours(1);
            }

            var utc = TimeZoneInfo.ConvertTimeToUtc(local, Pacific.Zone);
            if (utc > nowUtc)
            {
                return utc;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconcile skipped: another ingestion job holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} run(s) left Running by a process that ended early")]
    private static partial void LogAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconcile run {RunId} started")]
    private static partial void LogStarted(ILogger logger, long runId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogGuard(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Source serves {Keys:N0} keys: {Removed:N0} marked removed, {Restored:N0} restored, {Refetch:N0} to fetch again")]
    private static partial void LogMarked(ILogger logger, int keys, int removed, int restored, int refetch);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reconcile {Status}: {Keys:N0} keys, {Removed:N0} removed, {Restored:N0} restored; fetched again {Fetched:N0}: {Inserted:N0} inserted, {Updated:N0} updated, {Unchanged:N0} unchanged")]
    private static partial void LogFinished(ILogger logger, string status, int keys, int removed, int restored, int fetched, int inserted, int updated, int unchanged);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconcile failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
