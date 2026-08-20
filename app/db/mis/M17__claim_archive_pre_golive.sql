-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M17  -  archive pre-go-live claims
-- ============================================================================
--  Context: the Claims page lists every Closed Quality Order and shows
--  "Pending" for any that has no qms_claim row yet (a UI-only sentinel — see
--  Models/Claim.cs). At go-live on 2026-08-18 that backlog was 218 orders
--  closed between 2026-07-27 and 2026-08-17, which would have buried the
--  first day's real work.
--
--  They are NOT deleted. Deleting them would erase the quality orders
--  themselves along with 2,295 samples and their readings, defects and photos
--  — the app deliberately refuses to do that (QualityOrderService.DeleteAsync
--  allows Initial/Open only). Instead each one gets a settled claim row with
--  the new status 'Archived', so it leaves the Pending worklist while every
--  inspection record stays intact and every report stays printable.
--
--  Part A: extend CK_qms_claim_status with 'Archived'.
--  Part B: backfill an Archived claim + explanatory note for each pre-cut-over
--          Closed QO.
--
--  decided_at is left NULL on purpose: a Quality Manager can still raise a
--  genuine claim on an old order later. The Claim Manager cannot act on an
--  Archived claim (CmTransitionAsync's allow-list excludes it), which is the
--  intended behaviour.
--
--  Cut-over instant: 2026-08-18 00:00 Riyadh time = 2026-08-17T21:00:00Z.
--  closed_at is stored UTC. (No rows fall in the 21:00–24:00Z window, so the
--  local-vs-UTC reading of "prior to August 18" selects the same 218 rows.)
--
--  Idempotent: safe to re-run — Part B inserts only where no claim row exists.
-- ============================================================================

--  ####  SUPERSEDED BY M20 (2026-08-20)  ####
--  M20 replaced status-based archiving with qms_quality_order.archived_at and
--  deleted every row this script wrote. Re-running M17 afterwards would
--  resurrect the 'Archived' claim status and undo that. Every batch below is
--  therefore guarded: once M20 has run, this script does nothing. Kept for the
--  historical record, not for re-execution.

IF COL_LENGTH('qms.qms_quality_order', 'archived_at') IS NOT NULL
BEGIN
    PRINT 'M17 skipped -- superseded by M20 (archive is a flag on qms_quality_order).';
    SET NOEXEC ON;
END
GO

-- Part A ---------------------------------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_qms_claim_status'
             AND OBJECT_SCHEMA_NAME(parent_object_id) = 'qms'
             AND definition NOT LIKE '%Archived%')
BEGIN
    ALTER TABLE qms.qms_claim DROP CONSTRAINT CK_qms_claim_status;
    ALTER TABLE qms.qms_claim ADD CONSTRAINT CK_qms_claim_status CHECK (
        claim_status IN ('ClaimRequest','PassedQC','ClaimRequestApproved','HoldClaim','Archived'));
END
GO

-- Part B ---------------------------------------------------------------
DECLARE @cutoverUtc DATETIME2(0) = '2026-08-17T21:00:00';
DECLARE @actor      NVARCHAR(80) = 'system';
DECLARE @noteText   NVARCHAR(MAX) =
    N'Archived automatically at the 2026-08-18 go-live cut-over. This Quality Order '
  + N'was finished before the new claim workflow went live, so no claim decision was '
  + N'ever recorded for it. The inspection record is unchanged and the report can '
  + N'still be printed. Raise a claim request here if one is genuinely needed.';

DECLARE @inserted TABLE (claim_id BIGINT);

INSERT INTO qms.qms_claim
    (quality_order_id, claim_status, created_at, created_by, last_changed_at, last_changed_by)
OUTPUT inserted.claim_id INTO @inserted
SELECT qo.quality_order_id, 'Archived', SYSUTCDATETIME(), @actor, SYSUTCDATETIME(), @actor
FROM   qms.qms_quality_order qo
LEFT   JOIN qms.qms_claim cl ON cl.quality_order_id = qo.quality_order_id
WHERE  qo.status_code = 'Closed'
  AND  cl.claim_id IS NULL
  AND  qo.closed_at < @cutoverUtc;

INSERT INTO qms.qms_claim_note
    (claim_id, note_text, note_kind, status_at_post, created_at, created_by, author_role)
SELECT i.claim_id, @noteText, 'StatusChange', 'Archived', SYSUTCDATETIME(), @actor, 'System'
FROM   @inserted i;
GO

-- Sanity report --------------------------------------------------------
SELECT 'archived_claims'        AS check_item, COUNT(*) AS value FROM qms.qms_claim WHERE claim_status = 'Archived'
UNION ALL
SELECT 'still_pending_pre_cutover', COUNT(*)
FROM   qms.qms_quality_order qo
LEFT   JOIN qms.qms_claim cl ON cl.quality_order_id = qo.quality_order_id
WHERE  qo.status_code = 'Closed' AND cl.claim_id IS NULL AND qo.closed_at < '2026-08-17T21:00:00'
UNION ALL
SELECT 'quality_orders_total', COUNT(*) FROM qms.qms_quality_order
UNION ALL
SELECT 'samples_total', COUNT(*) FROM qms.qms_sample;
GO

-- Release the guard so the session stays usable for the next script.
SET NOEXEC OFF;
GO
