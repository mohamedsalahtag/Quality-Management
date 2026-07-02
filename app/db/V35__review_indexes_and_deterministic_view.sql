-- V35  Code-review fixes (2026-07-02):
--   * M-12: add indexes for common query paths that previously scanned.
--   * M-10: make vw_qms_flat_defects deterministic (ORDER BY in the OUTER APPLY
--           TOP 1) so PO date / STO don't vary between runs.
-- All statements are idempotent / CREATE OR ALTER so re-running is safe.

------------------------------------------------------------------------
-- M-12: missing indexes
------------------------------------------------------------------------

-- Audit filtering by actor with no date anchor previously scanned the whole
-- IX_qms_audit_log_filter (which leads on changed_at).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_audit_log_actor')
    CREATE INDEX IX_qms_audit_log_actor
        ON dbo.qms_audit_log (changed_by, changed_at DESC);
GO

-- Entity-type-only audit filtering.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_audit_log_entity_type')
    CREATE INDEX IX_qms_audit_log_entity_type
        ON dbo.qms_audit_log (entity_type, changed_at DESC);
GO

-- AuditService.GetForCompositeRecordAsync and other lookups filter samples by
-- their parent QO; qms_sample only had an index on (qo_material_id, sample_no).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_sample_qo')
    CREATE INDEX IX_qms_sample_qo
        ON dbo.qms_sample (quality_order_id) WHERE is_deleted = 0;
GO

-- QO list pages filter by status_code; qms_arrival already had one, QO did not.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_quality_order_status')
    CREATE INDEX IX_qms_quality_order_status
        ON dbo.qms_quality_order (status_code);
GO

------------------------------------------------------------------------
-- M-10: deterministic OUTER APPLY in the flat-defects view
--       (verbatim copy of the V34 definition + ORDER BY in the APPLY)
------------------------------------------------------------------------
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
    m.brand                                                     AS Brand,
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
    -- Cache natural key is wider than (container_no, bol_no, ebeln). Pick the
    -- newest matching row DETERMINISTICALLY so PoDate/Sto/dates are reproducible
    -- across runs (previously TOP 1 with no ORDER BY returned an arbitrary row).
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
