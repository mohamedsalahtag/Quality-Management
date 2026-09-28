-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M38  -  vessel arrival for uncached arrivals
-- ============================================================================
--  M36 filled qms_shipment_snapshot.port_arrival_date (the vessel reaching port,
--  SAP Arrival_Date) from the SAP container cache only. An arrival created
--  through "Search SAP directly" before its goods receipt was posted never
--  reaches the cache -- the sweep only fetches rows that have a Receive_Date --
--  so it was left blank even though SAP had supplied the date when the arrival
--  was created (e.g. QO-2026-002935 / MMAU1500290: SAP Arrival_Date 2026-09-18).
--
--  This fills the gap from the arrival's OWN SAP payload, captured at creation
--  in qms_arrival_sap_snapshot. Only blank values are filled; nothing is
--  overwritten. Arrivals created after the 2026-09-26 deploy already store the
--  date at creation (ArrivalService.CreateFromSapAsync).
--
--  Idempotent: safe to re-run.
-- ============================================================================

UPDATE ss
SET    ss.port_arrival_date = TRY_CONVERT(date, JSON_VALUE(sap.payload_json, '$[0].PortArrivalDate'))
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_arrival_sap_snapshot sap ON sap.arrival_id = ss.arrival_id
WHERE  ss.port_arrival_date IS NULL
  AND  ISJSON(sap.payload_json) = 1
  AND  TRY_CONVERT(date, JSON_VALUE(sap.payload_json, '$[0].PortArrivalDate')) IS NOT NULL;
GO

-- Expect 0: no snapshot is blank where its own SAP payload has the date.
SELECT 'still_recoverable' AS check_item, COUNT(*) AS value
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_arrival_sap_snapshot sap ON sap.arrival_id = ss.arrival_id
WHERE  ss.port_arrival_date IS NULL
  AND  ISJSON(sap.payload_json) = 1
  AND  TRY_CONVERT(date, JSON_VALUE(sap.payload_json, '$[0].PortArrivalDate')) IS NOT NULL
UNION ALL
SELECT 'qo_2026_002935_vessel_arrival_filled', COUNT(*)
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_quality_order qo ON qo.arrival_id = ss.arrival_id
WHERE  qo.quality_order_no = 'QO-2026-002935' AND ss.port_arrival_date IS NOT NULL;
GO
