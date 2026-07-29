-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M11  -  Report unit per material group
-- ============================================================================
--  The Quality Order PDF printed "Pieces" as a hard-coded literal in two
--  places: the group summary's "Sample Size: N Pieces" field, and the column
--  header above every defect list (in the summary AND on each sample card).
--  Some material groups are counted in boxes or cartons, not pieces.
--
--  One row per material group holds the label to print instead. A group with
--  no row prints the default 'Pieces', so this table starts empty on purpose
--  -- nothing to seed, and no migration to re-run when SAP grows a new group.
--
--  The label is cosmetic: no percentage or total arithmetic changes with it.
--
--  Managed from Parameters > Report Units (Manager/SiteAdmin).
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_material_group_unit') IS NULL
BEGIN
    CREATE TABLE qms.qms_material_group_unit (
        material_group VARCHAR(9)   NOT NULL
            CONSTRAINT PK_qms_material_group_unit PRIMARY KEY,
        unit_label     NVARCHAR(20) NOT NULL
            CONSTRAINT DF_qms_material_group_unit_label DEFAULT N'Pieces',
        updated_at     DATETIME2    NOT NULL
            CONSTRAINT DF_qms_material_group_unit_upd DEFAULT SYSUTCDATETIME(),
        updated_by     NVARCHAR(100) NULL
    );
END
GO

-- The app addresses the table by its bare name; mirror the M05 convention.
IF OBJECT_ID('dbo.qms_material_group_unit') IS NULL
    CREATE SYNONYM dbo.qms_material_group_unit FOR qms.qms_material_group_unit;
GO

SELECT 'material_group_unit' AS check_item, COUNT(*) AS value FROM qms.qms_material_group_unit;
