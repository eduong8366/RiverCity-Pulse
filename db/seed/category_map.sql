-- CategoryLevel1 -> category_group, keyed by MapKey.For (lowercase alphanumerics), from the 28 values in
-- docs/source-profile.md. Rules:
--   * "Homeless Camp - Primary" merges into "Homeless Camp".
--   * Workflow buckets (Review, Escalation, Web Form) say how a request was routed, not what it is: Process/Unclassified.
--   * Junk singletons and tiny buckets (DCR, CDD-Planning, Community Tag, Homeless Services) go to Other.
-- Blank CategoryLevel1 has no key, so those rows become Unmapped with the UnmappedCategory flag.
SET NOCOUNT ON;

DECLARE @seed TABLE (category_key varchar(255) NOT NULL PRIMARY KEY, source_value nvarchar(255) NOT NULL, category_group nvarchar(100) NOT NULL);

INSERT INTO @seed (category_key, source_value, category_group) VALUES
    ('solidwaste',                N'Solid Waste',                 N'Solid Waste'),
    ('other',                     N'Other',                       N'Other'),
    ('animalcontrol',             N'Animal Control',              N'Animal Control'),
    ('homelesscamp',              N'Homeless Camp',               N'Homeless Camp'),
    ('codeenforcement',           N'Code Enforcement',            N'Code Enforcement'),
    ('homelesscampprimary',       N'Homeless Camp - Primary',     N'Homeless Camp'),
    ('parking',                   N'Parking',                     N'Parking'),
    ('streets',                   N'Streets',                     N'Streets'),
    ('review',                    N'Review',                      N'Process/Unclassified'),
    ('buildingandplanning',       N'Building and Planning',       N'Building and Planning'),
    ('water',                     N'Water',                       N'Water'),
    ('facilities',                N'Facilities',                  N'Facilities'),
    ('urbanforestry',             N'Urban Forestry',              N'Urban Forestry'),
    ('parks',                     N'Parks',                       N'Parks'),
    ('utilitybilling',            N'Utility Billing',             N'Utility Billing'),
    ('sharedrideables',           N'Shared Rideables',            N'Shared Rideables'),
    ('sewer',                     N'Sewer',                       N'Sewer'),
    ('drains',                    N'Drains',                      N'Drains'),
    ('parkrangers',               N'Park Rangers',                N'Park Rangers'),
    ('escalation',                N'Escalation',                  N'Process/Unclassified'),
    ('webform',                   N'Web Form',                    N'Process/Unclassified'),
    ('businessresources',         N'Business Resources',          N'Business Resources'),
    ('measureonoticeanddemand',   N'Measure O notice and Demand', N'Measure O'),
    ('communitytag',              N'Community Tag',               N'Other'),
    ('homelessservices',          N'Homeless Services',           N'Other'),
    ('dcr',                       N'DCR',                         N'Other'),
    ('cddplanning',               N'CDD-Planning',                N'Other');

UPDATE t
SET t.source_value = s.source_value, t.category_group = s.category_group
FROM ref.category_map AS t
JOIN @seed AS s ON s.category_key = t.category_key
WHERE t.source_value <> s.source_value OR t.category_group <> s.category_group;

INSERT INTO ref.category_map (category_key, source_value, category_group)
SELECT s.category_key, s.source_value, s.category_group
FROM @seed AS s
WHERE NOT EXISTS (SELECT 1 FROM ref.category_map AS t WHERE t.category_key = s.category_key);
