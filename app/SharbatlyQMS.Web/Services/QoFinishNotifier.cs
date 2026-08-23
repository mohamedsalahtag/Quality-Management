using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

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

    /// <summary>Replaces the recipient list wholesale with the given users.</summary>
    Task SaveRecipientsAsync(IEnumerable<int> userIds, string actor);

    /// <summary>Builds and sends the "quality order finished" mail. Best-effort:
    /// returns the number of addresses mailed, and never throws.</summary>
    Task<int> NotifyFinishedAsync(long qualityOrderId, string finishedBy, CancellationToken ct = default);

    /// <summary>The mail body for a quality order, for the settings screen's
    /// preview. Same builder the real send uses, so the preview cannot drift.</summary>
    Task<(string subject, string html)?> BuildPreviewAsync(long qualityOrderId);
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
        var chosen = (await c.QueryAsync<int>(
            "SELECT user_id FROM qms_qo_notify_recipient")).ToHashSet();

        return users
            .Select(u => new NotifyCandidate
            {
                UserId   = u.UserId,
                Username = u.Username,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName,
                Email    = u.Email,
                RoleName = u.RoleName,
                IsActive = u.IsActive,
                Selected = chosen.Contains(u.UserId)
            })
            // Selected first so a long user list always opens on what is set.
            .OrderByDescending(x => x.Selected)
            .ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SaveRecipientsAsync(IEnumerable<int> userIds, string actor)
    {
        var ids = userIds.Distinct().ToArray();

        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Replace wholesale inside one transaction: a half-applied list would
        // quietly mail the wrong people until someone noticed.
        await c.ExecuteAsync("DELETE FROM qms_qo_notify_recipient", transaction: tx);
        if (ids.Length > 0)
            await c.ExecuteAsync(
                "INSERT INTO qms_qo_notify_recipient (user_id, added_by) VALUES (@id, @actor)",
                ids.Select(id => new { id, actor }), tx);

        tx.Commit();
    }

    private async Task<IReadOnlyList<string>> ResolveAddressesAsync()
    {
        var candidates = await ListCandidatesAsync();
        return candidates
            .Where(x => x.Selected && x.IsActive && x.CanBeMailed)
            .Select(x => x.Email!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- Sending ------------------------------------------------------------

    public async Task<int> NotifyFinishedAsync(long qualityOrderId, string finishedBy, CancellationToken ct = default)
    {
        try
        {
            var addresses = await ResolveAddressesAsync();
            if (addresses.Count == 0) return 0;

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

    // A muted palette that survives Outlook: every colour is inline, no classes,
    // no external stylesheet, and tables do the layout because Outlook's engine
    // still ignores most of flex/grid.
    private const string Ink    = "#212529";
    private const string Muted  = "#6c757d";
    private const string Line   = "#dee2e6";
    private const string Accent = "#0d6efd";
    private const string Wash   = "#f8f9fa";

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

        var subject = $"QC finished — {qo.QualityOrderNo}"
                    + (string.IsNullOrWhiteSpace(arrival?.ContainerNo) ? "" : $" — {arrival!.ContainerNo}");

        var sb = new System.Text.StringBuilder();
        sb.Append($"<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:{Ink};" +
                   "max-width:820px;margin:0 auto\">");

        // ---- header band ----
        sb.Append($"<div style=\"background:{Accent};color:#fff;padding:14px 18px;border-radius:6px 6px 0 0\">" +
                  $"<div style=\"font-size:18px;font-weight:600\">Quality order finished</div>" +
                  $"<div style=\"opacity:.9;font-size:13px;margin-top:2px\">{H(qo.QualityOrderNo)}" +
                  (string.IsNullOrWhiteSpace(arrival?.ContainerNo) ? "" : $" &nbsp;&middot;&nbsp; {H(arrival!.ContainerNo)}") +
                  $" &nbsp;&middot;&nbsp; finished by {H(finishedBy)} on " +
                  $"{(qo.ClosedAt ?? DateTime.UtcNow).ToLocalTime():yyyy-MM-dd HH:mm}</div></div>");
        sb.Append($"<div style=\"border:1px solid {Line};border-top:0;border-radius:0 0 6px 6px;padding:18px\">");

        // ---- the finisher's comment, first and unmissable ----
        // This is the decision criterion: whoever reads this mail is deciding
        // whether to raise a claim, and the inspector's note is the argument.
        if (!string.IsNullOrWhiteSpace(qo.CloseReason))
        {
            sb.Append($"<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;margin-bottom:18px\"><tr>" +
                      $"<td style=\"background:#fff3cd;border:1px solid #ffe69c;border-left:5px solid #ffc107;" +
                       "border-radius:4px;padding:12px 14px\">" +
                      $"<div style=\"font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:#8a6d3b;" +
                       "font-weight:700;margin-bottom:4px\">Inspector's note on finishing</div>" +
                      $"<div style=\"font-size:15px;line-height:1.45\">{H(qo.CloseReason)}</div>" +
                      $"</td></tr></table>");
        }

        // ---- open in QMS ----
        if (!string.IsNullOrWhiteSpace(link))
        {
            sb.Append($"<table role=\"presentation\" style=\"border-collapse:collapse;margin-bottom:18px\"><tr><td " +
                      $"style=\"background:{Accent};border-radius:4px\">" +
                      $"<a href=\"{H(link)}\" style=\"display:inline-block;padding:9px 18px;color:#fff;" +
                       "text-decoration:none;font-weight:600;font-size:14px\">Open the quality order &rarr;</a>" +
                      $"</td></tr></table>");
        }

        // ---- shipment basics ----
        sb.Append(Heading("Shipment"));
        sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse;font-size:13px\">");
        Row(sb, "QC number",        qo.QualityOrderNo);
        Row(sb, "Container",        arrival?.ContainerNo);
        Row(sb, "Bill of lading",   arrival?.BolNo);
        Row(sb, "Purch. doc.",      arrival?.Ebeln);
        Row(sb, "Procurement type", _codes.PoTypeDisplay(arrival?.PoType));
        Row(sb, "Supplier",         arrival?.VendorName);
        Row(sb, "Plant",            _codes.PlantDisplay(arrival?.Plant));
        Row(sb, "Arrival",          arrival?.ArrivalNo);
        Row(sb, "Vessel",           shipment?.VesselName);
        Row(sb, "Discharge date",   shipment?.DischargeDate?.ToString("yyyy-MM-dd"));
        Row(sb, "Opened",           qo.OpenedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Row(sb, "Finished",         qo.ClosedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Row(sb, "Samples",          samples.Count.ToString());
        sb.Append("</table>");

        // ---- the QC summary, per material group ----
        // Deliberately the FULL rollup -- readings as well as defects -- so this
        // mail says the same thing as the Summary on the quality order page. A
        // shorter version would send people back into the system to see whether
        // anything was actually wrong, which is the opposite of the point.
        sb.Append(Heading("QC summary"));
        if (summaries.Count == 0)
        {
            sb.Append($"<div style=\"color:{Muted}\">No samples were recorded on this order.</div>");
        }
        foreach (var g in summaries)
        {
            var title = string.IsNullOrWhiteSpace(g.MaterialGroupDesc) ? g.MaterialGroup : g.MaterialGroupDesc!;
            var key = new[] { g.Brand, g.Variety, g.Grade }
                      .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => H(x)).ToArray();

            sb.Append($"<div style=\"border:1px solid {Line};border-radius:4px;margin:0 0 14px\">");
            sb.Append($"<div style=\"background:{Wash};border-bottom:1px solid {Line};padding:8px 12px\">" +
                      $"<div style=\"font-weight:600\">{H(title)}</div>" +
                      (key.Length > 0
                          ? $"<div style=\"color:{Muted};font-size:12px\">{string.Join(" &middot; ", key)}</div>"
                          : "") +
                      $"<div style=\"color:{Muted};font-size:12px;margin-top:2px\">" +
                      $"{g.MaterialCount} material(s) &middot; {g.SampleCount} sample(s) &middot; " +
                      $"{g.SumSampleSize:N0} {H(g.SampleUnit)} inspected</div></div>");
            sb.Append("<div style=\"padding:10px 12px\">");

            // Readings first: they describe the fruit, before the faults.
            var readings = g.Readings.Where(r => !string.IsNullOrWhiteSpace(r.DisplayValue)).ToList();
            if (readings.Count > 0)
            {
                sb.Append($"<div style=\"font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:{Muted};" +
                           "font-weight:700;margin-bottom:4px\">Readings</div>");
                sb.Append("<table role=\"presentation\" style=\"border-collapse:collapse;font-size:13px;margin-bottom:12px\">");
                foreach (var r in readings)
                    Row(sb, r.Name, r.DisplayValue + (string.IsNullOrWhiteSpace(r.Unit) ? "" : " " + r.Unit));
                sb.Append("</table>");
            }

            var sections = g.DefectSections.Where(sec => sec.Rows.Any(r => r.SumValue > 0)).ToList();
            if (sections.Count == 0)
            {
                sb.Append("<div style=\"color:#198754;font-weight:600\">No defects recorded.</div>");
            }
            else
            {
                foreach (var sec in sections)
                {
                    var total = sec.Rows.Sum(r => r.SumValue);
                    var pct   = g.SumSampleSize > 0 ? total / g.SumSampleSize * 100m : 0m;
                    sb.Append($"<div style=\"font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:{Muted};" +
                               "font-weight:700;margin:8px 0 4px\">" + H(sec.CategoryName) +
                              $" <span style=\"color:{Ink}\">&mdash; {total:N2} ({pct:N2}%)</span></div>");
                    sb.Append($"<table role=\"presentation\" width=\"100%\" style=\"border-collapse:collapse;font-size:13px\">");
                    foreach (var r in sec.Rows.Where(r => r.SumValue > 0).OrderByDescending(r => r.SumValue))
                        sb.Append("<tr>"
                            + $"<td style=\"padding:2px 10px 2px 0;border-bottom:1px solid {Wash}\">{H(r.Name)}</td>"
                            + $"<td style=\"padding:2px 10px 2px 0;text-align:right;border-bottom:1px solid {Wash}\">{r.SumValue:N2}</td>"
                            + $"<td style=\"padding:2px 0;text-align:right;width:70px;border-bottom:1px solid {Wash};font-weight:600\">{r.Percentage:N2}%</td>"
                            + "</tr>");
                    sb.Append("</table>");
                }
            }
            sb.Append("</div></div>");
        }

        sb.Append($"<div style=\"color:{Muted};font-size:12px;border-top:1px solid {Line};padding-top:10px;margin-top:6px\">" +
                   "Sent automatically by Sharbatly QMS when a quality order is finished. " +
                   "The full report, with photos, is available in the system.</div>");
        sb.Append("</div></div>");

        return (subject, sb.ToString());
    }

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
        $"<div style=\"font-weight:600;font-size:15px;border-bottom:2px solid {Line};" +
        $"padding-bottom:4px;margin:20px 0 10px\">{title}</div>";

    private static void Row(System.Text.StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;   // an empty row tells the reader nothing
        sb.Append($"<tr><td style=\"padding:3px 16px 3px 0;color:{Muted};vertical-align:top\">{H(label)}</td>"
                + $"<td style=\"padding:3px 0;font-weight:600\">{H(value!)}</td></tr>");
    }

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
