using System.Globalization;
using Microsoft.Extensions.Logging;
using Sac311.Data.Ingest;
using Sac311.Domain;
using Sac311.Domain.Cleaners;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <param name="SinceUtc">Only rows with DateUpdated at or after this; null loads the whole feed.</param>
/// <param name="UntilUtc">Only rows with DateUpdated before this; null is open-ended.</param>
/// <param name="Restart">Ignore an unfinished backfill's checkpoint and start from the first OBJECTID.</param>
/// <param name="MaxPages">Stop after this many pages, leaving the checkpoint for the next run to resume from.</param>
public sealed record BackfillRequest(DateTime? SinceUtc = null, DateTime? UntilUtc = null, bool Restart = false, int? MaxPages = null);

/// <summary>
/// Loads the feed (or a DateUpdated slice of it) by keyset paging on OBJECTID. Each page goes through
/// fetch → <c>raw.page</c> → clean → one transaction (stage, <c>usp_apply_batch</c>, rejects, checkpoint, run counters),
/// so a crash loses at most the page in flight and replaying it is idempotent. The run is recorded in <c>ops.ingest_run</c>.
/// </summary>
public sealed partial class BackfillJob(
    ArcGisClient client, RunLog runLog, CheckpointStore checkpoints, PageWriter writer, TimeProvider time, ILogger<BackfillJob> logger)
{
    public async Task<IngestRun> RunAsync(BackfillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var where = WhereClause(request.SinceUtc, request.UntilUtc);
        var runId = await runLog.StartAsync(Pipeline.Backfill, request.SinceUtc, request.UntilUtc, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = runId, ["Pipeline"] = Pipeline.Backfill });
        LogStarted(logger, runId, where);

        try
        {
            // Schema contract first: on drift nothing is written beyond the run row.
            using (var layer = await client.GetLayerAsync(cancellationToken).ConfigureAwait(false))
            {
                var contract = SchemaContract.Check(layer.RootElement);
                if (contract.IsDrift)
                {
                    LogSchemaDrift(logger, contract);
                    return await runLog.FinishAsync(runId, RunStatus.SchemaDrift, "Schema drift: " + contract, CancellationToken.None).ConfigureAwait(false);
                }

                if (contract.Added.Count > 0)
                {
                    LogAddedFields(logger, string.Join(", ", contract.Added));
                }
            }

            var cursor = 0L;
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
                LogResuming(logger, cursor);
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
                    await ProcessPageAsync(runId, where, page, cancellationToken).ConfigureAwait(false);
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

    private async Task ProcessPageAsync(long runId, string where, ArcGisPage page, CancellationToken cancellationToken)
    {
        var nowUtc = time.GetUtcNow().UtcDateTime;
        var rows = new List<CleanedRequest>(page.Features.Count);
        var rejects = new List<IngestReject>();
        foreach (var feature in page.Features)
        {
            var source = SourceRow.FromFeature(feature);
            if (Record.Validate(source) is { } reason)
            {
                rejects.Add(new IngestReject(source.ObjectId > 0 ? source.ObjectId : null, source.ReferenceNumber, reason, feature.GetRawText()));
            }
            else
            {
                rows.Add(CleanedRequest.From(source, nowUtc));
            }
        }

        await writer.SaveRawAsync(
            new RawPage(runId, Pipeline.Backfill, page.Where, page.CursorObjectId, page.Features.Count, (int)page.Elapsed.TotalMilliseconds, page.Payload),
            cancellationToken).ConfigureAwait(false);

        var counts = await writer.ApplyAsync(runId, rows, rejects, new Checkpoint(Pipeline.Backfill, page.LastObjectId, null, where), page.Features.Count, cancellationToken)
            .ConfigureAwait(false);
        LogPage(logger, page.LastObjectId, page.Features.Count, counts.Inserted, counts.Updated, counts.Unchanged, rejects.Count, (long)page.Elapsed.TotalMilliseconds);
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

    private static string Timestamp(DateTime utc) => "TIMESTAMP '" + utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'";

    [LoggerMessage(Level = LogLevel.Information, Message = "Backfill run {RunId} started: {Where}")]
    private static partial void LogStarted(ILogger logger, long runId, string where);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Schema drift, stopping without writing: {Contract}")]
    private static partial void LogSchemaDrift(ILogger logger, SchemaCheckResult contract);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The layer has fields the contract doesn't know: {Fields}")]
    private static partial void LogAddedFields(ILogger logger, string fields);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    private static partial void LogFilterMismatch(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming after OBJECTID {Cursor}")]
    private static partial void LogResuming(ILogger logger, long cursor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Source count for this filter: {Expected:N0}")]
    private static partial void LogExpected(ILogger logger, long expected);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Page to OBJECTID {LastObjectId}: {Fetched} fetched, {Inserted} inserted, {Updated} updated, {Unchanged} unchanged, {Rejected} rejected ({HttpMs} ms)")]
    private static partial void LogPage(ILogger logger, long lastObjectId, int fetched, int inserted, int updated, int unchanged, int rejected, long httpMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopped after {Pages} page(s) as asked; the next backfill resumes from the checkpoint.")]
    private static partial void LogStoppedEarly(ILogger logger, int pages);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Backfill {Status}: {Pages} page(s), {Fetched:N0} fetched (source count {Expected:N0}), {Inserted:N0} inserted, {Updated:N0} updated, {Unchanged:N0} unchanged, {Rejected:N0} rejected, {History:N0} history rows")]
    private static partial void LogFinished(ILogger logger, string status, int pages, int fetched, long expected, int inserted, int updated, int unchanged, int rejected, int history);

    [LoggerMessage(Level = LogLevel.Error, Message = "Backfill failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
