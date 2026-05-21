-- ===================================================================
-- V09  Reading types become per-material-group, mirroring V06 which did
-- the same for the defect catalog.
--
-- Before: qms_reading_type was a flat global list. Every sample form
-- on every material showed every reading type, and Major/Minor split
-- via the legacy qms_material_group_reading junction was never wired
-- up in code.
--
-- After: each reading type belongs to one material_group. The same
-- reading_type_code can be re-used across groups (e.g. TARA on APPLE
-- and TARA on BANANA are independent rows). The Sample form only
-- shows readings whose group matches the QO material line's group --
-- exactly the same pattern the defect catalog now uses.
-- ===================================================================

-- 1. Add the column nullable, backfill, then lock to NOT NULL.
IF COL_LENGTH('qms_reading_type','material_group') IS NULL
    ALTER TABLE qms_reading_type ADD material_group VARCHAR(20) NULL;
GO

UPDATE qms_reading_type SET material_group = 'APPLE' WHERE material_group IS NULL;
GO

ALTER TABLE qms_reading_type ALTER COLUMN material_group VARCHAR(20) NOT NULL;
GO

-- 2. Drop the global UNIQUE on reading_type_code (unnamed in V02).
DECLARE @uqName SYSNAME;
SELECT TOP 1 @uqName = kc.name
FROM   sys.key_constraints kc
JOIN   sys.index_columns  ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
JOIN   sys.columns        cl ON cl.object_id = ic.object_id AND cl.column_id = ic.column_id
WHERE  kc.parent_object_id = OBJECT_ID('dbo.qms_reading_type')
  AND  kc.type = 'UQ'
  AND  cl.name = 'reading_type_code';
IF @uqName IS NOT NULL
    EXEC('ALTER TABLE qms_reading_type DROP CONSTRAINT ' + @uqName);
GO

-- 3. New unique scoped to (material_group, reading_type_code).
IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE name = 'UQ_qms_reading_type_group_code' AND parent_object_id = OBJECT_ID('dbo.qms_reading_type'))
    ALTER TABLE qms_reading_type
        ADD CONSTRAINT UQ_qms_reading_type_group_code UNIQUE (material_group, reading_type_code);
GO

-- 4. Index on material_group for fast filter.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_reading_type_group' AND object_id = OBJECT_ID('qms_reading_type'))
    CREATE INDEX IX_qms_reading_type_group ON qms_reading_type(material_group) INCLUDE (is_active, sort_order);
GO

-- 5. Sanity report.
SELECT material_group, COUNT(*) AS reading_type_count
FROM   qms_reading_type
GROUP BY material_group
ORDER BY material_group;
