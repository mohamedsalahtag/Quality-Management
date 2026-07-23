-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M05  -  dbo synonyms
-- ============================================================================
--  The QMS app writes unqualified SQL ("FROM qms_arrival"), which SQL Server
--  resolves against the connecting principal's DEFAULT_SCHEMA and then dbo.
--  The app connects as SAP_User, whose default schema is dbo, so every QMS
--  object is surfaced in dbo as a synonym pointing into [qms].
--
--  This keeps the real tables grouped in [qms] (one query enumerates the whole
--  QMS footprint) while requiring no change to a single application query.
--
--  The two SAP caches deliberately resolve to VIEWS over the MIS-owned tables
--  rather than to QMS copies:
--      dbo.qms_sap_material_cache -> qms.MaterialCatalog -> dbo.Mara
--      dbo.qms_sap_vendor_cache   -> qms.VendorCatalog   -> dbo.SAP_Vendors
--  Reads work unchanged; writes do not, which is intended -- the QMS material
--  and vendor sync jobs are disabled in the app.
--
--  If a DBA later creates the QMS_App login with DEFAULT_SCHEMA = qms (see
--  M01), these synonyms become redundant and can all be dropped:
--      SELECT 'DROP SYNONYM dbo.' + QUOTENAME(name) + ';'
--      FROM sys.synonyms WHERE base_object_name LIKE '%[[]qms]%';
--
--  Idempotent: safe to re-run.
-- ============================================================================

SET NOCOUNT ON;

-- Every QMS object that the app addresses by its bare name. UserProfile and
-- MaterialExtra are excluded: they are new, QMS-internal, and always written
-- as qms.* in code, so they need no synonym.
;WITH src AS (
    SELECT o.name AS target_name,
           CASE o.name
               WHEN 'MaterialCatalog' THEN 'qms_sap_material_cache'
               WHEN 'VendorCatalog'   THEN 'qms_sap_vendor_cache'
               ELSE o.name
           END AS synonym_name
    FROM sys.objects o
    WHERE o.schema_id = SCHEMA_ID('qms')
      AND o.type IN ('U','V')
      AND o.name NOT IN ('UserProfile','MaterialExtra')
)
SELECT
    -- Guard: only create where dbo has no real object of that name, so a
    -- synonym can never shadow Mara / SAP_Vendors / Supplier_Material.
    drop_stmt   = 'DROP SYNONYM IF EXISTS dbo.' + QUOTENAME(synonym_name) + ';',
    create_stmt = CASE
                    WHEN OBJECT_ID('dbo.' + QUOTENAME(synonym_name)) IS NULL
                      OR OBJECT_ID('dbo.' + QUOTENAME(synonym_name), 'SN') IS NOT NULL
                    THEN 'CREATE SYNONYM dbo.' + QUOTENAME(synonym_name) + ' FOR qms.' + QUOTENAME(target_name) + ';'
                    ELSE '/* SKIPPED ' + synonym_name + ' -- real dbo object exists */'
                  END,
    synonym_name, target_name
INTO #plan
FROM src;

DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql + drop_stmt + CHAR(13) + CHAR(10)
                   + create_stmt + CHAR(13) + CHAR(10)
FROM #plan
ORDER BY synonym_name;

EXEC sp_executesql @sql;

SELECT CONCAT('synonyms planned: ', COUNT(*),
              ' | skipped: ', SUM(CASE WHEN create_stmt LIKE '/*%' THEN 1 ELSE 0 END)) AS result
FROM #plan;

DROP TABLE #plan;
