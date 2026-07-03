using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
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
    private readonly ILogger<AccountController> _logger;

    public AccountController(IDbService db, IAdService ad, ISettingsService settings,
        IWebHostEnvironment env, ILogger<AccountController> logger)
    {
        _db = db;
        _ad = ad;
        _settings = settings;
        _env = env;
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
                    if (!adCfg.AutoCreateOnLogin)
                    {
                        ModelState.AddModelError("",
                            "Your AD account is recognised but no local profile exists. Ask an administrator to create one.");
                        return View(model);
                    }
                    user = new User
                    {
                        Username     = canonical,
                        FullName     = string.IsNullOrWhiteSpace(adInfo.FullName) ? canonical : adInfo.FullName,
                        Email        = adInfo.Email,
                        Department   = adInfo.Department,
                        PasswordHash = "",                    // AD-managed; no local password
                        Role         = UserRoles.Viewer,      // least privilege; admin promotes
                        IsActive     = true
                    };
                    user.UserId = await _db.CreateUserAsync(user);
                    _logger.LogInformation("Auto-created local profile for AD user {User}", canonical);
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
            // failure reasons to the client. Fall through to local check so
            // the bootstrap admin (BCrypt) always retains a recovery path.
            _logger.LogDebug("AD authentication for {User} failed: {Err}", model.Username, adErr);
        }

        // 2. Local BCrypt fallback. The seed admin and any pre-AD users use
        //    this path. Normalize for the same reason.
        var local = await _db.GetUserByUsernameAsync(SamOnly(model.Username));
        if (local == null)
        {
            ModelState.AddModelError("", "Invalid credentials or account not registered.");
            return View(model);
        }
        if (!local.IsActive)
        {
            ModelState.AddModelError("", "Your account has been disabled. Contact the administrator.");
            return View(model);
        }
        // AD-only accounts have an empty PasswordHash -- skip the BCrypt
        // verify call so it doesn't throw on empty input.
        bool authenticated = !string.IsNullOrEmpty(local.PasswordHash)
            && BCrypt.Net.BCrypt.Verify(model.Password, local.PasswordHash);

        if (!authenticated)
        {
            ModelState.AddModelError("", "Invalid credentials.");
            return View(model);
        }

        await IssueCookieAsync(local, model.RememberMe);
        _logger.LogInformation("User {User} signed in locally", local.Username);
        return RedirectAfterLogin(model);
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
            new(ClaimTypes.Role,           user.Role),
            new("Department",              user.Department ?? ""),
            new("EmployeeId",              user.EmployeeId ?? ""),
            new("ProfilePicture",          user.ProfilePicture ?? ""),
            // Plant scope: only Operators are restricted. Manager / SiteAdmin /
            // Viewer / ClaimManager always see every plant, so they get an
            // empty PlantCode claim regardless of any DB assignment.
            new("PlantCode",
                string.Equals(user.Role, UserRoles.Operator, StringComparison.OrdinalIgnoreCase)
                    ? (user.PlantCode ?? "")
                    : "")
        };

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
    // Lets a real SiteAdmin impersonate another role to verify security gates.
    // The Role-claim swap is done by ViewAsClaimsTransformer on each request;
    // here we only manage the cookie that drives it. Passing role="" (or any
    // non-role value) clears impersonation and the admin reverts to self.
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult ViewAs(string? role, string? returnUrl = null)
    {
        // Trust the OriginalRole claim when present (we are mid-impersonation),
        // otherwise the current Role claim is the real one.
        var realRole = User.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim)?.Value
                    ?? User.FindFirst(ClaimTypes.Role)?.Value;
        if (realRole != UserRoles.SiteAdmin) return Forbid();

        if (string.IsNullOrWhiteSpace(role) || role == UserRoles.SiteAdmin || !UserRoles.IsValid(role))
        {
            Response.Cookies.Delete(ViewAsClaimsTransformer.CookieName);
        }
        else
        {
            Response.Cookies.Append(ViewAsClaimsTransformer.CookieName, role, new CookieOptions
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

    [HttpGet, Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await CurrentUserAsync();
        if (user == null) return Forbid();
        return View(ToVm(user));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize]
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

    [HttpPost, ValidateAntiForgeryToken, Authorize]
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

    [HttpPost, ValidateAntiForgeryToken, Authorize]
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
