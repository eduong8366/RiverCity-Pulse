using System.Globalization;
using Microsoft.Extensions.Logging;
using Sac311.Data.Ingest;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <param name="Restart">Ignore an unfinished reclean's checkpoint and start from the first OBJECTID.</param>
/// <param name="MaxPages">Stop after this many pages, leaving the checkpoint for the next run to resume from.</param>
public sealed record RecleanRequest(bool Restart = false, int? MaxPages = null);

/// <summary>
/// Puts every request the source still serves through the current cleaners and seeds again. A cleaner or seed change
/// never reaches rows already loaded on its own: the row hash covers raw values only, so an unchanged source row is
/// skipped as unchanged. This job fetches the whole feed again through the backfill path (keyset paging,
/// <see cref="PageProcessor"/>, a checkpoint per page, the ingest lock) with <c>usp_apply_batch</c> in reclean mode, which
/// also rewrites rows whose cleaned values now differ, without history rows, and counts them in
/// <c>ops.ingest_run.rows_recleaned</c>. It fetches rather than replaying <c>raw.page</c>, because incremental pages
/// are pruned after 180 days and a replay would fall back to backfill-time versions. Requests the source no longer
/// serves keep their old cleaning. It ends with an aggregate refresh whether or not rows changed.
/// </summary>
public sealed partial class RecleanJob(
    ArcGisClient client, IngestLock ingestLock, RunLog runLog, CheckpointStore checkpoints, PageProcessor processor, PageWriter writer, DqRunner dq, AggregateRefresher aggregates,
    TimeProvider time, ILogger<RecleanJob> logger)
{
    private const string Where = "1=1";

    public async Task<IngestRun> RunAsync(RecleanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var held = await ingestLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            LogSkipped(logger);
            return await runLog.SkipAsync(Pipeline.Reclean, cancellationToken).ConfigureAwait(false);
        }

        await using (held.ConfigureAwait(false))
        {
            var run = await RunLockedAsync(request, cancellationToken).ConfigureAwait(false);
            // Always refresh: a seed edit may change only the classification, not any cleaned row. Before DQ, as elsewhere.
            await aggregates.RefreshAfterAsync(run, always: true, cancellationToken).ConfigureAwait(false);
            await dq.RunAfterAsync(run, cancellationToken).ConfigureAwait(false);
            return run;
        }
    }

    private async Task<IngestRun> RunLockedAsync(RecleanRequest request, CancellationToken cancellationToken)
    {
        var abandoned = await runLog.CloseAbandonedAsync(cancellationToken).ConfigureAwait(false);
        if (abandoned > 0)
        {
            LogAbandoned(logger, abandoned);
        }

        var startedUtc = time.GetUtcNow().UtcDateTime;
        var runId = await runLog.StartAsync(Pipeline.Reclean, null, null, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Pipeline"] = Pipeline.Reclean });
        LogStarted(logger, runId);

        try
        {
            var contract = await SchemaGuard.CheckAsync(client, logger, cancellationToken).ConfigureAwait(false);
            if (contract.IsDrift)
            {
                return await runLog.FinishAsync(runId, RunStatus.SchemaDrift, "Schema drift: " + contract, CancellationToken.None).ConfigureAwait(false);
            }

            var cursor = 0L;
            // When the reclean first started; a resume keeps it, so the finished reclean keeps its own raw pages.
            var recleanStartedUtc = startedUtc;
            var existing = await checkpoints.GetAsync(Pipeline.Reclean, cancellationToken).ConfigureAwait(false);
            if (existing is { LastObjectId: { } last } && !request.Restart)
            {
                cursor = last;
                recleanStartedUtc = existing.WatermarkUtc ?? startedUtc;
                LogResuming(logger, cursor, recleanStartedUtc);
            }

            var expected = await client.CountAsync(string.Create(CultureInfo.InvariantCulture, $"OBJECTID > {cursor}"), cancellationToken).ConfigureAwait(false);
            LogExpected(logger, expected);

            var pages = 0;
            var finished = true;
            await foreach (var page in client.GetPagesAsync(Where, cursor, cancellationToken).ConfigureAwait(false))
            {
                using (page)
                {
                    await processor.ProcessAsync(runId, Pipeline.Reclean, page, new Checkpoint(Pipeline.Reclean, page.LastObjectId, recleanStartedUtc, Where), cancellationToken)
                        .ConfigureAwait(false);
                }

                if (++pages == request.MaxPages)
                {
                    finished = false;
                    break;
                }
            }

            if (finished)
            {
                await checkpoints.SaveAsync(new Checkpoint(Pipeline.Reclean, null, null, null), runId, cancellationToken).ConfigureAwait(false);
                // The finished reclean's pages replace the previous one's: one full copy of the feed is enough.
                var pruned = await writer.PruneRawAsync(Pipeline.Reclean, recleanStartedUtc, cancellationToken).ConfigureAwait(false);
                LogPruned(logger, pruned);
            }
            else
            {
                LogStoppedEarly(logger, pages);
            }

            var run = await runLog.FinishAsync(runId, RunStatus.Succeeded, null, CancellationToken.None).ConfigureAwait(false);
            LogFinished(logger, run.Status, pages, run.RowsFetched, expected, run.RowsRecleaned, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsRejected, run.RowsHistory);
            return run;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            var error = ex is OperationCanceledException ? "Cancelled." : ex.ToString();
            return await runLog.FinishAsync(runId, RunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reclean skipped: another ingestion job holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} run(s) left Running by a process that ended early")]
    private static partial void LogAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reclean run {RunId} started")]
    private static partial void LogStarted(ILogger logger, long runId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming after OBJECTID {Cursor} (reclean first started {RecleanStartedUtc:u})")]
    private static partial void LogResuming(ILogger logger, long cursor, DateTime recleanStartedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source count: {Expected:N0}")]
    private static partial void LogExpected(ILogger logger, long expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Pruned:N0} raw page(s) from earlier recleans")]
    private static partial void LogPruned(ILogger logger, int pruned);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopped after {Pages} page(s) as asked; the next reclean resumes from the checkpoint.")]
    private static partial void LogStoppedEarly(ILogger logger, int pages);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Reclean {Status}: {Pages} page(s), {Fetched:N0} fetched (source count {Expected:N0}), {Recleaned:N0} recleaned, {Inserted:N0} inserted, {Updated:N0} updated, {Unchanged:N0} unchanged, {Rejected:N0} rejected, {History:N0} history rows")]
    private static partial void LogFinished(ILogger logger, string status, int pages, int fetched, long expected, int recleaned, int inserted, int updated, int unchanged, int rejected, int history);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reclean failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
