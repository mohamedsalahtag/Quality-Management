using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// "Soft" layout variant of the Quality Control Report, selectable from Site
/// Configuration → Report → Report layout version. It consumes the SAME
/// <see cref="QualityReportData"/> and reproduces exactly the same data,
/// formatting and hidden-field filtering as the Classic <see cref="QualityReportPdf"/>
/// (shared via <see cref="SummaryReadingFilter"/> and <see cref="Fmt"/>) — ONLY
/// the visual styling differs: soft-blue section bands, ruled/zebra tables, and
/// rose/amber Major/Minor defect-table headers. Layout only; the numbers are
/// identical to Classic for the same Quality Order.
/// </summary>
public static class QualityReportPdfSoft
{
    // ---- Soft, muted palette ----------------------------------------------
    private const string Ink      = "#1c2733";  // body text — dark near-black
    private const string Muted    = "#5f6b7a";  // secondary / labels
    private const string Accent   = "#5b7fa6";  // muted slate-blue accent
    private const string BandBg    = "#eaf1f8"; // soft-blue section band fill
    private const string HeadFill  = "#dfe8f2"; // soft table-header fill
    private const string Stripe    = "#f6f8fb";
    // One step lighter than Stripe so a card tint never fights the zebra rows
    // inside it. This is the whole separator scheme: tint + a left rule +
    // whitespace, no new hues.
    private const string CardBg    = "#fbfcfe"; // very light alternating row tint
    private const string Border    = "#c9d4e0"; // soft hairline borders
    // The two part headings. Sample Details is deliberately a different hue
    // from everything else in the report so the eye lands on the boundary
    // without reading a word -- the same slate blue for both made the two
    // halves of the document look like one continuous run.
    private const string DetailHead = "#4e7a63"; // muted green — Sample Details bar
    private const string MajorHead = "#b76e79"; // soft muted rose — Major header
    private const string MinorHead = "#b08d3e"; // soft muted amber — Minor header
    private const string White     = "#ffffff";

    public static byte[] Build(QualityReportData d)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page => RenderMainPage(page, d));

            bool anySampleImgs   = d.Samples.Any(sb => sb.Images.Any(i => i.InlineBytes is { Length: > 0 }));
            bool anyArrivalImgs  = d.ArrivalImages.Any(i => i.InlineBytes is { Length: > 0 });
            if (anySampleImgs || anyArrivalImgs)
                container.Page(page => RenderImagesPage(page, d));
        });
        return doc.GeneratePdf();
    }

    // ============================== Main content ============================
    private static void RenderMainPage(PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(22);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Ink).FontFamily(Fonts.Calibri));

        page.Header().Element(h => RenderHeaderBand(h, d));
        page.Footer().PaddingTop(4).BorderTop(0.6f).BorderColor(Border).PaddingTop(3)
            .Text(V(d.CompanyFooter)).FontSize(6.5f).FontColor(Muted).AlignCenter();

        page.Content().PaddingTop(6).Column(col =>
        {
            col.Spacing(7);

            // 2. Top reference strip (right-aligned)
            var date = d.InspectionDate?.ToString("dd/MM/yyyy")
                       ?? d.GeneratedAt.ToLocalTime().ToString("dd/MM/yyyy");
            col.Item().AlignRight().Column(rc =>
            {
                rc.Spacing(1);
                rc.Item().Text(t => { t.Span("Date  ").FontColor(Muted); t.Span(date).SemiBold(); });
                rc.Item().Text(t => { t.Span("QC No.  ").FontColor(Muted); t.Span(V(d.QualityOrder.QualityOrderNo)).SemiBold(); });
                if (!string.IsNullOrWhiteSpace(d.CreatedByName))
                    rc.Item().Text(t =>
                    {
                        t.Span("Created by  ").FontColor(Muted);
                        t.Span(V(d.CreatedByName)).SemiBold();
                        if (!string.IsNullOrWhiteSpace(d.CreatedByBranch))
                        {
                            t.Span("     Branch  ").FontColor(Muted);
                            t.Span(V(d.CreatedByBranch)).SemiBold();
                        }
                    });
            });

            // 2b. Rejection banner. First thing under the reference strip so
            //     nobody reads the shipment details without knowing the
            //     container was refused.
            if (d.IsContainerRejection)
                col.Item().Element(c => RenderRejectionBanner(c, d));

            // 3. Shipment Details
            Band(col.Item(), "Shipment Details");
            RenderShipmentDetails(col, d);

            // 4. Materials
            if (d.Materials.Count > 0)
            {
                Band(col.Item(), "Materials");
                RenderMaterialsTable(col.Item(), d);
            }

            // 5. Summary. Guarded: an order finished through the no-samples
            //    bypass has no groups, and an unconditional band printed a
            //    heading with nothing under it.
            if (d.GroupSummaries.Count > 0)
            {
                PartBand(col.Item(), 1, 2, "Summary",
                    "Results rolled up per material group — the overall picture.", Accent);
                foreach (var g in d.GroupSummaries)
                    col.Item().Element(Card).Element(c => RenderGroupSummary(c, g));
            }

            // 6. Sample Details (per material, then its samples).
            //    Each material and ITS samples live in one card, so the boundary
            //    between one material's samples and the next is a visible edge
            //    rather than an extra few points of blank space.
            if (d.Samples.Count > 0)
            {
                // Told apart by COLOUR, not by a page break. A page edge worked
                // but wasted a sheet and left a gap the reader had to scroll
                // past; two differently coloured bars sit right next to each
                // other and still read as two different things.
                PartBand(col.Item(), d.GroupSummaries.Count > 0 ? 2 : 1,
                                     d.GroupSummaries.Count > 0 ? 2 : 1, "Sample Details",
                    "Every sample and every reading, material by material.", DetailHead);
                foreach (var grp in d.Samples.GroupBy(s => s.Sample.QoMaterialId))
                {
                    var first   = grp.First();
                    var unit    = d.UnitFor(first.Material?.MaterialGroup);
                    var samples = grp.ToList();
                    col.Item().Element(Card).Column(mc =>
                    {
                        mc.Spacing(5);
                        // ShowEntire on the header alone: it must not orphan at a
                        // page foot, but the samples below it may legitimately
                        // flow onto the next page.
                        mc.Item().ShowEntire().Element(c => RenderMaterialCard(c, first.Material,
                            first.MaterialHeaderValues, d.HeaderFieldScopeById,
                            samples.Select(x => x.Sample.SampleSize).ToList(), unit));
                        foreach (var s in samples)
                            mc.Item().Element(c => RenderSampleDetail(c, d, s));
                    });
                }
            }
        });
    }

    private static void RenderHeaderBand(IContainer container, QualityReportData d)
    {
        // Identical logo sizing to Classic (Site Configuration → Branding).
        float logoScale  = Math.Clamp(d.LogoScalePercent <= 0 ? 100 : d.LogoScalePercent, 50, 400) / 100f;
        float logoHeight = 45f * logoScale;
        float logoColW   = Math.Max(52f, logoHeight + 8f);

        container.Column(h =>
        {
            h.Item().Row(r =>
            {
                r.ConstantItem(logoColW).AlignMiddle().Element(e =>
                {
                    if (!string.IsNullOrWhiteSpace(d.LogoAbsolutePath) && System.IO.File.Exists(d.LogoAbsolutePath))
                    {
                        try { e.Height(logoHeight).AlignLeft().Image(d.LogoAbsolutePath).FitArea(); return; }
                        catch { /* fall through to placeholder */ }
                    }
                    e.Width(logoHeight).Height(logoHeight).Background(HeadFill).Border(0.6f).BorderColor(Border)
                        .AlignCenter().AlignMiddle().Text("QMS").Bold().FontSize(11).FontColor(Accent);
                });
                r.RelativeItem().PaddingHorizontal(8).AlignMiddle().Column(c =>
                {
                    c.Item().AlignCenter().Text(V(d.CompanyName)).Bold().FontSize(10.5f).FontColor(Ink);
                    c.Item().AlignCenter().Text("Quality Control Report").FontSize(9).FontColor(Accent);
                });
                r.ConstantItem(72).AlignRight().AlignMiddle().Text(t =>
                {
                    t.Span("Page ").FontColor(Muted);
                    t.CurrentPageNumber().SemiBold();
                    t.Span(" / ").FontColor(Muted);
                    t.TotalPages().SemiBold();
                });
            });
            h.Item().PaddingTop(3).Height(1.4f).Background(Accent);
        });
    }

    private static void RenderShipmentDetails(ColumnDescriptor col, QualityReportData d)
    {
        var s  = d.Shipment;
        var cl = d.Checklist;

        var basisDate  = d.TimeBarBasis == TimeBarBases.Arrival ? s?.ArrivalDate : s?.DischargeDate;
        var basisLabel = d.TimeBarBasis == TimeBarBases.Arrival ? "Arrival" : "Discharge";

        var col1 = new List<(string, string)>
        {
            ("Shipper",           V(d.Arrival.VendorName)),
            ("Report Location",   V(d.Arrival.Plant)),
            ("Bill of Lading No.",V(d.Arrival.BolNo)),
            ("Container",         V(d.Arrival.ContainerNo)),
            ("Purch.Doc.",        V(d.Arrival.Ebeln)),
            ("Procurement Type",  V(d.ProcurementType)),
            ("Country Of Origin", V(Countries.Display(s?.LoadingCountry))),
            ("Loading Port",      V(s?.LoadingPort)),
            ("Port Of Arrival",   V(s?.ArrivalPlace)),
            ("Vessel Name",       V(s?.VesselName)),
        };
        // Middle column is the shipment TIMELINE, in the order the events
        // actually happen: loading -> discharge -> pullout -> receive ->
        // unloading -> inspection. Reading it top to bottom shows the movement,
        // so a gap or an out-of-order date stands out. Arrival Date is not
        // printed; only the Time Bar basis above still reads one. Derived
        // figures (transit days, time bar) close the column. The QO page, the
        // Claims page and the QC summary panel use the same sequence.
        var col2 = new List<(string, string)>
        {
            ("Loading Date",      Dt(s?.SailingDate)),
            ("Discharge Date",    Dt(s?.DischargeDate)),
            ("Pullout Date",      Dt(s?.PullOutDate)),
            // Receive Date is an internal goods-receipt date; the supplier's
            // copy omits it. Removed from the list rather than blanked so the
            // column closes up instead of printing an orphaned label.
            ("Receive Date",      Dt(s?.ReceiveDate)),
            ("Unloading Date",    Dt(s?.UnloadingDate)),
            ("Inspection Date",   Dt(d.InspectionDate)),
            ("Transit Days",      s?.TransitDays?.ToString() ?? "—"),
            ($"Time Bar ({basisLabel})", (TimeBarDays(basisDate, d.QualityOrder.ClosedAt ?? d.GeneratedAt) ?? "—") + " days"),
        };
        if (d.SupplierCopy)
            col2.RemoveAll(x => x.Item1 == "Receive Date");

        var col3 = new List<(string, string)>
        {
            ("Logger Serial",                      V(cl?.DataLoggerSerial)),
            ("Seal No",                            V(JoinLines(cl?.SealNo))),
            ("Temperature",                        cl?.LoggerTemperature?.ToString() ?? "—"),
            ("Pulp Temperature",                   V(JoinTemps(cl))),
            ("Joint Survey",                       YN(s?.JointSurvey)),
            ("Seal Intact?",                       YN(cl?.SealIntact)),
            ("External damage to container",       YN(cl?.ExternalDamageExists)),
            ("Visual cargo condition acceptable",  YN(cl?.VisualCargoAcceptable)),
            ("Logger active & data available",     YN(cl?.LoggerActiveDataAvailable)),
        };
        ThreeColumns(col.Item(), col1, col2, col3);

        // Arrival custom fields for this report's material groups (numeric ones
        // print their share of the group's total quantity).
        if (d.CustomFields.Count > 0)
        {
            var list = d.CustomFields.OrderBy(f => f.SortOrder).ThenBy(f => f.FieldName).ToList();
            col.Item().Text("Additional Fields").Bold().FontSize(8).FontColor(Accent);
            PairGrid(col.Item(), list.Select(f => (f.FieldName, CustomVal(d, f))).ToList(), 3);
        }
    }

    /// <summary>A numeric custom field prints its share of the material group's
    /// TOTAL QUANTITY (Sigma arrival_item.quantity across the group's materials) --
    /// e.g. Soft Green/Destroyed 26.00 of Qty 1540 => "26.00 (1.69%)". The
    /// denominator is the quantity, never the sample size.</summary>
    private static string CustomVal(QualityReportData d, ArrivalCustomField cf)
    {
        var val = cf.DisplayValue;
        if (string.Equals(cf.ValueKind, "Numeric", StringComparison.OrdinalIgnoreCase) && cf.NumericValue.HasValue)
        {
            var denom = d.GroupSummaries
                .Where(g => string.Equals(g.MaterialGroup, cf.MaterialGroup, StringComparison.OrdinalIgnoreCase))
                .Sum(g => g.SumPoQuantity);
            if (denom > 0)
                val += $" ({Fmt.Dec2(cf.NumericValue.Value / denom * 100m)}%)";
        }
        return val;
    }

    private static void RenderMaterialsTable(IContainer c, QualityReportData d) =>
        c.Table(t =>
        {
            t.ColumnsDefinition(cd =>
            {
                cd.RelativeColumn(2.4f); cd.RelativeColumn(3.4f); cd.RelativeColumn(1.4f);
                cd.RelativeColumn(1.6f); cd.ConstantColumn(58); cd.ConstantColumn(38);
            });
            t.Header(h =>
            {
                foreach (var (label, right) in new[] {
                    ("Material", false), ("Material description", false), ("Origin", false),
                    ("Material Group", false), ("Quantity", true), ("Unit", false) })
                {
                    var cell = Cell(h.Cell(), head: true);
                    (right ? cell.AlignRight() : cell).Text(label).SemiBold().FontColor(Ink);
                }
            });
            int i = 0;
            foreach (var m in d.Materials)
            {
                var bg = (i++ % 2 == 1) ? Stripe : White;
                Cell(t.Cell(), fill: bg).Text(V(m.MaterialNo)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).Text(V(m.MaterialDesc)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).Text(V(m.Origin)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).Text(V(m.MaterialGroup)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).AlignRight().Text(Fmt.Dec2(m.Quantity ?? 0m)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).Text(V(m.Uom)).FontColor(Ink);
            }
        });

    private static void RenderGroupSummary(IContainer c, MaterialGroupSummary g) =>
        c.Column(gc =>
        {
            gc.Spacing(4);

            gc.Item().Border(0.5f).BorderColor(Border).Background(BandBg)
                .PaddingVertical(3).PaddingHorizontal(5).Row(r =>
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(g.Brand))   parts.Add(g.Brand!);
                if (!string.IsNullOrWhiteSpace(g.Variety)) parts.Add(g.Variety!);
                if (!string.IsNullOrWhiteSpace(g.Grade))   parts.Add(g.Grade!);
                if (parts.Count == 0) parts.Add("(brand / variety / grade not set)");
                r.RelativeItem().Text(string.Join("  ·  ", parts)).Bold().FontColor(Ink);
                r.RelativeItem().AlignRight()
                    .Text($"Material Group: {V(g.MaterialGroup)} — {V(g.MaterialGroupDesc)}").FontColor(Muted);
            });

            ThreeColumns(gc.Item(),
                new() { ("Product", V(g.MajorCategory)), ("Brand", V(g.Brand)), ("Variety", V(g.Variety)), ("Grade", V(g.Grade)) },
                new() { ("Material Group", V(g.MaterialGroup)), ("Count of Materials", g.MaterialCount.ToString()), ("Samples", $"{g.SampleCount} Cartons") },
                new() { ("Sample Size", $"{g.SumSampleSize} {V(g.SampleUnit)}"), ("PO Quantity", Fmt.Dec2(g.SumPoQuantity)),
                        ("Tara Weight", string.IsNullOrEmpty(g.TaraWeightText) ? "—" : $"{g.TaraWeightText} kg") });

            var readings = SummaryReadingFilter.VisibleSummaryReadings(g.Readings);
            if (readings.Count > 0)
            {
                gc.Item().Text("Readings").Bold().FontSize(7.5f).FontColor(Accent);
                PairGrid(gc.Item(), readings.Select(r =>
                    (string.IsNullOrWhiteSpace(r.Unit) ? V(r.Name) : $"{V(r.Name)} ({r.Unit})",
                     string.IsNullOrWhiteSpace(r.DisplayValue) ? "—" : r.DisplayValue)).ToList(), 3);
            }

            if (g.DefectSections.Count > 0)
                DefectsTwoAcross(gc.Item(), g.DefectSections);
        });

    // ============================ Per-material card ==========================
    private static void RenderMaterialCard(IContainer c, QualityOrderMaterial? m,
        List<MaterialHeaderValue> materialHeaderValues, IReadOnlyDictionary<int, string> scopeById,
        IReadOnlyList<short?> sampleSizes, string unit) =>
        c.Column(mc =>
        {
            mc.Spacing(4);

            var heading = $"Material {m?.MaterialNo ?? ""}".TrimEnd();
            if (!string.IsNullOrWhiteSpace(m?.MaterialDesc)) heading += $" — {m!.MaterialDesc}";
            mc.Item().Border(0.5f).BorderColor(Border).Background(BandBg)
                .PaddingVertical(3).PaddingHorizontal(5).Text(heading).Bold().FontColor(Ink);

            var headerCells = new List<(string, string)>();
            // Sample Size at material level -- see MaterialSampleSizeRule. Fixed
            // packs always show it; a banana material whose cartons disagree
            // does not, because one number would misrepresent the inspection.
            if (MaterialSampleSizeRule.ShowAtMaterialLevel(m?.MaterialGroup, sampleSizes))
                headerCells.Add(("Sample Size", SampleSizeText(m, sampleSizes, unit)));
            headerCells.Add(("Size", Dash(m?.MaterialSize)));
            // Tara is recorded once per material (Material details card), so it
            // belongs on the material card rather than on each sample.
            headerCells.Add(("Tara Weight", m?.TaraWeight is > 0 ? $"{Fmt.Dec2(m.TaraWeight)} kg" : "—"));
            foreach (var hv in materialHeaderValues.OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName))
            {
                if (SummaryReadingFilter.IsHiddenReportField(hv.FieldName)) continue;
                var label = string.IsNullOrWhiteSpace(hv.DefaultUnit) ? hv.FieldName : $"{hv.FieldName} ({hv.DefaultUnit})";
                headerCells.Add((label, Dash(FormatHeaderValue(hv.ValueKind, hv.FieldCode, hv.FieldName, hv.NumericValue, hv.DateValue, hv.TextValue))));
            }
            var leftHeader  = headerCells.Where((_, i) => i % 2 == 0).ToList();
            var rightHeader = headerCells.Where((_, i) => i % 2 == 1).ToList();

            ThreeColumns(mc.Item(),
                new() { ("Product", Dash(m?.MajorCategory)), ("Brand", Dash(m?.Brand)), ("Variety", Dash(m?.Variety)), ("Grade", Dash(m?.MaterialClass)), ("Material Group", Dash(m?.MaterialGroup)) },
                leftHeader, rightHeader);
        });

    // ============================ Per-sample card ============================
    private static void RenderSampleDetail(IContainer c, QualityReportData d, SampleBundle s) =>
        c.Column(sccol =>
        {
            sccol.Spacing(3);
            var m = s.Material;

            sccol.Item().Border(0.5f).BorderColor(Border).Background(Stripe)
                .PaddingVertical(2).PaddingHorizontal(5).Row(r =>
            {
                r.RelativeItem().Text(t =>
                {
                    t.Span($"Sample #{s.Sample.SampleNo:000}").Bold().FontColor(Ink);
                    if (s.Sample.SampleSize is > 0)
                        t.Span($"    sample size = {s.Sample.SampleSize} {d.UnitFor(m?.MaterialGroup)}").FontColor(Muted);
                });
                r.RelativeItem().AlignRight().Text(t =>
                {
                    var vs = m?.Variety;
                    if (!string.IsNullOrWhiteSpace(m?.MaterialSize))
                        vs = string.IsNullOrWhiteSpace(vs) ? $"({m!.MaterialSize})" : $"{vs} ({m!.MaterialSize})";
                    if (!string.IsNullOrWhiteSpace(vs)) t.Span(V(vs)).FontColor(Ink);
                    t.Span($"   ·   by {V(s.Sample.CreatedBy)}").FontColor(Muted);
                    t.Span($"   ·   QO {V(d.QualityOrder.QualityOrderNo)}").FontColor(Muted);
                });
            });

            // Sample-scoped header values only (same filter as Classic).
            var sampleScoped = s.HeaderValues
                .Where(hv => !d.HeaderFieldScopeById.TryGetValue(hv.FieldId, out var sc) || !string.Equals(sc, "Material", StringComparison.OrdinalIgnoreCase))
                .Where(hv => !SummaryReadingFilter.IsHiddenReportField(hv.FieldName))
                .OrderBy(h => h.SortOrder).ThenBy(h => h.FieldName)
                .ToList();
            if (sampleScoped.Count > 0)
                PairGrid(sccol.Item(), sampleScoped.Select(hv =>
                    (string.IsNullOrWhiteSpace(hv.DefaultUnit) ? hv.FieldName : $"{hv.FieldName} ({hv.DefaultUnit})",
                     Dash(FormatHeaderValue(hv.ValueKind, hv.FieldCode, hv.FieldName, hv.NumericValue, hv.DateValue, hv.TextValue)))).ToList(), 3);

            var readings = s.Readings.Where(r => !SummaryReadingFilter.IsHiddenReportField(r.ReadingName)).ToList();
            if (readings.Count > 0)
            {
                sccol.Item().Text("Sample Readings").Bold().FontSize(7.5f).FontColor(Accent);
                PairGrid(sccol.Item(), readings.Select(FormatReadingPair).ToList(), 4);
            }

            var catalog = (m?.MaterialGroup != null && d.DefectsByGroup.TryGetValue(m.MaterialGroup!, out var cat))
                ? cat : Array.Empty<DefectCatalogEntry>();
            var sections = BuildSampleDefectSections(s, catalog, d.Categories, d.UnitFor(m?.MaterialGroup));
            if (sections.Count > 0)
                DefectsTwoAcross(sccol.Item(), sections);
        });

    // Mirrors QualityReportPdf.BuildSampleDefectSections exactly.
    private static List<DefectCategorySection> BuildSampleDefectSections(
        SampleBundle s, IReadOnlyList<DefectCatalogEntry> catalog,
        IReadOnlyList<DefectCategory> categories, string unit)
    {
        var catMeta      = categories.ToDictionary(c => c.CategoryName, StringComparer.OrdinalIgnoreCase);
        var sections     = new Dictionary<string, DefectCategorySection>(StringComparer.OrdinalIgnoreCase);
        var recordedById = s.Defects.ToDictionary(dd => dd.DefectId);
        decimal size     = s.Sample.SampleSize ?? 0;

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
            decimal val = recordedById.TryGetValue(entry.DefectId, out var rec) && rec.DefectValue.HasValue ? rec.DefectValue.Value : 0m;
            decimal pct = size > 0 ? (val / size) * 100m : 0m;
            Add(entry.DefectCategory, new DefectAggRow
            {
                DefectId = entry.DefectId, Code = entry.DefectCode, Name = entry.DefectName,
                Category = entry.DefectCategory, SumValue = val, Percentage = pct
            });
        }
        var catalogIds = catalog.Select(c => c.DefectId).ToHashSet();
        foreach (var rec in s.Defects.Where(dd => !catalogIds.Contains(dd.DefectId)))
        {
            decimal pct = size > 0 ? ((rec.DefectValue ?? 0) / size) * 100m : 0m;
            Add(rec.DefectCategory ?? "", new DefectAggRow
            {
                DefectId = rec.DefectId, Code = rec.DefectCode, Name = rec.DefectName + "  (inactive)",
                Category = rec.DefectCategory ?? "", SumValue = rec.DefectValue ?? 0, Percentage = pct
            });
        }
        return sections.Values.OrderBy(x => x.SortOrder).ThenBy(x => x.CategoryName).ToList();
    }

    // ============================== Image page =============================
    private static void RenderImagesPage(PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(22);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Ink).FontFamily(Fonts.Calibri));
        page.Header().Element(h => RenderHeaderBand(h, d));
        page.Footer().PaddingTop(4).BorderTop(0.6f).BorderColor(Border).PaddingTop(3)
            .Text(V(d.CompanyFooter)).FontSize(6.5f).FontColor(Muted).AlignCenter();

        page.Content().PaddingTop(6).Column(col =>
        {
            col.Spacing(6);
            Band(col.Item(), "Photo Appendix");

            var arrivals = d.ArrivalImages.Where(i => i.InlineBytes is { Length: > 0 }).ToList();
            col.Item().Text(t =>
            {
                t.Span("Arrival Photos").Bold().FontColor(Ink);
                t.Span($"   ({arrivals.Count})").FontColor(Muted);
            });
            if (arrivals.Count > 0) PhotoTiles(col.Item(), d, arrivals);
            else col.Item().Text("—").FontColor(Muted);

            foreach (var m in d.Materials)
            {
                var matSamples = d.Samples
                    .Where(sb => sb.Material?.QoMaterialId == m.QoMaterialId && sb.Images.Any(i => i.InlineBytes is { Length: > 0 }))
                    .OrderBy(sb => sb.Sample.SampleNo)
                    .ToList();
                if (matSamples.Count == 0) continue;

                var heading = $"Material {m.MaterialNo}".TrimEnd();
                if (!string.IsNullOrWhiteSpace(m.MaterialDesc)) heading += $" — {m.MaterialDesc}";
                col.Item().PaddingTop(4).Border(0.5f).BorderColor(Border).Background(BandBg)
                    .PaddingVertical(2).PaddingHorizontal(5).Text(heading).Bold().FontColor(Ink);

                foreach (var sb in matSamples)
                {
                    var imgs = sb.Images.Where(i => i.InlineBytes is { Length: > 0 }).ToList();
                    col.Item().PaddingTop(2).Text($"Sample #{sb.Sample.SampleNo:000} — {imgs.Count} photo{(imgs.Count == 1 ? "" : "s")}")
                        .SemiBold().FontSize(7.5f).FontColor(Accent);
                    PhotoTiles(col.Item(), d, imgs);
                }
            }
        });
    }

    private static void PhotoTiles(IContainer c, QualityReportData d, List<ImageRef> images)
    {
        if (images.Count == 0) { c.Text("—").FontColor(Muted); return; }
        const int cols = 4;
        bool cover = d.ThumbCover;
        c.PaddingTop(2).Table(t =>
        {
            t.ColumnsDefinition(cd => { for (int i = 0; i < cols; i++) cd.RelativeColumn(); });
            int n = 1;
            foreach (var img in images)
            {
                int idx = n++;
                t.Cell().Padding(2).Border(0.5f).BorderColor(Border).Column(cc =>
                {
                    cc.Item().Height(96).Background(White).Element(b =>
                    {
                        try
                        {
                            if (img.InlineBytes is { Length: > 0 })
                            {
                                if (cover) b.Image(img.InlineBytes).UseOriginalImage().FitUnproportionally();
                                else       b.AlignCenter().AlignMiddle().Image(img.InlineBytes).UseOriginalImage().FitArea();
                            }
                            else b.AlignCenter().AlignMiddle().Text("(missing)").FontSize(6).FontColor(Muted);
                        }
                        catch { b.AlignCenter().AlignMiddle().Text("(image error)").FontSize(6).FontColor(Muted); }
                    });
                    cc.Item().Background(BandBg).PaddingVertical(1).PaddingHorizontal(3)
                        .Text($"Photo {idx:00}").FontSize(6).FontColor(Muted);
                });
            }
            int rem = images.Count % cols;
            if (rem != 0) for (int i = rem; i < cols; i++) t.Cell().Text("");
        });
    }

    /// <summary>
    /// The banner on a container-rejection report. Reuses the existing muted
    /// rose (MajorHead) on a pale tint rather than introducing a hard red: the
    /// report's whole palette is deliberately quiet, and a siren colour here
    /// would make every other severity signal on the page read as less urgent.
    /// The refusal comment is printed in full and never truncated -- it is the
    /// substance of the claim.
    /// </summary>
    private static void RenderRejectionBanner(IContainer c, QualityReportData d) =>
        c.Background("#f7e6e8").Border(0.8f).BorderColor(MajorHead)
         .BorderLeft(3).PaddingVertical(6).PaddingHorizontal(8)
         .Column(col =>
         {
             col.Spacing(3);
             col.Item().AlignCenter()
                .Text(V(d.RejectionHeader).ToUpperInvariant())
                .ExtraBold().FontSize(13).FontColor(MajorHead);

             var by   = V(d.QualityOrder.ClosedBy);
             var when = d.QualityOrder.ClosedAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "";
             col.Item().AlignCenter()
                .Text($"Recorded by {by} on {when}").FontSize(7.5f).FontColor(Muted);

             if (!string.IsNullOrWhiteSpace(d.RejectionComment))
                 col.Item().PaddingTop(2).Text(d.RejectionComment).FontSize(8).FontColor(Ink);

             col.Item().Text("No inspection was carried out: the container was refused on arrival.")
                .Italic().FontSize(7.5f).FontColor(Muted);
         });

    // ===== Reusable visual pieces ==========================================
    // A section heading. Closed top and bottom by an Accent rule, with real
    // space above it, so the eye can see where one section of the report ends
    // and the next begins -- previously the only boundary was the tinted label
    // itself and a flat 7pt gap, which read as continuous text when scanning.
    private static void Band(IContainer c, string title) =>
        c.PaddingTop(9).Column(col =>
        {
            col.Item().Height(1.4f).Background(Accent);
            col.Item().Background(BandBg).Border(0.5f).BorderColor(Border)
                .PaddingVertical(3).PaddingHorizontal(6)
                .Text(title.ToUpperInvariant()).Bold().FontSize(9.5f).FontColor(Accent);
            col.Item().Height(1.4f).Background(Accent);
        });

    /// <summary>
    /// A PART heading, one weight above <see cref="Band"/>.
    ///
    /// The report is really two documents bound together: a summary a manager
    /// reads, and a per-sample record an inspector checks. Giving both the same
    /// light section band as "Shipment Details" made all four look like peers,
    /// and the eye slid straight past the boundary. A solid reversed bar with a
    /// line saying what the part contains stops it.
    /// </summary>
    private static void PartBand(IContainer c, int part, int ofParts, string title, string caption,
                                 string fill) =>
        c.PaddingTop(10).Column(col =>
        {
            col.Item().Background(fill).PaddingVertical(6).PaddingHorizontal(8).Row(r =>
            {
                r.RelativeItem().Text(title.ToUpperInvariant())
                    .Bold().FontSize(12).FontColor("#ffffff");
                // The numbering is what makes the boundary unmissable at a
                // glance: a reader who sees "PART 2 OF 2" knows without reading
                // a word that the previous section has ended.
                r.ConstantItem(70).AlignRight().Text($"PART {part} OF {ofParts}")
                    .SemiBold().FontSize(7.5f).FontColor("#dbe6f2");
            });
            col.Item().Background(BandBg).BorderBottom(1.4f).BorderColor(fill)
               .PaddingVertical(2.5f).PaddingHorizontal(8)
               .Text(caption).Italic().FontSize(7).FontColor(Muted);
        });

    /// <summary>
    /// One block of the report as a card: light tint, an Accent rule down the
    /// left edge, and breathing room around it. Used for each material-group
    /// summary and each material's run of samples, so "where does this material
    /// end and the next begin" is answerable at a glance.
    /// </summary>
    private static IContainer Card(IContainer c) =>
        c.PaddingTop(4).Background(CardBg).BorderLeft(2).BorderColor(Accent)
         .PaddingVertical(5).PaddingLeft(6).PaddingRight(4);

    private static void ThreeColumns(IContainer c,
        List<(string, string)> a, List<(string, string)> b, List<(string, string)> d) =>
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.ConstantColumn(8); cd.RelativeColumn(); cd.ConstantColumn(8); cd.RelativeColumn(); });
            PairGrid(t.Cell(), a, 1);
            t.Cell().Text("");
            PairGrid(t.Cell(), b, 1);
            t.Cell().Text("");
            PairGrid(t.Cell(), d, 1);
        });

    private static void PairGrid(IContainer c, List<(string label, string val)> pairs, int cols)
    {
        if (pairs.Count == 0) { c.Text(""); return; }
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { for (int i = 0; i < cols; i++) { cd.RelativeColumn(1.1f); cd.RelativeColumn(1.5f); } });
            foreach (var (label, val) in pairs)
            {
                Cell(t.Cell(), head: true).Text(label).FontSize(7).FontColor(Muted);
                Cell(t.Cell()).Text(string.IsNullOrWhiteSpace(val) ? "—" : val).SemiBold().FontColor(Ink);
            }
            int rem = pairs.Count % cols;
            if (rem != 0)
                for (int i = rem; i < cols; i++) { Cell(t.Cell(), head: true).Text(""); Cell(t.Cell()).Text(""); }
        });
    }

    private static void DefectsTwoAcross(IContainer c, IReadOnlyList<DefectCategorySection> sections) =>
        c.Column(col =>
        {
            for (int i = 0; i < sections.Count; i += 2)
            {
                var left  = sections[i];
                var right = i + 1 < sections.Count ? sections[i + 1] : null;
                col.Item().PaddingTop(2).Row(r =>
                {
                    DefectTable(r.RelativeItem(), left);
                    r.ConstantItem(8);
                    if (right != null) DefectTable(r.RelativeItem(), right);
                    else               r.RelativeItem();
                });
            }
        });

    private static void DefectTable(IContainer c, DefectCategorySection s) =>
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { cd.RelativeColumn(3); cd.ConstantColumn(34); cd.ConstantColumn(42); });

            // The colour an administrator set on Admin -> Defect Categories,
            // which that screen already promises is "used for the section banner
            // in the form and PDF". It was loaded into the section and then
            // ignored here: the fill was chosen by string-matching the category
            // name for "Major", so every other category -- Critical, Minor,
            // Other -- printed the same amber whatever was configured.
            var isMajor  = s.CategoryName.Trim().StartsWith("Major", StringComparison.OrdinalIgnoreCase);
            var headFill = SummaryReadingFilter.IsColour(s.ColorHex)
                            ? s.ColorHex!
                            : (isMajor ? MajorHead : MinorHead);
            // Never hardcode white on a configured colour: the seeded Minor is
            // #ffc107, on which white is unreadable.
            var headText = SummaryReadingFilter.OnFill(headFill, dark: Ink, light: White);

            var head = t.Cell().ColumnSpan(3).Background(headFill).Border(0.5f).BorderColor(Border)
                .PaddingVertical(2).PaddingHorizontal(4)
                .Text($"{V(s.CategoryName)} Defects").FontColor(headText);
            if (isMajor) head.ExtraBold().FontSize(8.5f); else head.Bold().FontSize(8);

            Cell(t.Cell(), head: true).Text("Defect").FontSize(7).FontColor(Muted);
            Cell(t.Cell(), head: true).AlignRight().Text(s.Unit).FontSize(7).FontColor(Muted);
            Cell(t.Cell(), head: true).AlignRight().Text("%").FontSize(7).FontColor(Muted);

            if (s.Rows.Count == 0)
                Cell(t.Cell().ColumnSpan(3)).Text("No defects configured for this category.").Italic().FontSize(7).FontColor(Muted);

            int i = 0;
            foreach (var row in s.Rows)
            {
                var bg = (i++ % 2 == 1) ? Stripe : White;
                Cell(t.Cell(), fill: bg).Text(V(row.Name)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).AlignRight().Text(Fmt.Dec2(row.SumValue)).FontColor(Ink);
                Cell(t.Cell(), fill: bg).AlignRight().Text(Fmt.Dec2(row.Percentage) + "%").FontColor(Ink);
            }

            Cell(t.Cell(), head: true).Text("Total").Bold().FontColor(Ink);
            Cell(t.Cell(), head: true).AlignRight().Text(Fmt.Dec2(s.TotalPieces)).Bold().FontColor(Ink);
            Cell(t.Cell(), head: true).AlignRight().Text(Fmt.Dec2(s.TotalPct) + "%").Bold().FontColor(Ink);
        });

    private static IContainer Cell(IContainer c, bool head = false, string? fill = null)
    {
        if (head) c = c.Background(HeadFill);
        else if (fill != null) c = c.Background(fill);
        return c.Border(0.5f).BorderColor(Border).PaddingVertical(2).PaddingHorizontal(4);
    }

    // ===== value formatting (shared rules with Classic / Fmt) ==============
    private static (string, string) FormatReadingPair(SampleReading r)
    {
        var label = string.IsNullOrWhiteSpace(r.UnitCode) ? r.ReadingName : $"{r.ReadingName} ({r.UnitCode})";
        bool isDateCode = Fmt.IsDateCode(r.ReadingTypeCode) || Fmt.IsDateCode(r.ReadingName);
        string value = string.Equals(r.ValueKind, "Text", StringComparison.OrdinalIgnoreCase)
            ? (r.TextValue ?? "")
            : (r.NumericValue.HasValue ? (isDateCode ? Fmt.Int0(r.NumericValue) : Fmt.Dec2(r.NumericValue)) : (r.TextValue ?? ""));
        return (V(label), string.IsNullOrWhiteSpace(value) ? "—" : value);
    }

    private static string FormatHeaderValue(string kind, string code, string name, decimal? num, DateTime? date, string? text) => kind switch
    {
        "Numeric" => (Fmt.IsDateCode(code) || Fmt.IsDateCode(name)) ? Fmt.Int0(num) : Fmt.Dec2(num),
        "Date"    => date?.ToString("yyyy-MM-dd") ?? "",
        _         => text ?? ""
    };

    private static string SampleSizeText(QualityOrderMaterial? m, IReadOnlyList<short?> sampleSizes, string unit)
    {
        var fromMaterial = m?.SampleSize ?? m?.EffectiveSampleSize;
        if (fromMaterial is > 0) return $"{fromMaterial} {unit}";
        var distinct = sampleSizes.Where(x => x is > 0).Select(x => x!.Value).Distinct().OrderBy(x => x).ToList();
        return distinct.Count == 0 ? "—" : $"{string.Join(" / ", distinct)} {unit}";
    }

    private static string? TimeBarDays(DateTime? basis, DateTime finished)
        => basis == null ? null : (finished.Date - basis.Value.Date).Days.ToString();

    private static string? JoinLines(string? s) => string.IsNullOrWhiteSpace(s)
        ? null
        : string.Join(" / ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));

    private static string? JoinTemps(ArrivalChecklist? cl)
    {
        if (cl == null) return null;
        var parts = new List<string>();
        if (cl.PulpTempFront.HasValue)  parts.Add(cl.PulpTempFront.Value.ToString());
        if (cl.PulpTempMiddle.HasValue) parts.Add(cl.PulpTempMiddle.Value.ToString());
        if (cl.PulpTempBack.HasValue)   parts.Add(cl.PulpTempBack.Value.ToString());
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    private static string Dash(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v!;
    private static string YN(bool? b) => b switch { true => "YES", false => "NO", _ => "—" };
    private static string Dt(DateTime? d) => d?.ToString("dd/MM/yyyy") ?? "—";

    private static string V(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "—";
        return s.Replace("\r\n", " / ").Replace("\n", " / ").Replace("\r", " / ").Trim();
    }
}

/// <summary>
/// Renders the QC report. Soft is the permanent layout — the Classic renderer
/// (<see cref="QualityReportPdf"/>) is kept for reference only and is no longer
/// reachable from the app. Called by ReportsController.
/// </summary>
public static class QualityReportRenderer
{
    public static byte[] Build(QualityReportData d)
        => QualityReportPdfSoft.Build(d);
}
