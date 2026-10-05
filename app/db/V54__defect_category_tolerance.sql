/* =====================================================================
   V54 -- Tolerance on a defect CATEGORY, per material group. V-SERIES TWIN
   of app\db\mis\M39__defect_category_tolerance.sql.

   The live system runs the M-series against Sharbatly_MIS (schema [qms]
   behind dbo synonyms). This file exists only so the older private
   SharbatlyQMS database on 192.168.3.10 can be brought level; that
   database is retired, so this is unapplied unless it is revived.

   One row per (category, material group): a category is shared by every
   fruit, but its agreed limit is not. A category's TOTAL percentage at or
   above the tolerance is printed in red. NULL means none agreed, which is
   not the same as zero.

   Idempotent: safe to re-run.
   ===================================================================== */

IF OBJECT_ID(N'qms_defect_category_tolerance', N'U') IS NULL
BEGIN
    CREATE TABLE qms_defect_category_tolerance (
        category_id    INT           NOT NULL
            CONSTRAINT FK_qms_dct_category REFERENCES qms_defect_category (category_id) ON DELETE CASCADE,
        material_group VARCHAR(9)    NOT NULL,
        tolerance      DECIMAL(9,2)  NULL,
        updated_at     DATETIME2(0)  NULL,
        updated_by     NVARCHAR(256) NULL,
        CONSTRAINT PK_qms_defect_category_tolerance PRIMARY KEY (category_id, material_group)
    );
END;
GO

SELECT 'category tolerance table' AS check_item, COUNT(*) AS found
FROM   sys.tables WHERE object_id = OBJECT_ID('qms_defect_category_tolerance');
