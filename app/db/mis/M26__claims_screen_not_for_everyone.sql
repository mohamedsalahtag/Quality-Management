-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M26  -  Claims is not a screen for everyone
-- ============================================================================
--  The Claims screen was declared with Seed.Everyone, which means all six
--  built-in roles received a day-one grant the first time the catalogue was
--  reconciled. The result on the live database:
--
--      QcOperator    Claims  2 (Edit)
--      QcSupervisor  Claims  2 (Edit)
--      QcViewer      Claims  1 (Read)
--
--  None of them hold any Claims.* ACTION permission, so they could open the
--  page and read every claim decision without being able to act on one --
--  visibility nobody intended to grant. The attribute now declares
--  Seed.ManagerOrClaimManagerOrAdmin, which fixes a fresh installation, but an
--  existing permission is deliberately never re-seeded (that is what stops a
--  restart undoing an administrator's revoke), so the rows already granted have
--  to be removed here.
--
--  Revoking is a DELETE, matching what the Security screen itself does when a
--  checkbox is cleared (SecurityAdminService.SaveGrantsAsync deletes the role's
--  rows and re-inserts the ticked ones). An administrator can grant it back on
--  the Security screen at any time; this migration does not prevent that.
--
--  Only the three roles listed are touched. QcAdmin, QcManager and
--  QcClaimManager keep everything they hold.
--
--  Idempotent: safe to re-run.
-- ============================================================================

DECLARE @roles TABLE (role_code VARCHAR(64) PRIMARY KEY);
INSERT INTO @roles (role_code) VALUES ('QcOperator'), ('QcSupervisor'), ('QcViewer');

SELECT 'before_screen_grants' AS check_item, COUNT(*) AS value
FROM   qms.qms_role_permission
WHERE  permission_code = 'Claims' AND role_code IN (SELECT role_code FROM @roles);
GO

DECLARE @roles TABLE (role_code VARCHAR(64) PRIMARY KEY);
INSERT INTO @roles (role_code) VALUES ('QcOperator'), ('QcSupervisor'), ('QcViewer');

-- The screen itself, plus any Claims.* action grant these roles may have
-- picked up. (None do today; included so a re-run after a mistaken grant
-- still leaves the intended state.)
DELETE FROM qms.qms_role_permission
WHERE  role_code IN (SELECT role_code FROM @roles)
  AND  (permission_code = 'Claims' OR permission_code LIKE 'Claims.%');
GO

-- Sanity report --------------------------------------------------------
SELECT 'claims_grants_remaining' AS check_item,
       role_code                 AS role,
       permission_code           AS permission,
       access_level              AS level
FROM   qms.qms_role_permission
WHERE  permission_code = 'Claims' OR permission_code LIKE 'Claims.%'
ORDER  BY role_code, permission_code;
-- Expect QcAdmin, QcManager and QcClaimManager only.
GO
