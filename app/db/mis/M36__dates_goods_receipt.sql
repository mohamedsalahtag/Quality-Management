-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M36  -  one meaning per shipment date
-- ============================================================================
--  What was wrong, measured against live data on 2026-09-26:
--
--    * SAP's Receive_Date MOVES. It first appears when the container is pulled
--      out of the port and is then advanced to the day the BRANCH books the
--      goods in -- on average four days later, on 1,180 of the 2,268 arrivals
--      with a finished QC (52%). The arrival snapshot copied it once at arrival
--      creation and never looked again, so the arrival page, the QO page, the
--      claims list, the QC report and the dashboard showed the pull-out date
--      while the pending list and the Time Bar page (which read the live SAP
--      cache) showed the goods receipt.
--    * The snapshot's arrival_date has always been a COPY of receive_date
--      (2,642 of 2,642 identical). The vessel's real arrival at port (SAP
--      Arrival_Date) was cached in M18 but never stored on the arrival.
--    * The QC report's Time Bar counted from the inspector's discharge date;
--      the Time Bar page counted from the goods receipt. Same container, two
--      different "time bars".
--    * Transit days were SAP's Transit_Days, which is Arrival_Date minus
--      Sailing_Date -- and Arrival_Date equals SAP's Estimated_Arrival_Date on
--      99.6% of rows, so it was an ETA-based figure. The business defines
--      transit as loading to the VESSEL DISCHARGE the inspector records.
--    * The Data Hub's "Loading Date" was always blank: SAP's LoadingDate column
--      is empty on every one of 25,120 rows, and the snapshot never wrote it.
--
--  What this migration does:
--
--    1. Adds qms_shipment_snapshot.port_arrival_date (the vessel reaching port).
--    2. Backs up the snapshot dates it is about to change into
--       qms.qms_shipment_snapshot_dates_m36, so the change is reversible.
--    3. Refreshes every snapshot's receive_date (and its twin arrival_date),
--       port_arrival_date and SAP transit_days from the SAP cache -- the same
--       statement ContainerCacheService.RefreshArrivalSnapshotsAsync now runs
--       at the end of every sweep.
--    4. Re-creates vw_qms_flat_defects: Loading Date = sailing date, transit =
--       loading -> discharge with SAP's figure as fallback, both Time Bar
--       columns counted to the QO's LOCAL finish date, and a new
--       PortArrivalDate column (appended; nothing existing moves).
--    5. Retires the report-only setting Report.TimeBarBasis: the report now
--       reads the Time Bar page's basis (goods receipt by default).
--    6. Seeds the new UI captions so they appear on Admin -> Labels.
--
--  Effect a reader will notice: monthly "Received" counts on the dashboard
--  shift for containers pulled out at the end of one month and booked in at
--  the start of the next -- they now count in the month the branch received
--  them, which is what the number claims to be.
--
--  Idempotent: safe to re-run. The backup table only ever gains rows.
-- ============================================================================

-- 1. The vessel's arrival at port, on the snapshot.
IF COL_LENGTH('qms.qms_shipment_snapshot', 'port_arrival_date') IS NULL
    ALTER TABLE qms.qms_shipment_snapshot ADD port_arrival_date date NULL;
GO

-- 2. Backup of the values about to change. One row per arrival, taken the
--    first time this script sees it change; re-runs never overwrite it.
IF OBJECT_ID('qms.qms_shipment_snapshot_dates_m36') IS NULL
    CREATE TABLE qms.qms_shipment_snapshot_dates_m36 (
        arrival_id   bigint    NOT NULL PRIMARY KEY,
        arrival_date date      NULL,
        receive_date date      NULL,
        transit_days smallint  NULL,
        backed_up_at datetime2 NOT NULL DEFAULT (SYSUTCDATETIME())
    );
GO

;WITH k AS (
    SELECT container_no, bol_no, ebeln,
           MAX(receive_date)      AS receive_date,
           MAX(port_arrival_date) AS port_arrival_date,
           MAX(transit_days)      AS transit_days
    FROM   qms.qms_sap_container_cache
    GROUP  BY container_no, bol_no, ebeln
)
INSERT INTO qms.qms_shipment_snapshot_dates_m36 (arrival_id, arrival_date, receive_date, transit_days)
SELECT ss.arrival_id, ss.arrival_date, ss.receive_date, ss.transit_days
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_arrival a ON a.arrival_id = ss.arrival_id
JOIN   k ON k.container_no = ISNULL(a.container_no, '')
        AND k.bol_no       = ISNULL(a.bol_no, '')
        AND k.ebeln        = ISNULL(a.ebeln, '')
WHERE  ((k.receive_date IS NOT NULL AND (ss.receive_date IS NULL OR ss.receive_date <> k.receive_date
                                       OR ss.arrival_date IS NULL OR ss.arrival_date <> k.receive_date))
     OR (k.transit_days IS NOT NULL AND (ss.transit_days IS NULL OR ss.transit_days <> k.transit_days)))
  AND NOT EXISTS (SELECT 1 FROM qms.qms_shipment_snapshot_dates_m36 b WHERE b.arrival_id = ss.arrival_id);
GO

-- 3. The refresh itself. Identical to RefreshArrivalSnapshotsAsync in
--    ContainerCacheService; keep the two in step. A cache line that has lost
--    its date is ignored, never copied: a snapshot is never blanked.
;WITH k AS (
    SELECT container_no, bol_no, ebeln,
           MAX(receive_date)      AS receive_date,
           MAX(port_arrival_date) AS port_arrival_date,
           MAX(transit_days)      AS transit_days
    FROM   qms.qms_sap_container_cache
    GROUP  BY container_no, bol_no, ebeln
)
UPDATE ss
SET    ss.receive_date      = COALESCE(k.receive_date,      ss.receive_date),
       ss.arrival_date      = COALESCE(k.receive_date,      ss.arrival_date),
       ss.port_arrival_date = COALESCE(k.port_arrival_date, ss.port_arrival_date),
       ss.transit_days      = COALESCE(k.transit_days,      ss.transit_days)
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_arrival a ON a.arrival_id = ss.arrival_id
JOIN   k ON k.container_no = ISNULL(a.container_no, '')
        AND k.bol_no       = ISNULL(a.bol_no, '')
        AND k.ebeln        = ISNULL(a.ebeln, '')
WHERE  (k.receive_date      IS NOT NULL AND (ss.receive_date      IS NULL OR ss.receive_date      <> k.receive_date
                                          OR ss.arrival_date      IS NULL OR ss.arrival_date      <> k.receive_date))
   OR  (k.port_arrival_date IS NOT NULL AND (ss.port_arrival_date IS NULL OR ss.port_arrival_date <> k.port_arrival_date))
   OR  (k.transit_days      IS NOT NULL AND (ss.transit_days      IS NULL OR ss.transit_days      <> k.transit_days));
GO

-- 4. The flat view. Every existing column keeps its name and position; the
--    definitions of LoadingDate, ArrivalDate, ReceiveDate, TransitDays,
--    TimeBarDischarge and TimeBarArrival change, and PortArrivalDate is
--    appended. Time Bars are counted to the QO's LOCAL finish date (Arab
--    Standard Time, no DST) -- closed_at is UTC, and an order finished at 01:00
--    Riyadh time is 22:00 UTC the day before, which under-counted by a day.
--    Never negative, matching ShipmentDates.TimeBarDays.
CREATE OR ALTER VIEW dbo.vw_qms_flat_defects AS
SELECT
    s.sample_id                                                 AS SampleId,
    qo.quality_order_id                                         AS QualityOrderId,
    qo.quality_order_no                                         AS QualityOrderNo,
    qo.status_code                                              AS QoStatus,
    a.arrival_id                                                AS ArrivalId,
    a.arrival_no                                                AS ArrivalNo,
    a.plant                                                     AS Plant,
    a.container_no                                              AS ContainerNo,
    a.bol_no                                                    AS BolNo,
    a.ebeln                                                     AS Ebeln,
    a.vendor_no                                                 AS VendorNo,
    a.vendor_name                                               AS VendorName,
    ai.storage_location                                         AS StorageLocation,
    ai.quantity                                                 AS PoQuantity,
    cc.sto                                                      AS Sto,
    cc.doc_date                                                 AS PoDate,
    -- The branch goods receipt (SAP Receive_Date). The cache holds SAP's
    -- CURRENT value and leads; the snapshot, refreshed from it on every sweep,
    -- stands in for an arrival the cache has never seen. ArrivalDate is the
    -- same date -- the column was always Receive_Date's twin.
    COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) AS ArrivalDate,
    COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) AS ReceiveDate,
    ss.sailing_date                                             AS ShippingDate,
    -- SAP's LoadingDate is empty on every row; the loading date the business
    -- works from is Sailing_Date, so this column now carries it too.
    ss.sailing_date                                             AS LoadingDate,
    -- Transit = loading -> the inspector's discharge date, never negative;
    -- SAP's ETA-based Transit_Days until the discharge date is entered.
    CAST(COALESCE(
        CASE WHEN ss.sailing_date IS NOT NULL AND ss.discharge_date IS NOT NULL
             THEN CASE WHEN DATEDIFF(DAY, ss.sailing_date, ss.discharge_date) < 0 THEN 0
                       ELSE DATEDIFF(DAY, ss.sailing_date, ss.discharge_date) END END,
        cc.transit_days, ss.transit_days) AS SMALLINT)             AS TransitDays,
    m.material_no                                               AS MaterialNo,
    m.material_desc                                             AS MaterialDesc,
    m.material_group                                            AS MaterialGroup,
    m.material_group_desc                                       AS MaterialGroupDesc,
    COALESCE(NULLIF(m.major_category, N''), mc.major_category_desc) AS MajorCategory,
    mc.sub_major_category                                       AS SubMajorCategory,
    COALESCE(NULLIF(m.variety,        N''), mc.variety_name)    AS Variety,
    COALESCE(NULLIF(m.material_class, N''), mc.class_name)      AS MaterialClass,
    COALESCE(NULLIF(m.origin,         N''), mc.origin_name)     AS Origin,
    COALESCE(NULLIF(m.brand,          N''), mc.brand)           AS Brand,
    m.pack_type                                                 AS PackType,
    s.sample_no                                                 AS SampleNo,
    s.sample_scope                                              AS SampleScope,
    s.sample_size                                               AS SampleSize,
    dc.defect_code                                              AS DefectCode,
    dc.defect_name                                              AS DefectName,
    dc.defect_category                                          AS DefectCategory,
    sd.severity_code                                            AS SeverityCode,
    sd.defect_value                                             AS DefectValue,
    sd.defect_percentage                                        AS DefectPercentage,
    CASE WHEN ISNULL(s.sample_size, 0) > 0
         THEN CAST(sd.defect_value AS DECIMAL(18,4)) * 100.0 / s.sample_size
    END                                                         AS DefectRate,

    -- ---- M09 additions ------------------------------------------------------
    ss.discharge_date                                           AS DischargeDate,
    qo.opened_at                                                AS QoOpenedAt,
    qo.closed_at                                                AS QoClosedAt,
    qo.created_at                                               AS QoCreatedAt,
    -- Time Bar: whole days from the basis date to the QO's LOCAL finish date.
    -- Null until the QO is closed or when the basis date is missing.
    CASE WHEN DATEDIFF(DAY, ss.discharge_date,
                       CAST(qo.closed_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) < 0 THEN 0
         ELSE DATEDIFF(DAY, ss.discharge_date,
                       CAST(qo.closed_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) END
                                                                AS TimeBarDischarge,
    -- The figure the Time Bar page and the QC report print: receive date to
    -- QC finish.
    CASE WHEN DATEDIFF(DAY, COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date),
                       CAST(qo.closed_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) < 0 THEN 0
         ELSE DATEDIFF(DAY, COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date),
                       CAST(qo.closed_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) END
                                                                AS TimeBarArrival,
    m.net_weight                                                AS NetWeight,
    m.material_size                                             AS MaterialSize,
    m.sample_size                                               AS MaterialSampleSize,
    s.grower                                                    AS Grower,
    s.pallet_no                                                 AS PalletNo,
    s.grower_pallet                                             AS GrowerPallet,
    s.pack_code                                                 AS PackCode,
    s.date_code                                                 AS DateCode,
    s.label_value                                               AS LabelValue,
    s.lot_no                                                    AS LotNo,
    s.packaging_material                                        AS PackagingMaterial,
    s.created_at                                                AS SampleCreatedAt,
    s.created_by                                                AS SampleCreatedBy,

    -- ---- M36 addition -------------------------------------------------------
    -- The vessel reaching the port (SAP Arrival_Date), ETA-grade.
    COALESCE(cc.port_arrival_date, ss.port_arrival_date)        AS PortArrivalDate
FROM        dbo.qms_sample s
JOIN        dbo.qms_quality_order qo            ON qo.quality_order_id = s.quality_order_id
JOIN        dbo.qms_quality_order_material m    ON m.qo_material_id    = s.qo_material_id
JOIN        dbo.qms_arrival_item ai             ON ai.arrival_item_id  = m.arrival_item_id
JOIN        dbo.qms_arrival a                   ON a.arrival_id        = ai.arrival_id
LEFT JOIN   dbo.qms_shipment_snapshot ss        ON ss.arrival_id       = a.arrival_id
OUTER APPLY (
    SELECT TOP 1 c2.sto, c2.doc_date, c2.arrival_date, c2.receive_date,
                 c2.port_arrival_date, c2.transit_days
    FROM   dbo.qms_sap_container_cache c2
    WHERE  c2.container_no = a.container_no
      AND  c2.bol_no       = a.bol_no
      AND  c2.ebeln        = a.ebeln
    ORDER  BY c2.doc_date DESC, c2.sto
) cc
LEFT JOIN   dbo.qms_sap_material_cache mc       ON mc.material_no      = m.material_no
JOIN        dbo.qms_sample_defect sd            ON sd.sample_id        = s.sample_id
JOIN        dbo.qms_defect_catalog dc           ON dc.defect_id        = sd.defect_id
WHERE       s.is_deleted = 0;
GO

-- 5. The report no longer has a basis of its own.
DELETE FROM portal.SystemSetting WHERE SettingKey = N'qms.Report.TimeBarBasis';
GO

-- 6. New captions, so Admin -> Labels lists them before anyone renders them.
--    INSERT-if-missing only; nothing an administrator renamed is touched.
MERGE qms.qms_ui_label AS t
USING (VALUES
    (N'Vessel arrival date',        N'Arrivals',        N'Field label'),
    (N'Vessel Arrival Date',        N'Shared',          N'Field label'),
    (N'Vessel arrival',             N'TimeBar',         N'Column header'),
    (N'Vessel arrival:',            N'Arrivals',        N'Field label'),
    (N'Vessel',                     N'Arrivals',        N'Caption'),
    (N'Recv',                       N'Arrivals',        N'Caption'),
    (N'Received',                   N'TimeBar',         N'Column header'),
    (N'Received:',                  N'Arrivals',        N'Field label'),
    (N'Received from',              N'TimeBar',         N'Field label'),
    (N'Received to',                N'TimeBar',         N'Field label'),
    (N'Receive date from',          N'Arrivals',        N'Field label'),
    (N'Receive date to',            N'Arrivals',        N'Field label'),
    (N'Restore by receive date',    N'Arrivals',        N'Heading'),
    (N'Archive by receive date',    N'Arrivals',        N'Heading'),
    (N'Loading:',                   N'Arrivals',        N'Field label'),
    (N'From SAP (Sailing_Date)',    N'Arrivals',        N'Caption'),
    (N'From SAP (Arrival_Date)',    N'Arrivals',        N'Caption'),
    (N'Discharge date − loading date', N'Arrivals',     N'Caption'),
    (N'SAP''s estimate until the discharge date is entered', N'Arrivals', N'Caption'),
    (N'End of the transit leg: transit days = discharge − loading', N'Arrivals', N'Caption'),
    (N'Only count containers received on or after', N'Admin', N'Field label')
) AS s (label_key, screen_key, kind)
    ON t.label_key = s.label_key
WHEN NOT MATCHED THEN
    INSERT (label_key, screen_key, kind) VALUES (s.label_key, NULLIF(s.screen_key, N''), s.kind);
GO

-- ---- Checks ----------------------------------------------------------------
SELECT 'snapshot_port_arrival_column' AS check_item, COUNT(*) AS value
FROM   sys.columns WHERE object_id = OBJECT_ID('qms.qms_shipment_snapshot') AND name = 'port_arrival_date'
UNION ALL
SELECT 'snapshots_backed_up', COUNT(*) FROM qms.qms_shipment_snapshot_dates_m36
UNION ALL
SELECT 'snapshots_with_port_arrival', COUNT(port_arrival_date) FROM qms.qms_shipment_snapshot
-- Expect 0: every arrival the cache knows now carries SAP's current receive date.
UNION ALL
SELECT 'snapshots_still_behind_cache', COUNT(*)
FROM   qms.qms_shipment_snapshot ss
JOIN   qms.qms_arrival a ON a.arrival_id = ss.arrival_id
CROSS APPLY (SELECT MAX(receive_date) AS receive_date FROM qms.qms_sap_container_cache c
             WHERE c.container_no = ISNULL(a.container_no, '') AND c.bol_no = ISNULL(a.bol_no, '') AND c.ebeln = ISNULL(a.ebeln, '')) k
WHERE  k.receive_date IS NOT NULL AND (ss.receive_date <> k.receive_date OR ss.arrival_date <> k.receive_date)
-- Expect 0: the twin columns agree everywhere.
UNION ALL
SELECT 'snapshots_twin_columns_differ', COUNT(*)
FROM   qms.qms_shipment_snapshot WHERE ISNULL(arrival_date, '19000101') <> ISNULL(receive_date, '19000101')
UNION ALL
SELECT 'report_basis_setting_rows', COUNT(*) FROM portal.SystemSetting WHERE SettingKey = N'qms.Report.TimeBarBasis'
UNION ALL
SELECT 'flat_view_port_arrival_column', COUNT(*)
FROM   sys.columns WHERE object_id = OBJECT_ID('dbo.vw_qms_flat_defects') AND name = 'PortArrivalDate';
GO
