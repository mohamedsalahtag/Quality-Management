-- Mark any in-flight sync log rows as cancelled (their host process has stopped).
UPDATE qms_sap_sync_log
SET completed_at = SYSUTCDATETIME(),
    success      = 0,
    message      = 'Cancelled (host process stopped before completion)'
WHERE completed_at IS NULL;

-- Also clear any sync settings that point at the cancelled run.
UPDATE SiteConfiguration
SET ConfigValue = 'CANCELLED: previous sync did not complete'
WHERE ConfigKey = 'Sap.Sync.MaterialMaster.LastResult'
  AND (ConfigValue IS NULL OR ConfigValue = '');

UPDATE SiteConfiguration
SET ConfigValue = (SELECT CAST(COUNT(*) AS NVARCHAR(20)) FROM qms_sap_material_cache)
WHERE ConfigKey = 'Sap.Sync.MaterialMaster.LastRowCount';
