namespace Sac311.Ingestion;

public sealed class IngestOptions
{
    public const string SectionName = "Ingest";

    /// <summary>
    /// How far back each incremental run reaches before its watermark. Rows edited while a run was paging (behind its
    /// OBJECTID cursor) or with a DateUpdated stamped late by the source are picked up by the next run.
    /// </summary>
    public TimeSpan WatermarkOverlap { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>How often the scheduler (<c>worker run</c>) starts an incremental run.</summary>
    public TimeSpan IncrementalInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Incremental raw pages older than this are deleted after each incremental run. Backfill pages are kept.</summary>
    public TimeSpan RawRetention { get; set; } = TimeSpan.FromDays(180);

    /// <summary>When the scheduler runs the daily reconcile, as a Sacramento (America/Los_Angeles) time of day.</summary>
    public TimeSpan ReconcileTimeLocal { get; set; } = new(3, 30, 0);

    /// <summary>
    /// A reconcile aborts, marking nothing, when the source serves fewer keys than this share of the last successful
    /// reconcile's count: a truncated feed must not mark most requests as removed.
    /// </summary>
    public double ReconcileGuardRatio { get; set; } = 0.95;

    /// <summary>Reference numbers per <c>ReferenceNumber IN (...)</c> query when a reconcile fetches requests again.</summary>
    public int ReconcileRefetchBatch { get; set; } = 200;
}
