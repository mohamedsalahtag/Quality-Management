using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The one place a user's QMS role is provisioned or changed, shared by the
/// Users screen and the Security screen so both go through the same lockout
/// guard, the same audit shape, and — crucially — the same permission-snapshot
/// refresh. The resolver caches user→role in a singleton snapshot; a role change
/// that does not refresh it leaves the moved user on their old permissions until
/// the next restart, so every mutation here calls <c>RefreshAsync</c>.
/// </summary>
public interface IUserAdminService
{
    /// <summary>
    /// True when disabling, deleting or reassigning this user would leave nobody
    /// active who can reach the Security screen. With one role per user, "assign
    /// myself the new role to test it" is the single most likely way to lose
    /// administration entirely.
    /// </summary>
    Task<bool> IsLastSecurityAdminAsync(User user);

    /// <summary>Import a person from Active Directory and give them a role.</summary>
    Task<ProvisionResult> AddFromDirectoryAsync(
        string username, string roleCode, string? plantCode, int actorUserId, string actor);

    /// <summary>Reassign an existing user's role (and plant), with the lockout
    /// guard applied and the permission snapshot refreshed on success.</summary>
    Task<ProvisionResult> AssignRoleAsync(int userId, string roleCode, string? plantCode, string actor);
}

/// <summary>Outcome of a provisioning call. <see cref="EffectivePlant"/> is the
/// plant actually stored after the role's scoping rule was applied, so callers
/// can craft an accurate message without re-deriving it.</summary>
public sealed record ProvisionResult(bool Ok, string? Error = null, int UserId = 0, string? EffectivePlant = null);

public sealed class UserAdminService : IUserAdminService
{
    private readonly IDbService _db;
    private readonly IPermissionResolver _perms;
    private readonly ISettingsService _settings;
    private readonly IAdService _ad;
    private readonly IAuditService _audit;

    public UserAdminService(IDbService db, IPermissionResolver perms, ISettingsService settings,
        IAdService ad, IAuditService audit)
    {
        _db = db; _perms = perms; _settings = settings; _ad = ad; _audit = audit;
    }

    public async Task<bool> IsLastSecurityAdminAsync(User user)
    {
        if (!user.IsActive) return false;
        if (!_perms.RoleHasSecurityAdmin(user.RoleCode)) return false;

        var others = (await _db.ListUsersAsync(null, null, true))
            .Count(x => x.UserId != user.UserId && _perms.RoleHasSecurityAdmin(x.RoleCode));
        return others == 0;
    }

    public async Task<ProvisionResult> AddFromDirectoryAsync(
        string username, string roleCode, string? plantCode, int actorUserId, string actor)
    {
        // `roleCode` is validated against the role table so a role composed on the
        // Security screen is assignable straight away.
        var target = (await _db.ListAssignableRolesAsync())
            .FirstOrDefault(r => string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(username) || target == null)
            return new ProvisionResult(false, "A username and an active role are required.");

        // Plant scope follows the role's own flag. Stripped on the server as well
        // as hidden in the UI, so a crafted post cannot smuggle one in.
        var plant = target.IsPlantScoped && !string.IsNullOrWhiteSpace(plantCode) ? plantCode : null;

        var adCfg = await _settings.GetAdConfigAsync();
        if (!adCfg.IsConfigured)
            return new ProvisionResult(false,
                "Active Directory is not configured. Open Site Configuration → Active Directory first.");

        if (await _db.GetUserByUsernameAsync(username) != null)
            return new ProvisionResult(false, $"A user named '{username}' already exists.");

        // Fast path: the picker just rendered the full AD list and the service has
        // it cached. Slow path / fallback: one direct LDAP lookup keeps Add working
        // even after a restart between the picker open and the Add click.
        var cached = await _ad.ListUsersAsync(adCfg, null, max: int.MaxValue);
        var info = cached.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        info ??= await _ad.GetUserInfoAsync(username, adCfg);
        if (info == null)
            return new ProvisionResult(false, $"User '{username}' was not found in Active Directory.");

        var userId = await _db.CreateUserAsync(new User
        {
            Username      = info.Username,
            FullName      = info.FullName,
            Email         = info.Email,
            Department    = info.Department,
            Role          = target.LegacyName ?? target.RoleCode,
            RoleCode      = target.RoleCode,
            RoleName      = target.DisplayName,
            IsPlantScoped = target.IsPlantScoped,
            PlantCode     = plant,
            PasswordHash  = "",       // AD-managed; password lives in the directory
            IsActive      = true,
            CreatedBy     = actorUserId
        });

        await _audit.WriteAsync(EntityTypes.User, userId, ActionCodes.Created,
            null, new { info.Username, role = target.RoleCode, plantCode = plant }, actor);
        await _perms.RefreshAsync();
        return new ProvisionResult(true, null, userId, plant);
    }

    public async Task<ProvisionResult> AssignRoleAsync(int userId, string roleCode, string? plantCode, string actor)
    {
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return new ProvisionResult(false, "That user no longer exists.");

        var target = (await _db.ListAssignableRolesAsync())
            .FirstOrDefault(r => string.Equals(r.RoleCode, roleCode, StringComparison.OrdinalIgnoreCase));
        if (target == null) return new ProvisionResult(false, "That role does not exist or is not active.");

        var changingRole = !string.Equals(u.RoleCode, target.RoleCode, StringComparison.OrdinalIgnoreCase);

        // Somebody has to stay able to administer. Only block when the move would
        // actually remove the last security admin -- moving them to another role
        // that can also administer is safe.
        if (changingRole
            && !_perms.RoleHasSecurityAdmin(target.RoleCode)
            && await IsLastSecurityAdminAsync(u))
            return new ProvisionResult(false,
                $"'{u.Username}' is the only active user who can manage security. Give somebody else that access first.");

        var beforeRole = u.RoleCode; var beforePlant = u.PlantCode;
        u.RoleCode      = target.RoleCode;
        u.Role          = target.LegacyName ?? target.RoleCode;
        u.RoleName      = target.DisplayName;
        u.IsPlantScoped = target.IsPlantScoped;
        u.PlantCode     = target.IsPlantScoped && !string.IsNullOrWhiteSpace(plantCode) ? plantCode : null;

        await _db.UpdateUserAsync(u);
        await _audit.WriteAsync(EntityTypes.User, u.UserId, ActionCodes.Updated,
            new { role = beforeRole, plantCode = beforePlant },
            new { role = u.RoleCode, plantCode = u.PlantCode }, actor);
        await _perms.RefreshAsync();
        return new ProvisionResult(true, null, u.UserId, u.PlantCode);
    }
}
