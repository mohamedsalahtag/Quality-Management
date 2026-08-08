-- ===================================================================
-- V40  Plant override for pending SAP containers
--
-- Lets a QC Manager / Admin reassign a pending container to a different
-- plant so the resulting Arrival + Quality Order are created there, and
-- the container becomes visible to the target plant's users.
--
-- The SAP `plant` column is left untouched (it is part of the natural
-- key the UPSERT MERGE matches on -- overwriting it would make the next
-- sweep re-INSERT the row under the original plant). The override lives
-- in a separate column; effective plant = COALESCE(override_plant, plant).
-- UpsertAsync never touches override_plant, so the override survives every
-- SAP re-sync.
--
--   * override_plant       -- target plant (NULL = use SAP's plant)
--   * override_plant_by    -- who set it (audit provenance)
--   * override_plant_at    -- when it was set (UTC)
--
-- Idempotent.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID(N'dbo.qms_sap_container_cache')
      AND  name      = N'override_plant')
BEGIN
    ALTER TABLE dbo.qms_sap_container_cache
        ADD override_plant    VARCHAR(4)    NULL,
            override_plant_by  NVARCHAR(256) NULL,
            override_plant_at  DATETIME2     NULL;
END;
