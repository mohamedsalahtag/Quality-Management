-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M25  -  archive flag on the container cache
-- ============================================================================
--  The pending list accumulates containers that will never become an Arrival --
--  cancelled shipments, old PO lines SAP keeps returning, anything from before
--  the QMS go-live. They cannot be deleted: the next SAP sweep re-inserts them.
--  So archiving is a flag the sweep never touches, exactly like override_plant
--  in M15. ContainerCacheService.UpsertAsync leaves both columns alone, so an
--  archived container stays archived across every re-sync.
--
--    * archived_at  -- when it was archived (UTC). NULL = still pending.
--    * archived_by  -- who archived it (audit provenance)
--
--  A container is archived per (container_no, bol_no, ebeln) triplet: every
--  cache row in the triplet is stamped together, so the pending list -- which
--  groups by that triplet -- never shows a half-archived container. Restoring
--  clears both columns.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_sap_container_cache', 'archived_at') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD archived_at DATETIME2 NULL;
GO

IF COL_LENGTH('qms.qms_sap_container_cache', 'archived_by') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD archived_by NVARCHAR(256) NULL;
GO

-- Filtered index: the pending list adds "archived_at IS NULL" to every load
-- and the archive view filters "IS NOT NULL". Only archived rows need
-- indexing -- they are the minority, and the one the archive view seeks.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_sap_container_cache_archived')
    CREATE INDEX IX_qms_sap_container_cache_archived
        ON qms.qms_sap_container_cache(archived_at)
        INCLUDE (container_no, bol_no, ebeln)
        WHERE archived_at IS NOT NULL;
GO

SELECT 'cache_archive_columns' AS check_item, COUNT(*) AS value
FROM   sys.columns
WHERE  object_id = OBJECT_ID('qms.qms_sap_container_cache')
  AND  name IN ('archived_at', 'archived_by')
UNION ALL
SELECT 'archived_rows', COUNT(*) FROM qms.qms_sap_container_cache WHERE archived_at IS NOT NULL
UNION ALL
SELECT 'pending_rows',  COUNT(*) FROM qms.qms_sap_container_cache WHERE has_arrival = 0 AND archived_at IS NULL;
-- Expect cache_archive_columns = 2.
GO
