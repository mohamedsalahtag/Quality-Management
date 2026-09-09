-- ===================================================================
-- V44  Claims is not a screen for everyone
--
-- The Claims screen was declared with Seed.Everyone, so all six built-in
-- roles received a day-one grant the first time the catalogue was
-- reconciled -- including QcOperator, QcSupervisor and QcViewer, none of
-- which hold any Claims.* action permission. They could read every claim
-- decision without being able to act on one.
--
-- The attribute now declares Seed.ManagerOrClaimManagerOrAdmin, which
-- fixes a fresh installation. An existing permission is deliberately
-- never re-seeded (that is what stops a restart undoing an
-- administrator's revoke), so the already-granted rows are removed here.
--
-- Revoking is a DELETE, matching what the Security screen does when a
-- checkbox is cleared. An administrator can grant it back at any time.
--
-- Idempotent.
-- ===================================================================

-- The security tables arrived with the MIS lineage (M14), so a database
-- still on the pre-overhaul dev schema simply has nothing to revoke.
IF OBJECT_ID(N'dbo.qms_role_permission') IS NOT NULL
    DELETE FROM dbo.qms_role_permission
    WHERE  role_code IN ('QcOperator', 'QcSupervisor', 'QcViewer')
      AND  (permission_code = 'Claims' OR permission_code LIKE 'Claims.%');
GO
