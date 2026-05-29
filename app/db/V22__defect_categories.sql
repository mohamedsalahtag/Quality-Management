-- ===================================================================
-- V22  Dynamic defect categories.
--
-- Until now a defect's category was a free string on qms_defect_catalog
-- (defect_category) constrained by a hardcoded CHECK to
-- ('Major','Minor','Critical','Other'), and the sample form + PDF
-- collapsed everything into two fixed buckets (Major+Critical vs Minor).
--
-- This migration makes categories a first-class, admin-managed master
-- list so new categories (e.g. "Progressive") can be defined and each
-- renders as its own coloured section in the sample form and the PDF.
--
--   qms_defect_category   -- one row per category (name, sort, colour)
--
-- defect_category stays the VARCHAR name on qms_defect_catalog (the
-- de-facto key everywhere); a FK with ON UPDATE CASCADE links it to the
-- master so renames propagate and existing string-keyed code keeps
-- working. The old CHECK is dropped. Critical now sorts as its own
-- section directly after Major (no longer merged into it).
-- ===================================================================

-- 1) Master table -------------------------------------------------------
IF OBJECT_ID('qms_defect_category') IS NULL
BEGIN
    CREATE TABLE qms_defect_category (
        category_id   INT IDENTITY(1,1) PRIMARY KEY,
        category_name VARCHAR(20)  NOT NULL,      -- must match qms_defect_catalog.defect_category type (FK target)
        sort_order    INT          NOT NULL CONSTRAINT DF_qms_defect_category_sort   DEFAULT 500,
        color_hex     VARCHAR(7)   NULL,          -- '#RRGGBB'
        is_active     BIT          NOT NULL CONSTRAINT DF_qms_defect_category_active DEFAULT 1,
        CONSTRAINT UQ_qms_defect_category_name UNIQUE (category_name)
    );
END
GO

-- 2) Seed the existing four so nothing changes visually until new
--    categories are added. Idempotent via category_name uniqueness.
IF NOT EXISTS (SELECT 1 FROM qms_defect_category WHERE category_name = 'Major')
    INSERT INTO qms_defect_category (category_name, sort_order, color_hex) VALUES ('Major',    10, '#b02a37');
IF NOT EXISTS (SELECT 1 FROM qms_defect_category WHERE category_name = 'Critical')
    INSERT INTO qms_defect_category (category_name, sort_order, color_hex) VALUES ('Critical', 20, '#7a1620');
IF NOT EXISTS (SELECT 1 FROM qms_defect_category WHERE category_name = 'Minor')
    INSERT INTO qms_defect_category (category_name, sort_order, color_hex) VALUES ('Minor',    30, '#ffc107');
IF NOT EXISTS (SELECT 1 FROM qms_defect_category WHERE category_name = 'Other')
    INSERT INTO qms_defect_category (category_name, sort_order, color_hex) VALUES ('Other',    40, '#6c757d');
GO

-- 3) Backfill any orphan category values that exist on defect rows but
--    not in the master (hand-edited data), so the FK can be added safely.
INSERT INTO qms_defect_category (category_name, sort_order, color_hex)
SELECT DISTINCT d.defect_category, 900, '#6c757d'
FROM   qms_defect_catalog d
WHERE  d.defect_category IS NOT NULL
  AND  NOT EXISTS (SELECT 1 FROM qms_defect_category c WHERE c.category_name = d.defect_category);
GO

-- 4) Drop the hardcoded CHECK (categories are now data-driven).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_qms_defect_catalog_category')
    ALTER TABLE qms_defect_catalog DROP CONSTRAINT CK_qms_defect_catalog_category;
GO

-- 5) Link the catalog to the master. FK to a UNIQUE (non-PK) column is
--    legal in SQL Server. ON UPDATE CASCADE so an admin rename of a
--    category propagates to every defect row automatically.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_qms_defect_catalog_category')
    ALTER TABLE qms_defect_catalog
        ADD CONSTRAINT FK_qms_defect_catalog_category
            FOREIGN KEY (defect_category) REFERENCES qms_defect_category(category_name)
            ON UPDATE CASCADE;
GO

-- 6) Sanity
SELECT 'qms_defect_category rows' AS check_item, COUNT(*) AS value
FROM   qms_defect_category;

SELECT 'defect rows with unresolved category' AS check_item, COUNT(*) AS value
FROM   qms_defect_catalog d
WHERE  d.defect_category IS NOT NULL
  AND  NOT EXISTS (SELECT 1 FROM qms_defect_category c WHERE c.category_name = d.defect_category);
GO
