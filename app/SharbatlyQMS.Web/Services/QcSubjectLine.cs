namespace SharbatlyQMS.Web.Services;

/// <summary>
/// One standard subject line for every Quality-Control e-mail, so a recipient
/// can tell what a message is about from the inbox list without opening it.
///
/// Shape: <c>QC 956 · Finished — Potential Claim · Cont MNBU3969264 · SQ FLORA B.V. · BOL 065-49966700</c>
///
/// The QC number is the SHORT form — the sequence only, with the "QO-YYYY-"
/// prefix and its leading zeros dropped — because the prefix is identical on
/// every mail and wastes the part of the subject most clients actually show.
/// Fields are added in decreasing usefulness and the line stops growing at
/// <see cref="MaxLength"/>, so a long vendor name can never push the BOL out of
/// view in a narrow client; the QC number is always present.
/// </summary>
public static class QcSubjectLine
{
    /// <summary>Practical cap. Outlook/OWA and Gmail truncate well before this,
    /// but the tail still shows in previews and searches, so we allow a
    /// generous line rather than a minimal one.</summary>
    public const int MaxLength = 150;

    /// <summary>Vendor names from SAP run long ("SOCIEDAD AGRICOLA ..."); cap
    /// them so one field cannot eat the whole subject.</summary>
    private const int MaxVendorLength = 40;

    private const string Sep = " · ";

    /// <summary>"QO-2026-000956" -> "956". Falls back to the full number when
    /// it isn't in the expected prefix-sequence shape, so an unusual number is
    /// shown rather than mangled.</summary>
    public static string ShortQcNo(string? qualityOrderNo)
    {
        var full = Clean(qualityOrderNo);
        if (full.Length == 0) return "";

        var cut  = full.LastIndexOf('-');
        var tail = cut >= 0 ? full[(cut + 1)..] : full;
        if (tail.Length == 0 || !tail.All(char.IsDigit)) return full;

        var trimmed = tail.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>Builds the standard line. Every argument is optional except the
    /// QC number; blanks are skipped rather than leaving empty separators.</summary>
    /// <param name="status">Inspection status, e.g. "Finished — Potential Claim".</param>
    public static string Build(string? qualityOrderNo, string? status = null,
        string? container = null, string? vendor = null, string? bol = null)
    {
        var qc = ShortQcNo(qualityOrderNo);
        var sb = new System.Text.StringBuilder(qc.Length == 0 ? "Quality Control Report" : $"QC {qc}");

        // Order matters: whatever is appended last is what a narrow client
        // drops first.
        Append(sb, Clean(status));
        Append(sb, Label("Cont", Clean(container)));
        Append(sb, Truncate(Clean(vendor), MaxVendorLength));
        Append(sb, Label("BOL", Clean(bol)));

        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, string part)
    {
        if (part.Length == 0) return;
        // Skip a part that would overflow, but keep trying the later (usually
        // shorter) ones -- a long vendor name shouldn't cost us the BOL.
        if (sb.Length + Sep.Length + part.Length > MaxLength) return;
        sb.Append(Sep).Append(part);
    }

    private static string Label(string label, string value) =>
        value.Length == 0 ? "" : $"{label} {value}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>Collapses whitespace and strips CR/LF. Subjects are built from
    /// SAP-sourced text, and a newline in a header is a header-injection bug
    /// waiting to happen.</summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray();
        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
