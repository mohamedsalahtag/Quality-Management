using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

public class AccountController : Controller
{
    private static readonly HashSet<string> AllowedAvatarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };
    private const long MaxAvatarBytes = 2L * 1024 * 1024; // 2 MB

    private readonly IDbService _db;
    private readonly IAdService _ad;
    private readonly ISettingsService _settings;
    private readonly IWebHostEnvironment _env;
    private readonly IUserPermissions _perms;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IDbService db, IAdService ad, ISettingsService settings,
        IWebHostEnvironment env, IUserPermissions perms, ILogger<AccountController> logger)
    {
        _db = db;
        _ad = ad;
        _settings = settings;
        _env = env;
        _perms = perms;
        _logger = logger;
    }

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        var adCfg = await _settings.GetAdConfigAsync();
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.AdEnabled = adCfg.IsConfigured;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken, AllowAnonymous]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        var adCfg = await _settings.GetAdConfigAsync();
        ViewBag.AdEnabled = adCfg.IsConfigured;
        if (!ModelState.IsValid) return View(model);

        // Normalize whatever the user typed to the SAM-only form so
        // "mohamed.tag" and "mohamed.tag@sharbatlyfruit.com" map to the same
        // local Users row. AD bind itself still accepts either form.
        static string SamOnly(string? s) =>
            (s ?? "").Trim().Split('@')[0].ToLowerInvariant();

        // 1. Try Active Directory bind first when configured. Auto-create
        //    the local Users row on first successful bind if the admin has
        //    enabled it -- otherwise we refuse and ask an admin to create
        //    the account.
        if (adCfg.IsConfigured)
        {
            var (adOk, adInfo, adErr) = await _ad.AuthenticateAsync(model.Username, model.Password, adCfg);
            if (adOk && adInfo != null)
            {
                // adInfo.Username is the canonical sAMAccountName returned by
                // AD (e.g. "mohamed.tag"), never the UPN form. Use it as the
                // DB lookup key so login is idempotent regardless of input.
                var canonical = SamOnly(adInfo.Username);
                var user = await _db.GetUserByUsernameAsync(canonical);
                if (user == null)
                {
                    // Identity is shared with the SCM app now, so a missing
                    // profile is never resolved by inventing a portal.User row.
                    // Auto-create degrades to "grant the default quality role to
                    // an account the portal already knows".
                    if (!adCfg.AutoCreateOnLogin ||
                        !await _db.TryGrantDefaultAccessAsync(canonical))
                    {
                        ModelState.AddModelError("",
                            "Your AD account is recognised but you have no quality-system access. Ask an administrator to grant it.");
                        return View(model);
                    }
                    user = await _db.GetUserByUsernameAsync(canonical);
                    if (user == null)
                    {
                        ModelState.AddModelError("",
                            "Your AD account is recognised but you have no quality-system access. Ask an administrator to grant it.");
                        return View(model);
                    }
                    _logger.LogInformation("Granted default QMS access to existing portal user {User}", canonical);
                }
                if (!user.IsActive)
                {
                    ModelState.AddModelError("", "Your account has been disabled. Contact the administrator.");
                    return View(model);
                }
                await IssueCookieAsync(user, model.RememberMe);
                _logger.LogInformation("User {User} signed in via AD", user.Username);
                return RedirectAfterLogin(model);
            }
            // adErr is logged inside AdService -- we don't reveal AD-specific
            // failure reasons to the client.
            _logger.LogDebug("AD authentication for {User} failed: {Err}", model.Username, adErr);
        }

        // 2. No local fallback. Passwords in the shared portal.User table use a
        //    different scheme (varbinary hash + salt + algo) that QMS never
        //    evaluates, and the BCrypt seed-admin back-door was removed on
        //    2026-05-13. Active Directory is the only way in.
        ModelState.AddModelError("", adCfg.IsConfigured
            ? "Invalid credentials."
            : "Active Directory is not configured, so sign-in is unavailable. Contact the administrator.");
        return View(model);
    }

    private async Task IssueCookieAsync(User user, bool rememberMe)
    {
        await _db.UpdateLastSeenAsync(user.UserId);
        await _db.SetUserOnlineAsync(user.UserId, true);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name,           user.Username),
            new(ClaimTypes.GivenName,      user.FullName),
            new(ClaimTypes.Email,          user.Email ?? ""),
            // The ROLE CODE, not the display name. Permissions hang off the code,
            // it survives a rename, and it is the only thing that means anything
            // for a role composed on the Security screen.
            new(ClaimTypes.Role,           user.RoleCode),
            new("RoleName",                user.RoleName ?? user.Role),
            new("Department",              user.Department ?? ""),
            new("EmployeeId",              user.EmployeeId ?? ""),
            new("ProfilePicture",          user.ProfilePicture ?? ""),
            // Plant scope now follows a flag on the ROLE rather than the literal
            // name "Operator". A composed operator-style role would otherwise
            // have been handed sight of every plant -- a silent widening that
            // nothing would have surfaced.
            new("PlantCode", user.IsPlantScoped ? (user.PlantCode ?? "") : "")
        };

        // Exactly one role claim. The extra claims this used to emit -- one per
        // Qc role held -- were never refreshed after login, so they went stale
        // for the life of the cookie. Access is resolved from the database by
        // user id now, so a second claim would buy nothing and could only
        // disagree with the real answer.

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc   = rememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(8)
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), props);
    }

    private IActionResult RedirectAfterLogin(LoginViewModel model)
    {
        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [QmsAlwaysAllowed]
    public async Task<IActionResult> Logout()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (int.TryParse(idClaim?.Value, out var userId))
            await _db.SetUserOnlineAsync(userId, false);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    [HttpGet, AllowAnonymous]
    public IActionResult AccessDenied() => View();

    // ---- View site as ----------------------------------------------------
    // Preview the application as another role, to verify a composed role before
    // assigning it to anybody. ViewAsClaimsTransformer does the claim swap on
    // each request; this only manages the cookie that drives it.
    //
    // [QmsAlwaysAllowed] and the unconditional CLEAR path below are what stop an
    // administrator trapping themselves: previewing a role that cannot reach
    // this endpoint would otherwise leave them stuck as that role for eight
    // hours. Starting a preview still requires the permission, checked against
    // the user's REAL role.
    [HttpPost, ValidateAntiForgeryToken]
    [QmsAlwaysAllowed]
    public IActionResult ViewAs(string? role, string? returnUrl = null)
    {
        var clearing = string.IsNullOrWhiteSpace(role);
        if (!clearing && !_perms.AsActualUser.Can(Perm.Admin.ViewAs)) return Forbid();

        if (clearing || string.Equals(role, _perms.ActualRole, StringComparison.OrdinalIgnoreCase))
        {
            Response.Cookies.Delete(ViewAsClaimsTransformer.CookieName);
        }
        else
        {
            // role is non-null here: `clearing` is exactly the null/blank case.
            Response.Cookies.Append(ViewAsClaimsTransformer.CookieName, role!, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure   = Request.IsHttps,
                MaxAge   = TimeSpan.FromHours(8)
            });
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Home");
    }

    // ---- My Profile ------------------------------------------------------

    [HttpGet, Authorize, QmsAlwaysAllowed]
    public async Task<IActionResult> Profile()
    {
        var user = await CurrentUserAsync();
        if (user == null) return Forbid();
        return View(ToVm(user));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize, QmsAlwaysAllowed]
    public async Task<IActionResult> Profile(ProfileVm vm)
    {
        var user = await CurrentUserAsync();
        if (user == null) return Forbid();
        if (!ModelState.IsValid)
        {
            vm.Username       = user.Username;
            vm.Role           = user.Role;
            vm.EmployeeId     = user.EmployeeId;
            vm.ProfilePicture = user.ProfilePicture;
            vm.IsAdUser       = string.IsNullOrEmpty(user.PasswordHash);
            return View(vm);
        }

        user.FullName   = vm.FullName.Trim();
        user.Email      = string.IsNullOrWhiteSpace(vm.Email) ? null : vm.Email.Trim();
        user.Department = string.IsNullOrWhiteSpace(vm.Department) ? null : vm.Department.Trim();
        await _db.UpdateUserAsync(user);

        // Re-issue the cookie so the claims (FullName, Email, Department)
        // refresh without forcing a sign-out.
        await IssueCookieAsync(user, rememberMe: true);
        TempData["Success"] = "Profile updated.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize, QmsAlwaysAllowed]
    public async Task<IActionResult> ChangePassword(ChangePasswordVm vm)
    {
        var user = await CurrentUserAsync();
        if (user == null) return Forbid();
        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            TempData["Error"] = "This is an Active Directory account. Change your password in AD.";
            return RedirectToAction(nameof(Profile));
        }
        if (!ModelState.IsValid)
        {
            TempData["Error"] = string.Join(" ",
                ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Profile));
        }
        if (!BCrypt.Net.BCrypt.Verify(vm.CurrentPassword, user.PasswordHash))
        {
            TempData["Error"] = "Current password is incorrect.";
            return RedirectToAction(nameof(Profile));
        }
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(vm.NewPassword);
        await _db.UpdateUserAsync(user);
        TempData["Success"] = "Password changed.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize, QmsAlwaysAllowed]
    public async Task<IActionResult> UploadProfilePicture(IFormFile avatar)
    {
        var user = await CurrentUserAsync();
        if (user == null) return Forbid();
        if (avatar == null || avatar.Length == 0)
        {
            TempData["Error"] = "Choose a picture file to upload.";
            return RedirectToAction(nameof(Profile));
        }
        var ext = Path.GetExtension(avatar.FileName).ToLowerInvariant();
        if (!AllowedAvatarExtensions.Contains(ext))
        {
            TempData["Error"] = "Avatar must be PNG, JPG, GIF, or WEBP.";
            return RedirectToAction(nameof(Profile));
        }
        if (avatar.Length > MaxAvatarBytes)
        {
            TempData["Error"] = "Avatar must be under 2 MB.";
            return RedirectToAction(nameof(Profile));
        }

        var dir = Path.Combine(_env.WebRootPath, "avatars");
        Directory.CreateDirectory(dir);
        // Delete prior avatar files for this user (extension may change).
        foreach (var prior in Directory.GetFiles(dir, $"{user.UserId}.*"))
            try { System.IO.File.Delete(prior); } catch { /* best-effort */ }

        var fileName = $"{user.UserId}{ext}";
        var fullPath = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(fullPath))
            await avatar.CopyToAsync(fs);

        var publicPath = $"/avatars/{fileName}";
        await _db.UpdateProfilePictureAsync(user.UserId, publicPath);
        user.ProfilePicture = publicPath;
        await IssueCookieAsync(user, rememberMe: true);
        TempData["Success"] = "Avatar updated.";
        return RedirectToAction(nameof(Profile));
    }

    private async Task<User?> CurrentUserAsync()
    {
        var idClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var id)) return null;
        return await _db.GetUserByIdAsync(id);
    }

    private static ProfileVm ToVm(User u) => new()
    {
        UserId         = u.UserId,
        Username       = u.Username,
        FullName       = u.FullName,
        Email          = u.Email,
        Department     = u.Department,
        EmployeeId     = u.EmployeeId,
        Role           = u.Role,
        ProfilePicture = u.ProfilePicture,
        IsAdUser       = string.IsNullOrEmpty(u.PasswordHash)
    };
}
