-- ===================================================================
-- V28  qms_sap_container_cache  — add `sto` column
--
-- Context: the SAP ZQC_Data endpoint was reshaped so that
--   * Field aliased as `ShipmentNo` now carries the actual PO number.
--   * Field `PO_Number` now carries the STO (Stock Transport Order).
-- The QMS triplet key (container_no, bol_no, ebeln) is repointed at
-- ShipmentNo (mapped into Ebeln by HybridSapClient.MapRow), and the
-- STO is persisted in this new column for a parallel display column
-- on /Arrivals/Pending. No filter or index on STO is needed yet.
--
-- Idempotent.
-- ===================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID(N'dbo.qms_sap_container_cache')
      AND  name      = N'sto')
BEGIN
    ALTER TABLE dbo.qms_sap_container_cache
        ADD sto VARCHAR(10) NULL;
END;
