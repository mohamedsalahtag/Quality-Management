using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Sap;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Refresh materials from SAP: only another grade of the same goods, only for
/// the administrator by default, and every refusal leaves the order as it was.
/// Nothing here applies a change to live data.
/// </summary>
[Collection("workflow")]
public class MaterialRefreshTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public MaterialRefreshTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private static MaterialKey Key(string no, string grade, string size = "100", decimal weight = 18m) => new()
    {
        MaterialNo = no, ClassName = grade, MaterialGroup = "APPLE", VarietyId = "C97", OriginId = "RS",
        SizeId = size, Weight = weight, BaseUnit = "CT", MaterialType = "ZTRD"
    };

    [Fact]
    public void Only_a_different_grade_of_the_same_goods_is_a_sibling()
    {
        var xf = Key("XF100", "Extra Fancy");
        Assert.True(MaterialKey.Siblings(xf, Key("FC100", "Fancy")));
        Assert.False(MaterialKey.Siblings(xf, Key("FC113", "Fancy", size: "113")));
        Assert.False(MaterialKey.Siblings(xf, Key("FC100-10", "Fancy", weight: 10m)));
        Assert.False(MaterialKey.Siblings(xf, new MaterialKey { MaterialNo = "GS", ClassName = "Fancy", MaterialGroup = "APPLE",
                                                               VarietyId = "C12", OriginId = "RS", SizeId = "100", Weight = 18m, BaseUnit = "CT" }));
    }

    private static (List<MaterialRefreshLine>, List<MaterialRefreshService.LineRow>, Dictionary<string, MaterialKey>) Order()
    {
        var lines = new List<MaterialRefreshService.LineRow>
        {
            new() { QoMaterialId = 1, Ebelp = "10", Quantity = 392, MaterialNo = "XF100" },
            new() { QoMaterialId = 2, Ebelp = "20", Quantity = 280, MaterialNo = "XF113" },
        };
        var preview = lines.Select(l => new MaterialRefreshLine { QoMaterialId = l.QoMaterialId, OldNo = l.MaterialNo }).ToList();
        var old = new Dictionary<string, MaterialKey>(StringComparer.OrdinalIgnoreCase)
        { ["XF100"] = Key("XF100", "Extra Fancy"), ["XF113"] = Key("XF113", "Extra Fancy", size: "113") };
        return (preview, lines, old);
    }

    [Fact]
    public void A_regrade_in_SAP_is_matched_line_by_line()
    {
        var (preview, lines, old) = Order();
        var sap = new[]
        {
            new SapShipmentRow { Ebelp = "20", MaterialNo = "FC113", Quantity = 280 },
            new SapShipmentRow { Ebelp = "10", MaterialNo = "FC100", Quantity = 392 },
        };
        var keys = new Dictionary<string, MaterialKey>(StringComparer.OrdinalIgnoreCase)
        { ["FC100"] = Key("FC100", "Fancy"), ["FC113"] = Key("FC113", "Fancy", size: "113") };

        Assert.Null(MaterialRefreshService.MatchSap(preview, lines, old, sap, keys));
        Assert.Equal("FC100", preview[0].NewNo);
        Assert.Equal("FC113", preview[1].NewNo);
        Assert.Equal("Fancy", preview[0].NewGrade);
    }

    [Fact]
    public void A_different_size_a_changed_quantity_or_a_missing_line_is_refused()
    {
        var keys = new Dictionary<string, MaterialKey>(StringComparer.OrdinalIgnoreCase)
        { ["FC100"] = Key("FC100", "Fancy"), ["FC113"] = Key("FC113", "Fancy", size: "113"), ["FC125"] = Key("FC125", "Fancy", size: "125") };

        var (p1, l1, o1) = Order();
        Assert.NotNull(MaterialRefreshService.MatchSap(p1, l1, o1, new[]
        {
            new SapShipmentRow { Ebelp = "10", MaterialNo = "FC100", Quantity = 392 },
            new SapShipmentRow { Ebelp = "20", MaterialNo = "FC125", Quantity = 280 },   // size changed
        }, keys));

        var (p2, l2, o2) = Order();
        Assert.NotNull(MaterialRefreshService.MatchSap(p2, l2, o2, new[]
        {
            new SapShipmentRow { Ebelp = "10", MaterialNo = "FC100", Quantity = 400 },   // quantity changed
            new SapShipmentRow { Ebelp = "20", MaterialNo = "FC113", Quantity = 280 },
        }, keys));

        var (p3, l3, o3) = Order();
        Assert.NotNull(MaterialRefreshService.MatchSap(p3, l3, o3, new[]
        {
            new SapShipmentRow { Ebelp = "10", MaterialNo = "FC100", Quantity = 392 },
        }, keys));
        Assert.All(p3.Concat(p1).Concat(p2), l => Assert.True(l.NewNo == null || l.NewNo.StartsWith("FC")));
    }

    [Fact]
    public async Task Only_the_administrator_holds_it_by_default()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        await perms.EnsureLoadedAsync();
        Assert.True(perms.RoleHas(RoleCodes.Admin, Perm.Qo.RefreshMaterials, AccessLevel.Edit, isScreen: false));
        foreach (var r in new[] { RoleCodes.Manager, RoleCodes.Supervisor, RoleCodes.Operator, RoleCodes.Viewer })
            Assert.False(perms.RoleHas(r, Perm.Qo.RefreshMaterials, AccessLevel.Edit, isScreen: false));
    }

    private async Task<long?> FinishedOrderAsync()
    {
        using var s = _factory.Services.CreateScope();
        using var c = new Microsoft.Data.SqlClient.SqlConnection(
            s.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        return await c.ExecuteScalarAsync<long?>(@"
            SELECT TOP 1 quality_order_id FROM qms_quality_order
            WHERE status_code = 'Closed' AND superseded_at IS NULL AND archived_at IS NULL
            ORDER BY quality_order_id DESC");
    }

    [Theory]
    [InlineData(RoleCodes.Operator)]
    [InlineData(RoleCodes.Supervisor)]
    public async Task Other_roles_are_refused_and_see_no_button(string role)
    {
        var id = await FinishedOrderAsync();
        if (id is null) return;
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        var res = await client.GetAsync($"/QualityOrders/RefreshMaterialsPreview/{id}");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        var page = await client.GetStringAsync($"/QualityOrders/Details/{id}");
        Assert.DoesNotContain("id=\"refreshMaterialsModal\"", page);
    }

    [Fact]
    public async Task Refusals_change_nothing()
    {
        var id = await FinishedOrderAsync();
        if (id is null) return;
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IMaterialRefreshService>();
        var qos = s.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var before = (await qos.GetMaterialsAsync(id.Value)).Select(m => m.MaterialNo).ToList();

        var preview = await svc.PreviewAsync(id.Value);
        _out.WriteLine($"{preview.Mode}: {preview.Message}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ApplyAsync(id.Value, new Dictionary<long, string>(), "no", "test"));
        var line = preview.Lines.FirstOrDefault();
        if (line != null)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                svc.ApplyAsync(id.Value, new Dictionary<long, string> { [line.QoMaterialId] = "NOT-A-SIBLING" }, "Testing a refusal", "test"));

        var after = (await qos.GetMaterialsAsync(id.Value)).Select(m => m.MaterialNo).ToList();
        Assert.Equal(before, after);
    }
}
