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

    /// <summary>A recipient with no plant or procurement-type restriction —
    /// how every recipient behaved before scoping existed.</summary>
    private static NotifyRecipientInput NoScope(int userId) =>
        new(userId, Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// The recipient list AS IT STANDS, scope included.
    ///
    /// This suite runs against the live database, so every test that touches
    /// the notification list has to put back exactly what it found. Capturing
    /// only the user ids and restoring them with NoScope silently cleared every
    /// recipient's plant and procurement selections -- and because an empty
    /// scope means "everything", the result was not an obvious blank screen but
    /// fifty people quietly being mailed about every plant. Restore the whole
    /// row or do not restore at all.
    /// </summary>
    private static async Task<NotifyRecipientInput[]> CaptureAsync(IQoFinishNotifier notifier) =>
        (await notifier.ListCandidatesAsync())
            .Where(p => p.Selected)
            .Select(p => new NotifyRecipientInput(p.UserId, p.Plants.ToArray(), p.PoTypes.ToArray()))
            .ToArray();

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

        var before = await CaptureAsync(notifier);
        try
        {
            var pick = (await notifier.ListCandidatesAsync())
                .Where(p => p.CanBeMailed).Take(2).Select(p => p.UserId).ToArray();
            if (pick.Length == 0) return;   // no mailable users in this database

            await notifier.SaveRecipientsAsync(pick.Select(NoScope), "test");
            var after = (await notifier.ListCandidatesAsync()).Where(p => p.Selected).Select(p => p.UserId).ToArray();
            Assert.Equal(pick.OrderBy(x => x), after.OrderBy(x => x));

            // Saving nothing is a real choice -- it is how the notification is
            // turned off -- and must clear the list rather than be ignored.
            await notifier.SaveRecipientsAsync(Array.Empty<NotifyRecipientInput>(), "test");
            Assert.DoesNotContain(await notifier.ListCandidatesAsync(), p => p.Selected);
        }
        finally
        {
            await notifier.SaveRecipientsAsync(before, "test-restore");
        }
    }

    [Fact]
    public async Task Scope_survives_a_save_and_empty_still_means_everything()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();

        var before = await CaptureAsync(notifier);
        try
        {
            var options = await notifier.GetScopeOptionsAsync();
            var people  = (await notifier.ListCandidatesAsync()).Where(p => p.CanBeMailed).Take(2).ToArray();
            if (people.Length < 2 || options.Plants.Count == 0 || options.PoTypes.Count == 0) return;

            var plant  = options.Plants[0].Code;
            var poType = options.PoTypes[0].Code;

            await notifier.SaveRecipientsAsync(new[]
            {
                new NotifyRecipientInput(people[0].UserId, new[] { plant }, new[] { poType }),
                // Deliberately unscoped: this is the default, and it must stay
                // empty rather than being back-filled with "everything", or the
                // screen would show a narrowed recipient who is not narrowed.
                new NotifyRecipientInput(people[1].UserId, Array.Empty<string>(), Array.Empty<string>()),
            }, "test");

            var after = await notifier.ListCandidatesAsync();
            var scoped   = after.First(p => p.UserId == people[0].UserId);
            var unscoped = after.First(p => p.UserId == people[1].UserId);

            Assert.Equal(new[] { plant },  scoped.Plants);
            Assert.Equal(new[] { poType }, scoped.PoTypes);
            Assert.Empty(unscoped.Plants);
            Assert.Empty(unscoped.PoTypes);

            // Blank values from an empty form field must not become rows.
            await notifier.SaveRecipientsAsync(new[]
            {
                new NotifyRecipientInput(people[0].UserId, new[] { "", "  " }, new[] { "" }),
            }, "test");
            var cleaned = (await notifier.ListCandidatesAsync()).First(p => p.UserId == people[0].UserId);
            Assert.Empty(cleaned.Plants);
            Assert.Empty(cleaned.PoTypes);
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

        var rows = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
        var qoId = rows.Where(r => r.StatusCode == QualityOrderStatus.Closed)
                       .Select(r => r.QualityOrderId).FirstOrDefault();
        if (qoId == 0) return;

        var built = await notifier.BuildPreviewAsync(qoId);
        Assert.NotNull(built);
        var (subject, html) = built!.Value;

        // The subject is built by QcSubjectLine: the short QC number, the
        // state, and the claim assessment when there is one -- e.g.
        // "QC 1213 - Finished - No Potential Claim". Assert on what it must
        // CARRY rather than on a fixed phrase, or every future refinement of
        // the wording reads as a regression.
        Assert.Contains("QC", subject);
        Assert.Contains("Finished", subject);
        // The basics the reader needs before opening the system at all. Assert on
        // the VALUE, not a field label: the order number moved into the header
        // band during the redesign and a label check would have called that a
        // regression when nothing was actually lost.
        var qo = await scope.ServiceProvider.GetRequiredService<IQualityOrderService>().GetAsync(qoId);
        Assert.Contains(qo!.QualityOrderNo, html);
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
        var rows = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
        long pick = 0;
        // An in-progress reinspection can head this list; it has no samples,
        // so the loop skips it, but be explicit rather than lucky.
        foreach (var r in rows.Where(r => r.StatusCode == QualityOrderStatus.Closed).Take(25))
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

        var rows = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
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

        var before = await CaptureAsync(notifier);
        try
        {
            await notifier.SaveRecipientsAsync(Array.Empty<NotifyRecipientInput>(), "test");
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

    /// <summary>
    /// The regression that cost every recipient their plant and procurement
    /// selections on production: capture the list, put it back, and the scope
    /// must survive. It did not, because the capture kept only the user ids and
    /// the restore wrote an empty scope -- which does not read as "blank", it
    /// reads as "notify me about everything".
    /// </summary>
    [Fact]
    public async Task Capturing_and_restoring_the_list_keeps_each_persons_scope()
    {
        using var scope = Scope();
        var notifier = scope.ServiceProvider.GetRequiredService<IQoFinishNotifier>();

        var before = await CaptureAsync(notifier);
        try
        {
            var options = await notifier.GetScopeOptionsAsync();
            var pick = (await notifier.ListCandidatesAsync())
                .Where(p => p.CanBeMailed).Take(2).Select(p => p.UserId).ToArray();
            if (pick.Length == 0 || options.Plants.Count == 0) return;

            var plant = options.Plants[0].Code;
            await notifier.SaveRecipientsAsync(
                pick.Select(id => new NotifyRecipientInput(id, new[] { plant }, Array.Empty<string>())),
                "test");

            // Round-trip through the same pair of calls every test uses.
            var captured = await CaptureAsync(notifier);
            await notifier.SaveRecipientsAsync(Array.Empty<NotifyRecipientInput>(), "test");
            await notifier.SaveRecipientsAsync(captured, "test");

            var after = await CaptureAsync(notifier);
            Assert.Equal(pick.Length, after.Length);
            Assert.All(after, r =>
            {
                Assert.Single(r.Plants);
                Assert.Equal(plant, r.Plants[0]);
            });
        }
        finally { await notifier.SaveRecipientsAsync(before, "test-restore"); }
    }
}
