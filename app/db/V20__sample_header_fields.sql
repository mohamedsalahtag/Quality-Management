-- ===================================================================
-- V20  Configurable sample header fields (global, like Reading Types
--      and Defect Catalog but never scoped to a material group).
--
-- Until now the user-input identification fields on qms_sample were
-- hardcoded columns (carton_count, carton_identifier, sample_scope,
-- grower, pallet_no, grower_pallet, pack_code, date_code, label_value,
-- lot_no, packaging_material). Adding a new one required a schema +
-- code change. This migration introduces two tables modeled on the
-- reading-type infrastructure:
--
--   qms_sample_header_field   -- one row per configurable field,
--                                always global (no material_group)
--   qms_sample_header_value   -- per-sample value, keyed by sample_id
--                                + field_id
--
-- The 10 existing hardcoded fields are seeded into the catalog so the
-- admin screen is pre-populated, and existing per-sample data is
-- copied into the value table so old samples render correctly through
-- the new code path. The legacy columns on qms_sample stay (read-only
-- safety net) -- a future migration can drop them once we're sure
-- nothing reads them anymore.
--
-- sample_size is INTENTIONALLY not part of this catalog -- it remains
-- a first-class column on qms_sample because every defect percentage
-- in the report divides by it.
-- ===================================================================

-- 1) Catalog table -----------------------------------------------------
IF OBJECT_ID('qms_sample_header_field') IS NULL
BEGIN
    CREATE TABLE qms_sample_header_field (
        field_id      INT IDENTITY(1,1) PRIMARY KEY,
        field_code    VARCHAR(40)   NOT NULL,
        field_name    NVARCHAR(100) NOT NULL,
        value_kind    VARCHAR(20)   NOT NULL CONSTRAINT DF_qms_sample_header_field_kind DEFAULT 'Text',
        default_unit  VARCHAR(20)   NULL,
        is_active     BIT           NOT NULL CONSTRAINT DF_qms_sample_header_field_active DEFAULT 1,
        is_mandatory  BIT           NOT NULL CONSTRAINT DF_qms_sample_header_field_mandatory DEFAULT 0,
        sort_order    INT           NOT NULL CONSTRAINT DF_qms_sample_header_field_sort DEFAULT 500,
        CONSTRAINT UQ_qms_sample_header_field_code UNIQUE (field_code),
        CONSTRAINT CK_qms_sample_header_field_kind
            CHECK (value_kind IN ('Text','Numeric','Date'))
    );
END
GO

-- 2) Value table -------------------------------------------------------
IF OBJECT_ID('qms_sample_header_value') IS NULL
BEGIN
    CREATE TABLE qms_sample_header_value (
        sample_id     BIGINT        NOT NULL,
        field_id      INT           NOT NULL,
        text_value    NVARCHAR(255) NULL,
        numeric_value DECIMAL(18,4) NULL,
        date_value    DATE          NULL,
        CONSTRAINT PK_qms_sample_header_value PRIMARY KEY (sample_id, field_id),
        CONSTRAINT FK_qms_sample_header_value_sample
            FOREIGN KEY (sample_id) REFERENCES qms_sample(sample_id) ON DELETE CASCADE,
        CONSTRAINT FK_qms_sample_header_value_field
            FOREIGN KEY (field_id)  REFERENCES qms_sample_header_field(field_id)
    );
    CREATE INDEX IX_qms_sample_header_value_sample ON qms_sample_header_value(sample_id);
END
GO

-- 3) Seed the existing 10 hardcoded fields so the new admin screen is
--    immediately populated. Idempotent via field_code uniqueness.
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'CARTON_COUNT')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('CARTON_COUNT', 'Carton Count', 'Numeric', 10);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'CARTON_IDENTIFIER')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('CARTON_IDENTIFIER', 'Carton Identifier', 'Text', 20);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'SAMPLE_SCOPE')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('SAMPLE_SCOPE', 'Sample Scope', 'Text', 30);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'GROWER')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('GROWER', 'Grower', 'Text', 40);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'PALLET_NO')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('PALLET_NO', 'Pallet No', 'Text', 50);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'GROWER_PALLET')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('GROWER_PALLET', 'Grower Pallet', 'Text', 60);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'PACK_CODE')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('PACK_CODE', 'Pack Code', 'Text', 70);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'DATE_CODE')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('DATE_CODE', 'Date Code', 'Text', 80);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'LABEL_VALUE')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('LABEL_VALUE', 'Label', 'Text', 90);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'LOT_NO')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('LOT_NO', 'Lot No', 'Text', 100);
IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'PACKAGING_MATERIAL')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, sort_order)
    VALUES ('PACKAGING_MATERIAL', 'Packaging Material', 'Text', 110);
GO

-- 4) Migrate existing per-sample values from the legacy hardcoded
--    columns into qms_sample_header_value. Each insert is gated on
--    "no row already exists for this (sample, field)" so re-running
--    the migration after a partial apply is safe -- and so a value
--    edited through the new admin UI after this migration won't be
--    overwritten by a re-run.

-- CARTON_COUNT (numeric)
INSERT INTO qms_sample_header_value (sample_id, field_id, numeric_value)
SELECT s.sample_id, f.field_id, s.carton_count
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'CARTON_COUNT'
WHERE  s.carton_count IS NOT NULL
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

-- Text-valued legacy columns -- one INSERT per column.
INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.carton_identifier
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'CARTON_IDENTIFIER'
WHERE  s.carton_identifier IS NOT NULL AND LEN(s.carton_identifier) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.sample_scope
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'SAMPLE_SCOPE'
WHERE  s.sample_scope IS NOT NULL AND LEN(s.sample_scope) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.grower
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'GROWER'
WHERE  s.grower IS NOT NULL AND LEN(s.grower) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.pallet_no
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'PALLET_NO'
WHERE  s.pallet_no IS NOT NULL AND LEN(s.pallet_no) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.grower_pallet
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'GROWER_PALLET'
WHERE  s.grower_pallet IS NOT NULL AND LEN(s.grower_pallet) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.pack_code
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'PACK_CODE'
WHERE  s.pack_code IS NOT NULL AND LEN(s.pack_code) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.date_code
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'DATE_CODE'
WHERE  s.date_code IS NOT NULL AND LEN(s.date_code) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.label_value
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'LABEL_VALUE'
WHERE  s.label_value IS NOT NULL AND LEN(s.label_value) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.lot_no
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'LOT_NO'
WHERE  s.lot_no IS NOT NULL AND LEN(s.lot_no) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);

INSERT INTO qms_sample_header_value (sample_id, field_id, text_value)
SELECT s.sample_id, f.field_id, s.packaging_material
FROM   qms_sample s
JOIN   qms_sample_header_field f ON f.field_code = 'PACKAGING_MATERIAL'
WHERE  s.packaging_material IS NOT NULL AND LEN(s.packaging_material) > 0
  AND  NOT EXISTS (SELECT 1 FROM qms_sample_header_value v
                   WHERE v.sample_id = s.sample_id AND v.field_id = f.field_id);
GO

-- 5) Sanity
SELECT 'qms_sample_header_field rows' AS check_item, COUNT(*) AS value
FROM   qms_sample_header_field;

SELECT 'qms_sample_header_value rows' AS check_item, COUNT(*) AS value
FROM   qms_sample_header_value;
GO
