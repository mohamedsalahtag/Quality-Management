/* =====================================================================
   M33 -- Tolerance on a defect catalog row.

   Each defect can carry an allowed tolerance: the level at or below which
   the defect is considered acceptable for that material group. Held to
   ONE decimal place, which is how the QC team quotes them (2.5, 0.5), and
   enforced by the column type rather than by the form alone -- a value
   typed with more precision is stored rounded, so what is read back is
   what was agreed.

   NULL means no tolerance has been set, which is NOT the same as zero: a
   zero tolerance says "any occurrence fails", while NULL says nobody has
   decided yet. The catalog screen shows the two differently for that
   reason.

   DECIMAL(9,1) rather than a tighter width because nothing here knows
   whether a given defect's tolerance is a percentage, a piece count or a
   weight -- the unit belongs to the material group, and the column should
   not quietly cap a number the business finds reasonable.

   Purely additive: no existing row changes, and every query that does not
   ask for the column behaves exactly as before.

   Idempotent: safe to re-run (the migrate tool has no version table).
   Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M33__defect_tolerance.sql
   ===================================================================== */

IF COL_LENGTH('qms.qms_defect_catalog', 'tolerance') IS NULL
    ALTER TABLE qms.qms_defect_catalog ADD tolerance DECIMAL(9,1) NULL;
GO

-- Sanity report (the migrate tool prints SELECT batches).
SELECT 'tolerance column' AS check_item,
       COUNT(*)           AS found
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_defect_catalog')
  AND  name = 'tolerance';

SELECT 'catalog rows'            AS check_item, COUNT(*) AS found FROM qms.qms_defect_catalog
UNION ALL
SELECT 'rows with a tolerance',  COUNT(*) FROM qms.qms_defect_catalog WHERE tolerance IS NOT NULL;
