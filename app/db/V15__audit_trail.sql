-- ===================================================================
-- V15  Audit Trail feature
--
-- Extends qms_audit_log (introduced in V02) with the source_user_agent
-- column + a composite filter index for the global audit log read path.
-- Adds Auditor to the Users role CHECK constraint. No data migration
-- required.
--
-- Idempotent: every change is guarded by IF EXISTS / IF NOT EXISTS so
-- re-runs are safe. Mirrors the V12 (role overhaul) + V13 (ClaimManager)
-- pattern for the CK_Users_Role swap.
-- ===================================================================

-- 1. Add user-agent column (existing rows get NULL -- backward compatible).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_user_agent')
BEGIN
    ALTER TABLE qms_audit_log
        ADD source_user_agent NVARCHAR(500) NULL;
END
GO

-- 2. Filter / pagination index for the global audit log page (SC-008).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_qms_audit_log_filter'
      AND object_id = OBJECT_ID('qms_audit_log'))
BEGIN
    CREATE INDEX IX_qms_audit_log_filter
        ON qms_audit_log(changed_at DESC)
        INCLUDE (entity_type, action_code, changed_by);
END
GO

-- 3. Extend CK_Users_Role to allow 'Auditor' (same idempotent pattern as V12/V13).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Auditor','Operator','Viewer'));
GO

-- 4. Sanity report.
SELECT 'qms_audit_log.source_user_agent' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_user_agent')
            THEN 'OK' ELSE 'MISSING' END AS status
UNION ALL
SELECT 'IX_qms_audit_log_filter',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'IX_qms_audit_log_filter') THEN 'OK' ELSE 'MISSING' END
UNION ALL
SELECT 'CK_Users_Role contains Auditor',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE name = 'CK_Users_Role' AND definition LIKE '%Auditor%')
            THEN 'OK' ELSE 'MISSING' END;
GO
