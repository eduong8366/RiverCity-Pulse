-- One page of cleaned rows at a time; truncated before each page.
-- Same columns as dbo.service_request plus the raw map keys; tracking columns are set by usp_apply_batch.
CREATE TABLE stg.request
(
    reference_number         varchar(50)    NOT NULL,
    object_id                bigint         NOT NULL,
    sf_ticket_id             varchar(25)    NULL,
    category_level1          nvarchar(255)  NULL,
    category_level2          nvarchar(255)  NULL,
    category_name            nvarchar(255)  NULL,
    category_key             varchar(255)   NULL,
    source_channel_raw       nvarchar(100)  NULL,
    source_key               varchar(100)   NULL,
    district_number          tinyint        NULL,
    is_city                  bit            NULL,
    neighborhood             nvarchar(100)  NULL,
    neighborhood_key         varchar(100)   NULL,
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
    dq_flags                 int            NOT NULL,
    row_hash                 binary(32)     NOT NULL
);
GO

-- Every (ReferenceNumber, DateUpdated) pair in the source, loaded by the daily reconcile.
CREATE TABLE stg.reconcile_key
(
    reference_number varchar(50)  NOT NULL CONSTRAINT PK_reconcile_key PRIMARY KEY,
    updated_utc      datetime2(3) NOT NULL
);
