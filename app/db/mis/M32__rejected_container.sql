-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M32  -  rejected containers
-- ============================================================================
--  When an arrival shows the container is in bad condition there is nothing to
--  inspect -- the goods are damaged. But a quality order is still needed, to
--  carry the shipment details and the damage photos to the supplier and into
--  the claim process. So rejecting an arrival:
--
--    * moves it to the new terminal status 'Rejected', and
--    * raises a quality order that is CLOSED on creation, carrying a potential
--      claim and a configurable banner ("Not accepted container condition").
--
--  Three changes:
--
--    1. CK_qms_arrival_status widened to allow 'Rejected'. Dropped and re-added
--       rather than altered -- SQL Server has no ALTER CHECK. The live
--       definition is the OR form written by M02; only Draft and Completed are
--       actually in use, so no existing row can violate the new constraint.
--
--    2. qms_arrival gains rejected_at / rejected_by / reject_reason. The reason
--       is mandatory at the point of rejection and is printed verbatim on the
--       claim report, so it lives on the row rather than being dug out of
--       qms_status_history on every render.
--
--    3. qms_quality_order gains container_rejected. STORED rather than derived
--       from the arrival's status: a printed report must look the same forever,
--       and arrival status is mutable.
--
--  Apply BEFORE deploying the binaries -- container_rejected joins the shared
--  quality-order projection, so code without the column breaks every QO page.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF EXISTS (SELECT 1 FROM sys.check_constraints cc
           JOIN sys.tables t ON t.object_id = cc.parent_object_id
           WHERE cc.name = 'CK_qms_arrival_status'
             AND t.name = 'qms_arrival' AND SCHEMA_NAME(t.schema_id) = 'qms')
    ALTER TABLE qms.qms_arrival DROP CONSTRAINT CK_qms_arrival_status;
GO

ALTER TABLE qms.qms_arrival ADD CONSTRAINT CK_qms_arrival_status
    CHECK (status_code IN ('Draft', 'Completed', 'Cancelled', 'Rejected'));
GO

IF COL_LENGTH('qms.qms_arrival', 'rejected_at') IS NULL
    ALTER TABLE qms.qms_arrival ADD rejected_at DATETIME2 NULL;
GO
IF COL_LENGTH('qms.qms_arrival', 'rejected_by') IS NULL
    ALTER TABLE qms.qms_arrival ADD rejected_by NVARCHAR(80) NULL;
GO
IF COL_LENGTH('qms.qms_arrival', 'reject_reason') IS NULL
    ALTER TABLE qms.qms_arrival ADD reject_reason NVARCHAR(500) NULL;
GO

IF COL_LENGTH('qms.qms_quality_order', 'container_rejected') IS NULL
    ALTER TABLE qms.qms_quality_order ADD container_rejected BIT NOT NULL
        CONSTRAINT DF_qms_quality_order_container_rejected DEFAULT (0);
GO

-- The claims worklist and the Time Bar both filter on it; only the rejected
-- minority needs indexing.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_quality_order_container_rejected')
    CREATE INDEX IX_qms_quality_order_container_rejected
        ON qms.qms_quality_order(container_rejected)
        INCLUDE (arrival_id, status_code)
        WHERE container_rejected = 1;
GO

SELECT 'arrival_reject_columns' AS check_item, CAST(COUNT(*) AS VARCHAR(20)) AS value
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_arrival')
  AND  name IN ('rejected_at', 'rejected_by', 'reject_reason')
UNION ALL
SELECT 'qo_container_rejected',
       CASE WHEN COL_LENGTH('qms.qms_quality_order', 'container_rejected') IS NULL
            THEN 'MISSING' ELSE 'present' END
UNION ALL
SELECT 'status_check_allows_rejected',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints cc
                         JOIN sys.tables t ON t.object_id = cc.parent_object_id
                         WHERE cc.name = 'CK_qms_arrival_status'
                           AND SCHEMA_NAME(t.schema_id) = 'qms'
                           AND cc.definition LIKE '%Rejected%')
            THEN 'yes' ELSE 'NO' END;
-- Expect 3 / present / yes.
GO
