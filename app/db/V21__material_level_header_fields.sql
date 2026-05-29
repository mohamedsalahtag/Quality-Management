-- ===================================================================
-- V21  Material-level sample-header fields + material sample_size.
--
-- Several "sample header" fields actually describe the MATERIAL, not the
-- individual sample (grower pallet, pack code, date code, label, label
-- number, sample size). This migration lets a header field be SCOPED to
-- the material: the user enters it once per qms_quality_order_material
-- and every sample of that material inherits it.
--
--   qms_sample_header_field.scope    -- 'Sample' (default) | 'Material'
--   qms_qo_material_header_value      -- per-material value, keyed by
--                                        qo_material_id + field_id
--   qms_quality_order_material.sample_size  -- material-level sample size;
--                                        the per-sample qms_sample.sample_size
--                                        becomes an inherited cache (the report
--                                        still divides by it, so it stays).
--
-- The four existing identification fields (grower pallet, pack code,
-- date code, label) default to Material scope; a new LABEL_NUMBER field
-- is seeded (Material). Existing QOs are migrated by rolling each
-- material's value up from its lowest-numbered sample.
-- ===================================================================

-- 1) scope column on the header-field catalog --------------------------
IF COL_LENGTH('qms_sample_header_field','scope') IS NULL
    ALTER TABLE qms_sample_header_field
        ADD scope VARCHAR(10) NOT NULL
            CONSTRAINT DF_qms_sample_header_field_scope DEFAULT 'Sample';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_qms_sample_header_field_scope')
    ALTER TABLE qms_sample_header_field
        ADD CONSTRAINT CK_qms_sample_header_field_scope CHECK (scope IN ('Sample','Material'));
GO

-- 2) Default the four identification fields to Material scope, and seed
--    the new LABEL_NUMBER field. (One-time initial assignment; the admin
--    screen can re-scope any field afterwards.)
UPDATE qms_sample_header_field
SET    scope = 'Material'
WHERE  field_code IN ('GROWER_PALLET','PACK_CODE','DATE_CODE','LABEL_VALUE')
  AND  scope <> 'Material';

IF NOT EXISTS (SELECT 1 FROM qms_sample_header_field WHERE field_code = 'LABEL_NUMBER')
    INSERT INTO qms_sample_header_field (field_code, field_name, value_kind, scope, sort_order)
    VALUES ('LABEL_NUMBER', 'Label Number', 'Text', 'Material', 95);
GO

-- 3) Material-level sample_size (source of truth; samples inherit) ------
IF COL_LENGTH('qms_quality_order_material','sample_size') IS NULL
    ALTER TABLE qms_quality_order_material ADD sample_size SMALLINT NULL;
GO

-- 4) Per-material header value table (mirrors qms_sample_header_value) --
IF OBJECT_ID('qms_qo_material_header_value') IS NULL
BEGIN
    CREATE TABLE qms_qo_material_header_value (
        qo_material_id BIGINT        NOT NULL,
        field_id       INT           NOT NULL,
        text_value     NVARCHAR(255) NULL,
        numeric_value  DECIMAL(18,4) NULL,
        date_value     DATE          NULL,
        CONSTRAINT PK_qms_qo_material_header_value PRIMARY KEY (qo_material_id, field_id),
        CONSTRAINT FK_qms_qmhv_material
            FOREIGN KEY (qo_material_id) REFERENCES qms_quality_order_material(qo_material_id) ON DELETE CASCADE,
        CONSTRAINT FK_qms_qmhv_field
            FOREIGN KEY (field_id) REFERENCES qms_sample_header_field(field_id)
    );
    CREATE INDEX IX_qms_qo_material_header_value_mat ON qms_qo_material_header_value(qo_material_id);
END
GO

-- 5) Migrate existing QOs ----------------------------------------------
--    Roll each material's Material-scoped header values up from its
--    LOWEST-numbered sample (deterministic, no tie-break ambiguity; the
--    first sample entered best represents the material). Gated on
--    NOT EXISTS so a re-apply won't clobber values edited since.
WITH ranked AS (
    SELECT s.qo_material_id,
           v.field_id,
           v.text_value,
           v.numeric_value,
           v.date_value,
           ROW_NUMBER() OVER (PARTITION BY s.qo_material_id, v.field_id ORDER BY s.sample_no) AS rn
    FROM   qms_sample s
    JOIN   qms_sample_header_value v ON v.sample_id = s.sample_id
    JOIN   qms_sample_header_field f ON f.field_id  = v.field_id
    WHERE  s.is_deleted = 0 AND f.scope = 'Material'
)
INSERT INTO qms_qo_material_header_value (qo_material_id, field_id, text_value, numeric_value, date_value)
SELECT r.qo_material_id, r.field_id, r.text_value, r.numeric_value, r.date_value
FROM   ranked r
WHERE  r.rn = 1
  AND  NOT EXISTS (SELECT 1 FROM qms_qo_material_header_value x
                   WHERE x.qo_material_id = r.qo_material_id AND x.field_id = r.field_id);

-- sample_size: take the lowest-numbered sample with a non-null size;
-- only fill materials that don't already have one.
WITH ss AS (
    SELECT qo_material_id,
           sample_size,
           ROW_NUMBER() OVER (PARTITION BY qo_material_id ORDER BY sample_no) AS rn
    FROM   qms_sample
    WHERE  is_deleted = 0 AND sample_size IS NOT NULL
)
UPDATE m
SET    m.sample_size = ss.sample_size
FROM   qms_quality_order_material m
JOIN   ss ON ss.qo_material_id = m.qo_material_id AND ss.rn = 1
WHERE  m.sample_size IS NULL;

-- Propagate the material's sample_size back to every one of its samples
-- so the inherited-cache invariant holds for legacy data too.
UPDATE s
SET    s.sample_size = m.sample_size
FROM   qms_sample s
JOIN   qms_quality_order_material m ON m.qo_material_id = s.qo_material_id
WHERE  s.is_deleted = 0 AND m.sample_size IS NOT NULL;
GO

-- 6) Sanity
SELECT 'header fields (Material scope)' AS check_item, COUNT(*) AS value
FROM   qms_sample_header_field WHERE scope = 'Material';

SELECT 'qms_qo_material_header_value rows' AS check_item, COUNT(*) AS value
FROM   qms_qo_material_header_value;

SELECT 'materials with sample_size' AS check_item, COUNT(*) AS value
FROM   qms_quality_order_material WHERE sample_size IS NOT NULL;
GO
