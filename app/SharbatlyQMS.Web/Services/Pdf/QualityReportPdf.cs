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

            // Arrival photos used to render here. They now live at the
            // start of the appendix page (see RenderImagesPage) so the
            // main page stays focused on data and all photos sit together
            // at the back of the report.

            // Per-material grouped detail blocks. Each material's identity
            // (Product/Brand/Variety/Grade/Group + Material-scoped header
            // values like Grower/Pallet/Lot/etc.) renders ONCE on a per-
            // material card; its samples render beneath it showing only
            // Sample-scoped header values + readings + defects. Eliminates
            // the per-sample repetition of the material strip.
            //
            // d.Samples is already ordered by (QoMaterialId, SampleNo)
            // (QualityOrderService.ListSamplesAsync), so GroupBy preserves
            // the table's material order naturally -- no extra OrderBy.
            if (d.Samples.Count > 0)
            {
                // "Sample Details" starts on a fresh page (separates the
                // page-1 grouped summary from the per-material detail), and
                // EACH material's card also starts on a fresh page so
                // material details and their samples don't run into the
                // next material's strip.
                bool firstMaterial = true;
                foreach (var grp in d.Samples.GroupBy(s => s.Sample.QoMaterialId))
                {
                    col.Item().PageBreak();
                    if (firstMaterial)
                    {
                        col.Item().PaddingBottom(3).Background(AccentLight).Padding(3)
                            .Text("Sample Details").Bold().FontSize(9).FontColor(Accent);
                        firstMaterial = false;
                    }

                    var first = grp.First();
                    // Material card: ShowEntire so its header never orphans
                    // at a page bottom with no samples following.
                    col.Item().ShowEntire().Element(c =>
                        RenderMaterialCard(c, first.Material, first.MaterialHeaderValues, d.HeaderFieldScopeById));

                    // ShowEntire keeps each sample card whole: if it doesn't
                    // fit on the current page, QuestPDF moves the entire card
                    // to the next page instead of splitting it mid-section.
                    foreach (var s in grp)
                        col.Item().ShowEntire().Element(c => RenderSampleDetail(c, d, s));
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
                    // Col-3 labels are long ("External damage to container",
                    // "Logger active & data available", etc.). Widen the
                    // label slot from the default 78 so each label fits on
                    // one line instead of wrapping.
                    const float w = 130;
                    Field(c, "Seal No",                        cl?.SealNo,                          labelWidth: w);
                    Field(c, "Temperature",                    cl?.SetTemperature?.ToString(),      labelWidth: w);
                    Field(c, "Pulp Temperature",               JoinTemps(cl),                       labelWidth: w);
                    Field(c, "Joint Survey",                   YN(s?.JointSurvey),                  labelWidth: w);
                    Field(c, "Seal Intact?",                   YN(cl?.SealIntact),                  labelWidth: w);
                    Field(c, "External damage to container",   YN(cl?.ExternalDamageExists),        labelWidth: w);
                    Field(c, "Visual cargo condition acceptable", YN(cl?.VisualCargoAcceptable),    labelWidth: w);
                    Field(c, "Logger active & data available", YN(cl?.LoggerActiveDataAvailable),   labelWidth: w);
                    Field(c, "TIME BAR EXCEED",                YN(s?.TimeBarExceeded),              labelWidth: w);
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
                // Col 1: grouping identity (4 lines). Col 2: Material Group +
                // counts (4 lines). Col 3: weights (3 lines). Rebalanced from
                // a 5/3/3 split so the row height drops by one line.
                row.RelativeItem().Column(c => {
                    Field(c, "Product",        Dash(g.MajorCategory));
                    Field(c, "Brand",          Dash(g.Brand));
                    Field(c, "Variety",        Dash(g.Variety));
                    Field(c, "Grade",          Dash(g.Grade));
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Material Group", Dash(g.MaterialGroup));
                    Field(c, "Materials",      g.MaterialCount.ToString());
                    Field(c, "Samples",        g.SampleCount.ToString());
                    Field(c, "Sample Size",    g.SumSampleSize.ToString());
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
                // Every span in the banner reverses to white so the text
                // stays legible no matter which colour the admin configured
                // for the category (dark reds, deep greens, etc. used to
                // swallow the dark-grey "Total:" / "(N defects)" labels).
                col.Item().PaddingTop(6).Background(banner).Padding(4).Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontColor(Colors.White));
                    t.Span(sec.CategoryName.ToUpperInvariant() + " DEFECTS").Bold().FontSize(9);
                    t.Span("   —   Total: ");
                    t.Span(sec.TotalPct.ToString("0.##") + "%").Bold();
                    t.Span($"   ({sec.Rows.Count} defect" + (sec.Rows.Count == 1 ? "" : "s") + ")")
                        .FontSize(7);
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

    // rowBg=null renders the grid with no cell background -- used by the
    // per-sample defect sections (2026-05-30) for a cleaner look. The page-1
    // grouped summary still passes the section's tint so its grid reads as
    // one coloured band beneath the loud banner.
    private static void RenderGroupDefectGrid(QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<DefectAggRow> defects, string? rowBg)
    {
        if (defects.Count == 0)
        {
            var empty = rowBg != null ? container.Background(rowBg) : container;
            empty.Padding(4)
                .Text("No defects are configured for this category in Defect Catalog.")
                .Italic().FontColor(Colors.Grey.Darken1).FontSize(7);
            return;
        }
        QuestPDF.Infrastructure.IContainer Cell(QuestPDF.Infrastructure.IContainer c) =>
            (rowBg != null ? c.Background(rowBg) : c).Border(0.4f).BorderColor(GridLine).Padding(2);

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
                Cell(table.Cell()).Text(d.Name).FontSize(7);
                Cell(table.Cell()).AlignRight().Text(d.SumValue.ToString("0.##")).FontSize(7);
                Cell(table.Cell()).AlignRight().Text(d.Percentage.ToString("0.##") + "%").FontSize(7);
            }
            var leftover = (3 - (defects.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                Cell(table.Cell()).Text("");
                Cell(table.Cell()).Text("");
                Cell(table.Cell()).Text("");
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
        container.Border(0.8f).BorderColor(Accent).Padding(4).Column(col =>
        {
            // ---- Material header bar (slightly stronger than the per-sample
            //      card so it visually anchors the group below it).
            col.Item().Background(AccentLight).Padding(3).Text(t =>
            {
                t.Span("Material  ").Bold().FontColor(Accent);
                t.Span(m?.MaterialNo ?? "").Bold().FontColor(Accent);
                if (!string.IsNullOrWhiteSpace(m?.MaterialDesc))
                {
                    t.Span(" — ").FontColor(Colors.Grey.Darken1);
                    t.Span(m!.MaterialDesc!);
                }
            });

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

            // Five always-show identity fields. Even when no value has been
            // entered for one (or the field isn't in the admin catalog) we
            // still print the row with "—" so the reader sees that the slot
            // exists. Keyed by qms_sample_header_field.field_code so an admin
            // can rename the label without breaking this; case-insensitive.
            // Renders BEFORE the other Material-scoped values, and the
            // catalog-driven loop below skips any value already shown here so
            // the field isn't printed twice.
            var alwaysShow = new (string Code, string Label)[]
            {
                ("GROWER",    "Grower"),
                ("PALLET_NO", "Pallet No"),
                ("PACK_CODE", "Pack Code"),
                ("DATE_CODE", "Date Code"),
                ("LOT_NO",    "Lot No"),
            };
            var byCode = materialHeaderValues
                .GroupBy(h => h.FieldCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var shownFieldIds = new HashSet<int>();
            foreach (var (code, label) in alwaysShow)
            {
                if (byCode.TryGetValue(code, out var hv))
                {
                    headerCells.Add((label, Dash(FormatValue(hv))));
                    shownFieldIds.Add(hv.FieldId);
                }
                else
                {
                    headerCells.Add((label, "—"));
                }
            }

            foreach (var hv in materialHeaderValues.OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName))
            {
                if (shownFieldIds.Contains(hv.FieldId)) continue;
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
        // Bold border in the brand accent so each sample is clearly framed
        // and reads as a distinct unit, even without a tinted defect grid.
        container.PaddingTop(3).Border(1.5f).BorderColor(Accent).Padding(4).Column(col =>
        {
            // ---- Compact header: sample number + creator on the left, QO no
            //      on the right so the card is still self-contained when
            //      photocopied. Material no/desc is intentionally not
            //      repeated -- the parent material card names it.
            col.Item().Background(AccentLight).Padding(3).Row(hr =>
            {
                hr.RelativeItem().Text(t =>
                {
                    t.Span("Sample #").Bold().FontColor(Accent);
                    t.Span(s.Sample.SampleNo.ToString()).Bold().FontColor(Accent);
                    t.Span("   ").FontColor(Colors.Grey.Darken1);
                    t.Span("by ").FontColor(Colors.Grey.Darken1).FontSize(7);
                    t.Span(s.Sample.CreatedBy).FontSize(7);
                });
                hr.ConstantItem(120).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(st => st.FontSize(7).FontColor(Colors.Grey.Darken1));
                    t.Span("QO ");
                    t.Span(d.QualityOrder.QualityOrderNo).Bold();
                });
            });

            // ---- Sample-scoped header values only. HeaderValues carries
            //      BOTH scopes (V23+ copy-down), so filter using the scope
            //      map. Defensive: an unknown FieldId (field deleted/renamed
            //      between fetch and render) is treated as Sample so the
            //      value is still visible somewhere rather than vanishing.
            var sampleScoped = s.HeaderValues
                .Where(hv => !d.HeaderFieldScopeById.TryGetValue(hv.FieldId, out var sc)
                             || !string.Equals(sc, "Material", StringComparison.OrdinalIgnoreCase))
                .OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName)
                .ToList();
            if (sampleScoped.Count > 0)
            {
                col.Item().PaddingTop(2).Element(c => RenderSampleHeaderGrid(c, sampleScoped));
            }

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

            // Per-sample defect sections use a SOFTER look than the page-1
            // group summary so the two are visually distinct even though the
            // structure is identical: the bold category colour becomes the
            // section title text on a tinted background, with a left accent
            // stripe in the same colour. Summary stays loud, samples stay
            // quiet — same category identity, different visual weight.
            for (int si = 0; si < sampleSections.Count; si++)
            {
                var sec = sampleSections[si];
                var accent = BannerBg(sec.ColorHex, si);
                var soft   = TintHex(accent);
                // Header strip is filled with the category's configured
                // colour (from /Admin/Defect Categories); white text reads
                // on any colour from the configured palette. Grid cells
                // below stay un-tinted so the per-sample defect list is
                // still clean and readable.
                col.Item().PaddingTop(4).Background(accent).Padding(3).Text(t =>
                {
                    t.Span(sec.CategoryName.ToUpperInvariant() + " DEFECTS").Bold().FontSize(8).FontColor(Colors.White);
                    t.Span("   —   Total: ").FontColor(Colors.White).FontSize(7);
                    t.Span(sec.TotalPct.ToString("0.##") + "%").Bold().FontSize(8).FontColor(Colors.White);
                    t.Span($"   ({sec.Rows.Count})").FontColor(Colors.White).FontSize(7);
                });
                col.Item().Element(c => RenderGroupDefectGrid(c, sec.Rows, rowBg: null));
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
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text(label).FontSize(7);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(Dash(val)).FontSize(7);
            }
            var leftover = (3 - (values.Count % 3)) % 3;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
            }
        });
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
            col.Item().Text("Photos attached to this quality order.")
                .FontSize(8).Italic().FontColor(Colors.Grey.Darken2);

            // Arrival-level photos first (relocated 2026-06-13 from the
            // main page so the report's narrative is Summary -> per-
            // material detail -> appendix of all photos).
            if (d.ArrivalImages.Count > 0)
            {
                col.Item().PaddingTop(6).Background(AccentLight).Padding(3)
                    .Text("Arrival Photos").Bold().FontSize(11).FontColor(Accent);
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
