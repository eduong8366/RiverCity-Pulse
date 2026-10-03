-- Applies the page in stg.request to dbo.service_request and its history, and returns what it did.
-- The caller runs it in the same transaction as the staging load and the checkpoint update, so a page is applied
-- entirely or not at all, and replaying a page changes nothing.
-- Separate UPDATE and INSERT statements instead of MERGE: MERGE has a history of bugs, the applock already makes
-- this the only writer, and separate statements give exact counts (see docs/tradeoffs.md).
CREATE OR ALTER PROCEDURE dbo.usp_apply_batch
    @run_id bigint
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @now datetime2(3) = SYSUTCDATETIME();
    DECLARE @staged int, @inserted int, @updated int, @unchanged int, @history int;

    -- 1. One row per request (the newest version wins), with the map tables resolved. Unmatched category and
    --    source values become 'Unmapped' with UnmappedCategory (128) / UnmappedSource (256). A neighborhood alias
    --    swaps in the boundary file's NAME and slug; an unknown neighborhood keeps its cleaned spelling.
    SELECT
        s.reference_number, s.object_id, s.sf_ticket_id,
        s.category_level1, s.category_level2, s.category_name,
        ISNULL(cm.category_group, N'Unmapped') AS category_group,
        s.source_channel_raw,
        ISNULL(sm.source_channel, N'Unmapped') AS source_channel,
        s.district_number, s.is_city,
        ISNULL(na.neighborhood, s.neighborhood) AS neighborhood,
        ISNULL(na.neighborhood_slug, s.neighborhood_slug) AS neighborhood_slug,
        s.address, s.cross_street, s.zip, s.latitude, s.longitude,
        s.public_status, s.status_group,
        s.created_utc, s.updated_utc, s.closed_utc, s.created_date_local, s.closed_date_local,
        s.backlog_close_date_local, s.days_to_close,
        s.dq_flags
            | CASE WHEN cm.category_key IS NULL THEN 128 ELSE 0 END
            | CASE WHEN sm.source_key IS NULL THEN 256 ELSE 0 END AS dq_flags,
        s.row_hash
    INTO #batch
    FROM
    (
        SELECT r.*, ROW_NUMBER() OVER (PARTITION BY r.reference_number ORDER BY r.updated_utc DESC, r.object_id DESC) AS rn
        FROM stg.request AS r
    ) AS s
    LEFT JOIN ref.category_map AS cm ON cm.category_key = s.category_key
    LEFT JOIN ref.source_map AS sm ON sm.source_key = s.source_key
    LEFT JOIN ref.neighborhood_alias AS na ON na.alias_key = s.neighborhood_key
    WHERE s.rn = 1;

    SET @staged = (SELECT COUNT(*) FROM stg.request);
    CREATE UNIQUE CLUSTERED INDEX IX_batch_reference ON #batch (reference_number);

    BEGIN TRANSACTION;

    -- 2. History: one row per distinct version (a row_hash not seen before), a request's first version included.
    INSERT INTO dbo.service_request_history
        (reference_number, row_hash, captured_utc, run_id, object_id,
         category_level1, category_level2, category_name, category_group, source_channel,
         district_number, is_city, neighborhood_slug, address, zip, latitude, longitude,
         public_status, status_group, created_utc, updated_utc, closed_utc, dq_flags)
    SELECT
        b.reference_number, b.row_hash, @now, @run_id, b.object_id,
        b.category_level1, b.category_level2, b.category_name, b.category_group, b.source_channel,
        b.district_number, b.is_city, b.neighborhood_slug, b.address, b.zip, b.latitude, b.longitude,
        b.public_status, b.status_group, b.created_utc, b.updated_utc, b.closed_utc, b.dq_flags
    FROM #batch AS b
    WHERE NOT EXISTS
    (
        SELECT 1 FROM dbo.service_request_history AS h
        WHERE h.reference_number = b.reference_number AND h.row_hash = b.row_hash
    );
    SET @history = @@ROWCOUNT;

    -- 3. Unchanged (same hash) or stale (older than the stored version): only last_seen moves. A request seen in
    --    the feed is no longer removed, so source_removed_utc clears.
    UPDATE t
    SET t.last_seen_utc = @now,
        t.source_removed_utc = NULL
    FROM dbo.service_request AS t
    JOIN #batch AS b ON b.reference_number = t.reference_number
    WHERE t.row_hash = b.row_hash OR b.updated_utc < t.updated_utc;
    SET @unchanged = @@ROWCOUNT;

    -- 4. Changed: a new hash at least as new as the stored version.
    UPDATE t
    SET t.object_id = b.object_id,
        t.sf_ticket_id = b.sf_ticket_id,
        t.category_level1 = b.category_level1,
        t.category_level2 = b.category_level2,
        t.category_name = b.category_name,
        t.category_group = b.category_group,
        t.source_channel_raw = b.source_channel_raw,
        t.source_channel = b.source_channel,
        t.district_number = b.district_number,
        t.is_city = b.is_city,
        t.neighborhood = b.neighborhood,
        t.neighborhood_slug = b.neighborhood_slug,
        t.address = b.address,
        t.cross_street = b.cross_street,
        t.zip = b.zip,
        t.latitude = b.latitude,
        t.longitude = b.longitude,
        t.public_status = b.public_status,
        t.status_group = b.status_group,
        t.created_utc = b.created_utc,
        t.updated_utc = b.updated_utc,
        t.closed_utc = b.closed_utc,
        t.created_date_local = b.created_date_local,
        t.closed_date_local = b.closed_date_local,
        t.backlog_close_date_local = b.backlog_close_date_local,
        t.days_to_close = b.days_to_close,
        t.dq_flags = b.dq_flags,
        t.row_hash = b.row_hash,
        t.last_seen_utc = @now,
        t.last_changed_utc = @now,
        t.source_removed_utc = NULL
    FROM dbo.service_request AS t
    JOIN #batch AS b ON b.reference_number = t.reference_number
    WHERE t.row_hash <> b.row_hash AND b.updated_utc >= t.updated_utc;
    SET @updated = @@ROWCOUNT;

    -- 5. New requests.
    INSERT INTO dbo.service_request
        (reference_number, object_id, sf_ticket_id,
         category_level1, category_level2, category_name, category_group, source_channel_raw, source_channel,
         district_number, is_city, neighborhood, neighborhood_slug, address, cross_street, zip, latitude, longitude,
         public_status, status_group, created_utc, updated_utc, closed_utc, created_date_local, closed_date_local,
         backlog_close_date_local, days_to_close, dq_flags, row_hash, first_seen_utc, last_seen_utc, last_changed_utc)
    SELECT
        b.reference_number, b.object_id, b.sf_ticket_id,
        b.category_level1, b.category_level2, b.category_name, b.category_group, b.source_channel_raw, b.source_channel,
        b.district_number, b.is_city, b.neighborhood, b.neighborhood_slug, b.address, b.cross_street, b.zip, b.latitude, b.longitude,
        b.public_status, b.status_group, b.created_utc, b.updated_utc, b.closed_utc, b.created_date_local, b.closed_date_local,
        b.backlog_close_date_local, b.days_to_close, b.dq_flags, b.row_hash, @now, @now, @now
    FROM #batch AS b
    WHERE NOT EXISTS (SELECT 1 FROM dbo.service_request AS t WHERE t.reference_number = b.reference_number);
    SET @inserted = @@ROWCOUNT;

    COMMIT TRANSACTION;

    SELECT
        @inserted AS Inserted,
        @updated AS Updated,
        @unchanged AS Unchanged,
        @history AS History,
        @staged - (SELECT COUNT(*) FROM #batch) AS Duplicates;
END
