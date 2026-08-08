-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M15  -  plant override on the container cache
-- ============================================================================
--  Lets a QC Manager / Admin reassign a pending container to a different plant
--  so the resulting Arrival + Quality Order are created there, and the
--  container becomes visible to the target plant's users.
--
--  The SAP `plant` column is part of the UPSERT MERGE key, so it is left
--  untouched -- overwriting it would make the next sweep re-INSERT the row
--  under the original plant. The override lives in its own column; effective
--  plant = COALESCE(override_plant, plant). ContainerCacheService.UpsertAsync
--  never touches override_plant, so the override survives every SAP re-sync.
--
--    * override_plant       -- target plant (NULL = use SAP's plant)
--    * override_plant_by    -- who set it (audit provenance)
--    * override_plant_at    -- when it was set (UTC)
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_sap_container_cache', 'override_plant') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD override_plant VARCHAR(4) NULL;
GO

IF COL_LENGTH('qms.qms_sap_container_cache', 'override_plant_by') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD override_plant_by NVARCHAR(256) NULL;
GO

IF COL_LENGTH('qms.qms_sap_container_cache', 'override_plant_at') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD override_plant_at DATETIME2 NULL;
GO

SELECT 'cache_override_columns' AS check_item, COUNT(*) AS value
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_sap_container_cache')
  AND  name IN ('override_plant', 'override_plant_by', 'override_plant_at');
-- Expect 3.
