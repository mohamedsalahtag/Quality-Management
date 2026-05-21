SELECT
  (SELECT COUNT(*) FROM qms_sap_material_cache) AS materials_cached,
  (SELECT COUNT(*) FROM qms_sap_vendor_cache)   AS vendors_cached,
  (SELECT COUNT(*) FROM qms_sap_sync_log)       AS sync_log_rows,
  (SELECT TOP 1 ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Url.MaterialMaster')   AS material_url,
  (SELECT TOP 1 ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Sync.MaterialMaster.LastResult')   AS material_last,
  (SELECT TOP 1 ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Sync.MaterialMaster.LastRowCount') AS material_rows,
  (SELECT TOP 1 ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Sync.MaterialMaster.Hours')        AS material_hours,
  (SELECT TOP 1 ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Sync.MaterialMaster.Enabled')      AS material_enabled;
