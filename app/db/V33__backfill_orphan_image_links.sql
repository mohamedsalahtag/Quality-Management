-- ===================================================================
-- V33 (2026-06-20).  Backfill qms_image_link rows for asset rows that
-- were created without a matching link because of a long-standing bug
-- in ImagesController.Upload:
--   ASP.NET model binding silently converted the empty form field
--   `category=` to NULL; the subsequent INSERT into qms_image_link
--   (image_category VARCHAR(40) NOT NULL) failed, leaving the asset
--   row + the file on disk but no link visible in any gallery.
--
-- The fix is shipped in code (ImageService.UploadAsync now coalesces
-- category to ""). This migration recovers the orphan asset rows by
-- parsing the owner_type + owner_id from the storage_url path
-- (/uploads/<type>/<id>/<filename>).
--
-- Idempotent: re-running only touches asset rows that still have no
-- link (and skips ones we can't parse confidently).
-- ===================================================================

;WITH parsed AS (
    SELECT  a.image_id,
            a.storage_url,
            a.uploaded_at,
            a.uploaded_by,
            -- Strip leading "/uploads/", then split on the next "/".
            SUBSTRING(a.storage_url, 10,
                      CHARINDEX('/', a.storage_url, 10) - 10) AS owner_type,
            CHARINDEX('/', a.storage_url, 10) AS slash1
    FROM    qms_image_asset a
    LEFT JOIN qms_image_link l ON l.image_id = a.image_id
    WHERE   l.image_link_id IS NULL
      AND   a.storage_url LIKE '/uploads/%/%/%'
), parsed2 AS (
    SELECT  p.*,
            TRY_CAST(SUBSTRING(p.storage_url, p.slash1 + 1,
                               CHARINDEX('/', p.storage_url, p.slash1 + 1) - p.slash1 - 1
                              ) AS BIGINT) AS owner_id
    FROM    parsed p
)
INSERT INTO qms_image_link
    (image_id, owner_type, owner_id, image_category, display_order,
     caption, include_in_report, created_at, created_by)
SELECT  image_id,
        owner_type,
        owner_id,
        '' AS image_category,
        0,
        NULL,
        1,
        uploaded_at,
        uploaded_by
FROM    parsed2
WHERE   owner_id IS NOT NULL
  AND   owner_type IN
        ('Arrival','ArrivalChecklist','QualityOrder','QualityOrderMaterial','Sample');
GO

PRINT '  backfilled ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' orphan link row(s).';
GO
