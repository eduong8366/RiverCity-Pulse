-- One row per Sac311.Domain.DqFlags member (bit values must match; a unit test checks this).
-- excludes_from_metrics = 1 rows make up DqFlags.MetricExclusions, used by service_request.is_metric_eligible.
SET NOCOUNT ON;

DECLARE @seed TABLE (bit_value int NOT NULL PRIMARY KEY, name varchar(50) NOT NULL, description nvarchar(400) NOT NULL, excludes_from_metrics bit NOT NULL);

INSERT INTO @seed (bit_value, name, description, excludes_from_metrics) VALUES
    (1,    'SentinelDate',              N'A date before 2000 (e.g. the 1899-12-30 field default) was set to NULL.', 1),
    (2,    'FutureDate',                N'A date more than one day after the time it was cleaned.', 1),
    (4,    'InvalidCloseOrder',         N'DateClosed is earlier than DateCreated.', 1),
    (8,    'ClosedMissingDate',         N'PublicStatus is CLOSED but DateClosed is empty.', 1),
    (16,   'AddressJunk',               N'Address was a placeholder (N/A, TBD, OK, ZOOM, NONE, UNKNOWN, bare digits or under 3 characters) and was set to NULL.', 0),
    (32,   'NeighborhoodMissingInCity', N'A row inside the city (a council district number) has no neighborhood.', 0),
    (64,   'GeoOutOfBounds',            N'The point is outside the Sacramento bounding box and was set to NULL.', 0),
    (128,  'UnmappedCategory',          N'CategoryLevel1 has no row in ref.category_map; category_group is Unmapped.', 0),
    (256,  'UnmappedSource',            N'SourceLevel1 has no row in ref.source_map; source_channel is Unmapped.', 0),
    (512,  'UnknownStatus',             N'PublicStatus is not one of NEW, IN PROGRESS, CLOSED or CANCELLED.', 0),
    (1024, 'UnknownDistrict',           N'CouncilDistrictNumber is neither "District 1" to "District 8" nor "Non City".', 0),
    (2048, 'InvalidZip',                N'ZIP is not a 5-digit or ZIP+4 code and was set to NULL.', 0);

UPDATE t
SET t.name = s.name, t.description = s.description, t.excludes_from_metrics = s.excludes_from_metrics
FROM ref.dq_flag AS t
JOIN @seed AS s ON s.bit_value = t.bit_value
WHERE t.name <> s.name OR t.description <> s.description OR t.excludes_from_metrics <> s.excludes_from_metrics;

INSERT INTO ref.dq_flag (bit_value, name, description, excludes_from_metrics)
SELECT s.bit_value, s.name, s.description, s.excludes_from_metrics
FROM @seed AS s
WHERE NOT EXISTS (SELECT 1 FROM ref.dq_flag AS t WHERE t.bit_value = s.bit_value);
