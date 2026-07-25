using System.Globalization;
using ClosedXML.Excel;
using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// Turns a <see cref="ReportBuilderDefinition"/> + a browse filter into a
/// professional Excel workbook. Reuses the flat-defect stream
/// (<see cref="IQualityOrderService.StreamFlatDefectRowsAsync"/>) and collapses
/// its one-row-per-sample×defect grain into ONE row per sample, then projects
/// the design's columns. Fields marked as "header" become a key/value block at
/// the top; the rest become the data grid, with an optional totals row.
/// </summary>
public interface IReportBuilderExporter
{
    Task<byte[]> BuildAsync(ReportBuilderDefinition def, string reportName,
        FlatDefectFilter filter, CancellationToken ct);

    Task<ReportPreview> PreviewAsync(ReportBuilderDefinition def, FlatDefectFilter filter,
        int maxRows, CancellationToken ct);
}

public sealed class ReportPreview
{
    public List<KeyValuePair<string, string>> Header { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public int SampleCount { get; set; }
    public bool Truncated { get; set; }
}

public class ReportBuilderExporter : IReportBuilderExporter
{
    private readonly IQualityOrderService _qos;

    // Bound memory the same way FlatDefectsExcel does.
    private const int MaxRows = 250_000;

    public ReportBuilderExporter(IQualityOrderService qos) => _qos = qos;

    // ---- data assembly -----------------------------------------------------

    private async Task<List<List<FlatDefectRow>>> GroupBySampleAsync(
        FlatDefectFilter filter, CancellationToken ct)
    {
        var order = new List<List<FlatDefectRow>>();
        var byId = new Dictionary<long, List<FlatDefectRow>>();
        int count = 0;
        await foreach (var r in _qos.StreamFlatDefectRowsAsync(filter, ct))
        {
            if (count >= MaxRows) break;
            if (!byId.TryGetValue(r.SampleId, out var list))
            {
                list = new List<FlatDefectRow>();
                byId[r.SampleId] = list;
                order.Add(list);
            }
            list.Add(r);
            count++;
        }
        return order;
    }

    /// <summary>Projects one sample into cell values aligned to def.Columns,
    /// building the numeric map so calculated columns can reference the ones
    /// before them.</summary>
    private static object?[] ProjectSample(ReportBuilderDefinition def, List<FlatDefectRow> group)
    {
        var rep = group[0];
        var cells = new object?[def.Columns.Count];
        var numeric = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < def.Columns.Count; i++)
        {
            var col = def.Columns[i];
            object? val = null;

            switch (col.Kind)
            {
                case ColumnKinds.Static:
                    if (ReportBuilderRegistry.TryGetStatic(col.Key, out var sf)) val = sf.Get(rep);
                    break;

                case ColumnKinds.Reading:
                    if (rep.Readings.TryGetValue(Strip(col.Key, ReportBuilderRegistry.ReadingPrefix), out var rv))
                        val = ParseMaybeNumber(rv);
                    break;

                case ColumnKinds.SampleHeader:
                    rep.SampleHeaderValues.TryGetValue(Strip(col.Key, ReportBuilderRegistry.SampleHeaderPrefix), out var shv);
                    val = shv;
                    break;

                case ColumnKinds.MaterialHeader:
                    rep.MaterialHeaderValues.TryGetValue(Strip(col.Key, ReportBuilderRegistry.MaterialHeaderPrefix), out var mhv);
                    val = mhv;
                    break;

                case ColumnKinds.Defect:
                    if (int.TryParse(Strip(col.Key, ReportBuilderRegistry.DefectPrefix), out var did))
                    {
                        var drow = group.FirstOrDefault(x => x.DefectId == did);
                        if (drow != null)
                            val = col.DefectValue == DefectValueModes.Percent
                                ? (object?)drow.DefectPercentage
                                : drow.DefectValue;
                    }
                    break;

                case ColumnKinds.Calc:
                    val = ReportFormula.Evaluate(col.Formula, numeric);
                    break;

                case ColumnKinds.Empty:
                    val = null;
                    break;
            }

            cells[i] = val;
            var num = ToDouble(val);
            if (num.HasValue) numeric[LabelOf(col)] = num;
        }
        return cells;
    }

    // ---- Excel -------------------------------------------------------------

    public async Task<byte[]> BuildAsync(ReportBuilderDefinition def, string reportName,
        FlatDefectFilter filter, CancellationToken ct)
    {
        var groups = await GroupBySampleAsync(filter, ct);
        var rows = groups.Select(g => ProjectSample(def, g)).ToList();

        var headerCols = IndexedColumns(def, ColumnPlacements.Header);
        var dataCols   = IndexedColumns(def, ColumnPlacements.Column);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report");
        int r = 1;

        // Title.
        var title = ws.Cell(r, 1);
        title.Value = string.IsNullOrWhiteSpace(reportName) ? "Report" : reportName;
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 14;
        r += 2;

        // Header block: one label:value pair per header column, using the
        // distinct values across the result (single value shown as-is, several
        // joined, many collapsed to "(multiple)").
        foreach (var (col, idx) in headerCols)
        {
            var label = LabelOf(col);
            var distinct = rows
                .Select(row => Display(row[idx], col))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string value = distinct.Count == 0 ? ""
                : distinct.Count == 1 ? distinct[0]
                : distinct.Count <= 4 ? string.Join(", ", distinct)
                : "(multiple)";

            ws.Cell(r, 1).Value = label;
            ws.Cell(r, 1).Style.Font.Bold = true;
            ws.Cell(r, 2).Value = value;
            r++;
        }
        if (headerCols.Count > 0) r++; // spacer

        // Data grid header row.
        int headerRow = r;
        for (int c = 0; c < dataCols.Count; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = LabelOf(dataCols[c].Col);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }
        r++;

        // Data rows.
        int firstDataRow = r;
        foreach (var row in rows)
        {
            for (int c = 0; c < dataCols.Count; c++)
                SetCell(ws.Cell(r, c + 1), row[dataCols[c].Index], dataCols[c].Col);
            r++;
        }
        int lastDataRow = r - 1;

        // Totals row.
        bool anyTotals = dataCols.Any(d => ColumnTotals.IsValid(d.Col.Total));
        if (anyTotals && rows.Count > 0)
        {
            ws.Cell(r, 1).Value = "Total";
            ws.Cell(r, 1).Style.Font.Bold = true;
            for (int c = 0; c < dataCols.Count; c++)
            {
                var col = dataCols[c].Col;
                if (!ColumnTotals.IsValid(col.Total)) continue;
                var nums = rows.Select(row => ToDouble(row[dataCols[c].Index]))
                               .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (nums.Count == 0) continue;
                double total = col.Total == ColumnTotals.Avg ? nums.Average() : nums.Sum();
                var cell = ws.Cell(r, c + 1);
                cell.Value = total;
                cell.Style.NumberFormat.Format = NumberFormatFor(col);
                cell.Style.Font.Bold = true;
                cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            }
            ws.Cell(r, 1).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        // Finish: freeze header, autofilter, size columns.
        if (dataCols.Count > 0 && lastDataRow >= firstDataRow)
        {
            ws.Range(headerRow, 1, lastDataRow, dataCols.Count).SetAutoFilter();
            ws.SheetView.FreezeRows(headerRow);
        }
        ws.Columns().AdjustToContents(10d, 45d);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<ReportPreview> PreviewAsync(ReportBuilderDefinition def,
        FlatDefectFilter filter, int maxRows, CancellationToken ct)
    {
        var groups = await GroupBySampleAsync(filter, ct);
        var headerCols = IndexedColumns(def, ColumnPlacements.Header);
        var dataCols   = IndexedColumns(def, ColumnPlacements.Column);

        var preview = new ReportPreview
        {
            SampleCount = groups.Count,
            Truncated   = groups.Count > maxRows,
            Columns     = dataCols.Select(d => LabelOf(d.Col)).ToList(),
        };

        var shown = groups.Take(maxRows).Select(g => ProjectSample(def, g)).ToList();
        foreach (var (col, idx) in headerCols)
        {
            var distinct = shown.Select(row => Display(row[idx], col))
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string value = distinct.Count == 0 ? "" : distinct.Count == 1 ? distinct[0]
                : distinct.Count <= 4 ? string.Join(", ", distinct) : "(multiple)";
            preview.Header.Add(new(LabelOf(col), value));
        }
        foreach (var row in shown)
            preview.Rows.Add(dataCols.Select(d => Display(row[d.Index], d.Col)).ToList());
        return preview;
    }

    // ---- helpers -----------------------------------------------------------

    private readonly record struct IndexedColumn(ReportColumn Col, int Index);

    private static List<IndexedColumn> IndexedColumns(ReportBuilderDefinition def, string placement)
    {
        var list = new List<IndexedColumn>();
        for (int i = 0; i < def.Columns.Count; i++)
            if (string.Equals(def.Columns[i].Placement, placement, StringComparison.OrdinalIgnoreCase))
                list.Add(new IndexedColumn(def.Columns[i], i));
        return list;
    }

    private static string Strip(string key, string prefix)
        => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;

    private static string LabelOf(ReportColumn col)
    {
        if (!string.IsNullOrWhiteSpace(col.Label)) return col.Label!;
        if (col.Kind == ColumnKinds.Static && ReportBuilderRegistry.TryGetStatic(col.Key, out var sf))
            return sf.Label;
        return col.Key;
    }

    private static bool IsNumericColumn(ReportColumn col) =>
        col.Kind is ColumnKinds.Defect or ColumnKinds.Calc
        || (col.Kind == ColumnKinds.Static
            && ReportBuilderRegistry.TryGetStatic(col.Key, out var sf)
            && sf.Type == ReportFieldType.Number);

    private static bool IsDateColumn(ReportColumn col) =>
        col.Kind == ColumnKinds.Static
        && ReportBuilderRegistry.TryGetStatic(col.Key, out var sf)
        && sf.Type == ReportFieldType.Date;

    private static object? ParseMaybeNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
            ? d : s;
    }

    private static double? ToDouble(object? v) => v switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        short s => s,
        _ => null
    };

    private static string NumberFormatFor(ReportColumn col)
        => col.Kind == ColumnKinds.Defect && col.DefectValue == DefectValueModes.Percent
            ? "0.0"
            : "0.##";

    private static void SetCell(IXLCell cell, object? val, ReportColumn col)
    {
        switch (val)
        {
            case null:
                break;
            case DateTime dt:
                cell.Value = dt;
                cell.Style.DateFormat.Format = IsDateColumn(col) ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
                break;
            default:
                var num = ToDouble(val);
                if (num.HasValue)
                {
                    cell.Value = num.Value;
                    if (IsNumericColumn(col)) cell.Style.NumberFormat.Format = NumberFormatFor(col);
                }
                else
                {
                    cell.Value = val.ToString();
                }
                break;
        }
    }

    private static string Display(object? val, ReportColumn col) => val switch
    {
        null => "",
        DateTime dt => IsDateColumn(col) ? dt.ToString("yyyy-MM-dd") : dt.ToString("yyyy-MM-dd HH:mm"),
        double d => d.ToString("0.##", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.##", CultureInfo.InvariantCulture),
        _ => val.ToString() ?? ""
    };
}
