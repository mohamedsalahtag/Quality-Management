-- ===================================================================
-- V31  Submit/Finish workflow + Supervisor role.
--
-- 1. qms_quality_order: widen status_code CHECK to allow 'Submitted'.
--    Today's domain values: Initial, Open, Closed, Reopened, Cancelled.
--    After V31:             + Submitted.
--    'Closed' stays the persisted code; UI labels it as "Finished" via
--    QualityOrderStatus.DisplayName() so there's no data-migration ripple
--    (claim queries / dashboard groupings that match on 'Closed' keep
--    working).
--
-- 2. Users: widen Role CHECK to allow 'Supervisor'.
--    Today's domain values: SiteAdmin, Manager, ClaimManager, Operator,
--    Viewer (V13 set, after V16 dropped 'Auditor').
--    After V31:             + Supervisor.
--    Hierarchy (informally documented in UserRoles.cs):
--      Viewer < Operator < Supervisor < Manager < SiteAdmin
--    ClaimManager is a peer role parallel to Manager.
--
-- Idempotent: each block guards the DROP with an existence check so the
-- migration is safe to re-run.
-- ===================================================================

-- 1. Quality Order status: drop + re-add the CHECK so 'Submitted' is valid.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_qms_quality_order_status')
    ALTER TABLE qms_quality_order DROP CONSTRAINT CK_qms_quality_order_status;
GO
ALTER TABLE qms_quality_order ADD CONSTRAINT CK_qms_quality_order_status
    CHECK (status_code IN ('Initial','Open','Submitted','Closed','Reopened','Cancelled'));
GO

-- 2. Users.Role: drop + re-add the CHECK so 'Supervisor' is valid. Mirrors
--    the V12/V13/V15 pattern.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Supervisor','Operator','Viewer'));
GO
