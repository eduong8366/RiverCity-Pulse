-- Lookup tables. Rows come from db/seed/*.sql, which runs on every migrate.

-- Bit values match Sac311.Domain.DqFlags (a unit test checks this).
CREATE TABLE ref.dq_flag
(
    bit_value             int           NOT NULL CONSTRAINT PK_dq_flag PRIMARY KEY,
    name                  varchar(50)   NOT NULL CONSTRAINT UQ_dq_flag_name UNIQUE,
    description           nvarchar(400) NOT NULL,
    excludes_from_metrics bit           NOT NULL,
    CONSTRAINT CK_dq_flag_power_of_two CHECK (bit_value > 0 AND (bit_value & (bit_value - 1)) = 0)
);

-- CategoryLevel1 -> reporting group. category_key is MapKey.For(CategoryLevel1).
CREATE TABLE ref.category_map
(
    category_key   varchar(255)  NOT NULL CONSTRAINT PK_category_map PRIMARY KEY,
    source_value   nvarchar(255) NOT NULL,
    category_group nvarchar(100) NOT NULL
);

-- SourceLevel1 -> intake channel. source_key is MapKey.For(SourceLevel1).
CREATE TABLE ref.source_map
(
    source_key     varchar(100)  NOT NULL CONSTRAINT PK_source_map PRIMARY KEY,
    source_value   nvarchar(100) NOT NULL,
    source_channel nvarchar(50)  NOT NULL
);

-- Any spelling of a neighborhood (keyed by MapKey.For) -> the boundary file's NAME and slug.
CREATE TABLE ref.neighborhood_alias
(
    alias_key         varchar(100)  NOT NULL CONSTRAINT PK_neighborhood_alias PRIMARY KEY,
    neighborhood      nvarchar(100) NOT NULL,
    neighborhood_slug varchar(100)  NOT NULL
);

CREATE INDEX IX_neighborhood_alias_slug ON ref.neighborhood_alias (neighborhood_slug);
