using ClosedXML.Excel;

namespace SharbatlyQMS.Web.Services;

/// <summary>One data row of the supplier contacts workbook, as read.</summary>
public sealed record SupplierImportRow(
    int RowNo, string Code, string? Origin, string? Name, string? ContactPerson,
    IReadOnlyList<string> Emails);

/// <summary>A row the import did not apply, and why.</summary>
public sealed record SupplierImportSkip(int RowNo, string Code, string? Name, string Reason);

/// <summary>
/// Reads the supplier contacts workbook ("Supplier Contacts - Updated.xlsx")
/// and decides which rows to apply. Split from the database writes so the
/// rules can be tested on an in-memory workbook.
///
/// The rules, as agreed for the first import:
///   * a row without a supplier code is skipped -- it cannot be matched to a
///     container, so its addresses would never be used;
///   * every row of a code that appears more than once is skipped -- those
///     are consolidators listing several farms (80000575 has 22) and which of
///     them should receive the report is a decision for a person;
///   * a 7-digit code such as 8000272 is the 8-digit SAP code with the zero
///     after the 8000/4000 prefix missing, and is read as 80000272 -- but only
///     when SAP knows that code, so a real typo is reported, not guessed.
/// </summary>
public static class SupplierImport
{
    public const string CodeHeader    = "Supplier code";
    public const string OriginHeader  = "Origin";
    public const string NameHeader    = "SUPPLIER NAME";
    public const string Email1Header  = "E-mail Address 1";
    public const string Email2Header  = "E-mail Address 2";
    public const string ContactHeader = "Contact Person Name";

    /// <summary>Reads the first sheet that has a supplier-code header. Columns
    /// are found by header text, so reordering or hiding them is harmless.</summary>
    public static (List<SupplierImportRow> Rows, string? Error) Read(Stream xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        foreach (var ws in wb.Worksheets)
        {
            var headerRow = ws.FirstRowUsed();
            if (headerRow == null) continue;
            var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in headerRow.CellsUsed())
                header[Normalise(cell.GetString())] = cell.Address.ColumnNumber;
            if (!header.ContainsKey(Normalise(CodeHeader))) continue;
            if (!header.ContainsKey(Normalise(Email1Header)))
                return (new(), $"The sheet '{ws.Name}' has no '{Email1Header}' column.");

            int? Col(string h) => header.TryGetValue(Normalise(h), out var c) ? c : null;
            var codeCol = Col(CodeHeader)!.Value;
            var rows = new List<SupplierImportRow>();
            foreach (var row in ws.RowsUsed().Where(r => r.RowNumber() > headerRow.RowNumber()))
            {
                string? Text(int? col)
                {
                    if (col == null) return null;
                    var s = row.Cell(col.Value).GetFormattedString().Trim();
                    return s.Length == 0 ? null : s;
                }
                var emails = SupplierEmails.Parse(Text(Col(Email1Header)) + ";" + Text(Col(Email2Header))).Valid
                                           .Where(e => !SupplierEmails.IsInternal(e)).ToList();
                var name   = Text(Col(NameHeader));
                var code   = CodeText(row.Cell(codeCol));
                if (code.Length == 0 && name == null && emails.Count == 0) continue;   // blank line
                rows.Add(new SupplierImportRow(row.RowNumber(), code, Text(Col(OriginHeader)), name,
                                               Text(Col(ContactHeader)), emails));
            }
            return (rows, null);
        }
        return (new(), $"No sheet has a '{CodeHeader}' column. Upload the supplier contacts workbook.");
    }

    /// <summary>Applies the rules above. <paramref name="sapHas"/> answers
    /// whether a vendor number exists in SAP.</summary>
    public static (List<SupplierImportRow> Accepted, List<SupplierImportSkip> Skipped) Plan(
        IReadOnlyList<SupplierImportRow> rows, Func<string, bool> sapHas)
    {
        var skipped = new List<SupplierImportSkip>();
        var coded   = new List<SupplierImportRow>();
        foreach (var r in rows)
        {
            if (r.Code.Length == 0)
            {
                skipped.Add(new(r.RowNo, "", r.Name, "No supplier code"));
                continue;
            }
            var code = r.Code;
            if (code.Length == 7 && code.All(char.IsDigit) && (code.StartsWith("8000") || code.StartsWith("4000")))
            {
                var fixedCode = code[..4] + "0" + code[4..];
                if (!sapHas(fixedCode))
                {
                    skipped.Add(new(r.RowNo, code, r.Name, $"Code has 7 digits and {fixedCode} is not in SAP"));
                    continue;
                }
                code = fixedCode;
            }
            if (code.Length > 40 || !code.All(char.IsLetterOrDigit))
            {
                skipped.Add(new(r.RowNo, r.Code, r.Name, "Not a supplier code"));
                continue;
            }
            coded.Add(r with { Code = code });
        }

        var repeated = coded.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<SupplierImportRow>();
        foreach (var r in coded)
        {
            if (repeated.Contains(r.Code))
                skipped.Add(new(r.RowNo, r.Code, r.Name, "Code is on more than one row"));
            else
                accepted.Add(r);
        }
        skipped.Sort((a, b) => a.RowNo.CompareTo(b.RowNo));
        return (accepted, skipped);
    }

    private static string Normalise(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The code as text: the workbook holds most codes as numbers
    /// (80000444) and some as text ('80000554').</summary>
    private static string CodeText(IXLCell cell)
    {
        if (cell.DataType == XLDataType.Number)
        {
            var d = cell.GetDouble();
            return d == Math.Floor(d) ? ((long)d).ToString() : d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return cell.GetFormattedString().Trim();
    }
}
