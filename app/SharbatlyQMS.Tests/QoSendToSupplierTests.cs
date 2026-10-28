using System.Net;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The envelope button on the Quality Orders list and Details: e-mails the
/// QC report to the supplier, turns green once sent, works only on finished
/// orders and needs its own permission (Admin by default). Mail never leaves
/// the test host (RecordingEmailService); every send row a test writes is
/// removed again.
/// </summary>
[Collection("workflow")]
public class QoSendToSupplierTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public QoSendToSupplierTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        return new Microsoft.Data.SqlClient.SqlConnection(
            s.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
    }

    private HttpClient Client(string role)
    {
        var c = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return c;
    }

    private async Task<long?> OrderAsync(bool finished)
    {
        using var c = Db();
        return await c.ExecuteScalarAsync<long?>(finished
            ? @"SELECT TOP 1 quality_order_id FROM qms_quality_order
                WHERE status_code = 'Closed' AND superseded_at IS NULL AND archived_at IS NULL
                ORDER BY quality_order_id DESC"
            : @"SELECT TOP 1 quality_order_id FROM qms_quality_order
                WHERE status_code IN ('Open', 'Initial') AND superseded_at IS NULL ORDER BY quality_order_id DESC");
    }

    private static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = System.Text.RegularExpressions.Regex.Match(html, "id=\"srm-token\"[^>]*>\\s*<input[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, "The page carries no send-dialog token.");
        return m.Groups[1].Value;
    }

    private static FormUrlEncodedContent Form(string token, long id) => new(new[]
    {
        new KeyValuePair<string, string>("__RequestVerificationToken", token),
        new KeyValuePair<string, string>("id", id.ToString()),
        new KeyValuePair<string, string>("to", "qa-supplier@example.com"),
        new KeyValuePair<string, string>("subject", "Test report"),
        new KeyValuePair<string, string>("body", "Test body"),
    });

    [Fact]
    public void Administrator_holds_the_new_permission_and_other_roles_do_not_by_default()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        perms.EnsureLoadedAsync().GetAwaiter().GetResult();
        Assert.True(perms.RoleHas(RoleCodes.Admin, Perm.Qo.SendToSupplier, AccessLevel.Edit, isScreen: false));
        foreach (var r in new[] { RoleCodes.Supervisor, RoleCodes.Operator, RoleCodes.Viewer })
            Assert.False(perms.RoleHas(r, Perm.Qo.SendToSupplier, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public async Task Sending_from_the_list_records_the_send_and_turns_the_envelope_green()
    {
        var id = await OrderAsync(finished: true);
        if (id is null) return;
        using var c = Db();
        await c.OpenAsync();
        var before = await c.ExecuteScalarAsync<long>("SELECT ISNULL(MAX(send_id), 0) FROM qms_qo_report_send");
        try
        {
            var client = Client(RoleCodes.Admin);
            var token = await TokenAsync(client, "/QualityOrders");
            var mails = RecordingEmailService.Instance.Outbox.Count;
            var res = await client.PostAsync("/Reports/SendFromQo", Form(token, id.Value));
            var body = await res.Content.ReadAsStringAsync();
            _out.WriteLine($"{(int)res.StatusCode} {body}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("\"ok\":true", body);
            Assert.Equal(mails + 1, RecordingEmailService.Instance.Outbox.Count);

            var row = await c.QuerySingleAsync<(string To, string By)>(
                "SELECT to_addresses, sent_by FROM qms_qo_report_send WHERE quality_order_id = @id AND send_id > @before",
                new { id, before });
            Assert.Equal("qa-supplier@example.com", row.To);

            using var scope = _factory.Services.CreateScope();
            var qo = await scope.ServiceProvider.GetRequiredService<IQualityOrderService>().GetAsync(id.Value);
            Assert.NotNull(qo!.ReportSentAt);
            Assert.Equal("qa-supplier@example.com", qo.ReportSentTo);

            // The Details page now shows the green envelope for it.
            var details = await client.GetStringAsync($"/QualityOrders/Details/{id}");
            Assert.Contains("bi-envelope-check-fill", details);
        }
        finally
        {
            await c.ExecuteAsync("DELETE FROM qms_qo_report_send WHERE send_id > @before", new { before });
        }
    }

    [Theory]
    [InlineData(RoleCodes.Operator)]
    [InlineData(RoleCodes.Supervisor)]
    public async Task Roles_without_the_permission_are_refused(string role)
    {
        var id = await OrderAsync(finished: true);
        if (id is null) return;
        var admin = Client(RoleCodes.Admin);
        var token = await TokenAsync(admin, "/QualityOrders");
        var client = Client(role);
        var res = await client.PostAsync("/Reports/SendFromQo", Form(token, id.Value));
        _out.WriteLine($"{role} -> {(int)res.StatusCode}");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);

        // And the list does not offer them the button at all.
        var list = await client.GetStringAsync("/QualityOrders");
        Assert.DoesNotContain("data-url-send=\"/Reports/SendFromQo\"", list);
    }

    [Fact]
    public async Task An_unfinished_order_cannot_be_sent_and_its_button_is_disabled()
    {
        var id = await OrderAsync(finished: false);
        if (id is null) return;
        var client = Client(RoleCodes.Admin);
        var token = await TokenAsync(client, "/QualityOrders");
        var res = await client.PostAsync("/Reports/SendFromQo", Form(token, id.Value));
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":false", body);

        var details = await client.GetStringAsync($"/QualityOrders/Details/{id}");
        var m = System.Text.RegularExpressions.Regex.Match(details,
            "<button[^>]*data-url-send=\"/Reports/SendFromQo\"[^>]*>");
        Assert.True(m.Success, "The Details page has no send button for an administrator.");
        Assert.Contains("disabled", m.Value);
    }
}
