/* =====================================================================
   V57 -- Supplier contacts. V-SERIES TWIN of
   app\db\mis\M42__supplier_contacts.sql.

   The live system runs the M-series against Sharbatly_MIS (schema [qms]
   behind dbo synonyms). This file exists only so the older private
   SharbatlyQMS database can be brought level; that database is retired, so
   this is unapplied unless it is revived. Idempotent.
   ===================================================================== */

IF OBJECT_ID(N'qms_supplier', N'U') IS NULL
BEGIN
    CREATE TABLE qms_supplier (
        vendor_no       NVARCHAR(40)  NOT NULL CONSTRAINT PK_qms_supplier PRIMARY KEY,
        name            NVARCHAR(200) NOT NULL,
        origin          NVARCHAR(80)  NULL,
        contact_person  NVARCHAR(200) NULL,
        is_active       BIT           NOT NULL CONSTRAINT DF_qms_supplier_active DEFAULT (1),
        source          VARCHAR(10)   NOT NULL,
        created_at      DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_created DEFAULT (SYSUTCDATETIME()),
        created_by      NVARCHAR(80)  NOT NULL,
        updated_at      DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_updated DEFAULT (SYSUTCDATETIME()),
        updated_by      NVARCHAR(80)  NOT NULL
    );
END;
GO

IF OBJECT_ID(N'qms_supplier_email', N'U') IS NULL
BEGIN
    CREATE TABLE qms_supplier_email (
        email_id    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_supplier_email PRIMARY KEY,
        vendor_no   NVARCHAR(40)  NOT NULL
            CONSTRAINT FK_qms_supplier_email_supplier REFERENCES qms_supplier (vendor_no) ON DELETE CASCADE,
        email       NVARCHAR(320) NOT NULL,
        created_at  DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_email_created DEFAULT (SYSUTCDATETIME()),
        created_by  NVARCHAR(80)  NOT NULL,
        CONSTRAINT UQ_qms_supplier_email UNIQUE (vendor_no, email)
    );
END;
GO

INSERT INTO qms_supplier (vendor_no, name, source, created_by, updated_by)
SELECT a.vendor_no, MAX(ISNULL(NULLIF(a.vendor_name, ''), a.vendor_no)), 'arrival', 'V57', 'V57'
FROM   qms_arrival a
WHERE  ISNULL(a.vendor_no, '') <> ''
  AND  NOT EXISTS (SELECT 1 FROM qms_supplier s WHERE s.vendor_no = a.vendor_no)
GROUP  BY a.vendor_no;
GO

MERGE qms_screen AS t
USING (VALUES
    ('Parameters.SupplierContacts', N'Supplier contacts', 'Parameters', 390, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO
