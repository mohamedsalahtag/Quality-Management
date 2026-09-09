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
        // Pallet identifiers belong to the individual carton that was sampled.
        // Rolled up over a whole material group they say nothing -- a summary
        // line reading "Pallet No: 12 / 47 / 103" is noise, not information.
        // Still printed on each sample card.
        "PALLETNO", "PALLETNUMBER", "PALLET", "GROWERPALLET",
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

    /// <summary>
    /// Text colour to print ON a filled swatch of <paramref name="hex"/> -- white
    /// on a dark fill, near-black ink on a light one.
    ///
    /// The report used to hardcode white, which is fine for the dark seeded
    /// colours (Major #b02a37, Critical #7a1620) and unreadable on the light one
    /// (Minor #ffc107). An administrator picking any pale colour on
    /// Admin -> Defect Categories would have produced an invisible heading.
    /// </summary>
    public static string OnFill(string? hex, string dark = "#1c2733", string light = "#ffffff")
    {
        if (string.IsNullOrWhiteSpace(hex) || !IsHex(hex)) return light;
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        // Perceived luminance, same weighting as ReadableOnWhite above.
        var lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        return lum > 0.6 ? dark : light;
    }

    /// <summary>True when the string is a usable #RRGGBB colour.</summary>
    public static bool IsColour(string? s) => !string.IsNullOrWhiteSpace(s) && IsHex(s!);

    private static bool IsHex(string s) =>
        s.Length == 7 && s[0] == '#' &&
        s.Skip(1).All(c => "0123456789abcdefABCDEF".IndexOf(c) >= 0);
}
