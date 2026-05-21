-- ===================================================================
-- V08  qms_sap_material_cache: drop the two columns that are no longer
-- read or written by any code path.
--
--  * payload_json was the original raw-blob cache; V07 backfilled every
--    field into a typed column and the readers (MaraService,
--    HybridSapClient) stopped touching the blob. The sync writer is
--    updated in the same commit to stop populating it.
--  * source_id was never read or written by any C# code (a legacy field
--    from the V04 cache template). Removing it shrinks the row.
--
-- Also widens the material_group index with an INCLUDE so the defect-
-- catalog dropdown can satisfy the query from the index alone without
-- the per-row key lookup that was making the page slow.
-- ===================================================================

IF COL_LENGTH('qms_sap_material_cache','payload_json') IS NOT NULL
    ALTER TABLE qms_sap_material_cache DROP COLUMN payload_json;
GO

IF COL_LENGTH('qms_sap_material_cache','source_id') IS NOT NULL
    ALTER TABLE qms_sap_material_cache DROP COLUMN source_id;
GO

-- Recreate the group index with material_group_desc INCLUDEd so the
-- ListMaterialGroupsAsync query becomes index-only (no key lookups).
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_sap_material_cache_group' AND object_id = OBJECT_ID('qms_sap_material_cache'))
    DROP INDEX IX_qms_sap_material_cache_group ON qms_sap_material_cache;
GO
CREATE INDEX IX_qms_sap_material_cache_group
    ON qms_sap_material_cache(material_group)
    INCLUDE (material_group_desc);
GO

-- Sanity report.
SELECT
    (SELECT COUNT(*) FROM qms_sap_material_cache)                                                          AS total_rows,
    (SELECT COUNT(DISTINCT material_group) FROM qms_sap_material_cache WHERE material_group IS NOT NULL)  AS distinct_groups,
    (SELECT TOP 1 name FROM sys.columns WHERE object_id = OBJECT_ID('qms_sap_material_cache') AND name='payload_json') AS payload_json_still_there,
    (SELECT TOP 1 name FROM sys.columns WHERE object_id = OBJECT_ID('qms_sap_material_cache') AND name='source_id')    AS source_id_still_there;
