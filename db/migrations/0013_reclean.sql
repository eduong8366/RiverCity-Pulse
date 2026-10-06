-- worker reclean: rows whose cleaned values changed under the current cleaners and seeds while the source version
-- (row_hash) stayed the same. Counted apart from rows_updated, which means the source changed.
ALTER TABLE ops.ingest_run ADD
    rows_recleaned int NOT NULL CONSTRAINT DF_ingest_run_recleaned DEFAULT 0;
