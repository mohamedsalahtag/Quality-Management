-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M12  -  transit_days on the container cache
-- ============================================================================
--  SAP's ZQC_Data already returns Transit_Days and the app already maps it
--  (SapShipmentRow.TransitDays), but on the pending-containers path the value
--  only ever landed inside payload_json. The Pending Containers list now shows
--  a Transit column, so promote it to a typed column like every other
--  displayed field (po_type, sto, doc_date, ...).
--
--  Reading it as MAX(JSON_VALUE(payload_json, ...)) inside the page's GROUP BY
--  would force an off-row LOB read per row on every load; a SMALLINT is 2
--  bytes in-row and sorts natively.
--
--  The backfill recovers the value for rows already cached. Rows touched by a
--  later SAP pull get it from the MERGE in ContainerCacheService -- note that
--  MERGE sets it in BOTH the MATCHED and NOT MATCHED branches, otherwise
--  existing rows would never receive it and this backfill would mask the bug.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_sap_container_cache', 'transit_days') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD transit_days SMALLINT NULL;
GO

-- One-shot backfill from the JSON payload already stored on each row.
-- PascalCase path: the cache serialises SapShipmentRow with no naming policy.
UPDATE qms.qms_sap_container_cache
SET    transit_days = TRY_CONVERT(SMALLINT, JSON_VALUE(payload_json, '$.TransitDays'))
WHERE  transit_days IS NULL
  AND  payload_json IS NOT NULL
  AND  ISJSON(payload_json) = 1
  AND  JSON_VALUE(payload_json, '$.TransitDays') IS NOT NULL;
GO

SELECT 'cache_with_transit_days' AS check_item, COUNT(*) AS value
FROM   qms.qms_sap_container_cache WHERE transit_days IS NOT NULL;

-- Expect 0: every cached row whose payload carries a transit time now has it.
SELECT 'cache_transit_unbackfilled' AS check_item, COUNT(*) AS value
FROM   qms.qms_sap_container_cache
WHERE  transit_days IS NULL
  AND  payload_json IS NOT NULL
  AND  ISJSON(payload_json) = 1
  AND  JSON_VALUE(payload_json, '$.TransitDays') IS NOT NULL;
