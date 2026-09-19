using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Choosing extra documents to send with the QC report.
///
/// The one that matters is the last: the document ids come from the browser, so
/// the send path has to prove each belongs to this order or its arrival. Without
/// that check anyone who may e-mail one supplier could mail out any document in
/// the system by guessing a number.
/// </summary>
[Collection("workflow")]
public class SupplierMailAttachmentTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SupplierMailAttachmentTests(QmsAppFactory f) => _factory = f;

    /// <summary>A closed, non-archived order — the only kind that can be sent.</summary>
    private async Task<QualityOrder?> SendableQoAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var rows   = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
        foreach (var r in rows.Take(30))
        {
            var qo = await qos.GetAsync(r.QualityOrderId);
            if (qo is { StatusCode: QualityOrderStatus.Closed, IsArchived: false }) return qo;
        }
        return null;
    }

    /// <summary>Pulls a usable antiforgery token (and its cookie, which the
    /// client keeps) out of a rendered page.</summary>
    private static async Task<string> AntiforgeryTokenAsync(HttpClient client, string pageUrl)
    {
        var html = await client.GetStringAsync(pageUrl);
        var m = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(m.Success, $"No antiforgery token found on {pageUrl}.");
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task Prepare_offers_documents_from_the_order_and_its_arrival()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var qo = await SendableQoAsync();
        if (qo == null) return;

        var res = await _factory.CreateClient()
            .GetAsync($"/Reports/PrepareSendQualityReport?id={qo.QualityOrderId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        // The dialog cannot offer a picker it was never told about.
        Assert.True(doc.RootElement.TryGetProperty("documents", out var docs));
        Assert.Equal(JsonValueKind.Array, docs.ValueKind);

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDocumentService>();
        var expected = (await svc.ListAsync("QualityOrder", qo.QualityOrderId)).Count
                     + (await svc.ListAsync("Arrival", qo.ArrivalId)).Count;
        Assert.Equal(expected, docs.GetArrayLength());

        foreach (var d in docs.EnumerateArray())
        {
            Assert.Contains(d.GetProperty("source").GetString(), new[] { "Quality order", "Arrival" });
            Assert.False(string.IsNullOrWhiteSpace(d.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task Sending_refuses_a_document_that_belongs_to_another_record()
    {
        TestAuthHandler.Role = RoleCodes.Admin;
        var qo = await SendableQoAsync();
        if (qo == null) return;

        // A real document id that is NOT on this order or its arrival. Using a
        // real one matters: a made-up number could be refused simply for not
        // existing, which would not prove the ownership check runs.
        long foreignId = 0;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDocumentService>();
            var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var mine = (await svc.ListAsync("QualityOrder", qo.QualityOrderId))
                .Concat(await svc.ListAsync("Arrival", qo.ArrivalId))
                .Select(d => d.DocumentId).ToHashSet();

            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
            foreach (var r in (await claims.ListClosedQosAsync(
                                 new ClaimListFilter { PageSize = 100 },
                                 PlantScope.All, "test")).Rows.Take(40))
            {
                if (r.QualityOrderId == qo.QualityOrderId) continue;
                var other = await qos.GetAsync(r.QualityOrderId);
                if (other == null) continue;
                var hit = (await svc.ListAsync("QualityOrder", other.QualityOrderId))
                    .Concat(await svc.ListAsync("Arrival", other.ArrivalId))
                    .FirstOrDefault(d => !mine.Contains(d.DocumentId));
                if (hit != null) { foreignId = hit.DocumentId; break; }
            }
        }
        if (foreignId == 0) return;   // no other record has a document to borrow

        // The endpoint validates an antiforgery token, so post a real one from a
        // real page -- otherwise the test would only prove that antiforgery works.
        var client = _factory.CreateClient();
        var token  = await AntiforgeryTokenAsync(client, $"/QualityOrders/Details/{qo.QualityOrderId}");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = qo.QualityOrderId.ToString(),
            ["to"] = "nobody@example.invalid",
            ["subject"] = "test",
            ["body"] = "test",
            ["documentIds"] = foreignId.ToString(),
            ["__RequestVerificationToken"] = token,
        });
        var res = await client.PostAsync("/Reports/SendQualityReport", form);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Refused BEFORE any send is attempted, and said so in words the sender
        // can act on.
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("does not belong", doc.RootElement.GetProperty("error").GetString()!);
    }
}
