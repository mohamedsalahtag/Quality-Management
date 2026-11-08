using System.Text.RegularExpressions;
using MimeKit;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Pulls supplier e-mail addresses out of free text. One parser for the three
/// places addresses come from -- the contacts workbook, the address box on
/// Parameters -> Supplier contacts, and the send dialog's To line -- so an
/// address accepted in one is accepted in all.
///
/// The workbook cells are whatever people pasted from Outlook:
/// <c>'Richard Hunt' &lt;Richard.Hunt@nee.com.au&gt;; 'vincent@nee.com.au'</c>,
/// <c>: Kayleigh van der Arend &lt;k.vanderarend@globalgreenteam.nl&gt;</c>,
/// <c>darapova@camposol.eu&gt;; Sales &lt;sales@camposol.eu&gt;</c>. So the
/// address is found inside each piece rather than the piece being taken as
/// the address. Pieces with no '@' are display names and are ignored; a piece
/// with an '@' but no usable address is reported back as invalid.
/// </summary>
public static class SupplierEmails
{
    private static readonly char[] Separators = { ';', ',', '\n', '\r', '\t' };

    private static readonly Regex Address = new(
        @"[A-Za-z0-9._%+'-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The addresses in <paramref name="text"/>, lower-cased and
    /// de-duplicated in first-seen order, plus the pieces that looked like an
    /// address but were not one.</summary>
    public static (IReadOnlyList<string> Valid, IReadOnlyList<string> Invalid) Parse(string? text)
    {
        var valid   = new List<string>();
        var invalid = new List<string>();
        foreach (var piece in (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = piece.Trim();
            if (p.Length == 0 || !p.Contains('@')) continue;
            var hits = Address.Matches(p);
            if (hits.Count == 0) { invalid.Add(p); continue; }
            foreach (Match m in hits)
            {
                var a = m.Value.Trim('\'', '.', '-').ToLowerInvariant();
                if (!MailboxAddress.TryParse(a, out _)) { invalid.Add(p); continue; }
                if (!valid.Contains(a)) valid.Add(a);
            }
        }
        return (valid, invalid);
    }
}
