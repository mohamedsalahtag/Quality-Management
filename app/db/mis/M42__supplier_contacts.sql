-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M42  -  Supplier contacts
-- ============================================================================
--  Parameters -> Supplier contacts: the suppliers QMS mails quality reports
--  to, each with as many addresses as it needs. Replaces M19's single address
--  per vendor (qms_vendor_contact), which could only be filled from the send
--  dialog one address at a time.
--
--    qms_supplier        one row per SAP vendor number. Seeded here from every
--                        vendor we have received a container from; the app
--                        adds new ones as their containers arrive, and the
--                        screen's Excel import adds those with none yet.
--    qms_supplier_email  the addresses. The send dialog puts ALL of them in To.
--
--  The four addresses already saved in qms_vendor_contact are copied across.
--  qms_vendor_contact itself is left in place, unread, so nothing is lost.
--
--  The screen row must exist BEFORE the binaries are deployed: permissions are
--  discovered from the controller attributes at startup, and qms_permission
--  carries a foreign key to qms_screen (see M31 for what happens without it).
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_supplier', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_supplier (
        vendor_no       NVARCHAR(40)  NOT NULL CONSTRAINT PK_qms_supplier PRIMARY KEY,
        name            NVARCHAR(200) NOT NULL,
        origin          NVARCHAR(80)  NULL,
        contact_person  NVARCHAR(200) NULL,
        is_active       BIT           NOT NULL CONSTRAINT DF_qms_supplier_active DEFAULT (1),
        -- Where the row first came from: 'arrival' (a container), 'excel'
        -- (the contacts import) or 'manual' (added on the screen).
        source          VARCHAR(10)   NOT NULL,
        created_at      DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_created DEFAULT (SYSUTCDATETIME()),
        created_by      NVARCHAR(80)  NOT NULL,
        updated_at      DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_updated DEFAULT (SYSUTCDATETIME()),
        updated_by      NVARCHAR(80)  NOT NULL
    );
END
GO

IF OBJECT_ID('qms.qms_supplier_email', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_supplier_email (
        email_id    INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_qms_supplier_email PRIMARY KEY,
        vendor_no   NVARCHAR(40)  NOT NULL
            CONSTRAINT FK_qms_supplier_email_supplier REFERENCES qms.qms_supplier (vendor_no) ON DELETE CASCADE,
        email       NVARCHAR(320) NOT NULL,
        created_at  DATETIME2(0)  NOT NULL CONSTRAINT DF_qms_supplier_email_created DEFAULT (SYSUTCDATETIME()),
        created_by  NVARCHAR(80)  NOT NULL,
        CONSTRAINT UQ_qms_supplier_email UNIQUE (vendor_no, email)
    );
END
GO

-- The app addresses tables by bare name (see M05), so it needs the dbo synonyms.
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_supplier' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_supplier FOR qms.qms_supplier;
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_supplier_email' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_supplier_email FOR qms.qms_supplier_email;
GO

-- Every supplier we have received a container from.
INSERT INTO qms.qms_supplier (vendor_no, name, source, created_by, updated_by)
SELECT a.vendor_no, MAX(ISNULL(NULLIF(a.vendor_name, ''), a.vendor_no)), 'arrival', 'M42', 'M42'
FROM   qms.qms_arrival a
WHERE  ISNULL(a.vendor_no, '') <> ''
  AND  NOT EXISTS (SELECT 1 FROM qms.qms_supplier s WHERE s.vendor_no = a.vendor_no)
GROUP  BY a.vendor_no;
GO

-- The addresses saved one at a time from the send dialog (M19).
IF OBJECT_ID('qms.qms_vendor_contact', 'U') IS NOT NULL
BEGIN
    INSERT INTO qms.qms_supplier (vendor_no, name, source, created_by, updated_by)
    SELECT vc.vendor_no, vc.vendor_no, 'manual', vc.updated_by, vc.updated_by
    FROM   qms.qms_vendor_contact vc
    WHERE  NOT EXISTS (SELECT 1 FROM qms.qms_supplier s WHERE s.vendor_no = vc.vendor_no);

    INSERT INTO qms.qms_supplier_email (vendor_no, email, created_at, created_by)
    SELECT vc.vendor_no, LOWER(LTRIM(RTRIM(vc.email))), vc.updated_at, vc.updated_by
    FROM   qms.qms_vendor_contact vc
    WHERE  NOT EXISTS (SELECT 1 FROM qms.qms_supplier_email e
                       WHERE e.vendor_no = vc.vendor_no AND e.email = LOWER(LTRIM(RTRIM(vc.email))));
END
GO

MERGE qms.qms_screen AS t
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

SELECT 'suppliers' AS check_item, CAST(COUNT(*) AS varchar(20)) AS value FROM qms.qms_supplier
UNION ALL
SELECT 'supplier_emails', CAST(COUNT(*) AS varchar(20)) FROM qms.qms_supplier_email
UNION ALL
SELECT 'dbo_synonyms_present', CAST(COUNT(*) AS varchar(20)) FROM sys.synonyms WHERE name IN ('qms_supplier', 'qms_supplier_email')
UNION ALL
SELECT 'supplier_contacts_screen',
       CASE WHEN EXISTS (SELECT 1 FROM qms.qms_screen WHERE screen_key = 'Parameters.SupplierContacts')
            THEN 'present' ELSE 'MISSING' END;
GO
