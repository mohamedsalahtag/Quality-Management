-- =====================================================================
-- V39  Document attachments (PDF / Word / Excel / CSV / TXT / MSG / ZIP)
--      (2026-07-15)
--
-- Arrivals could only carry photos (qms_image_asset + qms_image_link).
-- Inspectors also need real business documents on an arrival: supplier
-- invoices, packing lists, certificates, claim letters, temperature-logger
-- exports.
--
-- Why a separate table instead of reusing the image tables:
--
-- 1. _ImageGalleryGrid.cshtml renders <img> for EVERY qms_image_link row of
--    an owner, and ReportsController.PreprocessImagesAsync feeds each one to
--    ImageSharp. A PDF landing in qms_image_link would show as a broken
--    thumbnail on the Arrival / Sample / QO galleries and add noise to the
--    report pipeline. A separate table means zero existing query changes.
--
-- 2. The asset/link split exists so ONE photo can hang off several owners
--    without re-uploading. Documents have no such requirement, so the two
--    tables collapse into one and delete stays a plain soft-delete.
--
-- storage_path is a RELATIVE path (e.g. Arrival\17\<guid>.pdf) under the
-- documents root, NOT a URL. Documents are stored OUTSIDE wwwroot and served
-- only through the authenticated /Documents/Download/{id} action -- unlike
-- images, which static-file middleware serves to anyone with the URL.
--
-- owner_type is generic (Arrival | QualityOrder | Sample) so QO/Sample tabs
-- can be added later; only Arrival is reachable from the UI in V39.
-- =====================================================================

IF OBJECT_ID('qms_document', 'U') IS NULL
BEGIN
    CREATE TABLE qms_document (
        document_id        BIGINT IDENTITY(1,1) PRIMARY KEY,
        owner_type         VARCHAR(30)    NOT NULL,
        owner_id           BIGINT         NOT NULL,
        category           VARCHAR(40)    NOT NULL DEFAULT '',
        original_file_name NVARCHAR(255)  NOT NULL,
        content_type       VARCHAR(100)   NOT NULL,
        file_size_bytes    BIGINT         NOT NULL,
        storage_path       NVARCHAR(1000) NOT NULL,
        checksum_sha256    CHAR(64)       NULL,
        caption            NVARCHAR(255)  NULL,
        uploaded_at        DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME(),
        uploaded_by        NVARCHAR(80)   NOT NULL,
        is_deleted         BIT            NOT NULL DEFAULT 0,
        CONSTRAINT CK_qms_document_owner_type CHECK
            (owner_type IN ('Arrival','QualityOrder','Sample'))
    );
END
GO

-- Filtered on is_deleted = 0: every read path lists live documents for one
-- owner, so the deleted rows are dead weight in the index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_qms_document_owner'
               AND object_id = OBJECT_ID('qms_document'))
BEGIN
    CREATE INDEX IX_qms_document_owner ON qms_document(owner_type, owner_id)
        WHERE is_deleted = 0;
END
GO
