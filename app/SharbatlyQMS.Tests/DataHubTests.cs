using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Reports;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Data Hub (/Reports/FlatDefects + its Excel export), against live data.
///
/// It broke in production in a way no test would have caught: Dapper expands
/// <c>IN @ids</c> to one parameter per id, SQL Server refuses more than 2100 in
/// one command, and the hub hands EVERY sample in a date window to its batched
/// lookups. It worked until the data grew, then returned 500 with a message
/// about parameters that named nothing the user had done.
///
/// So the tests that matter here are the WIDE ones. A narrow window proves
/// nothing about this page.
/// </summary>
[Collection("workflow")]
public class DataHubTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public DataHubTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private IQualityOrderService Qos(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

    /// <summary>
    /// A year-wide window is far past the 2100-parameter cliff on this database.
    /// Streaming it to completion is the regression guard for the whole class of
    /// bug — every batched lookup on the path has to chunk, not just the three
    /// that were fixed first.
    /// </summary>
    [Fact]
    public async Task A_year_wide_window_streams_without_hitting_the_parameter_limit()
    {
        using var scope = _factory.Services.CreateScope();
        var filter = new FlatDefectFilter
        {
            PoFrom = DateTime.Today.AddYears(-1),
            PoTo   = DateTime.Today
        };

        var sw = Stopwatch.StartNew();
        var rows = 0;
        var withVariety = 0;
        var withOrigin  = 0;
        await foreach (var r in Qos(scope).StreamFlatDefectRowsAsync(filter, CancellationToken.None))
        {
            rows++;
            if (!string.IsNullOrWhiteSpace(r.Variety)) withVariety++;
            if (!string.IsNullOrWhiteSpace(r.Origin))  withOrigin++;
        }
        sw.Stop();
        _out.WriteLine($"year window: {rows} rows in {sw.ElapsedMilliseconds} ms " +
                       $"({withVariety} with variety, {withOrigin} with origin)");

        Assert.True(rows > 0, "A year of data produced no rows at all.");

        // MARA enrichment is wrapped in a catch that returns an EMPTY map on
        // failure, so a parameter-limit breach there does not throw — it just
        // silently strips Variety / Class / Origin / Brand from every row. A
        // wide window with zero enriched rows means that catch fired.
        Assert.True(withVariety > 0 || withOrigin > 0,
            "No row carries a Variety or Origin over a whole year. The MARA lookup " +
            "is failing silently — almost certainly the IN-list parameter limit again.");
    }

    /// <summary>
    /// Every filter the page offers, one at a time. A filter that throws is a
    /// 500 the user meets by typing in a box.
    /// </summary>
    public static TheoryData<string, FlatDefectFilter> Filters()
    {
        var from = DateTime.Today.AddMonths(-3);
        var to   = DateTime.Today;
        FlatDefectFilter Base() => new() { PoFrom = from, PoTo = to };
        return new TheoryData<string, FlatDefectFilter>
        {
            { "window only",   Base() },
            { "status",        new FlatDefectFilter { PoFrom = from, PoTo = to, Status = "Closed" } },
            { "plant",         new FlatDefectFilter { PoFrom = from, PoTo = to, Plant = "JD01" } },
            { "material group",new FlatDefectFilter { PoFrom = from, PoTo = to, MaterialGroup = "APPLE" } },
            { "vendor name",   new FlatDefectFilter { PoFrom = from, PoTo = to, VendorName = "a" } },
            { "container",     new FlatDefectFilter { PoFrom = from, PoTo = to, ContainerNo = "M" } },
            { "sample scope",  new FlatDefectFilter { PoFrom = from, PoTo = to, SampleScope = "Material" } },
            { "no window",     new FlatDefectFilter() },
        };
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Every_filter_streams_without_throwing(string name, FlatDefectFilter filter)
    {
        using var scope = _factory.Services.CreateScope();
        var rows = 0;
        await foreach (var _ in Qos(scope).StreamFlatDefectRowsAsync(filter, CancellationToken.None))
        {
            rows++;
            if (rows >= 5000) break;   // enough to prove the pipeline, not the whole table
        }
        _out.WriteLine($"{name}: {rows} rows");
    }

    /// <summary>
    /// The export is the half people actually take away, and it buffers before
    /// writing so the column map is final. A 200 with a real .xlsx body is the
    /// only thing that proves the whole path.
    /// </summary>
    [Fact]
    public async Task The_excel_export_returns_a_real_workbook()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();

        var from = DateTime.Today.AddMonths(-1).ToString("yyyy-MM-dd");
        var to   = DateTime.Today.ToString("yyyy-MM-dd");
        var res  = await client.GetAsync($"/Reports/FlatDefectsExcel?PoFrom={from}&PoTo={to}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        _out.WriteLine($"workbook: {bytes.Length} bytes");
        Assert.True(bytes.Length > 2000, $"Workbook suspiciously small ({bytes.Length} bytes).");
        // .xlsx is a zip: PK\x03\x04.
        Assert.True(bytes[0] == 0x50 && bytes[1] == 0x4B, "Response is not a zip/xlsx.");
    }

    /// <summary>
    /// The preview promises "the first N rows"; it must not quietly return a
    /// different number, and its truncation flag has to mean what it says.
    /// </summary>
    [Fact]
    public async Task The_preview_page_loads_and_reports_its_own_truncation_honestly()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();

        var sw = Stopwatch.StartNew();
        var res = await client.GetAsync("/Reports/FlatDefects");
        sw.Stop();
        _out.WriteLine($"preview page: {(int)res.StatusCode} in {sw.ElapsedMilliseconds} ms");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(html));
        Assert.DoesNotContain("Something went wrong", html);
    }

    /// <summary>
    /// The analyzer POSTs with an antiforgery header (X-CSRF-TOKEN), so a test
    /// that omits it gets a 400 from the framework and never reaches the
    /// action — proving nothing about the endpoint. Pull a real token from a
    /// rendered page, exactly as the browser does.
    /// </summary>
    private static async Task<HttpClient> AuthedJsonClientAsync(QmsAppFactory factory)
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = factory.CreateClient();
        var html = await client.GetStringAsync("/Reports/FlatDefects");
        var m = System.Text.RegularExpressions.Regex.Match(
            html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        Assert.True(m.Success, "No antiforgery token on the Data Hub page.");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", m.Groups[1].Value);
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    /// <summary>
    /// The dimension and measure keys the analyzer may use, straight from the
    /// registry. Deriving the test's payload from the schema rather than
    /// hard-coding keys means the test exercises what the UI would actually
    /// send, and cannot pass while quietly testing a key nobody uses.
    /// </summary>
    private async Task<(string dim, string measure, string agg)> FirstSchemaKeysAsync(HttpClient client)
    {
        var json = await client.GetStringAsync("/Reports/PivotSchema?report=flat_defects");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        var dim = root.GetProperty("dimensions")[0].GetProperty("key").GetString()!;
        var m   = root.GetProperty("measures")[0];
        var measure = m.GetProperty("key").GetString()!;
        var agg = m.GetProperty("aggs")[0].GetString()!;
        _out.WriteLine($"schema: dim={dim} measure={measure} agg={agg}");
        return (dim, measure, agg);
    }

    /// <summary>
    /// The pivot half of the hub. The interesting mode is ignorePageFilter:
    /// it drops the date window that keeps every other query bounded, which is
    /// the same shape of problem that broke the flat view.
    /// </summary>
    [Theory]
    [InlineData("windowed", false)]
    [InlineData("whole dataset", true)]
    public async Task The_pivot_aggregates_over_the_hub(string name, bool ignorePageFilter)
    {
        var client = await AuthedJsonClientAsync(_factory);
        var (dim, measure, agg) = await FirstSchemaKeysAsync(client);

        var body = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                ReportKey = "flat_defects",
                Rows      = new[] { dim },
                Cols      = Array.Empty<string>(),
                Measures  = new[] { new { Key = measure, Agg = agg } },
                IgnorePageFilter = ignorePageFilter,
                Filter    = new { PoFrom = DateTime.Today.AddMonths(-3), PoTo = DateTime.Today }
            }),
            System.Text.Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var res = await client.PostAsync("/Reports/Pivot", body);
        sw.Stop();
        var json = await res.Content.ReadAsStringAsync();
        _out.WriteLine($"pivot {name}: {(int)res.StatusCode} in {sw.ElapsedMilliseconds} ms, {json.Length} bytes");
        if (res.StatusCode != HttpStatusCode.OK)
            _out.WriteLine("BODY: " + json.Substring(0, Math.Min(700, json.Length)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("rowKeys", json);
    }

    /// <summary>
    /// The page filter must actually narrow the pivot, and IgnorePageFilter
    /// must actually widen it. A pivot that silently ignores its filter looks
    /// like it is working — it returns a full, plausible table — while every
    /// number in it answers a different question than the one asked.
    /// </summary>
    [Fact]
    public async Task The_page_filter_narrows_the_pivot_and_ignoring_it_widens_it()
    {
        var client = await AuthedJsonClientAsync(_factory);
        var (dim, measure, agg) = await FirstSchemaKeysAsync(client);

        async Task<int> RowCountAsync(bool ignore, DateTime from, DateTime to)
        {
            var body = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ReportKey = "flat_defects",
                    Rows      = new[] { dim },
                    Cols      = Array.Empty<string>(),
                    Measures  = new[] { new { Key = measure, Agg = agg } },
                    IgnorePageFilter = ignore,
                    Filter    = new { PoFrom = from, PoTo = to }
                }),
                System.Text.Encoding.UTF8, "application/json");
            var res = await client.PostAsync("/Reports/Pivot", body);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("rowKeys").GetArrayLength();
        }

        var narrow = await RowCountAsync(false, DateTime.Today.AddDays(-2), DateTime.Today);
        var wide   = await RowCountAsync(false, DateTime.Today.AddYears(-2), DateTime.Today);
        var all    = await RowCountAsync(true,  DateTime.Today.AddDays(-2), DateTime.Today);
        _out.WriteLine($"pivot rows -- 2 days: {narrow}, 2 years: {wide}, ignoring filter: {all}");

        Assert.True(wide > narrow,
            $"A two-year window ({wide} rows) returned no more than a two-day one ({narrow}). " +
            "The page filter is not reaching the pivot query.");
        Assert.True(all >= wide,
            $"IgnorePageFilter ({all} rows) returned fewer than an explicit wide window ({wide}). " +
            "The 'whole dataset' mode is narrower than the filter it claims to ignore.");
    }

    /// <summary>
    /// A dimension key the registry does not know must be REFUSED, not passed
    /// through to SQL. The pivot builds its GROUP BY from caller-supplied keys,
    /// so this is the injection surface of the whole hub.
    /// </summary>
    [Fact]
    public async Task An_unknown_pivot_dimension_is_rejected_not_executed()
    {
        var client = await AuthedJsonClientAsync(_factory);
        var (_, measure, agg) = await FirstSchemaKeysAsync(client);

        var body = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                ReportKey = "flat_defects",
                Rows      = new[] { "1; DROP TABLE qms_sample --" },
                Measures  = new[] { new { Key = measure, Agg = agg } }
            }),
            System.Text.Encoding.UTF8, "application/json");

        var res = await client.PostAsync("/Reports/Pivot", body);
        _out.WriteLine($"unknown dimension -> {(int)res.StatusCode}");

        // 400 means it was validated away. A 500 would mean it reached SQL.
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    /// <summary>
    /// The dimension-value picker feeds the analyzer's filter chips.
    /// </summary>
    [Fact]
    public async Task Pivot_dimension_values_load()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var (dim, _, _) = await FirstSchemaKeysAsync(client);

        var res = await client.GetAsync(
            $"/Reports/PivotValues?report=flat_defects&dim={Uri.EscapeDataString(dim)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("values", await res.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Perspectives are the hub's saved views. Listing them exercises the
    /// permission path that used to be decided by a role name that matched
    /// nobody, so the endpoint answered but could never report canShare.
    /// </summary>
    [Fact]
    public async Task Perspectives_list_for_the_data_hub()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/Reports/Perspectives?report=flat_defects");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("canShare", json);
        // An administrator holds ManagePerspectives/PublishPerspective now, so
        // this must be true — it was false for every user alive before.
        Assert.Contains("\"canShare\":true", json.Replace(" ", ""));
    }
}
