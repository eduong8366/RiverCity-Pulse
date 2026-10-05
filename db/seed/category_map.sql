-- CategoryLevel1 -> category_group, keyed by MapKey.For (lowercase alphanumerics), from the 28 values in
-- docs/source-profile.md. Rules:
--   * "Homeless Camp - Primary" merges into "Homeless Camp".
--   * Workflow buckets (Review, Escalation, Web Form) say how a request was routed, not what it is: Process/Unclassified.
--   * Junk singletons and tiny buckets (DCR, CDD-Planning, Community Tag, Homeless Services) go to Other.
--   * Every key in Other and Process/Unclassified is non-service (is_service = 0, with the reason): those requests are
--     left out of headline numbers and reported separately (docs/metrics.md). A seed edit applies at the next refresh.
-- Blank CategoryLevel1 has no key, so those rows become Unmapped with the UnmappedCategory flag (and count as service).
SET NOCOUNT ON;

DECLARE @seed TABLE
(
    category_key       varchar(255)  NOT NULL PRIMARY KEY,
    source_value       nvarchar(255) NOT NULL,
    category_group     nvarchar(100) NOT NULL,
    non_service_reason nvarchar(400) NULL
);

DECLARE @grouped_other nvarchar(400) = N'Too few requests to report on its own, so grouped as Other; the whole Other group is left out of headline numbers.';

INSERT INTO @seed (category_key, source_value, category_group, non_service_reason) VALUES
    ('solidwaste',                N'Solid Waste',                 N'Solid Waste',          NULL),
    ('other',                     N'Other',                       N'Other',                N'Information and referral calls (mostly "Information" and "Non City"), logged and closed during the call.'),
    ('animalcontrol',             N'Animal Control',              N'Animal Control',       NULL),
    ('homelesscamp',              N'Homeless Camp',               N'Homeless Camp',        NULL),
    ('codeenforcement',           N'Code Enforcement',            N'Code Enforcement',     NULL),
    ('homelesscampprimary',       N'Homeless Camp - Primary',     N'Homeless Camp',        NULL),
    ('parking',                   N'Parking',                     N'Parking',              NULL),
    ('streets',                   N'Streets',                     N'Streets',              NULL),
    ('review',                    N'Review',                      N'Process/Unclassified', N'An inbox bucket (mostly "Email Review"): items waiting to be read, not requests for service.'),
    ('buildingandplanning',       N'Building and Planning',       N'Building and Planning', NULL),
    ('water',                     N'Water',                       N'Water',                NULL),
    ('facilities',                N'Facilities',                  N'Facilities',           NULL),
    ('urbanforestry',             N'Urban Forestry',              N'Urban Forestry',       NULL),
    ('parks',                     N'Parks',                       N'Parks',                NULL),
    ('utilitybilling',            N'Utility Billing',             N'Utility Billing',      NULL),
    ('sharedrideables',           N'Shared Rideables',            N'Shared Rideables',     NULL),
    ('sewer',                     N'Sewer',                       N'Sewer',                NULL),
    ('drains',                    N'Drains',                      N'Drains',               NULL),
    ('parkrangers',               N'Park Rangers',                N'Park Rangers',         NULL),
    ('escalation',                N'Escalation',                  N'Process/Unclassified', N'A routing bucket (to a specialist or supervisor): says how a call was handled, not what service was asked for.'),
    ('webform',                   N'Web Form',                    N'Process/Unclassified', N'A form bucket (mostly "Mayor Form"): correspondence, not a request for service.'),
    ('businessresources',         N'Business Resources',          N'Business Resources',   NULL),
    ('measureonoticeanddemand',   N'Measure O notice and Demand', N'Measure O',            NULL),
    ('communitytag',              N'Community Tag',               N'Other',                @grouped_other),
    ('homelessservices',          N'Homeless Services',           N'Other',                @grouped_other),
    ('dcr',                       N'DCR',                         N'Other',                @grouped_other),
    ('cddplanning',               N'CDD-Planning',                N'Other',                @grouped_other);

UPDATE t
SET t.source_value = s.source_value, t.category_group = s.category_group,
    t.is_service = CASE WHEN s.non_service_reason IS NULL THEN 1 ELSE 0 END, t.non_service_reason = s.non_service_reason
FROM ref.category_map AS t
JOIN @seed AS s ON s.category_key = t.category_key
WHERE t.source_value <> s.source_value OR t.category_group <> s.category_group
   OR EXISTS (SELECT t.non_service_reason EXCEPT SELECT s.non_service_reason);

INSERT INTO ref.category_map (category_key, source_value, category_group, is_service, non_service_reason)
SELECT s.category_key, s.source_value, s.category_group, CASE WHEN s.non_service_reason IS NULL THEN 1 ELSE 0 END, s.non_service_reason
FROM @seed AS s
WHERE NOT EXISTS (SELECT 1 FROM ref.category_map AS t WHERE t.category_key = s.category_key);
