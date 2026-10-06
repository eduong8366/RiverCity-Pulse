using Microsoft.Extensions.Logging;
using Sac311.Data.Ingest;
using Sac311.Domain;
using Sac311.Domain.Cleaners;
using Sac311.Ingestion.ArcGis;

namespace Sac311.Ingestion;

/// <summary>What one page did, plus the newest DateUpdated on it (null when every row was rejected).</summary>
public sealed record PageResult(BatchCounts Counts, int Rejected, DateTime? MaxUpdatedUtc);

/// <summary>
/// The per-page step every job shares: <c>raw.page</c> first, then validate and clean each feature, then one
/// transaction through <see cref="PageWriter.ApplyAsync"/> (stage, <c>usp_apply_batch</c>, rejects, checkpoint, run counters).
/// Pages of a <see cref="Pipeline.Reclean"/> run are applied in reclean mode.
/// </summary>
public sealed partial class PageProcessor(PageWriter writer, TimeProvider time, ILogger<PageProcessor> logger)
{
    /// <param name="checkpoint">Saved in the page's transaction; null leaves the checkpoint alone.</param>
    public async Task<PageResult> ProcessAsync(long runId, string pipeline, ArcGisPage page, Checkpoint? checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var nowUtc = time.GetUtcNow().UtcDateTime;
        var rows = new List<CleanedRequest>(page.Features.Count);
        var rejects = new List<IngestReject>();
        DateTime? maxUpdated = null;
        foreach (var feature in page.Features)
        {
            var source = SourceRow.FromFeature(feature);
            if (Record.Validate(source) is { } reason)
            {
                rejects.Add(new IngestReject(source.ObjectId > 0 ? source.ObjectId : null, source.ReferenceNumber, reason, feature.GetRawText()));
            }
            else
            {
                var row = CleanedRequest.From(source, nowUtc);
                rows.Add(row);
                if (maxUpdated is null || row.UpdatedUtc > maxUpdated)
                {
                    maxUpdated = row.UpdatedUtc;
                }
            }
        }

        await writer.SaveRawAsync(
            new RawPage(runId, pipeline, page.Where, page.CursorObjectId, page.Features.Count, (int)page.Elapsed.TotalMilliseconds, page.Payload),
            cancellationToken).ConfigureAwait(false);

        var counts = await writer.ApplyAsync(runId, rows, rejects, checkpoint, page.Features.Count, pipeline == Pipeline.Reclean, cancellationToken).ConfigureAwait(false);
        LogPage(logger, page.LastObjectId, page.Features.Count, counts.Inserted, counts.Updated, counts.Recleaned, counts.Unchanged, rejects.Count, (long)page.Elapsed.TotalMilliseconds);
        return new PageResult(counts, rejects.Count, maxUpdated);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Page to OBJECTID {LastObjectId}: {Fetched} fetched, {Inserted} inserted, {Updated} updated, {Recleaned} recleaned, {Unchanged} unchanged, {Rejected} rejected ({HttpMs} ms)")]
    private static partial void LogPage(ILogger logger, long lastObjectId, int fetched, int inserted, int updated, int recleaned, int unchanged, int rejected, long httpMs);
}
