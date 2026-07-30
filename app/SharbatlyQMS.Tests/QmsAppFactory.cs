using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Boots the real application in-process against the real Sharbatly_MIS
/// database, with two substitutions:
///
///   * Authentication is stubbed. QMS signs users in by binding to Active
///     Directory, which a test cannot do, so a fake handler issues the same
///     claims AccountController would. Everything downstream of the bind --
///     routing, authorisation, controllers, services, Dapper, the views -- is
///     the genuine article, including the permission catalogue and resolver.
///   * Background services are removed, so booting the host does not kick off
///     a SAP pull or an AD cache prime.
///
/// Note what is deliberately NOT removed: the permission catalogue is primed by
/// an IStartupFilter rather than an IHostedService, precisely so it survives the
/// strip below. A catalogue primed by a hosted service would be empty in every
/// test, which would make the whole suite pass while proving nothing.
///
/// Environment is forced to Production so appsettings.Production.json supplies
/// the Sharbatly_MIS connection string, i.e. the tests exercise exactly what
/// the live service talks to.
/// </summary>
public class QmsAppFactory : WebApplicationFactory<Program>
{
    /// <summary>A real portal.User id holding QcAdmin, so user lookups resolve.</summary>
    public const int AdminUserId   = 85;
    public const string AdminUser  = "mohamed.tag";

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Drop the hosted services (SAP sync, container polling, AD cache
            // priming) -- they would fire real outbound calls during a test run.
            foreach (var d in services.Where(s => s.ServiceType == typeof(IHostedService)).ToList())
                services.Remove(d);

            services.AddAuthentication(o =>
            {
                o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                o.DefaultChallengeScheme    = TestAuthHandler.Scheme;
                o.DefaultScheme             = TestAuthHandler.Scheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        });
    }
}

/// <summary>
/// Signs every request in as a chosen role.
///
/// The role is taken from the <c>X-Test-Role</c> request header when present,
/// falling back to the static <see cref="Role"/>. The header is what a
/// permission test should use: the static is shared process-wide, so two test
/// classes running in parallel would otherwise authenticate each other's
/// requests as the wrong role and produce intermittent, baffling 403s.
///
/// The USER ID also follows the role, because permissions resolve from the user
/// id rather than the role claim -- signing in as user 85 while claiming to be
/// an Operator would simply resolve back to that user's real role.
/// </summary>
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public new const string Scheme = "TestAuth";

    /// <summary>Header that overrides the role for a single request.</summary>
    public const string RoleHeader = "X-Test-Role";

    /// <summary>Default role when no header is supplied. A portal.Role CODE
    /// (QcAdmin, QcOperator, ...), not a legacy display name.</summary>
    public static string Role { get; set; } = RoleCodes.Admin;

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var role = Request.Headers.TryGetValue(RoleHeader, out var h) && !string.IsNullOrWhiteSpace(h)
            ? h.ToString()
            : Role;

        // Only the genuine administrator maps to the real portal user; every
        // other role runs as an id that does not exist in qms.AppUser, so the
        // resolver falls back to the role claim and the request really is
        // evaluated as that role.
        var userId = string.Equals(role, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase)
            ? QmsAppFactory.AdminUserId
            : 0;

        // Same claim set AccountController.IssueCookieAsync builds.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name,           QmsAppFactory.AdminUser),
            new Claim(ClaimTypes.GivenName,      "Mohamed Tag"),
            new Claim(ClaimTypes.Email,          ""),
            new Claim(ClaimTypes.Role,           role),
            new Claim("RoleName",     role),
            new Claim("Department",   ""),
            new Claim("EmployeeId",   ""),
            new Claim("ProfilePicture", ""),
            new Claim("PlantCode",    ""),
        };
        var identity  = new ClaimsIdentity(claims, Scheme);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}
