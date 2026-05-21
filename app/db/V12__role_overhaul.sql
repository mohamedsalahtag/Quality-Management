-- ===================================================================
-- V12  Role model overhaul.
--
-- Replaces the old three-role set (QCStaff / QCManager / SiteAdmin)
-- with the new four-role set used by the role matrix:
--   * SiteAdmin -- everything incl. admin pages
--   * Manager   -- everything except admin pages
--   * Operator  -- everything except admin pages and parameters pages
--   * Viewer    -- read-only (arrivals, QOs, dashboards, reports)
--
-- Existing rows are remapped: QCStaff -> Operator, QCManager -> Manager.
-- SiteAdmin is unchanged. Default for new rows becomes Viewer (least
-- privilege).
-- ===================================================================

-- 1. Remap existing rows before we change the CHECK -- otherwise the
--    new CHECK would reject the old values.
UPDATE Users SET Role = CASE Role
    WHEN 'QCStaff'   THEN 'Operator'
    WHEN 'QCManager' THEN 'Manager'
    ELSE Role
END
WHERE Role IN ('QCStaff','QCManager');
GO

-- 2. Drop the old CHECK constraint by its known name.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO

-- 3. Drop the inline DEFAULT 'QCStaff' (SQL Server auto-names it; look it up).
DECLARE @dfName SYSNAME;
SELECT @dfName = dc.name
FROM   sys.default_constraints dc
JOIN   sys.columns c ON c.default_object_id = dc.object_id
WHERE  c.object_id = OBJECT_ID('dbo.Users')
  AND  c.name = 'Role';
IF @dfName IS NOT NULL
    EXEC('ALTER TABLE Users DROP CONSTRAINT ' + @dfName);
GO

-- 4. New CHECK + new DEFAULT (Viewer is the least-privilege landing).
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','Operator','Viewer'));
GO

ALTER TABLE Users ADD CONSTRAINT DF_Users_Role DEFAULT 'Viewer' FOR Role;
GO

-- 5. Sanity report.
SELECT Role, COUNT(*) AS user_count
FROM   Users
GROUP  BY Role
ORDER  BY Role;
