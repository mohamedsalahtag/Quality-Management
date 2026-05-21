-- ===================================================================
-- V06  Defect catalog becomes per-material-group.
--
-- Before this migration, qms_defect_catalog held a flat list of defects
-- with a global UNIQUE on defect_code, and qms_material_group_defect
-- linked each defect to material groups with a display_section override.
-- That made it impossible to have the same defect_name (e.g. "Crack")
-- on more than one group, and admins had to maintain two tables to
-- change a single mapping.
--
-- After this migration every defect carries its own material_group and
-- value_type; the Major/Minor split shown on the Sample form comes
-- straight from defect_category on the catalog row, and the legacy
-- qms_material_group_defect table is no longer read (kept in place for
-- audit / rollback only).
-- ===================================================================

-- 1. New columns. material_group is added nullable first so we can backfill,
-- then locked to NOT NULL.
ALTER TABLE qms_defect_catalog ADD material_group VARCHAR(9) NULL;
GO
ALTER TABLE qms_defect_catalog ADD value_type VARCHAR(20) NOT NULL
    CONSTRAINT DF_qms_defect_catalog_value_type DEFAULT 'Number';
GO
ALTER TABLE qms_defect_catalog ADD
    CONSTRAINT CK_qms_defect_catalog_value_type CHECK (value_type IN ('Number','Decimal'));
GO

-- 2. Backfill: every pre-existing defect belongs to the apple group
-- (FRSH-APP) -- the only group seeded today.
UPDATE qms_defect_catalog SET material_group = 'FRSH-APP' WHERE material_group IS NULL;
GO
ALTER TABLE qms_defect_catalog ALTER COLUMN material_group VARCHAR(9) NOT NULL;
GO

-- 3. Drop the global UNIQUE constraint on defect_code so the same code can
-- exist for more than one group. The constraint was unnamed in V02, so
-- locate it via sys.key_constraints and drop it dynamically.
DECLARE @uqName SYSNAME;
SELECT TOP 1 @uqName = kc.name
FROM   sys.key_constraints kc
JOIN   sys.index_columns  ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
JOIN   sys.columns        cl ON cl.object_id = ic.object_id AND cl.column_id = ic.column_id
WHERE  kc.parent_object_id = OBJECT_ID('dbo.qms_defect_catalog')
  AND  kc.type = 'UQ'
  AND  cl.name = 'defect_code';
IF @uqName IS NOT NULL
    EXEC('ALTER TABLE qms_defect_catalog DROP CONSTRAINT ' + @uqName);
GO

-- 4. New unique scoped to (material_group, defect_code). Lets "CRACK" exist
-- for FRSH-APP and FRSH-ORG without collision.
ALTER TABLE qms_defect_catalog
    ADD CONSTRAINT UQ_qms_defect_catalog_group_code UNIQUE (material_group, defect_code);
GO

-- 5. Sanity report -- prints how many rows landed in each group.
SELECT material_group, COUNT(*) AS defect_count
FROM   qms_defect_catalog
GROUP BY material_group
ORDER BY material_group;
