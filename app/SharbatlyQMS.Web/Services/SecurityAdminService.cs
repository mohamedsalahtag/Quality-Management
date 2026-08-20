using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;

namespace SharbatlyQMS.Web.Services;

/// <summary>Read/write side of the Security screen. Kept out of the resolver so
/// the hot read path stays a lock-free dictionary lookup.</summary>
public interface ISecurityAdminService
{
    Task<IReadOnlyList<RoleUsage>> ListRolesWithUsageAsync();

    /// <summary>Replaces one role's entire grant set. Absent = revoked.</summary>
    Task<(bool ok, string? error)> SaveGrantsAsync(
        string roleCode, IReadOnlyDictionary<string, AccessLevel> grants, string actor, string? actorRole);

    Task<(bool ok, string? error, string? roleCode)> CreateRoleAsync(
        string displayName, string? description, bool isPlantScoped, string? copyFromRoleCode, string actor);

    Task<(bool ok, string? error)> UpdateRoleAsync(
        string roleCode, string displayName, string? description, bool isPlantScoped, bool isActive, string actor);

    Task<(bool ok, string? error)> DeleteRoleAsync(string roleCode, string actor);
}

public sealed class RoleUsage
{
    public string  RoleCode      { get; set; } = "";
    public string  DisplayName   { get; set; } = "";
    public string? Description   { get; set; }
    public int     Rank          { get; set; }
    public bool    IsBuiltIn     { get; set; }
    public bool    IsSuper       { get; set; }
    public bool    IsPlantScoped { get; set; }
    public bool    IsActive      { get; set; }
    public int     UserCount     { get; set; }
    public int     GrantCount    { get; set; }
}

public sealed class SecurityAdminService : ISecurityAdminService
{
    private readonly string _cs;
    private readonly IPermissionResolver _perms;
    private readonly IAuditService _audit;
    private readonly IPermissionLogService _permLog;

    public SecurityAdminService(IConfiguration cfg, IPermissionResolver perms,
        IAuditService audit, IPermissionLogService permLog)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _perms = perms;
        _audit = audit;
        _permLog = permLog;
    }

    /// <summary>Friendly label for a permission code, from the live catalogue.
    /// Falls back to the code so a permission retired between the change and the
    /// read still shows something.</summary>
    private string PermissionLabel(string code) =>
        _perms.Permissions.FirstOrDefault(p =>
            string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? code;

    private string RoleDisplayName(string roleCode) =>
        _perms.Roles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? roleCode;

    private SqlConnection Open() => new(_cs);

    public async Task<IReadOnlyList<RoleUsage>> ListRolesWithUsageAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<RoleUsage>(@"
            SELECT r.role_code AS RoleCode, r.display_name AS DisplayName, r.description AS Description,
                   r.rank AS Rank, r.is_builtin AS IsBuiltIn, r.is_super AS IsSuper,
                   r.is_plant_scoped AS IsPlantScoped, r.is_active AS IsActive,
                   -- Count users by their EFFECTIVE role (qms.AppUser collapses a
                   -- user to their highest-ranked Qc role), so this badge matches
                   -- the members list exactly. Counting portal.UserRole rows
                   -- instead double-counts anyone holding more than one Qc role.
                   (SELECT COUNT(*) FROM qms.AppUser au WHERE au.RoleCode = r.role_code) AS UserCount,
                   (SELECT COUNT(*) FROM qms_role_permission rp WHERE rp.role_code = r.role_code) AS GrantCount
            FROM   qms_role r
            ORDER  BY r.rank DESC, r.display_name");
        return rows.ToList();
    }

    public async Task<(bool ok, string? error)> SaveGrantsAsync(
        string roleCode, IReadOnlyDictionary<string, AccessLevel> grants, string actor, string? actorRole)
    {
        var role = _perms.Roles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase));
        if (role == null) return (false, "That role no longer exists.");

        // ---- Lockout guard 1: never let anyone edit their own role's grants in
        //      the same request. The most common way to lose the Security screen
        //      is to untick something on the role you are signed in as.
        if (string.Equals(actorRole, roleCode, StringComparison.OrdinalIgnoreCase))
            return (false, "You cannot change the permissions of the role you are signed in as. " +
                           "Switch to another administrator, or edit a different role.");

        // ---- Lockout guard 2: somebody must still be able to reach this screen.
        var wouldKeepSecurity = grants.TryGetValue(Perm.Admin.SecurityEdit, out var lvl) && lvl == AccessLevel.Edit;
        if (!wouldKeepSecurity && !await AnotherRoleCanAdministerAsync(roleCode))
            return (false, "This is the only role that can manage security, and it is assigned to an active user. " +
                           "Give another role the Security permission first.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var before = (await c.QueryAsync<(string PermissionCode, byte AccessLevel)>(
            "SELECT permission_code, access_level FROM qms_role_permission WHERE role_code = @roleCode",
            new { roleCode }, tx)).ToDictionary(g => g.PermissionCode, g => g.AccessLevel);

        await c.ExecuteAsync("DELETE FROM qms_role_permission WHERE role_code = @roleCode", new { roleCode }, tx);

        foreach (var (code, level) in grants.Where(g => g.Value != AccessLevel.None))
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_role_permission (role_code, permission_code, access_level, granted_by)
                SELECT @roleCode, @code, @level, @actor
                WHERE  EXISTS (SELECT 1 FROM qms_permission WHERE permission_code = @code)",
                new { roleCode, code, level = (byte)level, actor }, tx);
        }

        var after = grants.Where(g => g.Value != AccessLevel.None)
                          .ToDictionary(g => g.Key, g => (byte)g.Value, StringComparer.OrdinalIgnoreCase);

        await _audit.WriteAsync(c, tx, EntityTypes.Role, 0, ActionCodes.Updated,
            oldValues: new { roleCode, grants = before },
            newValues: new { roleCode, grants = after },
            actor: actor);

        // The readable record: one row per permission whose level actually
        // moved. The audit row above keeps the whole map, but nobody can read a
        // 200-key diff -- and because the map serialises in dictionary order, a
        // save that changed nothing still shows old != new there. Comparing
        // levels here means a no-op save writes nothing at all.
        var roleName = RoleDisplayName(roleCode);
        var touched  = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase);
        var entries  = new List<PermissionLogEntry>();
        foreach (var code in touched)
        {
            var oldLevel = before.TryGetValue(code, out var b) ? AccessLevels.FromDb(b) : AccessLevel.None;
            var newLevel = after.TryGetValue(code, out var a)  ? AccessLevels.FromDb(a) : AccessLevel.None;
            if (oldLevel == newLevel) continue;
            entries.Add(new PermissionLogEntry
            {
                ChangedBy   = actor,
                SubjectType = PermissionSubjects.Role,
                SubjectKey  = roleCode,
                SubjectName = roleName,
                ChangeType  = PermissionChangeTypes.Permission,
                ItemCode    = code,
                ItemName    = PermissionLabel(code),
                OldValue    = AccessLevels.Label(oldLevel),
                NewValue    = AccessLevels.Label(newLevel)
            });
        }
        await _permLog.WriteAsync(c, tx, entries);
        tx.Commit();

        await _perms.RefreshAsync();
        return (true, null);
    }

    /// <summary>True when some OTHER active role can manage security and has at
    /// least one active user holding it.</summary>
    private async Task<bool> AnotherRoleCanAdministerAsync(string excludingRoleCode)
    {
        using var c = Open();
        var n = await c.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM   qms_role r
            JOIN   qms_role_permission rp ON rp.role_code = r.role_code
                                         AND rp.permission_code = @perm AND rp.access_level = 2
            WHERE  r.is_active = 1
              AND  r.role_code <> @excluding
              AND  EXISTS (SELECT 1 FROM qms.AppUser u
                           WHERE u.RoleCode = r.role_code AND u.IsActive = 1)",
            new { perm = Perm.Admin.SecurityEdit, excluding = excludingRoleCode });
        // The built-in administrator can always reach the screen through the
        // hard-coded floor, so an active QcAdmin holder counts even with no row.
        if (n > 0) return true;
        if (string.Equals(excludingRoleCode, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase)) return false;
        return await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms.AppUser WHERE RoleCode = @admin AND IsActive = 1",
            new { admin = RoleCodes.Admin }) > 0;
    }

    public async Task<(bool ok, string? error, string? roleCode)> CreateRoleAsync(
        string displayName, string? description, bool isPlantScoped, string? copyFromRoleCode, string actor)
    {
        displayName = (displayName ?? "").Trim();
        if (displayName.Length == 0) return (false, "A role name is required.", null);
        if (displayName.Length > 60)  return (false, "Role names are limited to 60 characters.", null);

        // Codes are derived, never typed: the Qc prefix is the ONLY thing keeping
        // QMS roles from colliding with the SCM application's in the shared
        // portal.Role table, and a hand-typed code would eventually lose it.
        var slug = new string(displayName.Where(char.IsLetterOrDigit).ToArray());
        if (slug.Length == 0) return (false, "The role name needs at least one letter or number.", null);
        if (slug.Length > 40) slug = slug[..40];
        var roleCode = RoleCodes.Prefix + char.ToUpperInvariant(slug[0]) + slug[1..];

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var taken = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM portal.Role WHERE RoleCode = @roleCode", new { roleCode }, tx);
        if (taken > 0) { tx.Rollback(); return (false, $"A role called '{displayName}' already exists.", null); }

        // IsSystem = 1 is not cosmetic: the SCM application's role-delete
        // endpoint refuses IsSystem rows, and it is the only thing stopping an
        // administrator over there from deleting a QMS role.
        await c.ExecuteAsync(@"
            INSERT INTO portal.Role (RoleCode, DisplayName, IsSupplierRole, IsSystem)
            VALUES (@roleCode, @portalName, 0, 1);",
            new { roleCode, portalName = "Quality - " + displayName }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_role (role_code, display_name, legacy_name, rank, is_builtin,
                                  is_super, is_plant_scoped, description, created_by, updated_by)
            VALUES (@roleCode, @displayName, NULL, 0, 0, 0, @isPlantScoped, @description, @actor, @actor);",
            new { roleCode, displayName, isPlantScoped, description, actor }, tx);

        if (!string.IsNullOrWhiteSpace(copyFromRoleCode))
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_role_permission (role_code, permission_code, access_level, granted_by)
                SELECT @roleCode, permission_code, access_level, @actor
                FROM   qms_role_permission WHERE role_code = @copyFrom;",
                new { roleCode, copyFrom = copyFromRoleCode, actor }, tx);
        }

        await _audit.WriteAsync(c, tx, EntityTypes.Role, 0, ActionCodes.Created,
            oldValues: null,
            newValues: new { roleCode, displayName, description, isPlantScoped, copiedFrom = copyFromRoleCode },
            actor: actor);

        await _permLog.WriteAsync(c, tx, new[]
        {
            new PermissionLogEntry
            {
                ChangedBy   = actor,
                SubjectType = PermissionSubjects.Role,
                SubjectKey  = roleCode,
                SubjectName = displayName,
                ChangeType  = PermissionChangeTypes.RoleCreated,
                NewValue    = string.IsNullOrWhiteSpace(copyFromRoleCode)
                    ? "Created"
                    : $"Created, copying permissions from {copyFromRoleCode}"
            }
        });
        tx.Commit();

        await _perms.RefreshAsync();
        return (true, null, roleCode);
    }

    public async Task<(bool ok, string? error)> UpdateRoleAsync(
        string roleCode, string displayName, string? description, bool isPlantScoped, bool isActive, string actor)
    {
        displayName = (displayName ?? "").Trim();
        if (displayName.Length == 0) return (false, "A role name is required.");

        var role = _perms.Roles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase));
        if (role == null) return (false, "That role no longer exists.");

        // Deactivating a role removes its holders from qms.AppUser entirely --
        // they would not be able to sign in at all, not merely lose buttons.
        if (!isActive && role.IsActive)
        {
            using var probe = Open();
            var holders = await probe.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM portal.UserRole WHERE RoleCode = @roleCode", new { roleCode });
            if (holders > 0)
                return (false, $"{holders} user(s) still hold this role. Move them to another role before deactivating it.");
        }
        if (role.IsBuiltIn && !isActive)
            return (false, "Built-in roles cannot be deactivated.");

        // Explicit open + transaction + commit, matching every other write in
        // this service. (An earlier version relied on Dapper's implicit auto-open
        // with no transaction.)
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            UPDATE qms_role
            SET    display_name = @displayName, description = @description,
                   is_plant_scoped = @isPlantScoped, is_active = @isActive,
                   updated_at = SYSUTCDATETIME(), updated_by = @actor
            WHERE  role_code = @roleCode",
            new { roleCode, displayName, description, isPlantScoped, isActive, actor }, tx);

        // This path wrote NOTHING to any log before M21, even though it is where
        // a role is deactivated -- which removes every holder from qms.AppUser
        // and stops them signing in -- and where plant-scoping is flipped, which
        // changes every holder's data visibility. One row per field that moved.
        var settingChanges = new List<PermissionLogEntry>();
        void Track(string field, string label, string? was, string? now)
        {
            if (string.Equals(was ?? "", now ?? "", StringComparison.Ordinal)) return;
            settingChanges.Add(new PermissionLogEntry
            {
                ChangedBy   = actor,
                SubjectType = PermissionSubjects.Role,
                SubjectKey  = roleCode,
                SubjectName = displayName,
                ChangeType  = PermissionChangeTypes.RoleSetting,
                ItemCode    = field,
                ItemName    = label,
                OldValue    = was,
                NewValue    = now
            });
        }
        Track("display_name",    "Role name",    role.DisplayName,            displayName);
        Track("description",     "Description",  role.Description,            description);
        Track("is_plant_scoped", "Plant-scoped", YesNo(role.IsPlantScoped),   YesNo(isPlantScoped));
        Track("is_active",       "Active",       YesNo(role.IsActive),        YesNo(isActive));
        await _permLog.WriteAsync(c, tx, settingChanges);

        tx.Commit();

        await _perms.RefreshAsync();
        return (true, null);
    }

    private static string YesNo(bool b) => b ? "Yes" : "No";

    public async Task<(bool ok, string? error)> DeleteRoleAsync(string roleCode, string actor)
    {
        var role = _perms.Roles.FirstOrDefault(r =>
            string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase));
        if (role == null) return (false, "That role no longer exists.");
        if (role.IsBuiltIn) return (false, "Built-in roles cannot be deleted.");

        using var c = Open();
        var holders = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM portal.UserRole WHERE RoleCode = @roleCode", new { roleCode });
        if (holders > 0)
            return (false, $"{holders} user(s) still hold this role. Move them to another role first.");

        if (!await AnotherRoleCanAdministerAsync(roleCode))
            return (false, "Deleting this role would leave nobody able to manage security.");

        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Snapshot the grants INSIDE the transaction, before the DELETE below,
        // so the permission log can record what this role could do. Read after
        // the delete and it is already gone.
        var doomedGrants = (await c.QueryAsync<(string PermissionCode, byte AccessLevel)>(
            "SELECT permission_code, access_level FROM qms_role_permission WHERE role_code = @roleCode",
            new { roleCode }, tx)).ToList();

        // Grants cascade from qms_role, and qms_role cascades from portal.Role --
        // but delete in dependency order anyway so a missing cascade surfaces as
        // a foreign-key error rather than as an orphaned grant.
        await c.ExecuteAsync("DELETE FROM qms_role_permission WHERE role_code = @roleCode", new { roleCode }, tx);
        await c.ExecuteAsync("DELETE FROM qms_role WHERE role_code = @roleCode AND is_builtin = 0", new { roleCode }, tx);
        // Scoped to Qc% and IsSystem is set on our own rows, so this can never
        // reach a role belonging to the other application.
        await c.ExecuteAsync(
            "DELETE FROM portal.Role WHERE RoleCode = @roleCode AND RoleCode LIKE 'Qc%'", new { roleCode }, tx);

        await _audit.WriteAsync(c, tx, EntityTypes.Role, 0, ActionCodes.Deleted,
            oldValues: new { roleCode, role.DisplayName, role.Description }, newValues: null, actor: actor);

        // One row per permission the role held. Without this a deleted role's
        // access is irrecoverable -- the audit snapshot above records only the
        // name and description, and the grant rows are gone.
        var lost = doomedGrants.Select(g => new PermissionLogEntry
        {
            ChangedBy   = actor,
            SubjectType = PermissionSubjects.Role,
            SubjectKey  = roleCode,
            SubjectName = role.DisplayName,
            ChangeType  = PermissionChangeTypes.RoleDeleted,
            ItemCode    = g.PermissionCode,
            ItemName    = PermissionLabel(g.PermissionCode),
            OldValue    = AccessLevels.Label(AccessLevels.FromDb(g.AccessLevel)),
            NewValue    = "Role deleted"
        }).ToList();
        lost.Insert(0, new PermissionLogEntry
        {
            ChangedBy   = actor,
            SubjectType = PermissionSubjects.Role,
            SubjectKey  = roleCode,
            SubjectName = role.DisplayName,
            ChangeType  = PermissionChangeTypes.RoleDeleted,
            OldValue    = $"{doomedGrants.Count} permission(s)",
            NewValue    = "Role deleted"
        });
        await _permLog.WriteAsync(c, tx, lost);
        tx.Commit();

        await _perms.RefreshAsync();
        return (true, null);
    }
}
