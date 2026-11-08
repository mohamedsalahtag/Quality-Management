using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Parameters -> Supplier contacts (M42): the address parser, the workbook
/// import rules, the screen's permissions, and the send dialog filling To from
/// the list. Rows the tests create are removed again; addresses use the
/// .invalid domain so nothing can ever be delivered to them.
/// </summary>
[Collection("workflow")]
public class SupplierContactsTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SupplierContactsTests(QmsAppFactory f) => _factory = f;

    private string Cs => _factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")!;

    // ---- parsing ----------------------------------------------------------

    [Theory]
    // Real cells from "Supplier Contacts - Updated.xlsx".
    [InlineData("qcr@nee.com.au'; 'Richard Hunt' <Richard.Hunt@nee.com.au>; 'vincent@nee.com.au'",
                "qcr@nee.com.au|richard.hunt@nee.com.au|vincent@nee.com.au")]
    [InlineData(": Kayleigh van der Arend <k.vanderarend@globalgreenteam.nl>; Frans van der Hout <F.vanderHout@globalgreenteam.nl>",
                "k.vanderarend@globalgreenteam.nl|f.vanderhout@globalgreenteam.nl")]
    [InlineData("darapova@camposol.eu>; Sales <sales@camposol.eu>", "darapova@camposol.eu|sales@camposol.eu")]
    [InlineData("moe.sherkawi@h2atrading.com.au; a.sherkawi@h2atrading.com.au;admin@h2atrading.com.au",
                "moe.sherkawi@h2atrading.com.au|a.sherkawi@h2atrading.com.au|admin@h2atrading.com.au")]
    [InlineData("a@x.com\nA@X.com\r\nb@x.com", "a@x.com|b@x.com")]
    public void Parse_finds_every_address_in_pasted_text(string text, string expected)
    {
        var (valid, invalid) = SupplierEmails.Parse(text);
        Assert.Equal(expected.Split('|'), valid);
        Assert.Empty(invalid);
    }

    [Fact]
    public void Parse_reports_a_broken_address_and_ignores_names()
    {
        var (valid, invalid) = SupplierEmails.Parse("Sales team; good@x.com; broken@nowhere");
        Assert.Equal(new[] { "good@x.com" }, valid);
        Assert.Equal(new[] { "broken@nowhere" }, invalid);
    }

    // ---- import rules -----------------------------------------------------

    private static MemoryStream Workbook(params object?[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Shipper Contacts Updated");
        var header = new[] { "Supplier code", "Origin", "SUPPLIER NAME", "E-mail Address 1", "E-mail Address 2", "Contact Person Name" };
        for (var c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] is { } v)
                    ws.Cell(r + 2, c + 1).Value = v is double d ? d : v.ToString();
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Import_skips_rows_without_a_code_and_codes_on_several_rows()
    {
        using var xlsx = Workbook(
            new object?[] { 80000444d, "Argentina", "Frudies", "info@frudies.com", null, "Fernando" },
            new object?[] { null, "Argentina", "BLACKBIRD", "ignacio@blackbird.com.ar" },
            new object?[] { 80000575d, "Philippines", "Mench", "a@mench.ph" },
            new object?[] { "80000575", "Philippines", "Questa", "b@questa.ph" },
            new object?[] { 8000272d, "India", "Harbour Fresh", "x@harbour.in" },
            new object?[] { 8000999d, "India", "Typo", "y@typo.in" },
            new object?[] { 80000025d, "Turkey", "GULBUDAK", null, null });

        var (rows, error) = SupplierImport.Read(xlsx);
        Assert.Null(error);
        Assert.Equal(7, rows.Count);

        var (accepted, skipped) = SupplierImport.Plan(rows, code => code == "80000272");

        // A code with no address is still a supplier -- it goes on the list.
        Assert.Equal(new[] { "80000444", "80000272", "80000025" }, accepted.Select(a => a.Code));
        Assert.Equal("Fernando", accepted[0].ContactPerson);
        Assert.Equal(new[] { "info@frudies.com" }, accepted[0].Emails);

        Assert.Contains(skipped, s => s.RowNo == 3 && s.Reason == "No supplier code");
        Assert.Equal(2, skipped.Count(s => s.Code == "80000575" && s.Reason == "Code is on more than one row"));
        // The 7-digit fix applies only when SAP knows the corrected code.
        Assert.Contains(skipped, s => s.Code == "8000999" && s.Reason.Contains("not in SAP"));
    }

    [Fact]
    public void Import_refuses_a_workbook_without_the_code_column()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("x").Cell(1, 1).Value = "Something else";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        var (rows, error) = SupplierImport.Read(ms);
        Assert.Empty(rows);
        Assert.Contains("Supplier code", error);
    }

    [Fact]
    public async Task Import_adds_only_once_when_the_same_rows_are_loaded_twice()
    {
        var code = "ZZT" + Random.Shared.Next(100000, 999999);
        var rows = new[]
        {
            new SupplierImportRow(2, code, "Testland", "Import test supplier", "Tester",
                new[] { "one@example.invalid", "two@example.invalid" })
        };
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISupplierContactService>();
        try
        {
            var first = await svc.ImportAsync(rows, "test");
            Assert.Equal(1, first.SuppliersAdded);
            Assert.Equal(2, first.EmailsAdded);

            var again = await svc.ImportAsync(rows, "test");
            Assert.Equal(0, again.SuppliersAdded);
            Assert.Equal(0, again.EmailsAdded);

            var s = await svc.GetAsync(code);
            Assert.NotNull(s);
            Assert.Equal("Import test supplier", s!.Name);   // SAP does not know the code
            Assert.Equal("Testland", s.Origin);
            Assert.Equal(new[] { "one@example.invalid", "two@example.invalid" }, s.Emails);
        }
        finally { await svc.DeleteAsync(code); }
    }

    // ---- the screen -------------------------------------------------------

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, $"No antiforgery token found on {url}.");
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task Operator_can_open_the_screen_but_not_edit_it()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        var token  = await TokenAsync(client, "/Admin/SupplierContacts");

        var get = new HttpRequestMessage(HttpMethod.Get, "/Admin/SupplierContacts");
        get.Headers.Add(TestAuthHandler.RoleHeader, RoleCodes.Operator);
        var page = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SaveSupplierContact", html);   // no edit dialog for a viewer

        var post = new HttpRequestMessage(HttpMethod.Post, "/Admin/SaveSupplierContact")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["isNew"] = "true", ["vendorNo"] = "ZZT000001", ["name"] = "x", ["isActive"] = "true",
                ["__RequestVerificationToken"] = token,
            })
        };
        post.Headers.Add(TestAuthHandler.RoleHeader, RoleCodes.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(post)).StatusCode);
    }

    [Fact]
    public async Task Admin_adds_changes_and_removes_a_supplier_and_its_addresses()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var code   = "ZZT" + Random.Shared.Next(100000, 999999);
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISupplierContactService>();

        async Task<HttpStatusCode> Post(string action, Dictionary<string, string> form)
        {
            form["__RequestVerificationToken"] = await TokenAsync(client, "/Admin/SupplierContacts");
            return (await client.PostAsync(action, new FormUrlEncodedContent(form))).StatusCode;
        }

        try
        {
            Assert.Equal(HttpStatusCode.Redirect, await Post("/Admin/SaveSupplierContact", new()
            {
                ["isNew"] = "true", ["vendorNo"] = code, ["name"] = "Screen test supplier", ["origin"] = "Testland",
                ["isActive"] = "true", ["emails"] = "a@example.invalid\nSales <b@example.invalid>",
            }));
            Assert.Equal(new[] { "a@example.invalid", "b@example.invalid" }, (await svc.GetAsync(code))!.Emails);

            // A bad address refuses the whole save.
            await Post("/Admin/SaveSupplierContact", new()
            {
                ["isNew"] = "false", ["vendorNo"] = code, ["name"] = "Screen test supplier",
                ["isActive"] = "true", ["emails"] = "b@example.invalid\nbroken@nowhere",
            });
            Assert.Equal(2, (await svc.GetAsync(code))!.Emails.Count);

            // Remove one, add one, deactivate.
            await Post("/Admin/SaveSupplierContact", new()
            {
                ["isNew"] = "false", ["vendorNo"] = code, ["name"] = "Renamed", ["isActive"] = "false",
                ["emails"] = "b@example.invalid; c@example.invalid",
            });
            var s = (await svc.GetAsync(code))!;
            Assert.Equal("Renamed", s.Name);
            Assert.False(s.IsActive);
            Assert.Equal(new[] { "b@example.invalid", "c@example.invalid" }, s.Emails);

            Assert.Equal(HttpStatusCode.Redirect, await Post("/Admin/DeleteSupplierContact", new() { ["vendorNo"] = code }));
            Assert.Null(await svc.GetAsync(code));
        }
        finally { await svc.DeleteAsync(code); }
    }

    [Fact]
    public async Task A_supplier_with_containers_cannot_be_deleted()
    {
        using var c = new SqlConnection(Cs);
        var vendorNo = await c.ExecuteScalarAsync<string>(
            "SELECT TOP 1 vendor_no FROM qms_arrival WHERE ISNULL(vendor_no, '') <> ''");
        Assert.NotNull(vendorNo);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISupplierContactService>();
        await svc.EnsureFromArrivalsAsync();

        Assert.Contains("stays on the list", await svc.DeleteAsync(vendorNo!));
        Assert.NotNull(await svc.GetAsync(vendorNo!));
    }

    [Fact]
    public async Task A_supplier_whose_container_arrived_is_added_to_the_list()
    {
        using var c = new SqlConnection(Cs);
        // One that carries nothing a person entered, so taking it off and letting
        // the service put it back loses nothing.
        var vendorNo = await c.ExecuteScalarAsync<string?>(@"
            SELECT TOP 1 s.vendor_no FROM qms_supplier s
            WHERE s.source = 'arrival' AND s.origin IS NULL AND s.contact_person IS NULL AND s.is_active = 1
              AND NOT EXISTS (SELECT 1 FROM qms_supplier_email e WHERE e.vendor_no = s.vendor_no)
              AND EXISTS (SELECT 1 FROM qms_arrival a WHERE a.vendor_no = s.vendor_no)");
        if (vendorNo == null) return;

        await c.ExecuteAsync("DELETE FROM qms_supplier WHERE vendor_no = @vendorNo", new { vendorNo });
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ISupplierContactService>();
        Assert.True(await svc.EnsureFromArrivalsAsync() >= 1);
        Assert.NotNull(await svc.GetAsync(vendorNo));
    }

    // ---- the send dialog --------------------------------------------------

    [Fact]
    public async Task Send_dialog_puts_every_address_in_To_and_remembers_new_ones()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        using var scope = _factory.Services.CreateScope();
        var sp     = scope.ServiceProvider;
        var claims = sp.GetRequiredService<IClaimService>();
        var qos    = sp.GetRequiredService<IQualityOrderService>();
        var arr    = sp.GetRequiredService<IArrivalService>();
        var svc    = sp.GetRequiredService<ISupplierContactService>();

        QualityOrder? qo = null; string? vendorNo = null;
        foreach (var r in (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows.Take(30))
        {
            var q = await qos.GetAsync(r.QualityOrderId);
            if (q is not { StatusCode: QualityOrderStatus.Closed, IsArchived: false, IsFrozen: false }) continue;
            vendorNo = (await arr.GetAsync(q.ArrivalId))?.VendorNo;
            if (!string.IsNullOrWhiteSpace(vendorNo)) { qo = q; break; }
        }
        if (qo == null) return;

        var mine = new[] { "qms-test-1@example.invalid", "qms-test-2@example.invalid", "qms-test-3@example.invalid" };
        try
        {
            await svc.AddEmailsAsync(vendorNo!, null, mine.Take(2), "test");

            var client = _factory.CreateClient();
            using (var doc = JsonDocument.Parse(await client.GetStringAsync($"/Reports/PrepareSendFromQo?id={qo.QualityOrderId}")))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                var to = MailAddresses.Split(doc.RootElement.GetProperty("to").GetString());
                Assert.Equal(await svc.GetEmailsAsync(vendorNo!), to);
                Assert.Contains(mine[0], to);
                Assert.Contains(mine[1], to);
                Assert.True(doc.RootElement.GetProperty("emailKnown").GetBoolean());
            }

            var token = await TokenAsync(client, $"/QualityOrders/Details/{qo.QualityOrderId}");
            var res = await client.PostAsync("/Reports/SaveSupplierEmailFromQo", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["vendorNo"] = vendorNo!,
                ["email"] = $"{mine[0]}; Third <{mine[2]}>",
                ["__RequestVerificationToken"] = token,
            }));
            using (var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(1, doc.RootElement.GetProperty("added").GetInt32());
            }
            Assert.Contains(mine[2], await svc.GetEmailsAsync(vendorNo!));
        }
        finally
        {
            using var c = new SqlConnection(Cs);
            await c.ExecuteAsync("DELETE FROM qms_supplier_email WHERE email IN @mine", new { mine });
        }
    }
}
