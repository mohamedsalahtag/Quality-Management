-- ============================================================================
--  SharbatlyQMS on Sharbatly_MIS  -  M06  -  QMS user projection
-- ============================================================================
--  QMS's Models/User.cs expects one flat row per user. That data now lives in
--  three places, so this view reassembles it:
--
--      portal.User       identity (username, display name, e-mail, active,
--                        last login, profile picture) - shared with the SCM app
--      qms.UserProfile   QMS-only attributes portal.User has no column for
--      portal.UserRole   role assignment, as namespaced Qc* codes
--      portal.UserPlant  the operator plant restriction
--
--  Only users holding at least one Qc* role appear. That keeps the QMS user
--  admin screen to quality staff rather than all 133 portal users, and means a
--  portal account with no quality role simply cannot sign in to QMS.
--
--  Role collapses to a single value because QMS's model carries one role per
--  user (a CHECK constraint enforced exactly that before the move). Highest
--  rank wins. AccountController additionally emits every Qc role the user holds
--  as a claim, so an account granted two roles still satisfies both policies.
-- ============================================================================

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
    r.Role,
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
    -- Highest-ranked Qc role the user holds, mapped back to the role name the
    -- application code uses (Models/User.cs UserRoles).
    SELECT TOP 1
           CASE ur.RoleCode
               WHEN 'QcAdmin'        THEN 'SiteAdmin'
               WHEN 'QcManager'      THEN 'Manager'
               WHEN 'QcClaimManager' THEN 'ClaimManager'
               WHEN 'QcSupervisor'   THEN 'Supervisor'
               WHEN 'QcOperator'     THEN 'Operator'
               ELSE 'Viewer'
           END AS Role
    FROM portal.UserRole AS ur
    WHERE ur.UserId = u.UserId
      AND ur.RoleCode LIKE 'Qc%'
    ORDER BY CASE ur.RoleCode
               WHEN 'QcAdmin'        THEN 6
               WHEN 'QcManager'      THEN 5
               WHEN 'QcClaimManager' THEN 4
               WHEN 'QcSupervisor'   THEN 3
               WHEN 'QcOperator'     THEN 2
               ELSE 1
             END DESC
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
