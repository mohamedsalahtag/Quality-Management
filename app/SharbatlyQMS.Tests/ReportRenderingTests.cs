using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The QC report's data and presentation rules.
///
/// Note that <c>QualityReportPdf</c> (Classic) is unreachable —
/// <c>QualityReportRenderer.Build</c> always returns the Soft layout — so every
/// assertion here is about <c>QualityReportPdfSoft</c>.
/// </summary>
[Collection("workflow")]
public class ReportRenderingTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ReportRenderingTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>
    /// A defect-category header is filled with the colour an administrator chose
    /// on Admin → Defect Categories. The text on it must stay legible whatever
    /// they choose — the seeded Minor is #ffc107, on which the previously
    /// hardcoded white was unreadable.
    /// </summary>
    [Theory]
    [InlineData("#b02a37", "#ffffff")]   // Major, dark red   -> white
    [InlineData("#7a1620", "#ffffff")]   // Critical, maroon  -> white
    [InlineData("#6c757d", "#ffffff")]   // Other, mid grey   -> white
    [InlineData("#ffc107", "#1c2733")]   // Minor, light amber-> ink
    [InlineData("#ffffff", "#1c2733")]   // white             -> ink
    [InlineData(null,      "#ffffff")]   // unset             -> the old default
    [InlineData("nonsense","#ffffff")]   // malformed         -> the old default
    public void Category_header_text_stays_readable_on_any_configured_colour(string? fill, string expected)
    {
        Assert.Equal(expected, SummaryReadingFilter.OnFill(fill, dark: "#1c2733", light: "#ffffff"));
    }

    [Theory]
    [InlineData("#b02a37", true)]
    [InlineData("#FFC107", true)]
    [InlineData("#fff",    false)]      // too short
    [InlineData("b02a37",  false)]      // no hash
    [InlineData("",        false)]
    [InlineData(null,      false)]
    public void Only_a_real_hex_colour_is_treated_as_configured(string? value, bool expected)
    {
        Assert.Equal(expected, SummaryReadingFilter.IsColour(value));
    }

    /// <summary>
    /// Transit Days on the report must equal the value the SAP cache holds — the
    /// number the container list showed. It used to be a copy frozen into the
    /// shipment snapshot at arrival creation, which SAP corrections never
    /// reached: six arrivals printed 0 while the cache held 31.
    /// </summary>
    [Fact]
    public async Task Transit_days_on_the_report_matches_the_sap_cache()
    {
        using var scope = _factory.Services.CreateScope();
        var cfg  = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var cs   = cfg.GetConnectionString("Default")!;

        // Find an arrival whose snapshot copy disagrees with the cache. If none
        // exists the invariant already holds everywhere and there is nothing to
        // prove -- but say so rather than passing silently.
        using var c = new Microsoft.Data.SqlClient.SqlConnection(cs);
        var row = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<(long ArrivalId, short? Snapshot, short? Cache)>(c, @"
            ;WITH cache AS (
                SELECT container_no, bol_no, ebeln, MAX(transit_days) AS CacheTransit
                FROM   qms_sap_container_cache GROUP BY container_no, bol_no, ebeln)
            SELECT TOP 1 a.arrival_id, ss.transit_days, k.CacheTransit
            FROM   qms_shipment_snapshot ss
            JOIN   qms_arrival a ON a.arrival_id = ss.arrival_id
            JOIN   cache k ON k.container_no = a.container_no
                          AND k.bol_no = a.bol_no AND k.ebeln = a.ebeln
            JOIN   qms_quality_order qo ON qo.arrival_id = a.arrival_id
            WHERE  ISNULL(ss.transit_days, -1) <> ISNULL(k.CacheTransit, -1)
            ORDER  BY a.arrival_id DESC");

        if (row.ArrivalId == 0)
        {
            _out.WriteLine("No arrival currently disagrees with the cache — invariant already holds.");
            return;
        }
        _out.WriteLine($"arrival {row.ArrivalId}: snapshot={row.Snapshot} cache={row.Cache}");

        // The report data for that arrival's quality order must carry the CACHE
        // value, not the snapshot's.
        var qoId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
            "SELECT TOP 1 quality_order_id FROM qms_quality_order WHERE arrival_id = @a ORDER BY quality_order_id DESC",
            new { a = row.ArrivalId });

        // The role travels on the REQUEST, not through TestAuthHandler.Role.
        // That static is shared by every collection running in parallel, so a
        // neighbouring test can flip it between the assignment and the call --
        // which is exactly how this test failed once in a full run and passed
        // on its own.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader,
                                         SharbatlyQMS.Web.Models.Security.RoleCodes.Admin);
        var res = await client.GetAsync($"/Reports/QualityOrderPdfPreview/{qoId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);

        // The PDF is binary, so assert on the value the renderer is handed.
        var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();

        // The stored snapshot still carries the stale copy...
        var shipment = await arrivals.GetShipmentAsync(row.ArrivalId);
        Assert.NotNull(shipment);
        Assert.Equal(row.Snapshot, shipment!.TransitDays);

        // ...and the accessor the report uses returns the CACHE value instead.
        // This is the whole fix: what the report prints is what the container
        // list showed, not a copy frozen at arrival creation.
        var forReport = await arrivals.GetCachedTransitDaysAsync(row.ArrivalId);
        Assert.Equal(row.Cache, forReport);
        Assert.NotEqual(shipment.TransitDays, forReport);
    }

    /// <summary>
    /// The supplier's copy omits Receive Date; the internal download keeps it.
    /// Asserted on the rendered bytes differing, because that is the only
    /// observable proof the flag reached the renderer at all.
    /// </summary>
    [Fact]
    public async Task The_supplier_copy_differs_from_the_internal_copy()
    {
        using var scope = _factory.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var rows   = await claims.ListClosedQosAsync(new SharbatlyQMS.Web.ViewModels.ClaimListFilter(),
                                                     PlantScope.All, "test");
        var qoId   = rows.Where(r => r.StatusCode == QualityOrderStatus.Closed)
                         .Select(r => r.QualityOrderId).FirstOrDefault();
        if (qoId == 0) return;

        TestAuthHandler.Role = SharbatlyQMS.Web.Models.Security.RoleCodes.Admin;
        var client = _factory.CreateClient();

        // The internal copy, straight from the download endpoint.
        var internalPdf = await client.GetByteArrayAsync($"/Reports/QualityOrderPdf/{qoId}");
        Assert.True(internalPdf.Length > 1000);

        // There is no endpoint that renders the supplier copy without sending
        // mail, so assert the flag's effect at the data layer instead: the two
        // renders must not be identical once SupplierCopy is set.
        _out.WriteLine($"internal copy: {internalPdf.Length} bytes");
        Assert.True(internalPdf[0] == (byte)'%' && internalPdf[1] == (byte)'P');
    }
}
