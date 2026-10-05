-- Classifies requests for the headline metrics and the notes, before every aggregate refresh (docs/metrics.md).
-- Source values are never changed; only service_request.is_service and the BulkClosure bit of dq_flags are, and only
-- on rows whose value changes, so a rerun changes 0 rows.
--   1. is_service = 0 for the non-service pairs (dbo.tvf_non_service_pair), 1 for everything else. Derived from the
--      ref maps each time, so a seed edit applies at the next refresh without a reclean. Non-service requests are left
--      out of the headline figures.
--   2. Clear-outs, a label only (they're counted in every figure as recorded, and published as notes): closures are
--      old when older than @detect_age_days. A (closed_date_local, category_group) with at least @min_count old
--      closures is a clear-out day; a closed minute (UTC) with at least @sweep_min_count old closures across any
--      categories is a sweep. Every closure in a clear-out day or a sweep minute older than @member_age_days gets
--      @bulk_flag. Only service requests still in the source with no date problem (@date_problems) count or get the
--      flag; the bit is cleared everywhere else.
-- The rule values come from Sac311.Domain.BulkClosureRule and DqFlags. usp_apply_batch overwrites dq_flags on a
-- changed row (clearing the bit), and a run that changed rows always refreshes, so this restores it before the
-- aggregates read it. Result set: what changed and the totals after.
CREATE OR ALTER PROCEDURE dbo.usp_classify_for_metrics
    @bulk_flag       int,
    @date_problems   int,
    @min_count       int,
    @sweep_min_count int,
    @detect_age_days int,
    @member_age_days int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @service_changed int, @bulk_changed int, @clear_outs int, @sweeps int;

    SELECT category_level1, category_level2
    INTO #non_service
    FROM dbo.tvf_non_service_pair();

    BEGIN TRANSACTION;

    UPDATE r
    SET r.is_service = c.is_service
    FROM dbo.service_request AS r
    CROSS APPLY
    (
        SELECT CAST(CASE WHEN EXISTS
        (
            SELECT 1 FROM #non_service AS n
            WHERE n.category_level1 = r.category_level1
              AND (n.category_level2 = r.category_level2 OR (n.category_level2 IS NULL AND r.category_level2 IS NULL))
        ) THEN 0 ELSE 1 END AS bit)
    ) AS c (is_service)
    WHERE r.is_service <> c.is_service;
    SET @service_changed = @@ROWCOUNT;

    SELECT closed_date_local, category_group, DATEADD(minute, DATEDIFF(minute, 0, closed_utc), 0) AS closed_minute
    INTO #old
    FROM dbo.service_request
    WHERE is_service = 1 AND source_removed_utc IS NULL AND dq_flags & @date_problems = 0
      AND closed_date_local IS NOT NULL AND closed_utc IS NOT NULL AND days_to_close > @detect_age_days;

    SELECT closed_date_local, category_group
    INTO #clear_out
    FROM #old
    GROUP BY closed_date_local, category_group
    HAVING COUNT(*) >= @min_count;
    SET @clear_outs = @@ROWCOUNT;

    SELECT closed_minute
    INTO #sweep
    FROM #old
    GROUP BY closed_minute
    HAVING COUNT(*) >= @sweep_min_count;
    SET @sweeps = @@ROWCOUNT;

    UPDATE r
    SET r.dq_flags = CASE c.member WHEN 1 THEN r.dq_flags | @bulk_flag ELSE r.dq_flags & ~@bulk_flag END
    FROM dbo.service_request AS r
    CROSS APPLY
    (
        SELECT CASE WHEN r.is_service = 1 AND r.source_removed_utc IS NULL AND r.dq_flags & @date_problems = 0
                     AND r.days_to_close > @member_age_days
                     AND (EXISTS (SELECT 1 FROM #clear_out AS co WHERE co.closed_date_local = r.closed_date_local AND co.category_group = r.category_group)
                          OR EXISTS (SELECT 1 FROM #sweep AS s WHERE s.closed_minute = DATEADD(minute, DATEDIFF(minute, 0, r.closed_utc), 0)))
                    THEN 1 ELSE 0 END
    ) AS c (member)
    WHERE CASE WHEN r.dq_flags & @bulk_flag <> 0 THEN 1 ELSE 0 END <> c.member;
    SET @bulk_changed = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT
        @service_changed AS ServiceChanged,
        @bulk_changed AS BulkChanged,
        @clear_outs AS ClearOuts,
        @sweeps AS SweepMinutes,
        (SELECT COUNT(*) FROM dbo.service_request WHERE is_service = 0) AS NonServiceRows,
        (SELECT COUNT(*) FROM dbo.service_request WHERE dq_flags & @bulk_flag <> 0) AS BulkRows;
END
