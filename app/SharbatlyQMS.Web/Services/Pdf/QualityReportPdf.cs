using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// QuestPDF document for the QMS Quality Control Report -- modeled on the
/// "Quality Report Version 2.pdf" template the user supplied:
///   Page 1: header, shipment details box, material table, compact per-sample
///           summary blocks (with Major/Minor defect totals + per-defect grid)
///   Page 2: per-sample detail card (header + readings + 16-defect grid)
///   Image gallery section at the end (one page or more depending on volume).
/// </summary>
public static class QualityReportPdf
{
    private const string Accent      = "#0d6efd";   // brand blue
    private const string AccentLight = "#e7f1ff";
    private const string GridLine    = "#9aa1a8";

    public static byte[] Build(QualityReportData d)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page => RenderPage1(page, d));
            container.Page(page => RenderPage2(page, d));

            if (d.MaterialImages.Any(kv => kv.Value.Count > 0))
                container.Page(page => RenderImagesPage(page, d));
        });
        return doc.GeneratePdf();
    }

    // ============================== Page 1 =================================
    private static void RenderPage1(QuestPDF.Fluent.PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeaderBand(h, d, "Page 1 of 2"));
        page.Footer().Element(f => RenderFooterBand(f, d));

        page.Content().PaddingVertical(6).Column(col =>
        {
            col.Spacing(6);

            // --- Date strip ---
            col.Item().AlignRight().Text(t =>
            {
                t.Span("Date  ").FontColor(Colors.Grey.Darken1);
                t.Span(d.Shipment?.InspectionDate?.ToString("dd/MM/yyyy")
                       ?? d.GeneratedAt.ToLocalTime().ToString("dd/MM/yyyy")).Bold();
            });

            // --- Shipment Details ---
            col.Item().Element(c => RenderShipmentDetails(c, d));

            // --- Material table ---
            col.Item().PaddingTop(4).Element(c => RenderMaterialTable(c, d));

            // --- Per-sample summary blocks ---
            foreach (var s in d.Samples)
            {
                col.Item().PaddingTop(4).Element(c => RenderSampleSummary(c, s));
            }
        });
    }

    private static void RenderHeaderBand(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d, string pageLabel)
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
            row.ConstantItem(60).AlignMiddle().AlignRight().Text(pageLabel).FontSize(8).FontColor(Colors.Grey.Darken1);
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
                    Field(c, "Sailing Date",     s?.SailingDate?.ToString("MMM dd, yyyy"));
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
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).AlignRight().Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(3).Text("");
            }
        });
    }

    private static void RenderSampleSummary(QuestPDF.Infrastructure.IContainer container, SampleBundle s)
    {
        var m = s.Material;
        container.Border(0.6f).BorderColor(GridLine).Padding(4).Column(col =>
        {
            col.Item().Background(AccentLight).Padding(2).Text("Summary").Bold();
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Column(c => {
                    Field(c, "Product",     m?.MajorCategory);
                    Field(c, "Brand",       m?.Brand);
                    Field(c, "Variety",     m?.Variety);
                    Field(c, "Grade",       m?.MaterialClass);
                    Field(c, "Pack Type",   m?.PackType);
                    Field(c, "Qty Sample",  s.Sample.SampleSize?.ToString());
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Sample Size",   s.Sample.SampleSize?.ToString());
                    Field(c, "Fruit Sticker", s.ReadingTxt("STICKER") ?? s.ReadingNum("STICKER")?.ToString());
                    Field(c, "Brix(BX)",      s.ReadingNum("BRIX")?.ToString());
                    Field(c, "TARA Weight",   s.ReadingNum("TARA")?.ToString());
                    Field(c, "Net Weight",    s.ReadingNum("NET_WEIGHT")?.ToString());
                    Field(c, "Waxing",        s.ReadingTxt("WAXING"));
                });
                row.RelativeItem().Column(c => {
                    Field(c, "Packaging Mat",  s.ReadingTxt("PACKAGING_MATERIAL"));
                    Field(c, "Colour",         s.ReadingTxt("COLOUR"));
                    Field(c, "Firmness",       s.ReadingNum("FIRMNESS")?.ToString());
                    Field(c, "Gross Weight",   s.ReadingNum("GROSS_WEIGHT")?.ToString());
                    Field(c, "Downgrade",      s.ReadingNum("DOWNGRADE")?.ToString());
                });
            });

            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Background("#fce4ec").Padding(3)
                    .Text(t => { t.Span("Major Defects: ").Bold(); t.Span(s.MajorDefectTotal.ToString("0.##")); });
                row.ConstantItem(8);
                row.RelativeItem().Background("#fff4e5").Padding(3)
                    .Text(t => { t.Span("Minor Defects : ").Bold(); t.Span(s.MinorDefectTotal.ToString("0.##")); });
            });

            // Per-defect grid: 4 columns
            col.Item().PaddingTop(2).Element(c => RenderDefectGrid(c, s));
        });
    }

    private static void RenderDefectGrid(QuestPDF.Infrastructure.IContainer container, SampleBundle s)
    {
        var ordered = s.Defects
            .OrderByDescending(d => s.SectionMap.GetValueOrDefault(d.DefectCode) == "Major")
            .ToList();
        if (ordered.Count == 0)
        {
            container.Text("(no defects recorded)").Italic().FontColor(Colors.Grey.Darken1);
            return;
        }
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < 4; i++)
                {
                    c.RelativeColumn(2);    // label
                    c.RelativeColumn(0.7f); // value
                    c.RelativeColumn(0.7f); // pct
                }
            });
            int col = 0;
            foreach (var d in ordered)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .Text(d.DefectName).FontSize(7);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(d.DefectValue?.ToString("0.##") ?? "").FontSize(7);
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2)
                    .AlignRight().Text(d.DefectPercentage?.ToString("0.##") ?? "").FontSize(7);
                col++;
                if (col % 4 == 0) col = 0;
            }
            // Pad incomplete row
            var leftover = (4 - (ordered.Count % 4)) % 4;
            for (int k = 0; k < leftover; k++)
            {
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
                table.Cell().Border(0.4f).BorderColor(GridLine).Padding(2).Text("");
            }
        });
    }

    // ============================== Page 2 =================================
    private static void RenderPage2(QuestPDF.Fluent.PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeaderBand(h, d, "Page 2 of 2"));
        page.Footer().Element(f => RenderFooterBand(f, d));

        page.Content().PaddingVertical(6).Column(col =>
        {
            col.Spacing(8);
            foreach (var s in d.Samples)
                col.Item().Element(c => RenderSampleDetail(c, d, s));
            if (d.Samples.Count == 0)
                col.Item().AlignCenter().Text("(no samples recorded)").Italic();
        });
    }

    private static void RenderSampleDetail(QuestPDF.Infrastructure.IContainer container,
        QualityReportData d, SampleBundle s)
    {
        var m = s.Material;
        container.Border(0.7f).BorderColor(GridLine).Padding(6).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(t => { t.Span("Quality Order No   ").Bold(); t.Span(d.QualityOrder.QualityOrderNo); });
                    c.Item().Text(t => { t.Span("Sample No: ").Bold(); t.Span(s.Sample.SampleNo.ToString()); });
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(t => { t.Span("Material:   ").Bold(); t.Span(m?.MaterialDesc ?? ""); });
                    c.Item().Text(t => { t.Span("Created By  ").Bold(); t.Span(s.Sample.CreatedBy); });
                });
            });
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    Field(c, "Product",   m?.MajorCategory);
                    Field(c, "Grower",    s.Sample.Grower);
                    Field(c, "Variety",   m?.Variety);
                    Field(c, "Size",      m?.MaterialSize);
                    Field(c, "Grade",     m?.MaterialClass);
                    Field(c, "Class",     m?.MaterialClass);
                    Field(c, "Pack Type", m?.PackType);
                    Field(c, "Brand",     m?.Brand);
                });
                row.RelativeItem().Column(c =>
                {
                    Field(c, "Date Code",        s.Sample.DateCode);
                    Field(c, "Pallet",           s.Sample.PalletNo);
                    Field(c, "Sample Size",      s.Sample.SampleSize?.ToString());
                    Field(c, "PUC",              s.ReadingNum("PUC")?.ToString());
                    Field(c, "PHC",              s.ReadingNum("PHC")?.ToString());
                    Field(c, "Lot",              s.Sample.LotNo);
                    Field(c, "Package Material", s.Sample.PackagingMaterial ?? s.ReadingTxt("PACKAGING_MATERIAL"));
                    Field(c, "TARRA",            s.ReadingNum("TARA")?.ToString());
                });
                row.RelativeItem().Column(c =>
                {
                    var sticker = s.ReadingNum("STICKER")?.ToString();
                    var stickerText = s.ReadingTxt("STICKER");
                    var color = s.ReadingTxt("COLOUR") ?? "";
                    Field(c, "Gross Weight",  s.ReadingNum("GROSS_WEIGHT")?.ToString());
                    Field(c, "STICKER",       sticker, stickerText);
                    Field(c, "Brix",          s.ReadingNum("BRIX")?.ToString());
                    Field(c, "Net Weight",    s.ReadingNum("NET_WEIGHT")?.ToString());
                    Field(c, "Waxing",        s.ReadingTxt("WAXING"));
                    Field(c, "Colour",        s.ReadingNum("COLOUR")?.ToString(), color);
                    Field(c, "Firmness",      s.ReadingNum("FIRMNESS")?.ToString());
                    Field(c, "Downgrade",     s.ReadingNum("DOWNGRADE")?.ToString());
                });
            });

            col.Item().PaddingTop(4).Background(AccentLight).Padding(3)
                .Text("Defects").Bold();
            col.Item().Element(c => RenderDefectGrid(c, s));
        });
    }

    // ============================== Image page =============================
    private static void RenderImagesPage(QuestPDF.Fluent.PageDescriptor page, QualityReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeaderBand(h, d, "Images"));
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
