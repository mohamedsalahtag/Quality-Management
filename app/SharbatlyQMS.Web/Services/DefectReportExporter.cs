using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// What the Defect reports page posts to export the tab on screen: the same
/// table model it rendered (wwwroot/js/defect-reports.js), the filters and
/// period it was drawn for, and the chart as a PNG. The server only formats --
/// it never recomputes -- so the file always matches the page.
/// </summary>
public class DefectReportExportRequest
{
    public string Format { get; set; } = "xlsx";
    public string Title { get; set; } = "Defect report";
    public string Tab { get; set; } = "";
    public string? Hint { get; set; }
    public string Period { get; set; } = "";
    public List<string> Filters { get; set; } = new();
    /// <summary>[label, value] pairs: orders, samples, cartons, pending containers.</summary>
    public List<List<string>> Kpis { get; set; } = new();
    public string RowLabel { get; set; } = "";
    public List<DefectReportExportColumn> Columns { get; set; } = new();
    public List<DefectReportExportRow> Rows { get; set; } = new();
    /// <summary>data:image/png;base64,... or null.</summary>
    public string? Chart { get; set; }
    public string? Generated { get; set; }
}

public class DefectReportExportColumn
{
    public string Group { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Color { get; set; }
    /// <summary>defect | cattot | cat | all | week | cartons | trend</summary>
    public string Kind { get; set; } = "";
}

public class DefectReportExportRow
{
    public string Label { get; set; } = "";
    /// <summary>"" | grp | child | sub | gt | info</summary>
    public string Level { get; set; } = "";
    public int Indent { get; set; }
    public List<decimal?> Values { get; set; } = new();
    public List<decimal?> Trends { get; set; } = new();
}

public static class DefectReportExporter
{
    private const int MaxRows = 5000, MaxColumns = 400;

    public static void Validate(DefectReportExportRequest r)
    {
        if (r.Rows.Count > MaxRows || r.Columns.Count > MaxColumns)
            throw new ArgumentException("The report is too large to export; narrow the filters.");
    }

    private static byte[]? ChartBytes(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var comma = dataUrl.IndexOf(',');
        try { return Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl); }
        catch (FormatException) { return null; }
    }

    private static bool IsCount(string kind) => kind == "cartons";
    private static bool IsTotal(string kind) => kind is "cattot" or "all" or "cartons" or "trend";
    private static bool IsBold(string level) => level is "grp" or "sub" or "gt";

    private static string Format(decimal? v, string kind) => v is null ? ""
        : kind == "trend" ? (v > 0 ? "+" : "") + v.Value.ToString("0.00")
        : IsCount(kind) ? v.Value.ToString("#,##0") : v.Value.ToString("0.00");

    // ================================ Excel ================================
    public static byte[] Excel(DefectReportExportRequest r)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(SheetName(r.Tab));
        ws.Style.Font.FontName = "Calibri";
        ws.Style.Font.FontSize = 10;

        var row = 1;
        ws.Cell(row, 1).Value = $"Defect reports — {r.Title}";
        ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(15);
        row++;
        ws.Cell(row, 1).Value = r.Period;
        ws.Cell(row, 1).Style.Font.SetBold();
        row++;
        ws.Cell(row, 1).Value = "Filters: " + string.Join(" · ", r.Filters);
        row++;
        ws.Cell(row, 1).Value = string.Join("    ", r.Kpis.Where(k => k.Count == 2).Select(k => $"{k[0]}: {k[1]}"));
        ws.Cell(row, 1).Style.Font.SetFontColor(XLColor.FromHtml("#495057"));
        row++;
        if (!string.IsNullOrWhiteSpace(r.Hint))
        {
            ws.Cell(row, 1).Value = r.Hint + " Defect % = defective units ÷ inspected units. ▲ higher than the week before, ▼ lower.";
            ws.Cell(row, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#6c757d"));
            row++;
        }
        row++;

        var chart = ChartBytes(r.Chart);
        if (chart != null)
        {
            using var ms = new MemoryStream(chart);
            var pic = ws.AddPicture(ms).MoveTo(ws.Cell(row, 1));
            pic.Width = 1000; pic.Height = 300;
            row += 17;
        }

        // Header: a category row above the defect names when the table has
        // defect columns, then the column labels.
        var grouped = r.Columns.Any(c => c.Kind == "defect");
        var headTop = row;
        ws.Cell(row, 1).Value = r.RowLabel;
        if (grouped)
        {
            ws.Range(row, 1, row + 1, 1).Merge();
            var col = 2;
            for (var i = 0; i < r.Columns.Count;)
            {
                var c = r.Columns[i];
                var span = 1;
                if (!string.IsNullOrEmpty(c.Group))
                    while (i + span < r.Columns.Count && r.Columns[i + span].Group == c.Group) span++;
                var top = ws.Range(row, col, row, col + span - 1);
                if (!string.IsNullOrEmpty(c.Group))
                {
                    top.Merge(); top.FirstCell().Value = c.Group;
                    Fill(top, c.Color);
                    for (var k = 0; k < span; k++)
                    {
                        var cell = ws.Cell(row + 1, col + k);
                        cell.Value = r.Columns[i + k].Label;
                        cell.Style.Alignment.SetTextRotation(90);
                        if (r.Columns[i + k].Kind == "cattot") Fill(ws.Range(row + 1, col + k, row + 1, col + k), c.Color);
                    }
                }
                else
                {
                    ws.Range(row, col, row + 1, col).Merge().FirstCell().Value = c.Label;
                }
                col += span; i += span;
            }
            row += 2;
        }
        else
        {
            for (var i = 0; i < r.Columns.Count; i++)
            {
                var cell = ws.Cell(row, i + 2);
                cell.Value = r.Columns[i].Label;
                if (r.Columns[i].Kind == "cat") Fill(ws.Range(row, i + 2, row, i + 2), r.Columns[i].Color);
            }
            row++;
        }
        var head = ws.Range(headTop, 1, row - 1, r.Columns.Count + 1);
        head.Style.Font.SetBold();
        head.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center).Alignment.SetVertical(XLAlignmentVerticalValues.Bottom).Alignment.SetWrapText(true);
        foreach (var cell in head.Cells().Where(x => x.Style.Fill.BackgroundColor == XLColor.NoColor))
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#f1f3f5");
        var firstData = row;

        foreach (var rr in r.Rows)
        {
            var label = ws.Cell(row, 1);
            label.Value = rr.Label;
            label.Style.Alignment.Indent = rr.Indent * 2;
            for (var i = 0; i < r.Columns.Count && i < rr.Values.Count; i++)
            {
                // The cartons line of the weekly report sits in week columns but is a count.
                var kind = rr.Level == "info" ? "cartons" : r.Columns[i].Kind;
                var cell = ws.Cell(row, i + 2);
                var v = rr.Values[i];
                if (v is null) continue;
                cell.Value = (double)v.Value;
                cell.Style.NumberFormat.Format = kind == "cartons" ? "#,##0" : kind == "trend" ? "+0.00;-0.00;0.00" : "0.00";
                if (kind == "trend") cell.Style.Font.SetFontColor(v > 0 ? XLColor.FromHtml("#dc3545") : XLColor.FromHtml("#198754"));
                var t = i < rr.Trends.Count ? rr.Trends[i] : null;
                // Week-on-week: the cell turns red when it rose, green when it fell,
                // and says by how much in its note.
                if (t is not null && Math.Abs(t.Value) >= 0.005m)
                {
                    cell.CreateComment().AddText($"{(t > 0 ? "▲ +" : "▼ ")}{t.Value:0.00} on the week before");
                    cell.Style.Font.SetFontColor(t > 0 ? XLColor.FromHtml("#b02a37") : XLColor.FromHtml("#146c43"));
                }
                if (IsTotal(kind)) cell.Style.Font.SetBold();
            }
            var line = ws.Range(row, 1, row, r.Columns.Count + 1);
            if (IsBold(rr.Level)) line.Style.Font.SetBold();
            if (rr.Level is "gt" or "sub") line.Style.Fill.BackgroundColor = XLColor.FromHtml("#f1f3f5");
            if (rr.Level == "info") line.Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#6c757d"));
            row++;
        }
        if (row > firstData)
        {
            var table = ws.Range(headTop, 1, row - 1, r.Columns.Count + 1);
            table.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            table.Style.Border.SetInsideBorderColor(XLColor.FromHtml("#dee2e6")).Border.SetOutsideBorderColor(XLColor.FromHtml("#adb5bd"));
        }
        ws.Column(1).Width = 34;
        for (var i = 0; i < r.Columns.Count; i++) ws.Column(i + 2).Width = grouped && r.Columns[i].Kind == "defect" ? 7 : 11;
        if (grouped) ws.Row(headTop + 1).Height = 110;
        ws.SheetView.Freeze(firstData - 1, 1);
        ws.Cell(row + 1, 1).Value = $"Generated {r.Generated} from Sharbatly QMS. Finished inspections only, dated by receive date.";
        ws.Cell(row + 1, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#6c757d"));
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);

        using var outStream = new MemoryStream();
        wb.SaveAs(outStream);
        return outStream.ToArray();
    }

    private static void Fill(IXLRange range, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7) return;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(hex);
        range.Style.Font.SetFontColor(Ink(hex) ? XLColor.FromHtml("#212529") : XLColor.White);
    }

    /// <summary>True when dark text reads better on this fill.</summary>
    private static bool Ink(string hex)
    {
        var n = Convert.ToInt32(hex[1..], 16);
        return 0.299 * (n >> 16) + 0.587 * ((n >> 8) & 255) + 0.114 * (n & 255) > 150;
    }

    private static string SheetName(string tab)
    {
        var s = new string((string.IsNullOrWhiteSpace(tab) ? "Report" : tab).Where(ch => !"[]*?/\\:".Contains(ch)).ToArray());
        return s.Length > 31 ? s[..31] : s;
    }

    // ================================= PDF =================================
    public static byte[] Pdf(DefectReportExportRequest r)
    {
        var chart = ChartBytes(r.Chart);
        var cols = r.Columns.Count;
        // Shrink the type as the table widens so every column fits a landscape page.
        float size = cols > 40 ? 4.5f : cols > 28 ? 5.5f : cols > 18 ? 6.5f : 7.5f;
        var grouped = r.Columns.Any(c => c.Kind == "defect");

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(20);
            page.DefaultTextStyle(t => t.FontSize(size).FontFamily(Fonts.Calibri).FontColor("#212529"));

            page.Header().Column(h =>
            {
                h.Item().Row(rw =>
                {
                    rw.RelativeItem().Text($"Defect reports — {r.Title}").Bold().FontSize(14);
                    rw.ConstantItem(200).AlignRight().Text(t =>
                    {
                        t.Span("Page ").FontColor("#6c757d"); t.CurrentPageNumber(); t.Span(" / ").FontColor("#6c757d"); t.TotalPages();
                    });
                });
                h.Item().Text(r.Period).SemiBold().FontSize(9);
                h.Item().Text("Filters: " + string.Join(" · ", r.Filters)).FontSize(8).FontColor("#495057");
                h.Item().PaddingTop(2).Height(1).Background("#0d6efd");
            });

            page.Content().PaddingTop(6).Column(col =>
            {
                col.Spacing(6);
                col.Item().Row(rw =>
                {
                    foreach (var k in r.Kpis.Where(k => k.Count == 2))
                        rw.RelativeItem().Border(0.5f).BorderColor("#dee2e6").Padding(4).Column(c =>
                        {
                            c.Item().Text(k[0].ToUpperInvariant()).FontSize(6.5f).FontColor("#6c757d");
                            c.Item().Text(k[1]).Bold().FontSize(11);
                        });
                });
                if (chart != null) col.Item().Height(170).Image(chart).FitArea();

                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(grouped ? 5 : 4);
                        foreach (var c in r.Columns) cd.RelativeColumn(c.Kind == "trend" || c.Kind == "cartons" ? 1.4f : 1);
                    });
                    t.Header(hd =>
                    {
                        if (grouped)
                        {
                            hd.Cell().RowSpan(2).Element(Head).Text(r.RowLabel).Bold();
                            for (var i = 0; i < r.Columns.Count;)
                            {
                                var c = r.Columns[i]; uint span = 1;
                                if (!string.IsNullOrEmpty(c.Group))
                                    while (i + span < r.Columns.Count && r.Columns[i + (int)span].Group == c.Group) span++;
                                if (!string.IsNullOrEmpty(c.Group))
                                    hd.Cell().ColumnSpan(span).Element(x => Head(x, c.Color)).AlignCenter().Text(c.Group).Bold().FontColor(InkHex(c.Color));
                                else
                                    hd.Cell().RowSpan(2).Element(Head).AlignRight().Text(c.Label).Bold();
                                i += (int)span;
                            }
                            foreach (var c in r.Columns.Where(c => !string.IsNullOrEmpty(c.Group)))
                                hd.Cell().Element(x => Head(x, c.Kind == "cattot" ? c.Color : null)).Height(70).AlignBottom()
                                  .RotateLeft().Text(c.Label).FontColor(c.Kind == "cattot" ? InkHex(c.Color) : "#212529");
                        }
                        else
                        {
                            hd.Cell().Element(Head).Text(r.RowLabel).Bold();
                            foreach (var c in r.Columns)
                                hd.Cell().Element(x => Head(x, c.Kind == "cat" ? c.Color : null)).AlignRight()
                                  .Text(c.Label).Bold().FontColor(c.Kind == "cat" ? InkHex(c.Color) : "#212529");
                        }
                    });

                    foreach (var rr in r.Rows)
                    {
                        var fill = rr.Level is "gt" or "sub" ? "#f1f3f5" : null;
                        var bold = IsBold(rr.Level);
                        var lab = t.Cell().Element(x => Body(x, fill)).PaddingLeft(rr.Indent * 8).Text(rr.Label);
                        if (bold) lab.Bold();
                        if (rr.Level == "info") lab.Italic().FontColor("#6c757d");
                        for (var i = 0; i < r.Columns.Count; i++)
                        {
                            var kind = rr.Level == "info" ? "cartons" : r.Columns[i].Kind;
                            var v = i < rr.Values.Count ? rr.Values[i] : null;
                            var tr = i < rr.Trends.Count ? rr.Trends[i] : null;
                            t.Cell().Element(x => Body(x, fill)).AlignRight().Text(txt =>
                            {
                                if (tr is not null && Math.Abs(tr.Value) >= 0.005m)
                                    txt.Span((tr > 0 ? "+" : "−") + " ").FontColor(tr > 0 ? "#dc3545" : "#198754").Bold();
                                var s = txt.Span(Format(v, kind));
                                if (bold || IsTotal(kind)) s.Bold();
                                if (kind == "trend" && v is not null) s.FontColor(v > 0 ? "#dc3545" : "#198754");
                                if (kind == "cartons") s.FontColor("#6c757d");
                            });
                        }
                    }
                });
                col.Item().Text("Defect % = defective units ÷ inspected units; a category total adds up its defects. " +
                                "Week cells marked + rose and − fell on the week before. Finished inspections only, dated by receive date.")
                   .FontSize(6.5f).FontColor("#6c757d");
            });

            page.Footer().AlignCenter().Text($"Generated {r.Generated} from Sharbatly QMS").FontSize(6.5f).FontColor("#6c757d");
        })).GeneratePdf();
    }

    private static IContainer Head(IContainer c) => Head(c, null);
    private static IContainer Head(IContainer c, string? fill) =>
        c.Background(string.IsNullOrWhiteSpace(fill) ? "#f1f3f5" : fill!).Border(0.5f).BorderColor("#dee2e6").PaddingVertical(2).PaddingHorizontal(3);
    private static IContainer Body(IContainer c, string? fill)
    {
        if (fill != null) c = c.Background(fill);
        return c.BorderBottom(0.5f).BorderColor("#e9ecef").PaddingVertical(1.5f).PaddingHorizontal(3);
    }
    private static string InkHex(string? hex) =>
        string.IsNullOrWhiteSpace(hex) || hex.Length != 7 ? "#212529" : (Ink(hex) ? "#212529" : "#ffffff");
}
