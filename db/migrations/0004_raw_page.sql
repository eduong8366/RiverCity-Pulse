-- Every fetched page, gzip-compressed, so the cleaned tables can be rebuilt (reclean) without the source.
CREATE TABLE raw.page
(
    page_id          bigint          NOT NULL IDENTITY CONSTRAINT PK_page PRIMARY KEY,
    run_id           bigint          NOT NULL CONSTRAINT FK_page_run REFERENCES ops.ingest_run (run_id),
    pipeline         varchar(20)     NOT NULL,
    where_clause     nvarchar(1000)  NOT NULL,
    cursor_object_id bigint          NULL,
    record_count     int             NOT NULL,
    http_ms          int             NOT NULL,
    payload_sha256   binary(32)      NOT NULL,
    payload_gzip     varbinary(max)  NOT NULL,
    fetched_utc      datetime2(3)    NOT NULL CONSTRAINT DF_page_fetched DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_page_run ON raw.page (run_id);
CREATE INDEX IX_page_pipeline_fetched ON raw.page (pipeline, fetched_utc);
