using System.Net;
using SharbatlyQMS.Web.Models;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Loads every significant page against the live Sharbatly_MIS database.
///
/// This is the check the migration most needed and that clicking around by hand
/// tends to miss: a page whose query still names a column that moved will throw
/// only when someone opens it. Rendering each view end-to-end exercises the
/// controller, the services, Dapper and the Razor view together.
///
/// Read-only -- these tests create nothing and delete nothing.
/// </summary>
public class PageSmokeTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public PageSmokeTests(QmsAppFactory factory) => _factory = factory;

    public static TheoryData<string> Pages => new()
    {
        "/",
        "/Home/Index",
        "/Arrivals",
        "/Arrivals/Pending",          // container picker, reads the SAP cache
        "/Arrivals/Search",
        "/QualityOrders",
        "/ClaimManagement",
        "/Audit",
        "/Account/Profile",
        "/Admin/Users",
        "/Admin/Settings",
        "/Admin/DefectCatalog",
        "/Admin/DefectCategories",
        "/Admin/ReadingTypes",
        "/Admin/ArrivalFields",
        "/Admin/SampleHeaders",
        "/Admin/MailTemplate",
        "/Reports/FlatDefects",
        "/Reports/Perspectives?report=flat_defects",
        "/Reports/PivotSchema?report=flat_defects",
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Page_loads_without_error(string url)
    {
        TestAuthHandler.Role = UserRoles.SiteAdmin;
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var res = await client.GetAsync(url);

        // 404 means the action does not exist (a bad url in this list, not a
        // broken app), so it is called out separately from a server error.
        Assert.False(res.StatusCode == HttpStatusCode.InternalServerError,
            $"{url} returned 500. Body:\n{await Body(res)}");
        Assert.True(
            res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Found or HttpStatusCode.Redirect,
            $"{url} returned {(int)res.StatusCode} {res.StatusCode}. Body:\n{await Body(res)}");
    }

    /// <summary>
    /// The catalogues carried over from the old database must actually reach
    /// the screens that consume them -- a page can return 200 while silently
    /// rendering an empty list if a lookup broke.
    /// </summary>
    [Fact]
    public async Task Defect_catalogue_page_shows_migrated_defects()
    {
        TestAuthHandler.Role = UserRoles.SiteAdmin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Admin/DefectCatalog");
        Assert.False(string.IsNullOrWhiteSpace(html));
        // 548 defect rows were carried over; the grid should not be empty.
        Assert.DoesNotContain("No defects", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_admin_lists_migrated_quality_users()
    {
        TestAuthHandler.Role = UserRoles.SiteAdmin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Admin/Users");
        // Identity now comes from portal.User via qms.AppUser.
        Assert.Contains("mohamed.tag", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An Operator must not reach the admin area.</summary>
    [Fact]
    public async Task Operator_is_denied_admin_pages()
    {
        TestAuthHandler.Role = UserRoles.Operator;
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var res = await client.GetAsync("/Admin/Settings");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    private static async Task<string> Body(HttpResponseMessage r)
    {
        var s = await r.Content.ReadAsStringAsync();
        return s.Length > 1200 ? s[..1200] : s;
    }
}
