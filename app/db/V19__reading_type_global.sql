-- ===================================================================
-- V19  Reading types: allow "Global" scope (applies to every material group)
--
-- Until now every qms_reading_type row had to belong to one material
-- group (NOT NULL, UNIQUE(material_group, reading_type_code) added in
-- V09). Common readings like BRIX, TARA, GROSS_WEIGHT were duplicated
-- once per fruit. This migration lets `material_group` be NULL, where
-- NULL means "applies to every material group". The sample form,
-- Admin → Reading Types listing, and the Quality Order PDF grouped
-- summary all include globals alongside the per-group rows.
--
-- Uniqueness is split into two filtered indexes so that:
--   - only one global row can exist per reading_type_code
--   - only one per-group row can exist per (material_group, code)
-- A code can still collide between a global and a per-group row -- the
-- admin save path rejects that case at the application layer (the PDF
-- and sample form would otherwise show two rows for the same code on
-- that material group).
-- ===================================================================

-- 1) Relax NOT NULL on material_group.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_reading_type')
      AND name = 'material_group' AND is_nullable = 0)
BEGIN
    ALTER TABLE qms_reading_type ALTER COLUMN material_group VARCHAR(20) NULL;
END
GO

-- 2) Drop the V09 uniqueness on (group+code) which forbids NULL on the
--    group column. Two filtered uniques replace it below. V09 may have
--    landed as either a UNIQUE CONSTRAINT (preferred form in V09) or a
--    bare unique index in some envs -- handle both shapes.
IF EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = 'UQ_qms_reading_type_group_code'
      AND parent_object_id = OBJECT_ID('qms_reading_type'))
BEGIN
    ALTER TABLE qms_reading_type DROP CONSTRAINT UQ_qms_reading_type_group_code;
END
ELSE IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UQ_qms_reading_type_group_code'
      AND object_id = OBJECT_ID('qms_reading_type'))
BEGIN
    DROP INDEX UQ_qms_reading_type_group_code ON qms_reading_type;
END
GO

-- 3) One global row per code.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UQ_qms_reading_type_global_code'
      AND object_id = OBJECT_ID('qms_reading_type'))
BEGIN
    CREATE UNIQUE INDEX UQ_qms_reading_type_global_code
        ON qms_reading_type(reading_type_code)
        WHERE material_group IS NULL;
END
GO

-- 4) One per-group row per (group, code) -- replaces the V09 index.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UQ_qms_reading_type_per_group_code'
      AND object_id = OBJECT_ID('qms_reading_type'))
BEGIN
    CREATE UNIQUE INDEX UQ_qms_reading_type_per_group_code
        ON qms_reading_type(material_group, reading_type_code)
        WHERE material_group IS NOT NULL;
END
GO

-- sanity
SELECT 'qms_reading_type.material_group nullable' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('qms_reading_type')
              AND name = 'material_group' AND is_nullable = 1)
            THEN 'OK' ELSE 'STILL NOT NULL' END AS status;

SELECT 'UQ_qms_reading_type_global_code'  AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'UQ_qms_reading_type_global_code'
              AND object_id = OBJECT_ID('qms_reading_type'))
            THEN 'OK' ELSE 'MISSING' END AS status;

SELECT 'UQ_qms_reading_type_per_group_code' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'UQ_qms_reading_type_per_group_code'
              AND object_id = OBJECT_ID('qms_reading_type'))
            THEN 'OK' ELSE 'MISSING' END AS status;

SELECT material_group, COUNT(*) AS rows_after_v19
  FROM qms_reading_type
 GROUP BY material_group
 ORDER BY CASE WHEN material_group IS NULL THEN 0 ELSE 1 END, material_group;
GO
