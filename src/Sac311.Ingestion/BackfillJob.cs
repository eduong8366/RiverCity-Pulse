using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sac311.Data.Ingest;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <param name="SinceUtc">Only rows with DateUpdated at or after this; null loads the whole feed.</param>
/// <param name="UntilUtc">Only rows with DateUpdated before this; null is open-ended.</param>
/// <param name="Restart">Ignore an unfinished backfill's checkpoint and start from the first OBJECTID.</param>
/// <param name="MaxPages">Stop after this many pages, leaving the checkpoint for the next run to resume from.</param>
public sealed record BackfillRequest(DateTime? SinceUtc = null, DateTime? UntilUtc = null, bool Restart = false, int? MaxPages = null);

/// <summary>
/// Loads the feed (or a DateUpdated slice of it) by keyset paging on OBJECTID. Each page goes through
/// <see cref="PageProcessor"/>, which advances the checkpoint in the page's transaction, so a crash loses at most the
/// page in flight and replaying it is idempotent. The run holds the ingest lock and is recorded in <c>ops.ingest_run</c>.
/// A finished backfill seeds the incremental watermark (see <see cref="SeedWatermark"/>).
/// </summary>
public sealed partial class BackfillJob(
    ArcGisClient client, IngestLock ingestLock, RunLog runLog, CheckpointStore checkpoints, PageProcessor processor, DqRunner dq,
    TimeProvider time, IOptions<IngestOptions> options, ILogger<BackfillJob> logger)
{
    public async Task<IngestRun> RunAsync(BackfillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var held = await ingestLock.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        if (held is null)
        {
            LogSkipped(logger);
            return await runLog.SkipAsync(Pipeline.Backfill, cancellationToken).ConfigureAwait(false);
        }

        await using (held.ConfigureAwait(false))
        {
            var run = await RunLockedAsync(request, cancellationToken).ConfigureAwait(false);
            await dq.RunAfterAsync(run, cancellationToken).ConfigureAwait(false);
            return run;
        }
    }

    private async Task<IngestRun> RunLockedAsync(BackfillRequest request, CancellationToken cancellationToken)
    {
        var abandoned = await runLog.CloseAbandonedAsync(cancellationToken).ConfigureAwait(false);
        if (abandoned > 0)
        {
            LogAbandoned(logger, abandoned);
        }

        var startedUtc = time.GetUtcNow().UtcDateTime;
        var where = WhereClause(request.SinceUtc, request.UntilUtc);
        var runId = await runLog.StartAsync(Pipeline.Backfill, request.SinceUtc, request.UntilUtc, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Pipeline"] = Pipeline.Backfill });
        LogStarted(logger, runId, where);

        try
        {
            // Schema contract first: on drift nothing is written beyond the run row.
            var contract = await SchemaGuard.CheckAsync(client, logger, cancellationToken).ConfigureAwait(false);
            if (contract.IsDrift)
            {
                return await runLog.FinishAsync(runId, RunStatus.SchemaDrift, "Schema drift: " + contract, CancellationToken.None).ConfigureAwait(false);
            }

            var cursor = 0L;
            // When the backfill first started; a resume keeps it so the incremental watermark covers edits made since then.
            var backfillStartedUtc = startedUtc;
            var existing = await checkpoints.GetAsync(Pipeline.Backfill, cancellationToken).ConfigureAwait(false);
            if (existing is { LastObjectId: { } last } && !request.Restart)
            {
                if (existing.WhereClause != where)
                {
                    var message = $"An unfinished backfill used a different filter ({existing.WhereClause}). Rerun it with the same filter, or pass --restart.";
                    LogFilterMismatch(logger, message);
                    return await runLog.FinishAsync(runId, RunStatus.Failed, message, CancellationToken.None).ConfigureAwait(false);
                }

                cursor = last;
                backfillStartedUtc = existing.WatermarkUtc ?? startedUtc;
                LogResuming(logger, cursor, backfillStartedUtc);
            }

            var expected = await client.CountAsync(string.Create(CultureInfo.InvariantCulture, $"({where}) AND OBJECTID > {cursor}"), cancellationToken)
                .ConfigureAwait(false);
            LogExpected(logger, expected);

            var pages = 0;
            var finished = true;
            await foreach (var page in client.GetPagesAsync(where, cursor, cancellationToken).ConfigureAwait(false))
            {
                using (page)
                {
                    await processor.ProcessAsync(runId, Pipeline.Backfill, page, new Checkpoint(Pipeline.Backfill, page.LastObjectId, backfillStartedUtc, where), cancellationToken)
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
                // Nothing in progress any more; the next backfill starts from the beginning.
                await checkpoints.SaveAsync(new Checkpoint(Pipeline.Backfill, null, null, null), runId, cancellationToken).ConfigureAwait(false);

                var incremental = await checkpoints.GetAsync(Pipeline.Incremental, cancellationToken).ConfigureAwait(false);
                var seed = SeedWatermark(backfillStartedUtc, request.UntilUtc, options.Value.WatermarkOverlap, incremental?.WatermarkUtc);
                await checkpoints.SaveAsync(new Checkpoint(Pipeline.Incremental, null, seed, null), runId, cancellationToken).ConfigureAwait(false);
                LogWatermarkSeeded(logger, seed);
            }
            else
            {
                LogStoppedEarly(logger, pages);
            }

            var run = await runLog.FinishAsync(runId, RunStatus.Succeeded, null, CancellationToken.None).ConfigureAwait(false);
            LogFinished(logger, run.Status, pages, run.RowsFetched, expected, run.RowsInserted, run.RowsUpdated, run.RowsUnchanged, run.RowsRejected, run.RowsHistory);
            return run;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
            var error = ex is OperationCanceledException ? "Cancelled." : ex.ToString();
            return await runLog.FinishAsync(runId, RunStatus.Failed, error, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The ArcGIS filter for a DateUpdated slice (UTC timestamps).</summary>
    public static string WhereClause(DateTime? sinceUtc, DateTime? untilUtc)
    {
        var parts = new List<string>();
        if (sinceUtc is { } s)
        {
            parts.Add($"DateUpdated >= {Timestamp(s)}");
        }

        if (untilUtc is { } u)
        {
            parts.Add($"DateUpdated < {Timestamp(u)}");
        }

        return parts.Count == 0 ? "1=1" : string.Join(" AND ", parts);
    }

    /// <summary>
    /// The incremental watermark after a finished backfill: everything edited before the backfill first started (or
    /// before <paramref name="untilUtc"/>, if earlier) is loaded, so incremental picks up from there, less the overlap.
    /// It never moves an existing watermark forward: a sliced backfill doesn't cover the gap between an old watermark
    /// and its <c>--since</c>, and an earlier watermark only costs a longer, idempotent incremental run.
    /// </summary>
    public static DateTime SeedWatermark(DateTime backfillStartedUtc, DateTime? untilUtc, TimeSpan overlap, DateTime? existingUtc)
    {
        var covered = untilUtc is { } u && u < backfillStartedUtc ? u : backfillStartedUtc;
        var seed = covered - overlap;
        return existingUtc is { } e && e < seed ? e : seed;
    }

    internal static string Timestamp(DateTime utc) => "TIMESTAMP '" + utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backfill skipped: another ingestion job holds the lock")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} run(s) left Running by a process that ended early")]
    private static partial void LogAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfill run {RunId} started: {Where}")]
    private static partial void LogStarted(ILogger logger, long runId, string where);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogFilterMismatch(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming after OBJECTID {Cursor} (backfill first started {BackfillStartedUtc:u})")]
    private static partial void LogResuming(ILogger logger, long cursor, DateTime backfillStartedUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source count for this filter: {Expected:N0}")]
    private static partial void LogExpected(ILogger logger, long expected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incremental watermark set to {WatermarkUtc:u}")]
    private static partial void LogWatermarkSeeded(ILogger logger, DateTime watermarkUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopped after {Pages} page(s) as asked; the next backfill resumes from the checkpoint.")]
    private static partial void LogStoppedEarly(ILogger logger, int pages);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backfill {Status}: {Pages} page(s), {Fetched:N0} fetched (source count {Expected:N0}), {Inserted:N0} inserted, {Updated:N0} updated, {Unchanged:N0} unchanged, {Rejected:N0} rejected, {History:N0} history rows")]
    private static partial void LogFinished(ILogger logger, string status, int pages, int fetched, long expected, int inserted, int updated, int unchanged, int rejected, int history);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backfill failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
