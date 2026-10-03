-- Compares dbo.service_request with every (ReferenceNumber, DateUpdated) pair the source serves, loaded into
-- stg.reconcile_key by the daily reconcile (after its 95% guard passed). Requests are never deleted:
--   * present here but not in the source: source_removed_utc is set (once; the first time it was missed);
--   * marked removed but back in the source: source_removed_utc is cleared;
-- and returns the requests to fetch again: in the source but not here, or newer in the source than here.
-- Result sets: 1. Removed, Restored counts; 2. the reference numbers to fetch again.
CREATE OR ALTER PROCEDURE dbo.usp_reconcile
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @removed int, @restored int;

    SELECT reference_number, MAX(updated_utc) AS updated_utc
    INTO #key
    FROM stg.reconcile_key
    GROUP BY reference_number;

    CREATE UNIQUE CLUSTERED INDEX IX_key_reference ON #key (reference_number);

    BEGIN TRANSACTION;

    UPDATE t
    SET t.source_removed_utc = @now
    FROM dbo.service_request AS t
    WHERE t.source_removed_utc IS NULL
      AND NOT EXISTS (SELECT 1 FROM #key AS k WHERE k.reference_number = t.reference_number);
    SET @removed = @@ROWCOUNT;

    UPDATE t
    SET t.source_removed_utc = NULL,
        t.last_seen_utc = @now
    FROM dbo.service_request AS t
    JOIN #key AS k ON k.reference_number = t.reference_number
    WHERE t.source_removed_utc IS NOT NULL;
    SET @restored = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT @removed AS Removed, @restored AS Restored;

    SELECT k.reference_number
    FROM #key AS k
    LEFT JOIN dbo.service_request AS t ON t.reference_number = k.reference_number
    WHERE t.reference_number IS NULL OR k.updated_utc > t.updated_utc
    ORDER BY k.reference_number;
END
