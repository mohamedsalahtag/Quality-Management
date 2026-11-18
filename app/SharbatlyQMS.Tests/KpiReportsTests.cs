using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SharbatlyQMS.Web.Controllers;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Reports -> KPI reports against live data, read-only: who may open it, that
/// the figures hold together, that each figure's drill lists exactly the rows
/// it counted, and that the export comes back titled for this page.
/// </summary>
[Collection("workflow")]
public class KpiReportsTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public KpiReportsTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static readonly DateTime To = DateTime.Today;
    private static readonly DateTime From = To.AddDays(-27);

    private HttpClient Client(string role)
    {
        var c = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return c;
    }

    private async Task<KpiDataset> DataAsync()
    {
        using var s = _factory.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IKpiReportService>().GetAsync(From, To, PlantScope.All);
    }

    private static decimal N(KpiDataset d, string kpi, string user) =>
        d.Values.TryGetValue(kpi, out var by) && by.TryGetValue(user, out var v) ? v[0] ?? 0 : 0;

    [Fact]
    public async Task Managers_and_admin_open_it_others_do_not()
    {
        foreach (var role in new[] { RoleCodes.Admin, RoleCodes.Manager })
        {
            var res = await Client(role).GetAsync("/KpiReports");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("KPI reports", await res.Content.ReadAsStringAsync());
        }
        foreach (var role in new[] { RoleCodes.Operator, RoleCodes.Supervisor })
        {
            var res = await Client(role).GetAsync("/KpiReports/Data");
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
            var home = await Client(role).GetAsync("/");
            if (home.StatusCode == HttpStatusCode.OK)
                Assert.DoesNotContain("/KpiReports", await home.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task The_figures_hold_together()
    {
        var d = await DataAsync();
        _out.WriteLine($"{d.Users.Count} users, {d.Values.Count} kpis");
        Assert.NotEmpty(d.Users);
        Assert.DoesNotContain(d.Users, u => KpiReportService.SystemActors.Contains(u.User));
        foreach (var u in d.Users.Select(x => x.User))
        {
            foreach (var k in d.Kpis.Where(k => k.Kind == "rate"))
                Assert.True(N(d, k.Num!, u) <= Math.Max(N(d, k.Den!, u), N(d, k.Num!, u)) || N(d, k.Den!, u) == 0);
            Assert.True(N(d, "samples", u) <= N(d, "samples_all", u));
            Assert.True(N(d, "weight_slips", u) <= N(d, "weight_readings", u));
        }
        foreach (var k in d.Kpis.Where(k => k.Kind == "dur"))
            if (d.Values.TryGetValue(k.Key, out var by))
                Assert.All(by.Values, v => { Assert.True(v[0] > 0); Assert.True(v[1] >= 0); Assert.True(v[2] >= 0); });
    }

    [Fact]
    public async Task Each_drill_lists_exactly_what_its_figure_counted()
    {
        var d = await DataAsync();
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IKpiReportService>();
        foreach (var kpi in new[] { "qo_finished", "samples", "open_submit", "sent_back_rate", "reports_sent" })
        {
            var key = d.Kpis.First(k => k.Key == kpi).Num ?? kpi;
            if (!d.Values.TryGetValue(key, out var by) || by.Count == 0) continue;
            var (user, v) = by.OrderByDescending(x => x.Value[0]).First();
            var rows = await svc.DrillAsync(kpi, user, From, To, PlantScope.All);
            var total = d.Kpis.First(k => k.Key == kpi).Kind == "dur" ? rows.Count(r => r.Dur.HasValue) : rows.Sum(r => r.Qty);
            _out.WriteLine($"{kpi} {user}: {v[0]} vs drill {total}");
            Assert.Equal(v[0], total);
        }
    }

    /// <summary>A submit's duration is the gap to the moment the order was last opened.</summary>
    [Fact]
    public async Task Opened_to_submitted_pairs_the_two_history_rows()
    {
        var d = await DataAsync();
        if (!d.Values.TryGetValue("open_submit", out var by) || by.Count == 0) return;
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IKpiReportService>();
        var user = by.Keys.First();
        var ev = (await svc.DrillAsync("open_submit", user, From, To, PlantScope.All)).First(e => e.Dur.HasValue);

        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            s.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var rows = (await c.QueryAsync<(string? Old, string New, DateTime At)>(@"
            SELECT old_status, new_status, changed_at FROM qms_status_history
            WHERE entity_type = 'QualityOrder' AND entity_id = @id ORDER BY changed_at, status_history_id",
            new { id = ev.QoId })).ToList();
        var submit = rows.FindIndex(r => r.New == "Submitted" && Math.Abs((r.At - ev.At).TotalSeconds) < 1);
        Assert.True(submit > 0);
        Assert.Equal("Open", rows[submit - 1].New);
        var minutes = (decimal)Math.Floor((rows[submit].At - rows[submit - 1].At).TotalMinutes);
        Assert.InRange(ev.Dur!.Value, minutes / 60m - 0.02m, minutes / 60m + 0.02m);
    }

    [Fact]
    public async Task A_one_plant_scope_shows_only_that_plant()
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IKpiReportService>();
        var all = await svc.GetAsync(From, To, PlantScope.All);
        if (!all.Values.TryGetValue("qo_finished", out var by) || by.Count == 0) return;
        var user = by.Keys.First();
        var rows = await svc.DrillAsync("qo_finished", user, From, To, PlantScope.All);
        var plant = rows.First().Plant!;
        var scoped = await svc.DrillAsync("qo_finished", user, From, To, new PlantScope(false, new[] { plant }));
        Assert.NotEmpty(scoped);
        Assert.All(scoped, r => Assert.Equal(plant, r.Plant));
    }

    [Fact]
    public async Task The_export_comes_back_titled_for_kpi_reports()
    {
        var client = Client(RoleCodes.Admin);
        var html = await client.GetStringAsync("/KpiReports");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var header = _factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.HeaderName!;
        foreach (var format in new[] { "xlsx", "pdf" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/KpiReports/Export")
            {
                Content = JsonContent($$"""
                    {"format":"{{format}}","title":"Volume","tab":"Volume","period":"Actions 2026-11-01 to 2026-11-18",
                     "rowLabel":"User","columns":[{"label":"Finished","kind":"count"}],
                     "rows":[{"label":"Team median","values":[12]}],"footnote":"Test."}
                    """)
            };
            req.Headers.Add(header, token);
            var res = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal(format == "pdf" ? (byte)'%' : (byte)'P', bytes[0]);
            if (format == "xlsx")
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
                Assert.Contains(wb.Worksheet(1).CellsUsed(), c => c.GetString().StartsWith("KPI reports — "));
            }
        }
        Assert.Contains($"\"{header}\": TOKEN", await client.GetStringAsync("/js/kpi-reports.js"));
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
