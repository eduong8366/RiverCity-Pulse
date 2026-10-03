using Sac311.Ingestion;

namespace Sac311.Integration.Tests.Ingestion;

public class WatermarkTests
{
    private static readonly DateTime Previous = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RunStarted = new(2026, 10, 2, 13, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(60);

    [Fact]
    public void Incremental_filter_is_strictly_after_the_watermark_truncated_to_the_second()
    {
        var from = new DateTime(2026, 10, 2, 11, 0, 0, 750, DateTimeKind.Utc);

        Assert.Equal("DateUpdated > TIMESTAMP '2026-10-02 11:00:00'", IncrementalJob.WhereClause(from));
    }

    [Fact]
    public void Next_watermark_is_the_newest_DateUpdated_seen()
    {
        var seen = new DateTime(2026, 10, 2, 12, 40, 0, DateTimeKind.Utc);

        Assert.Equal(seen, IncrementalJob.NextWatermark(Previous, seen, RunStarted));
    }

    [Fact]
    public void A_future_dated_row_caps_the_watermark_at_the_run_start()
    {
        var future = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(RunStarted, IncrementalJob.NextWatermark(Previous, future, RunStarted));
    }

    [Fact]
    public void An_empty_run_keeps_the_watermark()
    {
        Assert.Equal(Previous, IncrementalJob.NextWatermark(Previous, null, RunStarted));
    }

    [Fact]
    public void Rows_only_inside_the_overlap_never_move_the_watermark_back()
    {
        var insideOverlap = Previous - TimeSpan.FromMinutes(30);

        Assert.Equal(Previous, IncrementalJob.NextWatermark(Previous, insideOverlap, RunStarted));
    }

    [Fact]
    public void A_finished_backfill_seeds_its_first_start_less_the_overlap()
    {
        Assert.Equal(RunStarted - Overlap, BackfillJob.SeedWatermark(RunStarted, null, Overlap, null));
    }

    [Fact]
    public void A_backfill_bounded_by_until_seeds_from_until()
    {
        var until = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(until - Overlap, BackfillJob.SeedWatermark(RunStarted, until, Overlap, null));
    }

    [Fact]
    public void A_backfill_never_moves_an_older_watermark_forward()
    {
        // A sliced backfill doesn't cover the gap between an old watermark and its --since.
        var old = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(old, BackfillJob.SeedWatermark(RunStarted, null, Overlap, old));
    }

    [Fact]
    public void A_backfill_moves_a_newer_watermark_back_to_what_it_covered()
    {
        var newer = RunStarted + TimeSpan.FromHours(2);

        Assert.Equal(RunStarted - Overlap, BackfillJob.SeedWatermark(RunStarted, null, Overlap, newer));
    }
}
