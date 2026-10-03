-- What a reconcile found: the source's key count (the next reconcile's 95% guard baseline), and the requests it
-- marked as gone from the source or saw come back.
ALTER TABLE ops.ingest_run ADD
    source_count  int NULL,
    rows_removed  int NOT NULL CONSTRAINT DF_ingest_run_removed DEFAULT 0,
    rows_restored int NOT NULL CONSTRAINT DF_ingest_run_restored DEFAULT 0;
GO

-- Keys are bulk-loaded page by page, so a duplicate ReferenceNumber in the source must not fail the load:
-- no unique key here, and usp_reconcile keeps the newest DateUpdated per request.
DROP TABLE stg.reconcile_key;
GO

CREATE TABLE stg.reconcile_key
(
    reference_number varchar(50)  NOT NULL,
    updated_utc      datetime2(3) NOT NULL
);

CREATE CLUSTERED INDEX CIX_reconcile_key ON stg.reconcile_key (reference_number);
