-- Metric exclusions (docs/metrics.md): bulk closures and non-service requests. Source values are never changed;
-- usp_classify_for_metrics sets the bulk flag and is_service before each aggregate refresh.

-- 1. BulkClosure (4096) joins the metric exclusions. The persisted column can't be altered in place, so the two
--    indexes that INCLUDE it are dropped and rebuilt around it.
--    4111 = SentinelDate | FutureDate | InvalidCloseOrder | ClosedMissingDate | BulkClosure (DqFlags.MetricExclusions).
DROP INDEX IX_service_request_neighborhood_closed ON dbo.service_request;
DROP INDEX IX_service_request_category_closed ON dbo.service_request;
ALTER TABLE dbo.service_request DROP COLUMN is_metric_eligible;
GO

ALTER TABLE dbo.service_request
    ADD is_metric_eligible AS CAST(CASE WHEN dq_flags & 4111 = 0 THEN 1 ELSE 0 END AS bit) PERSISTED;

-- 2. Whether a request asks for service at all (0: an information call, an inbox item, a referral...). A
--    classification, not a data-quality problem, so it isn't a dq_flags bit.
ALTER TABLE dbo.service_request
    ADD is_service bit NOT NULL CONSTRAINT DF_service_request_is_service DEFAULT 1;
GO

CREATE INDEX IX_service_request_neighborhood_closed ON dbo.service_request (neighborhood_slug, closed_date_local)
    INCLUDE (category_group, days_to_close, is_metric_eligible, is_service);
CREATE INDEX IX_service_request_category_closed ON dbo.service_request (category_group, closed_date_local)
    INCLUDE (neighborhood_slug, district_number, days_to_close, is_metric_eligible, is_service);
GO

-- 3. Non-service categories: a whole CategoryLevel1 (category_map.is_service = 0, with the reason), or one
--    CategoryLevel2 inside a service category (ref.non_service_type). Keys are MapKey.For, like category_map.
ALTER TABLE ref.category_map
    ADD is_service bit NOT NULL CONSTRAINT DF_category_map_is_service DEFAULT 1,
        non_service_reason nvarchar(400) NULL;
GO

ALTER TABLE ref.category_map
    ADD CONSTRAINT CK_category_map_non_service_reason CHECK ((is_service = 1 AND non_service_reason IS NULL) OR (is_service = 0 AND non_service_reason IS NOT NULL));

CREATE TABLE ref.non_service_type
(
    category_key        varchar(255)  NOT NULL,
    category_level2_key varchar(255)  NOT NULL,
    source_level1       nvarchar(255) NOT NULL,
    source_level2       nvarchar(255) NOT NULL,
    reason              nvarchar(400) NOT NULL,
    CONSTRAINT PK_non_service_type PRIMARY KEY (category_key, category_level2_key)
);
GO

-- 4. Bulk closures per period in the window cells, and the exclusion breakdown /api/meta/exclusions serves.
ALTER TABLE agg.stats_window ADD bulk_closed_count int NOT NULL CONSTRAINT DF_stats_window_bulk_closed DEFAULT 0;
ALTER TABLE stg.agg_stats_window ADD bulk_closed_count int NOT NULL CONSTRAINT DF_agg_stats_window_bulk_closed DEFAULT 0;
GO

-- Citywide, per window and period (current, prior; 'now' with window_days 0 for requests open at the as-of time):
--   non_service: one row per non-service type (label = CategoryLevel1, or "CategoryLevel1 / CategoryLevel2");
--   bulk_day:    one row per clear-out (day, category group): closures flagged BulkClosure and their average age;
--   dq_flag:     one row per date-problem flag (label = flag name): closed requests left out of timing for it.
CREATE TABLE agg.exclusion
(
    window_days    smallint      NOT NULL,
    period         varchar(7)    NOT NULL,
    kind           varchar(12)   NOT NULL,
    category_group nvarchar(100) NOT NULL,
    label          nvarchar(520) NOT NULL,
    day            date          NULL,
    reason         nvarchar(400) NULL,
    opened_count   int           NOT NULL,
    closed_count   int           NOT NULL,
    open_count     int           NOT NULL,
    avg_age_days   decimal(9, 2) NULL,
    CONSTRAINT CK_exclusion_period CHECK (period IN ('current', 'prior', 'now')),
    CONSTRAINT CK_exclusion_kind CHECK (kind IN ('non_service', 'bulk_day', 'dq_flag'))
);
CREATE CLUSTERED INDEX CX_exclusion ON agg.exclusion (window_days, period, kind);

CREATE TABLE stg.agg_exclusion
(
    window_days    smallint      NOT NULL,
    period         varchar(7)    NOT NULL,
    kind           varchar(12)   NOT NULL,
    category_group nvarchar(100) NOT NULL,
    label          nvarchar(520) NOT NULL,
    day            date          NULL,
    reason         nvarchar(400) NULL,
    opened_count   int           NOT NULL,
    closed_count   int           NOT NULL,
    open_count     int           NOT NULL,
    avg_age_days   decimal(9, 2) NULL
);
