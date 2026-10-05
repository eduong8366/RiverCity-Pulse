-- Fills stg.agg_exclusion with what the headline numbers leave out, citywide, for /api/meta/exclusions
-- (docs/metrics.md). usp_refresh_aggregates publishes it with the other agg tables. Requests still in the source only.
-- Periods match AggregateBuilder: for each window N in @windows, current = (as-of − N days, as-of], prior = the N
-- days before; "opened" goes by created_date_local, "closed" by backlog_close_date_local on Closed requests (the day
-- the request left the backlog, as for excluded_count).
--   non_service: per type (dbo.tvf_non_service_pair), requests opened and closed per period, and open now
--                (period 'now', window_days 0), as of the end of @as_of_date.
--   dq_flag:     per bit of @date_problems, closed service requests left out of timing for it (one request can have two).
-- Clear-outs aren't exclusions (they're counted as recorded); usp_build_clear_outs writes their notes.
CREATE OR ALTER PROCEDURE dbo.usp_build_exclusions
    @as_of_date    date,
    @windows       varchar(100),
    @date_problems int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    TRUNCATE TABLE stg.agg_exclusion;

    SELECT
        CAST(w.value AS smallint) AS window_days,
        p.period,
        DATEADD(day, -CAST(w.value AS int) * p.periods_back, @as_of_date) AS to_day,
        DATEADD(day, -CAST(w.value AS int) * (p.periods_back + 1), @as_of_date) AS before_day
    INTO #period
    FROM STRING_SPLIT(@windows, ',') AS w
    CROSS JOIN (VALUES ('current', 0), ('prior', 1)) AS p (period, periods_back);

    -- Non-service requests with the type they're reported under.
    SELECT r.created_date_local, r.backlog_close_date_local, r.status_group, n.category_group, n.label, n.reason
    INTO #non_service
    FROM dbo.service_request AS r
    JOIN dbo.tvf_non_service_pair() AS n
        ON n.category_level1 = r.category_level1
       AND (n.category_level2 = r.category_level2 OR (n.category_level2 IS NULL AND r.category_level2 IS NULL))
    WHERE r.is_service = 0 AND r.source_removed_utc IS NULL;

    INSERT INTO stg.agg_exclusion
        (window_days, period, kind, category_group, label, day, reason, opened_count, closed_count, open_count, avg_age_days)
    SELECT
        p.window_days, p.period, 'non_service', n.category_group, n.label, NULL, n.reason,
        SUM(CASE WHEN n.created_date_local > p.before_day AND n.created_date_local <= p.to_day THEN 1 ELSE 0 END),
        SUM(CASE WHEN n.status_group = 'Closed' AND n.backlog_close_date_local > p.before_day AND n.backlog_close_date_local <= p.to_day THEN 1 ELSE 0 END),
        0, NULL
    FROM #non_service AS n
    JOIN #period AS p
        ON (n.created_date_local > p.before_day AND n.created_date_local <= p.to_day)
        OR (n.status_group = 'Closed' AND n.backlog_close_date_local > p.before_day AND n.backlog_close_date_local <= p.to_day)
    GROUP BY p.window_days, p.period, n.category_group, n.label, n.reason;

    INSERT INTO stg.agg_exclusion
        (window_days, period, kind, category_group, label, day, reason, opened_count, closed_count, open_count, avg_age_days)
    SELECT 0, 'now', 'non_service', n.category_group, n.label, NULL, n.reason, 0, 0, COUNT(*), NULL
    FROM #non_service AS n
    WHERE n.created_date_local <= @as_of_date
      AND (n.backlog_close_date_local IS NULL OR n.backlog_close_date_local > @as_of_date)
    GROUP BY n.category_group, n.label, n.reason;

    INSERT INTO stg.agg_exclusion
        (window_days, period, kind, category_group, label, day, reason, opened_count, closed_count, open_count, avg_age_days)
    SELECT p.window_days, p.period, 'dq_flag', N'', f.name, NULL, f.description, 0, COUNT(*), 0, NULL
    FROM dbo.service_request AS r
    JOIN #period AS p ON r.backlog_close_date_local > p.before_day AND r.backlog_close_date_local <= p.to_day
    JOIN ref.dq_flag AS f ON f.bit_value & @date_problems <> 0 AND r.dq_flags & f.bit_value <> 0
    WHERE r.dq_flags & @date_problems <> 0 AND r.is_service = 1 AND r.status_group = 'Closed' AND r.source_removed_utc IS NULL
    GROUP BY p.window_days, p.period, f.name, f.description;
END
