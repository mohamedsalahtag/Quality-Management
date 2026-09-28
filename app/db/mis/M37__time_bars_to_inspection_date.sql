-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M37  -  time bars count to the inspection date
-- ============================================================================
--  The QC report now prints two figures (2026-09-27, on request):
--
--    * Time Bar            = inspection date - vessel discharge date
--                            (supplier claim; column TimeBarDischarge)
--    * Inspection Time Bar = inspection date - branch receive date
--                            (internal performance; column TimeBarArrival)
--
--  Inspection date = the LOCAL day the live Quality Order was opened. Both
--  columns counted to the QC FINISH date until now. This re-creates the flat
--  view with only those two expressions changed; every column keeps its name
--  and position. Same arithmetic as ShipmentDates.DaysToInspection.
--
--  Idempotent (CREATE OR ALTER).
-- ============================================================================
GO
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
    -- Time Bar (supplier claim): inspection date - vessel discharge date.
    -- Inspection date = the LOCAL day the QO was opened. Null until opened.
    CASE WHEN DATEDIFF(DAY, ss.discharge_date,
                       CAST(qo.opened_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) < 0 THEN 0
         ELSE DATEDIFF(DAY, ss.discharge_date,
                       CAST(qo.opened_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) END
                                                                AS TimeBarDischarge,
    -- Inspection Time Bar (internal): inspection date - branch receive date,
    -- the figure the Inspection Time Bar page and the QC report print.
    CASE WHEN DATEDIFF(DAY, COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date),
                       CAST(qo.opened_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) < 0 THEN 0
         ELSE DATEDIFF(DAY, COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date),
                       CAST(qo.opened_at AT TIME ZONE 'UTC' AT TIME ZONE 'Arab Standard Time' AS date)) END
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

SELECT 'flat_rows_with_time_bar' AS check_item, COUNT(TimeBarDischarge) AS value FROM dbo.vw_qms_flat_defects
UNION ALL
SELECT 'flat_rows_with_inspection_time_bar', COUNT(TimeBarArrival) FROM dbo.vw_qms_flat_defects;
GO
