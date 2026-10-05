-- Publishes a refresh: replaces the agg tables with the stg.agg_* rows the worker just bulk-copied (and
-- usp_build_exclusions just wrote), and records the refresh, in one transaction, so the API sees either the old set
-- or the new one. Empties the staging tables after.
-- Result set: the new refresh_id.
CREATE OR ALTER PROCEDURE dbo.usp_refresh_aggregates
    @run_id        bigint = NULL,
    @as_of_utc     datetime2(3),
    @as_of_date    date,
    @request_count int,
    @build_ms      int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @refresh TABLE (refresh_id int NOT NULL);

    BEGIN TRANSACTION;

    TRUNCATE TABLE agg.stats_window;
    INSERT INTO agg.stats_window
        (window_days, period, neighborhood_slug, district_number, category_group,
         opened_count, closed_count, excluded_count, bulk_closed_count, median_days, p90_days)
    SELECT window_days, period, neighborhood_slug, district_number, category_group,
           opened_count, closed_count, excluded_count, bulk_closed_count, median_days, p90_days
    FROM stg.agg_stats_window;

    TRUNCATE TABLE agg.open_backlog;
    INSERT INTO agg.open_backlog (neighborhood_slug, district_number, category_group, open_count, median_open_age_days)
    SELECT neighborhood_slug, district_number, category_group, open_count, median_open_age_days
    FROM stg.agg_open_backlog;

    TRUNCATE TABLE agg.backlog_daily;
    INSERT INTO agg.backlog_daily (district_number, category_group, day, opened_count, closed_count, open_count)
    SELECT district_number, category_group, day, opened_count, closed_count, open_count
    FROM stg.agg_backlog_daily;

    TRUNCATE TABLE agg.exclusion;
    INSERT INTO agg.exclusion
        (window_days, period, kind, category_group, label, day, reason, opened_count, closed_count, open_count, avg_age_days)
    SELECT window_days, period, kind, category_group, label, day, reason, opened_count, closed_count, open_count, avg_age_days
    FROM stg.agg_exclusion;

    INSERT INTO agg.refresh (run_id, as_of_utc, as_of_date, request_count, build_ms)
    OUTPUT inserted.refresh_id INTO @refresh
    VALUES (@run_id, @as_of_utc, @as_of_date, @request_count, @build_ms);

    COMMIT TRANSACTION;

    TRUNCATE TABLE stg.agg_stats_window;
    TRUNCATE TABLE stg.agg_open_backlog;
    TRUNCATE TABLE stg.agg_backlog_daily;
    TRUNCATE TABLE stg.agg_exclusion;

    SELECT refresh_id FROM @refresh;
END
