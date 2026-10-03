-- The backfill's base filter (before the OBJECTID cursor), so a resume only continues a run with the same filter.
ALTER TABLE ops.ingest_checkpoint ADD where_clause nvarchar(1000) NULL;
