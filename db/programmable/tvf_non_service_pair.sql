-- Every (CategoryLevel1, CategoryLevel2) pair in dbo.service_request that is not a request for service, with the
-- type it is reported under and why: a whole CategoryLevel1 (ref.category_map.is_service = 0) or one CategoryLevel2
-- inside it (ref.non_service_type). Pairs are matched by MapKey, so spelling variants classify alike. Runs over the
-- few hundred distinct pairs, not the requests.
CREATE OR ALTER FUNCTION dbo.tvf_non_service_pair ()
RETURNS TABLE
AS
RETURN
    SELECT
        p.category_level1,
        p.category_level2,
        COALESCE(cm.category_group, tm.category_group) AS category_group,
        CASE WHEN cm.category_key IS NOT NULL THEN cm.source_value ELSE t.source_level1 + N' / ' + t.source_level2 END AS label,
        COALESCE(cm.non_service_reason, t.reason) AS reason
    FROM (SELECT DISTINCT category_level1, category_level2 FROM dbo.service_request) AS p
    CROSS APPLY (SELECT dbo.fn_map_key(p.category_level1) AS level1_key, dbo.fn_map_key(p.category_level2) AS level2_key) AS k
    LEFT JOIN ref.category_map AS cm ON cm.category_key = k.level1_key AND cm.is_service = 0
    LEFT JOIN ref.non_service_type AS t ON t.category_key = k.level1_key AND t.category_level2_key = k.level2_key
    LEFT JOIN ref.category_map AS tm ON tm.category_key = t.category_key
    WHERE cm.category_key IS NOT NULL OR t.category_key IS NOT NULL;
