-- A reconcile refetches by ReferenceNumber IN (...): 200 quoted references are about 3,600 characters,
-- past the old nvarchar(1000). The column isn't indexed, so max costs nothing.
ALTER TABLE raw.page ALTER COLUMN where_clause nvarchar(max) NOT NULL;
