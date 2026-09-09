-- ===================================================================
-- V50  Rejected containers
--
-- Rejecting an arrival whose container is in bad condition moves it to
-- the terminal status 'Rejected' and raises a quality order that is
-- already Closed, carrying a potential claim, so the damage still
-- reaches the supplier and the claim process without an inspection
-- that has nothing to inspect.
--
--   * CK_qms_arrival_status widened to allow 'Rejected'
--   * qms_arrival.rejected_at / rejected_by / reject_reason
--   * qms_quality_order.container_rejected (stored, not derived: a
--     printed report must look the same forever)
--
-- Apply before deploying — container_rejected joins the shared
-- quality-order projection.
--
-- Idempotent.
-- ===================================================================

IF EXISTS (SELECT 1 FROM sys.check_constraints cc
           JOIN sys.tables t ON t.object_id = cc.parent_object_id
           WHERE cc.name = N'CK_qms_arrival_status'
             AND t.name = N'qms_arrival' AND SCHEMA_NAME(t.schema_id) = N'dbo')
    ALTER TABLE dbo.qms_arrival DROP CONSTRAINT CK_qms_arrival_status;
GO

IF OBJECT_ID(N'dbo.qms_arrival') IS NOT NULL
    ALTER TABLE dbo.qms_arrival ADD CONSTRAINT CK_qms_arrival_status
        CHECK (status_code IN ('Draft', 'Completed', 'Cancelled', 'Rejected'));
GO

IF OBJECT_ID(N'dbo.qms_arrival') IS NOT NULL AND COL_LENGTH('dbo.qms_arrival', 'rejected_at') IS NULL
    ALTER TABLE dbo.qms_arrival
        ADD rejected_at DATETIME2 NULL, rejected_by NVARCHAR(80) NULL, reject_reason NVARCHAR(500) NULL;
GO

IF OBJECT_ID(N'dbo.qms_quality_order') IS NOT NULL AND COL_LENGTH('dbo.qms_quality_order', 'container_rejected') IS NULL
    ALTER TABLE dbo.qms_quality_order ADD container_rejected BIT NOT NULL
        CONSTRAINT DF_qms_quality_order_container_rejected DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.qms_quality_order') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_qms_quality_order_container_rejected')
    CREATE INDEX IX_qms_quality_order_container_rejected
        ON dbo.qms_quality_order(container_rejected)
        INCLUDE (arrival_id, status_code)
        WHERE container_rejected = 1;
GO
