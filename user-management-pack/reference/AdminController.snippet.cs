// AdminController - user management, technicians, AD config, AD browse,
// mass create. Reference snippet; merge with your existing AdminController.
//
// Hard rules from SPEC.md §6 enforced here:
//   - Self-protection on DeleteUser, ToggleUserAccess, BulkDeleteUsers.
//   - FK violations caught (SqlException 547) and shown as "disable instead".
//   - AD service password is "blank = keep" on save.
//   - AD browse is cached (10 min in MemoryCache, key "AdUsers_All").

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using HelpDesk.Models;
using HelpDesk.Services;

namespace HelpDesk.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/[action]")]
public partial class AdminController : Controller
{
    private readonly IDbService _db;
    private readonly IAdService _ad;
    private readonly IMemoryCache _cache;
    public AdminController(IDbService db, IAdService ad, IMemoryCache cache)
    {
        _db = db; _ad = ad; _cache = cache;
    }

    private const string AdUsersCacheKey = "AdUsers_All";

    // ============================================================
    // GET /Admin/Users - filtered table, no AD calls (instant)
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> Users(string? search, string? dept,
        string? roleFilter, string? statusFilter)
    {
        var sysUsers = await _db.GetAllUsersAsync();
        var rows = sysUsers.Select(u => new AdUser
        {
            Username       = u.AdUsername, FullName = u.FullName,
            Email          = u.Email,      Department = u.Department ?? "",
            EmployeeId     = u.EmployeeId ?? "",
            IsActive       = u.IsActive,
            IsRegistered   = true,         UserId = u.UserId,
            SystemRole     = u.Role,       SystemIsActive = u.IsActive
        }).ToList();

        if (!string.IsNullOrEmpty(search))
            rows = rows.Where(u =>
                u.FullName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                u.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(dept))
            rows = rows.Where(u => u.Department.Contains(dept, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(roleFilter))
            rows = rows.Where(u => u.SystemRole == roleFilter).ToList();
        if (!string.IsNullOrEmpty(statusFilter))
            rows = statusFilter switch
            {
                "enabled"  => rows.Where(u => u.SystemIsActive).ToList(),
                "disabled" => rows.Where(u => !u.SystemIsActive).ToList(),
                _          => rows
            };

        ViewBag.RoleFilter   = roleFilter;
        ViewBag.StatusFilter = statusFilter;
        ViewBag.Groups       = await _db.GetGroupsAsync();
        return View(new UserManagementViewModel { AllUsers = rows, Search = search, DeptFilter = dept });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleUserAccess(int userId, bool enable)
    {
        // Self-protection
        if (userId == GetUserId() && !enable)
        {
            TempData["Error"] = "You cannot disable your own account.";
            return RedirectToAction("Users");
        }
        var user = await _db.GetUserByIdAsync(userId);
        if (user == null) return NotFound();
        user.IsActive   = enable;
        user.DisabledAt = enable ? null : DateTime.UtcNow;
        user.DisabledBy = enable ? null : GetUserId();
        await _db.UpdateUserAsync(user);
        _cache.Remove(AdUsersCacheKey);
        TempData["Success"] = $"User {(enable ? "enabled" : "disabled")}.";
        return RedirectToAction("Users");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        if (userId == GetUserId())
        {
            TempData["Error"] = "You cannot delete your own account.";
            return RedirectToAction("Users");
        }
        var user = await _db.GetUserByIdAsync(userId);
        if (user == null) { TempData["Error"] = "User not found."; return RedirectToAction("Users"); }
        try
        {
            await _db.DeleteUserAsync(userId);
            TempData["Success"] = $"User '{user.FullName}' deleted.";
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
        {
            TempData["Error"] = $"Cannot delete '{user.FullName}' - still referenced. Disable instead.";
        }
        return RedirectToAction("Users");
    }

    // ============================================================
    // POST /Admin/BulkDeleteUsers - multi-select delete with self-skip.
    // SPEC.md §4.1 describes the UX; the front-end is in Users.snippet.cshtml.
    // ============================================================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> BulkDeleteUsers(int[] userIds)
    {
        if (userIds == null || userIds.Length == 0)
        {
            TempData["Error"] = "No users selected.";
            return RedirectToAction("Users");
        }
        var selfId = GetUserId();
        var deleted = new List<string>();
        var blocked = new List<string>();
        var skippedSelf = false;

        foreach (var id in userIds.Distinct())
        {
            if (id == selfId) { skippedSelf = true; continue; }
            var user = await _db.GetUserByIdAsync(id);
            if (user == null) continue;
            try { await _db.DeleteUserAsync(id); deleted.Add(user.FullName); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
                { blocked.Add(user.FullName); }
            catch { blocked.Add(user.FullName); }
        }

        var parts = new List<string>();
        if (deleted.Count > 0) parts.Add($"Deleted {deleted.Count}: {string.Join(", ", deleted)}");
        if (blocked.Count > 0) parts.Add($"Blocked {blocked.Count} (still referenced - disable instead): {string.Join(", ", blocked)}");
        if (skippedSelf)       parts.Add("Skipped your own account.");

        TempData[deleted.Count > 0 ? "Success" : "Error"] =
            parts.Count > 0 ? string.Join(" | ", parts) : "No users were deleted.";
        return RedirectToAction("Users");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateUserProfile(int userId, string employeeId,
        string fullName, string email, string department)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            TempData["Error"] = "Full name cannot be empty.";
            return RedirectToAction("Users");
        }
        await _db.UpdateUserProfileAsync(userId,
            employeeId?.Trim() ?? "", fullName.Trim(),
            email?.Trim() ?? "", department?.Trim() ?? "");
        TempData["Success"] = $"Profile updated for '{fullName.Trim()}'.";
        return RedirectToAction("Users");
    }

    // ============================================================
    // POST /Admin/GrantRole - change role + optional group assignment
    // ============================================================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> GrantRole(GrantRoleViewModel model)
    {
        var user = await _db.GetUserByIdAsync(model.UserId);
        if (user == null) return NotFound();
        user.Role = model.Role;
        await _db.UpdateUserAsync(user);
        if ((model.Role == UserRoles.Technician || model.Role == UserRoles.FirstLevelSupport)
            && model.GroupId.HasValue)
        {
            await _db.UpsertGroupMemberAsync(new TechnicianGroupMember
            {
                UserId = model.UserId, GroupId = model.GroupId.Value,
                CanDelete = model.CanDelete, CanAssign = model.CanAssign,
                CanPickupOthers = model.CanPickupOthers
            });
        }
        TempData["Success"] = "Role granted.";
        return RedirectToAction("Technicians");
    }

    // ============================================================
    // GET /Admin/Technicians - role-by-role view + group memberships
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> Technicians()
    {
        var allUsers   = await _db.GetAllUsersAsync();
        var groups     = await _db.GetGroupsAsync(false);
        var allMembers = new List<TechnicianGroupMember>();
        foreach (var g in groups)
        {
            var members = await _db.GetGroupMembersAsync(g.GroupId);
            foreach (var m in members) m.GroupName = g.GroupName;
            allMembers.AddRange(members);
        }
        ViewBag.AllUsers = allUsers.Where(u => u.IsActive).OrderBy(u => u.FullName).ToList();
        return View(new TechnicianManagementViewModel
        {
            Technicians = allUsers.Where(u =>
                u.Role is UserRoles.Technician or UserRoles.FirstLevelSupport).ToList(),
            Groups      = groups,
            Assignments = allMembers
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTechnicianAccess(int userId, int groupId,
        bool canDelete, bool canAssign, bool canPickupOthers)
    {
        await _db.UpsertGroupMemberAsync(new TechnicianGroupMember
        {
            UserId = userId, GroupId = groupId,
            CanDelete = canDelete, CanAssign = canAssign, CanPickupOthers = canPickupOthers
        });
        TempData["Success"] = "Access updated.";
        return RedirectToAction("Technicians");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveFromGroup(int userId, int groupId)
    {
        await _db.RemoveGroupMemberAsync(userId, groupId);
        TempData["Success"] = "Removed from group.";
        return RedirectToAction("Technicians");
    }

    // ============================================================
    // GET /Admin/AdManagement - AD config form
    // ============================================================
    [HttpGet]
    public async Task<IActionResult> AdManagement()
    {
        var cfg = await _db.GetAllConfigAsync();
        ViewBag.AdDomain     = cfg.GetValueOrDefault("AdDomain", "");
        ViewBag.AdLdapPath   = cfg.GetValueOrDefault("AdLdapPath", "");
        ViewBag.AdSvcUser    = cfg.GetValueOrDefault("AdServiceUser", "");
        ViewBag.AdSvcPassSet = !string.IsNullOrEmpty(cfg.GetValueOrDefault("AdServicePassword", ""));
        ViewBag.AllUsers     = await _db.GetAllUsersAsync();
        ViewBag.Groups       = await _db.GetGroupsAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveAdConfig(string adDomain, string adLdapPath,
        string adServiceUser, string? adServicePassword)
    {
        var uid = GetUserId();
        await _db.SetConfigAsync("AdDomain",      adDomain,      uid);
        await _db.SetConfigAsync("AdLdapPath",    adLdapPath,    uid);
        await _db.SetConfigAsync("AdServiceUser", adServiceUser, uid);
        if (!string.IsNullOrEmpty(adServicePassword))         // blank = keep existing
            await _db.SetConfigAsync("AdServicePassword", adServicePassword, uid);
        TempData["Success"] = "AD configuration saved.";
        return RedirectToAction("AdManagement");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestAdConnection()
    {
        var (ok, message) = await _ad.TestConnectionAsync();
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction("AdManagement");
    }

    // ============================================================
    // GET /Admin/AdUsers - cached AD browse (10-minute TTL)
    // ============================================================
    private async Task<List<AdUser>> GetCachedAdUsersAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cache.TryGetValue(AdUsersCacheKey, out object? obj)
            && obj is List<AdUser> cached)
            return cached;

        var adUsers = await _ad.GetAllActiveUsersAsync();
        if (adUsers.Any())
            adUsers = await _db.GetAdUsersWithRegistrationStatusAsync(adUsers);

        _cache.Set(AdUsersCacheKey, (object)adUsers,
            new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(10)));
        return adUsers;
    }

    [HttpGet]
    public async Task<IActionResult> AdUsers(string? search, string? dept, bool refresh = false)
    {
        List<AdUser> all;
        string? adError = null;
        try { all = await GetCachedAdUsersAsync(forceRefresh: refresh); }
        catch (Exception ex) { all = new(); adError = ex.Message; }

        var filtered = all.AsEnumerable();
        if (!string.IsNullOrEmpty(search))
            filtered = filtered.Where(u =>
                u.FullName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                u.Username.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(dept))
            filtered = filtered.Where(u => u.Department.Equals(dept, StringComparison.OrdinalIgnoreCase));

        ViewBag.Search      = search;
        ViewBag.DeptFilter  = dept;
        ViewBag.Departments = all.Select(u => u.Department)
                                 .Where(d => !string.IsNullOrEmpty(d))
                                 .Distinct().OrderBy(d => d).ToList();
        ViewBag.Groups      = await _db.GetGroupsAsync();
        ViewBag.AdError     = adError;
        ViewBag.TotalCount  = all.Count;
        return View(filtered.ToList());
    }

    // ============================================================
    // POST /Admin/MassCreateUsers - bulk import every enabled AD user
    // ============================================================
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MassCreateUsers(int? groupId)
    {
        List<AdUser> adUsers;
        try { adUsers = await GetCachedAdUsersAsync(); }
        catch
        {
            TempData["Error"] = "Could not retrieve AD users. Refresh AD list first.";
            return RedirectToAction("AdManagement");
        }

        var existingUsernames = (await _db.GetAllUsersAsync())
            .Select(u => u.AdUsername.ToLower()).ToHashSet();
        var domain   = await _db.GetConfigAsync("AdDomain") ?? "";
        var toCreate = adUsers.Where(u => u.IsActive
            && !existingUsernames.Contains(u.Username.ToLower())).ToList();

        int created = 0;
        foreach (var ad in toCreate)
        {
            try
            {
                var user = new User
                {
                    EmployeeId   = ad.Username,
                    AdUsername   = ad.Username,
                    FullName     = ad.FullName,
                    Email        = !string.IsNullOrEmpty(ad.Email) ? ad.Email : $"{ad.Username}@{domain}",
                    Department   = ad.Department,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
                    Role         = UserRoles.Requester,
                    IsActive     = true
                };
                var userId = await _db.CreateUserAsync(user);
                if (groupId.HasValue)
                    await _db.UpsertGroupMemberAsync(new TechnicianGroupMember
                    {
                        UserId = userId, GroupId = groupId.Value,
                        CanDelete = false, CanAssign = false, CanPickupOthers = false
                    });
                created++;
            }
            catch { /* skip individual failures */ }
        }

        _cache.Remove(AdUsersCacheKey);
        TempData["Success"] = $"Mass create complete: {created} users created as Requester."
            + (groupId.HasValue ? " Group assigned." : " No group assigned.");
        return RedirectToAction("AdUsers");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateUserFromAd(string adUsername, string fullName,
        string email, string department, string employeeId, string role, int? groupId)
    {
        var existing = await _db.GetUserByUsernameAsync(adUsername);
        if (existing != null)
        {
            TempData["Error"] = $"User '{adUsername}' is already registered.";
            return RedirectToAction("AdUsers");
        }

        var user = new User
        {
            EmployeeId   = string.IsNullOrEmpty(employeeId) ? adUsername : employeeId,
            AdUsername   = adUsername,
            FullName     = fullName,
            Email        = email,
            Department   = department,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            Role         = string.IsNullOrEmpty(role) ? UserRoles.Requester : role,
            IsActive     = true
        };
        var userId = await _db.CreateUserAsync(user);
        if ((role == UserRoles.Technician || role == UserRoles.FirstLevelSupport)
            && groupId.HasValue)
        {
            await _db.UpsertGroupMemberAsync(new TechnicianGroupMember
            {
                UserId = userId, GroupId = groupId.Value,
                CanDelete = false, CanAssign = false, CanPickupOthers = false
            });
        }
        _cache.Remove(AdUsersCacheKey);
        TempData["Success"] = $"User '{fullName}' ({adUsername}) created.";
        return RedirectToAction("Users");
    }

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return int.TryParse(claim?.Value, out var id) ? id : 0;
    }
}

public class GrantRoleViewModel
{
    public int    UserId          { get; set; }
    public string Role            { get; set; } = "";
    public int?   GroupId         { get; set; }
    public bool   CanDelete       { get; set; }
    public bool   CanAssign       { get; set; }
    public bool   CanPickupOthers { get; set; }
}

public class UserManagementViewModel
{
    public List<AdUser> AllUsers   { get; set; } = new();
    public string?      Search     { get; set; }
    public string?      DeptFilter { get; set; }
}

public class TechnicianManagementViewModel
{
    public List<User>                   Technicians { get; set; } = new();
    public List<TechnicianGroup>        Groups      { get; set; } = new();
    public List<TechnicianGroupMember>  Assignments { get; set; } = new();
}
