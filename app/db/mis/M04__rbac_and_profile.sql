-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M04  -  Identity, roles, permissions
-- ============================================================================
--  QMS no longer keeps its own Users table. Identity comes from portal.User
--  (which already carries the AD fields QMS needs: SamAccountName, LdapDn),
--  roles from portal.UserRole, and the per-user plant from portal.UserPlant.
--
--  Role codes are namespaced Qc* on purpose. portal.Role already defines
--  'Operator' and 'Supervisor' for the SCM *production* module; reusing those
--  would silently hand every production operator access to quality inspections.
--
--  Idempotent: safe to re-run.
-- ============================================================================

-- ---------------------------------------------------------------------------
-- 1) QMS-only user attributes that portal.User has no column for.
--    Everything portal.User already covers (Username, DisplayName, Email,
--    IsActive, LastLoginAt, ProfilePicturePath) is NOT repeated here.
--    PlantCode is not here either -- it maps onto portal.UserPlant.
-- ---------------------------------------------------------------------------
IF OBJECT_ID('qms.UserProfile') IS NULL
CREATE TABLE qms.UserProfile (
    UserId      int          NOT NULL CONSTRAINT PK_UserProfile PRIMARY KEY,
    EmployeeId  nvarchar(50) NULL,
    Department  nvarchar(200) NULL,
    IsOnline    bit          NOT NULL CONSTRAINT DF_UserProfile_IsOnline DEFAULT (0),
    LastSeen    datetime2(7) NULL,
    DisabledAt  datetime2(7) NULL,
    DisabledBy  int          NULL,
    CreatedBy   int          NULL,
    CONSTRAINT FK_UserProfile_User FOREIGN KEY (UserId) REFERENCES portal.[User](UserId)
);
GO

-- ---------------------------------------------------------------------------
-- 2) QMS roles.
--    Hierarchy (from Models/User.cs): Viewer < Operator < Supervisor < Manager
--    < Admin. ClaimManager is a peer of Manager scoped to Claim Management and
--    does NOT sit on the main axis -- a user may hold it alongside another Qc
--    role, which portal.UserRole supports natively.
-- ---------------------------------------------------------------------------
MERGE portal.Role AS t
USING (VALUES
    ('QcViewer',       N'Quality - Viewer'),
    ('QcOperator',     N'Quality - Operator'),
    ('QcSupervisor',   N'Quality - Supervisor'),
    ('QcManager',      N'Quality - Manager'),
    ('QcClaimManager', N'Quality - Claim Manager'),
    ('QcAdmin',        N'Quality - Administrator')
) AS s (RoleCode, DisplayName)
    ON t.RoleCode = s.RoleCode
WHEN NOT MATCHED THEN
    INSERT (RoleCode, DisplayName, IsSupplierRole, IsSystem)
    VALUES (s.RoleCode, s.DisplayName, 0, 1);
GO

-- ---------------------------------------------------------------------------
-- 3) QMS permissions, under ModuleKey 'qms' so they group in the shared
--    RBAC admin screen alongside core/production/sales/etc.
--
--    NOTE: the QMS app enforces access through its own AuthPolicies
--    (AdminOnly / ManagerOrAdmin / SupervisorOrAbove / OperatorOrAbove in
--    Models/User.cs), driven by the role claims it reads from portal.UserRole.
--    These permission rows document the model and make it visible/editable in
--    the shared admin UI; they are not themselves consulted at request time.
--    In particular QMS does NOT honour the ADMIN_ALL wildcard, so an SCM
--    Portal Administrator does not implicitly become a quality admin.
-- ---------------------------------------------------------------------------
MERGE portal.Permission AS t
USING (VALUES
    ('QMS_VIEW',         N'View arrivals, quality orders, samples and reports'),
    ('QMS_OPERATE',      N'Record readings, defects and samples on an open quality order'),
    ('QMS_SUPERVISE',    N'Finish a submitted QO, cancel-submit it, edit completed arrivals'),
    ('QMS_MANAGE',       N'Reopen finished QOs, edit catalogues and parameters, destructive operations'),
    ('QMS_CLAIM_MANAGE', N'Approve or hold items in Claim Management'),
    ('QMS_ADMIN',        N'Quality site administration: users, settings, global audit log')
) AS s (PermissionCode, Description)
    ON t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (PermissionCode, Description, ModuleKey)
    VALUES (s.PermissionCode, s.Description, 'qms');
GO

-- ---------------------------------------------------------------------------
-- 4) Role -> permission matrix.
-- ---------------------------------------------------------------------------
MERGE portal.RolePermission AS t
USING (VALUES
    ('QcViewer',       'QMS_VIEW'),

    ('QcOperator',     'QMS_VIEW'),
    ('QcOperator',     'QMS_OPERATE'),

    ('QcSupervisor',   'QMS_VIEW'),
    ('QcSupervisor',   'QMS_OPERATE'),
    ('QcSupervisor',   'QMS_SUPERVISE'),

    ('QcManager',      'QMS_VIEW'),
    ('QcManager',      'QMS_OPERATE'),
    ('QcManager',      'QMS_SUPERVISE'),
    ('QcManager',      'QMS_MANAGE'),

    ('QcClaimManager', 'QMS_VIEW'),
    ('QcClaimManager', 'QMS_CLAIM_MANAGE'),

    ('QcAdmin',        'QMS_VIEW'),
    ('QcAdmin',        'QMS_OPERATE'),
    ('QcAdmin',        'QMS_SUPERVISE'),
    ('QcAdmin',        'QMS_MANAGE'),
    ('QcAdmin',        'QMS_CLAIM_MANAGE'),
    ('QcAdmin',        'QMS_ADMIN')
) AS s (RoleCode, PermissionCode)
    ON t.RoleCode = s.RoleCode AND t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN
    INSERT (RoleCode, PermissionCode) VALUES (s.RoleCode, s.PermissionCode);
GO
