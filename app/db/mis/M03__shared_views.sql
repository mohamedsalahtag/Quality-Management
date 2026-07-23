-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M03  -  Shared SAP catalogue views
-- ============================================================================
--  QMS used to keep its own copies of the SAP material and vendor masters
--  (qms_sap_material_cache / qms_sap_vendor_cache), synced by its own jobs.
--  Sharbatly_MIS already holds the same data in dbo.Mara (33,785 rows, synced
--  by the SCM app) and dbo.SAP_Vendors (6,700 rows), so the QMS tables are not
--  re-created. These views present the MIS tables under the exact column names
--  the QMS app already selects; M05 then maps the old table names onto them
--  with synonyms, so no application query has to change.
--
--  Consequence: the QMS MaterialMaster / VendorMaster sync jobs are switched
--  off in the app (a view cannot be MERGEd into). dbo.Mara is now owned solely
--  by the SCM app's maraSync.
--
--  NOTE ON TYPES: columns are exposed in dbo.Mara's native types rather than
--  cast down to the old QMS column widths. Casting nvarchar(100) MaterialGroup
--  into the old varchar(20) would silently truncate, and narrowing nvarchar to
--  varchar would also defeat index seeks when Dapper passes nvarchar
--  parameters. Dapper maps by column name into strings, so the width does not
--  matter to the application.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- QMS-only material attributes that have no home in dbo.Mara.
--   brand: added by QMS V38 and populated for 16,217 materials. dbo.Mara has
--          no brand column, so the values are carried over here and joined in
--          below. They are static now that QMS no longer syncs the material
--          master -- a new material will show a NULL brand until this table is
--          extended (or SCM's maraSync grows a brand column).
-- ---------------------------------------------------------------------------
IF OBJECT_ID('qms.MaterialExtra') IS NULL
CREATE TABLE qms.MaterialExtra (
    material_no nvarchar(80)  NOT NULL CONSTRAINT PK_MaterialExtra PRIMARY KEY,
    brand       nvarchar(100) NULL
);
GO

-- ---------------------------------------------------------------------------
-- Material catalogue: dbo.Mara presented with QMS column names.
-- Consumed by MaraService.ListMaterialGroupsAsync / LookupAsync and by
-- HybridSapClient's material enrichment.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW qms.MaterialCatalog
AS
SELECT
    m.MATERIAL                                  AS material_no,
    m.MATERIAL_DESCRIPTION                      AS material_desc,
    m.MAJOR_CATEGORY                            AS major_category,
    m.MAJOR_CATEGORYNAME                        AS major_category_desc,
    m.SUB_MAJOR_CATEGORY                        AS sub_major_category,
    CAST(m.OverHead AS varchar(40))             AS overhead,
    m.MaterialGroup                             AS material_group,
    m.MaterialGroupDesc                         AS material_group_desc,
    m.ORIGIN_ID                                 AS origin_id,
    m.ORIGIN_NAME                               AS origin_name,
    m.PROCUREMENT_TYPE_ID                       AS procurement_type,
    m.PROCUREMENT_TYPE_NAME                     AS procurement_type_name,
    m.VARIETY_ID                                AS variety_id,
    m.VARIETY_NAME                              AS variety_name,
    m.CLASS_ID                                  AS class_id,
    m.CLASS_NAME                                AS class_name,
    m.SIZE_ID                                   AS size_id,
    m.SIZE_NAME                                 AS size_name,
    m.ITEM_WEIGHT                               AS material_weight,
    m.MVGR5DESCRPTION                           AS material_weight_name,
    m.OLDMATERIALCODE                           AS old_material_code,
    m.Weight                                    AS weight,
    m.WeightUnit                                AS weight_unit,
    m.MATERIAL_TYPE                             AS material_type,
    m.BASE_UNIT                                 AS base_unit,
    m.BASE_UNIT_NAME                            AS base_unit_name,
    -- CREATED_ON is nvarchar in Mara and is empty for every row today;
    -- TRY_CONVERT keeps it NULL rather than failing if that ever changes.
    TRY_CONVERT(datetime2(7), m.CREATED_ON)     AS sap_created_on,
    m.CREATED_BY                                AS sap_created_by,
    x.brand                                     AS brand,
    -- The old table stamped each row at sync time. There is no equivalent on
    -- dbo.Mara; nothing in the app reads this column.
    CAST(NULL AS datetime2(7))                  AS synced_at
FROM dbo.Mara AS m
LEFT JOIN qms.MaterialExtra AS x
       ON x.material_no = m.MATERIAL;
GO

-- ---------------------------------------------------------------------------
-- Vendor catalogue: dbo.SAP_Vendors presented as the old vendor cache.
--
-- VendorService reads a raw JSON `payload_json` column and digs the name and
-- e-mail out with JSON_VALUE over several candidate field names, so the view
-- synthesises the JSON document rather than forcing a code change.
-- STRING_ESCAPE (SQL Server 2016+) keeps quotes in vendor names from breaking
-- the document.
--
-- dbo.SAP_Vendors holds no e-mail address, so JSON_VALUE(...'$.Email') stays
-- NULL and the send-report dialog opens with an empty To field -- exactly the
-- documented fallback. This is not a regression: qms_sap_vendor_cache was
-- empty, so the lookup previously returned nothing at all. Vendor *names* now
-- resolve where they never did before.
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW qms.VendorCatalog
AS
SELECT
    v.VendorId AS vendor_no,
    CASE WHEN v.VendorName IS NULL THEN NULL
         ELSE N'{"Vendor_Name":"' + STRING_ESCAPE(v.VendorName, 'json') + N'"}'
    END                          AS payload_json,
    CAST(N'SAP_Vendors' AS nvarchar(50)) AS source_id,
    CAST(NULL AS datetime2(7))   AS synced_at
FROM dbo.SAP_Vendors AS v;
GO
