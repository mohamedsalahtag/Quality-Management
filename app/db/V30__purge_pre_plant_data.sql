-- ===================================================================
-- V30  One-shot purge: wipe arrivals, quality orders, claims, the SAP
-- container cache, and reset every related sequence + identity, so the
-- new plant-scoping logic (V29 + app code) starts on an empty dataset.
--
-- Idempotent: re-running on already-empty tables is a no-op. Cascades
-- (qms_claim_note, qms_claim_read_marker, qms_sample_header_value,
-- qms_qo_material_header_value) fire automatically via their existing
-- ON DELETE CASCADE FKs.
--
-- Audit/log trails are PRESERVED:
--   * qms_audit_log
--   * qms_status_history
--   * qms_sap_sync_log
-- ===================================================================

SET NOCOUNT ON;

-- Image links that point at the wiped entities. Image asset blobs
-- (qms_image_asset) are intentionally kept -- they're addressable by
-- hash and orphaned rows are harmless until a future GC pass.
DELETE FROM qms_image_link
WHERE  owner_type IN ('Arrival','ArrivalChecklist','QualityOrder','QualityOrderMaterial','Sample');

-- Claims (claim_note + claim_read_marker cascade via FK).
DELETE FROM qms_claim;

-- Report generation history (FK to qms_quality_order).
DELETE FROM qms_report_log;

-- Sample-level rows (qms_sample_header_value cascades from qms_sample).
DELETE FROM qms_sample_defect;
DELETE FROM qms_sample_reading;
DELETE FROM qms_sample_observation;
DELETE FROM qms_sample;

-- QO material rows (qms_qo_material_header_value cascades).
DELETE FROM qms_quality_order_material;

-- QO header.
DELETE FROM qms_quality_order;

-- SAP container cache: entire table, including the 8 fossil arrived rows
-- (their ebeln value reflects the old PO_Number-as-PO mapping; the next
-- SAP pull repopulates under the current ShipmentNo-as-PO logic).
DELETE FROM qms_sap_container_cache;

-- Arrival subtables (no cascades from qms_arrival).
DELETE FROM qms_arrival_sap_snapshot;
DELETE FROM qms_arrival_checklist;
DELETE FROM qms_shipment_snapshot;
DELETE FROM qms_arrival_item;

-- Arrival header.
DELETE FROM qms_arrival;

-- Sequences -- next NEXT VALUE starts at 1, so the first arrival/QO/
-- shipment number after purge is ARR-2026-000001, QO-2026-000001,
-- SHP-2026-000001 (the year prefix comes from the app, not SQL).
ALTER SEQUENCE seq_qms_arrival_no       RESTART WITH 1;
ALTER SEQUENCE seq_qms_quality_order_no RESTART WITH 1;
ALTER SEQUENCE seq_qms_shipment_no      RESTART WITH 1;

-- IDENTITY reseed: next INSERT into each table allocates 1, 2, 3, ...
-- Guarded so the reseed only fires when the table is actually empty
-- (DBCC CHECKIDENT on a non-empty table would shift live IDs).
DECLARE @c INT;
SELECT @c = COUNT(*) FROM qms_arrival;            IF @c = 0 DBCC CHECKIDENT('qms_arrival', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_arrival_item;       IF @c = 0 DBCC CHECKIDENT('qms_arrival_item', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_arrival_checklist;  IF @c = 0 DBCC CHECKIDENT('qms_arrival_checklist', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_arrival_sap_snapshot; IF @c = 0 DBCC CHECKIDENT('qms_arrival_sap_snapshot', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_shipment_snapshot;  IF @c = 0 DBCC CHECKIDENT('qms_shipment_snapshot', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_quality_order;      IF @c = 0 DBCC CHECKIDENT('qms_quality_order', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_quality_order_material; IF @c = 0 DBCC CHECKIDENT('qms_quality_order_material', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_sample;             IF @c = 0 DBCC CHECKIDENT('qms_sample', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_sample_reading;     IF @c = 0 DBCC CHECKIDENT('qms_sample_reading', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_sample_defect;      IF @c = 0 DBCC CHECKIDENT('qms_sample_defect', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_sample_observation; IF @c = 0 DBCC CHECKIDENT('qms_sample_observation', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_claim;              IF @c = 0 DBCC CHECKIDENT('qms_claim', RESEED, 0) WITH NO_INFOMSGS;
SELECT @c = COUNT(*) FROM qms_sap_container_cache; IF @c = 0 DBCC CHECKIDENT('qms_sap_container_cache', RESEED, 0) WITH NO_INFOMSGS;

-- Sanity report.
SELECT 'qms_arrival'                AS tbl, COUNT(*) AS rows FROM qms_arrival       UNION ALL
SELECT 'qms_arrival_item',          COUNT(*) FROM qms_arrival_item                  UNION ALL
SELECT 'qms_quality_order',         COUNT(*) FROM qms_quality_order                 UNION ALL
SELECT 'qms_quality_order_material',COUNT(*) FROM qms_quality_order_material        UNION ALL
SELECT 'qms_sample',                COUNT(*) FROM qms_sample                        UNION ALL
SELECT 'qms_claim',                 COUNT(*) FROM qms_claim                         UNION ALL
SELECT 'qms_sap_container_cache',   COUNT(*) FROM qms_sap_container_cache;
