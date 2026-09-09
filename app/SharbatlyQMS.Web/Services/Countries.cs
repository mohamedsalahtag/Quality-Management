using System.Collections.Concurrent;
using System.Globalization;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// ISO-3166 alpha-2 country code → full English name, for reports that leave the
/// building. "CL" means nothing to a supplier reading a PDF; "Chile" does.
///
/// SAP gives us the two-letter code (ZQC_Data → qms_shipment_snapshot.loading_country),
/// so the code stays the stored value and this is display-only. Anything that
/// isn't a recognised two-letter code — blank, a full name already, a stray
/// three-letter code — is passed through untouched rather than guessed at.
/// </summary>
public static class Countries
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full English country name, or the input unchanged when it isn't
    /// a recognised alpha-2 code. Never returns null.</summary>
    public static string Display(string? code)
    {
        var raw = (code ?? "").Trim();
        if (raw.Length != 2 || !raw.All(char.IsLetter)) return raw;

        return Cache.GetOrAdd(raw, key =>
        {
            try
            {
                // RegionInfo throws for a well-formed but unknown region, and
                // returns the code itself when ICU has no display name for it.
                var name = new RegionInfo(key.ToUpperInvariant()).EnglishName;
                return string.IsNullOrWhiteSpace(name) ? key : name;
            }
            catch (ArgumentException)
            {
                return key;
            }
        });
    }

    /// <summary>"Chile (CL)" — for screens where someone may still need to
    /// cross-check the raw SAP code.</summary>
    public static string DisplayWithCode(string? code)
    {
        var raw  = (code ?? "").Trim();
        var name = Display(raw);
        return name.Equals(raw, StringComparison.OrdinalIgnoreCase) ? raw : $"{name} ({raw.ToUpperInvariant()})";
    }
}
