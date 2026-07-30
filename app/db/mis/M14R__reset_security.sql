-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M14R  -  Reset the built-in role grants
-- ============================================================================
--  RECOVERY SCRIPT, not part of the normal migration sequence. Apply it only
--  when the six built-in roles have been mis-edited on the Security screen and
--  you want them back as they shipped.
--
--      cd W:\app\SharbatlyQMS.Migrate
--      dotnet run -- apply "<connection string>" W:\app\db\mis\M14R__reset_security.sql
--
--  It restores each built-in role's grants from qms_permission.seed_roles --
--  the record, written when each permission was first discovered, of which
--  roles it was granted to on day one. So this is a true "as delivered" restore
--  and stays correct as new permissions are added, with no hard-coded list to
--  fall out of date.
--
--  CUSTOM ROLES ARE NOT TOUCHED. Only rows whose role_code is a built-in are
--  deleted and rebuilt; anything an administrator composed survives intact.
--
--  If you cannot even reach the Security screen, you do not need this script --
--  QcAdmin is a hard-coded bypass in the application and never consults the
--  grant table. Restore an administrator instead:
--      DELETE FROM portal.UserRole WHERE UserId = @id AND RoleCode LIKE 'Qc%';
--      INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind)
--      VALUES (@id, 'QcAdmin', 'MANUAL');
--
--  Idempotent: safe to re-run.
-- ============================================================================

SET XACT_ABORT ON;
BEGIN TRAN;

-- What we are about to replace, for the record.
SELECT CONCAT('before: builtinGrants=',
  (SELECT COUNT(*) FROM qms.qms_role_permission rp
   JOIN qms.qms_role r ON r.role_code = rp.role_code WHERE r.is_builtin = 1),
  ' customGrants=',
  (SELECT COUNT(*) FROM qms.qms_role_permission rp
   JOIN qms.qms_role r ON r.role_code = rp.role_code WHERE r.is_builtin = 0)) AS check_item;

DELETE rp
FROM   qms.qms_role_permission rp
JOIN   qms.qms_role r ON r.role_code = rp.role_code
WHERE  r.is_builtin = 1;

-- Rebuild from the recorded seed. seed_roles is a comma-separated list of
-- built-in role codes; STRING_SPLIT is unavailable on SQL Server 2016 SP1 at
-- this compatibility level, so match on the delimited string instead.
--
-- Screen permissions on a levelled screen are restored at Edit for every role
-- except QcViewer, which is Read by definition. Everything else is Edit.
INSERT INTO qms.qms_role_permission (role_code, permission_code, access_level, granted_by)
SELECT r.role_code,
       p.permission_code,
       CASE WHEN p.kind = 'Screen' AND s.supports_access_level = 1
                 AND r.role_code = 'QcViewer'
            THEN 1 ELSE 2 END,
       'reset-M14R'
FROM   qms.qms_permission p
JOIN   qms.qms_screen     s ON s.screen_key = p.screen_key
JOIN   qms.qms_role       r ON r.is_builtin = 1
WHERE  p.is_obsolete = 0
  AND  p.seed_roles IS NOT NULL
  AND  ',' + p.seed_roles + ',' LIKE '%,' + r.role_code + ',%';

SELECT CONCAT('after: builtinGrants=',
  (SELECT COUNT(*) FROM qms.qms_role_permission rp
   JOIN qms.qms_role r ON r.role_code = rp.role_code WHERE r.is_builtin = 1),
  ' customGrants=',
  (SELECT COUNT(*) FROM qms.qms_role_permission rp
   JOIN qms.qms_role r ON r.role_code = rp.role_code WHERE r.is_builtin = 0),
  ' permsWithNoSeed=',
  (SELECT COUNT(*) FROM qms.qms_permission WHERE is_obsolete = 0 AND seed_roles IS NULL)) AS check_item;

COMMIT;
GO

-- The application caches grants in memory and refreshes them when the Security
-- screen saves. After running this script, restart the SharbatlyQMS service (or
-- save any role once) so the new grants take effect.
SELECT 'Restart the SharbatlyQMS service, or save any role on the Security screen, to pick this up.' AS check_item;
