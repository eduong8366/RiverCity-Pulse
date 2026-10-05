-- Fills stg.agg_clear_out with the clear-out notes for /api/meta/clear-outs (docs/metrics.md); usp_refresh_aggregates
-- publishes it with the other agg tables. Clear-outs are counted in every figure as recorded: this only describes them.
-- One row per (closed_date_local, category group) since @from_date with BulkClosure members (@bulk_flag, set by
-- usp_classify_for_metrics): service requests still in the source, Closed.
--   closed_count, avg_age_days: the members and their average days to close.
--   minutes_spanned:            clock minutes (UTC) from the first member's close to the last's, inclusive (1 = one minute).
--   sweep_categories:           the other category groups with members in the same sweep minutes, most members first.
--                               A sweep minute has at least @sweep_min_count members older than @detect_age_days
--                               (BulkClosureRule's sweep clause) in more than one category group. is_sweep = 1 when set.
CREATE OR ALTER PROCEDURE dbo.usp_build_clear_outs
    @from_date       date,
    @bulk_flag       int,
    @sweep_min_count int,
    @detect_age_days int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    TRUNCATE TABLE stg.agg_clear_out;

    SELECT closed_date_local AS day, category_group, DATEADD(minute, DATEDIFF(minute, 0, closed_utc), 0) AS closed_minute, days_to_close
    INTO #member
    FROM dbo.service_request
    WHERE dq_flags & @bulk_flag <> 0 AND is_service = 1 AND status_group = 'Closed' AND source_removed_utc IS NULL
      AND closed_date_local >= @from_date;

    SELECT closed_minute
    INTO #sweep_minute
    FROM #member
    GROUP BY closed_minute
    HAVING SUM(CASE WHEN days_to_close > @detect_age_days THEN 1 ELSE 0 END) >= @sweep_min_count
       AND COUNT(DISTINCT category_group) > 1;

    -- For each clear-out, the other categories in its sweep minutes and how many members they had there.
    WITH in_sweep AS
    (
        SELECT m.day, m.category_group, m.closed_minute
        FROM #member AS m
        JOIN #sweep_minute AS s ON s.closed_minute = m.closed_minute
        GROUP BY m.day, m.category_group, m.closed_minute
    ),
    per_minute AS
    (
        SELECT m.closed_minute, m.category_group, COUNT(*) AS members
        FROM #member AS m
        JOIN #sweep_minute AS s ON s.closed_minute = m.closed_minute
        GROUP BY m.closed_minute, m.category_group
    ),
    other AS
    (
        SELECT i.day, i.category_group, o.category_group AS other_group, SUM(o.members) AS members
        FROM in_sweep AS i
        JOIN per_minute AS o ON o.closed_minute = i.closed_minute AND o.category_group <> i.category_group
        GROUP BY i.day, i.category_group, o.category_group
    )
    SELECT day, category_group,
           STRING_AGG(CAST(other_group AS nvarchar(max)), N', ') WITHIN GROUP (ORDER BY members DESC, other_group) AS sweep_categories
    INTO #sweep
    FROM other
    GROUP BY day, category_group;

    INSERT INTO stg.agg_clear_out (day, category_group, closed_count, avg_age_days, minutes_spanned, is_sweep, sweep_categories)
    SELECT m.day, m.category_group, COUNT(*), CAST(AVG(m.days_to_close) AS decimal(9, 2)),
           DATEDIFF(minute, MIN(m.closed_minute), MAX(m.closed_minute)) + 1,
           CAST(CASE WHEN MAX(s.sweep_categories) IS NULL THEN 0 ELSE 1 END AS bit), MAX(s.sweep_categories)
    FROM #member AS m
    LEFT JOIN #sweep AS s ON s.day = m.day AND s.category_group = m.category_group
    GROUP BY m.day, m.category_group;
END
