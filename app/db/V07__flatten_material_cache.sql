-- ===================================================================
-- V07  qms_sap_material_cache: flatten payload_json into typed columns.
--
-- Before: every cached SAP MARA record lived as a JSON blob in
-- payload_json, and every read (the MARA group dropdown, the QO
-- materials enrichment, the arrival container search) used JSON_VALUE,
-- which forces SQL Server to parse the whole blob per row.
--
-- After: 27 dedicated columns matching the CDS view fields. Reads are
-- normal column SELECTs; the material_group column is indexed so the
-- defect-catalog dropdown and any per-group filtering hits an index.
-- payload_json is kept (nullable) as a diagnostic archive -- a later
-- migration can drop it once the new flow is verified.
-- ===================================================================

-- 1. Add the typed columns (idempotent guard for re-runs).
IF COL_LENGTH('qms_sap_material_cache','material_desc')          IS NULL ALTER TABLE qms_sap_material_cache ADD material_desc          NVARCHAR(255) NULL;
IF COL_LENGTH('qms_sap_material_cache','major_category')         IS NULL ALTER TABLE qms_sap_material_cache ADD major_category         VARCHAR(40)   NULL;
IF COL_LENGTH('qms_sap_material_cache','major_category_desc')    IS NULL ALTER TABLE qms_sap_material_cache ADD major_category_desc    NVARCHAR(120) NULL;
IF COL_LENGTH('qms_sap_material_cache','sub_major_category')     IS NULL ALTER TABLE qms_sap_material_cache ADD sub_major_category     NVARCHAR(120) NULL;
IF COL_LENGTH('qms_sap_material_cache','overhead')               IS NULL ALTER TABLE qms_sap_material_cache ADD overhead               VARCHAR(40)   NULL;
IF COL_LENGTH('qms_sap_material_cache','material_group')         IS NULL ALTER TABLE qms_sap_material_cache ADD material_group         VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','material_group_desc')    IS NULL ALTER TABLE qms_sap_material_cache ADD material_group_desc    NVARCHAR(120) NULL;
IF COL_LENGTH('qms_sap_material_cache','origin_id')              IS NULL ALTER TABLE qms_sap_material_cache ADD origin_id              VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','origin_name')            IS NULL ALTER TABLE qms_sap_material_cache ADD origin_name            NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','procurement_type')       IS NULL ALTER TABLE qms_sap_material_cache ADD procurement_type       VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','procurement_type_name')  IS NULL ALTER TABLE qms_sap_material_cache ADD procurement_type_name  NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','variety_id')             IS NULL ALTER TABLE qms_sap_material_cache ADD variety_id             VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','variety_name')           IS NULL ALTER TABLE qms_sap_material_cache ADD variety_name           NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','class_id')               IS NULL ALTER TABLE qms_sap_material_cache ADD class_id               VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','class_name')             IS NULL ALTER TABLE qms_sap_material_cache ADD class_name             NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','size_id')                IS NULL ALTER TABLE qms_sap_material_cache ADD size_id                VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','size_name')              IS NULL ALTER TABLE qms_sap_material_cache ADD size_name              NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','material_weight')        IS NULL ALTER TABLE qms_sap_material_cache ADD material_weight        VARCHAR(40)   NULL;
IF COL_LENGTH('qms_sap_material_cache','material_weight_name')   IS NULL ALTER TABLE qms_sap_material_cache ADD material_weight_name   NVARCHAR(80)  NULL;
IF COL_LENGTH('qms_sap_material_cache','old_material_code')      IS NULL ALTER TABLE qms_sap_material_cache ADD old_material_code      NVARCHAR(40)  NULL;
IF COL_LENGTH('qms_sap_material_cache','weight')                 IS NULL ALTER TABLE qms_sap_material_cache ADD weight                 DECIMAL(18,3) NULL;
IF COL_LENGTH('qms_sap_material_cache','weight_unit')            IS NULL ALTER TABLE qms_sap_material_cache ADD weight_unit            VARCHAR(10)   NULL;
IF COL_LENGTH('qms_sap_material_cache','material_type')          IS NULL ALTER TABLE qms_sap_material_cache ADD material_type          VARCHAR(20)   NULL;
IF COL_LENGTH('qms_sap_material_cache','base_unit')              IS NULL ALTER TABLE qms_sap_material_cache ADD base_unit              VARCHAR(10)   NULL;
IF COL_LENGTH('qms_sap_material_cache','base_unit_name')         IS NULL ALTER TABLE qms_sap_material_cache ADD base_unit_name         NVARCHAR(40)  NULL;
IF COL_LENGTH('qms_sap_material_cache','sap_created_on')         IS NULL ALTER TABLE qms_sap_material_cache ADD sap_created_on         DATETIME2     NULL;
IF COL_LENGTH('qms_sap_material_cache','sap_created_by')         IS NULL ALTER TABLE qms_sap_material_cache ADD sap_created_by         NVARCHAR(40)  NULL;
GO

-- payload_json was created NOT NULL in V04. Loosen it so future syncs can
-- choose to skip the blob entirely once the flow is bedded in.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_sap_material_cache') AND name = 'payload_json' AND is_nullable = 0)
    ALTER TABLE qms_sap_material_cache ALTER COLUMN payload_json NVARCHAR(MAX) NULL;
GO

-- 2. One-shot backfill from payload_json.
WITH src AS (
    SELECT
        material_no,
        JSON_VALUE(payload_json, '$.Material_Desc')          AS material_desc,
        JSON_VALUE(payload_json, '$.Major_Category')         AS major_category,
        JSON_VALUE(payload_json, '$.Major_Category_Desc')    AS major_category_desc,
        JSON_VALUE(payload_json, '$.SubMajor_Category')      AS sub_major_category,
        JSON_VALUE(payload_json, '$.OverHead')               AS overhead,
        JSON_VALUE(payload_json, '$.Material_Group')         AS material_group,
        JSON_VALUE(payload_json, '$.Material_Group_Desc')    AS material_group_desc,
        JSON_VALUE(payload_json, '$.Origin_Id')              AS origin_id,
        JSON_VALUE(payload_json, '$.Origin_Name')            AS origin_name,
        JSON_VALUE(payload_json, '$.Procurement_type')       AS procurement_type,
        JSON_VALUE(payload_json, '$.Procurement_type_Name')  AS procurement_type_name,
        JSON_VALUE(payload_json, '$.VarietyID')              AS variety_id,
        JSON_VALUE(payload_json, '$.Variety_Name')           AS variety_name,
        JSON_VALUE(payload_json, '$.ClassID')                AS class_id,
        JSON_VALUE(payload_json, '$.Class_Name')             AS class_name,
        JSON_VALUE(payload_json, '$.SizeID')                 AS size_id,
        JSON_VALUE(payload_json, '$.Size_Name')              AS size_name,
        JSON_VALUE(payload_json, '$.Material_Weight')        AS material_weight,
        JSON_VALUE(payload_json, '$.Material_Weight_Name')   AS material_weight_name,
        JSON_VALUE(payload_json, '$.OldMaterialCode')        AS old_material_code,
        TRY_CAST(JSON_VALUE(payload_json, '$.Weight') AS DECIMAL(18,3)) AS weight,
        JSON_VALUE(payload_json, '$.Weight_Unit')            AS weight_unit,
        JSON_VALUE(payload_json, '$.Material_Type')          AS material_type,
        JSON_VALUE(payload_json, '$.Base_Unit')              AS base_unit,
        JSON_VALUE(payload_json, '$.Base_Unit_Name')         AS base_unit_name,
        -- /Date(1703808000000)/ -> DATETIME2 via ms-since-epoch
        TRY_CONVERT(DATETIME2,
            DATEADD(MILLISECOND,
                TRY_CAST(SUBSTRING(JSON_VALUE(payload_json, '$.Created_ON'), 7,
                    NULLIF(CHARINDEX(')', JSON_VALUE(payload_json, '$.Created_ON')), 0) - 7) AS BIGINT) % 1000,
                DATEADD(SECOND,
                    TRY_CAST(SUBSTRING(JSON_VALUE(payload_json, '$.Created_ON'), 7,
                        NULLIF(CHARINDEX(')', JSON_VALUE(payload_json, '$.Created_ON')), 0) - 7) AS BIGINT) / 1000,
                    CAST('1970-01-01' AS DATETIME2)))) AS sap_created_on,
        JSON_VALUE(payload_json, '$.Created_By')             AS sap_created_by
    FROM qms_sap_material_cache
    WHERE payload_json IS NOT NULL
)
UPDATE t SET
    t.material_desc           = src.material_desc,
    t.major_category          = NULLIF(src.major_category, ''),
    t.major_category_desc     = NULLIF(src.major_category_desc, ''),
    t.sub_major_category      = NULLIF(src.sub_major_category, ''),
    t.overhead                = NULLIF(src.overhead, ''),
    t.material_group          = NULLIF(src.material_group, ''),
    t.material_group_desc     = NULLIF(src.material_group_desc, ''),
    t.origin_id               = NULLIF(src.origin_id, ''),
    t.origin_name             = NULLIF(src.origin_name, ''),
    t.procurement_type        = NULLIF(src.procurement_type, ''),
    t.procurement_type_name   = NULLIF(src.procurement_type_name, ''),
    t.variety_id              = NULLIF(src.variety_id, ''),
    t.variety_name            = NULLIF(src.variety_name, ''),
    t.class_id                = NULLIF(src.class_id, ''),
    t.class_name              = NULLIF(src.class_name, ''),
    t.size_id                 = NULLIF(src.size_id, ''),
    t.size_name               = NULLIF(src.size_name, ''),
    t.material_weight         = NULLIF(src.material_weight, ''),
    t.material_weight_name    = NULLIF(src.material_weight_name, ''),
    t.old_material_code       = NULLIF(src.old_material_code, ''),
    t.weight                  = src.weight,
    t.weight_unit             = NULLIF(src.weight_unit, ''),
    t.material_type           = NULLIF(src.material_type, ''),
    t.base_unit               = NULLIF(src.base_unit, ''),
    t.base_unit_name          = NULLIF(src.base_unit_name, ''),
    t.sap_created_on          = src.sap_created_on,
    t.sap_created_by          = NULLIF(src.sap_created_by, '')
FROM qms_sap_material_cache t
JOIN src ON src.material_no = t.material_no;
GO

-- 3. Index on material_group -- powers the defect-catalog dropdown and any
-- per-group join from the QO materials side.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_sap_material_cache_group' AND object_id = OBJECT_ID('qms_sap_material_cache'))
    CREATE INDEX IX_qms_sap_material_cache_group ON qms_sap_material_cache(material_group);
GO

-- 4. Sanity report.
SELECT
    (SELECT COUNT(*) FROM qms_sap_material_cache)                                  AS total_rows,
    (SELECT COUNT(*) FROM qms_sap_material_cache WHERE material_group IS NOT NULL) AS rows_with_group,
    (SELECT COUNT(DISTINCT material_group) FROM qms_sap_material_cache WHERE material_group IS NOT NULL) AS distinct_groups,
    (SELECT TOP 1 material_group FROM qms_sap_material_cache WHERE material_group = 'APPLE') AS apple_check;
