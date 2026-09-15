/* =====================================================================
   M34 -- Widen the defect tolerance to TWO decimal places.

   M33 shipped it at DECIMAL(9,1) because that is how the tolerances were
   quoted in conversation. Two decimals is what the business actually
   maintains, so the scale is widened before anybody starts entering
   values against the one-decimal rule and finds them rounded.

   Widening the SCALE of a DECIMAL is a size-preserving, non-destructive
   change here: no row carries a tolerance yet (checked: 0 of 708 on
   2026-09-15), and even if one did, 2.5 becomes 2.50 rather than moving.
   No index references the column, so there is nothing to rebuild.

   ALTER COLUMN, not drop-and-add: dropping would discard any value
   entered between this migration being written and being applied.

   Idempotent: safe to re-run -- the guard checks the current scale, so a
   second run is a no-op rather than a redundant rewrite.
   Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M34__defect_tolerance_two_decimals.sql
   ===================================================================== */

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('qms.qms_defect_catalog')
             AND name = 'tolerance' AND scale <> 2)
    ALTER TABLE qms.qms_defect_catalog ALTER COLUMN tolerance DECIMAL(9,2) NULL;
GO

-- Sanity report (the migrate tool prints SELECT batches).
SELECT 'tolerance precision/scale' AS check_item,
       CAST(c.precision AS VARCHAR(4)) + ',' + CAST(c.scale AS VARCHAR(4)) AS found
FROM   sys.columns c
WHERE  c.object_id = OBJECT_ID('qms.qms_defect_catalog') AND c.name = 'tolerance';

SELECT 'catalog rows'          AS check_item, COUNT(*) AS found FROM qms.qms_defect_catalog
UNION ALL
SELECT 'rows with a tolerance', COUNT(*) FROM qms.qms_defect_catalog WHERE tolerance IS NOT NULL;
