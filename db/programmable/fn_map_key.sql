-- The SQL twin of Sac311.Domain.Cleaners.MapKey.For: ASCII letters and digits only, lowercased; NULL when nothing is
-- left. Lets SQL match raw values to the ref map keys (an integration test compares it with the C#).
CREATE OR ALTER FUNCTION dbo.fn_map_key (@value nvarchar(4000))
RETURNS varchar(255)
WITH SCHEMABINDING
AS
BEGIN
    RETURN NULLIF(CAST(LOWER(REGEXP_REPLACE(@value, '[^A-Za-z0-9]', '')) AS varchar(255)), '');
END
