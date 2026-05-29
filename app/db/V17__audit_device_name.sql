-- ===================================================================
-- V17  Audit Trail: capture the device (host) name alongside IP + UA
--
-- Adds a nullable source_device_name column to qms_audit_log. Populated
-- at write time by AuditContextActionFilter via a reverse-DNS lookup of
-- the remote IP, cached in IMemoryCache. Pre-V17 audit rows have NULL;
-- the UI falls back to the User-Agent-based "Browser / OS" label for
-- those entries (so the column is never blank).
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_device_name')
BEGIN
    ALTER TABLE qms_audit_log
        ADD source_device_name NVARCHAR(255) NULL;
END
GO

SELECT 'qms_audit_log.source_device_name' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_device_name')
            THEN 'OK' ELSE 'MISSING' END AS status;
GO
