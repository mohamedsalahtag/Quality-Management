using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The "quality order finished" notification.
///
/// What these guard: the mail goes out unattended, so nobody sees it fail. A
/// body that silently loses the QC summary, or a recipient list that keeps
/// mailing someone who left, would both go unnoticed for a long time.
///
/// In the "workflow" collection because saving recipients writes to a real
/// table and the smoke tests share the process-wide auth role.
/// </summary>
[Collection("workflow")]
public class QoFinishNotifierTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public QoFinishNotifierTests(QmsAppFactory f) => _factory = f;

    private IServiceScope Scope() => _factory.Services.CreateScope();

    [Fact]
    public async Task Candidates_flag_who_can_actually_be_mailed()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();

        var people = await notifier.ListCandidatesAsync();

        Assert.NotEmpty(people);
        // CanBeMailed must track the address, not the account state -- the screen
        // uses it to explain why somebody is not selectable.
        Assert.All(people, p =>
            Assert.Equal(!string.IsNullOrWhiteSpace(p.Email), p.CanBeMailed));
    }

    [Fact]
    public async Task Saving_recipients_replaces_the_list_and_survives_a_reload()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();

        var before = (await notifier.ListCandidatesAsync()).Where(p => p.Selected).Select(p => p.UserId).ToArray();
        try
        {
            var pick = (await notifier.ListCandidatesAsync())
                .Where(p => p.CanBeMailed).Take(2).Select(p => p.UserId).ToArray();
            if (pick.Length == 0) return;   // no mailable users in this database

            await notifier.SaveRecipientsAsync(pick, "test");
            var after = (await notifier.ListCandidatesAsync()).Where(p => p.Selected).Select(p => p.UserId).ToArray();
            Assert.Equal(pick.OrderBy(x => x), after.OrderBy(x => x));

            // Saving nothing is a real choice -- it is how the notification is
            // turned off -- and must clear the list rather than be ignored.
            await notifier.SaveRecipientsAsync(Array.Empty<int>(), "test");
            Assert.DoesNotContain(await notifier.ListCandidatesAsync(), p => p.Selected);
        }
        finally
        {
            await notifier.SaveRecipientsAsync(before, "test-restore");
        }
    }

    [Fact]
    public async Task Mail_body_carries_the_basics_and_the_qc_summary()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();
        var claims   = scope.ServiceProvider.GetRequiredService<IClaimService>();

        var rows = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
        var qoId = rows.Select(r => r.QualityOrderId).FirstOrDefault();
        if (qoId == 0) return;

        var built = await notifier.BuildPreviewAsync(qoId);
        Assert.NotNull(built);
        var (subject, html) = built!.Value;

        Assert.Contains("QC finished", subject);
        // The basics the reader needs before opening the system at all.
        Assert.Contains("QC number", html);
        Assert.Contains("Supplier", html);
        Assert.Contains("Shipment", html);
        // And the reason the mail exists: the result, not just a "it finished".
        Assert.True(html.Contains("inspected") || html.Contains("No defects recorded"),
            "The body must carry the QC summary, not only the header fields.");
        // Never leak raw markup from a supplier name into the body.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mail_body_carries_the_full_summary_the_qo_page_shows()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();
        var claims   = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

        // A finished order that actually recorded defects, so "full summary"
        // means something -- an order with none would pass a weaker assertion.
        var rows = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
        long pick = 0;
        foreach (var r in rows.Take(25))
        {
            var mats = (await qos.GetMaterialsAsync(r.QualityOrderId)).ToList();
            if (mats.Count == 0) continue;
            var sums = await qos.BuildGroupSummariesAsync(r.QualityOrderId, mats, await qos.GetReportUnitsAsync());
            if (sums.Any(g => g.DefectSections.Any(sec => sec.Rows.Any(x => x.SumValue > 0)))) { pick = r.QualityOrderId; break; }
        }
        if (pick == 0) return;

        var (_, html) = (await notifier.BuildPreviewAsync(pick))!.Value;

        Assert.Contains("QC summary", html);
        Assert.Contains("inspected", html);
        // Percentages are the reason the summary exists: a count without its
        // share of the sample size cannot be judged.
        Assert.Contains("%", html);
        Assert.Contains("Procurement type", html);
    }

    [Fact]
    public async Task Mail_highlights_the_finish_comment_and_links_to_the_order()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();
        var claims   = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

        var rows = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
        long pick = 0; string? comment = null;
        foreach (var r in rows.Take(40))
        {
            var qo = await qos.GetAsync(r.QualityOrderId);
            if (qo != null && !string.IsNullOrWhiteSpace(qo.CloseReason))
            { pick = r.QualityOrderId; comment = qo.CloseReason; break; }
        }
        if (pick == 0) return;

        var (_, html) = (await notifier.BuildPreviewAsync(pick))!.Value;

        // The comment is the decision criterion the reader acts on, so it has to
        // be in the body and called out, not buried.
        Assert.Contains("note on finishing", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(comment!), html);
        // And a way straight into the order, so nobody hunts for it.
        Assert.Contains($"/QualityOrders/Details/{pick}", html);
    }

    [Fact]
    public async Task Notifying_with_nobody_selected_sends_nothing()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();

        var before = (await notifier.ListCandidatesAsync()).Where(p => p.Selected).Select(p => p.UserId).ToArray();
        try
        {
            await notifier.SaveRecipientsAsync(Array.Empty<int>(), "test");
            // An empty list is how the feature is switched off; it must not fall
            // back to "mail everyone", and must not throw.
            Assert.Equal(0, await notifier.NotifyFinishedAsync(1, "test"));
        }
        finally
        {
            await notifier.SaveRecipientsAsync(before, "test-restore");
        }
    }

    [Fact]
    public async Task Notifying_a_missing_order_never_throws()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();
        // Finishing already succeeded by the time this runs, so nothing here may
        // surface as an error.
        Assert.Equal(0, await notifier.NotifyFinishedAsync(long.MaxValue, "test"));
    }
}
