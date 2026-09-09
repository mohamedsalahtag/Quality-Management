-- ===================================================================
-- V43  Archive flag for pending SAP containers
--
-- The pending list accumulates containers that will never become an
-- Arrival — cancelled shipments, old PO lines SAP keeps returning,
-- anything from before the QMS go-live. They cannot be deleted (the
-- next SAP sweep re-inserts them), so archiving is a flag the sweep
-- never touches, exactly like override_plant in V40.
--
--   * archived_at  -- when it was archived (UTC). NULL = still pending.
--   * archived_by  -- who archived it (audit provenance)
--
-- A container is archived per (container_no, bol_no, ebeln) triplet:
-- every cache row in the triplet is stamped together, so the pending
-- list — which groups by that triplet — never shows a half-archived
-- container. Restoring clears both columns.
--
-- Idempotent.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID(N'dbo.qms_sap_container_cache')
      AND  name      = N'archived_at')
BEGIN
    ALTER TABLE dbo.qms_sap_container_cache
        ADD archived_at DATETIME2     NULL,
            archived_by NVARCHAR(256) NULL;
END;
GO

-- Filtered index: the pending list adds "archived_at IS NULL" to every
-- load and the archive view filters "IS NOT NULL". Only archived rows
-- need indexing — they are the minority and the one the archive view seeks.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_qms_sap_container_cache_archived')
    CREATE INDEX IX_qms_sap_container_cache_archived
        ON dbo.qms_sap_container_cache(archived_at)
        INCLUDE (container_no, bol_no, ebeln)
        WHERE archived_at IS NOT NULL;
GO
