using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Run cleanly when launched as a Windows Service (no console attached).
// No-op when run via `dotnet run` so dev workflow is unchanged.
builder.Host.UseWindowsService(opt => opt.ServiceName = "SharbatlyQMS");

// MVC + global authorization (everything requires login by default; opt out with [AllowAnonymous]).
builder.Services.AddControllersWithViews(opt =>
{
    var policy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    opt.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter(policy));
});

builder.Services.AddScoped<IDbService, DbService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IArrivalService, ArrivalService>();
builder.Services.AddScoped<IQualityOrderService, QualityOrderService>();
builder.Services.AddScoped<IClaimService, ClaimService>();
builder.Services.AddScoped<IImageService, ImageService>();
builder.Services.AddScoped<IMaraService, MaraService>();
builder.Services.AddScoped<IVendorService, VendorService>();
builder.Services.AddScoped<ICatalogCache, CatalogCache>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
#pragma warning disable CA1416 // AdService uses System.DirectoryServices (Windows-only); host runs on Windows.
builder.Services.AddScoped<IAdService, AdService>();
#pragma warning restore CA1416
builder.Services.AddSingleton<SharbatlyQMS.Web.Services.Sap.StubSapClient>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapClient,
                           SharbatlyQMS.Web.Services.Sap.HybridSapClient>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapODataClient,
                           SharbatlyQMS.Web.Services.Sap.SapODataClient>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapSyncService,
                           SharbatlyQMS.Web.Services.Sap.SapSyncService>();
builder.Services.AddHostedService<AutoSyncService>();
builder.Services.AddHostedService<AdCachePrimingService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IClaimsTransformation, ViewAsClaimsTransformer>();
// Persist data-protection keys to disk so cookies + antiforgery tokens
// survive app restarts. Default storage is ephemeral, which invalidates
// every open session on each rebuild -- the source of "HTTP 400" on the
// first POST after a deploy.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")))
    .SetApplicationName("SharbatlyQMS");

// Cookie auth (30-day sliding) -- adapted from user-management-pack
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opt =>
    {
        opt.LoginPath           = "/Account/Login";
        opt.LogoutPath          = "/Account/Logout";
        opt.AccessDeniedPath    = "/Account/AccessDenied";
        opt.ExpireTimeSpan      = TimeSpan.FromDays(30);
        opt.SlidingExpiration   = true;
        opt.Cookie.Name         = "SharbatlyQMS.Auth";
        opt.Cookie.SameSite     = SameSiteMode.Lax;
        opt.Cookie.SecurePolicy = CookieSecurePolicy.None;
        opt.Cookie.MaxAge       = TimeSpan.FromDays(30);
        opt.Cookie.HttpOnly     = true;
    });

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy(AuthPolicies.AdminOnly,
        p => p.RequireRole(UserRoles.SiteAdmin));
    opt.AddPolicy(AuthPolicies.ManagerOrAdmin,
        p => p.RequireRole(UserRoles.Manager, UserRoles.SiteAdmin));
    opt.AddPolicy(AuthPolicies.OperatorOrAbove,
        p => p.RequireRole(UserRoles.Operator, UserRoles.Manager, UserRoles.SiteAdmin));
    // Claim Manager (commercial approver) or SiteAdmin -- gates the Approve/Hold
    // actions in Claim Management. SiteAdmin still acts as both roles.
    opt.AddPolicy(AuthPolicies.ClaimManagerOrAdmin,
        p => p.RequireRole(UserRoles.ClaimManager, UserRoles.SiteAdmin));
});

builder.Services.AddAntiforgery(opt =>
{
    opt.Cookie.SameSite     = SameSiteMode.Lax;
    opt.Cookie.SecurePolicy = CookieSecurePolicy.None;
    opt.HeaderName          = "X-CSRF-TOKEN";
});

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
