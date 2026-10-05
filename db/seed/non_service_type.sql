-- CategoryLevel2 lines inside service categories that are not requests for service: information calls, duplicates,
-- after-hours logging and referrals to someone else. Keys are MapKey.For of the source values (a unit test checks).
-- Chosen by hand from the candidate query in docs/metrics.md (100+ requests since 2024, 90%+ of closures within an
-- hour), reviewing each candidate by its name; candidates that name real work stay counted (listed in docs/metrics.md).
-- Speed only finds candidates: a line is listed for what it is, never for how fast it closes.
-- Requests of these types get service_request.is_service = 0 at the next aggregate refresh.
SET NOCOUNT ON;

DECLARE @seed TABLE
(
    category_key        varchar(255)  NOT NULL,
    category_level2_key varchar(255)  NOT NULL,
    source_level1       nvarchar(255) NOT NULL,
    source_level2       nvarchar(255) NOT NULL,
    reason              nvarchar(400) NOT NULL,
    PRIMARY KEY (category_key, category_level2_key)
);

INSERT INTO @seed (category_key, category_level2_key, source_level1, source_level2, reason) VALUES
    ('animalcontrol',       'generalother',            N'Animal Control',        N'General - Other',             N'Information call on another topic, answered during the call.'),
    ('animalcontrol',       'generalduplicaterequest', N'Animal Control',        N'General - Duplicate Request', N'A duplicate of a request already logged; the original is counted.'),
    ('animalcontrol',       'generalafterhourscall',   N'Animal Control',        N'General - After Hours Call',  N'An after-hours call logged for the record, not a request for service.'),
    ('animalcontrol',       'generaladoption',         N'Animal Control',        N'General - Adoption',          N'Information call about adoption.'),
    ('animalcontrol',       'generalspayneuter',       N'Animal Control',        N'General - Spay/ Neuter',      N'Information call about spay and neuter services.'),
    ('animalcontrol',       'generalinvestigation',    N'Animal Control',        N'General - Investigation',     N'Information call about an investigation.'),
    ('animalcontrol',       'generallicense',          N'Animal Control',        N'General - License',           N'Information call about pet licenses.'),
    ('animalcontrol',       'generalfoundanimal',      N'Animal Control',        N'General - Found Animal',      N'Information call about a found animal (pickups are logged as "Found Animal").'),
    ('animalcontrol',       'generalbarking',          N'Animal Control',        N'General - Barking',           N'Information call about barking (complaints are logged as "Complaint - Barking").'),
    ('buildingandplanning', 'general',                 N'Building and Planning', N'General',                     N'Information call, answered during the call.'),
    ('parking',             'general',                 N'Parking',               N'General',                     N'Information call, answered during the call.'),
    ('parks',               'general',                 N'Parks',                 N'General',                     N'Information call, answered during the call.'),
    ('urbanforestry',       'general',                 N'Urban Forestry',        N'General',                     N'Information call, answered during the call.'),
    ('sharedrideables',     'lime',                    N'Shared Rideables',      N'Lime',                        N'Referred to the operator (Lime); the city does not do the work.'),
    ('sharedrideables',     'bird',                    N'Shared Rideables',      N'Bird',                        N'Referred to the operator (Bird); the city does not do the work.');

DELETE t
FROM ref.non_service_type AS t
WHERE NOT EXISTS (SELECT 1 FROM @seed AS s WHERE s.category_key = t.category_key AND s.category_level2_key = t.category_level2_key);

UPDATE t
SET t.source_level1 = s.source_level1, t.source_level2 = s.source_level2, t.reason = s.reason
FROM ref.non_service_type AS t
JOIN @seed AS s ON s.category_key = t.category_key AND s.category_level2_key = t.category_level2_key
WHERE t.source_level1 <> s.source_level1 OR t.source_level2 <> s.source_level2 OR t.reason <> s.reason;

INSERT INTO ref.non_service_type (category_key, category_level2_key, source_level1, source_level2, reason)
SELECT s.category_key, s.category_level2_key, s.source_level1, s.source_level2, s.reason
FROM @seed AS s
WHERE NOT EXISTS (SELECT 1 FROM ref.non_service_type AS t WHERE t.category_key = s.category_key AND t.category_level2_key = s.category_level2_key);
