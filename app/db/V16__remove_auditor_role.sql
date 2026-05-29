-- ===================================================================
-- V16  Remove the Auditor role
--
-- The Auditor role was introduced by V15 (audit-trail feature) but its
-- distinct value was eliminated when the global /Audit page and Excel
-- export became SiteAdmin-only on 2026-05-21. Removing it from CK_Users_Role
-- keeps the role-set tight (Viewer / Operator / Manager / ClaimManager /
-- SiteAdmin -- back to 5).
--
-- Defensive: demote any existing Auditor user to Manager BEFORE dropping
-- the value from the constraint, otherwise the recreate would fail. At
-- time of writing, zero users have role=Auditor in production, but the
-- demote runs anyway so this migration is safe to re-apply against any
-- environment that ever issued the role.
-- ===================================================================

UPDATE Users SET Role = 'Manager' WHERE Role = 'Auditor';
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO

ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Operator','Viewer'));
GO

-- Sanity report
SELECT 'CK_Users_Role no longer contains Auditor' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE name = 'CK_Users_Role' AND definition LIKE '%Auditor%')
            THEN 'STILL PRESENT' ELSE 'OK' END AS status
UNION ALL
SELECT 'Users with role=Auditor (should be 0)',
       CAST((SELECT COUNT(*) FROM Users WHERE Role = 'Auditor') AS NVARCHAR(20));
GO
