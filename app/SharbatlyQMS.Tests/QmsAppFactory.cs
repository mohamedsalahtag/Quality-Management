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
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Boots the real application in-process against the real Sharbatly_MIS
/// database, with two substitutions:
///
///   * Authentication is stubbed. QMS signs users in by binding to Active
///     Directory, which a test cannot do, so a fake handler issues the same
///     claims AccountController would. Everything downstream of the bind --
///     routing, authorisation policies, controllers, services, Dapper, the
///     views -- is the genuine article.
///   * Background services are removed, so booting the host does not kick off
///     a SAP pull or an AD cache prime.
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

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public new const string Scheme = "TestAuth";

    /// <summary>Role the next request runs as; mirrors UserRoles values.</summary>
    public static string Role { get; set; } = UserRoles.SiteAdmin;

    public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Same claim set AccountController.IssueCookieAsync builds.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, QmsAppFactory.AdminUserId.ToString()),
            new Claim(ClaimTypes.Name,           QmsAppFactory.AdminUser),
            new Claim(ClaimTypes.GivenName,      "Mohamed Tag"),
            new Claim(ClaimTypes.Email,          ""),
            new Claim(ClaimTypes.Role,           Role),
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
