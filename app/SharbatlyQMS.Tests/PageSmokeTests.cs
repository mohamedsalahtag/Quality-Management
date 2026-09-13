using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;
using SharbatlyQMS.Web.Models.Reports;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
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
///
/// In the "workflow" collection so it does NOT run in parallel with the other
/// classes: it sets TestAuthHandler.Role, which is process-wide, and a parallel
/// class issuing HTTP requests would otherwise be authenticated as whatever role
/// this class last wrote. That produced nothing worse than luck before the
/// permission model; now it would be intermittent 403s that look like a bug in
/// the feature.
/// </summary>
[Collection("workflow")]
public class PageSmokeTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public PageSmokeTests(QmsAppFactory factory) => _factory = factory;

    public static TheoryData<string> Pages => new()
    {
        "/",
        "/Home/Index",
        // The dashboard is one page in many slices: each period shape drives a
        // different bucket width and date window through every portlet query,
        // and the plant filter threads a second predicate through all of them.
        "/?period=today",
        "/?period=7d",
        "/?period=30d",
        "/?period=90d",
        "/?period=custom&from=2026-08-01&to=2026-08-31",
        // Every drill-through off the Received vs Inspected portlet. Each one
        // is a different predicate and a different join shape, so a broken
        // bucket shows up here rather than under somebody's mouse.
        "/Home/CommitmentDetail?bucket=received&period=30d",
        "/Home/CommitmentDetail?bucket=period&period=30d",
        "/Home/CommitmentDetail?bucket=pending&period=30d",
        "/Home/CommitmentDetail?bucket=backlog&period=30d",
        "/Home/CommitmentDetail?bucket=total&period=30d",
        "/Home/CommitmentDetail?bucket=nonsense&period=30d",
        "/Home/CommitmentExcel?period=7d",
        "/?period=30d&plant=1010",
        "/Arrivals",
        "/Arrivals/Pending",          // container picker, reads the SAP cache
        // The archive is the same page in its other mode: a different WHERE on
        // the archived flag, a different action column. Worth its own load —
        // the pending list passing proves nothing about it.
        "/Arrivals/Pending?archived=true",
        "/Arrivals/Pending?archived=true&from=2020-01-01&to=2030-01-01",
        "/Arrivals/Search",
        "/QualityOrders",
        "/ClaimManagement",
        // Claims filter panel: one URL per input shape (text LIKE, dropdown
        // equality, date range, status chip) so a typo in the new WHERE clause
        // fails here rather than on the Claims page.
        "/ClaimManagement?status=PassedQC&from=2026-07-01&to=2026-08-17",
        // Archived is its own tab (own action), not a claim status.
        "/ClaimManagement/Archived",
        "/ClaimManagement/Archived?from=2026-07-01&to=2026-08-17",
        "/ClaimManagement?container=A&po=4&supplier=&plant=&closedBy=&claimOwner=&material=apple",
        "/Audit",
        "/Audit/Security",
        // Audit filters: the noise toggle, an admin entity type (previously
        // unreachable from the UI at all) and the local-date range.
        "/Audit?showTechnical=true",
        "/Audit?entityTypes=Role&entityTypes=User&actionCodes=Updated",
        "/Audit?from=2026-08-01&to=2026-08-20",
        // Record scope: each of these drives the arrival -> QO -> sample EXISTS,
        // so a broken join surfaces as a 500 rather than as an empty page.
        "/Audit?qoNo=QO-2026",
        "/Audit?container=A",
        "/Audit?vendor=&plant=",
        "/Audit?qoNo=QO-2026-000353&showTechnical=true",
        // Users & security, unfiltered and through each filter path: the
        // permission-log-only branch, the audit-log-only branch, and search.
        "/Audit/Security?category=Permission",
        "/Audit/Security?category=UserCreated",
        "/Audit/Security?subjectType=User&search=a",
        "/Audit/Security?from=2026-01-01&to=2026-08-20",
        "/Account/Profile",
        "/Admin/Users",
        "/Admin/Settings",
        "/Security",
        // Time Bar: each URL drives a different branch of the one shared WHERE
        // clause, so a typo fails here rather than in front of a manager.
        "/TimeBar",
        "/TimeBar?stage=Pending",
        "/TimeBar?stage=QC&status=Closed",
        "/TimeBar?stage=Arrival",
        "/TimeBar?overOnly=true",
        "/TimeBar?noCacheOnly=true",
        "/TimeBar?includeArchived=true",
        "/TimeBar?minDays=5&supplier=a",
        "/TimeBar?from=2026-01-01&to=2026-12-31",
        "/TimeBar?search=MSC&plant=JD01&poType=",
        "/TimeBar?page=2&pageSize=50",
        // Admin -> Labels, unfiltered and through each filter path.
        "/Labels",
        "/Labels?changedOnly=true",
        "/Labels?screen=Arrivals&q=container",
        "/Labels?kind=Field%20label",
        "/Labels?kind=Column%20header&screen=Arrivals",
        "/Admin/DefectCatalog",
        "/Admin/DefectCategories",
        "/Admin/ReadingTypes",
        "/Admin/ArrivalFields",
        "/Admin/SampleHeaders",
        "/Admin/MailTemplate",
        "/Admin/Notifications",
        "/Reports/FlatDefects",
        "/Reports/Perspectives?report=flat_defects",
        "/Reports/PivotSchema?report=flat_defects",
        "/Reports/ReportBuilder",
        "/Reports/ReportBuilderPalette?materialGroup=APPLE",
        "/Reports/ReportBuilderVendors",
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Page_loads_without_error(string url)
    {
        TestAuthHandler.Role = RoleCodes.Admin;
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
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Admin/DefectCatalog");
        Assert.False(string.IsNullOrWhiteSpace(html));
        // 548 defect rows were carried over; the grid should not be empty.
        Assert.DoesNotContain("No defects", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_admin_lists_migrated_quality_users()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Admin/Users");
        // Identity now comes from portal.User via qms.AppUser.
        Assert.Contains("mohamed.tag", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Security editor must tag its read-only actions so the browser can keep
    /// them grantable on a read-only screen. This renders the real controller,
    /// view model and Razor view, so it catches the flag being dropped anywhere
    /// along that path -- the "view / export cannot be chosen" bug.
    /// </summary>
    [Fact]
    public async Task Security_editor_marks_read_only_actions()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        // Viewer is the read-only built-in, so the download PDF action is on show.
        var html = await client.GetStringAsync("/Security?role=" + RoleCodes.Viewer);

        Assert.Contains(Perm.Qo.Pdf, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-readonly", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Users tab in the role editor lists the people who hold the selected
    /// role. Renders the controller, the ListUsers query and the view together.
    /// </summary>
    [Fact]
    public async Task Security_users_tab_lists_role_members()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync("/Security?role=" + RoleCodes.Admin + "&tab=users");

        Assert.Contains("Users with the", html, StringComparison.OrdinalIgnoreCase);
        // The signed-in administrator holds QcAdmin, so they appear in the list.
        Assert.Contains(QmsAppFactory.AdminUser, html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The claim page (QO Details in claim context) renders the QC report's
    /// grouped Summary as a header. Drives a real closed QO that has samples, so
    /// the summary block is populated — this exercises BuildGroupSummariesAsync,
    /// the controller, and the new Razor section end-to-end.
    /// </summary>
    [Fact]
    public async Task Claim_page_renders_the_report_summary_header()
    {
        TestAuthHandler.Role = RoleCodes.Admin;

        long chosen = 0;
        using (var scope = _factory.Services.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
            var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var rows   = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            foreach (var r in rows.Take(25))
            {
                var samples = await qos.ListSamplesAsync(r.QualityOrderId);
                if (samples.Count > 0) { chosen = r.QualityOrderId; break; }
            }
        }
        if (chosen == 0) return;   // no closed QO with samples to test against

        var client = _factory.CreateClient();
        var html = await client.GetStringAsync($"/ClaimManagement/Details/{chosen}");

        // The Summary section we added only renders in claim context with data.
        Assert.Contains("qc-summary", html);
        Assert.Contains("Summary", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The QC report PDF still generates after adding the opener/branch header —
    /// exercises ReportsController.BuildDataAsync (incl. the new user + plant
    /// lookup) end to end for a real quality order.
    /// </summary>
    [Fact]
    public async Task Quality_report_pdf_generates_with_opener_header()
    {
        TestAuthHandler.Role = RoleCodes.Admin;

        long qoId;
        using (var scope = _factory.Services.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
            var rows   = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            qoId = rows.Select(r => r.QualityOrderId).FirstOrDefault();
        }
        if (qoId == 0) return;   // no quality orders to render

        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/Reports/QualityOrderPdf/{qoId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 1000, $"PDF unexpectedly small ({bytes.Length} bytes).");
        // Real PDFs start with the "%PDF" magic bytes.
        Assert.True(bytes.Length >= 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P'
                    && bytes[2] == (byte)'D' && bytes[3] == (byte)'F', "Response is not a PDF.");
    }

    /// <summary>
    /// The QO page's "Preview QC report" popup renders the report inline
    /// instead of downloading it. Same bytes as QualityOrderPdf, but the
    /// disposition has to stay "inline" — an "attachment" here would make the
    /// preview download a file, which is exactly what the popup exists to avoid.
    /// </summary>
    [Fact]
    public async Task Quality_report_pdf_preview_is_served_inline()
    {
        TestAuthHandler.Role = RoleCodes.Admin;

        long qoId;
        using (var scope = _factory.Services.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
            var rows   = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            qoId = rows.Select(r => r.QualityOrderId).FirstOrDefault();
        }
        if (qoId == 0) return;   // no quality orders to render

        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/Reports/QualityOrderPdfPreview/{qoId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", res.Content.Headers.ContentDisposition?.DispositionType);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length >= 4 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P'
                    && bytes[2] == (byte)'D' && bytes[3] == (byte)'F', "Response is not a PDF.");
    }

    /// <summary>
    /// /QualityOrders/SummaryPanel — the grouped summary the claim page and the
    /// report both print, rebuilt on demand. The QO page's popup now previews
    /// the whole report instead, so nothing in the UI calls this any more; the
    /// endpoint and its partials are kept for the claim-side rollup, and this
    /// test keeps them honest.
    /// </summary>
    [Fact]
    public async Task Quality_order_summary_panel_returns_grouped_summary()
    {
        TestAuthHandler.Role = RoleCodes.Admin;

        long chosen = 0;
        using (var scope = _factory.Services.CreateScope())
        {
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
            var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var rows   = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            foreach (var r in rows.Take(25))
            {
                var samples = await qos.ListSamplesAsync(r.QualityOrderId);
                if (samples.Count > 0) { chosen = r.QualityOrderId; break; }
            }
        }
        if (chosen == 0) return;   // no QO with samples to test against

        var client = _factory.CreateClient();
        var html = await client.GetStringAsync($"/QualityOrders/SummaryPanel/{chosen}");

        // The grouped summary fields (qcs-f) render for a QO that has samples.
        Assert.Contains("qcs-f", html);
    }

    /// <summary>
    /// The Arrivals list now has the same filter surface as Quality Orders: a
    /// status chip bar and a collapsible "Filters" panel. This exercises the
    /// new ArrivalListFilter binding, the filtered query, and the options load.
    /// </summary>
    [Fact]
    public async Task Arrivals_page_renders_status_chips_and_filter_panel()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var html = await client.GetStringAsync(
            "/Arrivals?status=Completed&container=C&from=2020-01-01&to=2030-01-01");

        Assert.Contains("data-bs-target=\"#arrFilters\"", html); // the panel toggle
        Assert.Contains("arrFilters", html);                     // the collapsible panel id
    }

    /// <summary>
    /// Report Builder / data-hub identifier fields (Grower, Pallet No, Date Code,
    /// Lot No, Label) are captured as reading types in this deployment, not the
    /// legacy qms_sample columns. The flat pipeline must resolve them from the
    /// reading/header bags so the column a user picks actually shows their value.
    /// Verified against container MSGU9205337 (grower entered as a reading).
    /// </summary>
    [Fact]
    public async Task Flat_rows_resolve_grower_from_readings_not_the_empty_column()
    {
        using var scope = _factory.Services.CreateScope();
        var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

        var filter = new FlatDefectFilter { ContainerNo = "MSGU9205337" };
        var rows = 0;
        var growerResolved = false;
        await foreach (var row in qos.StreamFlatDefectRowsAsync(filter, CancellationToken.None))
        {
            rows++;
            if (!string.IsNullOrWhiteSpace(row.Grower)) { growerResolved = true; break; }
        }

        if (rows == 0) return;   // container purged from the DB — nothing to assert
        Assert.True(growerResolved,
            "Grower did not resolve from the reading bag for a container that has grower readings.");
    }

    /// <summary>An Operator must not reach the admin area.</summary>
    [Fact]
    public async Task Operator_is_denied_admin_pages()
    {
        TestAuthHandler.Role = RoleCodes.Operator;
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
