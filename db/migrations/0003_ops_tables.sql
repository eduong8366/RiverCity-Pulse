-- One row per job run.
CREATE TABLE ops.ingest_run
(
    run_id             bigint         NOT NULL IDENTITY CONSTRAINT PK_ingest_run PRIMARY KEY,
    pipeline           varchar(20)    NOT NULL,
    status             varchar(20)    NOT NULL,
    started_utc        datetime2(3)   NOT NULL CONSTRAINT DF_ingest_run_started DEFAULT SYSUTCDATETIME(),
    finished_utc       datetime2(3)   NULL,
    duration_ms        int            NULL,
    rows_fetched       int            NOT NULL CONSTRAINT DF_ingest_run_fetched DEFAULT 0,
    rows_inserted      int            NOT NULL CONSTRAINT DF_ingest_run_inserted DEFAULT 0,
    rows_updated       int            NOT NULL CONSTRAINT DF_ingest_run_updated DEFAULT 0,
    rows_unchanged     int            NOT NULL CONSTRAINT DF_ingest_run_unchanged DEFAULT 0,
    rows_rejected      int            NOT NULL CONSTRAINT DF_ingest_run_rejected DEFAULT 0,
    rows_history       int            NOT NULL CONSTRAINT DF_ingest_run_history DEFAULT 0,
    watermark_from_utc datetime2(3)   NULL,
    watermark_to_utc   datetime2(3)   NULL,
    host_name          nvarchar(128)  NULL,
    error              nvarchar(max)  NULL,
    CONSTRAINT CK_ingest_run_pipeline CHECK (pipeline IN ('Backfill', 'Incremental', 'Reconcile', 'Reclean')),
    CONSTRAINT CK_ingest_run_status CHECK (status IN ('Running', 'Succeeded', 'Failed', 'Skipped', 'SchemaDrift'))
);

CREATE INDEX IX_ingest_run_pipeline_started ON ops.ingest_run (pipeline, started_utc DESC) INCLUDE (status);

-- Resume point per pipeline: OBJECTID cursor for backfill, DateUpdated watermark for incremental.
CREATE TABLE ops.ingest_checkpoint
(
    pipeline       varchar(20)  NOT NULL CONSTRAINT PK_ingest_checkpoint PRIMARY KEY,
    last_object_id bigint       NULL,
    watermark_utc  datetime2(3) NULL,
    run_id         bigint       NULL CONSTRAINT FK_ingest_checkpoint_run REFERENCES ops.ingest_run (run_id),
    updated_utc    datetime2(3) NOT NULL CONSTRAINT DF_ingest_checkpoint_updated DEFAULT SYSUTCDATETIME()
);

-- Rows that failed Record.Validate, kept with their raw JSON.
CREATE TABLE ops.ingest_reject
(
    reject_id        bigint        NOT NULL IDENTITY CONSTRAINT PK_ingest_reject PRIMARY KEY,
    run_id           bigint        NOT NULL CONSTRAINT FK_ingest_reject_run REFERENCES ops.ingest_run (run_id),
    object_id        bigint        NULL,
    reference_number varchar(50)   NULL,
    reason           varchar(200)  NOT NULL,
    raw_json         nvarchar(max) NOT NULL,
    rejected_utc     datetime2(3)  NOT NULL CONSTRAINT DF_ingest_reject_rejected DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_ingest_reject_run ON ops.ingest_reject (run_id);

-- Data-quality check results, written after every run.
CREATE TABLE ops.dq_result
(
    dq_result_id   bigint         NOT NULL IDENTITY CONSTRAINT PK_dq_result PRIMARY KEY,
    run_id         bigint         NULL CONSTRAINT FK_dq_result_run REFERENCES ops.ingest_run (run_id),
    check_name     varchar(100)   NOT NULL,
    status         varchar(10)    NOT NULL,
    observed_value decimal(18, 4) NULL,
    baseline_value decimal(18, 4) NULL,
    threshold      decimal(18, 4) NULL,
    message        nvarchar(1000) NULL,
    checked_utc    datetime2(3)   NOT NULL CONSTRAINT DF_dq_result_checked DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_dq_result_status CHECK (status IN ('Pass', 'Warn', 'Fail', 'Info'))
);

CREATE INDEX IX_dq_result_check ON ops.dq_result (check_name, checked_utc DESC);
