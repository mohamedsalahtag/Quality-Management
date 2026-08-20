-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M20  -  archive as a FLAG, not a status
-- ============================================================================
--  Supersedes M17. M17 archived the pre-go-live backlog by writing a fifth
--  claim status, 'Archived'. That conflated two orthogonal things: WHERE an
--  order is filed (active worklist vs archive) and WHAT the claim decision on
--  it is. The consequences showed up immediately:
--
--    * An order carrying a real decision (ClaimRequestApproved, PassedQC, ...)
--      could only be archived by DESTROYING that decision.
--    * An order that was never Closed has no qms_claim row at all, so it could
--      not be archived by any status write.
--
--  Archive is therefore a flag on the quality order itself. Any order can be
--  archived regardless of its QO status or its claim status, and the archive
--  page still shows each order's true claim decision.
--
--  Part A: qms_quality_order.archived_at / archived_by.
--  Part B: flag every order dated before the 2026-08-18 go-live cut-over,
--          whatever its status.
--  Part C: undo M17 -- delete the placeholder 'Archived' claim rows (they hold
--          nothing but the auto-generated note; the notes and read markers go
--          with them by ON DELETE CASCADE) and drop 'Archived' from the claim
--          status CHECK. Orders that had a REAL claim status keep it untouched.
--
--  Cut-over instant: 2026-08-17T21:00:00Z = 2026-08-18 00:00 Riyadh. Dates are
--  stored UTC. Non-closed orders have no closed_at, so the cut-over date falls
--  back to opened_at and then created_at.
--
--  Idempotent: safe to re-run.
-- ============================================================================

-- Part A ---------------------------------------------------------------
IF COL_LENGTH('qms.qms_quality_order', 'archived_at') IS NULL
    ALTER TABLE qms.qms_quality_order ADD archived_at DATETIME2(0) NULL;
GO
IF COL_LENGTH('qms.qms_quality_order', 'archived_by') IS NULL
    ALTER TABLE qms.qms_quality_order ADD archived_by NVARCHAR(80) NULL;
GO

-- Partial index: the Claims list filters archived_at IS NULL on every load,
-- and the archive page filters IS NOT NULL. Only archived rows need indexing.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_quality_order_archived')
    CREATE INDEX IX_qms_quality_order_archived
        ON qms.qms_quality_order(archived_at)
        WHERE archived_at IS NOT NULL;
GO

-- Part B ---------------------------------------------------------------
UPDATE qms.qms_quality_order
SET    archived_at = SYSUTCDATETIME(),
       archived_by = 'system'
WHERE  archived_at IS NULL
  AND  COALESCE(closed_at, opened_at, created_at) < '2026-08-17T21:00:00';
GO

-- Part C ---------------------------------------------------------------
-- Notes and read markers cascade (both FKs are ON DELETE CASCADE), so this
-- removes the placeholder rows whole. Orders return to "no claim row yet",
-- which the UI renders as Pending -- correct, since no decision was ever made.
DELETE FROM qms.qms_claim WHERE claim_status = 'Archived';
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_qms_claim_status'
             AND OBJECT_SCHEMA_NAME(parent_object_id) = 'qms'
             AND definition LIKE '%Archived%')
BEGIN
    ALTER TABLE qms.qms_claim DROP CONSTRAINT CK_qms_claim_status;
    ALTER TABLE qms.qms_claim ADD CONSTRAINT CK_qms_claim_status CHECK (
        claim_status IN ('ClaimRequest','PassedQC','ClaimRequestApproved','HoldClaim'));
END
GO

-- Sanity report --------------------------------------------------------
SELECT 'archived_orders'            AS check_item, COUNT(*) AS value FROM qms.qms_quality_order WHERE archived_at IS NOT NULL
UNION ALL
SELECT 'active_orders',             COUNT(*) FROM qms.qms_quality_order WHERE archived_at IS NULL
UNION ALL
SELECT 'pre_cutover_not_archived',  COUNT(*) FROM qms.qms_quality_order
       WHERE archived_at IS NULL AND COALESCE(closed_at, opened_at, created_at) < '2026-08-17T21:00:00'
UNION ALL
SELECT 'claim_rows_remaining',      COUNT(*) FROM qms.qms_claim
UNION ALL
SELECT 'archived_status_rows_left', COUNT(*) FROM qms.qms_claim WHERE claim_status = 'Archived'
UNION ALL
SELECT 'quality_orders_total',      COUNT(*) FROM qms.qms_quality_order;
GO
