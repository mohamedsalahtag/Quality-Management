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
    private readonly ILogger<QoFinishNotifier> _log;

    public QoFinishNotifier(IConfiguration cfg, IDbService db, IQualityOrderService qos,
        IArrivalService arrivals, IMaraService mara, IEmailService email,
        ICodeDescriptionDirectory codes, ILogger<QoFinishNotifier> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _db = db; _qos = qos; _arrivals = arrivals; _mara = mara;
        _email = email; _codes = codes; _log = log;
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
            var (ok, error) = await _email.SendWithAttachmentsAsync(
                to: addresses, cc: Array.Empty<string>(),
                subject: subject, body: html, bodyIsHtml: true,
                attachments: Array.Empty<(byte[], string, string)>(), ct: ct);

            if (!ok)
            {
                _log.LogWarning("QO {QoId} finish notification failed: {Error}", qualityOrderId, error);
                return 0;
            }
            _log.LogInformation("QO {QoId} finish notification sent to {Count} recipient(s)",
                qualityOrderId, addresses.Count);
            return addresses.Count;
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

        var subject = $"QC finished — {qo.QualityOrderNo}"
                    + (string.IsNullOrWhiteSpace(arrival?.ContainerNo) ? "" : $" — {arrival!.ContainerNo}");

        var sb = new System.Text.StringBuilder();
        sb.Append("""
            <div style="font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#212529">
            """);
        sb.Append($"<h2 style=\"margin:0 0 2px;font-size:18px;color:#0d6efd\">Quality order finished</h2>");
        sb.Append($"<div style=\"color:#6c757d;margin-bottom:14px\">{H(qo.QualityOrderNo)}"
                + $" &middot; finished by {H(finishedBy)}"
                + $" &middot; {(qo.ClosedAt ?? DateTime.UtcNow).ToLocalTime():yyyy-MM-dd HH:mm}</div>");

        // ---- basic information ----
        sb.Append(Section("Shipment"));
        sb.Append("<table style=\"border-collapse:collapse;font-size:13px\">");
        Row(sb, "QC number",   qo.QualityOrderNo);
        Row(sb, "Container",   arrival?.ContainerNo);
        Row(sb, "Bill of lading", arrival?.BolNo);
        Row(sb, "Purch. doc.", arrival?.Ebeln);
        Row(sb, "Procurement type", _codes.PoTypeDisplay(arrival?.PoType));
        Row(sb, "Supplier",    arrival?.VendorName);
        Row(sb, "Plant",       _codes.PlantDisplay(arrival?.Plant));
        Row(sb, "Arrival",     arrival?.ArrivalNo);
        Row(sb, "Vessel",      shipment?.VesselName);
        Row(sb, "Discharge date", shipment?.DischargeDate?.ToString("yyyy-MM-dd"));
        Row(sb, "Opened",      qo.OpenedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Row(sb, "Finished",    qo.ClosedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Row(sb, "Samples",     samples.Count.ToString());
        sb.Append("</table>");

        // ---- the QC result, per material group ----
        foreach (var g in summaries)
        {
            var title = string.IsNullOrWhiteSpace(g.MaterialGroupDesc) ? g.MaterialGroup : g.MaterialGroupDesc!;
            sb.Append(Section(H(title)));
            sb.Append("<div style=\"color:#6c757d;font-size:12px;margin-bottom:6px\">"
                    + $"{g.MaterialCount} material(s) &middot; {g.SampleCount} sample(s) &middot; "
                    + $"{g.SumSampleSize:N0} {H(g.SampleUnit)} inspected</div>");

            var anyDefect = g.DefectSections.Any(sec => sec.Rows.Any(r => r.SumValue > 0));
            if (!anyDefect)
            {
                sb.Append("<div style=\"color:#198754\">No defects recorded.</div>");
                continue;
            }

            sb.Append("<table style=\"border-collapse:collapse;font-size:13px\">");
            sb.Append("<tr style=\"color:#6c757d\">"
                    + Th("Category") + Th("Defect") + Th("Count", right: true) + Th("%", right: true) + "</tr>");
            foreach (var sec in g.DefectSections)
                foreach (var r in sec.Rows.Where(r => r.SumValue > 0).OrderByDescending(r => r.SumValue))
                    sb.Append("<tr>"
                        + Td(H(sec.CategoryName))
                        + Td(H(r.Name))
                        + Td($"{r.SumValue:N2}", right: true)
                        + Td($"{r.Percentage:N2}%", right: true)
                        + "</tr>");
            sb.Append("</table>");
        }

        sb.Append("<p style=\"color:#6c757d;font-size:12px;margin-top:18px\">"
                + "Sent automatically by Sharbatly QMS when a quality order is finished. "
                + "The full report, with photos, is available in the system.</p>");
        sb.Append("</div>");

        return (subject, sb.ToString());
    }

    private static string Section(string title) =>
        $"<div style=\"margin:16px 0 6px;font-weight:600;border-bottom:1px solid #dee2e6;padding-bottom:3px\">{title}</div>";

    private static void Row(System.Text.StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;   // an empty row tells the reader nothing
        sb.Append($"<tr><td style=\"padding:2px 14px 2px 0;color:#6c757d\">{H(label)}</td>"
                + $"<td style=\"padding:2px 0;font-weight:600\">{H(value!)}</td></tr>");
    }

    private static string Th(string t, bool right = false) =>
        $"<th style=\"text-align:{(right ? "right" : "left")};padding:2px 14px 2px 0;font-weight:600\">{t}</th>";

    private static string Td(string t, bool right = false) =>
        $"<td style=\"text-align:{(right ? "right" : "left")};padding:2px 14px 2px 0\">{t}</td>";

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
