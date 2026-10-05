/* =====================================================================
   M39 -- Tolerance on a defect CATEGORY, per material group.

   M33/M34 gave each catalog defect its own tolerance. The QC team also
   agrees a limit on a category as a whole -- "Major defects together must
   stay under 5% for apples" -- and that limit differs by fruit. A category
   (Major, Minor, ...) is one row shared by every material group, so the
   tolerance cannot live on qms_defect_category itself: it is held here,
   one row per (category, material group).

   The rule it drives: a category's TOTAL percentage in a sample or a
   group summary (the Total line of that category's table) at or above
   this tolerance is printed in red -- the same inclusive rule as the
   per-defect tolerance.

   NULL tolerance (or no row) means none agreed, which is NOT the same as
   zero: zero says any occurrence of the category fails. DECIMAL(9,2)
   matches the catalog column after M34.

   Deleting a category removes its tolerances with it (ON DELETE CASCADE);
   the Defect Categories screen only lets an unused category be deleted.

   Purely additive. Idempotent: safe to re-run (the migrate tool has no
   version table). Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M39__defect_category_tolerance.sql
   ===================================================================== */

IF OBJECT_ID(N'qms.qms_defect_category_tolerance', N'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_defect_category_tolerance (
        category_id    INT           NOT NULL
            CONSTRAINT FK_qms_dct_category REFERENCES qms.qms_defect_category (category_id) ON DELETE CASCADE,
        material_group VARCHAR(9)    NOT NULL,
        tolerance      DECIMAL(9,2)  NULL,
        updated_at     DATETIME2(0)  NULL,
        updated_by     NVARCHAR(256) NULL,
        CONSTRAINT PK_qms_defect_category_tolerance PRIMARY KEY (category_id, material_group)
    );
END;
GO

-- dbo synonym so the app's unqualified queries resolve (M05 convention).
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_defect_category_tolerance' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_defect_category_tolerance FOR qms.qms_defect_category_tolerance;
GO

-- Sanity report (the migrate tool prints SELECT batches).
SELECT 'category tolerance table' AS check_item,
       COUNT(*)                   AS found
FROM   sys.tables
WHERE  object_id = OBJECT_ID('qms.qms_defect_category_tolerance');

SELECT 'dbo synonym' AS check_item, COUNT(*) AS found
FROM   sys.synonyms WHERE name = 'qms_defect_category_tolerance' AND schema_id = SCHEMA_ID('dbo');
