/* =====================================================================
   M35 -- Reinspection of a finished quality order.

   When a finished inspection is doubted, an administrator can have the
   container inspected again. The reinspection is a NEW quality order for
   the same arrival; the original is kept, stays Closed, and both remain
   printable.

   THE BLOCKER THIS MIGRATION REMOVES
   ----------------------------------
   UX_qms_quality_order_active_per_arrival is a UNIQUE index on
   arrival_id filtered to status_code <> 'Cancelled'. A Closed order
   occupies that slot forever, so a second INSERT for the same arrival
   fails with a duplicate key (error 2601) no matter what the application
   does. The index is not wrong -- it means "one LIVE inspection per
   container" -- it just had no way to express that an order had been
   superseded. Adding superseded_at to the filter says exactly that and
   keeps the protection everywhere else.

   WHY superseded_at RATHER THAN A STATUS CHANGE
   ---------------------------------------------
   The original was finished. Moving it out of 'Closed' would falsify the
   record and break the status-history chain, which asserts that each
   transition's old_status matches the previous row's new_status. The
   supersede is a separate fact recorded alongside the status, not a
   rewrite of it.

   ONE REINSPECTION PER CONTAINER is enforced by a unique index on
   reinspection_of rather than by application code alone: the C# guard can
   be bypassed by a second request arriving in the same instant, and this
   is the kind of duplicate nobody would notice until two reports
   disagreed.

   APPLY BEFORE DEPLOYING THE BINARIES. reinspection_of and superseded_at
   join the shared quality-order projection (QoSelect), so code expecting
   them against a database without them breaks every quality-order page.
   Same warning as M32.

   Idempotent: safe to re-run (the migrate tool has no version table).
   Apply with:
     dotnet run --project app\SharbatlyQMS.Migrate -- apply "<cs>" app\db\mis\M35__reinspection.sql
   ===================================================================== */

/* ---- 1. the columns ------------------------------------------------ */

IF COL_LENGTH('qms.qms_quality_order', 'reinspection_of') IS NULL
    ALTER TABLE qms.qms_quality_order ADD reinspection_of BIGINT NULL;
GO

IF COL_LENGTH('qms.qms_quality_order', 'superseded_at') IS NULL
    ALTER TABLE qms.qms_quality_order ADD superseded_at DATETIME2(0) NULL;
GO

IF COL_LENGTH('qms.qms_quality_order', 'superseded_by') IS NULL
    ALTER TABLE qms.qms_quality_order ADD superseded_by NVARCHAR(80) NULL;
GO

/* ---- 2. the order this one re-does has to exist --------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys
               WHERE name = 'FK_qms_quality_order_reinspection_of')
    ALTER TABLE qms.qms_quality_order
        ADD CONSTRAINT FK_qms_quality_order_reinspection_of
            FOREIGN KEY (reinspection_of) REFERENCES qms.qms_quality_order (quality_order_id);
GO

/* ---- 3. one LIVE inspection per container --------------------------
   Drop and recreate rather than alter: a filtered index's predicate
   cannot be changed in place. Between the two statements the uniqueness
   is briefly unenforced, which is why this belongs in a migration run
   against a stopped or quiet service rather than in application code. */

IF EXISTS (SELECT 1 FROM sys.indexes
           WHERE name = 'UX_qms_quality_order_active_per_arrival'
             AND object_id = OBJECT_ID('qms.qms_quality_order')
             AND filter_definition <> '([status_code]<>''Cancelled'' AND [superseded_at] IS NULL)')
    DROP INDEX UX_qms_quality_order_active_per_arrival ON qms.qms_quality_order;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_quality_order_active_per_arrival'
                 AND object_id = OBJECT_ID('qms.qms_quality_order'))
    CREATE UNIQUE INDEX UX_qms_quality_order_active_per_arrival
        ON qms.qms_quality_order (arrival_id)
        WHERE status_code <> 'Cancelled' AND superseded_at IS NULL;
GO

/* ---- 4. one reinspection per original ------------------------------- */

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_quality_order_reinspection_of'
                 AND object_id = OBJECT_ID('qms.qms_quality_order'))
    CREATE UNIQUE INDEX UX_qms_quality_order_reinspection_of
        ON qms.qms_quality_order (reinspection_of)
        WHERE reinspection_of IS NOT NULL;
GO

/* ---- 5. 'Reinspection' becomes a claim decision ---------------------
   The decision to reinspect is recorded on the ORIGINAL order's claim, so
   the Claims worklist says why the container came back rather than simply
   losing a row. CK_qms_claim_status has to admit the new value first.
   Drop and re-add, following V31's widening of the quality-order status
   check. */

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_qms_claim_status'
             AND parent_object_id = OBJECT_ID('qms.qms_claim')
             AND definition NOT LIKE '%Reinspection%')
    ALTER TABLE qms.qms_claim DROP CONSTRAINT CK_qms_claim_status;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_qms_claim_status'
                 AND parent_object_id = OBJECT_ID('qms.qms_claim'))
    ALTER TABLE qms.qms_claim WITH CHECK
        ADD CONSTRAINT CK_qms_claim_status CHECK (
            claim_status IN ('ClaimRequest', 'PassedQC', 'ClaimRequestApproved',
                             'HoldClaim', 'Reinspection'));
GO

/* ---- Sanity report (the migrate tool prints SELECT batches) --------- */

SELECT 'new columns' AS check_item, COUNT(*) AS found
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_quality_order')
  AND  name IN ('reinspection_of', 'superseded_at', 'superseded_by');

SELECT 'active-per-arrival filter' AS check_item, i.filter_definition AS found
FROM   sys.indexes i
WHERE  i.object_id = OBJECT_ID('qms.qms_quality_order')
  AND  i.name = 'UX_qms_quality_order_active_per_arrival';

SELECT 'claim status check' AS check_item, cc.definition AS found
FROM   sys.check_constraints cc
WHERE  cc.parent_object_id = OBJECT_ID('qms.qms_claim')
  AND  cc.name = 'CK_qms_claim_status';

-- Nothing existing may have been disturbed: every arrival still has at
-- most one live order, and no row is a reinspection yet.
SELECT 'arrivals with more than one live order' AS check_item, COUNT(*) AS found
FROM  (SELECT arrival_id FROM qms.qms_quality_order
       WHERE status_code <> 'Cancelled' AND superseded_at IS NULL
       GROUP BY arrival_id HAVING COUNT(*) > 1) x;

SELECT 'reinspection rows' AS check_item, COUNT(*) AS found
FROM   qms.qms_quality_order WHERE reinspection_of IS NOT NULL;
