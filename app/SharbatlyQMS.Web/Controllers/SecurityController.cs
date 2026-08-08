using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Compose roles from permissions, and assign what each screen and button
/// requires. A controller of its own rather than another section of
/// AdminController, which is already 1,900 lines.
/// </summary>
[Authorize]
public class SecurityController : Controller
{
    private readonly IPermissionResolver _perms;
    private readonly IUserPermissions _me;
    private readonly ISecurityAdminService _admin;
    private readonly PermissionCatalog _catalog;
    private readonly IDbService _db;

    public SecurityController(IPermissionResolver perms, IUserPermissions me,
        ISecurityAdminService admin, PermissionCatalog catalog, IDbService db)
    {
        _perms = perms; _me = me; _admin = admin; _catalog = catalog; _db = db;
    }

    private string Actor => User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

    [HttpGet]
    [RequireScreen(Screens.AdminSecurity, Seed.AdminOnly, "Open the Security screen")]
    public async Task<IActionResult> Index(string? role, bool showObsolete = false, string? tab = null)
    {
        await _perms.EnsureLoadedAsync();
        var roles = await _admin.ListRolesWithUsageAsync();

        var selected = roles.FirstOrDefault(r => string.Equals(r.RoleCode, role, StringComparison.OrdinalIgnoreCase))
                       ?? roles.FirstOrDefault(r => !r.IsBuiltIn)
                       ?? roles.FirstOrDefault();

        var perms   = _perms.Permissions.Where(p => showObsolete || !p.IsObsolete).ToList();
        var screens = _perms.Screens;
        var readOnlyCodes = _catalog.ReadOnlyActionCodes();

        // The built-in Administrator (is_super) holds every permission implicitly
        // and can never be edited here -- render it fully granted and locked.
        var selectedIsSuper = selected != null && _perms.IsSuperRole(selected.RoleCode);

        var cards = new List<ScreenCardVm>();
        foreach (var s in screens)
        {
            var forScreen = perms.Where(p => string.Equals(p.ScreenKey, s.ScreenKey, StringComparison.OrdinalIgnoreCase)).ToList();
            if (forScreen.Count == 0) continue;

            var screenPerm = forScreen.FirstOrDefault(p => p.Kind == "Screen");
            cards.Add(new ScreenCardVm
            {
                ScreenKey           = s.ScreenKey,
                Title               = s.DisplayName,
                GroupName           = s.GroupName,
                SupportsAccessLevel = s.SupportsAccessLevel,
                HasScreenPermission = screenPerm != null,
                ScreenLevel         = screenPerm == null || selected == null
                                        ? AccessLevel.None
                                        : selectedIsSuper ? AccessLevel.Edit
                                        : _perms.RawLevel(selected.RoleCode, screenPerm.Code),
                Actions = forScreen.Where(p => p.Kind == "Action")
                    .OrderBy(p => p.SortOrder).ThenBy(p => p.DisplayName)
                    .Select(p => new PermissionRowVm
                    {
                        Code             = p.Code,
                        DisplayName      = p.DisplayName,
                        IsObsolete       = p.IsObsolete,
                        Granted          = selected != null && (selectedIsSuper || _perms.RawLevel(selected.RoleCode, p.Code) != AccessLevel.None),
                        IsLocked         = selectedIsSuper || IsFloor(selected?.RoleCode, p.Code),
                        IsReadOnlyAction = readOnlyCodes.Contains(p.Code)
                    })
                    .ToList()
            });
        }

        var matrixRows = perms
            .OrderBy(p => screens.FirstOrDefault(s => s.ScreenKey == p.ScreenKey)?.SortOrder ?? 999)
            .ThenBy(p => p.Kind == "Screen" ? 0 : 1).ThenBy(p => p.SortOrder).ThenBy(p => p.DisplayName)
            .Select(p =>
            {
                var owner = screens.FirstOrDefault(s => string.Equals(s.ScreenKey, p.ScreenKey, StringComparison.OrdinalIgnoreCase));
                return new MatrixRowVm
                {
                    ScreenTitle = owner?.DisplayName ?? p.ScreenKey,
                    ScreenKey   = p.ScreenKey,
                    Code        = p.Code,
                    DisplayName = p.DisplayName,
                    Kind        = p.Kind,
                    IsObsolete  = p.IsObsolete,
                    SupportsAccessLevel = p.Kind == "Screen" && (owner?.SupportsAccessLevel ?? false),
                    IsReadOnlyAction    = readOnlyCodes.Contains(p.Code),
                    Cells = roles.Select(r =>
                    {
                        var locked = r.IsSuper || IsFloor(r.RoleCode, p.Code);
                        return new MatrixCellVm
                        {
                            Locked = locked,
                            Level  = locked ? AccessLevel.Edit : _perms.RawLevel(r.RoleCode, p.Code)
                        };
                    }).ToList()
                };
            })
            .ToList();

        // ---- Users tab: who holds the selected role. Read-only: this screen
        //      shows the members, the Users screen is where they are managed. ----
        IReadOnlyList<Models.User> members = selected == null
            ? Array.Empty<Models.User>()
            : await _db.ListUsersAsync(null, selected.RoleCode, null);

        // Open the top-level "Compare all roles" tab on load when asked (e.g. the
        // redirect after a matrix save), otherwise the Role editor.
        ViewBag.OpenCompare = string.Equals(tab, "compare", StringComparison.OrdinalIgnoreCase);
        ViewBag.ActorRole   = _me.ActualRole;

        return View(new SecurityVm
        {
            Roles          = roles,
            Selected       = selected,
            Cards          = cards,
            MatrixRows     = matrixRows,
            Members         = members,
            ActiveTab       = string.Equals(tab, "users", StringComparison.OrdinalIgnoreCase) ? "users" : "permissions",
            AnyObsolete    = _perms.Permissions.Any(p => p.IsObsolete),
            ShowObsolete   = showObsolete,
            // Nothing has been granted to any role yet: a permission nobody can
            // reach is invisible, so it is worth calling out.
            NewSinceReview = _perms.Permissions.Count(p => !p.IsObsolete
                                && !_perms.Grants.Any(g => g.Value.ContainsKey(p.Code))),
            EditingOwnRole = selected != null
                             && string.Equals(selected.RoleCode, _me.ActualRole, StringComparison.OrdinalIgnoreCase),
            SelectedIsSuper = selectedIsSuper,
            PermissionsLoadedAt = _perms.LoadedAtUtc
        });
    }

    /// <summary>The administrator floor, rendered as locked-and-ticked cells so
    /// the screen tells the truth about what it cannot change.</summary>
    private static bool IsFloor(string? roleCode, string code) =>
        string.Equals(roleCode, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase)
        && Array.Exists(Perm.AdminFloor, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));

    // The form posts one "granted" value per ticked action, and one "screenLevel"
    // value of the form "<screenKey>=<1|2>" per screen. Absent means revoked --
    // sparse, so a revoke is simply the missing value.
    private static Dictionary<string, AccessLevel> BuildGrantMap(string?[]? granted, string?[]? screenLevel)
    {
        var map = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in granted ?? Array.Empty<string?>())
            if (!string.IsNullOrWhiteSpace(code)) map[code.Trim()] = AccessLevel.Edit;

        foreach (var raw in screenLevel ?? Array.Empty<string?>())
        {
            var parts = (raw ?? "").Split('=', 2);
            if (parts.Length != 2) continue;
            var level = parts[1] == "2" ? AccessLevel.Edit : parts[1] == "1" ? AccessLevel.Read : AccessLevel.None;
            if (level != AccessLevel.None) map[parts[0]] = level;
        }
        return map;
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> SaveGrants(string roleCode, string[]? granted, string[]? screenLevel)
    {
        var (ok, error) = await _admin.SaveGrantsAsync(roleCode, BuildGrantMap(granted, screenLevel), Actor, _me.ActualRole);
        if (ok) TempData["Success"] = "Permissions saved. They take effect on the next page anyone loads.";
        else    TempData["Error"]   = error;
        return RedirectToAction(nameof(Index), new { role = roleCode });
    }

    // Bulk editor: the "Compare all roles" grid posts one set of granted_<role> +
    // screenLevel_<role> fields per role column. Each role is saved through the
    // same SaveGrantsAsync (and its lockout guards) as the single-role editor, so
    // the matrix can never do anything the per-role editor could not.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> SaveMatrix()
    {
        // Only roles whose column was actually rendered may be saved. Without
        // this a role missing from the grid would post no fields and be wiped to
        // an empty grant set. An empty set for a PRESENT column is intentional
        // (the user set that role to nothing).
        var present = new HashSet<string>(Request.Form["role_present"].OfType<string>(), StringComparer.OrdinalIgnoreCase);

        var saved = 0; var skipped = 0; var errors = new List<string>();
        foreach (var role in _perms.Roles.Where(r => r.IsActive))
        {
            if (!present.Contains(role.RoleCode)) continue;

            // Super roles (the built-in Administrator) are read-only everywhere,
            // and the role you are signed in as can't be edited here (same rule
            // the per-role editor enforces). Skip both quietly.
            if (_perms.IsSuperRole(role.RoleCode)) { skipped++; continue; }
            if (string.Equals(role.RoleCode, _me.ActualRole, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

            var granted     = Request.Form[$"granted_{role.RoleCode}"].ToArray();
            var screenLevel = Request.Form[$"screenLevel_{role.RoleCode}"].ToArray();

            var (ok, error) = await _admin.SaveGrantsAsync(role.RoleCode, BuildGrantMap(granted, screenLevel), Actor, _me.ActualRole);
            if (ok) saved++;
            else    errors.Add($"{role.DisplayName}: {error}");
        }

        if (errors.Count > 0)
            TempData["Error"] = $"Saved {saved} role(s). Could not save: {string.Join("; ", errors)}";
        else
            TempData["Success"] = $"Saved permissions for {saved} role(s)."
                + (skipped > 0 ? " Your own role was left unchanged." : "")
                + " They take effect on the next page anyone loads.";
        return RedirectToAction(nameof(Index), new { tab = "compare" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> CreateRole(string displayName, string? description,
        bool isPlantScoped, string? copyFrom)
    {
        var (ok, error, roleCode) = await _admin.CreateRoleAsync(displayName, description, isPlantScoped, copyFrom, Actor);
        if (ok)
        {
            TempData["Success"] = string.IsNullOrWhiteSpace(copyFrom)
                ? $"Role '{displayName}' created. Tick the permissions it should have, then save."
                : $"Role '{displayName}' created with a copy of the permissions from the role you picked.";
            return RedirectToAction(nameof(Index), new { role = roleCode });
        }
        TempData["Error"] = error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> UpdateRole(string roleCode, string displayName, string? description,
        bool isPlantScoped, bool isActive)
    {
        var (ok, error) = await _admin.UpdateRoleAsync(roleCode, displayName, description, isPlantScoped, isActive, Actor);
        if (ok) TempData["Success"] = "Role updated.";
        else    TempData["Error"]   = error;
        return RedirectToAction(nameof(Index), new { role = roleCode });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> DeleteRole(string roleCode)
    {
        var (ok, error) = await _admin.DeleteRoleAsync(roleCode, Actor);
        if (ok) TempData["Success"] = "Role deleted.";
        else    TempData["Error"]   = error;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Re-scans the application for permissions. Runs at startup too;
    /// this is here so a deploy does not have to be waited out.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SecurityEdit, Seed.AdminOnly, "Create, edit and compose roles")]
    public async Task<IActionResult> Rescan()
    {
        await _catalog.ReconcileAsync();
        TempData["Success"] = "Permission list refreshed from the application.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Preview the application as another role. Declared here so the
    /// permission belongs to the Security screen, where an administrator looks
    /// for it; the endpoint itself lives on AccountController.</summary>
    [HttpGet]
    [RequirePermission(Perm.Admin.ViewAs, Seed.AdminOnly, "Preview the site as another role", ReadOnly = true)]
    public IActionResult ViewAsInfo() => RedirectToAction(nameof(Index));
}
