using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sac311.Data.Ingest;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <summary>
/// Fetches rows edited since the watermark (less <see cref="IngestOptions.WatermarkOverlap"/>), keyset paged on
/// OBJECTID through the same <see cref="PageProcessor"/> as the backfill, so a changed row gets one update and one
/// history row and a row inside the overlap counts as unchanged. The watermark moves only when the run succeeds, to
/// the newest DateUpdated seen (capped at the run's start), so a killed run is simply repeated by the next one.
/// </summary>
public sealed partial class IncrementalJob(
    ArcGisClient client, IngestLock ingestLock, RunLog runLog, CheckpointStore checkpoints, PageProcessor processor, PageWriter writer, DqRunner dq, AggregateRefresher aggregates,
    TimeProvider time, IOptions<IngestOptions> options, ILogger<IncrementalJob> logger)
{
    private readonly IngestOptions _options = options.Value;

    public async Task<IngestRun> RunAsync(CancellationToken cancellationToken)
    {
        var held = await ingestLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            LogSkipped(logger);
            return await runLog.SkipAsync(Pipeline.Incremental, cancellationToken).ConfigureAwait(false);
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

        var startedUtc = time.GetUtcNow().UtcDateTime;
        var watermark = (await checkpoints.GetAsync(Pipeline.Incremental, cancellationToken).ConfigureAwait(false))?.WatermarkUtc;
        var fromUtc = watermark - _options.WatermarkOverlap;
        var runId = await runLog.StartAsync(Pipeline.Incremental, fromUtc, null, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Pipeline"] = Pipeline.Incremental });

        if (watermark is null || fromUtc is null)
        {
            const string message = "No incremental watermark yet. Run a backfill first; finishing one sets the watermark.";
            LogNoWatermark(logger, message);
            return await runLog.FinishAsync(runId, RunStatus.Failed, message, CancellationToken.None).ConfigureAwait(false);
        }

        var where = WhereClause(fromUtc.Value);
        LogStarted(logger, runId, watermark.Value, where);

        try
        {
            var contract = await SchemaGuard.CheckAsync(client, logger, cancellationToken).ConfigureAwait(false);
            if (contract.IsDrift)
            {
                return await runLog.FinishAsync(runId, RunStatus.SchemaDrift, "Schema drift: " + contract, CancellationToken.None).ConfigureAwait(false);
            }

            var pages = 0;
            DateTime? maxSeen = null;
            await foreach (var page in client.GetPagesAsync(where, 0, cancellationToken).ConfigureAwait(false))
            {
                using (page)
                {
                    // No checkpoint per page: the watermark moves once, after the last page.
                    var result = await processor.ProcessAsync(runId, Pipeline.Incremental, page, null, cancellationToken).ConfigureAwait(false);
                    if (result.MaxUpdatedUtc is { } m && (maxSeen is null || m > maxSeen))
                    {
                        maxSeen = m;
                    }
                }

                pages++;
            }

            var next = NextWatermark(watermark.Value, maxSeen, startedUtc);
            if (maxSeen > startedUtc)
            {
                LogFutureDate(logger, maxSeen.Value, startedUtc);
            }

            await checkpoints.SaveAsync(new Checkpoint(Pipeline.Incremental, null, next, null), runId, cancellationToken).ConfigureAwait(false);
            await runLog.SetWatermarksAsync(runId, fromUtc, next, cancellationToken).ConfigureAwait(false);

            var pruned = await writer.PruneRawAsync(Pipeline.Incremental, startedUtc - _options.RawRetention, cancellationToken).ConfigureAwait(false);
            if (pruned > 0)
            {
                LogPruned(logger, pruned);
            }

            var run = await runLog.FinishAsync(runId, RunStatus.Succeeded, null, CancellationToken.None).ConfigureAwait(false);
            LogFinished(logger, run.Status, pages, run.RowsFetched, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsRejected, run.RowsHistory, next);
            return run;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            var error = ex is OperationCanceledException ? "Cancelled." : ex.ToString();
            return await runLog.FinishAsync(runId, RunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The ArcGIS filter for rows edited after <paramref name="fromUtc"/> (truncated to the second, so it never misses a row).</summary>
    public static string WhereClause(DateTime fromUtc) => "DateUpdated > " + BackfillJob.Timestamp(fromUtc);

    /// <summary>
    /// The watermark after a successful run: the newest DateUpdated seen, but never past the run's start (a
    /// future-dated row would otherwise push it ahead of real edits) and never backwards.
    /// </summary>
    public static DateTime NextWatermark(DateTime previousUtc, DateTime? maxSeenUtc, DateTime runStartedUtc)
    {
        var candidate = maxSeenUtc ?? previousUtc;
        if (candidate > runStartedUtc)
        {
            candidate = runStartedUtc;
        }

        return candidate > previousUtc ? candidate : previousUtc;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incremental skipped: another ingestion job holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} run(s) left Running by a process that ended early")]
    private static partial void LogAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogNoWatermark(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental run {RunId} started from watermark {WatermarkUtc:u}: {Where}")]
    private static partial void LogStarted(ILogger logger, long runId, DateTime watermarkUtc, string where);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Newest DateUpdated {MaxSeenUtc:u} is after the run started; watermark capped at {RunStartedUtc:u}")]
    private static partial void LogFutureDate(ILogger logger, DateTime maxSeenUtc, DateTime runStartedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} incremental raw page(s) past retention")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Incremental {Status}: {Pages} page(s), {Fetched:N0} fetched, {Inserted:N0} inserted, {Updated:N0} updated, {Unchanged:N0} unchanged, {Rejected:N0} rejected, {History:N0} history rows; watermark now {WatermarkUtc:u}")]
    private static partial void LogFinished(ILogger logger, string status, int pages, int fetched, int inserted, int updated, int unchanged, int rejected, int history, DateTime watermarkUtc);

    [LoggerMessage(Level = LogLevel.Error, Message = "Incremental failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
