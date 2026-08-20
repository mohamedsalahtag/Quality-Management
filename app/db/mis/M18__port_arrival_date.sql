-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M18  -  true port arrival date
-- ============================================================================
--  The QC report's "Arrival Date" printed the SAP Receive_Date (goods receipt),
--  because HybridSapClient maps BOTH ArrivalDate and ReceiveDate from
--  Receive_Date -- a deliberate 2026 choice for the /Arrivals/Pending grid,
--  since ZQC_Data.Arrival_Date is sparse. Measured 2026-08-20 over 1,000 live
--  rows: Arrival_Date 33% populated, Receive_Date 100%.
--
--  The report now needs the REAL port arrival date, blank when SAP has none.
--  That value was never stored, so it gets its own column rather than
--  overwriting arrival_date -- /Arrivals/Pending keeps reading Receive_Date
--  exactly as before, and no existing value is touched.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF COL_LENGTH('qms.qms_sap_container_cache', 'port_arrival_date') IS NULL
    ALTER TABLE qms.qms_sap_container_cache ADD port_arrival_date DATE NULL;
GO

SELECT 'cache_rows'                AS check_item, COUNT(*) AS value FROM qms.qms_sap_container_cache
UNION ALL
SELECT 'port_arrival_date_filled', COUNT(port_arrival_date) FROM qms.qms_sap_container_cache;
GO
