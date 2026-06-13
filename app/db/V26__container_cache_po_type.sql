-- ===================================================================
-- V26  Add po_type to qms_sap_container_cache.
--
-- The CDS view ZQC_Data exposes EKKO.BSART as PO_Type ("Document Type"
-- in SAP terminology: NB = standard PO, ZB = subcontract, etc.).
-- Showing it on /Arrivals/Pending lets the operator distinguish
-- regular purchase orders from special / consignment / framework
-- orders before deciding to create a QMS Arrival.
--
-- Nullable for backwards compatibility -- existing cache rows stay
-- as-is; the next UPSERT pass populates them.
-- Idempotent.
-- ===================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.qms_sap_container_cache')
      AND name = N'po_type'
)
BEGIN
    ALTER TABLE dbo.qms_sap_container_cache
        ADD po_type VARCHAR(4) NULL;
END;
