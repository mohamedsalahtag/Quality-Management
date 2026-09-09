/* =====================================================================
   M24 -- Potential-claim classification captured when a Quality Order is
   finished.

   The QC supervisor now classifies every shipment at the end of the
   inspection report as either "Potential Claim" or "No Potential Claim".
   That verdict is a QC assessment, NOT a claim decision: the claim
   workflow (qms_claim.claim_status: ClaimRequest / PassedQC /
   ClaimRequestApproved / HoldClaim) stays exactly as it was, and a claim
   row is still created lazily on the Quality Manager's first action. The
   two live side by side in the Claims window -- the assessment says
   "this one needs looking at", the claim status says what was decided.

   Kept on qms_quality_order (not qms_claim) for the same reason M20 moved
   archiving off the claim status: writing a qms_claim row at finish time
   would destroy the "Pending" bucket, which is defined as "a Closed QO
   with no claim row yet".

   NULL = not classified. Every order finished before this migration is
   NULL, and the Claims window shows those as "Not classified".

   Idempotent: safe to re-run (the migrate tool has no version table).
   Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M24__qo_potential_claim.sql
   ===================================================================== */

IF COL_LENGTH('qms.qms_quality_order', 'potential_claim') IS NULL
    ALTER TABLE qms.qms_quality_order ADD potential_claim BIT NULL;
GO

IF COL_LENGTH('qms.qms_quality_order', 'potential_claim_at') IS NULL
    ALTER TABLE qms.qms_quality_order ADD potential_claim_at DATETIME2(0) NULL;
GO

IF COL_LENGTH('qms.qms_quality_order', 'potential_claim_by') IS NULL
    ALTER TABLE qms.qms_quality_order ADD potential_claim_by NVARCHAR(80) NULL;
GO

/* The Claims window filters on this column across the whole Closed
   population, so give it an index that covers the two buckets people
   actually chase (Potential / not classified) without bloating writes. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_quality_order_potential_claim'
                 AND object_id = OBJECT_ID('qms.qms_quality_order'))
    CREATE INDEX IX_qms_quality_order_potential_claim
        ON qms.qms_quality_order (potential_claim)
        INCLUDE (status_code, archived_at);
GO

-- Sanity report (the migrate tool prints SELECT batches).
SELECT 'potential_claim columns' AS check_item,
       COUNT(*)                  AS found
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_quality_order')
  AND  name IN ('potential_claim', 'potential_claim_at', 'potential_claim_by');

SELECT 'closed orders not yet classified' AS check_item,
       COUNT(*)                           AS found
FROM   qms.qms_quality_order
WHERE  status_code = 'Closed' AND potential_claim IS NULL;
GO
