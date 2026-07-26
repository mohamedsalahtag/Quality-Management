-- =====================================================================
-- V38  Multi seal/logger, MARA brand cache column, 'avg' reading mode
--      (2026-07-08)
--
-- Three independent schema tweaks bundled for one deploy:
--
-- 1. Arrival checklist now stores UP TO 4 seal numbers and UP TO 4
--    record-logger serials, newline-delimited in the existing single
--    columns. Widen both columns to fit four values + delimiters.
--
-- 2. MARA "Brand" was newly added to the material-master feed. Add a
--    brand column to the flattened material cache so the sync can land
--    it and MaraService can read it (was hardcoded NULL before).
--
-- 3. New reading display_mode 'avg' = Σ numeric ÷ sample count. Used so
--    the QO summary Readings section shows Gross/Net weight as the
--    average across the group's samples. Extend the CHECK constraint and
--    switch GROSS_WEIGHT / NET_WEIGHT to 'avg'.
-- =====================================================================

-- 1. Widen seal / logger columns ------------------------------------
--    seal_no was VARCHAR(30); data_logger_serial was NVARCHAR(50).
ALTER TABLE qms_arrival_checklist ALTER COLUMN seal_no            NVARCHAR(200) NULL;
GO
ALTER TABLE qms_arrival_checklist ALTER COLUMN data_logger_serial NVARCHAR(300) NULL;
GO

-- 2. Brand column on the material cache -----------------------------
IF COL_LENGTH('qms_sap_material_cache','brand') IS NULL
    ALTER TABLE qms_sap_material_cache ADD brand NVARCHAR(100) NULL;
GO

-- 3. 'avg' reading display_mode -------------------------------------
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_qms_reading_type_display_mode')
    ALTER TABLE qms_reading_type DROP CONSTRAINT CK_qms_reading_type_display_mode;
GO
ALTER TABLE qms_reading_type
    ADD CONSTRAINT CK_qms_reading_type_display_mode
    CHECK (display_mode IN ('text','count','sum','sum_over_size','avg','formula'));
GO

-- Gross / Net weight summarise as the per-sample average, not the sum.
UPDATE qms_reading_type
   SET display_mode = 'avg'
 WHERE reading_type_code IN ('GROSS_WEIGHT','NET_WEIGHT');
GO

SELECT 'seal_no width' AS check_item, CHARACTER_MAXIMUM_LENGTH AS value
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME = 'qms_arrival_checklist' AND COLUMN_NAME = 'seal_no';

SELECT 'material_cache brand' AS check_item, COUNT(*) AS value
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME = 'qms_sap_material_cache' AND COLUMN_NAME = 'brand';

SELECT 'avg reading types' AS check_item, COUNT(*) AS value
FROM   qms_reading_type WHERE display_mode = 'avg';
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
