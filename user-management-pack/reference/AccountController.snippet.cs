// AccountController - login, logout, setup, profile picture upload.
// Reference: ASP.NET Core 9 cookie auth + AD bind + BCrypt local fallback.
//
// Hard rules from SPEC.md §6:
//   - No self-registration UI; never link to /Account/Register from the login page.
//   - Login uses AD bind-only (no LDAP search). Fall back to BCrypt local hash.
//   - Auto-create-on-login is gated by SiteConfiguration.AutoCreateAdUsers.
//   - The seeded admin self-corrects its placeholder hash on first real login.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HelpDesk.Models;
using HelpDesk.Services;

namespace HelpDesk.Controllers;

public class AccountController : Controller
{
    private readonly IDbService _db;
    private readonly IAdService _ad;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AccountController> _logger;

    public AccountController(IDbService db, IAdService ad,
        IWebHostEnvironment env, ILogger<AccountController> logger)
    {
        _db = db; _ad = ad; _env = env; _logger = logger;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    // -------------------------------------------------------------------
    // POST /Account/Login - the algorithm in SPEC.md §2.1.
    // -------------------------------------------------------------------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await _db.GetUserByUsernameAsync(model.Username);

        // Auto-create unknown AD user (gated by SiteAdmin toggle)
        if (user == null)
        {
            var autoCreate = (await _db.GetConfigAsync("AutoCreateAdUsers") ?? "false") == "true";
            var adDomain   = await _db.GetConfigAsync("AdDomain") ?? "";
            var adLdapPath = await _db.GetConfigAsync("AdLdapPath") ?? "";
            var adReady    = !string.IsNullOrWhiteSpace(adDomain) && !string.IsNullOrWhiteSpace(adLdapPath);

            if (autoCreate && adReady)
            {
                var adUser = await _ad.AuthenticateAsync(model.Username, model.Password);
                if (adUser != null)
                {
                    var sam = model.Username.Contains('@')
                        ? model.Username.Split('@')[0] : model.Username;
                    var newUser = new User
                    {
                        EmployeeId   = "",
                        AdUsername   = sam,
                        FullName     = string.IsNullOrWhiteSpace(adUser.FullName) ? sam : adUser.FullName,
                        Email        = adUser.Email ?? "",
                        Department   = adUser.Department ?? "",
                        PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
                        Role         = UserRoles.Requester,
                        IsActive     = true
                    };
                    var newId = await _db.CreateUserAsync(newUser);
                    newUser.UserId = newId;
                    user = newUser;
                    _logger.LogInformation("Auto-created AD user '{Sam}' as Requester (UserId={Id})", sam, newId);
                }
            }

            if (user == null)
            {
                ModelState.AddModelError("", "Invalid credentials or account not registered.");
                return View(model);
            }
        }

        if (!user.IsActive)
        {
            ModelState.AddModelError("", "Your account has been disabled. Contact the IT admin.");
            return View(model);
        }

        // Verify password - AD first, then BCrypt local hash.
        bool authenticated = false;
        if (_ad.IsConfigured)
        {
            var adUser = await _ad.AuthenticateAsync(model.Username, model.Password);
            authenticated = adUser != null;
        }
        if (!authenticated)
            authenticated = BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash);

        // Bootstrap admin path: re-hash if matching the seed hash.
        if (!authenticated && user.AdUsername == "admin")
        {
            var knownPlaceholders = new[]
            {
                "$2a$11$rBV2JDeWW3.vKyeXqmcLHuYHnmJiEoOaQx6M9ELvGKxwFz8t7Garm",
                "$2a$11$92IXUNpkjO0rOQ5byMi.Ye4oKoEa3Ro9llC/.og/at2uheWG/igi.",
                "$2a$11$K7qFPFGmFGKPFcXcYxBnuuBQyKqpzG3SX3t3JHu2OG5EfFjSqFWsS",
                "$2a$11$K/YL1mZMBJaEBFwP1OQnCOkMmLLqJOwv8QqKAynaqxaFGgOGVJBSa"
            };
            if (Array.Exists(knownPlaceholders, h => h == user.PasswordHash))
            {
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password);
                await _db.UpdateUserAsync(user);
                authenticated = true;
            }
        }

        if (!authenticated)
        {
            ModelState.AddModelError("", "Invalid credentials.");
            return View(model);
        }

        // Update last-login + online flag
        user.LastLogin = DateTime.UtcNow;
        await _db.UpdateLastSeenAsync(user.UserId);
        await _db.SetUserOnlineAsync(user.UserId, true);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name,           user.AdUsername),
            new(ClaimTypes.GivenName,      user.FullName),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Role,           user.Role),
            new("Department",              user.Department ?? ""),
            new("EmployeeId",              user.EmployeeId)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = model.RememberMe,
            ExpiresUtc   = model.RememberMe
                ? DateTimeOffset.UtcNow.AddDays(30)
                : DateTimeOffset.UtcNow.AddHours(8)
        };

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity), props);

        if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
            return Redirect(model.ReturnUrl);
        return RedirectToAction("Index", "Home");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var userId = GetUserId();
        if (userId > 0) await _db.SetUserOnlineAsync(userId, false);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }

    // POST /Account/ViewAs (role-impersonation) -- extracted to its own pack.
    // See ../../view-as-pack/reference/AccountController.ViewAs.snippet.cs

    // -------------------------------------------------------------------
    // /Account/Setup - one-time bootstrap. Remove or env-gate in production.
    // -------------------------------------------------------------------
    [HttpGet]
    public async Task<IActionResult> Setup()
    {
        const string adminUser = "admin";
        const string tempPassword = "Admin@123";

        var user = await _db.GetUserByUsernameAsync(adminUser);
        if (user == null)
        {
            var newUser = new User
            {
                EmployeeId   = "EMP0000",
                AdUsername   = adminUser,
                FullName     = "System Administrator",
                Email        = "admin@company.com",
                Department   = "IT Department",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword),
                Role         = UserRoles.SiteAdmin,
                IsActive     = true
            };
            await _db.CreateUserAsync(newUser);
            return Content($"Admin created. Login with: {adminUser} / {tempPassword}");
        }
        else
        {
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(tempPassword);
            user.IsActive     = true;
            await _db.UpdateUserAsync(user);
            return Content($"Admin password reset. Login with: {adminUser} / {tempPassword}");
        }
    }

    // -------------------------------------------------------------------
    // Profile picture upload (multipart). SPEC.md §5.
    // -------------------------------------------------------------------
    [HttpPost, Authorize]
    public async Task<IActionResult> UploadProfilePicture(IFormFile picture)
    {
        var userId = GetUserId();
        if (userId == 0) return Json(new { ok = false, error = "Not authenticated" });
        if (picture == null || picture.Length == 0)
            return Json(new { ok = false, error = "No file selected" });

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext = Path.GetExtension(picture.FileName).ToLower();
        if (!allowed.Contains(ext))
            return Json(new { ok = false, error = "Only JPG, PNG, GIF or WebP allowed" });
        if (picture.Length > 2 * 1024 * 1024)
            return Json(new { ok = false, error = "Image must be under 2MB" });

        var avatarDir = Path.Combine(_env.WebRootPath, "avatars");
        Directory.CreateDirectory(avatarDir);
        // Delete any prior avatar for this user (handles ext changes)
        foreach (var old in Directory.GetFiles(avatarDir, $"{userId}.*"))
            System.IO.File.Delete(old);

        var fileName = $"{userId}{ext}";
        var filePath = Path.Combine(avatarDir, fileName);
        using (var fs = System.IO.File.Create(filePath))
            await picture.CopyToAsync(fs);

        var webPath = $"/avatars/{fileName}?v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await _db.UpdateProfilePictureAsync(userId, $"/avatars/{fileName}");
        return Json(new { ok = true, path = webPath });
    }

    [HttpPost, Authorize]
    public async Task<IActionResult> RemoveProfilePicture()
    {
        var userId = GetUserId();
        var avatarDir = Path.Combine(_env.WebRootPath, "avatars");
        if (Directory.Exists(avatarDir))
            foreach (var old in Directory.GetFiles(avatarDir, $"{userId}.*"))
                System.IO.File.Delete(old);
        await _db.UpdateProfilePictureAsync(userId, "");
        return Json(new { ok = true });
    }

    private int GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return int.TryParse(claim?.Value, out var id) ? id : 0;
    }
}

public class LoginViewModel
{
    public string Username   { get; set; } = "";
    public string Password   { get; set; } = "";
    public bool   RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
