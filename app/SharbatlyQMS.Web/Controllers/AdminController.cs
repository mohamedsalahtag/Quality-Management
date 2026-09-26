using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

// The class used to carry a Manager+ gate that AND-combined with a per-action
// AdminOnly one, so several actions relied on inheriting it and an action with
// no attribute of its own quietly fell back to Manager. Every action here now
// declares its own permission, and the decision filter refuses anything that
// declares nothing -- so nothing depends on inheritance any more.
[Authorize]
public class AdminController : Controller
{
    private readonly IDbService _db;
    private readonly ISettingsService _settings;
    private readonly ISapODataClient _sapOData;
    private readonly ISapSyncService _sapSync;
    private readonly IEmailService _email;
    private readonly IAdService _ad;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminController> _adminLog;
    private readonly ICatalogCache _catalogCache;
    private readonly IMaraService _mara;
    private readonly IAuditService _audit;
    private readonly ICodeDescriptionDirectory _codes;
    private readonly IPermissionResolver _perms;
    private readonly IUserAdminService _userAdmin;
    private readonly IPermissionLogService _permLog;
    private readonly IQoFinishNotifier _notifier;

    public AdminController(IDbService db, ISettingsService settings,
        ISapODataClient sapOData, ISapSyncService sapSync, IEmailService email,
        IAdService ad, IServiceScopeFactory scopeFactory, IWebHostEnvironment env,
        ILogger<AdminController> adminLog, ICatalogCache catalogCache, IMaraService mara,
        IAuditService audit, ICodeDescriptionDirectory codes, IPermissionResolver perms,
        IUserAdminService userAdmin, IPermissionLogService permLog,
        IQoFinishNotifier notifier)
    {
        _db = db; _settings = settings; _sapOData = sapOData; _sapSync = sapSync;
        _email = email; _ad = ad; _scopeFactory = scopeFactory; _env = env; _adminLog = adminLog;
        _catalogCache = catalogCache; _mara = mara; _audit = audit; _codes = codes; _perms = perms;
        _userAdmin = userAdmin; _permLog = permLog; _notifier = notifier;
    }

    [HttpGet]
    [RequireScreen(Screens.AdminUsers, Seed.AdminOnly, "Open the Users screen")]
    public async Task<IActionResult> Users(string? search, string? role, string? status)
    {
        bool? active = status switch
        {
            "active"   => true,
            "disabled" => false,
            _          => null
        };

        var users = await _db.ListUsersAsync(search, role, active);
        var adCfg = await _settings.GetAdConfigAsync();
        ViewBag.Search       = search;
        ViewBag.Role         = role;
        ViewBag.Status       = status;
        ViewBag.AdConfigured = adCfg.IsConfigured;
        // Sourced from the role table rather than a hard-coded list, so a role
        // composed on the Security screen is assignable the moment it exists.
        ViewBag.Roles        = await _db.ListAssignableRolesAsync();
        // Per-user plant assignments drive the multi-select in the edit modal.
        ViewBag.AllPlants    = await _db.ListAllPlantCodesAsync();
        ViewBag.UserPlants   = await _db.GetPlantsForUsersAsync(users.Select(u => u.UserId));
        return View(users);
    }

    // Users are added by picking from Active Directory -- never typed
    // free-form. The picker UI in /Admin/Users posts here with the selected
    // username + chosen role. We resolve the AD profile from the cached
    // browse list (zero round-trip in the common case); fall back to a
    // direct LDAP lookup only if the cache has expired between modal-open
    // and Add.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersEdit, Seed.AdminOnly, "Add or edit a user")]
    public async Task<IActionResult> CreateUser(string username, string role, string? plantCode)
    {
        var actor = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        var res = await _userAdmin.AddFromDirectoryAsync(username, role, plantCode, GetCurrentUserId(), actor);
        if (!res.Ok)
            TempData["Error"] = res.Error;
        else
            TempData["Success"] = res.EffectivePlant != null
                ? $"User '{username}' added as {role} scoped to plant {res.EffectivePlant}."
                : $"User '{username}' added with role {role}.";
        return RedirectToAction(nameof(Users));
    }

    /// <summary>
    /// Lists AD users for the "Add user from AD" picker on /Admin/Users.
    /// Returns a JSON shape the modal renders directly, with an
    /// alreadyAdded flag for users already imported into the local Users
    /// table so the picker can grey them out.
    /// </summary>
    [HttpGet]
    [RequireScreen(Screens.AdminUsers)]
    public async Task<IActionResult> BrowseAdUsersAjax(string? q)
    {
        var adCfg = await _settings.GetAdConfigAsync();
        if (!adCfg.IsConfigured)
            return Json(new { ok = false, error = "Active Directory is not configured." });

        // No server-side cap -- the client filters in memory and the DOM
        // tops out at 1000 visible rows. Returning every AD user means the
        // search box can find any account, not just the first 200.
        var ad = await _ad.ListUsersAsync(adCfg, q, max: int.MaxValue);
        var have = (await _db.ListUsersAsync(null, null, null))
            .Select(u => u.Username)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = ad.Select(u => new
        {
            username     = u.Username,
            fullName     = u.FullName,
            email        = u.Email ?? "",
            department   = u.Department ?? "",
            alreadyAdded = have.Contains(u.Username)
        }).ToList();
        return Json(new { ok = true, users = rows });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersEdit, Seed.AdminOnly, "Add or edit a user")]
    public async Task<IActionResult> EditUser(int userId, string fullName, string email,
        string? department, string? employeeId, string role, string[]? plantCodes)
    {
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();

        // `role` is a role CODE now, validated against the role table -- the six
        // hard-coded names are gone. A name-to-code map used to sit here and
        // silently fall back to Viewer, which would have demoted anyone holding
        // a composed role on their next e-mail edit.
        var target = (await _db.ListAssignableRolesAsync())
            .FirstOrDefault(r => string.Equals(r.RoleCode, role, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            TempData["Error"] = "That role does not exist or is not active.";
            return RedirectToAction(nameof(Users));
        }

        // Somebody has to stay able to administer. Refuse the change if this is
        // the last active user whose role can reach the Security screen.
        if (!string.Equals(u.RoleCode, target.RoleCode, StringComparison.OrdinalIgnoreCase)
            && await _userAdmin.IsLastSecurityAdminAsync(u))
        {
            TempData["Error"] = $"'{u.Username}' is the only active user who can manage security. " +
                                "Give somebody else that access first.";
            return RedirectToAction(nameof(Users));
        }

        var beforeRole = u.RoleCode;
        var beforePlants = await _db.GetUserPlantsAsync(u.UserId);
        u.FullName   = fullName;
        u.Email      = email;
        u.Department = department;
        u.EmployeeId = employeeId;
        u.RoleCode   = target.RoleCode;
        u.Role       = target.LegacyName ?? target.RoleCode;
        u.RoleName   = target.DisplayName;
        await _db.UpdateUserAsync(u);

        // Plant access is a per-USER assignment now, independent of the role.
        // Administrators always see every plant, so we never store plants for
        // them (their login issues an unrestricted claim regardless). Everyone
        // else is limited to exactly the plants ticked here; none ticked means
        // "sees no plant data until assigned".
        var isAdmin = string.Equals(target.RoleCode, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase);
        var newPlants = isAdmin ? Array.Empty<string>() : (plantCodes ?? Array.Empty<string>());
        await _db.SetUserPlantsAsync(u.UserId, newPlants);

        await AuditAdminAsync(EntityTypes.User, u.UserId, ActionCodes.Updated,
            new { role = beforeRole, plants = beforePlants },
            new { role = u.RoleCode, plants = newPlants });

        // Readable log: the role move as one line, and each plant granted or
        // revoked as its own line. The audit row above collapses plants into a
        // single JSON array diff, which is the thing nobody could read.
        var perm = new List<PermissionLogEntry>();
        if (!string.Equals(beforeRole, u.RoleCode, StringComparison.OrdinalIgnoreCase))
        {
            var e = UserEntry(u, PermissionChangeTypes.RoleAssignment);
            e.ItemCode = u.RoleCode;
            e.ItemName = target.DisplayName;
            e.OldValue = beforeRole;
            e.NewValue = u.RoleCode;
            perm.Add(e);
        }
        foreach (var added in newPlants.Except(beforePlants, StringComparer.OrdinalIgnoreCase))
        {
            var e = UserEntry(u, PermissionChangeTypes.Plant);
            e.ItemCode = added;
            e.ItemName = _codes.PlantDisplay(added);
            e.OldValue = "No access";
            e.NewValue = "Access granted";
            perm.Add(e);
        }
        foreach (var removed in beforePlants.Except(newPlants, StringComparer.OrdinalIgnoreCase))
        {
            var e = UserEntry(u, PermissionChangeTypes.Plant);
            e.ItemCode = removed;
            e.ItemName = _codes.PlantDisplay(removed);
            e.OldValue = "Access granted";
            e.NewValue = "Access removed";
            perm.Add(e);
        }
        await PermLogAsync(perm.ToArray());
        // Authorisation resolves a user's role from the permission snapshot, so a
        // role change only takes effect once the snapshot is rebuilt.
        await _perms.RefreshAsync();
        TempData["Success"] = $"User '{u.Username}' updated.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersEdit, Seed.AdminOnly, "Add or edit a user")]
    public async Task<IActionResult> ResetPassword(int userId, string newPassword)
    {
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 10)
        {
            TempData["Error"] = "Password must be at least 10 characters.";
            return RedirectToAction(nameof(Users));
        }
        u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.UpdateUserAsync(u);
        // Record that a reset happened (never the password itself).
        await AuditAdminAsync(EntityTypes.User, u.UserId, ActionCodes.PasswordReset,
            null, new { u.Username });
        TempData["Success"] = $"Password reset for '{u.Username}'.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersEdit, Seed.AdminOnly, "Add or edit a user")]
    public async Task<IActionResult> ToggleActive(int userId)
    {
        var current = GetCurrentUserId();
        if (current == userId)
        {
            TempData["Error"] = "You cannot disable your own account.";
            return RedirectToAction(nameof(Users));
        }
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();
        if (u.IsActive && await _userAdmin.IsLastSecurityAdminAsync(u))
        {
            TempData["Error"] = $"'{u.Username}' is the only active user who can manage security. " +
                                "Give somebody else that access before disabling them.";
            return RedirectToAction(nameof(Users));
        }
        await _db.SetUserActiveAsync(userId, !u.IsActive, current);
        await AuditAdminAsync(EntityTypes.User, u.UserId, ActionCodes.Updated,
            new { isActive = u.IsActive }, new { isActive = !u.IsActive });
        // Disabling an account revokes every permission the person had, so it
        // belongs in the permission log as much as a role change does.
        var toggled = UserEntry(u, PermissionChangeTypes.Account);
        toggled.ItemCode = "is_active";
        toggled.ItemName = "Account active";
        toggled.OldValue = u.IsActive ? "Active" : "Disabled";
        toggled.NewValue = !u.IsActive ? "Active" : "Disabled";
        await PermLogAsync(toggled);
        // Disabling drops the user from the snapshot's user->role map; refresh so
        // the change is felt on the next request, not the next restart.
        await _perms.RefreshAsync();
        TempData["Success"] = $"User '{u.Username}' is now {(!u.IsActive ? "active" : "disabled")}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersDelete, Seed.AdminOnly, "Remove a user")]
    public async Task<IActionResult> DeleteUser(int userId)
    {
        var current = GetCurrentUserId();
        if (current == userId)
        {
            TempData["Error"] = "You cannot delete your own account.";
            return RedirectToAction(nameof(Users));
        }
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();
        if (await _userAdmin.IsLastSecurityAdminAsync(u))
        {
            TempData["Error"] = $"'{u.Username}' is the only active user who can manage security. " +
                                "Give somebody else that access before removing them.";
            return RedirectToAction(nameof(Users));
        }
        var ok = await _db.TryDeleteUserAsync(userId);
        if (ok)
        {
            await AuditAdminAsync(EntityTypes.User, u.UserId, ActionCodes.Deleted,
                new { u.Username, u.Role, u.PlantCode }, null);
            var gone = UserEntry(u, PermissionChangeTypes.Account);
            gone.ItemCode = "deleted";
            gone.ItemName = "Account deleted";
            gone.OldValue = $"Role {u.RoleCode}";
            gone.NewValue = "Account deleted";
            await PermLogAsync(gone);
            await _perms.RefreshAsync();
        }
        TempData[ok ? "Success" : "Error"] = ok
            ? $"User '{u.Username}' deleted."
            : $"User '{u.Username}' is referenced elsewhere - disable instead.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.UsersDelete, Seed.AdminOnly, "Remove a user")]
    public async Task<IActionResult> BulkDeleteUsers(int[] userIds)
    {
        if (userIds == null || userIds.Length == 0)
        {
            TempData["Error"] = "No users selected.";
            return RedirectToAction(nameof(Users));
        }
        var current = GetCurrentUserId();
        int deleted = 0, blockedByFk = 0, skippedSelf = 0;
        foreach (var id in userIds.Distinct())
        {
            if (id == current) { skippedSelf++; continue; }
            var u = await _db.GetUserByIdAsync(id);
            if (u == null) continue;
            if (await _db.TryDeleteUserAsync(id))
            {
                deleted++;
                await AuditAdminAsync(EntityTypes.User, u.UserId, ActionCodes.Deleted,
                    new { u.Username, u.Role, u.PlantCode }, null);
                var bulkGone = UserEntry(u, PermissionChangeTypes.Account);
                bulkGone.ItemCode = "deleted";
                bulkGone.ItemName = "Account deleted";
                bulkGone.OldValue = $"Role {u.RoleCode}";
                bulkGone.NewValue = "Account deleted";
                await PermLogAsync(bulkGone);
            }
            else blockedByFk++;
        }
        if (deleted > 0) await _perms.RefreshAsync();
        var parts = new List<string> { $"{deleted} deleted" };
        if (blockedByFk > 0) parts.Add($"{blockedByFk} kept (referenced elsewhere)");
        if (skippedSelf > 0) parts.Add($"{skippedSelf} skipped (your own account)");
        TempData[deleted > 0 ? "Success" : "Error"] = string.Join(", ", parts) + ".";
        return RedirectToAction(nameof(Users));
    }

    // ---- Site Configuration ------------------------------------------------

    // Danger Zone: wipe every operational record so the live system starts
    // from a clean slate (used when graduating from testing to production).
    // Catalogs, config, users, and SAP caches are preserved. Image files on
    // disk are also removed so wwwroot/uploads/ matches the empty DB state.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.PurgeAll, Seed.AdminOnly, "Purge every transaction (danger zone)")]
    public async Task<IActionResult> PurgeAll(string? confirmation)
    {
        if (confirmation != "PURGE ALL")
        {
            TempData["Error"] = "Purge cancelled: you must type PURGE ALL exactly (case-sensitive) to confirm.";
            return RedirectToAction(nameof(Settings), new { activeTab = "danger" });
        }

        var who = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        _adminLog.LogWarning("Danger-Zone PurgeAll initiated by {User}", who);

        // Delete order matters: every FK must be cleared before its parent.
        // Per-table row counts are returned to the admin so they can see the
        // wipe actually did something.
        var deleteOrder = new (string label, string sql)[]
        {
            ("sample_defect",          "DELETE FROM qms_sample_defect"),
            ("sample_reading",         "DELETE FROM qms_sample_reading"),
            ("sample_observation",     "DELETE FROM qms_sample_observation"),
            ("sample",                 "DELETE FROM qms_sample"),
            ("quality_order_material", "DELETE FROM qms_quality_order_material"),
            // Claim tables reference qms_quality_order (FK_qms_claim_qo, NO ACTION),
            // so they must be cleared before the quality_order delete or the purge
            // throws an FK violation and rolls back whenever any claim exists.
            ("claim_read_marker",      "DELETE FROM qms_claim_read_marker"),
            ("claim_note",             "DELETE FROM qms_claim_note"),
            ("claim",                  "DELETE FROM qms_claim"),
            ("quality_order",          "DELETE FROM qms_quality_order"),
            ("arrival_checklist",      "DELETE FROM qms_arrival_checklist"),
            ("arrival_item",           "DELETE FROM qms_arrival_item"),
            ("arrival_sap_snapshot",   "DELETE FROM qms_arrival_sap_snapshot"),
            ("shipment_snapshot",      "DELETE FROM qms_shipment_snapshot"),
            ("arrival",                "DELETE FROM qms_arrival"),
            ("image_link",             "DELETE FROM qms_image_link"),
            ("image_asset",            "DELETE FROM qms_image_asset"),
            ("status_history",         "DELETE FROM qms_status_history"),
            ("report_log",             "DELETE FROM qms_report_log")
            // NOTE: qms_audit_log is deliberately NOT purged. The audit log is
            // append-only by design (spec FR-006 — no delete surface, including
            // SiteAdmin). Wiping operational data does not entitle us to erase the
            // forensic record of who did what. The purge itself is recorded as an
            // audit entry after the transaction commits (see below).
        };

        // Reseed IDENTITY counters back to 1 so new records start from #1.
        // qms_audit_log is intentionally excluded: its rows survive the purge, so
        // reseeding its identity would collide with existing audit_id values.
        var reseedTables = new[]
        {
            "qms_sample_defect","qms_sample_reading","qms_sample_observation","qms_sample",
            "qms_quality_order_material","qms_quality_order",
            "qms_claim_read_marker","qms_claim_note","qms_claim",
            "qms_arrival_checklist","qms_arrival_item","qms_arrival_sap_snapshot",
            "qms_shipment_snapshot","qms_arrival",
            "qms_image_link","qms_image_asset",
            "qms_status_history","qms_report_log"
        };

        // Reseed the auto-numbered sequences (arrival_no, shipment_no, quality_order_no).
        var sequences = new[] { "seq_qms_arrival_no", "seq_qms_shipment_no", "seq_qms_quality_order_no" };

        var totalDeleted = 0;
        var perTable = new List<string>();
        var connStr = HttpContext.RequestServices.GetRequiredService<IConfiguration>()
            .GetConnectionString("Default");

        await using (var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                foreach (var (label, sql) in deleteOrder)
                {
                    var n = await conn.ExecuteAsync(sql, transaction: tx);
                    totalDeleted += n;
                    if (n > 0) perTable.Add($"{label}={n}");
                }

                foreach (var tbl in reseedTables)
                    await conn.ExecuteAsync($"DBCC CHECKIDENT('{tbl}', RESEED, 0) WITH NO_INFOMSGS", transaction: tx);

                foreach (var seq in sequences)
                    await conn.ExecuteAsync($"ALTER SEQUENCE {seq} RESTART WITH 1", transaction: tx);

                // The arrivals we just deleted left qms_sap_container_cache rows
                // flagged has_arrival=1 with a now-dangling arrival_id. Nothing ever
                // resets these, so without this the affected containers would stay
                // permanently hidden from the Pending Containers queue after a purge.
                await conn.ExecuteAsync(
                    "UPDATE qms_sap_container_cache SET has_arrival = 0, arrival_id = NULL " +
                    "WHERE has_arrival = 1 OR arrival_id IS NOT NULL",
                    transaction: tx);

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // Delete image files on disk so wwwroot/uploads/ matches the empty DB.
        // Best-effort: any locked file is skipped; the DB is the source of truth.
        var uploadsRoot = UploadStorage.Root(_env,
            HttpContext.RequestServices.GetRequiredService<IConfiguration>());
        var filesDeleted = 0;
        if (Directory.Exists(uploadsRoot))
        {
            foreach (var f in Directory.EnumerateFiles(uploadsRoot, "*", SearchOption.AllDirectories))
            {
                try { System.IO.File.Delete(f); filesDeleted++; } catch { /* skip locked */ }
            }
            foreach (var d in Directory.EnumerateDirectories(uploadsRoot, "*", SearchOption.AllDirectories)
                                       .OrderByDescending(p => p.Length))
            {
                try { Directory.Delete(d); } catch { /* skip non-empty */ }
            }
        }

        _adminLog.LogWarning("Danger-Zone PurgeAll completed by {User}: {Rows} rows, {Files} files. Detail: {Detail}",
            who, totalDeleted, filesDeleted, string.Join(", ", perTable));

        // Record the purge in the (preserved) audit log. Best-effort: a failure
        // here must not mask a successful purge, so it's logged and swallowed.
        try
        {
            await _audit.WriteAsync(EntityTypes.System, 0, ActionCodes.Purged,
                oldValues: null,
                newValues: new { rows = totalDeleted, files = filesDeleted, tables = perTable },
                actor: who);
        }
        catch (Exception ex)
        {
            _adminLog.LogError(ex, "PurgeAll succeeded but writing the purge audit entry failed.");
        }

        _catalogCache.Invalidate();
        TempData["Success"] = perTable.Count == 0
            ? $"System was already clean. {filesDeleted} image file(s) removed. Sequences reset."
            : $"Purged {totalDeleted} row(s) ({string.Join(", ", perTable)}) and {filesDeleted} image file(s). Sequences reset to #1.";
        return RedirectToAction(nameof(Settings), new { activeTab = "danger" });
    }

    [HttpGet]
    [RequireScreen(Screens.AdminSettings, Seed.AdminOnly, "Open Site Configuration")]
    public async Task<IActionResult> Settings(string? activeTab = null)
    {
        var vm = await BuildSettingsVmAsync();
        ViewBag.ActiveTab = activeTab ?? "sap";
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveSapSettings(SapEndpointConfig sap)
    {
        await _settings.SaveSapConfigAsync(sap, GetCurrentUserId());
        // Audit the change (section only — never the credentials themselves).
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null, new { section = "SAP OData" });
        TempData["Success"] = "SAP OData settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveSmtpSettings(SmtpConfig smtp)
    {
        await _settings.SaveSmtpConfigAsync(smtp, GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null, new { section = "SMTP" });
        TempData["Success"] = "SMTP settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "smtp" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveThumbnailSettings(ThumbnailConfig thumbnails)
    {
        // Parameter name must equal the property name on SettingsVm (Thumbnails)
        // because Razor renders the form fields as "Thumbnails.ScreenWidth" etc.
        // and the default model binder matches the parameter name as the prefix.
        // A different parameter name (e.g. "thumb") silently binds to defaults.
        await _settings.SaveThumbnailConfigAsync(thumbnails, GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null, new { section = "Thumbnails" });
        TempData["Success"] = "Thumbnail settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "thumbnails" });
    }

    /// <summary>
    /// Time Bar thresholds. The parameter MUST be named <c>timeBar</c>: the view
    /// renders the inputs as TimeBar.GoodDays and the default binder matches the
    /// PARAMETER name as the prefix, so a different name silently binds every
    /// field to its default.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveTimeBarSettings(TimeBarConfig timeBar)
    {
        await _settings.SaveTimeBarConfigAsync(timeBar ?? new TimeBarConfig(), GetCurrentUserId());
        TempData["Success"] = "Time Bar thresholds saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "alerts" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveAlertSettings(AlertConfig alerts)
    {
        await _settings.SaveAlertConfigAsync(alerts, GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null, new { section = "Alert thresholds" });
        TempData["Success"] = "Alert thresholds saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "alerts" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveReportSettings(ReportConfig report)
    {
        await _settings.SaveReportConfigAsync(report ?? new ReportConfig(), GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null,
            new { section = "Report options", rejectedContainerHeader = report?.RejectedContainerHeader });
        TempData["Success"] = "Report options saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "report" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveStorageSettings(StorageConfig storage)
    {
        var cfg  = storage ?? new StorageConfig();
        var path = (cfg.UploadsRoot ?? "").Trim();

        // A blank value resets to the appsettings/default location. A non-blank
        // value must be an absolute, creatable, WRITABLE folder — otherwise a bad
        // path would silently break photo uploads + serving after the restart.
        if (!string.IsNullOrEmpty(path))
        {
            if (!Path.IsPathFullyQualified(path))
            {
                TempData["Error"] = "Uploads folder must be an absolute path (e.g. D:\\QMS\\uploads).";
                return RedirectToAction(nameof(Settings), new { activeTab = "storage" });
            }
            try
            {
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".qms_write_test");
                await System.IO.File.WriteAllTextAsync(probe, "ok");
                System.IO.File.Delete(probe);
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Cannot use that folder (create/write test failed): {ex.Message}";
                return RedirectToAction(nameof(Settings), new { activeTab = "storage" });
            }
        }

        cfg.UploadsRoot = path;
        await _settings.SaveStorageConfigAsync(cfg, GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null,
            new { section = "Storage", uploadsRoot = path });
        TempData["Success"] = string.IsNullOrEmpty(path)
            ? "Uploads folder reset to the default. Restart the service to apply."
            : "Uploads folder saved. Move the existing photos into the new folder and RESTART the service to apply — until the restart the app keeps writing to and serving from the current folder.";
        return RedirectToAction(nameof(Settings), new { activeTab = "storage" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveContainerPollSettings(ContainerPollConfig containerPoll)
    {
        var cfg = containerPoll ?? new ContainerPollConfig();
        // Clamp into the allowed range so a fat-fingered value can't take
        // SAP down by pulling every second or stall the queue forever.
        if (cfg.PollingMinutes < 5)    cfg.PollingMinutes = 5;
        if (cfg.PollingMinutes > 1440) cfg.PollingMinutes = 1440;
        await _settings.SaveContainerPollConfigAsync(cfg, GetCurrentUserId());
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null,
            new { section = "Container polling", cfg.PollingMinutes,
                  archiveArrivalsBefore = cfg.ArchiveArrivalsBefore?.ToString("yyyy-MM-dd") });
        TempData["Success"] = "Pending Containers pulling saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SapSync, Seed.AdminOnly, "Run a SAP sync or pull")]
    public async Task<IActionResult> PullContainersNow(ContainerPollConfig containerPoll,
        [FromServices] IConfiguration config)
    {
        // Save first so the start date the admin just typed is used by the
        // background pull (and so the next scheduled tick respects the
        // latest values).
        var cfg = containerPoll ?? new ContainerPollConfig();
        if (cfg.PollingMinutes < 5)    cfg.PollingMinutes = 5;
        if (cfg.PollingMinutes > 1440) cfg.PollingMinutes = 1440;
        await _settings.SaveContainerPollConfigAsync(cfg, GetCurrentUserId());

        if (cfg.StartDate is null)
        {
            TempData["Error"] = "Set a start date before pulling.";
            return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
        }

        // Don't queue a second pull on top of a running one. The in-flight
        // sync_log row (completed_at IS NULL) is the source of truth -- it's
        // written by RefreshFromSapAsync at start and updated at end. Same
        // guard the auto-scheduler uses.
        var cs = config.GetConnectionString("Default")!;
        using (var c = new Microsoft.Data.SqlClient.SqlConnection(cs))
        {
            var inFlight = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
                SELECT COUNT(*) FROM qms_sap_sync_log
                WHERE  endpoint_key = @ep AND completed_at IS NULL",
                new { ep = ContainerCacheService.SyncLogEndpointKey });
            if (inFlight > 0)
            {
                TempData["Error"] = "A pull is already running. Refresh the page in a moment to see its result.";
                return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
            }
        }

        var user      = User.FindFirstValue(ClaimTypes.Name) ?? "system";
        var startDate = cfg.StartDate.Value;
        var archiveBefore = cfg.ArchiveArrivalsBefore;

        // Fire-and-forget. The HTTP request returns immediately so the
        // browser doesn't sit on a multi-minute load -- a bulk pull can fetch
        // tens of thousands of rows across many OData pages. The background
        // task writes start + completion timestamps + row counts to
        // qms_sap_sync_log; the Settings page reads them back in its "Last
        // pull" status line, so refreshing the page shows progress.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var cache = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
                await cache.RefreshFromSapAsync(startDate, user, "Manual", archiveBefore, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // RefreshFromSapAsync already wrote the failure to sync_log;
                // log here too in case the throw came from somewhere upstream.
                _adminLog.LogError(ex, "Background container pull threw");
            }
        });

        TempData["Success"] = "Pull started in the background. Refresh this page in a moment to see the result.";
        return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
    }

    // ---- Per-endpoint sync (Material Master, Vendor Master) ----------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SapSync, Seed.AdminOnly, "Run a SAP sync or pull")]
    public IActionResult SyncSapEndpointAjax(string endpointKey)
    {
        if (!SyncableEndpoints.IsValid(endpointKey))
            return Json(new { ok = false, message = $"Endpoint '{endpointKey}' is not syncable." });

        var user = User.FindFirstValue(ClaimTypes.Name) ?? "system";

        // Fire-and-forget so the request returns immediately. The sync may take
        // minutes for large catalogues; the UI polls SyncStatusAjax to track
        // progress. A new DI scope is required because the request-scoped one
        // is disposed when this method returns.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var sync = scope.ServiceProvider.GetRequiredService<ISapSyncService>();
                await sync.SyncAsync(endpointKey, user, "Manual");
            }
            catch (Exception ex) { _adminLog.LogError(ex, "Background sync of {Endpoint} threw", endpointKey); }
        });

        return Json(new { ok = true, started = true,
            message = $"Sync started for {endpointKey}. Watch the status indicator below for progress." });
    }

    [HttpGet]
    [RequireScreen(Screens.AdminSettings)]
    public async Task<IActionResult> SyncStatusAjax(string endpointKey)
    {
        if (!SyncableEndpoints.IsValid(endpointKey))
            return Json(new { ok = false, message = $"Unknown endpoint '{endpointKey}'." });

        // Latest sync log row gives the most accurate "running / done" picture.
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var row = await c.QuerySingleOrDefaultAsync(@"
            SELECT TOP 1 sync_log_id    AS LogId,
                         started_at     AS StartedAt,
                         completed_at   AS CompletedAt,
                         success        AS Succeeded,
                         rows_synced    AS RowsSynced,
                         message        AS Msg,
                         triggered_by   AS TriggeredBy
            FROM   qms_sap_sync_log
            WHERE  endpoint_key = @endpointKey
            ORDER  BY sync_log_id DESC", new { endpointKey });

        var cacheCount = endpointKey == SyncableEndpoints.MaterialMaster
            ? await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM qms_sap_material_cache")
            : await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM qms_sap_vendor_cache");

        if (row == null)
            return Json(new { running = false, cacheCount, message = "No sync runs yet." });

        var running = row.CompletedAt == null;
        return Json(new
        {
            running,
            success     = (bool?)row.Succeeded,
            rows        = (int?)row.RowsSynced,
            startedAt   = row.StartedAt,
            completedAt = row.CompletedAt,
            message     = (string?)row.Msg,
            cacheCount,
            triggeredBy = (string?)row.TriggeredBy
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveEndpointSync(string endpointKey, bool enabled, string? hoursCsv)
    {
        if (!SyncableEndpoints.IsValid(endpointKey))
        {
            TempData["Error"] = $"Unknown endpoint '{endpointKey}'.";
            return RedirectToAction(nameof(Settings), new { activeTab = "syncstatus" });
        }
        var hours = (hoursCsv ?? "")
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n is >= 0 and <= 23);
        await _settings.SaveEndpointSyncAsync(endpointKey, enabled, hours, GetCurrentUserId());
        TempData["Success"] = $"{endpointKey} schedule saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "syncstatus" });
    }

    // Inline AJAX endpoints used by the Settings → SAP tab. Each URL row has
    // Test and Preview buttons that read the current input values and POST
    // them here without saving the form first.
    //
    // Credential resolution rules:
    //   * If both user and password are typed in the form, use them as-is.
    //   * If user is typed but password is blank, the admin is testing with
    //     the previously-saved password (rendered fields don't echo passwords
    //     back for security). Look up the saved per-URL password.
    //   * If both are blank, fall back to the saved per-URL pair, then to
    //     the global Sap.User / Sap.Password.
    // The endpointKey parameter tells us which row this is so we can look up
    // the right saved password.
    [HttpPost, ValidateAntiForgeryToken]
    [RequireScreen(Screens.AdminSettings)]
    public async Task<IActionResult> TestSapEndpointAjax(string url, string? user = null, string? password = null, string? endpointKey = null)
    {
        var (u, p) = await ResolveCredsForLiveTestAsync(user, password, endpointKey);
        var (ok, msg) = await _sapOData.TestEndpointAsync(url, NullIfBlank(u), NullIfBlank(p));
        return Json(new { ok, message = msg });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireScreen(Screens.AdminSettings)]
    public async Task<IActionResult> PreviewSapEndpointAjax(string url, string? user = null, string? password = null, string? endpointKey = null)
    {
        var (u, p) = await ResolveCredsForLiveTestAsync(user, password, endpointKey);
        var (ok, body) = await _sapOData.PreviewEndpointAsync(url, NullIfBlank(u), NullIfBlank(p));
        return Json(new { ok, body });
    }

    private async Task<(string user, string password)> ResolveCredsForLiveTestAsync(
        string? incomingUser, string? incomingPassword, string? endpointKey)
    {
        // Both supplied -- use exactly what the admin typed.
        if (!string.IsNullOrWhiteSpace(incomingUser) && !string.IsNullOrWhiteSpace(incomingPassword))
            return (incomingUser, incomingPassword);

        // Otherwise consult the saved configuration to fill in the gap.
        var sap = await _settings.GetSapConfigAsync();
        var (savedUser, savedPwd) = string.IsNullOrWhiteSpace(endpointKey)
            ? (sap.User, sap.Password)
            : sap.ResolveCredentials(endpointKey);

        var finalUser = !string.IsNullOrWhiteSpace(incomingUser) ? incomingUser : savedUser;
        var finalPwd  = !string.IsNullOrWhiteSpace(incomingPassword) ? incomingPassword : savedPwd;
        return (finalUser, finalPwd);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SendTestEmail, Seed.AdminOnly, "Send a test e-mail")]
    public async Task<IActionResult> SendTestEmail(string toEmail)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            TempData["Error"] = "Recipient email is required.";
            return RedirectToAction(nameof(Settings), new { activeTab = "smtp" });
        }
        var (ok, msg) = await _email.SendTestEmailAsync(toEmail);
        TempData[ok ? "Success" : "Error"] = msg;
        return RedirectToAction(nameof(Settings), new { activeTab = "smtp" });
    }

    // ---- Catalog admin (defects + reading types) -----------------------------

    [HttpGet]
    [RequireScreen(Screens.DefectCatalog, Seed.ManagerOrAdmin, "Open the Defect Catalog")]
    public async Task<IActionResult> DefectCatalog(string? materialGroup = null)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // Canonical list of material groups comes from the SAP MARA cache
        // so admins can only assign defects to groups that actually exist
        // in SAP. If MARA is empty (sync not yet run on this DB) we fall
        // back to whatever groups are already present in the defect catalog
        // / QO material rows so the page is still usable.
        var maraGroups = await _mara.ListMaterialGroupsAsync();
        IReadOnlyList<MaraGroup> groups = maraGroups;
        if (groups.Count == 0)
        {
            var fallback = (await c.QueryAsync<string>(@"
                SELECT DISTINCT material_group FROM qms_defect_catalog WHERE material_group IS NOT NULL
                UNION
                SELECT DISTINCT material_group FROM qms_quality_order_material WHERE material_group IS NOT NULL
                ORDER BY 1")).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            groups = fallback.Select(g => new MaraGroup { Code = g!, Name = null }).ToList();
        }

        // IsInUse = at least one sample_defect references this catalog row.
        // Drives the Delete button: in-use rows show a disabled Delete with
        // a tooltip; never-used rows can be deleted (along with their
        // material_group_defect binding).
        var rows = await c.QueryAsync<DefectCatalogEntry>(@"
            SELECT d.defect_id DefectId, d.defect_code DefectCode, d.defect_name DefectName,
                   d.defect_category DefectCategory,
                   d.is_active IsActive, d.sort_order SortOrder,
                   d.material_group MaterialGroup, d.value_type ValueType,
                   d.tolerance Tolerance,
                   CAST(CASE WHEN u.defect_id IS NULL THEN 0 ELSE 1 END AS BIT) IsInUse
            FROM   qms_defect_catalog d
            -- The used-defect set is built ONCE and joined, rather than an
            -- EXISTS evaluated per row. qms_sample_defect holds 351,834 rows
            -- and its only usable index leads on sample_id, so the correlated
            -- form could not seek: 708 catalog rows meant 708 scans, and the
            -- page took 6.4 seconds to open. One pass takes 71 ms.
            LEFT   JOIN (SELECT defect_id FROM qms_sample_defect GROUP BY defect_id) u
                   ON u.defect_id = d.defect_id
            WHERE  (@materialGroup IS NULL OR d.material_group = @materialGroup)
            ORDER  BY d.material_group, d.sort_order, d.defect_name",
            new { materialGroup });

        ViewBag.Groups        = groups;
        ViewBag.FilterGroup   = materialGroup;
        ViewBag.MaraEmpty     = maraGroups.Count == 0;
        ViewBag.Categories    = await _catalogCache.GetActiveCategoriesAsync();
        return View(rows.ToList());
    }

    // ===================================================================
    // Defect catalog round trip: export to Excel, maintain the tolerances
    // there, upload the same file back.
    //
    // Only the TOLERANCE is written back. The other columns are exported so
    // the sheet is readable and so a row can be identified by eye, but an
    // import that also rewrote codes, names, categories or the active flag
    // would turn a spreadsheet edit into a schema-shaped weapon: one careless
    // sort or fill-down, and 700 defects change identity with no undo. If the
    // other columns should become editable this way, that is a deliberate
    // decision to take separately.
    // ===================================================================

    /// <summary>Column order of the workbook. The import reads by HEADER NAME,
    /// not by position, so a user who hides or moves a column still gets a
    /// file that uploads.</summary>
    private static readonly string[] CatalogSheetHeaders =
    {
        "Defect ID", "Material group", "Code", "Name", "Category",
        "Value type", "Sort order", "Active", "Tolerance"
    };

    private const string CatalogSheetName = "Defect catalog";

    [RequireScreen(Screens.DefectCatalog, Seed.ManagerOrAdmin, "Open the Defect Catalog")]
    public async Task<IActionResult> DefectCatalogExcel(string? materialGroup = null)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var rows = (await c.QueryAsync<DefectCatalogEntry>(@"
            SELECT d.defect_id DefectId, d.defect_code DefectCode, d.defect_name DefectName,
                   d.defect_category DefectCategory,
                   d.is_active IsActive, d.sort_order SortOrder,
                   d.material_group MaterialGroup, d.value_type ValueType,
                   d.tolerance Tolerance
            FROM   qms_defect_catalog d
            WHERE  (@materialGroup IS NULL OR d.material_group = @materialGroup)
            ORDER  BY d.material_group, d.sort_order, d.defect_name",
            new { materialGroup })).ToList();

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add(CatalogSheetName);

        for (var i = 0; i < CatalogSheetHeaders.Length; i++)
        {
            ws.Cell(1, i + 1).Value = CatalogSheetHeaders[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var d in rows)
        {
            ws.Cell(r, 1).Value = d.DefectId;
            ws.Cell(r, 2).Value = d.MaterialGroup;
            ws.Cell(r, 3).Value = d.DefectCode;
            ws.Cell(r, 4).Value = d.DefectName;
            ws.Cell(r, 5).Value = d.DefectCategory;
            ws.Cell(r, 6).Value = d.ValueType;
            ws.Cell(r, 7).Value = d.SortOrder;
            ws.Cell(r, 8).Value = d.IsActive ? "Yes" : "No";
            // Left EMPTY when unset, never 0: blank means no tolerance agreed,
            // and the import reads it back the same way. Writing a dash here
            // would make the column text and lose that distinction.
            if (d.Tolerance.HasValue) ws.Cell(r, 9).Value = d.Tolerance.Value;
            r++;
        }

        ws.Column(9).Style.NumberFormat.Format = DefectTolerance.Format;

        // Everything except the tolerance is reference data on the way back in,
        // so lock it: a protected sheet makes the one editable column obvious
        // and stops a stray edit that the import would silently ignore.
        ws.Range(1, 1, Math.Max(1, r - 1), CatalogSheetHeaders.Length).Style.Protection.SetLocked(true);
        if (r > 2) ws.Range(2, 9, r - 1, 9).Style.Protection.SetLocked(false);
        ws.Protect();

        ws.Range(1, 1, 1, CatalogSheetHeaders.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, CatalogSheetHeaders.Length).AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var name = string.IsNullOrWhiteSpace(materialGroup) ? "all-groups" : materialGroup;
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"defect-catalog-{name}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>
    /// Reads tolerances back out of the exported workbook.
    ///
    /// Everything is validated before ANYTHING is written: a file with one bad
    /// cell applies nothing rather than half of itself, because a partial
    /// import across hundreds of defects cannot be reasoned about afterwards.
    /// Rows whose tolerance is unchanged are left alone, so re-uploading the
    /// same file twice is a no-op rather than 700 pointless writes.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    [RequirePermission(Perm.Parameters.DefectCatalogEdit, Seed.ManagerOrAdmin, "Edit the Defect Catalog")]
    public async Task<IActionResult> DefectCatalogImport(IFormFile? file, string? materialGroup = null)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "No file was chosen.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }
        if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Upload the .xlsx file that Export produced.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }

        var parsed  = new List<(int DefectId, decimal? Tolerance)>();
        var errors  = new List<string>();

        try
        {
            using var stream = file.OpenReadStream();
            using var wb = new ClosedXML.Excel.XLWorkbook(stream);
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name == CatalogSheetName)
                     ?? wb.Worksheets.First();

            // By header name rather than position: a hidden or reordered column
            // is a normal thing to do to a spreadsheet and should not corrupt
            // the import.
            var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headerRow = ws.FirstRowUsed();
            if (headerRow == null)
            {
                TempData["Error"] = "That sheet is empty.";
                return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
            }
            foreach (var cell in headerRow.CellsUsed())
                header[cell.GetString().Trim()] = cell.Address.ColumnNumber;

            foreach (var needed in new[] { "Defect ID", "Tolerance" })
                if (!header.ContainsKey(needed))
                {
                    TempData["Error"] = $"The sheet has no '{needed}' column. Upload the file that Export produced.";
                    return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
                }

            var idCol  = header["Defect ID"];
            var tolCol = header["Tolerance"];

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                var excelRow = row.RowNumber();
                var idCell   = row.Cell(idCol);
                if (idCell.IsEmpty()) continue;                 // spacer row

                if (!idCell.TryGetValue<int>(out var defectId) || defectId <= 0)
                {
                    errors.Add($"Row {excelRow}: '{idCell.GetString()}' is not a defect id.");
                    continue;
                }

                var tolCell = row.Cell(tolCol);
                if (tolCell.IsEmpty() || string.IsNullOrWhiteSpace(tolCell.GetString()))
                {
                    // Cleared on purpose: back to "no tolerance agreed".
                    parsed.Add((defectId, null));
                    continue;
                }

                if (!tolCell.TryGetValue<decimal>(out var tol))
                {
                    errors.Add($"Row {excelRow}: '{tolCell.GetString()}' is not a number.");
                    continue;
                }
                if (tol < 0)
                {
                    errors.Add($"Row {excelRow}: tolerance cannot be negative.");
                    continue;
                }
                if (tol > 9999999)
                {
                    errors.Add($"Row {excelRow}: {tol} is too large for a tolerance.");
                    continue;
                }
                parsed.Add((defectId, Math.Round(tol, DefectTolerance.Decimals, MidpointRounding.AwayFromZero)));
            }
        }
        catch (Exception ex)
        {
            _adminLog.LogWarning(ex, "Defect catalog import could not read {File}", file.FileName);
            TempData["Error"] = "That file could not be read as an Excel workbook.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }

        if (errors.Count > 0)
        {
            // Nothing is written. Naming the first few rows is enough to find
            // the problem; the full list would be a wall of text in a toast.
            TempData["Error"] = $"Nothing was imported - {errors.Count} row(s) could not be read. " +
                                string.Join(" ", errors.Take(5)) +
                                (errors.Count > 5 ? $" (+{errors.Count - 5} more)" : "");
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }
        if (parsed.Count == 0)
        {
            TempData["Error"] = "That sheet had no rows to import.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        await c.OpenAsync();

        // Only rows that EXIST and whose value actually differs. An id that is
        // not in the catalog is reported rather than silently dropped -- it
        // usually means the wrong file.
        var known = (await c.QueryAsync<(int DefectId, decimal? Tolerance)>(
            "SELECT defect_id, tolerance FROM qms_defect_catalog"))
            .ToDictionary(x => x.DefectId, x => x.Tolerance);

        var unknown = parsed.Where(p => !known.ContainsKey(p.DefectId)).Select(p => p.DefectId).ToList();
        var changed = parsed.Where(p => known.TryGetValue(p.DefectId, out var cur) && cur != p.Tolerance).ToList();

        using var tx = c.BeginTransaction();
        foreach (var row in changed)
            await c.ExecuteAsync(
                "UPDATE qms_defect_catalog SET tolerance = @Tolerance WHERE defect_id = @DefectId",
                new { row.DefectId, row.Tolerance }, tx);
        tx.Commit();

        await AuditAdminAsync(EntityTypes.DefectCatalog, 0, ActionCodes.Updated, null,
            new
            {
                action = "Tolerance import",
                file = file.FileName,
                rowsInFile = parsed.Count,
                updated = changed.Count,
                unchanged = parsed.Count - changed.Count - unknown.Count,
                unknownIds = unknown.Count
            });
        _catalogCache.Invalidate();

        var message = $"{changed.Count} tolerance(s) updated from {parsed.Count} row(s).";
        if (unknown.Count > 0)
            message += $" {unknown.Count} row(s) referenced defects that are not in the catalog and were skipped.";
        TempData[changed.Count == 0 && unknown.Count == 0 ? "Error" : "Success"] =
            changed.Count == 0 && unknown.Count == 0
                ? "Nothing changed - every tolerance in the file already matched the catalog."
                : message;

        return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.DefectCatalogEdit, Seed.ManagerOrAdmin, "Edit the Defect Catalog")]
    public async Task<IActionResult> SaveDefect(int defectId, string materialGroup, string defectCode,
        string defectName, string defectCategory, string valueType,
        bool isActive, int sortOrder, decimal? tolerance = null)
    {
        // Two decimal places, decided here rather than left to the column: the
        // database would round it silently, and an administrator who typed
        // 2.555 should see what was actually kept rather than discover it later
        // on a report. Blank stays blank -- NULL means no tolerance agreed,
        // which is a different statement from zero.
        if (tolerance.HasValue)
        {
            if (tolerance < 0)
            {
                TempData["Error"] = "Tolerance cannot be negative.";
                return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
            }
            tolerance = Math.Round(tolerance.Value, DefectTolerance.Decimals, MidpointRounding.AwayFromZero);
        }

        if (string.IsNullOrWhiteSpace(materialGroup))
        {
            TempData["Error"] = "Material group is required.";
            return RedirectToAction(nameof(DefectCatalog));
        }
        if (valueType != "Number" && valueType != "Decimal") valueType = "Number";
        // Category must be one of the active, admin-defined categories
        // (the FK enforces it too, but this is a friendlier message).
        var activeCats = await _catalogCache.GetActiveCategoriesAsync();
        if (!activeCats.Any(x => string.Equals(x.CategoryName, defectCategory, StringComparison.OrdinalIgnoreCase)))
        {
            TempData["Error"] = $"Unknown defect category '{defectCategory}'. Define it first under Defect Categories.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        if (defectId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_defect_catalog
                    (material_group, defect_code, defect_name, defect_category, value_type,
                     is_active, sort_order, tolerance)
                VALUES
                    (@materialGroup, @defectCode, @defectName, @defectCategory, @valueType,
                     @isActive, @sortOrder, @tolerance)",
                new { materialGroup, defectCode, defectName, defectCategory, valueType, isActive, sortOrder, tolerance });
            TempData["Success"] = $"Defect '{defectName}' added to {materialGroup}.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_defect_catalog SET
                  material_group=@materialGroup, defect_code=@defectCode, defect_name=@defectName,
                  defect_category=@defectCategory, value_type=@valueType,
                  is_active=@isActive, sort_order=@sortOrder, tolerance=@tolerance
                WHERE defect_id=@defectId",
                new { defectId, materialGroup, defectCode, defectName, defectCategory, valueType, isActive, sortOrder, tolerance });
            TempData["Success"] = "Defect updated.";
        }
        await AuditAdminAsync(EntityTypes.DefectCatalog, defectId <= 0 ? 0 : defectId,
            defectId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { materialGroup, defectCode, defectName, defectCategory, valueType, isActive, sortOrder, tolerance });
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
    }

    // Delete a defect-catalog row -- refused if any sample_defect references
    // it, since the sample form's defect_id FK would orphan. Also wipes the
    // matching qms_material_group_defect binding row(s) so the per-group
    // catalog stays in sync.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.DefectCatalogEdit, Seed.ManagerOrAdmin, "Edit the Defect Catalog")]
    public async Task<IActionResult> DeleteDefect(int defectId, string? materialGroup)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var inUse = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_sample_defect WHERE defect_id = @defectId", new { defectId });
        if (inUse > 0)
        {
            TempData["Error"] = $"Defect is used by {inUse} sample defect record(s) -- cannot delete. Mark it inactive instead.";
            return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
        }

        // Remove the per-group binding first so the FK to defect_catalog doesn't block.
        await c.ExecuteAsync("DELETE FROM qms_material_group_defect WHERE defect_id = @defectId", new { defectId });
        var n = await c.ExecuteAsync("DELETE FROM qms_defect_catalog WHERE defect_id = @defectId", new { defectId });

        TempData[n > 0 ? "Success" : "Error"] = n > 0 ? "Defect deleted." : "Defect not found.";
        if (n > 0)
            await AuditAdminAsync(EntityTypes.DefectCatalog, defectId, ActionCodes.Deleted,
                new { defectId, materialGroup }, null);
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
    }

    // ---- Defect categories (V22+) -- global master list driving the
    // dynamic per-category sections in the sample form + PDF. -------------
    [HttpGet]
    [RequireScreen(Screens.DefectCategories, Seed.ManagerOrAdmin, "Open Defect Categories")]
    public async Task<IActionResult> DefectCategories()
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var rows = await c.QueryAsync<DefectCategory>(@"
            SELECT c.category_id CategoryId, c.category_name CategoryName,
                   c.sort_order SortOrder, c.color_hex ColorHex, c.is_active IsActive,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM qms_defect_catalog d WHERE d.defect_category = c.category_name)
                             THEN 1 ELSE 0 END AS BIT) IsInUse
            FROM   qms_defect_category c
            ORDER  BY c.sort_order, c.category_name");
        return View(rows.ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.DefectCategoriesEdit, Seed.ManagerOrAdmin, "Edit Defect Categories")]
    public async Task<IActionResult> SaveDefectCategory(int categoryId, string categoryName,
        int sortOrder, string? colorHex, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            TempData["Error"] = "Category name is required.";
            return RedirectToAction(nameof(DefectCategories));
        }
        categoryName = categoryName.Trim();
        // Normalize / validate colour (#RRGGBB); null it out otherwise.
        if (!string.IsNullOrWhiteSpace(colorHex) &&
            !System.Text.RegularExpressions.Regex.IsMatch(colorHex, "^#[0-9A-Fa-f]{6}$"))
            colorHex = null;

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var conflict = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 category_id FROM qms_defect_category
            WHERE category_name = @categoryName AND category_id <> @categoryId",
            new { categoryName, categoryId });
        if (conflict.HasValue)
        {
            TempData["Error"] = $"Another category already uses the name '{categoryName}'.";
            return RedirectToAction(nameof(DefectCategories));
        }

        if (categoryId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_defect_category (category_name, sort_order, color_hex, is_active)
                VALUES (@categoryName, @sortOrder, @colorHex, @isActive)",
                new { categoryName, sortOrder, colorHex, isActive });
            TempData["Success"] = $"Category '{categoryName}' added.";
        }
        else
        {
            // category_name change cascades to qms_defect_catalog.defect_category
            // via FK ON UPDATE CASCADE.
            await c.ExecuteAsync(@"
                UPDATE qms_defect_category SET
                  category_name=@categoryName, sort_order=@sortOrder,
                  color_hex=@colorHex, is_active=@isActive
                WHERE category_id=@categoryId",
                new { categoryId, categoryName, sortOrder, colorHex, isActive });
            TempData["Success"] = "Category updated.";
        }
        await AuditAdminAsync(EntityTypes.DefectCategory, categoryId <= 0 ? 0 : categoryId,
            categoryId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { categoryName, sortOrder, colorHex, isActive });
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCategories));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.DefectCategoriesEdit, Seed.ManagerOrAdmin, "Edit Defect Categories")]
    public async Task<IActionResult> DeleteDefectCategory(int categoryId)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var name = await c.ExecuteScalarAsync<string?>(
            "SELECT category_name FROM qms_defect_category WHERE category_id = @categoryId", new { categoryId });
        if (name == null)
        {
            TempData["Error"] = "Category not found.";
            return RedirectToAction(nameof(DefectCategories));
        }
        var inUse = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_defect_catalog WHERE defect_category = @name", new { name });
        if (inUse > 0)
        {
            TempData["Error"] = $"Category '{name}' is assigned to {inUse} defect(s) -- reassign or deactivate it instead of deleting.";
            return RedirectToAction(nameof(DefectCategories));
        }
        await c.ExecuteAsync("DELETE FROM qms_defect_category WHERE category_id = @categoryId", new { categoryId });
        TempData["Success"] = $"Category '{name}' deleted.";
        await AuditAdminAsync(EntityTypes.DefectCategory, categoryId, ActionCodes.Deleted, new { categoryId, name }, null);
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCategories));
    }

    [HttpGet]
    [RequireScreen(Screens.ReadingTypes, Seed.ManagerOrAdmin, "Open Reading Types")]
    public async Task<IActionResult> ReadingTypes(string? materialGroup = null)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // MARA groups for the dropdown (same source as Defect Catalog).
        var maraGroups = await _mara.ListMaterialGroupsAsync();
        IReadOnlyList<MaraGroup> groups = maraGroups;
        if (groups.Count == 0)
        {
            var fallback = (await c.QueryAsync<string>(@"
                SELECT DISTINCT material_group FROM qms_reading_type WHERE material_group IS NOT NULL
                UNION
                SELECT DISTINCT material_group FROM qms_quality_order_material WHERE material_group IS NOT NULL
                ORDER BY 1")).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
            groups = fallback.Select(g => new MaraGroup { Code = g!, Name = null }).ToList();
        }

        // IsInUse = a sample_reading exists with this catalog row's code on
        // a sample whose qo_material is in the same material group. (codes
        // can repeat across groups, so we scope the usage check by group.)
        // Globals (material_group IS NULL) are returned alongside the
        // filtered group so the admin can see they apply too -- the view
        // tags them with a "Global" badge so they're visually distinct
        // from per-group rows. IsInUse for a global row checks usage
        // across every material_group, not just the row's own (which
        // would always be NULL and produce a false negative).
        var rows = await c.QueryAsync<ReadingTypeEntry>(@"
            SELECT r.reading_type_id ReadingTypeId, r.reading_type_code ReadingTypeCode,
                   r.reading_name ReadingName, r.value_kind ValueKind,
                   r.default_unit DefaultUnit, r.is_active IsActive, r.sort_order SortOrder,
                   ISNULL(r.material_group, '') MaterialGroup, r.is_mandatory IsMandatory,
                   r.display_mode DisplayMode,
                   CAST(CASE WHEN EXISTS (
                            SELECT 1
                            FROM   qms_sample_reading sr
                            JOIN   qms_sample s ON s.sample_id = sr.sample_id
                            JOIN   qms_quality_order_material m ON m.qo_material_id = s.qo_material_id
                            WHERE  sr.reading_type_code = r.reading_type_code
                              AND  (r.material_group IS NULL OR m.material_group = r.material_group))
                             THEN 1 ELSE 0 END AS BIT) IsInUse
            FROM   qms_reading_type r
            WHERE  (@materialGroup IS NULL
                    OR r.material_group = @materialGroup
                    OR r.material_group IS NULL)
            ORDER  BY CASE WHEN r.material_group IS NULL THEN 0 ELSE 1 END,
                      r.material_group, r.sort_order, r.reading_name",
            new { materialGroup });

        ViewBag.Groups      = groups;
        ViewBag.FilterGroup = materialGroup;
        ViewBag.MaraEmpty   = maraGroups.Count == 0;
        return View(rows.ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ReadingTypesEdit, Seed.ManagerOrAdmin, "Edit Reading Types")]
    public async Task<IActionResult> SaveReadingType(int readingTypeId, string? materialGroup,
        string readingTypeCode, string readingName, string valueKind, string? defaultUnit,
        bool isActive, int sortOrder, bool isMandatory, string? displayMode)
    {
        // Empty / whitespace material group means "Global -- applies to every
        // material group". Store as NULL so the filtered unique index in V19
        // catches duplicate-global attempts at the DB level.
        var groupOrGlobal = string.IsNullOrWhiteSpace(materialGroup) ? null : materialGroup.Trim();
        if (valueKind != "Numeric" && valueKind != "Text") valueKind = "Numeric";

        // Whitelist the display_mode; matches the CK_qms_reading_type_display_mode
        // CHECK constraint (V18, extended with 'avg' in V38). Fall back to a
        // sensible default if a stale form posts something unknown.
        var allowedModes = new[] { "text", "count", "sum", "avg", "sum_over_size", "formula" };
        if (string.IsNullOrWhiteSpace(displayMode) || !allowedModes.Contains(displayMode))
            displayMode = valueKind == "Text" ? "text" : "sum";

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // Conflict check: a code can exist either as global OR per-group,
        // but the two namespaces must not overlap for the same effective
        // material group. New globals collide with any existing row of
        // that code; new per-group rows collide with the same group's row
        // OR with a global of the same code. The filtered unique indexes
        // in V19 cover same-namespace duplicates; this check covers the
        // cross-namespace case the DB can't enforce alone.
        var conflictRow = await c.QuerySingleOrDefaultAsync<(int Id, string? Group)?>(@"
            SELECT TOP 1 reading_type_id AS Id, material_group AS [Group]
            FROM   qms_reading_type
            WHERE  reading_type_code = @code
              AND  reading_type_id <> @selfId
              AND  (
                    @newIsGlobal = 1                                -- new global vs any existing
                 OR material_group IS NULL                          -- new per-group vs existing global
                 OR material_group = @group                         -- new per-group vs same-group existing
              )",
            new
            {
                code     = readingTypeCode,
                selfId   = readingTypeId,
                newIsGlobal = groupOrGlobal == null ? 1 : 0,
                @group   = groupOrGlobal
            });
        if (conflictRow.HasValue)
        {
            var conflictLabel = conflictRow.Value.Group ?? "Global";
            TempData["Error"] = $"Reading type code '{readingTypeCode}' already exists for '{conflictLabel}'. " +
                                "A code can be defined either as Global OR for a specific material group, not both.";
            return RedirectToAction(nameof(ReadingTypes), new { materialGroup });
        }

        if (readingTypeId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_reading_type
                    (material_group, reading_type_code, reading_name, value_kind,
                     default_unit, is_active, sort_order, is_mandatory, display_mode)
                VALUES (@groupOrGlobal, @readingTypeCode, @readingName, @valueKind,
                        @defaultUnit, @isActive, @sortOrder, @isMandatory, @displayMode)",
                new { groupOrGlobal, readingTypeCode, readingName, valueKind, defaultUnit, isActive, sortOrder, isMandatory, displayMode });
            TempData["Success"] = $"Reading type '{readingName}' added to {(groupOrGlobal ?? "Global")}.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_reading_type SET
                  material_group=@groupOrGlobal, reading_type_code=@readingTypeCode, reading_name=@readingName,
                  value_kind=@valueKind, default_unit=@defaultUnit, is_active=@isActive, sort_order=@sortOrder,
                  is_mandatory=@isMandatory, display_mode=@displayMode
                WHERE reading_type_id=@readingTypeId",
                new { readingTypeId, groupOrGlobal, readingTypeCode, readingName, valueKind, defaultUnit, isActive, sortOrder, isMandatory, displayMode });
            TempData["Success"] = "Reading type updated.";
        }
        await AuditAdminAsync(EntityTypes.ReadingType, readingTypeId <= 0 ? 0 : readingTypeId,
            readingTypeId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { groupOrGlobal, readingTypeCode, readingName, valueKind, isActive, isMandatory });
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(ReadingTypes), new { materialGroup });
    }

    // Delete a reading-type row -- refused if any sample_reading references
    // it (scoped by material_group + reading_type_code; codes repeat across
    // groups). The qms_material_group_reading binding is dropped first so
    // the FK to reading_type doesn't block the parent delete.
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ReadingTypesEdit, Seed.ManagerOrAdmin, "Edit Reading Types")]
    public async Task<IActionResult> DeleteReadingType(int readingTypeId, string? materialGroup)
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var inUse = await c.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*)
            FROM   qms_sample_reading sr
            JOIN   qms_sample s ON s.sample_id = sr.sample_id
            JOIN   qms_quality_order_material m ON m.qo_material_id = s.qo_material_id
            JOIN   qms_reading_type rt ON rt.reading_type_id = @readingTypeId
            WHERE  sr.reading_type_code = rt.reading_type_code
              AND  (rt.material_group IS NULL OR m.material_group = rt.material_group)",
            new { readingTypeId });
        if (inUse > 0)
        {
            TempData["Error"] = $"Reading type is used by {inUse} sample reading record(s) -- cannot delete. Mark it inactive instead.";
            return RedirectToAction(nameof(ReadingTypes), new { materialGroup });
        }

        await c.ExecuteAsync("DELETE FROM qms_material_group_reading WHERE reading_type_id = @readingTypeId", new { readingTypeId });
        var n = await c.ExecuteAsync("DELETE FROM qms_reading_type WHERE reading_type_id = @readingTypeId", new { readingTypeId });

        TempData[n > 0 ? "Success" : "Error"] = n > 0 ? "Reading type deleted." : "Reading type not found.";
        if (n > 0)
            await AuditAdminAsync(EntityTypes.ReadingType, readingTypeId, ActionCodes.Deleted, new { readingTypeId, materialGroup }, null);
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(ReadingTypes), new { materialGroup });
    }

    // ---- Sample Header Fields (V20+) -------------------------------------
    //
    // Configurable per-sample identification fields. Always global -- no
    // material_group binding. The admin can add / rename / disable header
    // fields from this screen; the sample form renders the active ones as
    // dynamic inputs (replacing the hardcoded Grower / Pallet / Date Code /
    // etc. columns on qms_sample). sample_size is intentionally NOT part
    // of this catalog -- it stays a first-class column on qms_sample
    // because every defect percentage divides by it.
    [HttpGet]
    [RequireScreen(Screens.SampleHeaders, Seed.ManagerOrAdmin, "Open Sample Headers")]
    public async Task<IActionResult> SampleHeaders()
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // IsInUse = at least one sample has a value for this field.
        // Blocks the Delete button so historical samples don't suddenly
        // lose data; admin should mark inactive instead.
        var rows = await c.QueryAsync<SampleHeaderField>(@"
            SELECT f.field_id    AS FieldId,
                   f.field_code  AS FieldCode,
                   f.field_name  AS FieldName,
                   f.value_kind  AS ValueKind,
                   f.default_unit AS DefaultUnit,
                   f.is_active   AS IsActive,
                   f.is_mandatory AS IsMandatory,
                   f.sort_order  AS SortOrder,
                   f.scope       AS Scope,
                   CAST(CASE WHEN EXISTS (
                            SELECT 1 FROM qms_sample_header_value v
                            WHERE v.field_id = f.field_id)
                            THEN 1 ELSE 0 END AS BIT) IsInUse
            FROM   qms_sample_header_field f
            ORDER  BY f.sort_order, f.field_name");
        return View(rows.ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SampleHeadersEdit, Seed.ManagerOrAdmin, "Edit Sample Headers")]
    public async Task<IActionResult> SaveSampleHeaderField(int fieldId, string fieldCode,
        string fieldName, string valueKind, string? defaultUnit,
        bool isActive, bool isMandatory, int sortOrder, string scope = "Sample")
    {
        if (string.IsNullOrWhiteSpace(fieldCode) || string.IsNullOrWhiteSpace(fieldName))
        {
            TempData["Error"] = "Field code and name are required.";
            return RedirectToAction(nameof(SampleHeaders));
        }
        // Whitelist matches the CK constraint added in V20.
        if (valueKind != "Text" && valueKind != "Numeric" && valueKind != "Date") valueKind = "Text";
        // Whitelist matches the CK constraint added in V21.
        if (scope != "Sample" && scope != "Material") scope = "Sample";

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // Conflict check on field_code (UNIQUE in schema, but a clearer
        // error message than a raw 2627 is friendlier).
        var conflictId = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 field_id FROM qms_sample_header_field
            WHERE field_code = @fieldCode AND field_id <> @fieldId",
            new { fieldCode, fieldId });
        if (conflictId.HasValue)
        {
            TempData["Error"] = $"Another sample header field already uses code '{fieldCode}'.";
            return RedirectToAction(nameof(SampleHeaders));
        }

        if (fieldId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_sample_header_field
                    (field_code, field_name, value_kind, default_unit, is_active, is_mandatory, sort_order, scope)
                VALUES (@fieldCode, @fieldName, @valueKind, @defaultUnit, @isActive, @isMandatory, @sortOrder, @scope)",
                new { fieldCode, fieldName, valueKind, defaultUnit, isActive, isMandatory, sortOrder, scope });
            TempData["Success"] = $"Sample header field '{fieldName}' added.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_sample_header_field SET
                    field_code=@fieldCode, field_name=@fieldName, value_kind=@valueKind,
                    default_unit=@defaultUnit, is_active=@isActive, is_mandatory=@isMandatory,
                    sort_order=@sortOrder, scope=@scope
                WHERE field_id=@fieldId",
                new { fieldId, fieldCode, fieldName, valueKind, defaultUnit, isActive, isMandatory, sortOrder, scope });
            TempData["Success"] = "Sample header field updated.";
        }
        await AuditAdminAsync(EntityTypes.SampleHeaderField, fieldId <= 0 ? 0 : fieldId,
            fieldId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { fieldCode, fieldName, valueKind, isActive, isMandatory, scope });
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(SampleHeaders));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SampleHeadersEdit, Seed.ManagerOrAdmin, "Edit Sample Headers")]
    public async Task<IActionResult> DeleteSampleHeaderField(int fieldId)
    {
        // Unlike Defect Catalog / Reading Types, sample-header deletion
        // cascades to the stored values on every sample (the field FK in
        // V20 has no ON DELETE CASCADE, so we do it explicitly). The
        // confirmation dialog already warns the user when the field is in
        // use, so by the time we get here the intent is clear.
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Pre-count for the success message; same query happens once inside
        // the transaction so the number we report matches what we deleted.
        var wipedValues = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_sample_header_value WHERE field_id = @fieldId",
            new { fieldId }, tx);

        await c.ExecuteAsync(
            "DELETE FROM qms_sample_header_value WHERE field_id = @fieldId",
            new { fieldId }, tx);

        var n = await c.ExecuteAsync(
            "DELETE FROM qms_sample_header_field WHERE field_id = @fieldId",
            new { fieldId }, tx);

        tx.Commit();

        if (n > 0)
        {
            TempData["Success"] = wipedValues > 0
                ? $"Sample header field deleted (and {wipedValues} stored value(s) removed across samples)."
                : "Sample header field deleted.";
        }
        else
        {
            TempData["Error"] = "Field not found.";
        }
        if (n > 0)
            await AuditAdminAsync(EntityTypes.SampleHeaderField, fieldId, ActionCodes.Deleted,
                new { fieldId, wipedValues }, null);
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(SampleHeaders));
    }

    // ---- Arrival Fields (V36, Parameters menu) ------------------------
    //
    // Admin-defined extra fields on an Arrival, each linked to exactly one
    // material group. The arrival Details page renders a field only when the
    // arrival's line items contain that group; the value also prints in the
    // Arrival Checklist PDF identity block after Seal Number.
    [HttpGet]
    [RequireScreen(Screens.ArrivalFields, Seed.ManagerOrAdmin, "Open Arrival Fields")]
    public async Task<IActionResult> ArrivalFields()
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var rows = await c.QueryAsync<ArrivalCustomField>(@"
            SELECT f.field_id       FieldId,
                   f.field_name     FieldName,
                   f.value_kind     ValueKind,
                   f.material_group MaterialGroup,
                   f.sort_order     SortOrder,
                   f.is_active      IsActive,
                   CAST(CASE WHEN EXISTS (
                            SELECT 1 FROM qms_arrival_field_value v
                            WHERE v.field_id = f.field_id)
                            THEN 1 ELSE 0 END AS BIT) IsInUse
            FROM   qms_arrival_field f
            ORDER  BY f.sort_order, f.field_name");

        // Material-group dropdown: MARA cache first, fall back to groups seen
        // on arrival items so the page works even when the cache is cold.
        var maraGroups = await _mara.ListMaterialGroupsAsync();
        IReadOnlyList<MaraGroup> groups = maraGroups;
        if (groups.Count == 0)
        {
            var fallback = (await c.QueryAsync<string>(@"
                SELECT DISTINCT material_group FROM qms_arrival_item
                WHERE material_group IS NOT NULL AND material_group <> ''")).ToList();
            groups = fallback.Select(g => new MaraGroup { Code = g, Name = null }).ToList();
        }
        ViewBag.MaterialGroups = groups;
        return View(rows.ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ArrivalFieldsEdit, Seed.ManagerOrAdmin, "Edit Arrival Fields")]
    public async Task<IActionResult> SaveArrivalField(int fieldId, string fieldName,
        string valueKind, string materialGroup, int sortOrder, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(materialGroup))
        {
            TempData["Error"] = "Field name and material group are required.";
            return RedirectToAction(nameof(ArrivalFields));
        }
        // Whitelist matches the CK constraint added in V36.
        if (!ArrivalCustomField.ValueKinds.Contains(valueKind)) valueKind = "Text";

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // Friendlier message than the raw UQ_qms_arrival_field_name_group 2627.
        var conflictId = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 field_id FROM qms_arrival_field
            WHERE field_name = @fieldName AND material_group = @materialGroup
              AND field_id <> @fieldId",
            new { fieldName, materialGroup, fieldId });
        if (conflictId.HasValue)
        {
            TempData["Error"] = $"A field named '{fieldName}' already exists for material group {materialGroup}.";
            return RedirectToAction(nameof(ArrivalFields));
        }

        var user = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system";
        if (fieldId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_arrival_field
                    (field_name, value_kind, material_group, sort_order, is_active, created_by)
                VALUES (@fieldName, @valueKind, @materialGroup, @sortOrder, @isActive, @user)",
                new { fieldName, valueKind, materialGroup, sortOrder, isActive, user });
            TempData["Success"] = $"Arrival field '{fieldName}' added for material group {materialGroup}.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_arrival_field SET
                    field_name=@fieldName, value_kind=@valueKind, material_group=@materialGroup,
                    sort_order=@sortOrder, is_active=@isActive
                WHERE field_id=@fieldId",
                new { fieldId, fieldName, valueKind, materialGroup, sortOrder, isActive });
            TempData["Success"] = "Arrival field updated.";
        }
        await AuditAdminAsync(EntityTypes.ArrivalField, fieldId <= 0 ? 0 : fieldId,
            fieldId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { fieldName, valueKind, materialGroup, sortOrder, isActive });
        return RedirectToAction(nameof(ArrivalFields));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ArrivalFieldsEdit, Seed.ManagerOrAdmin, "Edit Arrival Fields")]
    public async Task<IActionResult> DeleteArrivalField(int fieldId)
    {
        // Same policy as Sample Header Fields: deletion is allowed even when
        // in use — the confirm dialog warns that stored values are wiped —
        // and the value rows go in the same transaction.
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var wipedValues = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_arrival_field_value WHERE field_id = @fieldId",
            new { fieldId }, tx);
        await c.ExecuteAsync(
            "DELETE FROM qms_arrival_field_value WHERE field_id = @fieldId",
            new { fieldId }, tx);
        var n = await c.ExecuteAsync(
            "DELETE FROM qms_arrival_field WHERE field_id = @fieldId",
            new { fieldId }, tx);
        tx.Commit();

        if (n > 0)
        {
            TempData["Success"] = wipedValues > 0
                ? $"Arrival field deleted (and {wipedValues} stored value(s) removed across arrivals)."
                : "Arrival field deleted.";
            await AuditAdminAsync(EntityTypes.ArrivalField, fieldId, ActionCodes.Deleted,
                new { fieldId, wipedValues }, null);
        }
        else
        {
            TempData["Error"] = "Field not found.";
        }
        return RedirectToAction(nameof(ArrivalFields));
    }

    // ---- Arrival Field Rules (Parameters menu) ------------------------
    //
    // Per-field policy for the editable arrival fields (checklist + shipment):
    // whether each is Mandatory (blocks Complete when empty) and whether it can
    // be edited even after the arrival is Completed/closed. The field catalogue
    // is code-defined (ArrivalFieldRegistry); only the two flags are stored.
    [HttpGet]
    [RequireScreen(Screens.ArrivalFieldRules, Seed.ManagerOrAdmin, "Open Arrival Field Rules")]
    public async Task<IActionResult> ArrivalFieldRules()
    {
        var arrivals = HttpContext.RequestServices.GetRequiredService<IArrivalService>();
        var policies = await arrivals.GetArrivalFieldPoliciesAsync();
        // Ordered by form then registry order for a stable, grouped table.
        var rows = ArrivalFieldRegistry.All
            .Select(f => (Def: f, Policy: policies[f.Key]))
            .ToList();
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ArrivalFieldRulesEdit, Seed.ManagerOrAdmin, "Edit Arrival Field Rules")]
    public async Task<IActionResult> SaveArrivalFieldRules(string[]? mandatory, string[]? editableWhenClosed)
    {
        var arrivals = HttpContext.RequestServices.GetRequiredService<IArrivalService>();
        var mand = new HashSet<string>(mandatory ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var ewc  = new HashSet<string>(editableWhenClosed ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        // One row per registry field (checkboxes only post when ticked, so an
        // absent key means "off").
        var policies = ArrivalFieldRegistry.All.Select(f => new ArrivalFieldPolicy
        {
            FieldKey           = f.Key,
            IsMandatory        = mand.Contains(f.Key),
            EditableWhenClosed = ewc.Contains(f.Key)
        }).ToList();
        var user = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system";
        await arrivals.SaveArrivalFieldPoliciesAsync(policies, user);
        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null, new { section = "Arrival Field Rules" });
        TempData["Success"] = "Arrival field rules saved.";
        return RedirectToAction(nameof(ArrivalFieldRules));
    }

    // ---- Code Descriptions (M10, Parameters menu) ---------------------
    //
    // Friendly names for the raw SAP codes the lists used to print bare:
    // plants, storage locations and PO/document types. Replaces the old
    // hard-coded SapPlantDirectory (now only a fallback seed), so adding or
    // renaming a code no longer needs a redeploy.
    [HttpGet]
    [RequireScreen(Screens.CodeDescriptions, Seed.ManagerOrAdmin, "Open Code Descriptions")]
    public async Task<IActionResult> CodeDescriptions(string? domain, string? q)
    {
        if (!CodeDomains.IsValid(domain)) domain = CodeDomains.Plant;

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var rows = (await c.QueryAsync<CodeDescriptionEntry>(@"
            SELECT code_desc_id CodeDescId, domain Domain, parent_code ParentCode,
                   code Code, description Description, sort_order SortOrder,
                   is_active IsActive, updated_at UpdatedAt, updated_by UpdatedBy
            FROM   qms_code_description
            WHERE  domain = @domain
              AND (@q IS NULL OR code LIKE @q OR description LIKE @q OR parent_code LIKE @q)
            ORDER  BY parent_code, sort_order, code",
            new { domain, q = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%" })).ToList();

        // Counts per domain for the tab badges -- always unfiltered, so the
        // tabs don't appear to lose rows while a search is active.
        var counts = (await c.QueryAsync<(string Domain, int N)>(
            "SELECT domain AS Domain, COUNT(*) AS N FROM qms_code_description GROUP BY domain"))
            .ToDictionary(r => r.Domain, r => r.N, StringComparer.OrdinalIgnoreCase);

        ViewBag.Domain      = domain;
        ViewBag.Query       = q;
        ViewBag.Counts      = counts;
        ViewBag.PlantCodes  = (await c.QueryAsync<string>(
            "SELECT code FROM qms_code_description WHERE domain = 'Plant' ORDER BY code")).ToList();
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.CodeDescriptionsEdit, Seed.ManagerOrAdmin, "Edit Code Descriptions")]
    public async Task<IActionResult> SaveCodeDescription(int codeDescId, string domain, string? parentCode,
        string code, string description, int sortOrder, bool isActive)
    {
        if (!CodeDomains.IsValid(domain)) domain = CodeDomains.Plant;
        code        = (code ?? "").Trim();
        description = (description ?? "").Trim();
        // parent_code only means something for storage locations (codes repeat
        // across plants); force it null elsewhere so the uniqueness key is clean.
        parentCode  = domain == CodeDomains.StorageLocation
                          ? (string.IsNullOrWhiteSpace(parentCode) ? null : parentCode.Trim())
                          : null;

        if (code.Length == 0 || description.Length == 0)
        {
            TempData["Error"] = "Code and description are both required.";
            return RedirectToAction(nameof(CodeDescriptions), new { domain });
        }
        if (domain == CodeDomains.StorageLocation && parentCode == null)
        {
            TempData["Error"] = "A storage location needs its plant — the same code exists under several plants.";
            return RedirectToAction(nameof(CodeDescriptions), new { domain });
        }

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        // Friendlier than the raw UQ_qms_code_description 2627.
        var conflictId = await c.ExecuteScalarAsync<int?>(@"
            SELECT TOP 1 code_desc_id FROM qms_code_description
            WHERE domain = @domain AND ISNULL(parent_code,'') = ISNULL(@parentCode,'')
              AND code = @code AND code_desc_id <> @codeDescId",
            new { domain, parentCode, code, codeDescId });
        if (conflictId.HasValue)
        {
            TempData["Error"] = $"'{code}' already has a description in {CodeDomains.DisplayName(domain)}.";
            return RedirectToAction(nameof(CodeDescriptions), new { domain });
        }

        var user = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system";
        if (codeDescId <= 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_code_description
                    (domain, parent_code, code, description, sort_order, is_active, updated_by)
                VALUES (@domain, @parentCode, @code, @description, @sortOrder, @isActive, @user)",
                new { domain, parentCode, code, description, sortOrder, isActive, user });
            TempData["Success"] = $"'{code}' added to {CodeDomains.DisplayName(domain)}.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_code_description SET
                    parent_code = @parentCode, code = @code, description = @description,
                    sort_order = @sortOrder, is_active = @isActive,
                    updated_at = SYSUTCDATETIME(), updated_by = @user
                WHERE code_desc_id = @codeDescId",
                new { codeDescId, parentCode, code, description, sortOrder, isActive, user });
            TempData["Success"] = $"'{code}' updated.";
        }

        // Swap the in-memory lookup so the change is visible on the very next
        // page render rather than after the process restarts.
        await _codes.RefreshAsync();
        await AuditAdminAsync(EntityTypes.CodeDescription, codeDescId <= 0 ? 0 : codeDescId,
            codeDescId <= 0 ? ActionCodes.Created : ActionCodes.Updated,
            null, new { domain, parentCode, code, description, sortOrder, isActive });
        return RedirectToAction(nameof(CodeDescriptions), new { domain });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.CodeDescriptionsEdit, Seed.ManagerOrAdmin, "Edit Code Descriptions")]
    public async Task<IActionResult> DeleteCodeDescription(int codeDescId, string domain)
    {
        if (!CodeDomains.IsValid(domain)) domain = CodeDomains.Plant;
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var before = await c.QuerySingleOrDefaultAsync<CodeDescriptionEntry>(@"
            SELECT code_desc_id CodeDescId, domain Domain, parent_code ParentCode,
                   code Code, description Description
            FROM   qms_code_description WHERE code_desc_id = @codeDescId",
            new { codeDescId });

        var n = await c.ExecuteAsync(
            "DELETE FROM qms_code_description WHERE code_desc_id = @codeDescId", new { codeDescId });

        if (n > 0)
        {
            // Nothing references these rows -- the lists fall back to the seeded
            // name and then to the bare code, so a delete is always safe.
            TempData["Success"] = $"'{before?.Code}' removed. The lists will show the built-in name, or the code itself.";
            await _codes.RefreshAsync();
            await AuditAdminAsync(EntityTypes.CodeDescription, codeDescId, ActionCodes.Deleted, before, null);
        }
        else
        {
            TempData["Error"] = "Entry not found.";
        }
        return RedirectToAction(nameof(CodeDescriptions), new { domain });
    }

    // ---- Report Units (M11, Parameters menu) --------------------------
    //
    // One unit label per material group, printed by the Quality Order PDF in
    // place of the hard-coded word "Pieces": the group summary's Sample Size
    // and the count column above every defect list (summary and per sample).
    // A group with no row prints the default, so the table is sparse by design
    // and the page shows every known group with the default pre-filled.
    [HttpGet]
    [RequireScreen(Screens.ReportUnits, Seed.ManagerOrAdmin, "Open Report Units")]
    public async Task<IActionResult> ReportUnits()
    {
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));

        var configured = (await c.QueryAsync<(string MaterialGroup, string UnitLabel)>(
            "SELECT material_group AS MaterialGroup, unit_label AS UnitLabel FROM qms_material_group_unit"))
            .ToDictionary(r => r.MaterialGroup, r => r.UnitLabel, StringComparer.OrdinalIgnoreCase);

        // Group list: MARA cache first, falling back to the groups actually
        // seen on quality orders so the page still works with a cold cache.
        // Restricted to inspected material types (ZTRD / ZCON) -- spares,
        // packaging and advertising materials never reach a quality order, so
        // listing their groups here is noise.
        var maraGroups = await _mara.ListMaterialGroupsAsync(MaterialTypes.Inspected);
        IReadOnlyList<MaraGroup> groups = maraGroups;
        if (groups.Count == 0)
        {
            var fallback = (await c.QueryAsync<string>(@"
                SELECT DISTINCT material_group FROM qms_quality_order_material
                WHERE material_group IS NOT NULL AND material_group <> ''")).ToList();
            groups = fallback.Select(g => new MaraGroup { Code = g, Name = null }).ToList();
        }

        // Any group that has a saved unit but is missing from MARA still needs a
        // row, or the admin could never see (let alone clear) what they set.
        var known = groups.Select(g => g.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = configured.Keys.Where(k => !known.Contains(k))
                                     .Select(k => new MaraGroup { Code = k, Name = "(not in material master)" });

        var rows = groups.Concat(orphans)
            .OrderBy(g => g.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReportUnitRow
            {
                MaterialGroup = g.Code,
                GroupName     = g.Name,
                UnitLabel     = configured.TryGetValue(g.Code, out var u) ? u : ReportUnit.DefaultLabel,
                IsConfigured  = configured.ContainsKey(g.Code)
            })
            .ToList();

        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.ReportUnitsEdit, Seed.ManagerOrAdmin, "Edit Report Units")]
    public async Task<IActionResult> SaveReportUnits(string[] materialGroup, string[] unitLabel)
    {
        if (materialGroup == null || unitLabel == null || materialGroup.Length != unitLabel.Length)
        {
            TempData["Error"] = "Nothing to save — the form did not post as expected.";
            return RedirectToAction(nameof(ReportUnits));
        }

        var user = User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "system";
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            HttpContext.RequestServices.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var changed = new List<string>();
        for (int i = 0; i < materialGroup.Length; i++)
        {
            var grp   = (materialGroup[i] ?? "").Trim();
            if (grp.Length == 0) continue;
            var label = (unitLabel[i] ?? "").Trim();

            // Blank or the default means "no override" -- delete the row rather
            // than storing 'Pieces' everywhere, so the table stays sparse and
            // the default can be changed in one place later.
            if (label.Length == 0 || string.Equals(label, ReportUnit.DefaultLabel, StringComparison.OrdinalIgnoreCase))
            {
                var n = await c.ExecuteAsync(
                    "DELETE FROM qms_material_group_unit WHERE material_group = @grp", new { grp }, tx);
                if (n > 0) changed.Add($"{grp}→{ReportUnit.DefaultLabel}");
                continue;
            }
            if (label.Length > 20) label = label.Substring(0, 20);

            var affected = await c.ExecuteAsync(@"
                UPDATE qms_material_group_unit
                SET    unit_label = @label, updated_at = SYSUTCDATETIME(), updated_by = @user
                WHERE  material_group = @grp AND unit_label <> @label;

                INSERT INTO qms_material_group_unit (material_group, unit_label, updated_by)
                SELECT @grp, @label, @user
                WHERE  NOT EXISTS (SELECT 1 FROM qms_material_group_unit WHERE material_group = @grp);",
                new { grp, label, user }, tx);
            if (affected > 0) changed.Add($"{grp}→{label}");
        }
        tx.Commit();

        // The report reads these through ICatalogCache, so flush or the next
        // PDF still prints the old unit for up to an hour.
        _catalogCache.Invalidate();

        if (changed.Count > 0)
        {
            TempData["Success"] = $"Report units updated ({changed.Count} material group(s)).";
            await AuditAdminAsync(EntityTypes.ReportUnit, 0, ActionCodes.Updated, null, new { changed });
        }
        else
        {
            TempData["Success"] = "No changes to save.";
        }
        return RedirectToAction(nameof(ReportUnits));
    }

    // ---- Mail template (Parameters menu) -----------------------------
    [HttpGet]
    [RequireScreen(Screens.Notifications, Seed.ManagerOrAdmin, "Open Notifications")]
    public async Task<IActionResult> Notifications()
    {
        ViewBag.Candidates   = await _notifier.ListCandidatesAsync();
        ViewBag.ScopeOptions = await _notifier.GetScopeOptionsAsync();
        return View();
    }

    /// <summary>
    /// Replaces the recipient list. Posting no checkboxes is a valid choice --
    /// it turns the notification off -- so an absent list means empty, not
    /// "leave as it was".
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.NotificationsEdit, Seed.ManagerOrAdmin, "Choose who is notified")]
    public async Task<IActionResult> SaveNotifications(int[]? userIds)
    {
        var ids = userIds ?? Array.Empty<int>();

        // Scope arrives as plants_<userId> / poTypes_<userId>, so an unticked
        // person's leftover boxes are simply never read.
        var recipients = ids.Select(id => new NotifyRecipientInput(
            id,
            Request.Form[$"plants_{id}"].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray(),
            Request.Form[$"poTypes_{id}"].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray()
        )).ToList();

        // Audited with the BEFORE state, because this list is replaced wholesale
        // and there is no other record of it. When the plant and procurement
        // scopes were lost there was nothing anywhere to restore them from, and
        // fifty people silently started receiving every plant's notifications.
        var previous = (await _notifier.ListCandidatesAsync())
            .Where(p => p.Selected)
            .Select(p => new { p.UserId, p.Username, Plants = p.Plants, PoTypes = p.PoTypes })
            .ToList();

        await _notifier.SaveRecipientsAsync(recipients, User.FindFirstValue(ClaimTypes.Name) ?? "unknown");

        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated,
            new { section = "QO notifications", recipients = previous },
            new { section = "QO notifications",
                  recipients = recipients.Select(r => new { r.UserId, Plants = r.Plants, PoTypes = r.PoTypes }) });

        TempData["Success"] = ids.Length == 0
            ? "Nobody will be emailed when a quality order is finished."
            : $"{ids.Length} recipient(s) will be emailed when a quality order is finished.";
        return RedirectToAction(nameof(Notifications));
    }

    [RequireScreen(Screens.MailTemplate, Seed.ManagerOrAdmin, "Open the Mail Template")]
    public async Task<IActionResult> MailTemplate()
    {
        var cfg = await _settings.GetQoMailTemplateAsync();
        // A worked example of the automatic notification subject, so the page
        // can show what "leave it empty" actually sends rather than describing
        // it. Built by the same code that builds the real one.
        ViewBag.AutomaticSubjectExample = QcSubjectLine.Build(
            "QO-2026-000956", "Finished — Potential Claim",
            "MNBU3969264", "SQ FLORA B.V.", "065-49966700");
        return View(cfg);
    }

    /// <summary>
    /// The subject of the INTERNAL finish notification. Separate action from
    /// SaveMailTemplate so saving one mail's wording can never blank the
    /// other's: they are two forms on one page, and a single action would bind
    /// the fields the other form does not post to their defaults.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.MailTemplateEdit, Seed.ManagerOrAdmin, "Edit the Mail Template")]
    public async Task<IActionResult> SaveNotifySubject(string? notifySubject)
    {
        var cfg = await _settings.GetQoMailTemplateAsync();
        cfg.NotifySubject = (notifySubject ?? "").Trim();
        await _settings.SaveQoMailTemplateAsync(cfg, GetCurrentUserId());

        await AuditAdminAsync(EntityTypes.Configuration, 0, ActionCodes.Updated, null,
            new { section = "QO notification subject", cfg.NotifySubject });

        TempData["Success"] = cfg.NotifySubject.Length == 0
            ? "The notification will use the automatic subject line."
            : "Notification subject saved.";
        return RedirectToAction(nameof(MailTemplate));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.MailTemplateEdit, Seed.ManagerOrAdmin, "Edit the Mail Template")]
    public async Task<IActionResult> SaveMailTemplate(QoMailTemplate template)
    {
        template ??= new QoMailTemplate();

        // Validate here rather than at send time. A malformed standing CC would
        // otherwise fail every send with an error pointing at the supplier's
        // address, and nobody would think to look in Parameters.
        var bad = MailAddresses.Split(template.Cc)
            .Where(a => !MimeKit.MailboxAddress.TryParse(a, out _))
            .ToList();
        if (bad.Count > 0)
        {
            TempData["Error"] = bad.Count == 1
                ? $"'{bad[0]}' is not a valid e-mail address. Nothing was saved."
                : $"These are not valid e-mail addresses: {string.Join(", ", bad)}. Nothing was saved.";
            return RedirectToAction(nameof(MailTemplate));
        }
        template.Cc = string.Join(", ", MailAddresses.Split(template.Cc));

        var userId = (int?)null; // _settings doesn't currently look it up by id; pass null
        await _settings.SaveQoMailTemplateAsync(template, userId);
        TempData["Success"] = "Mail template saved.";
        return RedirectToAction(nameof(MailTemplate));
    }

    private async Task<SettingsVm> BuildSettingsVmAsync()
    {
        var ad = await _settings.GetAdConfigAsync();
        // Never echo the stored password back to the form; admin can leave
        // the field blank to keep the saved value (same convention as
        // SAP / SMTP passwords).
        ad.ServicePassword = "";
        // Show the admin where photos physically live right now (resolved path),
        // not just the saved override — a pending change still reads the old
        // folder until the next restart.
        var storage = await _settings.GetStorageConfigAsync();
        storage.EffectiveRoot = UploadStorage.Root(_env,
            HttpContext.RequestServices.GetRequiredService<IConfiguration>());
        return new SettingsVm
        {
            Sap          = await _settings.GetSapConfigAsync(),
            Smtp         = await _settings.GetSmtpConfigAsync(),
            Thumbnails   = await _settings.GetThumbnailConfigAsync(),
            TimeBar      = await _settings.GetTimeBarConfigAsync(),
            Alerts       = await _settings.GetAlertConfigAsync(),
            Report       = await _settings.GetReportConfigAsync(),
            Branding     = await _settings.GetBrandingConfigAsync(),
            Storage      = storage,
            Ad           = ad,
            MaterialSync = await _settings.GetEndpointSyncAsync(SyncableEndpoints.MaterialMaster),
            VendorSync   = await _settings.GetEndpointSyncAsync(SyncableEndpoints.VendorMaster),
            ContainerPoll = await BuildContainerPollVmAsync()
        };
    }

    private async Task<ContainerPollConfig> BuildContainerPollVmAsync()
    {
        var cfg = await _settings.GetContainerPollConfigAsync();
        // Decorate the typed config with the runtime pull status from
        // qms_sap_sync_log (last completed run + any in-flight pull).
        // Same source the Pending Containers page reads.
        try
        {
            var cache  = HttpContext.RequestServices.GetRequiredService<IContainerCacheService>();
            var status = await cache.GetPullStatusAsync(HttpContext.RequestAborted);
            cfg.LastRunUtc           = status.LastRunUtc;
            cfg.LastRowCount         = status.LastRowCount;
            cfg.LastResult           = status.LastResult;
            cfg.LastTriggerSource    = status.LastTriggerSource;
            cfg.IsRunning            = status.IsRunning;
            cfg.RunningSince         = status.RunningSince;
            cfg.RunningTriggerSource = status.RunningTriggerSource;
        }
        catch { /* status is best-effort */ }
        return cfg;
    }

    // ---- Active Directory settings (hosted as a tab on Site Configuration) ----

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.SettingsEdit, Seed.AdminOnly, "Change site configuration")]
    public async Task<IActionResult> SaveAdSettings(AdConfig cfg)
    {
        await _settings.SaveAdConfigAsync(cfg ?? new AdConfig(), GetCurrentUserId());
        TempData["Success"] = "AD settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "ad" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequireScreen(Screens.AdminSettings)]
    public async Task<IActionResult> TestAdConnectionAjax(string? domain, string? ldapPath,
        string? serviceUser, string? servicePassword)
    {
        // If the password field is blank, fall back to the saved one so the
        // admin can test the existing config without re-typing it.
        var saved = await _settings.GetAdConfigAsync();
        var probe = new AdConfig
        {
            Domain          = string.IsNullOrWhiteSpace(domain)      ? saved.Domain      : domain,
            LdapPath        = string.IsNullOrWhiteSpace(ldapPath)    ? saved.LdapPath    : ldapPath,
            ServiceUser     = string.IsNullOrWhiteSpace(serviceUser) ? saved.ServiceUser : serviceUser,
            ServicePassword = string.IsNullOrWhiteSpace(servicePassword) ? saved.ServicePassword : servicePassword
        };
        var (ok, message) = await _ad.TestConnectionAsync(probe);
        return Json(new { ok, message });
    }

    // ---- Branding tab (logo upload + company info on reports) ----

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> SaveBrandingSettings(BrandingConfig branding)
    {
        await _settings.SaveBrandingConfigAsync(branding, GetCurrentUserId());
        TempData["Success"] = "Branding settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> UploadCompanyLogo(IFormFile logo)
    {
        if (logo == null || logo.Length == 0)
        {
            TempData["Error"] = "No file selected.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };
        var ext = Path.GetExtension(logo.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
        {
            TempData["Error"] = "Logo must be PNG, JPG, GIF, WEBP, or SVG.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }
        if (logo.Length > 2 * 1024 * 1024)
        {
            TempData["Error"] = "Logo must be under 2 MB.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }

        var brandingDir = Path.Combine(_env.WebRootPath, "branding");
        Directory.CreateDirectory(brandingDir);

        // A NEW file name every time, rather than overwriting a fixed one.
        //
        // The logo is served out of wwwroot by the static-file middleware, which
        // keeps a memory-mapped section open on a file it has served. Windows
        // refuses to truncate or delete a file in that state, so the old code
        // failed with "The requested operation cannot be performed on a file
        // with a user-mapped section open" -- every upload after the current
        // logo had been displayed once, which is to say every upload. Writing a
        // file nobody has opened yet cannot collide, and the changing name also
        // busts the browser cache that a fixed name would keep serving.
        var fileName = $"company-logo-{DateTime.UtcNow:yyyyMMddHHmmssfff}{ext}";
        var fullPath = Path.Combine(brandingDir, fileName);

        try
        {
            await using var fs = System.IO.File.Create(fullPath);
            await logo.CopyToAsync(fs);
        }
        catch (IOException ex)
        {
            // Never a 500 for this: the administrator can act on a sentence,
            // not on a stack trace behind a support reference.
            _adminLog.LogError(ex, "Company logo upload failed writing {Path}", fullPath);
            TempData["Error"] = "The logo could not be saved to disk. " +
                                "Check free space and the service account's rights on the branding folder.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }

        await _settings.SaveLogoFilenameAsync(fileName, GetCurrentUserId());

        // Previous logos, best-effort and AFTER the new one is live: one still
        // mapped by the middleware cannot be deleted until the service next
        // restarts, and failing to tidy up is not a reason to fail the upload.
        foreach (var prior in Directory.GetFiles(brandingDir, "company-logo*"))
        {
            if (string.Equals(Path.GetFileName(prior), fileName, StringComparison.OrdinalIgnoreCase)) continue;
            try { System.IO.File.Delete(prior); } catch { /* still mapped, or in use */ }
        }

        TempData["Success"] = "Company logo uploaded.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> RemoveCompanyLogo()
    {
        var cfg = await _settings.GetBrandingConfigAsync();
        if (cfg.HasLogo)
        {
            // Refuse to act on a filename that contains directory components --
            // a tampered settings row could otherwise point at a file outside
            // wwwroot/branding.
            var safeName = Path.GetFileName(cfg.LogoFilename);
            if (string.Equals(safeName, cfg.LogoFilename, StringComparison.Ordinal))
            {
                var fullPath = Path.Combine(_env.WebRootPath, "branding", safeName);
                // Best-effort on purpose: a file the static-file middleware has
                // served is memory-mapped and cannot be deleted until the
                // service restarts. Clearing the setting below is what actually
                // removes the logo from the application, so a failed delete
                // leaves an orphaned file and nothing worse.
                try { System.IO.File.Delete(fullPath); } catch { /* still mapped, or in use */ }
            }
            await _settings.SaveLogoFilenameAsync("", GetCurrentUserId());
        }
        TempData["Success"] = "Company logo removed.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    // ---- Branding tab (page icon / favicon) ----

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> SaveFaviconChoice(string? choice)
    {
        choice = (choice ?? "").Trim();
        // Accept: "" (default favicon.ico), "custom" (uploaded file), or one
        // of the built-in fruit keys. Anything else falls back to "" so a
        // tampered POST can't poison the column with arbitrary strings.
        var valid = string.IsNullOrEmpty(choice)
                    || choice == FaviconChoices.Custom
                    || FaviconChoices.IsBuiltIn(choice);
        if (!valid) choice = "";
        // Selecting "custom" without an uploaded file makes no sense -- silently
        // fall back to default rather than show a broken favicon.
        if (choice == FaviconChoices.Custom)
        {
            var cfg = await _settings.GetBrandingConfigAsync();
            if (!cfg.HasCustomFavicon) choice = "";
        }
        await _settings.SaveFaviconChoiceAsync(choice, GetCurrentUserId());
        TempData["Success"] = "Page icon updated.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> UploadCustomFavicon(IFormFile favicon)
    {
        if (favicon == null || favicon.Length == 0)
        {
            TempData["Error"] = "No file selected.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico" };
        var ext = Path.GetExtension(favicon.FileName).ToLowerInvariant();
        if (!allowed.Contains(ext))
        {
            TempData["Error"] = "Page icon must be PNG, JPG, GIF, WEBP, SVG, or ICO.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }
        if (favicon.Length > 2 * 1024 * 1024)
        {
            TempData["Error"] = "Page icon must be under 2 MB.";
            return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
        }

        var brandingDir = Path.Combine(_env.WebRootPath, "branding");
        Directory.CreateDirectory(brandingDir);

        // Clean prior custom-favicon files (different extension may be replacing).
        foreach (var prior in Directory.GetFiles(brandingDir, "custom-favicon.*"))
            try { System.IO.File.Delete(prior); } catch { /* best-effort */ }

        var fileName = "custom-favicon" + ext;
        var fullPath = Path.Combine(brandingDir, fileName);
        await using (var fs = System.IO.File.Create(fullPath))
            await favicon.CopyToAsync(fs);

        // Save the filename AND flip the choice to "custom" so the upload
        // takes effect immediately -- the whole point of clicking Upload is
        // that the user wants this image active.
        await _settings.SaveFaviconCustomFilenameAsync(fileName, GetCurrentUserId());
        await _settings.SaveFaviconChoiceAsync(FaviconChoices.Custom, GetCurrentUserId());
        TempData["Success"] = "Your image is now the page icon. Press Ctrl+F5 to refresh.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.Branding, Seed.AdminOnly, "Change branding")]
    public async Task<IActionResult> RemoveCustomFavicon()
    {
        var cfg = await _settings.GetBrandingConfigAsync();
        if (cfg.HasCustomFavicon)
        {
            // Refuse to act on a filename with directory separators -- prevents
            // a tampered settings row pointing outside wwwroot/branding.
            var safeName = Path.GetFileName(cfg.FaviconCustomFilename);
            if (string.Equals(safeName, cfg.FaviconCustomFilename, StringComparison.Ordinal))
            {
                var fullPath = Path.Combine(_env.WebRootPath, "branding", safeName);
                try { System.IO.File.Delete(fullPath); } catch { /* best-effort */ }
            }
            await _settings.SaveFaviconCustomFilenameAsync("", GetCurrentUserId());
            // If the user is currently using the custom image, fall back to default.
            if (cfg.FaviconChoice == FaviconChoices.Custom)
                await _settings.SaveFaviconChoiceAsync("", GetCurrentUserId());
        }
        TempData["Success"] = "Custom page icon removed.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    private int GetCurrentUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return int.TryParse(claim?.Value, out var id) ? id : 0;
    }

    // Best-effort audit for administrative / master-data mutations. Uses the
    // self-contained (non-transactional) audit overload; a failure here is
    // logged and swallowed so it never blocks the admin action.
    /// <summary>
    /// Writes readable rows to the dedicated permission log (M21). Separate from
    /// <see cref="AuditAdminAsync"/> on purpose: the audit row keeps the raw
    /// before/after JSON, this keeps one plain line per thing that changed.
    /// </summary>
    private Task PermLogAsync(params PermissionLogEntry[] entries)
    {
        var actor = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        foreach (var e in entries) e.ChangedBy = actor;
        return _permLog.WriteAsync(entries);
    }

    private PermissionLogEntry UserEntry(Models.User u, string changeType) => new()
    {
        SubjectType = PermissionSubjects.User,
        SubjectKey  = u.Username,
        SubjectName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName,
        ChangeType  = changeType
    };

    private async Task AuditAdminAsync(string entityType, long entityId, string action,
        object? oldValues, object? newValues)
    {
        var actor = User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        try { await _audit.WriteAsync(entityType, entityId, action, oldValues, newValues, actor); }
        catch (Exception ex) { _adminLog.LogError(ex, "Admin audit write failed for {Entity} {Action}", entityType, action); }
    }
}

public class SettingsVm
{
    public SapEndpointConfig Sap          { get; set; } = new();
    public SmtpConfig        Smtp         { get; set; } = new();
    public ThumbnailConfig   Thumbnails   { get; set; } = new();
    public AlertConfig       Alerts       { get; set; } = new();
    public TimeBarConfig     TimeBar      { get; set; } = new();
    public ReportConfig      Report       { get; set; } = new();
    public BrandingConfig    Branding     { get; set; } = new();
    public StorageConfig     Storage      { get; set; } = new();
    public AdConfig          Ad           { get; set; } = new();
    public EndpointSyncConfig  MaterialSync  { get; set; } = new() { EndpointKey = SyncableEndpoints.MaterialMaster };
    public EndpointSyncConfig  VendorSync    { get; set; } = new() { EndpointKey = SyncableEndpoints.VendorMaster };
    public ContainerPollConfig ContainerPoll { get; set; } = new();
}
