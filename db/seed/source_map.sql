-- SourceLevel1 -> source_channel, keyed by MapKey.For (lowercase alphanumerics), from the 16 values in
-- docs/source-profile.md. "GoogleAI" and "Google AI" share the key googleai. Named web forms collapse to Web Form;
-- council district forms 5-8 aren't in the data yet but follow the same pattern.
-- Blank SourceLevel1 has no key, so those rows become Unmapped with the UnmappedSource flag.
SET NOCOUNT ON;

DECLARE @seed TABLE (source_key varchar(100) NOT NULL PRIMARY KEY, source_value nvarchar(100) NOT NULL, source_channel nvarchar(50) NOT NULL);

INSERT INTO @seed (source_key, source_value, source_channel) VALUES
    ('phone',                                    N'Phone',                                          N'Phone'),
    ('web',                                      N'Web',                                            N'Web'),
    ('email',                                    N'Email',                                          N'Email'),
    ('googleai',                                 N'GoogleAI',                                       N'Google AI'),
    ('publicworksfacilitiesworkorderrequestform', N'Public_Works_Facilities_Work_Order_Request_Form', N'Web Form'),
    ('mayorformcityweb',                         N'Mayor_Form_City_Web',                            N'Web Form'),
    ('mayorformengagesac',                       N'Mayor_Form_EngageSac',                           N'Web Form'),
    ('councildistrict1form',                     N'Council District 1 Form',                        N'Web Form'),
    ('councildistrict2form',                     N'Council District 2 Form',                        N'Web Form'),
    ('councildistrict3form',                     N'Council District 3 Form',                        N'Web Form'),
    ('councildistrict4form',                     N'Council District 4 Form',                        N'Web Form'),
    ('councildistrict5form',                     N'Council District 5 Form',                        N'Web Form'),
    ('councildistrict6form',                     N'Council District 6 Form',                        N'Web Form'),
    ('councildistrict7form',                     N'Council District 7 Form',                        N'Web Form'),
    ('councildistrict8form',                     N'Council District 8 Form',                        N'Web Form'),
    ('dcrab130form',                             N'DCR AB130 Form',                                 N'Web Form'),
    ('archivemobileapp',                         N'Archive Mobile App',                             N'Mobile App'),
    ('mayorsoffice',                             N'Mayor''s Office',                                N'Other');

UPDATE t
SET t.source_value = s.source_value, t.source_channel = s.source_channel
FROM ref.source_map AS t
JOIN @seed AS s ON s.source_key = t.source_key
WHERE t.source_value <> s.source_value OR t.source_channel <> s.source_channel;

INSERT INTO ref.source_map (source_key, source_value, source_channel)
SELECT s.source_key, s.source_value, s.source_channel
FROM @seed AS s
WHERE NOT EXISTS (SELECT 1 FROM ref.source_map AS t WHERE t.source_key = s.source_key);
