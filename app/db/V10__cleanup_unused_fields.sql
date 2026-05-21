-- ===================================================================
-- V10  Two house-keeping changes called out by the user:
--   * qms_defect_catalog.default_unit is dropped -- no code path
--     read or wrote the unit per defect (sample form never showed it).
--   * qms_quality_order status "Reopened" is collapsed into "Open" --
--     after a Site Admin re-opens a finished QO it should behave like
--     an Open one. Audit columns (reopened_at / by / reason) are kept.
-- ===================================================================

-- 1. Drop the auto-named DEFAULT constraint on default_unit (left over
-- from the V02 CREATE TABLE), then drop the column.
DECLARE @dfName SYSNAME;
SELECT TOP 1 @dfName = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns col ON col.default_object_id = dc.object_id
WHERE  col.object_id = OBJECT_ID('dbo.qms_defect_catalog')
  AND  col.name = 'default_unit';
IF @dfName IS NOT NULL
    EXEC('ALTER TABLE qms_defect_catalog DROP CONSTRAINT ' + @dfName);
GO

IF COL_LENGTH('qms_defect_catalog', 'default_unit') IS NOT NULL
    ALTER TABLE qms_defect_catalog DROP COLUMN default_unit;
GO

-- 2. Collapse Reopened -> Open. Audit fields (reopened_at / reopened_by /
-- reopen_reason) remain so we can still see the history. Loosen the
-- status_code CHECK constraint as needed -- there isn't one declared on
-- qms_quality_order today, so a plain UPDATE is enough.
UPDATE qms_quality_order
SET    status_code = 'Open'
WHERE  status_code = 'Reopened';
GO

-- 3. Sanity report.
SELECT status_code, COUNT(*) AS qo_count
FROM   qms_quality_order
GROUP BY status_code
ORDER BY status_code;
