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
/// first is precision: the column keeps one decimal, so a value typed with
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
    /// One decimal place, and rounded rather than truncated -- 2.55 is nearer
    /// 2.6 than 2.5, and an administrator quoting a tolerance to the supplier
    /// should get the value they would have written themselves.
    /// </summary>
    [Theory]
    [InlineData("2.5",  2.5)]
    [InlineData("0.5",  0.5)]
    [InlineData("0",    0.0)]
    [InlineData("10",  10.0)]
    [InlineData("2.55", 2.6)]
    [InlineData("2.44", 2.4)]
    public async Task A_tolerance_is_kept_to_one_decimal(string typed, double expected)
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
        var (id, code) = await SaveAsync(client, "3.5");
        try
        {
            Assert.Equal(3.5m, await ToleranceOfAsync(id));

            await SaveAsync(client, "1.2", code, id);
            Assert.Equal(1.2m, await ToleranceOfAsync(id));

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
        var (id, code) = await SaveAsync(client, "4.5");
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

            var forGroup = await qos.GetActiveDefectsForGroupAsync("APPLE");
            var mine = forGroup.FirstOrDefault(d => d.DefectId == id);
            Assert.NotNull(mine);
            _out.WriteLine($"{mine!.DefectCode}: tolerance {mine.Tolerance}");
            Assert.Equal(4.5m, mine.Tolerance);

            var all = await qos.GetActiveDefectsAsync();
            Assert.Equal(4.5m, all.First(d => d.DefectId == id).Tolerance);
        }
        finally { await CleanupAsync(code); }
    }

    /// <summary>And it is on the screen, in the grid and in the edit form.</summary>
    [Fact]
    public async Task The_catalog_page_shows_the_tolerance()
    {
        var client = Client(_factory);
        var (_, code) = await SaveAsync(client, "7.5");
        try
        {
            var html = await client.GetStringAsync("/Admin/DefectCatalog?materialGroup=APPLE");
            Assert.Contains("7.5", html);
            Assert.Contains("name=\"tolerance\"", html);
            Assert.Contains("step=\"0.1\"", html);
        }
        finally { await CleanupAsync(code); }
    }
}
