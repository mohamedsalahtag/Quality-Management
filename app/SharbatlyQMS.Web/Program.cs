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
    // Audit-trail: capture request IP + user-agent into IAuditContext
    // before each MVC action runs. Background hosted services never go
    // through this filter, which matches FR-001 (user-initiated only).
    opt.Filters.AddService<AuditContextActionFilter>();
});

builder.Services.AddScoped<IDbService, DbService>();
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IArrivalService, ArrivalService>();
builder.Services.AddScoped<IQualityOrderService, QualityOrderService>();
builder.Services.AddScoped<IClaimService, ClaimService>();
builder.Services.AddScoped<IAuditContext, AuditContext>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<AuditContextActionFilter>();
builder.Services.AddScoped<IImageService, ImageService>();
builder.Services.AddScoped<IMaraService, MaraService>();
builder.Services.AddScoped<IVendorService, VendorService>();
builder.Services.AddScoped<ICatalogCache, CatalogCache>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
// V34 (2026-06-20): Perspective Analyzer (server-side pivot + saved configs).
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Reports.IPivotService,
                           SharbatlyQMS.Web.Services.Reports.PivotService>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Reports.IPerspectiveService,
                           SharbatlyQMS.Web.Services.Reports.PerspectiveService>();
#pragma warning disable CA1416 // AdService uses System.DirectoryServices (Windows-only); host runs on Windows.
builder.Services.AddScoped<IAdService, AdService>();
#pragma warning restore CA1416
builder.Services.AddSingleton<SharbatlyQMS.Web.Services.Sap.StubSapClient>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapClient,
                           SharbatlyQMS.Web.Services.Sap.HybridSapClient>();
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapODataClient,
                           SharbatlyQMS.Web.Services.Sap.SapODataClient>();
// L3: a single named HttpClient for every SAP OData call. The factory
// pools the underlying SocketsHttpHandler, which the previous "new
// HttpClient(new HttpClientHandler())" idiom did not. H5: cert validation
// is wired here so SapODataClient no longer needs IWebHostEnvironment
// (and prod defaults to the secure path).
builder.Services.AddHttpClient(SharbatlyQMS.Web.Services.Sap.SapODataClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
})
.ConfigurePrimaryHttpMessageHandler(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var handler = new HttpClientHandler();
    if (env.IsDevelopment())
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
    }
    return handler;
});
builder.Services.AddScoped<SharbatlyQMS.Web.Services.Sap.ISapSyncService,
                           SharbatlyQMS.Web.Services.Sap.SapSyncService>();
builder.Services.AddScoped<IContainerCacheService, ContainerCacheService>();
builder.Services.AddHostedService<AutoSyncService>();
builder.Services.AddHostedService<AdCachePrimingService>();
builder.Services.AddHostedService<ContainerPollingService>();
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
        // SameAsRequest: the Secure flag is set automatically when the request
        // is HTTPS. In production behind HTTPS the cookie is Secure; behind an
        // HTTP-only dev/test host it still works. Avoids the previous .None
        // setting that allowed the cookie to be sniffed even on HTTPS hosts.
        opt.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        opt.Cookie.MaxAge       = TimeSpan.FromDays(30);
        opt.Cookie.HttpOnly     = true;
    });

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy(AuthPolicies.AdminOnly,
        p => p.RequireRole(UserRoles.SiteAdmin));
    opt.AddPolicy(AuthPolicies.ManagerOrAdmin,
        p => p.RequireRole(UserRoles.Manager, UserRoles.SiteAdmin));
    // Supervisor (2026-06-20): reviews operator work. Can Finish a Submitted QO,
    // cancel-submit it back to Open, edit Completed arrivals, view the flat
    // data-hub report. Cannot reopen Finished QOs (Manager-only).
    opt.AddPolicy(AuthPolicies.SupervisorOrAbove,
        p => p.RequireRole(UserRoles.Supervisor, UserRoles.Manager, UserRoles.SiteAdmin));
    opt.AddPolicy(AuthPolicies.OperatorOrAbove,
        p => p.RequireRole(UserRoles.Operator, UserRoles.Supervisor, UserRoles.Manager, UserRoles.SiteAdmin));
    // Claim Manager (commercial approver) or SiteAdmin -- gates the Approve/Hold
    // actions in Claim Management. SiteAdmin still acts as both roles.
    opt.AddPolicy(AuthPolicies.ClaimManagerOrAdmin,
        p => p.RequireRole(UserRoles.ClaimManager, UserRoles.SiteAdmin));
    // Audit-trail (2026-05-21): the global /Audit page + Excel export use
    // AdminOnly (above). The per-record audit panel uses ManagerOrAdmin.
    // The dedicated AuditViewer / AuditorOrAdmin policies were removed when
    // the Auditor role was retired (V16 migration).
});

builder.Services.AddAntiforgery(opt =>
{
    opt.Cookie.SameSite     = SameSiteMode.Lax;
    // Match the auth cookie: HTTPS host -> Secure; HTTP host -> no Secure.
    opt.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
