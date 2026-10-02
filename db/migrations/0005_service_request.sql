-- The cleaned table: one row per ReferenceNumber (the source's natural key).
CREATE TABLE dbo.service_request
(
    service_request_id       bigint         NOT NULL IDENTITY CONSTRAINT PK_service_request PRIMARY KEY,
    reference_number         varchar(50)    NOT NULL,
    object_id                bigint         NOT NULL,
    sf_ticket_id             varchar(25)    NULL,

    category_level1          nvarchar(255)  NULL,
    category_level2          nvarchar(255)  NULL,
    category_name            nvarchar(255)  NULL,
    category_group           nvarchar(100)  NOT NULL,
    source_channel_raw       nvarchar(100)  NULL,
    source_channel           nvarchar(50)   NOT NULL,
    district_number          tinyint        NULL,
    is_city                  bit            NULL,
    neighborhood             nvarchar(100)  NULL,
    neighborhood_slug        varchar(100)   NULL,
    address                  nvarchar(100)  NULL,
    cross_street             nvarchar(50)   NULL,
    zip                      char(5)        NULL,
    latitude                 decimal(9, 6)  NULL,
    longitude                decimal(9, 6)  NULL,
    public_status            varchar(20)    NULL,
    status_group             varchar(10)    NOT NULL,

    created_utc              datetime2(3)   NULL,
    updated_utc              datetime2(3)   NOT NULL,
    closed_utc               datetime2(3)   NULL,
    created_date_local       date           NULL,
    closed_date_local        date           NULL,

    backlog_close_date_local date           NULL,
    days_to_close            decimal(9, 2)  NULL,
    dq_flags                 int            NOT NULL CONSTRAINT DF_service_request_dq_flags DEFAULT 0,
    -- 15 = SentinelDate | FutureDate | InvalidCloseOrder | ClosedMissingDate (DqFlags.MetricExclusions).
    is_metric_eligible       AS CAST(CASE WHEN dq_flags & 15 = 0 THEN 1 ELSE 0 END AS bit) PERSISTED,

    row_hash                 binary(32)     NOT NULL,
    first_seen_utc           datetime2(3)   NOT NULL,
    last_seen_utc            datetime2(3)   NOT NULL,
    last_changed_utc         datetime2(3)   NOT NULL,
    source_removed_utc       datetime2(3)   NULL,

    CONSTRAINT UQ_service_request_reference UNIQUE (reference_number),
    CONSTRAINT CK_service_request_status_group CHECK (status_group IN ('Open', 'Closed', 'Cancelled', 'Unknown'))
);

CREATE INDEX IX_service_request_created ON dbo.service_request (created_date_local);
CREATE INDEX IX_service_request_neighborhood_closed ON dbo.service_request (neighborhood_slug, closed_date_local)
    INCLUDE (category_group, days_to_close, is_metric_eligible);
CREATE INDEX IX_service_request_category_closed ON dbo.service_request (category_group, closed_date_local)
    INCLUDE (neighborhood_slug, district_number, days_to_close, is_metric_eligible);
CREATE INDEX IX_service_request_open ON dbo.service_request (created_date_local)
    INCLUDE (category_group, neighborhood_slug, district_number)
    WHERE status_group = 'Open';
GO

-- One row per distinct version of a request (a new row_hash), so changes can be replayed over time.
CREATE TABLE dbo.service_request_history
(
    history_id        bigint         NOT NULL IDENTITY,
    reference_number  varchar(50)    NOT NULL,
    row_hash          binary(32)     NOT NULL,
    captured_utc      datetime2(3)   NOT NULL,
    run_id            bigint         NULL,
    object_id         bigint         NOT NULL,
    category_level1   nvarchar(255)  NULL,
    category_level2   nvarchar(255)  NULL,
    category_name     nvarchar(255)  NULL,
    category_group    nvarchar(100)  NOT NULL,
    source_channel    nvarchar(50)   NOT NULL,
    district_number   tinyint        NULL,
    is_city           bit            NULL,
    neighborhood_slug varchar(100)   NULL,
    address           nvarchar(100)  NULL,
    zip               char(5)        NULL,
    latitude          decimal(9, 6)  NULL,
    longitude         decimal(9, 6)  NULL,
    public_status     varchar(20)    NULL,
    status_group      varchar(10)    NOT NULL,
    created_utc       datetime2(3)   NULL,
    updated_utc       datetime2(3)   NOT NULL,
    closed_utc        datetime2(3)   NULL,
    dq_flags          int            NOT NULL,
    CONSTRAINT PK_service_request_history PRIMARY KEY (history_id) WITH (DATA_COMPRESSION = PAGE),
    CONSTRAINT UQ_service_request_history_version UNIQUE (reference_number, row_hash) WITH (DATA_COMPRESSION = PAGE)
);
