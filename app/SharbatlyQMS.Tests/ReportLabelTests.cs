using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Renaming a caption in the printed report.
///
/// "Discharge Date" was renamed on Admin → Labels, the screens changed, and the
/// PDF went on printing the old text: the renderer held its captions as string
/// literals and never consulted the label service at all. They now go through
/// the same lookup the views use.
/// </summary>
[Collection("workflow")]
public class ReportLabelTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ReportLabelTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    /// <summary>Locates the renderer's source, which this test reads as data.</summary>
    private static string RendererSource()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "SharbatlyQMS.Web", "Services", "Pdf", "QualityReportPdfSoft.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return "";
    }

    /// <summary>
    /// The catalogue drives what the Labels screen offers, and the renderer
    /// decides what is actually printed. A caption added to one and not the
    /// other is invisible: the administrator either cannot find a label that
    /// exists, or renames one that nothing prints. So they are compared.
    /// </summary>
    [Fact]
    public void Every_caption_the_renderer_prints_is_offered_on_the_labels_screen()
    {
        var src = RendererSource();
        if (src.Length == 0)
        {
            _out.WriteLine("Renderer source not reachable from the test output directory.");
            return;
        }

        var printed = Regex.Matches(src, "L\\(\"([^\"]*)\"\\)")
                           .Select(m => m.Groups[1].Value)
                           .ToHashSet(StringComparer.Ordinal);
        var offered = ReportLabelCatalog.All.ToHashSet(StringComparer.Ordinal);

        _out.WriteLine($"{printed.Count} printed, {offered.Count} offered");

        var missing = printed.Except(offered).OrderBy(x => x).ToList();
        var extra   = offered.Except(printed).OrderBy(x => x).ToList();
        foreach (var m in missing) _out.WriteLine($"  printed but not offered: {m}");
        foreach (var e in extra)   _out.WriteLine($"  offered but not printed: {e}");

        Assert.Empty(missing);
        Assert.Empty(extra);
    }

    /// <summary>
    /// Without a lookup installed the report prints the English it ships with.
    /// Every test that renders a PDF, and every code path outside a request,
    /// depends on this.
    /// </summary>
    [Fact]
    public void With_no_lookup_a_caption_prints_the_english_it_ships_with()
    {
        Assert.Equal("Discharge Date", ReportLabels.T("Discharge Date"));
    }

    /// <summary>
    /// The scope restores what it replaced rather than clearing, so the image
    /// appendix rendering inside the main document cannot switch the outer
    /// lookup off half way through.
    /// </summary>
    [Fact]
    public void A_nested_render_does_not_switch_the_outer_lookup_off()
    {
        using (ReportLabels.Use(t => "OUTER:" + t))
        {
            Assert.Equal("OUTER:Container", ReportLabels.T("Container"));
            using (ReportLabels.Use(t => "INNER:" + t))
                Assert.Equal("INNER:Container", ReportLabels.T("Container"));
            Assert.Equal("OUTER:Container", ReportLabels.T("Container"));
        }
        Assert.Equal("Container", ReportLabels.T("Container"));
    }

    /// <summary>
    /// Two reports rendering at once must not share a lookup. A plain static
    /// field would hand one user the other's captions; this is why it is an
    /// AsyncLocal.
    /// </summary>
    [Fact]
    public async Task Two_renders_at_once_keep_their_own_captions()
    {
        async Task<string> RenderAs(string tag)
        {
            using var _ = ReportLabels.Use(t => tag + ":" + t);
            await Task.Delay(30);                  // let the other flow interleave
            return ReportLabels.T("Container");
        }

        var both = await Task.WhenAll(RenderAs("A"), RenderAs("B"));
        _out.WriteLine(string.Join(", ", both));
        Assert.Contains("A:Container", both);
        Assert.Contains("B:Container", both);
    }

    /// <summary>
    /// The whole point, end to end: rename a caption and the PDF changes.
    /// A rendered PDF is compressed, so the bytes are not searched — the
    /// lookup the controller installs is exercised through the same call the
    /// renderer makes.
    /// </summary>
    [Fact]
    public async Task Renaming_a_caption_changes_what_the_report_would_print()
    {
        using var scope = _factory.Services.CreateScope();
        var labels = scope.ServiceProvider.GetRequiredService<ILabelService>();
        const string key = "Discharge Date";
        var before = (await labels.ListAsync(CancellationToken.None))
            .FirstOrDefault(l => l.Key == key)?.CustomText;
        try
        {
            await labels.SaveAsync(key, "Vessel Discharge Date", "test", CancellationToken.None);

            var data = new QualityReportData
            {
                Localiser = t => labels.Text(t, ReportLabels.ScreenKey)
            };
            using var _ = ReportLabels.Use(data.Localiser);
            Assert.Equal("Vessel Discharge Date", ReportLabels.T(key));
            // Untouched captions still print their own text.
            Assert.Equal("Loading Date", ReportLabels.T("Loading Date"));
        }
        finally
        {
            await labels.SaveAsync(key, before ?? "", "test", CancellationToken.None);
        }
    }

    /// <summary>
    /// Opening the Labels screen registers the report's captions, so an
    /// administrator can find them without generating a report first.
    /// </summary>
    [Fact]
    public async Task Opening_the_labels_screen_lists_the_report_captions()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var res = await _factory.CreateClient().GetAsync("/Labels");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();

        // A caption only the PDF prints, so its presence proves the report's
        // labels were registered rather than having arrived from some screen.
        // Not filtered by screen: a label is keyed by its English text alone,
        // and one the report shares with a view keeps whichever screen saw it
        // first -- renaming it still changes both, which is the point.
        Assert.Contains("Every sample and every reading", html);

        using var scope = _factory.Services.CreateScope();
        var labels = scope.ServiceProvider.GetRequiredService<ILabelService>();
        var known = (await labels.ListAsync(CancellationToken.None))
            .Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var absent = ReportLabelCatalog.All.Where(c => !known.Contains(c)).ToList();
        foreach (var a in absent) _out.WriteLine($"  not registered: {a}");
        Assert.Empty(absent);
    }

    /// <summary>The report still renders with the lookup in place.</summary>
    [Fact]
    public async Task A_report_still_renders_with_labels_installed()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        using var scope = _factory.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var rows   = await claims.ListClosedQosAsync(
            new Web.ViewModels.ClaimListFilter(), Web.Models.PlantScope.All, "test");

        long? id = null;
        foreach (var r in rows.Take(20))
        {
            var qo = await qos.GetAsync(r.QualityOrderId);
            if (qo is { StatusCode: Web.Models.QualityOrderStatus.Closed, IsArchived: false })
            { id = qo.QualityOrderId; break; }
        }
        if (id is null) { _out.WriteLine("No finished order to render."); return; }

        var res = await _factory.CreateClient()
            .GetAsync($"/Reports/QualityOrderPdfPreview/{id}");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        _out.WriteLine($"{bytes.Length} bytes");
        Assert.True(bytes.Length > 1000);
        Assert.True(bytes[0] == (byte)'%' && bytes[1] == (byte)'P', "Not a PDF.");
    }
}
