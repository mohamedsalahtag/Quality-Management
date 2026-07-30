-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M14  -  Security matrix (roles + permissions)
-- ============================================================================
--  Replaces six hard-coded role names and five RequireRole policies with an
--  administrator-owned model: roles composed from a catalogue of permissions,
--  one permission per screen and per button, plus a Read-only/Edit level on the
--  screens where records are edited.
--
--  WHERE THINGS LIVE, AND WHY
--
--  Roles stay in the SHARED portal.Role, Qc-prefixed, because portal.UserRole is
--  the assignment table and qms.AppUser is the login gate -- both shared, both
--  must keep working. Every Qc role is created with IsSystem = 1: the SCM app's
--  DELETE /roles/:role endpoint refuses IsSystem rows, and that flag is the only
--  thing standing between a custom QMS role and an SCM administrator deleting it.
--
--  The permission CATALOGUE and the GRANTS live here in [qms], never in
--  portal.Permission / portal.RolePermission. The SCM admin API returns
--  portal.Permission unfiltered and renders any unknown ModuleKey as an editable
--  tab, and SCM migration 121 set the precedent of deleting permission codes
--  that its own backend does not reference -- which QMS codes never would.
--  portal.RolePermission also has no column for the Read/Edit level.
--
--  BACKWARD COMPATIBILITY
--
--  qms.AppUser now resolves the role from data instead of a hard-coded CASE, so
--  a custom role finally means something. It emits THREE role columns:
--      Role      the legacy display name ('SiteAdmin', ...) so the currently
--                deployed binary keeps working unchanged after this migration
--      RoleCode  the portal.Role code -- what the next release authorises on
--      RoleName  the administrator-facing label
--  That overlap is deliberate: it lets the schema ship before the code.
--
--  CROSS APPLY and TOP 1 are preserved on purpose. "No Qc role => cannot sign in
--  to QMS" is a security property (see M06), and more than one row per user
--  would make Dapper's QuerySingleOrDefaultAsync throw inside cookie validation
--  -- a hard 500 on every request for that user.
--
--  BREAK-GLASS: if the security screen ever becomes unreachable, restore an
--  administrator directly. QcAdmin is a hard-coded bypass in the application and
--  never consults the grant table, so this always works:
--      DELETE FROM portal.UserRole WHERE UserId = @id AND RoleCode LIKE 'Qc%';
--      INSERT INTO portal.UserRole (UserId, RoleCode, SourceKind)
--      VALUES (@id, 'QcAdmin', 'MANUAL');
--  To undo a bad bulk edit of the built-in roles, apply M14R__reset_security.sql.
--
--  Idempotent: safe to re-run. Re-run M05__dbo_synonyms.sql afterwards so the
--  app's unqualified SQL resolves the four new tables.
-- ============================================================================

-- ---------------------------------------------------------------------------
--  1. Roles -- a QMS-owned sidecar on the shared portal.Role
-- ---------------------------------------------------------------------------
--  A sidecar rather than new columns on portal.Role: that table is shared with
--  the SCM app, and rank / is_super / is_plant_scoped are QMS-only concepts.
IF OBJECT_ID('qms.qms_role') IS NULL
BEGIN
    CREATE TABLE qms.qms_role (
        -- varchar(64) to match portal.Role.RoleCode exactly; SQL Server refuses
        -- a foreign key between columns of different width.
        role_code       varchar(64)   NOT NULL
            CONSTRAINT PK_qms_role PRIMARY KEY,
        display_name    nvarchar(100) NOT NULL,
        -- The name Models/User.cs used before this migration. NULL for custom
        -- roles; qms.AppUser falls back to role_code so nothing is ever blank.
        legacy_name     varchar(30)   NULL,
        -- Replaces the hard-coded ORDER BY CASE in qms.AppUser. Highest wins
        -- when a user somehow holds more than one Qc role.
        rank            int           NOT NULL
            CONSTRAINT DF_qms_role_rank DEFAULT 0,
        description     nvarchar(400) NULL,
        -- Built-ins cannot be renamed or deleted from the security screen.
        is_builtin      bit           NOT NULL CONSTRAINT DF_qms_role_builtin DEFAULT 0,
        -- QcAdmin only. Mirrors the application's hard-coded bypass for display
        -- purposes; the bypass itself is a string constant in code, because a
        -- data flag can be flipped by a bug and a constant cannot.
        is_super        bit           NOT NULL CONSTRAINT DF_qms_role_super DEFAULT 0,
        -- Whether holders are restricted to a single plant. Replaces the literal
        -- `role == "Operator"` test, which would silently give a custom
        -- operator-style role visibility of EVERY plant.
        is_plant_scoped bit           NOT NULL CONSTRAINT DF_qms_role_plant DEFAULT 0,
        is_active       bit           NOT NULL CONSTRAINT DF_qms_role_active DEFAULT 1,
        created_at      datetime2(7)  NOT NULL CONSTRAINT DF_qms_role_created DEFAULT SYSUTCDATETIME(),
        created_by      nvarchar(100) NULL,
        updated_at      datetime2(7)  NOT NULL CONSTRAINT DF_qms_role_updated DEFAULT SYSUTCDATETIME(),
        updated_by      nvarchar(100) NULL,
        CONSTRAINT CK_qms_role_prefix CHECK (role_code LIKE 'Qc%'),
        CONSTRAINT FK_qms_role_portal FOREIGN KEY (role_code)
            REFERENCES portal.Role (RoleCode) ON DELETE CASCADE
    );
END
GO

-- ---------------------------------------------------------------------------
--  2. Screens -- the curated top level of the permission tree
-- ---------------------------------------------------------------------------
IF OBJECT_ID('qms.qms_screen') IS NULL
BEGIN
    CREATE TABLE qms.qms_screen (
        screen_key            varchar(60)   NOT NULL
            CONSTRAINT PK_qms_screen PRIMARY KEY,
        display_name          nvarchar(100) NOT NULL,
        group_name            varchar(30)   NOT NULL,
        sort_order            int           NOT NULL
            CONSTRAINT DF_qms_screen_sort DEFAULT 500,
        -- 1 only on the six screens where records are edited. On those, a role
        -- set to Read cannot perform ANY action of that screen, whatever the
        -- individual button grants say.
        supports_access_level bit           NOT NULL
            CONSTRAINT DF_qms_screen_level DEFAULT 0,
        CONSTRAINT CK_qms_screen_group
            CHECK (group_name IN ('General','Records','Reports','Parameters','Admin'))
    );
END
GO

-- ---------------------------------------------------------------------------
--  3. Permission catalogue -- auto-maintained by the application at startup
-- ---------------------------------------------------------------------------
--  Rows are discovered by reflecting over every controller action, so a new
--  button is a permission the first time the app runs. Rows whose action has
--  disappeared are flagged is_obsolete, never deleted: their grants survive a
--  hotfix rollback, and deleting them would silently revoke on redeploy.
IF OBJECT_ID('qms.qms_permission') IS NULL
BEGIN
    CREATE TABLE qms.qms_permission (
        permission_code varchar(100)  NOT NULL
            CONSTRAINT PK_qms_permission PRIMARY KEY,
        screen_key      varchar(60)   NOT NULL,
        -- 'Screen' = the page itself (carries the Read/Edit level).
        -- 'Action'  = a button or function on it.
        kind            varchar(10)   NOT NULL,
        display_name    nvarchar(150) NOT NULL,
        sort_order      int           NOT NULL
            CONSTRAINT DF_qms_permission_sort DEFAULT 500,
        -- Which built-in roles this was granted to on first discovery, recorded
        -- so the seed is reproducible and auditable. Written once, on INSERT.
        seed_roles      varchar(200)  NULL,
        is_obsolete     bit           NOT NULL CONSTRAINT DF_qms_permission_obsolete DEFAULT 0,
        first_seen_at   datetime2(7)  NOT NULL CONSTRAINT DF_qms_permission_first DEFAULT SYSUTCDATETIME(),
        last_seen_at    datetime2(7)  NOT NULL CONSTRAINT DF_qms_permission_last  DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_qms_permission_kind CHECK (kind IN ('Screen','Action')),
        CONSTRAINT FK_qms_permission_screen FOREIGN KEY (screen_key)
            REFERENCES qms.qms_screen (screen_key)
    );
    CREATE INDEX IX_qms_permission_screen ON qms.qms_permission (screen_key, sort_order);
END
GO

-- ---------------------------------------------------------------------------
--  4. Grants -- only what IS granted, so revoke is a DELETE
-- ---------------------------------------------------------------------------
IF OBJECT_ID('qms.qms_role_permission') IS NULL
BEGIN
    CREATE TABLE qms.qms_role_permission (
        role_code       varchar(64)   NOT NULL,
        permission_code varchar(100)  NOT NULL,
        -- 1 = Read, 2 = Edit. Meaningful on Screen rows of a levelled screen;
        -- Action rows always carry 2. NO ROW AT ALL means no access.
        access_level    tinyint       NOT NULL
            CONSTRAINT DF_qms_role_permission_level DEFAULT 2,
        granted_at      datetime2(7)  NOT NULL
            CONSTRAINT DF_qms_role_permission_at DEFAULT SYSUTCDATETIME(),
        granted_by      nvarchar(100) NULL,
        CONSTRAINT PK_qms_role_permission PRIMARY KEY (role_code, permission_code),
        CONSTRAINT CK_qms_role_permission_level CHECK (access_level IN (1,2)),
        CONSTRAINT FK_qms_role_permission_role FOREIGN KEY (role_code)
            REFERENCES qms.qms_role (role_code) ON DELETE CASCADE,
        CONSTRAINT FK_qms_role_permission_perm FOREIGN KEY (permission_code)
            REFERENCES qms.qms_permission (permission_code) ON DELETE CASCADE
    );
    CREATE INDEX IX_qms_role_permission_perm ON qms.qms_role_permission (permission_code);
END
GO

-- ---------------------------------------------------------------------------
--  5. Seed the six built-in roles
-- ---------------------------------------------------------------------------
--  Ranks reproduce the hierarchy the hard-coded CASE in M06 encoded, so the
--  resolved role for every existing user is unchanged by this migration.
--  ClaimManager sits at 4 -- off the main axis, but above Supervisor, exactly
--  as before. is_plant_scoped is set only on QcOperator, matching the literal
--  role test in AccountController today.
MERGE qms.qms_role AS t
USING (VALUES
    ('QcAdmin',        N'Administrator',  'SiteAdmin',    6, 1, 1, 0, N'Full access, including users, settings and this security screen. Cannot be locked out.'),
    ('QcManager',      N'Manager',        'Manager',      5, 1, 0, 0, N'Reopens finished quality orders, edits catalogues and parameters, destructive operations.'),
    ('QcClaimManager', N'Claim Manager',  'ClaimManager', 4, 1, 0, 0, N'Approves or holds items in Claim Management.'),
    ('QcSupervisor',   N'Supervisor',     'Supervisor',   3, 1, 0, 0, N'Finishes submitted quality orders, edits completed arrivals, uses the reports.'),
    ('QcOperator',     N'Operator',       'Operator',     2, 1, 0, 1, N'Records readings, defects and samples. Restricted to a single plant.'),
    ('QcViewer',       N'Viewer',         'Viewer',       1, 1, 0, 0, N'Read-only access to arrivals, quality orders and samples.')
) AS s (role_code, display_name, legacy_name, rank, is_builtin, is_super, is_plant_scoped, description)
    ON t.role_code = s.role_code
WHEN NOT MATCHED THEN
    INSERT (role_code, display_name, legacy_name, rank, is_builtin, is_super, is_plant_scoped, description, created_by, updated_by)
    VALUES (s.role_code, s.display_name, s.legacy_name, s.rank, s.is_builtin, s.is_super, s.is_plant_scoped, s.description, 'seed-M14', 'seed-M14')
WHEN MATCHED AND t.is_builtin = 1 THEN
    -- Keep the structural columns authoritative on re-run; leave anything an
    -- administrator may have edited (description) alone once it exists.
    UPDATE SET legacy_name = s.legacy_name, rank = s.rank, is_super = s.is_super;
GO

-- Defensive backfill: any Qc role already in portal.Role with no sidecar row
-- would make its holders VANISH from qms.AppUser once the view joins qms_role.
-- Give it a row at rank 0 rather than losing the user.
INSERT INTO qms.qms_role (role_code, display_name, legacy_name, rank, is_builtin, description, created_by, updated_by)
SELECT r.RoleCode, r.DisplayName, NULL, 0, 0,
       N'Discovered in portal.Role during the M14 migration; review its permissions.',
       'seed-M14', 'seed-M14'
FROM   portal.Role r
WHERE  r.RoleCode LIKE 'Qc%'
  AND  NOT EXISTS (SELECT 1 FROM qms.qms_role q WHERE q.role_code = r.RoleCode);
GO

-- Every Qc role must be IsSystem = 1, or the SCM admin UI can delete it.
UPDATE portal.Role SET IsSystem = 1 WHERE RoleCode LIKE 'Qc%' AND IsSystem = 0;
GO

-- ---------------------------------------------------------------------------
--  6. Seed the screens
-- ---------------------------------------------------------------------------
--  supports_access_level = 1 on exactly the six screens where records are
--  edited. Everything else is all-or-nothing.
MERGE qms.qms_screen AS t
USING (VALUES
    ('General.Dashboard',           N'Dashboard',            'General',     10, 0),

    ('Arrivals.Pending',            N'Pending Containers',   'Records',    100, 1),
    ('Arrivals.Index',              N'Arrivals',             'Records',    110, 1),
    ('Arrivals.Search',             N'Search SAP directly',  'Records',    120, 0),
    ('Arrivals.Details',            N'Arrival details',      'Records',    130, 1),
    ('QualityOrders.Index',         N'Quality Orders',       'Records',    140, 1),
    ('QualityOrders.Details',       N'Quality Order details','Records',    150, 1),
    ('Claims',                      N'Claims',               'Records',    160, 1),

    ('Reports.DataHub',             N'Data hub',             'Reports',    200, 0),
    ('Reports.Builder',             N'Report builder',       'Reports',    210, 0),

    ('Parameters.DefectCatalog',    N'Defect Catalog',       'Parameters', 300, 0),
    ('Parameters.DefectCategories', N'Defect Categories',    'Parameters', 310, 0),
    ('Parameters.ReadingTypes',     N'Reading Types',        'Parameters', 320, 0),
    ('Parameters.SampleHeaders',    N'Sample Headers',       'Parameters', 330, 0),
    ('Parameters.ArrivalFields',    N'Arrival Fields',       'Parameters', 340, 0),
    ('Parameters.ReportUnits',      N'Report Units',         'Parameters', 350, 0),
    ('Parameters.CodeDescriptions', N'Code Descriptions',    'Parameters', 360, 0),
    ('Parameters.MailTemplate',     N'Mail Template',        'Parameters', 370, 0),

    ('Admin.Settings',              N'Site Configuration',   'Admin',      400, 0),
    ('Admin.Users',                 N'Users',                'Admin',      410, 0),
    ('Admin.AuditLog',              N'Audit Log',            'Admin',      420, 0),
    ('Admin.Security',              N'Security',             'Admin',      430, 0)
) AS s (screen_key, display_name, group_name, sort_order, supports_access_level)
    ON t.screen_key = s.screen_key
WHEN NOT MATCHED THEN
    INSERT (screen_key, display_name, group_name, sort_order, supports_access_level)
    VALUES (s.screen_key, s.display_name, s.group_name, s.sort_order, s.supports_access_level)
WHEN MATCHED THEN
    UPDATE SET display_name = s.display_name, group_name = s.group_name,
               sort_order = s.sort_order, supports_access_level = s.supports_access_level;
GO

-- ---------------------------------------------------------------------------
--  7. qms.AppUser -- resolve the role from data, not a hard-coded CASE
-- ---------------------------------------------------------------------------
CREATE OR ALTER VIEW qms.AppUser
AS
SELECT
    u.UserId,
    p.EmployeeId,
    u.Username,
    u.DisplayName                       AS FullName,
    u.Email,
    p.Department,
    u.ProfilePicturePath                AS ProfilePicture,
    -- QMS authenticates by AD bind only; the local BCrypt fallback is gone and
    -- portal.User's varbinary hash is a different scheme QMS never evaluates.
    CAST('' AS nvarchar(500))           AS PasswordHash,
    -- Three role columns on purpose -- see the header. `Role` keeps the legacy
    -- display name so the currently deployed binary is unaffected by M14;
    -- RoleCode is what the permission system authorises on.
    ISNULL(r.legacy_name, r.role_code)  AS Role,
    r.role_code                         AS RoleCode,
    r.display_name                      AS RoleName,
    r.is_plant_scoped                   AS IsPlantScoped,
    pl.Plant                            AS PlantCode,
    -- A QMS admin disabling someone must revoke QMS access WITHOUT locking them
    -- out of the SCM app, so "disabled in QMS" is recorded on qms.UserProfile
    -- and only combined with the shared flag for reading.
    CASE WHEN u.IsActive = 0 OR p.DisabledAt IS NOT NULL
         THEN CAST(0 AS bit) ELSE CAST(1 AS bit) END AS IsActive,
    ISNULL(p.IsOnline, CAST(0 AS bit))  AS IsOnline,
    u.LastLoginAt                       AS LastLogin,
    p.LastSeen,
    u.CreatedAt,
    p.CreatedBy,
    p.DisabledAt,
    p.DisabledBy
FROM portal.[User] AS u
LEFT JOIN qms.UserProfile AS p
       ON p.UserId = u.UserId
CROSS APPLY (
    -- Highest-ranked ACTIVE Qc role the user holds.
    --
    -- CROSS APPLY, not OUTER APPLY: a portal account with no Qc role must not
    -- be able to sign in to QMS. TOP 1: more than one row per user would make
    -- Dapper's QuerySingleOrDefaultAsync throw inside cookie validation, which
    -- is a hard 500 on every request for that user. The role_code tiebreak
    -- makes the choice deterministic when two roles share a rank.
    SELECT TOP 1 qr.role_code, qr.legacy_name, qr.display_name, qr.is_plant_scoped
    FROM   portal.UserRole AS ur
    JOIN   qms.qms_role    AS qr ON qr.role_code = ur.RoleCode AND qr.is_active = 1
    WHERE  ur.UserId = u.UserId
      AND  ur.RoleCode LIKE 'Qc%'
    ORDER  BY qr.rank DESC, qr.role_code
) AS r
OUTER APPLY (
    -- QMS supports a single plant restriction per user; portal.UserPlant is a
    -- many-to-many, so take the lowest code deterministically.
    SELECT TOP 1 up.Plant
    FROM portal.UserPlant AS up
    WHERE up.UserId = u.UserId
    ORDER BY up.Plant
) AS pl;
GO

-- ---------------------------------------------------------------------------
--  8. Verification -- appUsers MUST match the pre-migration baseline of 28.
--     A lower number means a Qc role has no qms_role row and its holders can
--     no longer sign in; re-run section 5's backfill.
-- ---------------------------------------------------------------------------
SELECT CONCAT(
  'appUsers=',    (SELECT COUNT(*) FROM qms.AppUser),
  ' roles=',      (SELECT COUNT(*) FROM qms.qms_role),
  ' builtins=',   (SELECT COUNT(*) FROM qms.qms_role WHERE is_builtin = 1),
  ' screens=',    (SELECT COUNT(*) FROM qms.qms_screen),
  ' levelled=',   (SELECT COUNT(*) FROM qms.qms_screen WHERE supports_access_level = 1),
  ' perms=',      (SELECT COUNT(*) FROM qms.qms_permission),
  ' grants=',     (SELECT COUNT(*) FROM qms.qms_role_permission),
  ' unmappedQc=', (SELECT COUNT(*) FROM portal.Role r WHERE r.RoleCode LIKE 'Qc%'
                   AND NOT EXISTS (SELECT 1 FROM qms.qms_role q WHERE q.role_code = r.RoleCode)),
  ' notIsSystem=',(SELECT COUNT(*) FROM portal.Role WHERE RoleCode LIKE 'Qc%' AND IsSystem = 0)
) AS check_item;
GO

-- Every user still resolves to the same role name the old hard-coded CASE gave.
-- Expect 0. Anything else means the ranks in section 5 are wrong.
SELECT CONCAT('roleResolutionChanged=', COUNT(*)) AS check_item
FROM   qms.AppUser a
CROSS  APPLY (
    SELECT TOP 1 CASE ur.RoleCode
               WHEN 'QcAdmin'        THEN 'SiteAdmin'
               WHEN 'QcManager'      THEN 'Manager'
               WHEN 'QcClaimManager' THEN 'ClaimManager'
               WHEN 'QcSupervisor'   THEN 'Supervisor'
               WHEN 'QcOperator'     THEN 'Operator'
               ELSE 'Viewer' END AS LegacyRole
    FROM portal.UserRole ur
    WHERE ur.UserId = a.UserId AND ur.RoleCode LIKE 'Qc%'
    ORDER BY CASE ur.RoleCode
               WHEN 'QcAdmin' THEN 6 WHEN 'QcManager' THEN 5
               WHEN 'QcClaimManager' THEN 4 WHEN 'QcSupervisor' THEN 3
               WHEN 'QcOperator' THEN 2 ELSE 1 END DESC) AS old
WHERE  a.Role <> old.LegacyRole;
