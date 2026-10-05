-- Method change, 2026-10-04 (docs/metrics.md): bulk closures are counted as recorded and published as notes.
-- BulkClosure stays a dq_flags bit (a label set by usp_classify_for_metrics) but leaves the metric exclusions.

-- 1. is_metric_eligible back to the date problems only. Same drop/re-add as 0011: the persisted column can't be
--    altered in place, so the two indexes that INCLUDE it are rebuilt around it.
--    15 = SentinelDate | FutureDate | InvalidCloseOrder | ClosedMissingDate (DqFlags.MetricExclusions).
DROP INDEX IX_service_request_neighborhood_closed ON dbo.service_request;
DROP INDEX IX_service_request_category_closed ON dbo.service_request;
ALTER TABLE dbo.service_request DROP COLUMN is_metric_eligible;
GO

ALTER TABLE dbo.service_request
    ADD is_metric_eligible AS CAST(CASE WHEN dq_flags & 15 = 0 THEN 1 ELSE 0 END AS bit) PERSISTED;
GO

CREATE INDEX IX_service_request_neighborhood_closed ON dbo.service_request (neighborhood_slug, closed_date_local)
    INCLUDE (category_group, days_to_close, is_metric_eligible, is_service);
CREATE INDEX IX_service_request_category_closed ON dbo.service_request (category_group, closed_date_local)
    INCLUDE (neighborhood_slug, district_number, days_to_close, is_metric_eligible, is_service);
GO

-- 2. Clear-outs leave agg.exclusion (they're no longer excluded) for their own table.
DELETE FROM agg.exclusion WHERE kind = 'bulk_day';
ALTER TABLE agg.exclusion DROP CONSTRAINT CK_exclusion_kind;
ALTER TABLE agg.exclusion ADD CONSTRAINT CK_exclusion_kind CHECK (kind IN ('non_service', 'dq_flag'));
GO

-- One row per clear-out (closed_date_local, category group) since 2024-01-01: the BulkClosure members, their average
-- days to close, the clock minutes from the first to the last of them (1 = all in one minute), and whether any of them
-- closed in a sweep minute shared with other categories (named in sweep_categories, largest first).
CREATE TABLE agg.clear_out
(
    day              date          NOT NULL,
    category_group   nvarchar(100) NOT NULL,
    closed_count     int           NOT NULL,
    avg_age_days     decimal(9, 2) NOT NULL,
    minutes_spanned  int           NOT NULL,
    is_sweep         bit           NOT NULL,
    sweep_categories nvarchar(1000) NULL,
    CONSTRAINT PK_clear_out PRIMARY KEY (day, category_group)
);

CREATE TABLE stg.agg_clear_out
(
    day              date          NOT NULL,
    category_group   nvarchar(100) NOT NULL,
    closed_count     int           NOT NULL,
    avg_age_days     decimal(9, 2) NOT NULL,
    minutes_spanned  int           NOT NULL,
    is_sweep         bit           NOT NULL,
    sweep_categories nvarchar(1000) NULL
);
