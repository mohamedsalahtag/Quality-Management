-- ===================================================================
-- data-hub-pack — schema.sql
--
-- Two parts:
--   (1) qms_perspective table + indexes  -- copy verbatim
--   (2) Template flat view               -- copy + edit to match YOUR domain
--
-- Idempotent: every CREATE is guarded with an existence check, so this
-- script is safe to re-run.
-- ===================================================================


-- =========================================================================
-- PART 1 -- qms_perspective: saved analyzer configurations.
-- Copy this block AS-IS into your DB. The "qms_" prefix is just convention;
-- you can rename to anything (and propagate to PerspectiveService.cs SQL).
-- =========================================================================

IF OBJECT_ID(N'dbo.qms_perspective', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.qms_perspective (
        perspective_id    BIGINT IDENTITY(1,1) PRIMARY KEY,
        report_key        VARCHAR(60)   NOT NULL,
        name              NVARCHAR(150) NOT NULL,
        owner_username    NVARCHAR(80)  NOT NULL,
        scope             VARCHAR(10)   NOT NULL
            CONSTRAINT CK_qms_perspective_scope CHECK (scope IN ('private','shared')),
        is_default        BIT           NOT NULL
            CONSTRAINT DF_qms_perspective_is_default DEFAULT (0),
        config_json       NVARCHAR(MAX) NOT NULL,
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

-- Filtered unique index = soft constraint: one default per (user, report).
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_qms_perspective_default'
                 AND object_id = OBJECT_ID(N'dbo.qms_perspective'))
    CREATE UNIQUE INDEX UX_qms_perspective_default
        ON dbo.qms_perspective (owner_username, report_key)
        WHERE is_default = 1;
GO


-- =========================================================================
-- PART 2 -- The flat view your analyzer pivots over.
-- THIS BLOCK IS A TEMPLATE. Replace EVERYTHING below with your own view.
--
-- The contract: one row per "fact" (whatever you want to drill into).
-- Every column referenced as a PivotDimension.SqlExpression or
-- PivotMeasure.SqlInner in PivotRegistry.cs must be selectable from this
-- view. Every measure column must be aggregatable with SUM/AVG/MIN/MAX/COUNT.
--
-- For inspiration: see how the Sharbatly QMS canonical view stitches
-- defects + samples + materials + arrivals + MARA + the shipment snapshot
-- + the SAP container cache into one flat row stream.
-- See: examples/fruit-quality-defects-data-source.cs
-- =========================================================================

/* ----------------------------------------------------------------------
   TEMPLATE -- adapt to your domain. The shape below is illustrative.

CREATE OR ALTER VIEW dbo.vw_yourdomain_flat AS
SELECT
    -- Identifiers -- one ID column per join level so drills can scope by them.
    t.transaction_id                            AS TransactionId,
    o.order_id                                  AS OrderId,

    -- Dimensions -- categorical strings to pivot on.
    t.plant                                     AS Plant,
    t.vendor_name                               AS VendorName,
    t.region                                    AS Region,
    o.status_code                               AS Status,
    p.product_no                                AS ProductNo,
    p.product_desc                              AS ProductDesc,
    p.category                                  AS Category,
    p.sub_category                              AS SubCategory,

    -- Dates -- pivot via time-bucket dim expressions (FORMAT(...) in registry).
    t.transaction_date                          AS TransactionDate,
    o.order_date                                AS OrderDate,

    -- Measures -- numeric columns to aggregate.
    t.quantity                                  AS Quantity,
    t.unit_price                                AS UnitPrice,
    t.line_amount                               AS LineAmount,
    t.discount_pct                              AS DiscountPct
FROM        dbo.your_transactions t
JOIN        dbo.your_orders o            ON o.order_id   = t.order_id
LEFT JOIN   dbo.your_products p          ON p.product_id = t.product_id
WHERE       t.is_deleted = 0;
GO

------------------------------------------------------------------------ */

-- End of schema.sql.
