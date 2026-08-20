-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M19  -  QMS-side supplier e-mail
-- ============================================================================
--  The "Send report to supplier" dialog has always opened with an empty To
--  field, and the reason is structural, not a bug: QMS reads vendors through
--  dbo.qms_sap_vendor_cache -> qms.VendorCatalog -> dbo.SAP_Vendors, and
--  dbo.SAP_Vendors has exactly two columns (VendorId, VendorName). The
--  ZQC_Data OData feed carries no e-mail field either (35 fields, checked
--  2026-08-20). So there is no SAP source for a supplier e-mail today, and the
--  operator retypes the address on every single send.
--
--  This table is the QMS-side store: one address per SAP vendor number, saved
--  from the send dialog and reused next time. It does NOT shadow SAP -- if a
--  vendor-master feed carrying e-mail is configured later, VendorService reads
--  SAP first and falls back to this table.
--
--  Idempotent: safe to re-run.
-- ============================================================================

IF OBJECT_ID('qms.qms_vendor_contact', 'U') IS NULL
BEGIN
    CREATE TABLE qms.qms_vendor_contact (
        vendor_no   NVARCHAR(40)  NOT NULL
            CONSTRAINT PK_qms_vendor_contact PRIMARY KEY,
        email       NVARCHAR(400) NOT NULL,
        updated_at  DATETIME2(0)  NOT NULL
            CONSTRAINT DF_qms_vendor_contact_updated DEFAULT SYSUTCDATETIME(),
        updated_by  NVARCHAR(80)  NOT NULL
    );
END
GO

-- The app addresses tables by bare name (see M05), so it needs the dbo synonym.
IF NOT EXISTS (SELECT 1 FROM sys.synonyms WHERE name = 'qms_vendor_contact' AND schema_id = SCHEMA_ID('dbo'))
    CREATE SYNONYM dbo.qms_vendor_contact FOR qms.qms_vendor_contact;
GO

SELECT 'vendor_contact_rows' AS check_item, COUNT(*) AS value FROM qms.qms_vendor_contact
UNION ALL
SELECT 'dbo_synonym_present', COUNT(*) FROM sys.synonyms WHERE name = 'qms_vendor_contact';
GO
