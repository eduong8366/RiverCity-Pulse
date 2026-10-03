-- Precomputed metrics the API serves as point lookups. The worker computes them (AggregateBuilder) after each run
-- that changed rows, bulk-copies them into the stg.agg_* tables, and usp_refresh_aggregates swaps them in.
-- In every cell, neighborhood_slug '' / district_number 0 / category_group '' mean "all".
CREATE SCHEMA agg AUTHORIZATION dbo;
GO

-- One row per refresh; the newest is what the agg tables hold.
CREATE TABLE agg.refresh
(
    refresh_id    int          NOT NULL IDENTITY CONSTRAINT PK_refresh PRIMARY KEY,
    run_id        bigint       NULL CONSTRAINT FK_refresh_run REFERENCES ops.ingest_run (run_id),
    as_of_utc     datetime2(3) NOT NULL,
    as_of_date    date         NOT NULL,
    request_count int          NOT NULL,
    build_ms      int          NOT NULL,
    refreshed_utc datetime2(3) NOT NULL CONSTRAINT DF_refresh_refreshed DEFAULT SYSUTCDATETIME()
);

-- Per window (30, 90 or 365 days) and period (current = the last N days through as_of_date, prior = the N before).
CREATE TABLE agg.stats_window
(
    window_days       smallint      NOT NULL,
    period            varchar(7)    NOT NULL,
    neighborhood_slug varchar(100)  NOT NULL,
    district_number   tinyint       NOT NULL,
    category_group    nvarchar(100) NOT NULL,
    opened_count      int           NOT NULL,
    closed_count      int           NOT NULL,
    excluded_count    int           NOT NULL,
    median_days       decimal(9, 2) NULL,
    p90_days          decimal(9, 2) NULL,
    CONSTRAINT PK_stats_window PRIMARY KEY (window_days, district_number, category_group, neighborhood_slug, period),
    CONSTRAINT CK_stats_window_period CHECK (period IN ('current', 'prior'))
);

-- Requests open at the refresh time.
CREATE TABLE agg.open_backlog
(
    neighborhood_slug    varchar(100)  NOT NULL,
    district_number      tinyint       NOT NULL,
    category_group       nvarchar(100) NOT NULL,
    open_count           int           NOT NULL,
    median_open_age_days decimal(9, 2) NULL,
    CONSTRAINT PK_open_backlog PRIMARY KEY (district_number, category_group, neighborhood_slug)
);

-- Opened, closed and open-at-day's-end per local day from 2024-01-01 (no neighborhood dimension).
CREATE TABLE agg.backlog_daily
(
    district_number tinyint       NOT NULL,
    category_group  nvarchar(100) NOT NULL,
    day             date          NOT NULL,
    opened_count    int           NOT NULL,
    closed_count    int           NOT NULL,
    open_count      int           NOT NULL,
    CONSTRAINT PK_backlog_daily PRIMARY KEY (district_number, category_group, day)
);
GO

-- Staging copies, loaded by SqlBulkCopy and emptied by usp_refresh_aggregates. The keys catch a duplicate cell.
CREATE TABLE stg.agg_stats_window
(
    window_days       smallint      NOT NULL,
    period            varchar(7)    NOT NULL,
    neighborhood_slug varchar(100)  NOT NULL,
    district_number   tinyint       NOT NULL,
    category_group    nvarchar(100) NOT NULL,
    opened_count      int           NOT NULL,
    closed_count      int           NOT NULL,
    excluded_count    int           NOT NULL,
    median_days       decimal(9, 2) NULL,
    p90_days          decimal(9, 2) NULL,
    CONSTRAINT PK_agg_stats_window PRIMARY KEY (window_days, district_number, category_group, neighborhood_slug, period)
);

CREATE TABLE stg.agg_open_backlog
(
    neighborhood_slug    varchar(100)  NOT NULL,
    district_number      tinyint       NOT NULL,
    category_group       nvarchar(100) NOT NULL,
    open_count           int           NOT NULL,
    median_open_age_days decimal(9, 2) NULL,
    CONSTRAINT PK_agg_open_backlog PRIMARY KEY (district_number, category_group, neighborhood_slug)
);

CREATE TABLE stg.agg_backlog_daily
(
    district_number tinyint       NOT NULL,
    category_group  nvarchar(100) NOT NULL,
    day             date          NOT NULL,
    opened_count    int           NOT NULL,
    closed_count    int           NOT NULL,
    open_count      int           NOT NULL,
    CONSTRAINT PK_agg_backlog_daily PRIMARY KEY (district_number, category_group, day)
);
