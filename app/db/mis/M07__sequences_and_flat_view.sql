-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M07  -  Sequences + flat-defects view
-- ============================================================================
--  The objects M02 missed: it was generated from sys.tables, so the non-table
--  objects came across only after an end-to-end workflow test failed with
--  "Invalid object name 'seq_qms_arrival_no'".
--
--  Both live in dbo rather than [qms]:
--    * Sequences - the app calls "NEXT VALUE FOR seq_qms_arrival_no"
--      unqualified, and SQL Server does NOT allow a synonym for a sequence, so
--      they must sit in the connecting user's default schema.
--    * The view  - Services/Reports/PivotRegistry.cs names it as the literal
--      "dbo.vw_qms_flat_defects" and its body is dbo-qualified throughout.
--      Every table it reads is a dbo synonym into [qms], so the data still
--      comes from the qms schema.
--
--  The view body below is the definition from the retired database, unedited
--  apart from the CREATE verb.
--
--  Idempotent: safe to re-run.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1) Document-number sequences.
--    They restart above the values the retired database reached (arrival 22,
--    shipment 22, quality order 17) so a number that already appeared on a
--    printed report is never issued again, even though the transactional
--    history itself was not migrated.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.seq_qms_arrival_no') IS NULL
    CREATE SEQUENCE dbo.seq_qms_arrival_no       AS int START WITH 100 INCREMENT BY 1;

IF OBJECT_ID('dbo.seq_qms_shipment_no') IS NULL
    CREATE SEQUENCE dbo.seq_qms_shipment_no      AS int START WITH 100 INCREMENT BY 1;

IF OBJECT_ID('dbo.seq_qms_quality_order_no') IS NULL
    CREATE SEQUENCE dbo.seq_qms_quality_order_no AS int START WITH 100 INCREMENT BY 1;
GO

-- ---------------------------------------------------------------------------
-- 2) Flat-defects reporting view (Data Hub grid, Excel export, Perspectives).
-- ---------------------------------------------------------------------------
GO

-- 4. Flat-defects view: source Brand from the material cache -------
--    qms_quality_order_material.brand is never persisted (brand is
--    enriched in-memory at view time via ApplyMara). The Data Hub grid
--    already re-merges MARA brand in code, but the Perspective Analyzer
--    reads this view directly, so fall back to the cache brand the same
--    way the view already does for Variety/Class/Origin. Full CREATE OR
--    ALTER (single source: V37 definition + the Brand fallback).
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
    END                                                         AS DefectRate
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

