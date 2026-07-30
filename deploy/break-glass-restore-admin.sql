-- ============================================================================
--  BREAK GLASS  --  restore a QMS administrator
-- ============================================================================
--  Use this when nobody can reach Admin > Security any more: the last
--  administrator was demoted, a role was deleted, or a bad bulk edit removed
--  the permissions needed to undo itself.
--
--  Why this always works: QcAdmin is a HARD-CODED bypass inside the
--  application. It never consults the grant table, so an account holding
--  QcAdmin can always reach the Security screen no matter what the permission
--  data says. Restoring that role is therefore sufficient on its own.
--
--  HOW TO RUN
--      1. Find the UserId (step 1 below).
--      2. Set @UserId in step 2 and run steps 2 and 3.
--      3. Ask the person to sign out and back in. The role claim is refreshed
--         within 60 seconds anyway, but a fresh sign-in is immediate.
--
--  Run it with the migration tool, or any SQL client connected to
--  Sharbatly_MIS on KSAJEDSVSQL003:
--      cd W:\app\SharbatlyQMS.Migrate
--      dotnet run -- apply "<connection string>" W:\deploy\break-glass-restore-admin.sql
--
--  This touches portal.UserRole, which is SHARED with the SCM app. It only ever
--  removes rows whose RoleCode starts 'Qc', so the person's SCM access is not
--  affected.
--
--  Related: to undo a bad edit of the six BUILT-IN roles without changing who
--  is an administrator, apply W:\app\db\mis\M14R__reset_security.sql instead.
-- ============================================================================

-- ---- Step 1: who is there? Run this first and pick a UserId. ----------------
SELECT u.UserId, u.Username, u.DisplayName, u.IsActive,
       ISNULL(STUFF((SELECT ', ' + ur.RoleCode FROM portal.UserRole ur
                     WHERE ur.UserId = u.UserId AND ur.RoleCode LIKE 'Qc%'
                     ORDER BY ur.RoleCode FOR XML PATH('')), 1, 2, ''), '(none)') AS QcRoles
FROM   portal.[User] u
WHERE  u.IsActive = 1
  AND  EXISTS (SELECT 1 FROM portal.UserRole ur
               WHERE ur.UserId = u.UserId AND ur.RoleCode LIKE 'Qc%')
ORDER  BY u.Username;
GO

-- ---- Step 2: grant QcAdmin. EDIT @UserId BEFORE RUNNING. -------------------
--  Left as 0 on purpose so an accidental whole-file run changes nothing.
DECLARE @UserId int = 0;

IF @UserId = 0
BEGIN
    SELECT 'Set @UserId in step 2 first -- nothing was changed.' AS check_item;
END
ELSE IF NOT EXISTS (SELECT 1 FROM portal.[User] WHERE UserId = @UserId)
BEGIN
    SELECT CONCAT('No portal.User with UserId ', @UserId, ' -- nothing was changed.') AS check_item;
END
ELSE
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRAN;

    -- Make sure the role itself still exists and is protected from the SCM app.
    IF NOT EXISTS (SELECT 1 FROM portal.Role WHERE RoleCode = 'QcAdmin')
        INSERT INTO portal.Role (RoleCode, DisplayName, IsSupplierRole, IsSystem)
        VALUES ('QcAdmin', N'Quality - Administrator', 0, 1);
    ELSE
        UPDATE portal.Role SET IsSystem = 1 WHERE RoleCode = 'QcAdmin' AND IsSystem = 0;

    -- And that it has its QMS sidecar row, or qms.AppUser will not see the user
    -- at all and they still will not be able to sign in.
    IF NOT EXISTS (SELECT 1 FROM qms.qms_role WHERE role_code = 'QcAdmin')
        INSERT INTO qms.qms_role (role_code, display_name, legacy_name, rank,
                                  is_builtin, is_super, description, created_by, updated_by)
        VALUES ('QcAdmin', N'Administrator', 'SiteAdmin', 6, 1, 1,
                N'Restored by break-glass.', 'break-glass', 'break-glass');

    UPDATE qms.qms_role SET is_active = 1 WHERE role_code = 'QcAdmin' AND is_active = 0;

    -- One Qc role per user, so clear any others first. Scoped to 'Qc%' -- SCM
    -- role rows on the same person are left alone.
    DELETE FROM portal.UserRole WHERE UserId = @UserId AND RoleCode LIKE 'Qc%';
    INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind)
    VALUES (@UserId, 'QcAdmin', 'MANUAL');

    -- Being disabled in QMS would still keep them out.
    UPDATE qms.UserProfile SET DisabledAt = NULL, DisabledBy = NULL WHERE UserId = @UserId;
    UPDATE portal.[User]   SET IsActive = 1 WHERE UserId = @UserId AND IsActive = 0;

    COMMIT;

    SELECT CONCAT('Restored QcAdmin for UserId ', @UserId, '. Sign out and back in.') AS check_item;
END
GO

-- ---- Step 3: confirm the account can sign in -------------------------------
--  A user only appears in qms.AppUser if they hold an active Qc role. If the
--  row is missing here, they still cannot log in.
SELECT UserId, Username, Role, RoleCode, IsActive
FROM   qms.AppUser
WHERE  RoleCode = 'QcAdmin'
ORDER  BY Username;
