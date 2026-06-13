-- One-shot cleanup: mark any in-flight ContainerCache pulls as cancelled.
-- Safe to re-run; only touches rows where completed_at IS NULL.
UPDATE qms_sap_sync_log
SET    completed_at = SYSUTCDATETIME(),
       success      = 0,
       message      = COALESCE(message, '') + ' [cleared: stale in-flight row]'
WHERE  endpoint_key = 'ContainerCache'
  AND  completed_at IS NULL;
SELECT @@ROWCOUNT AS cleared;
