using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The tolerance held against a defect CATEGORY for one material group (M39),
/// compared with the category's total percentage.
///
/// Same rule as a catalog defect's tolerance -- inclusive, and only once the
/// category has occurred -- but keyed on the material group, because Major is
/// one category shared by every fruit while its agreed limit is not. A
/// tolerance set for one group must never colour another group's total.
///
/// This suite writes to the live tolerance table; every test restores it.
/// </summary>
[Collection("workflow")]
public class CategoryToleranceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public CategoryToleranceTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
    }

    private static DefectCategorySection Section(decimal value, decimal pct, decimal? tolerance) => new()
    {
        CategoryName = "Major",
        Tolerance    = tolerance,
        Rows         = { new DefectAggRow { Name = "Bruising", SumValue = value, Percentage = pct } }
    };

    [Theory]
    [InlineData(5, 5.00, 5.00, true)]    // exactly on it -> breach
    [InlineData(5, 7.50, 5.00, true)]
    [InlineData(5, 4.99, 5.00, false)]
    [InlineData(5, 99.0, null, false)]   // none agreed -> never red
    [InlineData(1, 0.50, 0.00, true)]    // zero: any occurrence fails...
    [InlineData(0, 0.00, 0.00, false)]   // ...but an empty category is not a breach
    public void The_red_rule_on_a_category_total(decimal value, decimal pct, double? tolerance, bool expected)
    {
        var sec = Section(value, pct, tolerance is null ? null : (decimal)tolerance.Value);
        Assert.Equal(expected, sec.ExceedsTolerance);
    }

    [Fact]
    public void The_total_is_what_is_compared_not_a_single_row()
    {
        var sec = new DefectCategorySection
        {
            CategoryName = "Major", Tolerance = 5m,
            Rows =
            {
                new DefectAggRow { Name = "A", SumValue = 3, Percentage = 3m },
                new DefectAggRow { Name = "B", SumValue = 2, Percentage = 2m },
            }
        };
        Assert.Equal(5m, sec.TotalPct);
        Assert.True(sec.ExceedsTolerance);
    }

    private static HttpClient Client(QmsAppFactory f)
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        return f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false });
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin/DefectCategories");
        var m = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on the Defect Categories page.");
        return m.Groups[1].Value;
    }

    /// <summary>Snapshot of the whole table, put back after each write test.</summary>
    private sealed record Saved(int CategoryId, string MaterialGroup, decimal? Tolerance,
                                DateTime? UpdatedAt, string? UpdatedBy);

    private async Task<List<Saved>> SnapshotAsync()
    {
        using var c = Db();
        return (await c.QueryAsync<Saved>(@"
            SELECT category_id CategoryId, material_group MaterialGroup, tolerance Tolerance,
                   updated_at UpdatedAt, updated_by UpdatedBy
            FROM   qms_defect_category_tolerance")).ToList();
    }

    private async Task RestoreAsync(List<Saved> snap)
    {
        using var c = Db();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync("DELETE FROM qms_defect_category_tolerance", transaction: tx);
        foreach (var r in snap)
            await c.ExecuteAsync(
                @"INSERT INTO qms_defect_category_tolerance (category_id, material_group, tolerance, updated_at, updated_by)
                  VALUES (@CategoryId, @MaterialGroup, @Tolerance, @UpdatedAt, @UpdatedBy)", r, tx);
        tx.Commit();
    }

    private async Task<(int CategoryId, string Group, List<int> AllActive)> AnyCategoryAndGroupAsync()
    {
        using var c = Db();
        var cats  = (await c.QueryAsync<int>(
            "SELECT category_id FROM qms_defect_category WHERE is_active = 1 ORDER BY sort_order")).ToList();
        var group = await c.ExecuteScalarAsync<string>(
            "SELECT TOP 1 material_group FROM qms_defect_catalog WHERE material_group <> '' ORDER BY material_group");
        return (cats[0], group!, cats);
    }

    private async Task<decimal?> ToleranceOfAsync(int categoryId, string group)
    {
        using var c = Db();
        return await c.ExecuteScalarAsync<decimal?>(
            "SELECT tolerance FROM qms_defect_category_tolerance WHERE category_id = @categoryId AND material_group = @group",
            new { categoryId, group });
    }

    private async Task<HttpResponseMessage> SaveAsync(HttpClient client, string group,
        IEnumerable<(int Id, string Value)> values)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await TokenAsync(client)),
            new("materialGroup", group)
        };
        foreach (var (id, v) in values)
        {
            form.Add(new("categoryId", id.ToString()));
            form.Add(new("tolerance", v));
        }
        return await client.PostAsync("/Admin/SaveCategoryTolerances", new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task A_tolerance_saved_on_screen_is_kept_to_two_decimals_and_blank_stays_blank()
    {
        var snap = await SnapshotAsync();
        var client = Client(_factory);
        var (cat, group, all) = await AnyCategoryAndGroupAsync();
        try
        {
            var res = await SaveAsync(client, group, all.Select(id => (id, id == cat ? "4.256" : "")));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Equal(4.26m, await ToleranceOfAsync(cat, group));
            foreach (var other in all.Where(id => id != cat))
                Assert.Null(await ToleranceOfAsync(other, group));

            // Zero is a real value, not the same as blank.
            await SaveAsync(client, group, all.Select(id => (id, id == cat ? "0" : "")));
            Assert.Equal(0m, await ToleranceOfAsync(cat, group));

            // A negative is refused and nothing changes.
            await SaveAsync(client, group, all.Select(id => (id, id == cat ? "-1" : "")));
            Assert.Equal(0m, await ToleranceOfAsync(cat, group));
        }
        finally { await RestoreAsync(snap); }
    }

    /// <summary>
    /// The group summary must judge a category against ITS OWN group's
    /// tolerance. The test sets a tolerance on the summarised group and a
    /// different one on another group, and checks only the first arrives.
    /// </summary>
    [Fact]
    public async Task The_group_summary_carries_its_own_groups_category_tolerance()
    {
        var snap = await SnapshotAsync();
        using var scope = _factory.Services.CreateScope();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        try
        {
            var rows = (await claims.ListClosedQosAsync(new SharbatlyQMS.Web.ViewModels.ClaimListFilter(),
                                                       PlantScope.All, "test")).Rows;
            foreach (var r in rows.Where(r => r.StatusCode == QualityOrderStatus.Closed).Take(25))
            {
                var materials = await qos.GetMaterialsAsync(r.QualityOrderId);
                var before    = await qos.BuildGroupSummariesAsync(r.QualityOrderId, materials);
                var summary   = before.FirstOrDefault(g => g.DefectSections.Any(sec => sec.Rows.Count > 0));
                if (summary is null) continue;
                var section = summary.DefectSections.First(sec => sec.Rows.Count > 0);

                using var c = Db();
                var catId = await c.ExecuteScalarAsync<int>(
                    "SELECT category_id FROM qms_defect_category WHERE category_name = @n", new { n = section.CategoryName });
                await c.ExecuteAsync("DELETE FROM qms_defect_category_tolerance WHERE category_id = @catId", new { catId });
                await c.ExecuteAsync(@"
                    INSERT INTO qms_defect_category_tolerance (category_id, material_group, tolerance)
                    VALUES (@catId, @mine, 1.25), (@catId, 'ZZ-OTHER', 77)",
                    new { catId, mine = summary.MaterialGroup });

                var after = await qos.BuildGroupSummariesAsync(r.QualityOrderId, materials);
                var sec   = after.First(g => g.MaterialGroup == summary.MaterialGroup)
                                 .DefectSections.First(s => s.CategoryName == section.CategoryName);
                _out.WriteLine($"QO {r.QualityOrderId} {summary.MaterialGroup}/{sec.CategoryName}: " +
                               $"total {sec.TotalPct:0.00}% against 1.25 -> {(sec.ExceedsTolerance ? "RED" : "normal")}");
                Assert.Equal(1.25m, sec.Tolerance);
                Assert.Equal(sec.TotalPieces > 0 && sec.TotalPct >= 1.25m, sec.ExceedsTolerance);
                return;
            }
            _out.WriteLine("No finished order with summarised defects to test against.");
        }
        finally { await RestoreAsync(snap); }
    }

    [Fact]
    public async Task A_tolerance_edited_in_the_export_comes_back_on_upload()
    {
        var snap = await SnapshotAsync();
        var client = Client(_factory);
        var (cat, group, _) = await AnyCategoryAndGroupAsync();
        try
        {
            var res = await client.GetAsync($"/Admin/DefectCategoryTolerancesExcel?materialGroup={group}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
            var ws = wb.Worksheet("Category tolerances");
            ws.Unprotect();
            var row = ws.RowsUsed().Skip(1).First(x => x.Cell(1).GetValue<int>() == cat
                                                    && x.Cell(2).GetString() == group);
            row.Cell(4).Value = 3.5m;
            using var ms = new MemoryStream();
            wb.SaveAs(ms);

            var form = new MultipartFormDataContent
            {
                { new StringContent(await TokenAsync(client)), "__RequestVerificationToken" }
            };
            var file = new ByteArrayContent(ms.ToArray());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(file, "file", "category-tolerances.xlsx");
            var up = await client.PostAsync("/Admin/DefectCategoryTolerancesImport", form);
            Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
            Assert.Equal(3.5m, await ToleranceOfAsync(cat, group));
        }
        finally { await RestoreAsync(snap); }
    }
}
