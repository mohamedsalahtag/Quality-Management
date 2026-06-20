-- ===================================================================
-- V34  Perspective Analyzer (server-side pivot) for the data hub.
--
-- 1. qms_perspective         saved pivot configurations (private/shared)
-- 2. vw_qms_flat_defects     denormalized view used by PivotService;
--                             one row per (active sample x entered defect),
--                             MARA-merged so Variety/Class/Origin/Major
--                             pivot cleanly on a single JOIN to the cache.
--
-- Idempotent: each block guards with an existence check.
-- ===================================================================

-- 1. qms_perspective ----------------------------------------------------

IF OBJECT_ID(N'dbo.qms_perspective', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.qms_perspective (
        perspective_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
        report_key        VARCHAR(60)   NOT NULL,            -- 'flat_defects' | 'containers'
        name              NVARCHAR(150) NOT NULL,
        owner_username    NVARCHAR(80)  NOT NULL,            -- sAMAccountName
        scope             VARCHAR(10)   NOT NULL
            CONSTRAINT CK_qms_perspective_scope CHECK (scope IN ('private','shared')),
        is_default        BIT           NOT NULL
            CONSTRAINT DF_qms_perspective_is_default DEFAULT (0),
        config_json       NVARCHAR(MAX) NOT NULL,            -- { rows, cols, measure, agg, renderer, topN }
        created_at        DATETIME2(0)  NOT NULL
            CONSTRAINT DF_qms_perspective_created_at DEFAULT (SYSUTCDATETIME()),
        created_by        NVARCHAR(80)  NOT NULL,
        updated_at        DATETIME2(0)  NULL,
        updated_by        NVARCHAR(80)  NULL
    );
END;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_perspective_owner_report'
                 AND object_id = OBJECT_ID(N'dbo.qms_perspective'))
    CREATE INDEX IX_qms_perspective_owner_report
        ON dbo.qms_perspective (owner_username, report_key);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_qms_perspective_shared'
                 AND object_id = OBJECT_ID(N'dbo.qms_perspective'))
    CREATE INDEX IX_qms_perspective_shared
        ON dbo.qms_perspective (report_key)
        WHERE scope = 'shared';
GO

-- One default per user, per report. Filtered unique index = soft constraint.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_perspective_default'
                 AND object_id = OBJECT_ID(N'dbo.qms_perspective'))
    CREATE UNIQUE INDEX UX_qms_perspective_default
        ON dbo.qms_perspective (owner_username, report_key)
        WHERE is_default = 1;
GO

-- 2. vw_qms_flat_defects ------------------------------------------------
-- Server-side pivot source. The existing IAsyncEnumerable<FlatDefectRow>
-- stream is NOT used here -- it does a Cartesian product against the
-- defect catalog (zero-fill rows) which would dominate every GROUP BY.
-- The view exposes only DEFECTS THAT WERE ENTERED (sd is INNER-ed via
-- the LEFT-then-NOT-NULL filter pattern below). For "samples with no
-- defects" measures, the registry exposes a separate distinct-sample
-- count measure.
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
    -- MARA-merged categorical dimensions: prefer persisted snapshot,
    -- fall back to the live cache so old QOs (NULL snapshot) pivot.
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
    -- Percent-of-sample-size derived measure. NULL when sample_size is
    -- null/zero so AVG/MAX correctly skip those rows.
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
    -- Cache natural key is wider than (container_no, bol_no, ebeln);
    -- pick any one matching row to keep grain at sample x defect.
    SELECT TOP 1 c2.sto, c2.doc_date, c2.arrival_date, c2.receive_date
    FROM   dbo.qms_sap_container_cache c2
    WHERE  c2.container_no = a.container_no
      AND  c2.bol_no       = a.bol_no
      AND  c2.ebeln        = a.ebeln
) cc
LEFT JOIN   dbo.qms_sap_material_cache mc       ON mc.material_no      = m.material_no
JOIN        dbo.qms_sample_defect sd            ON sd.sample_id        = s.sample_id
JOIN        dbo.qms_defect_catalog dc           ON dc.defect_id        = sd.defect_id
WHERE       s.is_deleted = 0;
GO
