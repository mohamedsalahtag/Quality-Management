using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Controllers;

// Class-level gate is the loosest policy any action here uses (Manager+).
// Each truly admin-only action explicitly adds [Authorize(Policy = AdminOnly)]
// so AND-combining lifts it back to SiteAdmin. Parameters-menu actions
// (defect catalog / reading types / mail template) stay Manager-accessible.
[Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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

    public AdminController(IDbService db, ISettingsService settings,
        ISapODataClient sapOData, ISapSyncService sapSync, IEmailService email,
        IAdService ad, IServiceScopeFactory scopeFactory, IWebHostEnvironment env,
        ILogger<AdminController> adminLog, ICatalogCache catalogCache, IMaraService mara)
    {
        _db = db; _settings = settings; _sapOData = sapOData; _sapSync = sapSync;
        _email = email; _ad = ad; _scopeFactory = scopeFactory; _env = env; _adminLog = adminLog;
        _catalogCache = catalogCache; _mara = mara;
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
        return View(users);
    }

    // Users are added by picking from Active Directory -- never typed
    // free-form. The picker UI in /Admin/Users posts here with the selected
    // username + chosen role. We resolve the AD profile from the cached
    // browse list (zero round-trip in the common case); fall back to a
    // direct LDAP lookup only if the cache has expired between modal-open
    // and Add.
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> CreateUser(string username, string role, string? plantCode)
    {
        if (string.IsNullOrWhiteSpace(username) || !UserRoles.IsValid(role))
        {
            TempData["Error"] = "Username and a valid role are required.";
            return RedirectToAction(nameof(Users));
        }
        // Plant scope applies to Operators only; the picker is hidden in the UI
        // for every other role but we strip on the server too as a hard rule.
        if (!string.Equals(role, UserRoles.Operator, StringComparison.OrdinalIgnoreCase))
            plantCode = null;
        else if (string.IsNullOrWhiteSpace(plantCode))
            plantCode = null;
        var adCfg = await _settings.GetAdConfigAsync();
        if (!adCfg.IsConfigured)
        {
            TempData["Error"] = "Active Directory is not configured. Open Site Configuration → Active Directory first.";
            return RedirectToAction(nameof(Users));
        }
        if (await _db.GetUserByUsernameAsync(username) != null)
        {
            TempData["Error"] = $"A user named '{username}' already exists.";
            return RedirectToAction(nameof(Users));
        }

        // Fast path: the picker just rendered the full AD list and the
        // service has it cached. Look the chosen username up in memory.
        var cached = await _ad.ListUsersAsync(adCfg, null, max: int.MaxValue);
        var info = cached.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        // Slow path / fallback: cache miss or stale entry. One direct
        // LDAP lookup keeps Add working even after a server restart
        // between the picker open and the Add click.
        info ??= await _ad.GetUserInfoAsync(username, adCfg);
        if (info == null)
        {
            TempData["Error"] = $"User '{username}' was not found in Active Directory.";
            return RedirectToAction(nameof(Users));
        }
        await _db.CreateUserAsync(new User
        {
            Username     = info.Username,
            FullName     = info.FullName,
            Email        = info.Email,
            Department   = info.Department,
            Role         = role,
            PlantCode    = plantCode,
            PasswordHash = "",        // AD-managed; password lives in the directory
            IsActive     = true,
            CreatedBy    = GetCurrentUserId()
        });
        TempData["Success"] = plantCode != null
            ? $"User '{info.Username}' added as {role} scoped to plant {plantCode}."
            : $"User '{info.Username}' added with role {role}.";
        return RedirectToAction(nameof(Users));
    }

    /// <summary>
    /// Lists AD users for the "Add user from AD" picker on /Admin/Users.
    /// Returns a JSON shape the modal renders directly, with an
    /// alreadyAdded flag for users already imported into the local Users
    /// table so the picker can grey them out.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> EditUser(int userId, string fullName, string email,
        string? department, string? employeeId, string role, string? plantCode)
    {
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();
        if (!UserRoles.IsValid(role))
        {
            TempData["Error"] = "Invalid role.";
            return RedirectToAction(nameof(Users));
        }

        u.FullName   = fullName;
        u.Email      = email;
        u.Department = department;
        u.EmployeeId = employeeId;
        u.Role       = role;
        // Plant scope applies to Operators only; clear for every other role.
        u.PlantCode  = string.Equals(role, UserRoles.Operator, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(plantCode)
                            ? plantCode
                            : null;
        await _db.UpdateUserAsync(u);
        TempData["Success"] = $"User '{u.Username}' updated.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> ResetPassword(int userId, string newPassword)
    {
        var u = await _db.GetUserByIdAsync(userId);
        if (u == null) return NotFound();
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            TempData["Error"] = "Password must be at least 6 characters.";
            return RedirectToAction(nameof(Users));
        }
        u.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await _db.UpdateUserAsync(u);
        TempData["Success"] = $"Password reset for '{u.Username}'.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
        await _db.SetUserActiveAsync(userId, !u.IsActive, current);
        TempData["Success"] = $"User '{u.Username}' is now {(!u.IsActive ? "active" : "disabled")}.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
        var ok = await _db.TryDeleteUserAsync(userId);
        TempData[ok ? "Success" : "Error"] = ok
            ? $"User '{u.Username}' deleted."
            : $"User '{u.Username}' is referenced elsewhere - disable instead.";
        return RedirectToAction(nameof(Users));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
            if (await _db.TryDeleteUserAsync(id)) deleted++;
            else blockedByFk++;
        }
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
            ("quality_order",          "DELETE FROM qms_quality_order"),
            ("arrival_checklist",      "DELETE FROM qms_arrival_checklist"),
            ("arrival_item",           "DELETE FROM qms_arrival_item"),
            ("arrival_sap_snapshot",   "DELETE FROM qms_arrival_sap_snapshot"),
            ("shipment_snapshot",      "DELETE FROM qms_shipment_snapshot"),
            ("arrival",                "DELETE FROM qms_arrival"),
            ("image_link",             "DELETE FROM qms_image_link"),
            ("image_asset",            "DELETE FROM qms_image_asset"),
            ("status_history",         "DELETE FROM qms_status_history"),
            ("report_log",             "DELETE FROM qms_report_log"),
            ("audit_log",              "DELETE FROM qms_audit_log")
        };

        // Reseed IDENTITY counters back to 1 so new records start from #1.
        var reseedTables = new[]
        {
            "qms_sample_defect","qms_sample_reading","qms_sample_observation","qms_sample",
            "qms_quality_order_material","qms_quality_order",
            "qms_arrival_checklist","qms_arrival_item","qms_arrival_sap_snapshot",
            "qms_shipment_snapshot","qms_arrival",
            "qms_image_link","qms_image_asset",
            "qms_status_history","qms_report_log","qms_audit_log"
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
        var uploadsRoot = Path.Combine(_env.WebRootPath, "uploads");
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

        _catalogCache.Invalidate();
        TempData["Success"] = perTable.Count == 0
            ? $"System was already clean. {filesDeleted} image file(s) removed. Sequences reset."
            : $"Purged {totalDeleted} row(s) ({string.Join(", ", perTable)}) and {filesDeleted} image file(s). Sequences reset to #1.";
        return RedirectToAction(nameof(Settings), new { activeTab = "danger" });
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Settings(string? activeTab = null)
    {
        var vm = await BuildSettingsVmAsync();
        ViewBag.ActiveTab = activeTab ?? "sap";
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveSapSettings(SapEndpointConfig sap)
    {
        await _settings.SaveSapConfigAsync(sap, GetCurrentUserId());
        TempData["Success"] = "SAP OData settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveSmtpSettings(SmtpConfig smtp)
    {
        await _settings.SaveSmtpConfigAsync(smtp, GetCurrentUserId());
        TempData["Success"] = "SMTP settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "smtp" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveThumbnailSettings(ThumbnailConfig thumbnails)
    {
        // Parameter name must equal the property name on SettingsVm (Thumbnails)
        // because Razor renders the form fields as "Thumbnails.ScreenWidth" etc.
        // and the default model binder matches the parameter name as the prefix.
        // A different parameter name (e.g. "thumb") silently binds to defaults.
        await _settings.SaveThumbnailConfigAsync(thumbnails, GetCurrentUserId());
        TempData["Success"] = "Thumbnail settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "thumbnails" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveAlertSettings(AlertConfig alerts)
    {
        await _settings.SaveAlertConfigAsync(alerts, GetCurrentUserId());
        TempData["Success"] = "Alert thresholds saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "alerts" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveContainerPollSettings(ContainerPollConfig containerPoll)
    {
        var cfg = containerPoll ?? new ContainerPollConfig();
        // Clamp into the allowed range so a fat-fingered value can't take
        // SAP down by pulling every second or stall the queue forever.
        if (cfg.PollingMinutes < 5)    cfg.PollingMinutes = 5;
        if (cfg.PollingMinutes > 1440) cfg.PollingMinutes = 1440;
        await _settings.SaveContainerPollConfigAsync(cfg, GetCurrentUserId());
        TempData["Success"] = "Pending Containers pulling saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "sap" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
                await cache.RefreshFromSapAsync(startDate, user, "Manual", CancellationToken.None);
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> TestSapEndpointAjax(string url, string? user = null, string? password = null, string? endpointKey = null)
    {
        var (u, p) = await ResolveCredsForLiveTestAsync(user, password, endpointKey);
        var (ok, msg) = await _sapOData.TestEndpointAsync(url, NullIfBlank(u), NullIfBlank(p));
        return Json(new { ok, message = msg });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM qms_sample_defect sd WHERE sd.defect_id = d.defect_id)
                             THEN 1 ELSE 0 END AS BIT) IsInUse
            FROM   qms_defect_catalog d
            WHERE  (@materialGroup IS NULL OR d.material_group = @materialGroup)
            ORDER  BY d.material_group, d.sort_order, d.defect_name",
            new { materialGroup });

        ViewBag.Groups        = groups;
        ViewBag.FilterGroup   = materialGroup;
        ViewBag.MaraEmpty     = maraGroups.Count == 0;
        ViewBag.Categories    = await _catalogCache.GetActiveCategoriesAsync();
        return View(rows.ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> SaveDefect(int defectId, string materialGroup, string defectCode,
        string defectName, string defectCategory, string valueType,
        bool isActive, int sortOrder)
    {
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
                     is_active, sort_order)
                VALUES
                    (@materialGroup, @defectCode, @defectName, @defectCategory, @valueType,
                     @isActive, @sortOrder)",
                new { materialGroup, defectCode, defectName, defectCategory, valueType, isActive, sortOrder });
            TempData["Success"] = $"Defect '{defectName}' added to {materialGroup}.";
        }
        else
        {
            await c.ExecuteAsync(@"
                UPDATE qms_defect_catalog SET
                  material_group=@materialGroup, defect_code=@defectCode, defect_name=@defectName,
                  defect_category=@defectCategory, value_type=@valueType,
                  is_active=@isActive, sort_order=@sortOrder
                WHERE defect_id=@defectId",
                new { defectId, materialGroup, defectCode, defectName, defectCategory, valueType, isActive, sortOrder });
            TempData["Success"] = "Defect updated.";
        }
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
    }

    // Delete a defect-catalog row -- refused if any sample_defect references
    // it, since the sample form's defect_id FK would orphan. Also wipes the
    // matching qms_material_group_defect binding row(s) so the per-group
    // catalog stays in sync.
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCatalog), new { materialGroup });
    }

    // ---- Defect categories (V22+) -- global master list driving the
    // dynamic per-category sections in the sample form + PDF. -------------
    [HttpGet]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCategories));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(DefectCategories));
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        // CHECK constraint added in V18. Fall back to a sensible default if
        // a stale form posts something unknown.
        var allowedModes = new[] { "text", "count", "sum", "sum_over_size", "formula" };
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(ReadingTypes), new { materialGroup });
    }

    // Delete a reading-type row -- refused if any sample_reading references
    // it (scoped by material_group + reading_type_code; codes repeat across
    // groups). The qms_material_group_reading binding is dropped first so
    // the FK to reading_type doesn't block the parent delete.
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(SampleHeaders));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
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
        _catalogCache.Invalidate();
        return RedirectToAction(nameof(SampleHeaders));
    }

    // ---- Mail template (Parameters menu) -----------------------------
    [HttpGet]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> MailTemplate()
    {
        var cfg = await _settings.GetQoMailTemplateAsync();
        return View(cfg);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> SaveMailTemplate(QoMailTemplate template)
    {
        var userId = (int?)null; // _settings doesn't currently look it up by id; pass null
        await _settings.SaveQoMailTemplateAsync(template ?? new QoMailTemplate(), userId);
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
        return new SettingsVm
        {
            Sap          = await _settings.GetSapConfigAsync(),
            Smtp         = await _settings.GetSmtpConfigAsync(),
            Thumbnails   = await _settings.GetThumbnailConfigAsync(),
            Alerts       = await _settings.GetAlertConfigAsync(),
            Branding     = await _settings.GetBrandingConfigAsync(),
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveAdSettings(AdConfig cfg)
    {
        await _settings.SaveAdConfigAsync(cfg ?? new AdConfig(), GetCurrentUserId());
        TempData["Success"] = "AD settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "ad" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> SaveBrandingSettings(BrandingConfig branding)
    {
        await _settings.SaveBrandingConfigAsync(branding, GetCurrentUserId());
        TempData["Success"] = "Branding settings saved.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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

        // Remove any prior logo files so the fixed name + new extension can take
        // over cleanly. (Extension can differ from upload to upload.)
        foreach (var prior in Directory.GetFiles(brandingDir, "company-logo.*"))
            try { System.IO.File.Delete(prior); } catch { /* best-effort */ }

        var fileName = "company-logo" + ext;
        var fullPath = Path.Combine(brandingDir, fileName);
        await using (var fs = System.IO.File.Create(fullPath))
            await logo.CopyToAsync(fs);

        await _settings.SaveLogoFilenameAsync(fileName, GetCurrentUserId());
        TempData["Success"] = "Company logo uploaded.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
                try { System.IO.File.Delete(fullPath); } catch { /* best-effort */ }
            }
            await _settings.SaveLogoFilenameAsync("", GetCurrentUserId());
        }
        TempData["Success"] = "Company logo removed.";
        return RedirectToAction(nameof(Settings), new { activeTab = "branding" });
    }

    // ---- Branding tab (page icon / favicon) ----

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
    [Authorize(Policy = AuthPolicies.AdminOnly)]
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
}

public class SettingsVm
{
    public SapEndpointConfig Sap          { get; set; } = new();
    public SmtpConfig        Smtp         { get; set; } = new();
    public ThumbnailConfig   Thumbnails   { get; set; } = new();
    public AlertConfig       Alerts       { get; set; } = new();
    public BrandingConfig    Branding     { get; set; } = new();
    public AdConfig          Ad           { get; set; } = new();
    public EndpointSyncConfig  MaterialSync  { get; set; } = new() { EndpointKey = SyncableEndpoints.MaterialMaster };
    public EndpointSyncConfig  VendorSync    { get; set; } = new() { EndpointKey = SyncableEndpoints.VendorMaster };
    public ContainerPollConfig ContainerPoll { get; set; } = new();
}
