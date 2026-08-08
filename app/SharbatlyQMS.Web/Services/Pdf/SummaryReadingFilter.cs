using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Single source of truth for the grouped-SUMMARY block that appears both on the
/// QC report (<see cref="QualityReportPdf"/>) and, since the claim page reuses it,
/// as the "Summary" header on the claim view. Keeping the hide rules and the
/// readable-colour maths here means the PDF and the HTML summary can never drift.
/// </summary>
public static class SummaryReadingFilter
{
    /// <summary>Never shown on the report at all (identifiers, not measurements).</summary>
    public static readonly HashSet<string> HiddenReportFields = new(StringComparer.Ordinal)
    {
        "PACKAGINGMATERIAL", "PACKINGMATERIAL",
    };

    /// <summary>Hidden from the group summary only — still shown per sample. These
    /// are identifiers that are meaningless rolled up across a whole group.</summary>
    public static readonly HashSet<string> HiddenSummaryOnlyFields = new(StringComparer.Ordinal)
    {
        "PUC", "GROWER", "LOTNO", "LOTNUMBER", "DATECODE",
    };

    /// <summary>Uppercase, strip everything that isn't A-Z/0-9, so "Pallet No.",
    /// "Pallet No" and "PALLET_NO" all compare equal.</summary>
    public static string NormalizeFieldName(string? name)
        => new string((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static bool IsHiddenReportField(string? name)
        => HiddenReportFields.Contains(NormalizeFieldName(name));

    /// <summary>Hidden from the group summary readings (packaging + identifier
    /// fields) — but still shown per sample.</summary>
    public static bool IsHiddenSummaryField(string? name)
    {
        var n = NormalizeFieldName(name);
        return HiddenReportFields.Contains(n) || HiddenSummaryOnlyFields.Contains(n);
    }

    /// <summary>The readings shown in a grouped summary, in order: drop the
    /// not-yet-designed 'formula' display mode and every hidden identifier field.
    /// Matches exactly what <see cref="QualityReportPdf"/> renders.</summary>
    public static List<ReadingAggRow> VisibleSummaryReadings(IEnumerable<ReadingAggRow> readings)
        => readings
            .Where(r => !string.Equals(r.DisplayMode, "formula", StringComparison.OrdinalIgnoreCase))
            .Where(r => !IsHiddenSummaryField(r.Name))
            .ToList();

    /// <summary>A defect category's colour, darkened until it is legible on a white
    /// background (mirrors the report). Falls back to a neutral grey.</summary>
    public static string ReadableOnWhite(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !IsHex(hex)) return "#495057";
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        double Lum() => (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        int guard = 0;
        while (Lum() > 0.5 && guard++ < 10)
        {
            r = (int)(r * 0.7); g = (int)(g * 0.7); b = (int)(b * 0.7);
        }
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static bool IsHex(string s) =>
        s.Length == 7 && s[0] == '#' &&
        s.Skip(1).All(c => "0123456789abcdefABCDEF".IndexOf(c) >= 0);
}
