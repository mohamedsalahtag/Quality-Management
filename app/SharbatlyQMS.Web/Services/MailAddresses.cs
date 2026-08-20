namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Splitting and merging of the comma/semicolon separated address lists the
/// send-report dialog and the mail template both use. Shared so the standing
/// CC is parsed exactly the same way when it is validated on save, pre-filled
/// in the dialog, and enforced at send time — three places that silently
/// disagreeing would be hard to spot.
/// </summary>
public static class MailAddresses
{
    private static readonly char[] Separators = { ',', ';', '\n', '\r' };

    /// <summary>Splits a list into trimmed, non-empty addresses.</summary>
    public static IReadOnlyList<string> Split(string? s) =>
        (s ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                 .Select(a => a.Trim())
                 .Where(a => a.Length > 0)
                 .ToList();

    /// <summary>
    /// Concatenates address lists, dropping duplicates case-insensitively and
    /// keeping first-seen order. Used to fold the standing CC into whatever the
    /// sender typed without copying anyone twice.
    /// </summary>
    public static IReadOnlyList<string> Merge(params string?[] lists) =>
        lists.SelectMany(Split)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToList();
}
