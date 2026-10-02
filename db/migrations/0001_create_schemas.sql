-- Layers: raw (payloads as fetched), stg (one page at a time), dbo (cleaned + history),
-- ref (seeded lookup tables), ops (run log, checkpoints, rejects, DQ results).
CREATE SCHEMA raw AUTHORIZATION dbo;
GO
CREATE SCHEMA stg AUTHORIZATION dbo;
GO
CREATE SCHEMA ref AUTHORIZATION dbo;
GO
CREATE SCHEMA ops AUTHORIZATION dbo;
GO
