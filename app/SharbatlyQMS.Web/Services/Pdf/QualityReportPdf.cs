using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// QuestPDF document for the QMS Quality Control Report.
///
/// One flowing content stream (was two fixed pages before 2026-05-24): the
/// shipment details box, the material table, the grouped-summary blocks
/// (one per (MaterialGroup, Brand, Variety, Grade)) and the per-sample
/// detail cards all share one Page descriptor so QuestPDF paginates only
/// as needed -- no artificial gap between the summary section and the
/// per-sample cards. The image gallery is still its own Page when there
/// are photos.
///
/// Each per-sample card uses the same Major / Minor banner+grid pattern
/// as the page-1 grouped summary, iterating the FULL active defect
/// catalog for the sample's material group (zeros for any defect the
/// operator didn't record), so the layout is consistent and the defect
/// list is always the complete catalog.
/// </summary>
public static class QualityReportPdf
{
    private const string Accent      = "#0d6efd";   // brand blue — section headings only
    private const string AccentLight = "#e7f1ff";
    private const string GridLine    = "#9aa1a8";

    // Calm layout (2026-07-08): the report is deliberately quiet — no boxes,
    // no filled banners, no tinted grids. Structure comes from headings +
    // whitespace + a single hairline rule. Colour is reserved for section
    // headings (Accent) and defect percentages (their category colour).
    private const string Hairline    = "#dde1e6";

    // Fallback palette for categories with no configured colour. Indexed by
    // the section's position so distinct categories still read differently.
    private static readonly string[] FallbackPalette =
        { "#b02a37", "#7a1620", "#ffc107", "#6c757d", "#198754", "#0d6efd", "#6f42c1", "#fd7e14" };

    // Banner background for a category section: its own colour, else a
    // palette entry by position.
    private static string BannerBg(string? colorHex, int index) =>
        !string.IsNullOrWhiteSpace(colorHex) && IsHex(colorHex)
            ? colorHex!
            : FallbackPalette[index % FallbackPalette.Length];

    private static bool IsHex(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");

    // A category's colour, darkened toward a legible tone for use as TEXT on
    // white. Very light hues (e.g. a yellow "Minor" category) are unreadable
    // as-is, so darken until the luminance is comfortably below mid. Falls
    // back to a neutral dark grey when no/invalid colour is configured.
    private static string ReadableOnWhite(string? hex)
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

    // A thin hairline rule — the only "structure" line in the calm layout.
    private static void Rule(QuestPDF.Infrastructure.IContainer c) =>
        c.PaddingVertical(2).LineHorizontal(0.5f).LineColor(Hairline);

    // Calm section heading: understated soft-blue title followed by a hairline.
    private static void CalmHeading(ColumnDescriptor col, string text, float size = 12)
    {
        col.Item().Text(text).FontSize(size).SemiBold().FontColor(Accent);
        col.Item().Element(Rule);
    }

    public static byte[] Build(QualityReportData d)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page => RenderMainPage(page, d));

            // Photo appendix: rendered when ANY of arrival, material (legacy),
            // or per-sample photos exist. Per-sample photos are the V31
            // canonical source; the material map is kept for back-compat but
            // is empty for new reports.
            bool anySampleImgs   = d.Samples.Any(sb => sb.Images.Count > 0);
            bool anyMaterialImgs = d.MaterialImages.Any(kv => kv.Value.Count > 0);
            bool anyArrivalImgs  = d.ArrivalImages.Count > 0;
            if (anySampleImgs || anyMaterialImgs || anyArrivalImgs)
                container.Page(page => RenderImagesPage(page, d));
        });
        return doc.GeneratePdf();
    }

    // ============================== Main content ============================
    private static void RenderMainPage(QuestPDF.Fluent.PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeaderBand(h, d));
        page.Footer().Element(f => RenderFooterBand(f, d));

        // Single Content stream. Spacing(4) tightens the inter-block gap
        // (was 6) and PaddingTop is no longer added per-section so the
        // group summaries and per-sample cards sit immediately under the
        // material table -- no empty whitespace.
        page.Content().PaddingVertical(4).Column(col =>
        {
            col.Spacing(5);

            // Date + QC (quality order) number strip, top-right.
            col.Item().AlignRight().Text(t =>
            {
                t.Span("Date  ").FontColor(Colors.Grey.Darken1);
                t.Span(d.Shipment?.InspectionDate?.ToString("dd/MM/yyyy")
                       ?? d.GeneratedAt.ToLocalTime().ToString("dd/MM/yyyy")).Bold();
            });
            col.Item().AlignRight().Text(t =>
            {
                t.Span("QC No.  ").FontColor(Colors.Grey.Darken1);
                t.Span(d.QualityOrder.QualityOrderNo).Bold();
            });

            // Shipment details
            col.Item().Element(c => RenderShipmentDetails(c, d));

            // Material table
            col.Item().Element(c => RenderMaterialTable(c, d));

            // ---- Summary ----
            // Calm layout: one understated "summary" heading, then each grouped
            // record separated by a hairline — no boxes, no coloured frames.
            CalmHeading(col, "summary", 13);
            if (d.GroupSummaries.Count == 0)
                col.Item().Text("(no samples recorded yet — the summary appears once samples exist)")
                    .Italic().FontColor(Colors.Grey.Darken1);
            for (int gi = 0; gi < d.GroupSummaries.Count; gi++)
            {
                if (gi > 0) col.Item().Element(Rule);   // divider between records
                var g = d.GroupSummaries[gi];
                col.Item().Element(c => RenderGroupSummary(c, g));
            }

            // Arrival photos live at the start of the appendix page (see
            // RenderImagesPage), so the main page stays focused on data.

            // ---- Sample Details ----
            // Each material's identity renders once on a calm material card;
            // its samples render beneath it. The forced page breaks were removed
            // to save paper; sections are told apart by heading + whitespace.
            // d.Samples is pre-ordered by (QoMaterialId, SampleNo).
            if (d.Samples.Count > 0)
            {
                CalmHeading(col, "Sample Details", 13);

                foreach (var grp in d.Samples.GroupBy(s => s.Sample.QoMaterialId))
                {
                    var first = grp.First();
                    // Material card: ShowEntire so its header never orphans at a
                    // page bottom with no samples following.
                    col.Item().ShowEntire().Element(mc =>
                        RenderMaterialCard(mc, first.Material, first.MaterialHeaderValues, d.HeaderFieldScopeById));

                    // Do NOT ShowEntire a whole sample card: a large defect
                    // catalog can exceed one page and ShowEntire would throw a
                    // DocumentLayoutException. Letting it split is preferable.
                    foreach (var s in grp)
                        col.Item().Element(sc => RenderSampleDetail(sc, d, s));
                }
            }
        });
    }

    private static void RenderHeaderBand(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d)
    {
        container.Row(row =>
        {
            row.ConstantItem(70).AlignMiddle().Element(e =>
            {
                // Branding logo from Site Configuration → Branding tab.
                // Falls back to a "QMS" badge so the header always renders
                // something, even when the file is missing or no logo was
                // ever uploaded.
                if (!string.IsNullOrWhiteSpace(d.LogoAbsolutePath) && System.IO.File.Exists(d.LogoAbsolutePath))
                {
                    // Guard the image load: a corrupt/truncated/unsupported logo
                    // file would otherwise throw during GeneratePdf and break
                    // EVERY quality report (incl. supplier emails). Mirror the
                    // ArrivalReportPdf fallback.
                    try { e.Height(45).AlignLeft().Image(d.LogoAbsolutePath).FitArea(); return; }
                    catch { /* fall through to placeholder */ }
                }
                e.Width(45).Height(45)
                    .Background(Accent)
                    .AlignCenter().AlignMiddle()
                    .Text("QMS").FontColor(Colors.White).FontSize(10).Bold();
            });
            row.RelativeItem().AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Text(d.CompanyName).FontSize(11).Bold();
                c.Item().AlignCenter().Text("Quality Control Report").FontSize(14).Bold().FontColor(Accent);
            });
            // Dynamic page count -- single content stream lets QuestPDF
            // paginate naturally, so the header shows "Page N / Total"
            // rather than a fixed "Page 1 of 2".
            row.ConstantItem(60).AlignMiddle().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken1));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" / ");
                t.TotalPages();
            });
        });
    }

    private static void RenderFooterBand(QuestPDF.Infrastructure.IContainer container, QualityReportData d)
    {
        container.PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1)
            .PaddingTop(4).AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken2));
                t.Span(d.CompanyFooter);
            });
    }

    private static void RenderShipmentDetails(QuestPDF.Infrastructure.IContainer container, QualityReportData d)
    {
        var s = d.Shipment;
        var cl = d.Checklist;
        container.Column(col =>
        {
            CalmHeading(col, "Shipment Details");
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Column(c => {
                    Field(c, "Shipper",          d.Arrival.VendorName);
                    // Report Location is the receiving plant shown in the
                    // container details, not the shipment arrival place.
                    Field(c, "Report Location",  d.Arrival.Plant);
                    Field(c, "Bill of Lading No.", d.Arrival.BolNo);
                    Field(c, "Container",        d.Arrival.ContainerNo);
                    Field(c, "Purch.Doc.",       d.Arrival.Ebeln);
                    Field(c, "Country Of Origin",s?.LoadingCountry);
                    Field(c, "Loading Port",     s?.LoadingPort);
                    Field(c, "Port Of Arrival",  s?.ArrivalPlace);
                    Field(c, "Loading Date",     s?.SailingDate?.ToString("MMM dd, yyyy"));
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Vessel Name",      s?.VesselName);
                    Field(c, "Arrival Date",     s?.ArrivalDate?.ToString("MMM dd, yyyy"));
                    Field(c, "Pullout Date",     s?.PullOutDate?.ToString("MMM dd, yyyy"));
                    Field(c, "Receive Date",     s?.ReceiveDate?.ToString("MMM dd, yyyy"));
                    Field(c, "Unloading Date",   s?.UnloadingDate?.ToString("MMM dd, yyyy"));
                    Field(c, "Inspection Date",  s?.InspectionDate?.ToString("MMM dd, yyyy"));
                    Field(c, "Transit Days",     s?.TransitDays?.ToString());
                    Field(c, "Discharge Date",   s?.DischargeDate?.ToString("MMM dd, yyyy"));
                    // Time Bar = whole days between the chosen basis date and the
                    // date the quality order was finished (Closed / ClosedAt);
                    // falls back to the report date when the QO is not yet
                    // closed. The basis is Discharge date by default, or Arrival
                    // date when Report.TimeBarBasis says so (Admin → Settings →
                    // Report). Blank when the basis date is missing.
                    var basisDate = d.TimeBarBasis == TimeBarBases.Arrival ? s?.ArrivalDate : s?.DischargeDate;
                    var basisLabel = d.TimeBarBasis == TimeBarBases.Arrival ? "Arrival" : "Discharge";
                    Field(c, $"Time Bar ({basisLabel})", TimeBarDays(basisDate, d.QualityOrder.ClosedAt ?? d.GeneratedAt));
                    Field(c, "Date",             d.GeneratedAt.ToLocalTime().ToString("MMM dd, yyyy"));
                    Field(c, "Logger Serial",    cl?.DataLoggerSerial);
                });
                row.RelativeItem().Column(c => {
                    // Col-3 labels are long ("External damage to container",
                    // "Logger active & data available", etc.). Widen the
                    // label slot from the default 78 so each label fits on
                    // one line instead of wrapping.
                    const float w = 130;
                    // Seal No lists all recorded seals (stored newline-
                    // delimited, up to 4). Temperature reads the data-logger
                    // temperature from the arrival checklist, not the set point.
                    Field(c, "Seal No",                        JoinLines(cl?.SealNo),               labelWidth: w);
                    Field(c, "Temperature",                    cl?.LoggerTemperature?.ToString(),   labelWidth: w);
                    Field(c, "Pulp Temperature",               JoinTemps(cl),                       labelWidth: w);
                    Field(c, "Joint Survey",                   YN(s?.JointSurvey),                  labelWidth: w);
                    Field(c, "Seal Intact?",                   YN(cl?.SealIntact),                  labelWidth: w);
                    Field(c, "External damage to container",   YN(cl?.ExternalDamageExists),        labelWidth: w);
                    Field(c, "Visual cargo condition acceptable", YN(cl?.VisualCargoAcceptable),    labelWidth: w);
                    Field(c, "Logger active & data available", YN(cl?.LoggerActiveDataAvailable),   labelWidth: w);
                });
            });

            // Inspector's free-text notes (2026-05-25 fix). Full-width
            // block under the 3-column grid so multi-paragraph notes
            // don't deform the columns. Only rendered when populated --
            // a blank notes field would otherwise eat 1-2 lines of
            // valuable real estate at the top of the report.
            if (!string.IsNullOrWhiteSpace(cl?.Notes))
            {
                col.Item().PaddingTop(4).Column(nc =>
                {
                    nc.Item().Text("inspector notes").FontSize(8).FontColor(Colors.Grey.Darken1);
                    nc.Item().PaddingTop(1).Text(cl!.Notes!).FontSize(8);
                });
            }
        });
    }

    private static void RenderMaterialTable(QuestPDF.Infrastructure.IContainer container, QualityReportData d)
    {
        // Calm table: no cell borders or fills. Structure comes from a hairline
        // under the header row and a hairline between body rows.
        container.Column(col =>
        {
            CalmHeading(col, "Materials");
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.5f);
                    c.RelativeColumn(4f);
                    c.RelativeColumn(1.2f);
                    c.RelativeColumn(1.5f);
                    c.RelativeColumn(1.2f);
                    c.RelativeColumn(0.7f);
                });

                QuestPDF.Infrastructure.IContainer HeadCell(QuestPDF.Infrastructure.IContainer c) =>
                    c.BorderBottom(0.8f).BorderColor(Hairline).PaddingVertical(3).PaddingRight(4);
                QuestPDF.Infrastructure.IContainer BodyCell(QuestPDF.Infrastructure.IContainer c) =>
                    c.BorderBottom(0.5f).BorderColor(Hairline).PaddingVertical(2).PaddingRight(4);

                table.Header(header =>
                {
                    var labels = new[] { "Material", "Material description", "Origin", "Material Group", "Quantity", "Unit" };
                    for (int i = 0; i < labels.Length; i++)
                    {
                        var cell = HeadCell(header.Cell());
                        (i == 4 ? cell.AlignRight() : cell)
                            .Text(labels[i]).FontColor(Colors.Grey.Darken1).SemiBold().FontSize(7);
                    }
                });

                foreach (var m in d.Materials)
                {
                    BodyCell(table.Cell()).Text(m.MaterialNo);
                    BodyCell(table.Cell()).Text(m.MaterialDesc ?? "");
                    BodyCell(table.Cell()).Text(m.Origin ?? "");
                    BodyCell(table.Cell()).Text(m.MaterialGroup ?? "");
                    // Quantity + Unit joined from qms_arrival_item.
                    BodyCell(table.Cell()).AlignRight().Text(m.Quantity?.ToString("0.###") ?? "");
                    BodyCell(table.Cell()).Text(m.Uom ?? "");
                }
            });
        });
    }

    // Page 1 summary block: rolls up every material that shares
    // (MaterialGroup, Brand, Variety, Grade) into one section. Defect
    // percentages = Σ value / Σ size × 100; Major/Minor totals are the
    // arithmetic sum of each section's per-defect percentages. Reading
    // values come pre-rendered from BuildGroupSummariesAsync per each
    // reading-type's display_mode (text|count|sum|sum_over_size|formula).
    //
    // Layout (top to bottom):
    //   - Header bar with the grouping key in plain language
    //   - Three-column labeled grouping fields (Product / Brand / Variety
    //     / Grade / Material Group  +  Materials / Samples / Sample Size
    //     +  Gross / Tara / Net) so the auditor sees exactly which group
    //     they're looking at and the totals that feed the per-defect
    //     percentages
    //   - Optional Readings table (per display_mode)
    //   - FULL-WIDTH PINK "MAJOR DEFECTS" banner, then the Major grid
    //   - FULL-WIDTH YELLOW "MINOR DEFECTS" banner, then the Minor grid
    // The two banners are stacked (not side-by-side) so it's never
    // ambiguous which defect belongs to which section.
    private static void RenderGroupSummary(QuestPDF.Infrastructure.IContainer container, MaterialGroupSummary g)
    {
        container.Column(col =>
        {
            // ----- Header line: grouping key spelled out + material-group
            //       subtitle. No fill, no box — a calm record header.
            col.Item().Row(hr =>
            {
                hr.RelativeItem().Text(t =>
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(g.Brand))   parts.Add(g.Brand!);
                    if (!string.IsNullOrWhiteSpace(g.Variety)) parts.Add(g.Variety!);
                    if (!string.IsNullOrWhiteSpace(g.Grade))   parts.Add(g.Grade!);
                    if (parts.Count == 0) parts.Add("(brand / variety / grade not set)");
                    t.Span(string.Join("  ·  ", parts)).SemiBold().FontColor(Accent);
                });
                hr.ConstantItem(180).AlignRight().Text(t =>
                {
                    t.Span("Material Group: ").FontColor(Colors.Grey.Darken1).FontSize(7);
                    t.Span(g.MaterialGroup).SemiBold().FontSize(7);
                    if (!string.IsNullOrWhiteSpace(g.MaterialGroupDesc))
                    {
                        t.Span(" — ").FontColor(Colors.Grey.Darken1).FontSize(7);
                        t.Span(g.MaterialGroupDesc!).FontColor(Colors.Grey.Darken1).FontSize(7);
                    }
                });
            });

            // ----- Three-column labeled grouping fields. Column 1 is the
            //       grouping identity (Product/Brand/Variety/Grade); column 2
            //       is the material group + counts; column 3 carries Sample
            //       Size and, only for multi-material groups, the rolled-up PO
            //       quantity. Gross/Tara/Net weights were removed -- Gross and
            //       Net now appear as per-sample averages in the Readings table.
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Column(c => {
                    Field(c, "Product",        Dash(g.MajorCategory));
                    Field(c, "Brand",          Dash(g.Brand));
                    Field(c, "Variety",        Dash(g.Variety));
                    Field(c, "Grade",          Dash(g.Grade));
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Material Group",     Dash(g.MaterialGroup));
                    Field(c, "Count of Materials", g.MaterialCount.ToString());
                    Field(c, "Samples",            g.SampleCount + " Cartons");
                });
                row.RelativeItem().Column(c => {
                    // Unit comes from Parameters > Report Units for this material
                    // group -- "Pieces" unless the group counts boxes, cartons, ...
                    Field(c, "Sample Size", g.SumSampleSize + " " + g.SampleUnit);
                    // PO Quantity shown on every summary (single or multi-material).
                    Field(c, "PO Quantity", g.SumPoQuantity.ToString("0.###"));
                });
            });

            // ----- Readings (calm, borderless; skip formula until designed).
            // Also drop readings suppressed on the report (PUC, Packaging
            // Material, ...) so the header hides when nothing is left.
            var visibleReadings = g.Readings
                .Where(r => !string.Equals(r.DisplayMode, "formula", StringComparison.OrdinalIgnoreCase))
                .Where(r => !IsHiddenSummaryField(r.Name))
                .ToList();
            if (visibleReadings.Count > 0)
            {
                col.Item().PaddingTop(3).Text("Readings").Bold().Underline().FontColor(Colors.Grey.Darken2).FontSize(8);
                col.Item().Element(c => RenderGroupReadings(c, visibleReadings));
            }

            // ----- Calm defect lists, laid out two-across (Major beside Minor)
            //       to save vertical space — compaction is a priority.
            RenderDefectSectionsCalm(col, g.DefectSections);
        });
    }

    // Lay the per-category defect lists out two-across (Major | Minor, …) so the
    // report is shorter and uses fewer printed pages. Wraps to a new row every
    // two categories; a lone trailing category takes the left half.
    private static void RenderDefectSectionsCalm(ColumnDescriptor col,
        IReadOnlyList<DefectCategorySection> sections)
    {
        for (int i = 0; i < sections.Count; i += 2)
        {
            var left  = sections[i];
            var right = i + 1 < sections.Count ? sections[i + 1] : null;
            col.Item().Row(row =>
            {
                row.Spacing(18);
                row.RelativeItem().Element(c => RenderDefectListCalm(c, left));
                if (right != null) row.RelativeItem().Element(c => RenderDefectListCalm(c, right));
                else               row.RelativeItem();   // keep the lone column at half width
            });
        }
    }

    // Calm per-category defect list: a category header (name in its colour) with
    // right-aligned unit / "Percentage" column headers, a hairline, one plain row
    // per defect (name · count · coloured percentage), then a bold Total line.
    // Column weights scale to the container so it reads well full-width (summary)
    // or in a narrower column (sample card).
    //
    // Shared by BOTH call paths -- the page-1 group summary and every sample
    // card -- so the unit rides on the section rather than the signature: the
    // summary renderer never receives QualityReportData and so has no unit map
    // of its own to pass down.
    private static void RenderDefectListCalm(QuestPDF.Infrastructure.IContainer container,
        DefectCategorySection sec)
    {
        var pctColor = ReadableOnWhite(sec.ColorHex);
        container.PaddingTop(3).Column(col =>
        {
            // Category header: just the name in its colour. The piece count used
            // to be spelled out inline here; the Total row at the bottom says the
            // same thing in line with the numbers it totals.
            col.Item().Row(r =>
            {
                r.RelativeItem(3f).Text(t =>
                {
                    t.Span(sec.CategoryName).SemiBold().FontColor(pctColor).FontSize(8);
                });
                r.RelativeItem(1f).AlignRight().Text(sec.Unit).FontColor(Colors.Grey.Darken1).FontSize(7);
                r.RelativeItem(1.2f).AlignRight().Text("Percentage").FontColor(Colors.Grey.Darken1).FontSize(7);
            });
            col.Item().Element(Rule);

            if (sec.Rows.Count == 0)
            {
                col.Item().Text("No defects configured for this category in Defect Catalog.")
                    .Italic().FontColor(Colors.Grey.Darken1).FontSize(7);
                return;   // nothing to total
            }

            foreach (var dr in sec.Rows)
            {
                col.Item().Row(r =>
                {
                    r.RelativeItem(3f).Text(dr.Name).FontSize(7);
                    r.RelativeItem(1f).AlignRight().Text(dr.SumValue.ToString("0.##")).FontSize(7);
                    r.RelativeItem(1.2f).AlignRight()
                        .Text(dr.Percentage.ToString("0.##") + "%").SemiBold().FontColor(pctColor).FontSize(7);
                });
            }

            // ----- Total line. Both figures sum the rounded row values, so the
            //       printed column always adds up to the printed total.
            col.Item().Element(Rule);
            col.Item().Row(r =>
            {
                r.RelativeItem(3f).Text("Total").SemiBold().FontColor(Colors.Grey.Darken2).FontSize(7);
                r.RelativeItem(1f).AlignRight()
                    .Text(sec.TotalPieces.ToString("0.##")).SemiBold().FontColor(Colors.Grey.Darken2).FontSize(7);
                r.RelativeItem(1.2f).AlignRight()
                    .Text(sec.TotalPct.ToString("0.##") + "%").Bold().FontColor(pctColor).FontSize(7);
            });
        });
    }

    private static string Dash(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v!;

    private static void RenderGroupReadings(QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<ReadingAggRow> readings)
    {
        // Drop any reading suppressed on the report (e.g. PUC, Packaging
        // Material) so the summary matches the per-sample cards.
        var shown = readings.Where(r => !IsHiddenReportField(r.Name)).ToList();
        container.Table(table =>
        {
            // Three pairs of (Name, Value) columns side-by-side so a typical
            // 6-9 reading set lays out compactly without a tall single column.
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < 3; i++)
                {
                    c.RelativeColumn(2f);    // name + unit
                    c.RelativeColumn(1f);    // value
                }
            });
            foreach (var r in shown)
            {
                var label = string.IsNullOrWhiteSpace(r.Unit) ? r.Name : $"{r.Name} ({r.Unit})";
                table.Cell().PaddingVertical(1).PaddingRight(4).Text(label).FontColor(Colors.Grey.Darken1).FontSize(7);
                table.Cell().PaddingVertical(1).PaddingRight(6)
                    .AlignRight().Text(r.DisplayValue ?? "").Bold().FontSize(7);
            }
            // Pad to fill the last row so columns stay aligned.
            var leftover = (3 - (shown.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Text("");
                table.Cell().Text("");
            }
        });
    }

    // ============================ Per-material card ==========================
    // Rendered ONCE per material above its sample cards (2026-05-30). Carries
    // the material's identity (Product/Brand/Variety/Grade/Group + Sample Size
    // + Size + Pack Type) plus every Material-scoped header value
    // (Grower/Pallet/Pack Code/Date Code/Label/Lot/etc., admin-configured at
    // /Admin/SampleHeaders with scope="Material"). Eliminates the duplication
    // of these fields across every sample card under the same material.
    private static void RenderMaterialCard(QuestPDF.Infrastructure.IContainer container,
        QualityOrderMaterial? m, List<MaterialHeaderValue> materialHeaderValues,
        IReadOnlyDictionary<int, string> scopeById)
    {
        container.PaddingTop(2).Column(col =>
        {
            // ---- Calm material heading (soft-blue + hairline), no box/fill.
            var heading = $"Material {m?.MaterialNo ?? ""}".TrimEnd();
            if (!string.IsNullOrWhiteSpace(m?.MaterialDesc))
                heading += $" — {m!.MaterialDesc}";
            CalmHeading(col, heading, 11);

            // ---- Header cells: Sample Size (V21+ material-level source of
            //      truth), Size, Pack Type, then every Material-scoped header
            //      value in sort order. Defensive: also include any value
            //      whose field is unknown to the scope map (e.g. a brand new
            //      field added between fetch and render) so nothing silently
            //      disappears.
            var headerCells = new List<(string Label, string Value)>
            {
                ("Sample Size", Dash(m?.SampleSize?.ToString())),
                ("Size",        Dash(m?.MaterialSize)),
                ("Pack Type",   Dash(m?.PackType)),
            };

            // Helper: format one MaterialHeaderValue's value by its kind.
            static string FormatValue(MaterialHeaderValue hv) => hv.ValueKind switch
            {
                "Numeric" => hv.NumericValue?.ToString("0.##") ?? "",
                "Date"    => hv.DateValue?.ToString("yyyy-MM-dd") ?? "",
                _         => hv.TextValue ?? ""
            };

            // Every Material-scoped header value renders dynamically, in sort
            // order — no hardcoded field list (the old always-show Grower /
            // Pallet / Pack Code / Date Code / Lot block is gone, which also
            // removes Pack Code per request). A field appears here purely
            // because its admin-configured scope is "Material"; a Sample-scoped
            // field (e.g. Brix for fruits where it's defined as a header) prints
            // on the sample card instead, and a Brix *reading* stays in the
            // readings grid. Placement follows configuration, not code.
            foreach (var hv in materialHeaderValues.OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName))
            {
                if (IsHiddenReportField(hv.FieldName)) continue;   // suppressed on the report
                var label = string.IsNullOrWhiteSpace(hv.DefaultUnit) ? hv.FieldName : $"{hv.FieldName} ({hv.DefaultUnit})";
                headerCells.Add((label, Dash(FormatValue(hv))));
            }
            // Even indices → col 2, odd → col 3 (col 1 is the identity strip).
            var leftHeader  = headerCells.Where((_, i) => i % 2 == 0).ToList();
            var rightHeader = headerCells.Where((_, i) => i % 2 == 1).ToList();

            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    Field(c, "Product",        Dash(m?.MajorCategory));
                    Field(c, "Brand",          Dash(m?.Brand));
                    Field(c, "Variety",        Dash(m?.Variety));
                    Field(c, "Grade",          Dash(m?.MaterialClass));
                    Field(c, "Material Group", Dash(m?.MaterialGroup));
                });
                row.RelativeItem().Column(c =>
                {
                    foreach (var (l, v) in leftHeader) Field(c, l, v);
                });
                row.RelativeItem().Column(c =>
                {
                    foreach (var (l, v) in rightHeader) Field(c, l, v);
                });
            });
        });
    }

    // ============================ Per-sample card ============================
    // Compact per-sample card (2026-05-30 restructure): the material strip is
    // now printed ONCE on the per-material card above this one, so this card
    // shows only what is genuinely sample-specific -- the Sample-scoped header
    // values (if any), the readings, and the per-category defect sections.
    // Defects iterate the FULL active catalog for the sample's material_group
    // so a sample with no defects still shows every defect name with zero.
    private static void RenderSampleDetail(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d, SampleBundle s)
    {
        var m = s.Material;
        // Calm sample block: a soft-blue "Sample #NNN" heading with the
        // variety/size + creator/QO on the right, then a hairline. No box/fill.
        container.PaddingTop(4).Column(col =>
        {
            col.Item().Row(hr =>
            {
                hr.RelativeItem().Text(t =>
                    t.Span($"Sample #{s.Sample.SampleNo:000}").SemiBold().FontColor(Accent).FontSize(12));
                hr.RelativeItem().AlignRight().Text(t =>
                {
                    var vs = m?.Variety;
                    if (!string.IsNullOrWhiteSpace(m?.MaterialSize))
                        vs = string.IsNullOrWhiteSpace(vs) ? $"({m!.MaterialSize})" : $"{vs} ({m!.MaterialSize})";
                    if (!string.IsNullOrWhiteSpace(vs)) t.Span(vs + "    ").SemiBold();
                    t.Span("by ").FontColor(Colors.Grey.Darken1).FontSize(7);
                    t.Span(s.Sample.CreatedBy).FontSize(7);
                    t.Span("    QO ").FontColor(Colors.Grey.Darken1).FontSize(7);
                    t.Span(d.QualityOrder.QualityOrderNo).FontSize(7);
                });
            });
            col.Item().Element(Rule);

            // ---- Sample-scoped header values only. HeaderValues carries
            //      BOTH scopes (V23+ copy-down), so filter using the scope
            //      map. Defensive: an unknown FieldId (field deleted/renamed
            //      between fetch and render) is treated as Sample so the
            //      value is still visible somewhere rather than vanishing.
            var sampleScoped = s.HeaderValues
                .Where(hv => !d.HeaderFieldScopeById.TryGetValue(hv.FieldId, out var sc)
                             || !string.Equals(sc, "Material", StringComparison.OrdinalIgnoreCase))
                .Where(hv => !IsHiddenReportField(hv.FieldName))   // suppressed on the report
                .OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName)
                .ToList();
            if (sampleScoped.Count > 0)
            {
                col.Item().PaddingTop(1).Element(c => RenderSampleHeaderGrid(c, sampleScoped));
            }

            // ---- Readings recorded on this sample (calm 3-col layout)
            if (s.Readings.Any(r => !IsHiddenReportField(r.ReadingName)))
            {
                col.Item().PaddingTop(2).Text("Sample Readings").Bold().Underline().FontColor(Colors.Grey.Darken2).FontSize(8);
                col.Item().Element(c => RenderSampleReadingsGrid(c, s));
            }

            // ---- Defects, Major / Minor, full catalog with zeros for unrecorded.
            //      Catalog lookup is keyed by the sample's material_group;
            //      missing key (defensive: legacy QOs with stale material_group)
            //      falls back to whatever the sample actually recorded.
            var catalog = (m?.MaterialGroup != null
                              && d.DefectsByGroup.TryGetValue(m.MaterialGroup!, out var cat))
                              ? cat
                              : Array.Empty<DefectCatalogEntry>();
            var sampleSections = BuildSampleDefectSections(
                s, catalog, d.Categories, d.UnitFor(m?.MaterialGroup));

            // Same calm defect lists as the summary (category name in its colour,
            // Pieces / Percentage headers, coloured percentages), laid out
            // two-across to save vertical space.
            RenderDefectSectionsCalm(col, sampleSections);
        });
    }

    // Build the per-sample defect sections (one per category, V22+) from the
    // catalog + recorded defects. Iterates the catalog so every defect appears
    // (zero value when not recorded), and uses the recorded defect_value when
    // present. Percentage = value / sample_size * 100 (0 when sample_size == 0).
    // Sections are ordered by the category master's sort_order and coloured by
    // its colour; unknown/inactive categories fall to the end with no colour.
    private static List<DefectCategorySection> BuildSampleDefectSections(
        SampleBundle s, IReadOnlyList<DefectCatalogEntry> catalog, IReadOnlyList<DefectCategory> categories,
        string unit)
    {
        var catMeta = categories.ToDictionary(c => c.CategoryName, StringComparer.OrdinalIgnoreCase);
        var sections = new Dictionary<string, DefectCategorySection>(StringComparer.OrdinalIgnoreCase);
        var recordedById = s.Defects.ToDictionary(d => d.DefectId);
        decimal size = s.Sample.SampleSize ?? 0;

        void Add(string category, DefectAggRow row)
        {
            if (!sections.TryGetValue(category, out var sec))
            {
                catMeta.TryGetValue(category, out var meta);
                sec = new DefectCategorySection
                {
                    CategoryName = category,
                    ColorHex     = meta?.ColorHex,
                    SortOrder    = meta?.SortOrder ?? 999,
                    Unit         = unit
                };
                sections[category] = sec;
            }
            sec.Rows.Add(row);
        }

        foreach (var entry in catalog)
        {
            decimal val = recordedById.TryGetValue(entry.DefectId, out var rec) && rec.DefectValue.HasValue
                ? rec.DefectValue.Value
                : 0m;
            decimal pct = size > 0 ? (val / size) * 100m : 0m;
            Add(entry.DefectCategory, new DefectAggRow
            {
                DefectId   = entry.DefectId,
                Code       = entry.DefectCode,
                Name       = entry.DefectName,
                Category   = entry.DefectCategory,
                SumValue   = val,
                Percentage = pct
            });
        }

        // Defensive: a sample may still have a defect whose catalog row was
        // soft-deleted (is_active=0) after the sample was recorded -- show
        // those too rather than silently drop them, in their own category.
        var catalogIds = catalog.Select(c => c.DefectId).ToHashSet();
        foreach (var rec in s.Defects.Where(d => !catalogIds.Contains(d.DefectId)))
        {
            decimal pct = size > 0 ? ((rec.DefectValue ?? 0) / size) * 100m : 0m;
            Add(rec.DefectCategory ?? "", new DefectAggRow
            {
                DefectId   = rec.DefectId,
                Code       = rec.DefectCode,
                Name       = rec.DefectName + "  (inactive)",
                Category   = rec.DefectCategory ?? "",
                SumValue   = rec.DefectValue ?? 0,
                Percentage = pct
            });
        }

        return sections.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.CategoryName).ToList();
    }

    // Sample-scoped header values laid out in a 3-pair (label, value) grid.
    // Same density/look as the readings grid below so the per-sample card
    // stays compact regardless of how many header fields the admin defines.
    private static void RenderSampleHeaderGrid(QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<SampleHeaderValue> values)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < 3; i++)
                {
                    c.RelativeColumn(2f);  // label
                    c.RelativeColumn(1f);  // value
                }
            });
            foreach (var hv in values)
            {
                string val = hv.ValueKind switch
                {
                    "Numeric" => hv.NumericValue?.ToString("0.##") ?? "",
                    "Date"    => hv.DateValue?.ToString("yyyy-MM-dd") ?? "",
                    _         => hv.TextValue ?? ""
                };
                var label = string.IsNullOrWhiteSpace(hv.DefaultUnit) ? hv.FieldName : $"{hv.FieldName} ({hv.DefaultUnit})";
                table.Cell().PaddingVertical(1).PaddingRight(4).Text(label).FontColor(Colors.Grey.Darken1).FontSize(7);
                table.Cell().PaddingVertical(1).PaddingRight(6)
                    .AlignRight().Text(Dash(val)).Bold().FontSize(7);
            }
            var leftover = (3 - (values.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Text("");
                table.Cell().Text("");
            }
        });
    }

    private static void RenderSampleReadingsGrid(QuestPDF.Infrastructure.IContainer container, SampleBundle s)
    {
        // PUC, Packaging Material and the other suppressed readings never print.
        var shown = s.Readings.Where(r => !IsHiddenReportField(r.ReadingName)).ToList();
        container.Table(table =>
        {
            // 3 pairs of (label, value) so a typical reading set lays out
            // compactly without a tall single column.
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < 3; i++)
                {
                    c.RelativeColumn(2f);  // label
                    c.RelativeColumn(1f);  // value
                }
            });
            foreach (var r in shown)
            {
                var label = string.IsNullOrWhiteSpace(r.UnitCode) ? r.ReadingName : $"{r.ReadingName} ({r.UnitCode})";
                string value = string.Equals(r.ValueKind, "Text", StringComparison.OrdinalIgnoreCase)
                    ? (r.TextValue ?? "")
                    : (r.NumericValue?.ToString("0.##") ?? r.TextValue ?? "");
                table.Cell().PaddingVertical(1).PaddingRight(4).Text(label).FontColor(Colors.Grey.Darken1).FontSize(7);
                table.Cell().PaddingVertical(1).PaddingRight(6)
                    .AlignRight().Text(value).Bold().FontSize(7);
            }
            // Pad to fill the last row.
            var leftover = (3 - (shown.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Text("");
                table.Cell().Text("");
            }
        });
    }

    // ============================== Image page =============================
    private static void RenderImagesPage(QuestPDF.Fluent.PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeaderBand(h, d));
        page.Footer().Element(f => RenderFooterBand(f, d));

        page.Content().Column(col =>
        {
            col.Spacing(6);
            col.Item().Text("Photos attached to this quality order.")
                .FontSize(8).Italic().FontColor(Colors.Grey.Darken2);

            // Arrival-level photos first (relocated 2026-06-13 from the
            // main page so the report's narrative is Summary -> per-
            // material detail -> appendix of all photos).
            if (d.ArrivalImages.Count > 0)
            {
                col.Item().PaddingTop(6)
                    .Text("Arrival Photos").SemiBold().FontSize(11).FontColor(Accent);
                col.Item().Element(c => RenderImageGrid(c, d, d.ArrivalImages));
            }

            // V31 (2026-06-20): photos are per-sample. Group by material in
            // the appendix; under each material, list samples in SampleNo
            // order with each sample's own image grid. Materials and samples
            // with no images are skipped so the page stays compact.
            foreach (var m in d.Materials)
            {
                var matSamples = d.Samples
                    .Where(sb => sb.Material?.QoMaterialId == m.QoMaterialId && sb.Images.Count > 0)
                    .OrderBy(sb => sb.Sample.SampleNo)
                    .ToList();
                if (matSamples.Count == 0) continue;
                col.Item().PaddingTop(6).Text(t =>
                {
                    t.Span("Material ").FontColor(Accent).Bold().FontSize(11);
                    t.Span(m.MaterialNo).Bold().FontSize(11);
                    if (!string.IsNullOrWhiteSpace(m.MaterialDesc))
                    {
                        t.Span(" — ").FontColor(Colors.Grey.Darken1);
                        t.Span(m.MaterialDesc!).FontSize(10);
                    }
                });
                foreach (var sb in matSamples)
                {
                    col.Item().PaddingTop(3).PaddingLeft(8).Text(t =>
                    {
                        t.Span($"Sample #{sb.Sample.SampleNo}").Bold().FontSize(9).FontColor(Accent);
                        t.Span($" — {sb.Images.Count} photo{(sb.Images.Count == 1 ? "" : "s")}")
                            .FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                    col.Item().PaddingLeft(8).Element(c => RenderImageGrid(c, d, sb.Images));
                }
            }
        });
    }

    private static void RenderImageGrid(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d, List<ImageRef> images)
    {
        // PdfWidth/PdfHeight from Site Configuration map directly to PDF
        // points -- no CSS-px conversion. Same rationale as in
        // ArrivalReportPdf.RenderImageGrid: the value the admin types is the
        // cell size in the rendered PDF.
        float cellW = Math.Max(40, d.ThumbnailW);
        float cellH = Math.Max(30, d.ThumbnailH);
        const float pageInner = 539f;
        int columns = Math.Clamp((int)Math.Floor(pageInner / (cellW + 8f)), 1, 8);

        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < columns; i++) c.ConstantColumn(cellW + 8f);
            });
            foreach (var img in images)
            {
                table.Cell().Padding(4).Element(cell =>
                {
                    var box = cell.Width(cellW).Height(cellH)
                        .Border(0.6f).BorderColor(Colors.Grey.Medium)
                        .Background("#f5f5f5");
                    if (img.InlineBytes != null && img.InlineBytes.Length > 0)
                    {
                        try
                        {
                            // UseOriginalImage(): embed our already-resized, high-
                            // quality JPEG as-is instead of letting QuestPDF
                            // re-rasterise it down to 72 DPI (the cause of the
                            // blurry report photos). Same display box — more pixels.
                            if (d.ThumbCover) box.Image(img.InlineBytes).UseOriginalImage().FitUnproportionally();
                            else              box.AlignCenter().AlignMiddle().Image(img.InlineBytes).UseOriginalImage().FitArea();
                        }
                        catch
                        {
                            box.AlignCenter().AlignMiddle().Text("(image error)").FontColor(Colors.Grey.Darken1);
                        }
                    }
                    else
                    {
                        box.AlignCenter().AlignMiddle().Text("(missing)").FontColor(Colors.Grey.Darken1);
                    }
                });
            }
        });
    }

    // ============================== helpers ================================
    private static void Field(ColumnDescriptor col, string label, string? value, string? note = null, float labelWidth = 78)
    {
        col.Item().Row(r =>
        {
            r.ConstantItem(labelWidth).Text(label).FontColor(Colors.Grey.Darken1);
            r.RelativeItem().Text(t =>
            {
                t.Span(value ?? "").Bold();
                if (!string.IsNullOrWhiteSpace(note))
                {
                    t.Span("  ");
                    t.Span(note ?? "").FontColor(Colors.Grey.Darken2);
                }
            });
        });
    }
    private static string YN(bool? b) => b switch { true => "YES", false => "NO", _ => "—" };

    /// <summary>Seal numbers and logger serials are stored newline-delimited
    /// (up to 4 values). Present them on one line separated by " / ".</summary>
    private static string? JoinLines(string? s) => string.IsNullOrWhiteSpace(s)
        ? null
        : string.Join(" / ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => x.Trim()).Where(x => x.Length > 0));

    /// <summary>Whole days between the chosen basis date (discharge or arrival)
    /// and the QO finish (Closed) date. Null when the basis date is missing.</summary>
    private static string? TimeBarDays(DateTime? basis, DateTime finished)
        => basis == null ? null : (finished.Date - basis.Value.Date).Days.ToString();

    // Fields the QO report must NOT show. Matching is by a normalised name so
    // every spelling variant of the same field is caught: the header field
    // "Pallet No", the reading type "Pallet No." and the code "PALLET_NO" all
    // reduce to "PALLETNO". To show one again, remove its normalised form here.
    //
    // 2026-07-27: Grower / Pallet No / Date Code / Lot No / PUC were UN-hidden at
    // the user's request — they configure these as reading types and want them
    // to print in the sample Readings section. (They were hidden on 2026-07-25;
    // that is now reversed for those fields.) Packaging Material stays hidden.
    private static readonly HashSet<string> HiddenReportFields = new(StringComparer.Ordinal)
    {
        "PACKAGINGMATERIAL", "PACKINGMATERIAL",
    };

    // 2026-07-27: fields hidden from the page-1 group SUMMARY readings only (they
    // are identifiers, not measurements, so a group roll-up is meaningless), but
    // still shown on each per-sample card. This is on TOP of HiddenReportFields.
    private static readonly HashSet<string> HiddenSummaryOnlyFields = new(StringComparer.Ordinal)
    {
        "PUC",
        "GROWER",
        "LOTNO", "LOTNUMBER",
        "DATECODE",
    };

    /// <summary>Uppercase, strip everything that isn't A-Z/0-9, so "Pallet No.",
    /// "Pallet No" and "PALLET_NO" all compare equal.</summary>
    private static string NormalizeFieldName(string? name)
        => new string((name ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool IsHiddenReportField(string? name)
        => HiddenReportFields.Contains(NormalizeFieldName(name));

    /// <summary>Hidden from the group summary readings (identifiers like PUC /
    /// Grower / Lot No / Date Code) — but still shown per sample.</summary>
    private static bool IsHiddenSummaryField(string? name)
    {
        var n = NormalizeFieldName(name);
        return HiddenReportFields.Contains(n) || HiddenSummaryOnlyFields.Contains(n);
    }

    private static string? JoinTemps(SharbatlyQMS.Web.Models.ArrivalChecklist? cl)
    {
        if (cl == null) return null;
        var parts = new List<string>();
        if (cl.PulpTempFront.HasValue)  parts.Add(cl.PulpTempFront.Value.ToString());
        if (cl.PulpTempMiddle.HasValue) parts.Add(cl.PulpTempMiddle.Value.ToString());
        if (cl.PulpTempBack.HasValue)   parts.Add(cl.PulpTempBack.Value.ToString());
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }
}
