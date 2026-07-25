-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M09  -  Flat view: more analysable fields
-- ============================================================================
--  Extends vw_qms_flat_defects so the Perspective Analyzer (which pivots
--  directly on this view) can expose everything the Report Builder already can:
--    * Discharge date (M08) and the QO opened/closed/created timestamps
--    * Time Bar in days -- from discharge date (default) and from arrival date,
--      each counted to the QO finish (closed) date
--    * per-sample detail (grower, pallet, pack/date/lot/label, packaging)
--    * material net weight, material size, material-level sample size
--
--  All new columns are appended; the existing 42 are unchanged. Idempotent
--  (CREATE OR ALTER). Every base object is a dbo synonym into [qms].
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
    COALESCE(ss.arrival_date,  cc.arrival_date)                 AS ArrivalDate,
    COALESCE(ss.receive_date,  cc.receive_date)                 AS ReceiveDate,
    ss.sailing_date                                             AS ShippingDate,
    ss.loading_date                                             AS LoadingDate,
    ss.transit_days                                             AS TransitDays,
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
    -- Time Bar: whole days from the basis date to the QO finish (closed) date.
    -- Null until the QO is closed or when the basis date is missing.
    DATEDIFF(DAY, ss.discharge_date, qo.closed_at)              AS TimeBarDischarge,
    DATEDIFF(DAY, COALESCE(ss.arrival_date, cc.arrival_date), qo.closed_at) AS TimeBarArrival,
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
    s.created_by                                                AS SampleCreatedBy
FROM        dbo.qms_sample s
JOIN        dbo.qms_quality_order qo            ON qo.quality_order_id = s.quality_order_id
JOIN        dbo.qms_quality_order_material m    ON m.qo_material_id    = s.qo_material_id
JOIN        dbo.qms_arrival_item ai             ON ai.arrival_item_id  = m.arrival_item_id
JOIN        dbo.qms_arrival a                   ON a.arrival_id        = ai.arrival_id
LEFT JOIN   dbo.qms_shipment_snapshot ss        ON ss.arrival_id       = a.arrival_id
OUTER APPLY (
    SELECT TOP 1 c2.sto, c2.doc_date, c2.arrival_date, c2.receive_date
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
