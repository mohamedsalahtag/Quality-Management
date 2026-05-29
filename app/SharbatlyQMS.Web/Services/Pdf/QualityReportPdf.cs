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
    private const string Accent      = "#0d6efd";   // brand blue
    private const string AccentLight = "#e7f1ff";
    private const string GridLine    = "#9aa1a8";

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

    // Row background = the banner colour blended ~88% toward white (a light
    // tint), so rows stay readable under any banner colour.
    private static string TintHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !IsHex(hex)) return "#f5f5f5";
        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);
        int Mix(int c) => (int)(c + (255 - c) * 0.88);
        return $"#{Mix(r):X2}{Mix(g):X2}{Mix(b):X2}";
    }

    public static byte[] Build(QualityReportData d)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page => RenderMainPage(page, d));

            if (d.MaterialImages.Any(kv => kv.Value.Count > 0))
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
            col.Spacing(4);

            // Date strip
            col.Item().AlignRight().Text(t =>
            {
                t.Span("Date  ").FontColor(Colors.Grey.Darken1);
                t.Span(d.Shipment?.InspectionDate?.ToString("dd/MM/yyyy")
                       ?? d.GeneratedAt.ToLocalTime().ToString("dd/MM/yyyy")).Bold();
            });

            // Shipment details
            col.Item().Element(c => RenderShipmentDetails(c, d));

            // Material table
            col.Item().Element(c => RenderMaterialTable(c, d));

            // Grouped summary blocks
            foreach (var g in d.GroupSummaries)
                col.Item().Element(c => RenderGroupSummary(c, g));
            if (d.GroupSummaries.Count == 0)
                col.Item().AlignCenter()
                    .Text("(no samples recorded yet -- group summary will appear once samples exist)")
                    .Italic().FontColor(Colors.Grey.Darken1);

            // Per-sample detail cards (was page 2). Section header is a
            // light band so the reader knows the report has moved from
            // group summary to raw sample data, but there is NO forced
            // page break -- QuestPDF flows naturally.
            if (d.Samples.Count > 0)
            {
                col.Item().PaddingTop(2).Background(AccentLight).Padding(3)
                    .Text("Sample Details").Bold().FontSize(9).FontColor(Accent);
                foreach (var s in d.Samples)
                    col.Item().Element(c => RenderSampleDetail(c, d, s));
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
                    e.Height(45).AlignLeft().Image(d.LogoAbsolutePath).FitArea();
                }
                else
                {
                    e.Width(45).Height(45)
                        .Background(Accent)
                        .AlignCenter().AlignMiddle()
                        .Text("QMS").FontColor(Colors.White).FontSize(10).Bold();
                }
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
        container.Border(0.6f).BorderColor(GridLine).Padding(6).Column(col =>
        {
            col.Item().Background(AccentLight).Padding(3).Text("Shipment Details").Bold().FontSize(9);
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Column(c => {
                    Field(c, "Shipper",          d.Arrival.VendorName);
                    Field(c, "Report Location",  s?.ArrivalPlace);
                    Field(c, "Bill of Lading No.", d.Arrival.BolNo);
                    Field(c, "Container",        d.Arrival.ContainerNo);
                    Field(c, "Purch.Doc.",       d.Arrival.Ebeln);
                    Field(c, "Country Of Origin",s?.LoadingCountry);
                    Field(c, "Loading Port",     s?.LoadingPort);
                    Field(c, "Port Of Arrival",  s?.ArrivalPlace);
                    Field(c, "Inspection Point", s?.InspectionPoint);
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
                    Field(c, "Time Bar",         s?.TimeBar?.ToString());
                    Field(c, "Date",             d.GeneratedAt.ToLocalTime().ToString("MMM dd, yyyy"));
                    Field(c, "Logger Serial",    cl?.DataLoggerSerial);
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Seal No",                        cl?.SealNo);
                    Field(c, "Temperature",                    cl?.SetTemperature?.ToString());
                    Field(c, "Pulp Temperature",               JoinTemps(cl));
                    Field(c, "Joint Survey",                   YN(s?.JointSurvey));
                    Field(c, "Seal Intact?",                   YN(cl?.SealIntact));
                    Field(c, "External damage to container",   YN(cl?.ExternalDamageExists));
                    Field(c, "Visual cargo condition acceptable", YN(cl?.VisualCargoAcceptable));
                    Field(c, "Logger active & data available", YN(cl?.LoggerActiveDataAvailable));
                    Field(c, "TIME BAR EXCEED",                YN(s?.TimeBarExceeded));
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
                    nc.Item().Text("Inspector notes").Bold().FontSize(8).FontColor(Colors.Grey.Darken2);
                    nc.Item().PaddingTop(1).Background("#fafbfc")
                        .Border(0.4f).BorderColor(GridLine).Padding(4)
                        .Text(cl!.Notes!).FontSize(8);
                });
            }
        });
    }

    private static void RenderMaterialTable(QuestPDF.Infrastructure.IContainer container, QualityReportData d)
    {
        container.Table(table =>
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
            table.Header(header =>
            {
                foreach (var label in new[] { "Material", "Material description", "Origin", "Material Group", "Quantity", "Unit" })
                {
                    header.Cell().Background(AccentLight).Border(0.4f).BorderColor(GridLine).Padding(3)
                        .Text(label).Bold();
                }
            });

            foreach (var m in d.Materials)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text(m.MaterialNo);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text(m.MaterialDesc ?? "");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text(m.Origin ?? "");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text(m.MaterialGroup ?? "");
                // Quantity + Unit joined from qms_arrival_item (2026-05-25
                // fix -- previously rendered as empty strings).
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3)
                    .AlignRight().Text(m.Quantity?.ToString("0.###") ?? "");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text(m.Uom ?? "");
            }
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
        container.Border(0.6f).BorderColor(GridLine).Padding(4).Column(col =>
        {
            // ----- Header bar: grouping key spelled out + material-group badge
            col.Item().Background(AccentLight).Padding(3).Row(hr =>
            {
                hr.RelativeItem().Text(t =>
                {
                    t.Span("Summary  ").Bold().FontColor(Accent);
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(g.Brand))   parts.Add(g.Brand!);
                    if (!string.IsNullOrWhiteSpace(g.Variety)) parts.Add(g.Variety!);
                    if (!string.IsNullOrWhiteSpace(g.Grade))   parts.Add(g.Grade!);
                    if (parts.Count == 0) parts.Add("(brand / variety / grade not set)");
                    t.Span(string.Join("  ·  ", parts)).Bold();
                });
                hr.ConstantItem(180).AlignRight().Text(t =>
                {
                    t.Span("Material Group: ").FontColor(Colors.Grey.Darken1).FontSize(7);
                    t.Span(g.MaterialGroup).Bold().FontSize(7);
                    if (!string.IsNullOrWhiteSpace(g.MaterialGroupDesc))
                    {
                        t.Span(" — ").FontColor(Colors.Grey.Darken1).FontSize(7);
                        t.Span(g.MaterialGroupDesc!).FontColor(Colors.Grey.Darken1).FontSize(7);
                    }
                });
            });

            // ----- Three-column labeled grouping fields. Column 1 is the
            //       grouping identity (Product/Brand/Variety/Grade/Group);
            //       column 2 is the counts that drive the % denominator;
            //       column 3 is the derived weights. Empty values render
            //       as em-dash so the auditor knows the field IS being
            //       looked at -- it just isn't populated for this group.
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Column(c => {
                    Field(c, "Product",        Dash(g.MajorCategory));
                    Field(c, "Brand",          Dash(g.Brand));
                    Field(c, "Variety",        Dash(g.Variety));
                    Field(c, "Grade",          Dash(g.Grade));
                    Field(c, "Material Group", Dash(g.MaterialGroup));
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Materials",   g.MaterialCount.ToString());
                    Field(c, "Samples",     g.SampleCount.ToString());
                    Field(c, "Sample Size", g.SumSampleSize.ToString());
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Gross Weight", g.SumGross.ToString("0.###"));
                    Field(c, "TARA Weight",  g.SumTara.ToString("0.###"));
                    Field(c, "Net Weight",   g.Net.ToString("0.###"));
                });
            });

            // ----- Readings table (skip formula until designed)
            var visibleReadings = g.Readings
                .Where(r => !string.Equals(r.DisplayMode, "formula", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (visibleReadings.Count > 0)
            {
                col.Item().PaddingTop(3).Text("Readings").Bold().FontSize(8);
                col.Item().Element(c => RenderGroupReadings(c, visibleReadings));
            }

            // ----- One full-width banner + grid per defect category (V22+),
            //       in the category's configured order/colour.
            for (int si = 0; si < g.DefectSections.Count; si++)
            {
                var sec = g.DefectSections[si];
                var banner = BannerBg(sec.ColorHex, si);
                var rowBg  = TintHex(banner);
                col.Item().PaddingTop(6).Background(banner).Padding(4).Text(t =>
                {
                    t.Span(sec.CategoryName.ToUpperInvariant() + " DEFECTS").Bold().FontSize(9);
                    t.Span("   —   Total: ").FontColor(Colors.Grey.Darken3);
                    t.Span(sec.TotalPct.ToString("0.##") + "%").Bold();
                    t.Span($"   ({sec.Rows.Count} defect" + (sec.Rows.Count == 1 ? "" : "s") + ")")
                        .FontColor(Colors.Grey.Darken2).FontSize(7);
                });
                col.Item().Element(c => RenderGroupDefectGrid(c, sec.Rows, rowBg: rowBg));
            }
        });
    }

    private static string Dash(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v!;

    private static void RenderGroupReadings(QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<ReadingAggRow> readings)
    {
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
            int idx = 0;
            foreach (var r in readings)
            {
                var label = string.IsNullOrWhiteSpace(r.Unit) ? r.Name : $"{r.Name} ({r.Unit})";
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text(label).FontSize(7);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(r.DisplayValue ?? "").FontSize(7);
                idx++;
            }
            // Pad to fill the last row so the table doesn't end mid-stride.
            var leftover = (3 - (readings.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
            }
        });
    }

    private static void RenderGroupDefectGrid(QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<DefectAggRow> defects, string rowBg)
    {
        if (defects.Count == 0)
        {
            container.Background(rowBg).Padding(4)
                .Text("No defects are configured for this category in Defect Catalog.")
                .Italic().FontColor(Colors.Grey.Darken1).FontSize(7);
            return;
        }
        container.Table(table =>
        {
            // 3 column pairs of (defect name, Σ value, %) -- matches the
            // density of the old per-sample grid. The row background is
            // supplied by the caller so the grid colour matches the banner
            // that just labeled this section.
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < 3; i++)
                {
                    c.RelativeColumn(2.4f);   // name
                    c.RelativeColumn(0.7f);   // sum value
                    c.RelativeColumn(0.7f);   // pct
                }
            });
            foreach (var d in defects)
            {
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2)
                    .Text(d.Name).FontSize(7);
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(d.SumValue.ToString("0.##")).FontSize(7);
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(d.Percentage.ToString("0.##") + "%").FontSize(7);
            }
            var leftover = (3 - (defects.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                // Pad with same bg so the row reads as one visual band,
                // not "section ends mid-row".
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Background(rowBg).Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
            }
        });
    }

    // ============================ Per-sample card ============================
    // Compact per-sample card. Header identifies the sample (QO / number /
    // material / creator). The grouping fields (Product / Brand / Variety /
    // Grade / Material Group) repeat so each card is self-contained for
    // photocopying / sharing a single page. Defects use the same Major /
    // Minor banner+grid layout as the page-1 grouped summary, iterating the
    // FULL active catalog for the sample's material_group -- zeros render
    // for any defect the operator didn't record, so a sample with no
    // defects still shows every name.
    private static void RenderSampleDetail(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d, SampleBundle s)
    {
        var m = s.Material;
        container.Border(0.6f).BorderColor(GridLine).Padding(4).Column(col =>
        {
            // ---- Header bar with sample identity
            col.Item().Background(AccentLight).Padding(3).Row(hr =>
            {
                hr.RelativeItem().Text(t =>
                {
                    t.Span("Sample #").Bold().FontColor(Accent);
                    t.Span(s.Sample.SampleNo.ToString()).Bold().FontColor(Accent);
                    t.Span("   ").FontColor(Colors.Grey.Darken1);
                    t.Span(m?.MaterialNo ?? "");
                    if (!string.IsNullOrWhiteSpace(m?.MaterialDesc))
                    {
                        t.Span(" — ").FontColor(Colors.Grey.Darken1);
                        t.Span(m!.MaterialDesc!);
                    }
                });
                hr.ConstantItem(160).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(st => st.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("QO ");
                    t.Span(d.QualityOrder.QualityOrderNo).Bold();
                    t.Span("  ·  by ");
                    t.Span(s.Sample.CreatedBy);
                });
            });

            // ---- Material info (col 1) + dynamic sample header fields
            // (cols 2+3). Material columns are NOT configurable (they come
            // from MARA). Sample Size is hardcoded at the top of col 2 --
            // every defect percentage divides by it. The remaining inputs
            // come from qms_sample_header_field (V20+), so adding a new
            // header field in /Admin/SampleHeaders makes it appear here
            // automatically with no code change.
            //
            // Header values are interleaved 2-up across cols 2 and 3 to
            // keep the per-sample card compact regardless of how many
            // header fields the admin defines.
            var headerCells = new List<(string Label, string Value)>
            {
                ("Sample Size", Dash(s.Sample.SampleSize?.ToString())),
                ("Size",        Dash(m?.MaterialSize)),
                ("Pack Type",   Dash(m?.PackType)),
            };
            // Sample header values now include the Material-scoped fields too
            // (copied onto each sample, V23+), so a single loop prints both the
            // sample's own and the inherited material identification fields.
            foreach (var hv in s.HeaderValues.OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName))
            {
                string val = hv.ValueKind switch
                {
                    "Numeric" => hv.NumericValue?.ToString("0.##") ?? "",
                    "Date"    => hv.DateValue?.ToString("yyyy-MM-dd") ?? "",
                    _         => hv.TextValue ?? ""
                };
                var label = string.IsNullOrWhiteSpace(hv.DefaultUnit) ? hv.FieldName : $"{hv.FieldName} ({hv.DefaultUnit})";
                headerCells.Add((label, Dash(val)));
            }
            // Split header cells across two columns: even indices left
            // (col 2), odd indices right (col 3). Keeps the labeled rows
            // balanced without forcing a fixed list of fields.
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

            // ---- Readings recorded on this sample (compact 3-col grid)
            if (s.Readings.Count > 0)
            {
                col.Item().PaddingTop(2).Text("Sample Readings").Bold().FontSize(8);
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
            var sampleSections = BuildSampleDefectSections(s, catalog, d.Categories);

            // One banner + grid per category (V22+), category order/colour.
            for (int si = 0; si < sampleSections.Count; si++)
            {
                var sec = sampleSections[si];
                var banner = BannerBg(sec.ColorHex, si);
                var rowBg  = TintHex(banner);
                col.Item().PaddingTop(4).Background(banner).Padding(3).Text(t =>
                {
                    t.Span(sec.CategoryName.ToUpperInvariant() + " DEFECTS").Bold().FontSize(8);
                    t.Span("   —   Total: ").FontColor(Colors.Grey.Darken3).FontSize(7);
                    t.Span(sec.TotalPct.ToString("0.##") + "%").Bold().FontSize(8);
                    t.Span($"   ({sec.Rows.Count})").FontColor(Colors.Grey.Darken2).FontSize(7);
                });
                col.Item().Element(c => RenderGroupDefectGrid(c, sec.Rows, rowBg: rowBg));
            }
        });
    }

    // Build the per-sample defect sections (one per category, V22+) from the
    // catalog + recorded defects. Iterates the catalog so every defect appears
    // (zero value when not recorded), and uses the recorded defect_value when
    // present. Percentage = value / sample_size * 100 (0 when sample_size == 0).
    // Sections are ordered by the category master's sort_order and coloured by
    // its colour; unknown/inactive categories fall to the end with no colour.
    private static List<DefectCategorySection> BuildSampleDefectSections(
        SampleBundle s, IReadOnlyList<DefectCatalogEntry> catalog, IReadOnlyList<DefectCategory> categories)
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
                    SortOrder    = meta?.SortOrder ?? 999
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

    private static void RenderSampleReadingsGrid(QuestPDF.Infrastructure.IContainer container, SampleBundle s)
    {
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
            foreach (var r in s.Readings)
            {
                var label = string.IsNullOrWhiteSpace(r.UnitCode) ? r.ReadingName : $"{r.ReadingName} ({r.UnitCode})";
                string value = string.Equals(r.ValueKind, "Text", StringComparison.OrdinalIgnoreCase)
                    ? (r.TextValue ?? "")
                    : (r.NumericValue?.ToString("0.##") ?? r.TextValue ?? "");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text(label).FontSize(7);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(value).FontSize(7);
            }
            // Pad to fill the last row.
            var leftover = (3 - (s.Readings.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
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
            col.Item().Text("Photos attached to this quality order, grouped by material.")
                .FontSize(8).Italic().FontColor(Colors.Grey.Darken2);

            foreach (var m in d.Materials)
            {
                if (!d.MaterialImages.TryGetValue(m.QoMaterialId, out var imgs) || imgs.Count == 0)
                    continue;
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
                col.Item().Element(c => RenderImageGrid(c, d, imgs));
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
                            if (d.ThumbCover) box.Image(img.InlineBytes).FitUnproportionally();
                            else              box.AlignCenter().AlignMiddle().Image(img.InlineBytes).FitArea();
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
    private static void Field(ColumnDescriptor col, string label, string? value, string? note = null)
    {
        col.Item().Row(r =>
        {
            r.ConstantItem(78).Text(label).FontColor(Colors.Grey.Darken1);
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
