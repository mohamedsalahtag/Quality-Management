using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The tolerance held against a defect in the catalog.
///
/// Two things are easy to get wrong here and neither shows up by eye. The
/// first is precision: the column keeps two decimals, so a value typed with
/// more is rounded, and the administrator has to be shown what was actually
/// kept rather than discover it later on a report. The second is that BLANK
/// and ZERO are different statements -- no tolerance agreed, versus any
/// occurrence fails -- and a round trip that turned one into the other would
/// quietly change what the catalog means.
///
/// This suite writes to the live catalog; every test removes what it created.
/// </summary>
[Collection("workflow")]
public class DefectToleranceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public DefectToleranceTests(QmsAppFactory factory, ITestOutputHelper output)
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

    private static async Task<string> TokenAsync(HttpClient client)
    {
        var html = await client.GetStringAsync("/Admin/DefectCatalog");
        var m = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "No antiforgery token on the Defect Catalog page.");
        return m.Groups[1].Value;
    }

    private static HttpClient Client(QmsAppFactory f)
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        return f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false });
    }

    /// <summary>The category has to be a real one or the save is refused.</summary>
    private async Task<string> AnyCategoryAsync()
    {
        using var s = _factory.Services.CreateScope();
        var cats = await s.ServiceProvider.GetRequiredService<ICatalogCache>().GetActiveCategoriesAsync();
        return cats.First().CategoryName;
    }

    private async Task<(int Id, string Code)> SaveAsync(HttpClient client, string? tolerance,
        string? code = null, int id = 0)
    {
        code ??= "TESTTOL" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        var form = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", await TokenAsync(client)),
            new("defectId",       id.ToString()),
            new("materialGroup",  "APPLE"),
            new("defectCode",     code),
            new("defectName",     "Tolerance test " + code),
            new("defectCategory", await AnyCategoryAsync()),
            new("valueType",      "Decimal"),
            new("isActive",       "true"),
            new("sortOrder",      "999"),
        };
        if (tolerance != null) form.Add(new("tolerance", tolerance));

        var res = await client.PostAsync("/Admin/SaveDefect", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        using var c = Db();
        await c.OpenAsync();
        var newId = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
            "SELECT TOP 1 defect_id FROM qms_defect_catalog WHERE defect_code = @code", new { code });
        return (newId, code);
    }

    private async Task CleanupAsync(string code)
    {
        using var c = Db();
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c,
            "DELETE FROM qms_defect_catalog WHERE defect_code = @code", new { code });
    }

    private async Task<decimal?> ToleranceOfAsync(int id)
    {
        using var c = Db();
        await c.OpenAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<decimal?>(c,
            "SELECT tolerance FROM qms_defect_catalog WHERE defect_id = @id", new { id });
    }

    /// <summary>
    /// Two decimal places, and rounded rather than truncated -- 2.555 is nearer
    /// 2.56 than 2.55, and an administrator quoting a tolerance to the supplier
    /// should get the value they would have written themselves.
    /// </summary>
    [Theory]
    [InlineData("2.5",   2.50)]
    [InlineData("0.25",  0.25)]
    [InlineData("0",     0.00)]
    [InlineData("10",   10.00)]
    [InlineData("2.555", 2.56)]
    [InlineData("2.444", 2.44)]
    public async Task A_tolerance_is_kept_to_two_decimals(string typed, double expected)
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, typed);
        try
        {
            var stored = await ToleranceOfAsync(id);
            _out.WriteLine($"typed {typed,6} -> stored {stored}");
            Assert.Equal((decimal)expected, stored);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// Blank means "not agreed" and must stay NULL. Turning it into 0 would
    /// silently declare that any occurrence of the defect fails.
    /// </summary>
    [Fact]
    public async Task Leaving_it_blank_stores_nothing_rather_than_zero()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "");
        try
        {
            var stored = await ToleranceOfAsync(id);
            _out.WriteLine($"blank -> {(stored is null ? "NULL" : stored.ToString())}");
            Assert.Null(stored);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>And zero is kept as zero, not confused with blank.</summary>
    [Fact]
    public async Task Zero_is_kept_and_is_not_the_same_as_blank()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "0");
        try
        {
            Assert.Equal(0m, await ToleranceOfAsync(id));
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>A negative tolerance is meaningless; it is refused, not stored.</summary>
    [Fact]
    public async Task A_negative_tolerance_is_refused()
    {
        var client = Client(_factory);
        var code = "TESTNEG" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        try
        {
            await SaveAsync(client, "-1", code);
            using var c = Db();
            await c.OpenAsync();
            var exists = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_defect_catalog WHERE defect_code = @code", new { code });
            _out.WriteLine($"rows created for a negative tolerance: {exists}");
            Assert.Equal(0, exists);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// Editing an existing defect keeps the tolerance the form posts, including
    /// clearing it again. A field that could only ever be set would leave a
    /// wrong value with no way back.
    /// </summary>
    [Fact]
    public async Task An_existing_tolerance_can_be_changed_and_cleared()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "3.25");
        try
        {
            Assert.Equal(3.25m, await ToleranceOfAsync(id));

            await SaveAsync(client, "1.75", code, id);
            Assert.Equal(1.75m, await ToleranceOfAsync(id));

            await SaveAsync(client, "", code, id);
            Assert.Null(await ToleranceOfAsync(id));
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// It has to come back out through the service the sample form and the
    /// report read from, not just sit in the table.
    /// </summary>
    [Fact]
    public async Task The_catalog_reads_the_tolerance_back()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "4.25");
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

            var forGroup = await qos.GetActiveDefectsForGroupAsync("APPLE");
            var mine = forGroup.FirstOrDefault(d => d.DefectId == id);
            Assert.NotNull(mine);
            _out.WriteLine($"{mine!.DefectCode}: tolerance {mine.Tolerance}");
            Assert.Equal(4.25m, mine.Tolerance);

            var all = await qos.GetActiveDefectsAsync();
            Assert.Equal(4.25m, all.First(d => d.DefectId == id).Tolerance);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>And it is on the screen, in the grid and in the edit form.</summary>
    [Fact]
    public async Task The_catalog_page_shows_the_tolerance()
    {
        var client = Client(_factory);
        var (_, code) = await SaveAsync(client, "7.25");
        try
        {
            var html = await client.GetStringAsync("/Admin/DefectCatalog?materialGroup=APPLE");
            Assert.Contains("7.25", html);
            Assert.Contains("name=\"tolerance\"", html);
            Assert.Contains("step=\"0.01\"", html);
        }
        finally { await CleanupAsync(code); }
    }

    // ================= export / import round trip =====================

    private static async Task<ClosedXML.Excel.XLWorkbook> DownloadAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ms = new MemoryStream(await res.Content.ReadAsByteArrayAsync());
        return new ClosedXML.Excel.XLWorkbook(ms);
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] xlsx,
        string name = "catalog.xlsx")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(await TokenAsync(client)), "__RequestVerificationToken" }
        };
        var file = new ByteArrayContent(xlsx);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", name);
        return await client.PostAsync("/Admin/DefectCatalogImport", form);
    }

    /// <summary>Edits one tolerance cell of a downloaded workbook, returns the bytes.</summary>
    private static byte[] WithTolerance(ClosedXML.Excel.XLWorkbook wb, int defectId, string? value)
    {
        var ws = wb.Worksheet("Defect catalog");
        // The sheet ships protected so only the tolerance column is editable by
        // hand; a test writing through the API has to lift that first.
        ws.Unprotect();
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            if (row.Cell(1).GetValue<int>() != defectId) continue;
            if (value is null) row.Cell(9).Clear(ClosedXML.Excel.XLClearOptions.Contents);
            else row.Cell(9).Value = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            break;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task The_export_carries_every_column_and_the_tolerance()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "6.25");
        try
        {
            using var wb = await DownloadAsync(client, "/Admin/DefectCatalogExcel?materialGroup=APPLE");
            var ws = wb.Worksheet("Defect catalog");

            var headers = ws.Row(1).CellsUsed().Select(x => x.GetString()).ToList();
            _out.WriteLine("headers: " + string.Join(" | ", headers));
            foreach (var h in new[] { "Defect ID", "Material group", "Code", "Name",
                                      "Category", "Value type", "Sort order", "Active", "Tolerance" })
                Assert.Contains(h, headers);

            var mine = ws.RowsUsed().Skip(1).FirstOrDefault(x => x.Cell(1).GetValue<int>() == id);
            Assert.NotNull(mine);
            Assert.Equal(6.25m, mine!.Cell(9).GetValue<decimal>());
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// The whole point: export, change a tolerance in the sheet, upload the
    /// same file, and the catalog reflects it.
    /// </summary>
    [Fact]
    public async Task A_tolerance_edited_in_the_sheet_comes_back_on_upload()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "1.00");
        try
        {
            using var wb = await DownloadAsync(client, "/Admin/DefectCatalogExcel?materialGroup=APPLE");
            var res = await UploadAsync(client, WithTolerance(wb, id, "8.75"));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

            var stored = await ToleranceOfAsync(id);
            _out.WriteLine($"after upload: {stored}");
            Assert.Equal(8.75m, stored);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>Clearing the cell clears the tolerance rather than zeroing it.</summary>
    [Fact]
    public async Task An_emptied_cell_clears_the_tolerance()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "3.50");
        try
        {
            using var wb = await DownloadAsync(client, "/Admin/DefectCatalogExcel?materialGroup=APPLE");
            await UploadAsync(client, WithTolerance(wb, id, null));
            Assert.Null(await ToleranceOfAsync(id));
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// Re-uploading an untouched export must change nothing. If it did, every
    /// round trip would rewrite hundreds of rows and fill the audit log with
    /// edits nobody made.
    /// </summary>
    [Fact]
    public async Task Re_uploading_an_unchanged_export_changes_nothing()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "2.25");
        try
        {
            var res = await client.GetAsync("/Admin/DefectCatalogExcel?materialGroup=APPLE");
            var bytes = await res.Content.ReadAsByteArrayAsync();

            var before = await ToleranceOfAsync(id);
            await UploadAsync(client, bytes);
            var after = await ToleranceOfAsync(id);

            _out.WriteLine($"{before} -> {after}");
            Assert.Equal(before, after);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>
    /// A file with one unreadable cell applies NOTHING. A half-applied import
    /// across hundreds of defects cannot be reasoned about afterwards, so it
    /// is refused whole.
    /// </summary>
    [Fact]
    public async Task One_bad_cell_stops_the_whole_import()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "5.00");
        try
        {
            using var wb = await DownloadAsync(client, "/Admin/DefectCatalogExcel?materialGroup=APPLE");
            var ws = wb.Worksheet("Defect catalog");
            ws.Unprotect();

            // One good edit, and one row that cannot be read.
            foreach (var row in ws.RowsUsed().Skip(1))
                if (row.Cell(1).GetValue<int>() == id) { row.Cell(9).Value = 9.99m; break; }

            var extra = ws.LastRowUsed()!.RowNumber() + 1;
            ws.Cell(extra, 1).Value = 999999999;
            ws.Cell(extra, 9).Value = "not a number";

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var res = await UploadAsync(client, ms.ToArray());
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

            // The good edit did NOT land either.
            _out.WriteLine($"tolerance after a rejected import: {await ToleranceOfAsync(id)}");
            Assert.Equal(5.00m, await ToleranceOfAsync(id));
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>The import reads the tolerance and nothing else.</summary>
    [Fact]
    public async Task Editing_other_columns_in_the_sheet_changes_nothing()
    {
        var client = Client(_factory);
        var (id, code) = await SaveAsync(client, "4.00");
        try
        {
            using var wb = await DownloadAsync(client, "/Admin/DefectCatalogExcel?materialGroup=APPLE");
            var ws = wb.Worksheet("Defect catalog");
            ws.Unprotect();
            foreach (var row in ws.RowsUsed().Skip(1))
            {
                if (row.Cell(1).GetValue<int>() != id) continue;
                row.Cell(3).Value = "HACKED";
                row.Cell(4).Value = "Renamed";
                row.Cell(8).Value = "No";
                break;
            }
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            await UploadAsync(client, ms.ToArray());

            using var c = Db();
            await c.OpenAsync();
            var after = await Dapper.SqlMapper.QuerySingleAsync(c,
                "SELECT defect_code, defect_name, is_active FROM qms_defect_catalog WHERE defect_id = @id",
                new { id });
            _out.WriteLine($"code {after.defect_code}, active {after.is_active}");
            Assert.Equal(code, (string)after.defect_code);
            Assert.True((bool)after.is_active);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>Anything that is not the exported workbook is refused politely.</summary>
    [Fact]
    public async Task A_file_that_is_not_the_export_is_refused()
    {
        var client = Client(_factory);

        var res = await UploadAsync(client, new byte[] { 1, 2, 3, 4 }, "notes.txt");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var res2 = await UploadAsync(client, new byte[] { 1, 2, 3, 4 }, "broken.xlsx");
        Assert.Equal(HttpStatusCode.Redirect, res2.StatusCode);
    }
}
