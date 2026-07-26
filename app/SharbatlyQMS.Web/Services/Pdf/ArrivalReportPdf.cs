using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Arrival Checklist PDF, modelled on the sample the user provided (see
/// Arrival.pdf in chat). Two-column rows where the label sits left and
/// the answer (YES/NO/value) sits right. Each numbered section is its own
/// table block. A signature panel sits at the bottom of the last page.
///
/// The company logo is loaded from <c>BrandingConfig.LogoFilename</c> via the
/// caller-provided absolute path; when missing, a colored "QMS" tile draws
/// instead so the document always renders.
/// </summary>
public static class ArrivalReportPdf
{
    private const string Accent      = "#0d6efd";
    private const string AccentLight = "#e7f1ff";
    private const string GridLine    = "#9aa1a8";
    private const string LabelBg     = "#f3f5f7";

    public static byte[] Build(ArrivalReportData d)
    {
        // Numbering kept for the appendix caption only -- there's no separate
        // enlarged-image page anymore; clicking a thumbnail opens the original
        // photo in the user's browser via an absolute HTTP hyperlink.
        for (int i = 0; i < d.Images.Count; i++) d.Images[i].OrderIndex = i + 1;

        var doc = Document.Create(container =>
        {
            container.Page(page => RenderPage(page, d));
            // Appendix only when there are images; PDF stays compact otherwise.
            if (d.Images.Count > 0)
                container.Page(page => RenderAppendix(page, d));
        });
        return doc.GeneratePdf();
    }

    private static void RenderPage(QuestPDF.Fluent.PageDescriptor page, ArrivalReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeader(h, d));
        page.Footer().Element(f => RenderFooter(f, d));

        page.Content().PaddingVertical(8).Column(col =>
        {
            col.Spacing(8);

            // -- Identity block (Company / Date / Container / Carrier / Seal) --
            col.Item().Element(c => RenderIdentityTable(c, d));

            // -- 1. External Inspection --
            col.Item().Element(c => RenderSectionHeader(c, "1. External Inspection"));
            col.Item().Element(c => RenderQaTable(c, new[]
            {
                ("Seal intact?",                    YN(d.Checklist.SealIntact)),
                ("Seal number matches documents?",  YN(d.Checklist.SealMatchesDocuments)),
                ("External damage to container / pallets?", YN(d.Checklist.ExternalDamageExists))
            }));

            // -- Temperature Unit Reading (sub-block of section 1 in the sample) --
            col.Item().Element(c => RenderSubHeader(c, "Temperature Unit Reading"));
            col.Item().Element(c => RenderQaTable(c, new[]
            {
                ("Set Temp:",             d.Checklist.SetTemperature?.ToString("0.##") ?? ""),
                ("Display Temp:",         d.Checklist.DisplayTemperature?.ToString("0.##") ?? ""),
                ("Photo of display taken.", YN(d.Checklist.DisplayTempPhotoTaken))
            }));

            // -- 2. Internal Inspection --
            col.Item().Element(c => RenderSectionHeader(c, "2. Internal Inspection"));
            col.Item().Element(c => RenderQaTable(c, new[]
            {
                ("Cargo smell normal?",                       YN(d.Checklist.CargoSmellNormal)),
                ("Visual cargo condition acceptable?",        YN(d.Checklist.VisualCargoAcceptable)),
                ("Any cargo shifted, collapsed pallets, or water?", YN(d.Checklist.CargoShiftedCollapsedWater))
            }));

            // -- 3. Pulp Temperature Check (3-column table) --
            col.Item().Element(c => RenderSectionHeader(c, "3. Pulp Temperature Check"));
            col.Item().Element(c => RenderPulpTable(c, d));

            // -- 4. Data Logger Inspection --
            col.Item().Element(c => RenderSectionHeader(c, "4. Data Logger Inspection"));
            col.Item().Element(c => RenderQaTable(c, new[]
            {
                ("Locate and retrieve data logger from container", YN(d.Checklist.DataLoggerLocated)),
                ("Record logger serial number",                    JoinLines(d.Checklist.DataLoggerSerial)),
                ("Take photo of the data logger",                  YN(d.Checklist.DataLoggerPhotoTaken)),
                ("Handover logger to Quality / Logistics for downloading temperature data",
                                                                   YN(d.Checklist.LoggerHandedOver)),
                ("Check if logger is active and data available",   YN(d.Checklist.LoggerActiveDataAvailable)),
                ("Data logger temperature",                        d.Checklist.LoggerTemperature?.ToString("0.##") ?? "")
            }));

            // -- 5. Notes / Observations --
            col.Item().Element(c => RenderSectionHeader(c, "5. Notes / Observations"));
            col.Item().Border(0.6f).BorderColor(GridLine).Padding(8).MinHeight(50)
                .Text(d.Checklist.Notes ?? "");

            // -- 6. Attachments --
            col.Item().Element(c => RenderSectionHeader(c, "6. Attachments"));
            col.Item().Element(c => RenderQaTable(c, new[]
            {
                ("Container Seal Photo",                  YN(d.Checklist.ContainerSealPhotoTaken)),
                ("External Container Photo",              YN(d.Checklist.ExternalContainerPhotoTaken)),
                ("External Damage Photo (if any)",        YN(d.Checklist.ExternalDamagePhotoTaken)),
                ("Temperature Display Photo (if it's reefer)", YN(d.Checklist.DisplayTempPhotoTaken)),
                ("First View of Cargo Inside Photo",      YN(d.Checklist.FirstViewCargoPhotoTaken)),
                ("Internal Damage Photo (if any)",        YN(d.Checklist.InternalDamagePhotoTaken)),
                ("Pulp Temperature Photos",               YN(d.Checklist.PulpTempPhotoTaken)),
                ("Data Logger Photo",                     YN(d.Checklist.DataLoggerPhotoTaken))
            }));

            // -- Signatures --
            col.Item().PaddingTop(14).Element(c => RenderSignatures(c, d));
        });
    }

    // =================================================================== blocks
    private static void RenderHeader(IContainer container, ArrivalReportData d)
    {
        container.Row(row =>
        {
            row.ConstantItem(60).AlignMiddle().Element(e =>
            {
                if (!string.IsNullOrWhiteSpace(d.LogoAbsolutePath) && File.Exists(d.LogoAbsolutePath))
                {
                    try { e.Height(48).Image(d.LogoAbsolutePath).FitArea(); return; }
                    catch { /* fall through to placeholder */ }
                }
                e.Width(48).Height(48)
                    .Background(Accent).AlignCenter().AlignMiddle()
                    .Text("QMS").FontColor(Colors.White).FontSize(11).Bold();
            });
            row.RelativeItem().Column(c =>
            {
                c.Item().AlignCenter().Text(d.Branding.CompanyName).FontSize(11).Bold();
                c.Item().AlignCenter().PaddingTop(2).Text("Arrival Checklist").FontSize(15).Bold().FontColor(Accent);
            });
            row.ConstantItem(60).AlignMiddle().AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(d.Arrival.ArrivalNo ?? "").FontSize(8).FontColor(Colors.Grey.Darken1);
            });
        });
    }

    private static void RenderFooter(IContainer container, ArrivalReportData d)
    {
        container.PaddingTop(6).BorderTop(0.4f).BorderColor(Colors.Grey.Lighten1).PaddingTop(4)
            .Row(row =>
            {
                row.RelativeItem().AlignLeft().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken2));
                    t.Span(d.Branding.FooterLine);
                });
                row.ConstantItem(80).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken2));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
    }

    private static void RenderIdentityTable(IContainer container, ArrivalReportData d)
    {
        var dateStr = (d.Shipment?.ArrivalDate
                        ?? d.Arrival.CompletedAt
                        ?? d.Arrival.CreatedAt).ToLocalTime().ToString("MMMM d, yyyy");

        var rows = new List<(string, string)>
        {
            ("Company Name",              d.Branding.CompanyName),
            ("Date",                      dateStr),
            ("Container / Airway Bill # :", d.Arrival.ContainerNo ?? ""),
            ("Carrier Name:",             d.Checklist.CarrierName ?? d.Arrival.VendorName ?? ""),
            ("Seal Number",               JoinLines(d.Checklist.SealNo))
        };
        // V36: admin-defined arrival fields print directly after Seal Number,
        // in the catalog's sort order (only fields applicable to this
        // arrival's material groups are in the list).
        foreach (var cf in d.CustomFields)
            rows.Add((cf.FieldName, cf.DisplayValue));
        RenderQaTable(container, rows.ToArray());
    }

    private static void RenderSectionHeader(IContainer container, string text)
        => container.PaddingTop(4).Text(text).Bold().FontSize(11);

    private static void RenderSubHeader(IContainer container, string text)
        => container.Background(LabelBg).Padding(4).Text(text).Bold().FontSize(10);

    /// <summary>
    /// Two-column QA grid: label on the left (with light background), answer
    /// on the right. Matches the visual style of the user's sample template
    /// (cells with thin gray borders, label cell slightly tinted).
    /// </summary>
    private static void RenderQaTable(IContainer container, IEnumerable<(string Label, string Value)> rows)
    {
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2.2f); // label
                c.RelativeColumn(3.8f); // value
                c.RelativeColumn(2f);   // notes / spare column (matches sample's empty 3rd cell)
            });
            foreach (var (label, value) in rows)
            {
                t.Cell().Border(0.5f).BorderColor(GridLine).Background(LabelBg).Padding(5)
                    .Text(label).FontSize(9);
                t.Cell().Border(0.5f).BorderColor(GridLine).Padding(5)
                    .Text(value).FontSize(9);
                t.Cell().Border(0.5f).BorderColor(GridLine).Padding(5).Text(""); // spare
            }
        });
    }

    private static void RenderPulpTable(IContainer container, ArrivalReportData d)
    {
        container.Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.RelativeColumn(2f);
                c.RelativeColumn(3f);
                c.RelativeColumn(3f);
            });
            // header row
            t.Cell().Background(LabelBg).Border(0.5f).BorderColor(GridLine).Padding(5).Text("Sample Location").Bold();
            t.Cell().Background(LabelBg).Border(0.5f).BorderColor(GridLine).Padding(5).Text("Pulp Temp (°C)").Bold();
            t.Cell().Background(LabelBg).Border(0.5f).BorderColor(GridLine).Padding(5).Text("Photo Taken").Bold();

            void Row(string name, decimal? temp, bool? photo)
            {
                t.Cell().Border(0.5f).BorderColor(GridLine).Padding(5).Text(name);
                t.Cell().Border(0.5f).BorderColor(GridLine).Padding(5).Text(temp?.ToString("0.##") ?? "");
                t.Cell().Border(0.5f).BorderColor(GridLine).Padding(5).Text(YN(photo));
            }
            // The user's sample shows two pulp readings (Temperature 1 / 2).
            // Our schema actually has three (Front / Middle / Back) -- map the
            // first two to the sample's layout, keep the third as an extra row
            // if filled.
            Row("Temperature 1", d.Checklist.PulpTempFront,  d.Checklist.PulpTempPhotoTaken);
            Row("Temperature 2", d.Checklist.PulpTempMiddle, d.Checklist.PulpTempPhotoTaken);
            if (d.Checklist.PulpTempBack.HasValue)
                Row("Temperature 3 (back)", d.Checklist.PulpTempBack, d.Checklist.PulpTempPhotoTaken);
        });
    }

    private static void RenderSignatures(IContainer container, ArrivalReportData d)
    {
        var completedBy = string.IsNullOrWhiteSpace(d.Arrival.CompletedBy) ? d.Arrival.CreatedBy : d.Arrival.CompletedBy;
        container.Column(col =>
        {
            col.Spacing(20);

            col.Item().Row(row =>
            {
                row.RelativeItem().Text("Arrival Procedure Completed By:").Bold();
                row.RelativeItem().Column(c =>
                {
                    c.Item().AlignCenter().Text("Name").FontColor(Colors.Grey.Darken1).FontSize(8);
                    c.Item().AlignCenter().Text(completedBy ?? "").FontSize(10);
                    c.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken1);
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().AlignCenter().Text("Signature").FontColor(Colors.Grey.Darken1).FontSize(8);
                    c.Item().Height(18);
                    c.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken1);
                });
            });

            col.Item().Row(row =>
            {
                row.RelativeItem().Text("Supervisor Review:").Bold();
                row.RelativeItem().Column(c =>
                {
                    c.Item().AlignCenter().Text("Name").FontColor(Colors.Grey.Darken1).FontSize(8);
                    c.Item().Height(18);
                    c.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken1);
                });
                row.RelativeItem().Column(c =>
                {
                    c.Item().AlignCenter().Text("Signature").FontColor(Colors.Grey.Darken1).FontSize(8);
                    c.Item().Height(18);
                    c.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken1);
                });
            });
        });
    }

    private static string YN(bool? b) => b switch { true => "YES", false => "NO", _ => "—" };

    /// <summary>Seal numbers and logger serials are stored newline-delimited
    /// (up to 4 values). Present them on one line separated by " / ".</summary>
    private static string JoinLines(string? s) => string.IsNullOrWhiteSpace(s)
        ? ""
        : string.Join(" / ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                               .Select(x => x.Trim()).Where(x => x.Length > 0));

    // =================================================================== appendix
    private static void RenderAppendix(QuestPDF.Fluent.PageDescriptor page, ArrivalReportData d)
    {
        page.Size(PageSizes.A4);
        page.Margin(28);
        page.DefaultTextStyle(t => t.FontSize(9).FontColor(Colors.Black));

        page.Header().Element(h => RenderHeader(h, d));
        page.Footer().Element(f => RenderFooter(f, d));

        page.Content().PaddingVertical(8).Column(col =>
        {
            col.Spacing(10);
            col.Item().Text("Images Appendix").FontSize(15).Bold().FontColor(Accent);
            col.Item().Text($"{d.Images.Count} photo(s) attached to this arrival.")
                .FontSize(9).FontColor(Colors.Grey.Darken1);

            // Stable category order matching the checklist sections.
            var order = new[] { "ArrivalExternal", "ArrivalInternal", "TemperatureDisplay", "DataLogger" };
            var groups = d.Images
                .GroupBy(i => string.IsNullOrWhiteSpace(i.Category) ? "Other" : i.Category)
                .OrderBy(g => Array.IndexOf(order, g.Key) is int ix && ix >= 0 ? ix : 99)
                .ToList();

            foreach (var grp in groups)
            {
                col.Item().PaddingTop(4).Element(c =>
                    RenderSubHeader(c, FriendlyCategory(grp.Key) + $"  ({grp.Count()})"));
                col.Item().Element(c => RenderImageGrid(c, d, grp.ToList()));
            }
        });
    }

    private static string FriendlyCategory(string cat) => cat switch
    {
        "ArrivalExternal"    => "External container photos",
        "ArrivalInternal"    => "Internal / cargo-view photos",
        "TemperatureDisplay" => "Temperature display photos",
        "DataLogger"         => "Data logger photos",
        "Other"              => "Other",
        _                    => cat
    };

    private static void RenderImageGrid(IContainer container, ArrivalReportData d, List<ImageRef> images)
    {
        // The configured PdfWidth/PdfHeight values are treated as PDF points
        // (1 pt = 1/72 inch) so the size the admin types in Site Configuration
        // is exactly the cell size in the rendered PDF. No CSS-px conversion
        // -- previous 0.75x multiplier made the photos noticeably smaller
        // than the admin expected.
        float cellW = Math.Max(60, d.ThumbnailW);
        float cellH = Math.Max(45, d.ThumbnailH);
        const float pageInner = 539f;
        int columns = Math.Clamp((int)Math.Floor(pageInner / (cellW + 8f)), 1, 6);

        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                for (int i = 0; i < columns; i++) c.ConstantColumn(cellW + 8f);
            });
            foreach (var img in images)
            {
                // Self-contained inline image -- no URLs, no click target, no
                // enlarged copy. The bytes were pre-resized + JPEG-compressed
                // by the controller using the configured PDF width/height.
                table.Cell().Padding(4).Element(cell =>
                {
                    var box = cell.Width(cellW).Height(cellH)
                        .Border(0.6f).BorderColor(Colors.Grey.Medium)
                        .Background("#f5f5f5");

                    if (img.InlineBytes is { Length: > 0 } bytes)
                    {
                        try
                        {
                            // Embed our high-quality JPEG as-is (no 72-DPI re-raster).
                            if (d.ThumbCover) box.Image(bytes).UseOriginalImage().FitUnproportionally();
                            else              box.AlignCenter().AlignMiddle().Image(bytes).UseOriginalImage().FitArea();
                        }
                        catch
                        {
                            box.AlignCenter().AlignMiddle()
                                .Text("(image error)").FontColor(Colors.Grey.Darken1).FontSize(7);
                        }
                    }
                    else
                    {
                        box.AlignCenter().AlignMiddle()
                            .Text("(missing)").FontColor(Colors.Grey.Darken1).FontSize(7);
                    }
                });
            }
        });
    }

}
