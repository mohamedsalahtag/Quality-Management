-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M13  -  Arrival storage location + PO type
-- ============================================================================
--  qms_arrival carried plant but not storage location, and PO type (SAP
--  EKKO.BSART -- ZFAS / ZCON / ZFFP) existed nowhere outside the container
--  cache. Both are now shown as names on the Arrivals list and filtered on
--  from the Quality Orders list.
--
--  Storage location does exist per line on qms_arrival_item, but filtering the
--  QO list through the item table needs an EXISTS sub-query per row and can
--  never be indexed usefully. In practice every line of an arrival lands in
--  the same storage location, so denormalise it onto the header.
--
--  Backfill order:
--    1. the SAP container-cache row for the same container/BOL/PO triplet
--       (authoritative, and the only source of po_type);
--    2. failing that, the arrival's own line items (storage location only) --
--       covers arrivals created via the "search SAP directly" path, which
--       never passed through the cache.
--  Arrivals matching neither stay NULL and render as a blank cell.
--
--  ArrivalService.CreateAsync populates both columns going forward.
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_arrival', 'storage_location') IS NULL
    ALTER TABLE qms.qms_arrival ADD storage_location VARCHAR(4) NULL;
GO

IF COL_LENGTH('qms.qms_arrival', 'po_type') IS NULL
    ALTER TABLE qms.qms_arrival ADD po_type VARCHAR(4) NULL;
GO

-- 1. From the container cache, matched on the arrival-creation triplet.
UPDATE a
SET    a.po_type          = COALESCE(a.po_type,          NULLIF(x.po_type, '')),
       a.storage_location = COALESCE(a.storage_location, NULLIF(x.storage_loc, ''))
FROM   qms.qms_arrival a
CROSS  APPLY (SELECT TOP (1) c.po_type, c.storage_loc
              FROM   qms.qms_sap_container_cache c
              WHERE  ISNULL(c.container_no, '') = ISNULL(a.container_no, '')
                AND  ISNULL(c.bol_no, '')       = ISNULL(a.bol_no, '')
                AND  ISNULL(c.ebeln, '')        = ISNULL(a.ebeln, '')
              ORDER  BY c.cache_id) x
WHERE  a.po_type IS NULL OR a.storage_location IS NULL;
GO

-- 2. Storage location from the arrival's own line items for anything left.
UPDATE a
SET    a.storage_location = x.storage_location
FROM   qms.qms_arrival a
CROSS  APPLY (SELECT MIN(i.storage_location) AS storage_location
              FROM   qms.qms_arrival_item i
              WHERE  i.arrival_id = a.arrival_id
                AND  i.storage_location IS NOT NULL
                AND  i.storage_location <> '') x
WHERE  a.storage_location IS NULL
  AND  x.storage_location IS NOT NULL;
GO

SELECT 'arrival_total'        AS check_item, COUNT(*) AS value FROM qms.qms_arrival;
SELECT 'arrival_with_storage' AS check_item, COUNT(*) AS value FROM qms.qms_arrival WHERE storage_location IS NOT NULL;
SELECT 'arrival_with_po_type' AS check_item, COUNT(*) AS value FROM qms.qms_arrival WHERE po_type IS NOT NULL;
