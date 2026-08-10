using System.Globalization;

namespace SharbatlyQMS.Web;

/// <summary>
/// Shared number formatting so every screen and the PDF agree on how numbers
/// look. The rule (user requirement): any value with a fractional part shows
/// EXACTLY two decimals; counts and codes (e.g. the date code) show as whole
/// integers. Centralised here rather than sprinkling <c>ToString("0.##")</c> /
/// <c>0.###</c> / <c>0.####</c> at each call site, which is what caused the
/// inconsistent decimals in the first place.
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Exactly two decimals (e.g. 12 -> "12.00", 9.5 -> "9.50"), or "" when null.</summary>
    public static string Dec2(decimal? v) => v.HasValue ? v.Value.ToString("0.00", Inv) : "";
    public static string Dec2(decimal v)  => v.ToString("0.00", Inv);
    public static string Dec2(double? v)  => v.HasValue ? v.Value.ToString("0.00", Inv) : "";
    public static string Dec2(double v)   => v.ToString("0.00", Inv);

    /// <summary>Whole integer, no decimals (rounded away-from-zero), or "" when null.
    /// Used for counts and the date code.</summary>
    public static string Int0(decimal? v) => v.HasValue ? decimal.Round(v.Value, 0, MidpointRounding.AwayFromZero).ToString("0", Inv) : "";
    public static string Int0(decimal v)  => decimal.Round(v, 0, MidpointRounding.AwayFromZero).ToString("0", Inv);
    public static string Int0(double? v)  => v.HasValue ? Math.Round(v.Value, MidpointRounding.AwayFromZero).ToString("0", Inv) : "";

    /// <summary>True when a reading/field code or name denotes the date code
    /// (matches "DATE_CODE", "Date Code", "datecode" — punctuation/space/case
    /// insensitive). The date code is a whole number and must print without
    /// decimals.</summary>
    public static bool IsDateCode(string? codeOrName) =>
        !string.IsNullOrWhiteSpace(codeOrName) &&
        codeOrName.Replace("_", "").Replace(" ", "").Equals("datecode", StringComparison.OrdinalIgnoreCase);
}
