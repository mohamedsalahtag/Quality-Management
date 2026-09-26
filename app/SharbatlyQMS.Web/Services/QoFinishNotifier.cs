using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Pdf;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Emails a chosen set of people when a quality order is finished.
///
/// Recipients are stored as USERS, not addresses (see M22): the address then
/// follows the person's profile, a changed address never keeps mailing the old
/// one, and deactivating an account stops the mail without anyone remembering
/// to edit a list. An empty list means the notification is off — that is the
/// same thing as a disabled flag, with one fewer state to keep in step.
///
/// Sending never blocks or fails the transition that triggered it. Finishing a
/// quality order is the operator's work; a mail server being down must not undo
/// it or surface as an error on their screen.
/// </summary>
public interface IQoFinishNotifier
{
    /// <summary>Every QMS user who could be notified, each flagged with whether
    /// they currently are. Users with no email address are returned too, marked
    /// so the screen can explain why they cannot be selected.</summary>
    Task<IReadOnlyList<NotifyCandidate>> ListCandidatesAsync();

    /// <summary>Replaces the recipient list wholesale, scope included.</summary>
    Task SaveRecipientsAsync(IEnumerable<NotifyRecipientInput> recipients, string actor);

    /// <summary>Plants and procurement types the screen can offer, drawn from
    /// the arrivals that exist so an option always matches something.</summary>
    Task<NotifyScopeOptions> GetScopeOptionsAsync();

    /// <summary>Builds and sends the "quality order finished" mail. Best-effort:
    /// returns the number of addresses mailed, and never throws.</summary>
    Task<int> NotifyFinishedAsync(long qualityOrderId, string finishedBy, CancellationToken ct = default);

    /// <summary>The mail body for a quality order, for the settings screen's
    /// preview. Same builder the real send uses, so the preview cannot drift.</summary>
    Task<(string subject, string html)?> BuildPreviewAsync(long qualityOrderId);
}

/// <summary>What the screen posts back for one ticked person.</summary>
public sealed record NotifyRecipientInput(int UserId, string[] Plants, string[] PoTypes);

/// <summary>Choices offered by the notification screen's scope pickers.</summary>
public class NotifyScopeOptions
{
    /// <summary>Plant code -> display name.</summary>
    public IReadOnlyList<(string Code, string Name)> Plants  { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<(string Code, string Name)> PoTypes { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>One selectable person on the notification screen.</summary>
public class NotifyCandidate
{
    public int     UserId    { get; set; }
    public string  Username  { get; set; } = "";
    public string  FullName  { get; set; } = "";
    public string? Email     { get; set; }
    public string? RoleName  { get; set; }
    public bool    IsActive  { get; set; }
    public bool    Selected  { get; set; }

    /// <summary>Plants this person is notified for. EMPTY MEANS ALL — that is
    /// how every recipient behaved before scoping existed, so an untouched list
    /// keeps working rather than quietly going silent.</summary>
    public List<string> Plants  { get; set; } = new();
    /// <summary>Procurement types this person is notified for. Empty means all.</summary>
    public List<string> PoTypes { get; set; } = new();

    public bool    CanBeMailed => !string.IsNullOrWhiteSpace(Email);
}

public class QoFinishNotifier : IQoFinishNotifier
{
    private readonly string _cs;
    private readonly IDbService _db;
    private readonly IQualityOrderService _qos;
    private readonly IArrivalService _arrivals;
    private readonly IMaraService _mara;
    private readonly IEmailService _email;
    private readonly ICodeDescriptionDirectory _codes;
    private readonly ISettingsService _settings;
    private readonly ILogger<QoFinishNotifier> _log;

    public QoFinishNotifier(IConfiguration cfg, IDbService db, IQualityOrderService qos,
        IArrivalService arrivals, IMaraService mara, IEmailService email,
        ICodeDescriptionDirectory codes, ISettingsService settings,
        ILogger<QoFinishNotifier> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _db = db; _qos = qos; _arrivals = arrivals; _mara = mara;
        _email = email; _codes = codes; _settings = settings; _log = log;
    }

    // ---- Recipient list -----------------------------------------------------

    public async Task<IReadOnlyList<NotifyCandidate>> ListCandidatesAsync()
    {
        var users = await _db.ListUsersAsync(null, null, null);

        using var c = new SqlConnection(_cs);
        using var grid = await c.QueryMultipleAsync(@"
            SELECT user_id FROM qms_qo_notify_recipient;
            SELECT user_id AS UserId, plant   AS Value FROM qms_qo_notify_plant;
            SELECT user_id AS UserId, po_type AS Value FROM qms_qo_notify_potype;");
        var chosen  = (await grid.ReadAsync<int>()).ToHashSet();
        var plants  = (await grid.ReadAsync<ScopeRow>()).GroupBy(r => r.UserId)
                          .ToDictionary(g => g.Key, g => g.Select(r => r.Value).ToList());
        var poTypes = (await grid.ReadAsync<ScopeRow>()).GroupBy(r => r.UserId)
                          .ToDictionary(g => g.Key, g => g.Select(r => r.Value).ToList());

        return users
            .Select(u => new NotifyCandidate
            {
                UserId   = u.UserId,
                Username = u.Username,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName,
                Email    = u.Email,
                RoleName = u.RoleName,
                IsActive = u.IsActive,
                Selected = chosen.Contains(u.UserId),
                Plants   = plants.TryGetValue(u.UserId, out var pl) ? pl : new List<string>(),
                PoTypes  = poTypes.TryGetValue(u.UserId, out var pt) ? pt : new List<string>()
            })
            // Selected first so a long user list always opens on what is set.
            .OrderByDescending(x => x.Selected)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveRecipientsAsync(IEnumerable<NotifyRecipientInput> recipients, string actor)
    {
        var rows = recipients
            .GroupBy(r => r.UserId)          // one row per user even if the form repeats one
            .Select(g => g.First())
            .ToArray();

        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Replace wholesale inside one transaction: a half-applied list would
        // quietly mail the wrong people until someone noticed.
        await c.ExecuteAsync("DELETE FROM qms_qo_notify_plant",     transaction: tx);
        await c.ExecuteAsync("DELETE FROM qms_qo_notify_potype",    transaction: tx);
        await c.ExecuteAsync("DELETE FROM qms_qo_notify_recipient", transaction: tx);

        if (rows.Length > 0)
        {
            await c.ExecuteAsync(
                "INSERT INTO qms_qo_notify_recipient (user_id, added_by) VALUES (@UserId, @actor)",
                rows.Select(r => new { r.UserId, actor }), tx);

            var plantRows = rows.SelectMany(r => Clean(r.Plants).Select(p => new { r.UserId, plant = p })).ToList();
            if (plantRows.Count > 0)
                await c.ExecuteAsync(
                    "INSERT INTO qms_qo_notify_plant (user_id, plant) VALUES (@UserId, @plant)", plantRows, tx);

            var poRows = rows.SelectMany(r => Clean(r.PoTypes).Select(p => new { r.UserId, poType = p })).ToList();
            if (poRows.Count > 0)
                await c.ExecuteAsync(
                    "INSERT INTO qms_qo_notify_potype (user_id, po_type) VALUES (@UserId, @poType)", poRows, tx);
        }

        tx.Commit();

        static IEnumerable<string> Clean(string[]? v) =>
            (v ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<NotifyScopeOptions> GetScopeOptionsAsync()
    {
        using var c = new SqlConnection(_cs);
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT plant   FROM qms_arrival WHERE plant   IS NOT NULL AND LTRIM(RTRIM(plant))   <> '' ORDER BY plant;
            SELECT DISTINCT po_type FROM qms_arrival WHERE po_type IS NOT NULL AND LTRIM(RTRIM(po_type)) <> '' ORDER BY po_type;");
        var plants  = (await grid.ReadAsync<string>()).ToList();
        var poTypes = (await grid.ReadAsync<string>()).ToList();
        return new NotifyScopeOptions
        {
            Plants  = plants .Select(p => (p, _codes.PlantDisplay(p))).ToList(),
            PoTypes = poTypes.Select(p => (p, _codes.PoTypeDisplay(p))).ToList()
        };
    }

    private sealed class ScopeRow
    {
        public int    UserId { get; set; }
        public string Value  { get; set; } = "";
    }

    /// <summary>
    /// Who should hear about THIS order. A recipient with no plants chosen hears
    /// about every plant, and likewise for procurement type — empty means all,
    /// so a recipient nobody has scoped keeps behaving as before.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAddressesAsync(string? plant, string? poType)
    {
        var candidates = await ListCandidatesAsync();
        return candidates
            .Where(x => x.Selected && x.IsActive && x.CanBeMailed)
            .Where(x => Matches(x.Plants,  plant))
            .Where(x => Matches(x.PoTypes, poType))
            .Select(x => x.Email!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // An order with no plant (or no procurement type) recorded still reaches
        // the unscoped recipients: silence would hide the order from everybody,
        // which is worse than telling someone who did not narrowly ask.
        static bool Matches(List<string> scope, string? value) =>
            scope.Count == 0 ||
            (!string.IsNullOrWhiteSpace(value) &&
             scope.Contains(value!.Trim(), StringComparer.OrdinalIgnoreCase));
    }

    // ---- Sending ------------------------------------------------------------

    public async Task<int> NotifyFinishedAsync(long qualityOrderId, string finishedBy, CancellationToken ct = default)
    {
        try
        {
            // The order's plant and procurement type decide who is in scope, so
            // they have to be known before the recipient list is resolved.
            var qoForScope = await _qos.GetAsync(qualityOrderId);
            var arrivalForScope = qoForScope == null ? null : await _arrivals.GetAsync(qoForScope.ArrivalId);

            var addresses = await ResolveAddressesAsync(arrivalForScope?.Plant, arrivalForScope?.PoType);
            if (addresses.Count == 0)
            {
                _log.LogInformation(
                    "QO {QoId}: nobody is subscribed to plant '{Plant}' / procurement type '{PoType}'.",
                    qualityOrderId, arrivalForScope?.Plant, arrivalForScope?.PoType);
                return 0;
            }

            var built = await BuildAsync(qualityOrderId, finishedBy);
            if (built == null) return 0;

            var (subject, html) = built.Value;

            // One message per recipient rather than one message with several To
            // headers. A single message is all-or-nothing at the relay: one
            // address it dislikes can sink delivery for everyone on it, and the
            // log then says only "send failed" with no clue who missed out.
            // Per-recipient, each address succeeds or fails on its own and says
            // so by name. It also keeps the recipients from seeing each other,
            // which a notification has no reason to disclose.
            var sent = 0;
            foreach (var address in addresses)
            {
                var (ok, error) = await _email.SendWithAttachmentsAsync(
                    to: new[] { address }, cc: Array.Empty<string>(),
                    subject: subject, body: html, bodyIsHtml: true,
                    attachments: Array.Empty<(byte[], string, string)>(), ct: ct);

                if (ok) sent++;
                else _log.LogWarning("QO {QoId} finish notification to {Address} failed: {Error}",
                        qualityOrderId, address, error);
            }

            _log.LogInformation("QO {QoId} finish notification: {Sent} of {Total} recipient(s) mailed",
                qualityOrderId, sent, addresses.Count);
            return sent;
        }
        catch (Exception ex)
        {
            // Never let this surface to the operator: they finished the order,
            // and that succeeded.
            _log.LogError(ex, "QO {QoId} finish notification threw", qualityOrderId);
            return 0;
        }
    }

    public async Task<(string subject, string html)?> BuildPreviewAsync(long qualityOrderId)
        => await BuildAsync(qualityOrderId, "(preview)");

    // ---- The mail itself ----------------------------------------------------

    // Every colour is inline and every layout is a table: Outlook still ignores
    // most of flex and grid, and drops <style> blocks entirely in some versions.
    private const string Ink    = "#1f2933";
    private const string Muted  = "#7b8794";
    private const string Line   = "#e4e7eb";
    private const string Wash   = "#f5f7fa";
    private const string Brand  = "#0b5ed7";
    private const string Deep   = "#0a4bb0";
    private const string Good   = "#0f7b4f";

    private async Task<(string subject, string html)?> BuildAsync(long qualityOrderId, string finishedBy)
    {
        var qo = await _qos.GetAsync(qualityOrderId);
        if (qo == null) return null;

        var arrival    = await _arrivals.GetAsync(qo.ArrivalId);
        var shipment   = await _arrivals.GetShipmentAsync(qo.ArrivalId);
        var samples    = await _qos.ListSamplesAsync(qualityOrderId);

        // Same grouping the QO page and the PDF use: MARA-enrich first, or the
        // group key (group/brand/variety/grade) differs and the mail would show
        // a different breakdown from the report it refers to.
        var materials = (await _qos.GetMaterialsAsync(qualityOrderId)).ToList();
        var mara = await _mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (mara.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);
        var units     = await _qos.GetReportUnitsAsync();
        var summaries = await _qos.BuildGroupSummariesAsync(qualityOrderId, materials, units);

        var smtp = await _settings.GetSmtpConfigAsync();
        var link = QoLink(smtp.SiteUrl, qualityOrderId);

        // Standard subject line (QcSubjectLine): short QC number, inspection
        // status, container, vendor, BOL -- enough for a recipient to triage
        // from the inbox list. The status carries the M24 claim assessment when
        // the order has one, which is the whole point of asking for it at
        // finish time.
        var status = qo.PotentialClaim.HasValue
            ? $"Finished — {ClaimAssessment.Label(qo.PotentialClaim)}"
            : "Finished";
        // An administrator's own wording wins, when they have set one on
        // Parameters -> Mail Template. Blank keeps the automatic line, which is
        // what every installation had before the subject was settable, and what
        // a template cannot reproduce: it drops empty fields instead of leaving
        // dangling separators and stops growing before a client truncates it.
        var mail = await _settings.GetQoMailTemplateAsync();
        var subject = QcSubjectLine.Render(
            mail.NotifySubject, qo.QualityOrderNo, status,
            arrival?.ContainerNo, arrival?.VendorName, arrival?.BolNo,
            arrival?.Plant, arrival?.Ebeln);
        if (string.IsNullOrWhiteSpace(subject))
            subject = QcSubjectLine.Build(
                qo.QualityOrderNo, status, arrival?.ContainerNo, arrival?.VendorName, arrival?.BolNo);

        var sb = new System.Text.StringBuilder();
        sb.Append($"<div style=\"background:{Wash};padding:16px 0;font-family:Segoe UI,Roboto,Arial,sans-serif\">");
        sb.Append("<table role=\"presentation\" align=\"center\" width=\"760\" cellpadding=\"0\" cellspacing=\"0\" " +
                  $"style=\"width:760px;max-width:100%;border-collapse:collapse;background:#fff;" +
                  $"border:1px solid {Line};border-radius:8px;overflow:hidden\">");

        // ---- header ----------------------------------------------------------
        sb.Append($"<tr><td style=\"background:{Brand};background-image:linear-gradient(135deg,{Brand},{Deep});" +
                   "padding:16px 20px\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse\"><tr>");
        sb.Append($"<td style=\"color:#fff;font-size:20px;font-weight:700;letter-spacing:.2px\">{H(qo.QualityOrderNo)}</td>");
        sb.Append("<td align=\"right\"><span style=\"background:rgba(255,255,255,.22);color:#fff;font-size:11px;" +
                  "font-weight:700;letter-spacing:.06em;text-transform:uppercase;padding:4px 10px;" +
                  "border-radius:20px\">Finished</span></td>");
        sb.Append("</tr></table>");
        var sub = new[] { arrival?.ContainerNo, arrival?.VendorName }
                  .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => H(x)).ToArray();
        if (sub.Length > 0)
            sb.Append($"<div style=\"color:#e8effb;font-size:13px;margin-top:3px\">{string.Join(" &nbsp;·&nbsp; ", sub)}</div>");
        sb.Append($"<div style=\"color:#c3d6f5;font-size:12px;margin-top:2px\">by {H(finishedBy)} · " +
                  $"{(qo.ClosedAt ?? DateTime.UtcNow).ToLocalTime():dd MMM yyyy HH:mm}</div>");
        sb.Append("</td></tr>");

        sb.Append("<tr><td style=\"padding:18px 20px\">");

        // ---- the finisher's comment, first and unmissable ---------------------
        // This is the decision criterion: whoever reads this mail is deciding
        // whether to raise a claim, and the inspector's note is the argument.
        if (!string.IsNullOrWhiteSpace(qo.CloseReason))
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;margin-bottom:14px\"><tr>" +
                      "<td style=\"background:#fff8e6;border:1px solid #ffe3a3;border-left:4px solid #f0a500;" +
                       "border-radius:6px;padding:10px 14px\">" +
                      "<div style=\"font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:#9a6b00;" +
                       "font-weight:700;margin-bottom:3px\">Inspector&rsquo;s note on finishing</div>" +
                      $"<div style=\"font-size:15.5px;line-height:1.5;color:#4a3600\">{H(qo.CloseReason)}</div>" +
                      "</td></tr></table>");
        }

        // ---- shipment facts, two columns so the block stays short -------------
        sb.Append(Heading("Shipment"));
        var facts = new List<(string, string?)>
        {
            ("Container",        arrival?.ContainerNo),
            ("Supplier",         arrival?.VendorName),
            ("Bill of lading",   arrival?.BolNo),
            ("Plant",            _codes.PlantDisplay(arrival?.Plant)),
            ("Purch. doc.",      arrival?.Ebeln),
            ("Procurement type", _codes.PoTypeDisplay(arrival?.PoType)),
            ("Arrival",          arrival?.ArrivalNo),
            ("Vessel",           shipment?.VesselName),
            ("Discharge date",   shipment?.DischargeDate?.ToString("yyyy-MM-dd")),
            ("Receive date",     shipment?.ReceiveDate?.ToString("yyyy-MM-dd")),
            ("Finished",         qo.ClosedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
            ("Samples",          samples.Count.ToString()),
        }.Where(f => !string.IsNullOrWhiteSpace(f.Item2)).ToList();

        sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;font-size:14.5px\">");
        for (var i = 0; i < facts.Count; i += 2)
        {
            sb.Append("<tr>");
            sb.Append(FactCell(facts[i]));
            sb.Append(i + 1 < facts.Count ? FactCell(facts[i + 1]) : "<td colspan=\"2\"></td>");
            sb.Append("</tr>");
        }
        sb.Append("</table>");

        // ---- QC summary -------------------------------------------------------
        // The FULL rollup -- readings as well as defects -- so this mail says the
        // same thing as the Summary on the quality order page. A shorter version
        // sends people back into the system to find out whether anything was
        // actually wrong, which is the opposite of the point.
        sb.Append(Heading("QC summary"));
        if (summaries.Count == 0)
            sb.Append($"<div style=\"color:{Muted};font-size:14.5px\">No samples were recorded on this order.</div>");

        foreach (var g in summaries)
        {
            var title = string.IsNullOrWhiteSpace(g.MaterialGroupDesc) ? g.MaterialGroup : g.MaterialGroupDesc!;
            var key = new[] { g.Brand, g.Variety, g.Grade }
                      .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => H(x)).ToArray();
            var sections  = g.DefectSections.Where(s => s.Rows.Any(r => r.SumValue > 0)).ToList();
            var defectSum = sections.Sum(s => s.Rows.Sum(r => r.SumValue));
            var defectPct = g.SumSampleSize > 0 ? defectSum / g.SumSampleSize * 100m : 0m;

            sb.Append($"<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;border:1px solid {Line};" +
                       "border-radius:6px;margin:0 0 12px\">");

            // card header: name on the left, the one number that matters on the right
            sb.Append($"<tr><td style=\"background:{Wash};border-bottom:1px solid {Line};padding:9px 12px\">");
            sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse\"><tr>");
            sb.Append($"<td><span style=\"font-size:16px;font-weight:700;color:{Ink}\">{H(title)}</span>");
            if (key.Length > 0)
                sb.Append($"<span style=\"color:{Muted};font-size:13px\"> &nbsp;{string.Join(" · ", key)}</span>");
            sb.Append($"<div style=\"color:{Muted};font-size:12.5px;margin-top:2px\">" +
                      $"{g.MaterialCount} material(s) · {g.SampleCount} sample(s) · " +
                      $"{g.SumSampleSize:N0} {H(g.SampleUnit)} inspected</div></td>");
            sb.Append("<td align=\"right\" valign=\"top\">" + (sections.Count == 0
                ? $"<span style=\"background:#e6f5ee;color:{Good};font-size:11px;font-weight:700;" +
                   "padding:4px 10px;border-radius:20px;white-space:nowrap\">No defects</span>"
                : $"<span style=\"background:{Ink};color:#fff;font-size:12px;font-weight:700;" +
                  $"padding:4px 10px;border-radius:20px;white-space:nowrap\">{defectPct:N2}% defects</span>")
                + "</td>");
            sb.Append("</tr></table></td></tr>");

            sb.Append("<tr><td style=\"padding:10px 12px\">");

            // Readings as inline chips: a dozen label/value rows was most of the
            // mail's height, and these are context rather than the finding.
            // The SAME filter the QC report and the on-screen summary use: it
            // drops the not-yet-designed formula rows and the identifier fields
            // (PUC, grower, lot, date code) that mean nothing rolled up across a
            // group. Without it the mail showed readings the summary it claims to
            // mirror does not.
            var readings = SummaryReadingFilter.VisibleSummaryReadings(g.Readings)
                .Where(r => !string.IsNullOrWhiteSpace(r.DisplayValue))
                .ToList();
            if (readings.Count > 0)
            {
                sb.Append($"<div style=\"font-size:10px;text-transform:uppercase;letter-spacing:.08em;color:{Muted};" +
                           "font-weight:700;margin-bottom:5px\">Readings</div><div style=\"margin-bottom:10px\">");
                foreach (var r in readings)
                    sb.Append($"<span style=\"display:inline-block;background:{Wash};border:1px solid {Line};" +
                              $"border-radius:14px;padding:3px 11px;margin:0 5px 5px 0;font-size:13.5px;color:{Ink}\">" +
                              $"<span style=\"color:{Muted}\">{H(r.Name)}</span> <b>{H(r.DisplayValue)}" +
                              (string.IsNullOrWhiteSpace(r.Unit) ? "" : " " + H(r.Unit)) + "</b></span>");
                sb.Append("</div>");
            }

            if (sections.Count == 0)
            {
                sb.Append($"<div style=\"color:{Good};font-size:14.5px;font-weight:600\">No defects recorded.</div>");
            }
            else
            {
                foreach (var sec in sections)
                {
                    // The category's OWN configured colour, darkened for white by
                    // the same helper the on-screen summary uses -- so the mail and
                    // the app agree instead of inventing a second palette.
                    var col   = SummaryReadingFilter.ReadableOnWhite(sec.ColorHex);
                    var total = sec.Rows.Sum(r => r.SumValue);
                    var pct   = g.SumSampleSize > 0 ? total / g.SumSampleSize * 100m : 0m;

                    sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;margin-bottom:8px\">");
                    sb.Append($"<tr><td style=\"border-left:3px solid {col};padding:0 0 0 8px\">");

                    sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse\"><tr>" +
                              $"<td style=\"font-size:12px;font-weight:700;letter-spacing:.05em;text-transform:uppercase;" +
                              $"color:{col}\">{H(sec.CategoryName)}</td>" +
                              $"<td align=\"right\" style=\"font-size:13.5px;color:{Ink}\"><b>{total:N2}</b> " +
                              $"<span style=\"color:{Muted}\">({pct:N2}%)</span></td></tr></table>");

                    // A proportion bar, drawn with table cells because a div with a
                    // percentage width is unreliable in Outlook.
                    var w = (int)Math.Round(Math.Clamp(pct, 0, 100));
                    sb.Append($"<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;margin:4px 0 5px\">" +
                              $"<tr><td style=\"height:4px;background:{col};width:{w}%;font-size:0;line-height:0\">&nbsp;</td>" +
                              $"<td style=\"height:4px;background:{Line};width:{100 - w}%;font-size:0;line-height:0\">&nbsp;</td>" +
                              "</tr></table>");

                    sb.Append($"<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;font-size:13.5px\">");
                    foreach (var r in sec.Rows.Where(r => r.SumValue > 0).OrderByDescending(r => r.SumValue))
                        sb.Append("<tr>"
                            + $"<td style=\"padding:2px 8px 2px 0;color:{Ink}\">{H(r.Name)}</td>"
                            + $"<td align=\"right\" style=\"padding:2px 10px 2px 0;color:{Muted};width:72px\">{r.SumValue:N2}</td>"
                            + $"<td align=\"right\" style=\"padding:2px 0;width:60px;font-weight:700;color:{Ink}\">{r.Percentage:N2}%</td>"
                            + "</tr>");
                    sb.Append("</table>");

                    sb.Append("</td></tr></table>");
                }
            }
            sb.Append("</td></tr></table>");
        }

        // ---- call to action, after the reader knows what they are opening -----
        if (!string.IsNullOrWhiteSpace(link))
        {
            sb.Append("<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;margin:4px 0 2px\"><tr>" +
                      $"<td align=\"center\" style=\"background:{Brand};border-radius:6px\">" +
                      $"<a href=\"{H(link)}\" style=\"display:block;padding:11px 20px;color:#fff;text-decoration:none;" +
                       "font-weight:600;font-size:14px\">Open the quality order &rarr;</a></td></tr></table>");
        }

        sb.Append("</td></tr>");
        sb.Append($"<tr><td style=\"background:{Wash};border-top:1px solid {Line};padding:10px 20px;" +
                  $"color:{Muted};font-size:12px;line-height:1.5\">" +
                   "Sent automatically by Sharbatly QMS when a quality order is finished. " +
                   "The full report, with photos, is in the system.</td></tr>");
        sb.Append("</table></div>");

        return (subject, sb.ToString());
    }

    private static string FactCell((string Label, string? Value) f) =>
        $"<td style=\"padding:5px 16px 5px 0;color:{Muted};white-space:nowrap;width:1%\">{H(f.Label)}</td>" +
        $"<td style=\"padding:5px 26px 5px 0;font-weight:600;color:{Ink};width:49%\">{H(f.Value)}</td>";

    /// <summary>
    /// Absolute link to the order. Built from Site Configuration's site URL,
    /// because the mail is sent after the response has gone and there is no
    /// request to read a host from. Returns "" when that setting is blank, and
    /// the mail then simply omits the button rather than shipping a dead link.
    /// </summary>
    private static string QoLink(string? siteUrl, long qoId)
    {
        var baseUrl = (siteUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) return "";
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) baseUrl = "http://" + baseUrl;
        return $"{baseUrl}/QualityOrders/Details/{qoId}";
    }

    private static string Heading(string title) =>
        $"<div style=\"font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;" +
        $"color:{Muted};border-bottom:1px solid {Line};padding-bottom:5px;margin:14px 0 9px\">{title}</div>";

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
