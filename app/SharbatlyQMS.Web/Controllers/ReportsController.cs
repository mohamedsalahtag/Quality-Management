using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using SharbatlyQMS.Web.ViewModels;
using SixLabors.ImageSharp.Processing;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class ReportsController : Controller
{
    private readonly IQualityOrderService _qos;
    private readonly IArrivalService _arrivals;
    private readonly IImageService _images;
    private readonly ISettingsService _settings;
    private readonly IMaraService _mara;
    private readonly IVendorService _vendors;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ReportsController> _log;

    public ReportsController(IQualityOrderService qos, IArrivalService arrivals,
        IImageService images, ISettingsService settings, IMaraService mara,
        IVendorService vendors, IEmailService email,
        IConfiguration config, IWebHostEnvironment env, ILogger<ReportsController> log)
    {
        _qos = qos; _arrivals = arrivals; _images = images;
        _settings = settings; _mara = mara;
        _vendors = vendors; _email = email;
        _config = config; _env = env; _log = log;
    }

    /// <summary>
    /// Arrival Checklist PDF -- renders every checklist field, pulp temps,
    /// data logger info, notes, attachments, and a signature block. Layout
    /// mirrors the sample template the user provided (see chat).
    /// When the arrival has images attached, an Images Appendix page is
    /// added at the end with the photos grouped by category.
    /// </summary>
    public async Task<IActionResult> ArrivalChecklistPdf(long id)
    {
        var arrival = await _arrivals.GetAsync(id);
        if (arrival == null) return NotFound();

        var checklist = await _arrivals.GetChecklistAsync(id) ?? new SharbatlyQMS.Web.Models.ArrivalChecklist { ArrivalId = id };
        var shipment  = await _arrivals.GetShipmentAsync(id);
        var branding  = await _settings.GetBrandingConfigAsync();

        string? logoPath = null;
        if (branding.HasLogo)
        {
            var candidate = ResolveBrandingFile(branding.LogoFilename);
            if (candidate != null) logoPath = candidate;
        }

        var thumb = await _settings.GetThumbnailConfigAsync();

        // Images attached to this arrival -- pre-resized + JPEG-compressed at
        // PDF-generation time so the PDF is fully self-contained (no URLs, no
        // server references) and small. 2x the configured PDF dimensions gives
        // QuestPDF headroom for crisp rendering when the reader zooms in.
        // Preprocessing runs in parallel via PreprocessImages.
        var targetW = Math.Max(120, thumb.PdfWidth  * 2);
        var targetH = Math.Max(90,  thumb.PdfHeight * 2);
        var images = PreprocessImages(await _images.ListAsync("Arrival", id), targetW, targetH);

        var data = new SharbatlyQMS.Web.Services.Pdf.ArrivalReportData
        {
            Arrival          = arrival,
            Checklist        = checklist,
            Shipment         = shipment,
            Branding         = branding,
            LogoAbsolutePath = logoPath,
            Images           = images,
            ThumbnailW       = thumb.PdfWidth,
            ThumbnailH       = thumb.PdfHeight,
            ThumbCover       = thumb.FitMode.Equals("Cover", StringComparison.OrdinalIgnoreCase),
            GeneratedBy      = User.FindFirstValue(ClaimTypes.Name) ?? "system",
            GeneratedAt      = DateTime.UtcNow
        };

        var bytes = SharbatlyQMS.Web.Services.Pdf.ArrivalReportPdf.Build(data);
        var name  = $"{arrival.ArrivalNo}-checklist-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
        return File(bytes, "application/pdf", name);
    }

    /// <summary>
    /// AJAX endpoint used by the "Send report to supplier" dialog on the QO
    /// Details page. Returns the resolved To / CC / subject / body so the
    /// dialog can pre-populate (operator may edit before sending).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PrepareSendQualityReport(long id)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();
        var arrival = await _arrivals.GetAsync(qo.ArrivalId);
        var template = await _settings.GetQoMailTemplateAsync();

        VendorInfo? vendor = null;
        if (!string.IsNullOrWhiteSpace(arrival?.VendorNo))
            vendor = await _vendors.GetAsync(arrival.VendorNo!);

        string Substitute(string s) => (s ?? "")
            .Replace("{QO_NO}",     qo.QualityOrderNo)
            .Replace("{CONTAINER}", qo.ContainerNo ?? "")
            .Replace("{BOL}",       qo.BolNo ?? "")
            .Replace("{PO}",        qo.Ebeln ?? "")
            .Replace("{SUPPLIER}",  qo.VendorName ?? vendor?.Name ?? "")
            .Replace("{TODAY}",     DateTime.Today.ToString("yyyy-MM-dd"));

        return Json(new
        {
            ok            = true,
            to            = vendor?.Email ?? "",
            supplierName  = qo.VendorName ?? vendor?.Name ?? "",
            emailKnown    = !string.IsNullOrWhiteSpace(vendor?.Email),
            subject       = Substitute(template.Subject),
            body          = Substitute(template.Body),
            fileName      = $"{qo.QualityOrderNo}-{DateTime.Now:yyyyMMdd-HHmm}.pdf"
        });
    }

    /// <summary>
    /// Builds the same PDF as the download flow, then sends it to the
    /// supplier via SMTP. To / CC / subject / body are whatever the operator
    /// confirmed in the dialog. Returns JSON for the AJAX caller.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SendQualityReport(long id, string to, string? cc, string subject, string body)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return Json(new { ok = false, error = "Quality Order not found." });
        if (string.IsNullOrWhiteSpace(to))
            return Json(new { ok = false, error = "Recipient email is required." });

        var data  = await BuildDataAsync(qo);
        var bytes = QualityReportPdf.Build(data);
        var fileName = $"{qo.QualityOrderNo}-{DateTime.Now:yyyyMMdd-HHmm}.pdf";

        var toList = SplitAddrs(to);
        var ccList = SplitAddrs(cc ?? "");
        var (ok, error) = await _email.SendWithAttachmentsAsync(
            toList, ccList, subject, body, bodyIsHtml: false,
            attachments: new[] { (bytes, fileName, "application/pdf") });

        if (!ok) return Json(new { ok = false, error = error ?? "Send failed." });
        _log.LogInformation("QO {Qo} report emailed to {To} (cc={Cc})", qo.QualityOrderNo, string.Join(",", toList), string.Join(",", ccList));
        return Json(new { ok = true });
    }

    private static IEnumerable<string> SplitAddrs(string s) =>
        (s ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0);

    public async Task<IActionResult> QualityOrderPdf(long id)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();

        var data = await BuildDataAsync(qo);
        var bytes = QualityReportPdf.Build(data);

        // Record a report log entry so the admin audit shows the regeneration.
        try
        {
            using var c = new SqlConnection(_config.GetConnectionString("Default"));
            await c.ExecuteAsync(@"
                INSERT INTO qms_report_log (quality_order_id, archive_path, calculation_ver, page_count, file_size_bytes, generated_at, generated_by)
                VALUES (@qoId, @path, 'v1', @pages, @size, SYSUTCDATETIME(), @user)",
                new
                {
                    qoId = qo.QualityOrderId,
                    path = $"in-memory:{qo.QualityOrderNo}",
                    pages = 0,
                    size = bytes.Length,
                    user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system"
                });
        }
        catch (Exception ex) { _log.LogWarning(ex, "Report log write failed (non-fatal)"); }

        var fileName = $"{qo.QualityOrderNo}-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    private async Task<QualityReportData> BuildDataAsync(QualityOrder qo)
    {
        var data = new QualityReportData
        {
            QualityOrder = qo,
            GeneratedAt  = DateTime.UtcNow,
            GeneratedBy  = User.FindFirst(ClaimTypes.Name)?.Value ?? "system"
        };

        // Branding: same flow the Arrival PDF uses -- resolve the configured
        // logo to an absolute path; the renderer falls back to a placeholder
        // when the file is missing or no logo is configured.
        var branding = await _settings.GetBrandingConfigAsync();
        if (branding.HasLogo)
        {
            var candidate = ResolveBrandingFile(branding.LogoFilename);
            if (candidate != null) data.LogoAbsolutePath = candidate;
        }
        if (!string.IsNullOrWhiteSpace(branding.CompanyName)) data.CompanyName   = branding.CompanyName;
        if (!string.IsNullOrWhiteSpace(branding.FooterLine))  data.CompanyFooter = branding.FooterLine;

        var arrival = await _arrivals.GetAsync(qo.ArrivalId);
        if (arrival != null) data.Arrival = arrival;
        data.Shipment  = await _arrivals.GetShipmentAsync(qo.ArrivalId);
        data.Checklist = await _arrivals.GetChecklistAsync(qo.ArrivalId);

        var materials = (await _qos.GetMaterialsAsync(qo.QualityOrderId)).ToList();

        // Refresh material attributes from the local MARA cache, joined on
        // MaterialNo. The same merge runs on the QO Details page so the
        // screen and the report always agree on what MARA says.
        var mara = await _mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (mara.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);
        data.Materials = materials;

        // Page-1 grouped summary (replaces the per-sample summary blocks).
        // Materials are passed in already MARA-enriched so the grouping key
        // (MaterialGroup, Brand, Variety, Grade) reflects the live cache.
        data.GroupSummaries = await _qos.BuildGroupSummariesAsync(qo.QualityOrderId, materials);

        // Active catalog per material_group, for the per-sample defect render
        // (every sample card shows the FULL catalog with zeros for any defect
        // not recorded -- same source of truth as the page-1 grouped summary).
        var distinctGroups = materials
            .Select(m => m.MaterialGroup ?? "")
            .Where(g => g.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var defectsByGroup = new Dictionary<string, IReadOnlyList<SharbatlyQMS.Web.Models.DefectCatalogEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var grp in distinctGroups)
            defectsByGroup[grp] = await _qos.GetActiveDefectsForGroupAsync(grp);
        data.DefectsByGroup = defectsByGroup;

        // Defect category master (V22+) — drives the per-category sections + colours.
        data.Categories = await _qos.GetActiveCategoriesAsync();

        // Same compact-inline pipeline as the arrival report: pre-resize +
        // JPEG-compress at PDF-generation time so the PDF is self-contained
        // (no URLs, no server references) and small enough to send to
        // suppliers. 2x the configured PDF dimensions gives QuestPDF headroom
        // for crisp on-screen and print zoom.
        var thumb = await _settings.GetThumbnailConfigAsync();
        var targetW = Math.Max(120, thumb.PdfWidth  * 2);
        var targetH = Math.Max(90,  thumb.PdfHeight * 2);

        var samples = await _qos.ListSamplesAsync(qo.QualityOrderId);

        // Batch-load readings + defects for every sample in one round-trip
        // each, and pre-resolve the (group, category) section map per unique
        // material rather than per sample. Replaces a 3*N + 1 query pattern
        // that made 20-sample reports a 40+ second wait.
        var sampleIds = samples.Select(x => x.SampleId).ToArray();
        var readingsBySample = await _qos.GetReadingsBatchAsync(sampleIds);
        var defectsBySample  = await _qos.GetDefectsBatchAsync(sampleIds);
        // V20+ -- dynamic sample header values, batched.
        // The per-sample list includes both Sample-scoped values AND a copy of
        // the Material-scoped values (V23+ copy-down). The PDF renderer
        // separates them at draw time using HeaderFieldScopeById below: it
        // shows Material-scoped values ONCE on a per-material card, then only
        // Sample-scoped values on each compact sample card. So we don't try to
        // filter here -- the bundle carries the full list as before.
        var headerBySample   = await _qos.GetSampleHeaderValuesBatchAsync(sampleIds);

        // V21+ -- the master Material-scoped values live on
        // qms_qo_material_header_value. They are also copied onto each sample
        // (above), but the per-material card in the PDF renders them from the
        // material-level table so a material with no samples yet still shows
        // its identifying values.
        var materialHeaderByMat = await _qos.GetMaterialHeaderValuesBatchAsync(
            materials.Select(m => m.QoMaterialId));

        // FieldId -> Scope ("Sample"|"Material") so the renderer can route each
        // copied-down header value to the right block (material card vs sample
        // card). Cached / once per PDF.
        var headerFields = await _qos.GetActiveSampleHeaderFieldsAsync();
        data.HeaderFieldScopeById = headerFields.ToDictionary(f => f.FieldId, f => f.Scope ?? "Sample");

        var distinctMatKeys = materials
            .Select(m => (Group: m.MaterialGroup, Category: m.MajorCategory))
            .Distinct()
            .ToArray();
        var sectionMaps = new Dictionary<(string?, string?), IReadOnlyDictionary<string, string>>();
        foreach (var k in distinctMatKeys)
            sectionMaps[k] = await _qos.GetDisplaySectionMapAsync(k.Group, k.Category);

        foreach (var s in samples)
        {
            var mat = materials.FirstOrDefault(m => m.QoMaterialId == s.QoMaterialId);
            var key = (mat?.MaterialGroup, mat?.MajorCategory);
            var sectionMap = sectionMaps.TryGetValue(key, out var sm)
                ? sm
                : (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
            data.Samples.Add(new SampleBundle
            {
                Sample       = s,
                Material     = mat,
                Readings     = readingsBySample[s.SampleId].ToList(),
                Defects      = defectsBySample[s.SampleId].ToList(),
                SectionMap   = new Dictionary<string, string>(sectionMap),
                HeaderValues = headerBySample[s.SampleId].ToList(),
                MaterialHeaderValues = materialHeaderByMat[s.QoMaterialId].ToList(),
                Images       = new List<SharbatlyQMS.Web.Services.Pdf.ImageRef>()  // images live on the QO, not per-sample
            });
        }

        // Images come from two owners: the parent Arrival (rendered as a
        // dedicated block right after the page-1 Summary so the reader sees
        // shipment/checklist photos before drilling into per-material data)
        // and per-QO material (rendered as the appendix grouped by material).
        // No per-sample sub-galleries.
        data.ArrivalImages = PreprocessImages(
            await _images.ListAsync("Arrival", qo.ArrivalId), targetW, targetH);
        data.MaterialImages = new Dictionary<long, List<SharbatlyQMS.Web.Services.Pdf.ImageRef>>();
        // Fetch every material's image list in parallel, then preprocess.
        // Each ListAsync opens its own SqlConnection, so concurrency is safe.
        var listingTasks = materials.Select(m =>
            (m.QoMaterialId, ListTask: _images.ListAsync("QualityOrderMaterial", m.QoMaterialId))).ToArray();
        await Task.WhenAll(listingTasks.Select(t => t.ListTask));
        foreach (var (qoMaterialId, listTask) in listingTasks)
        {
            var matImages = PreprocessImages(listTask.Result, targetW, targetH);
            if (matImages.Count > 0)
                data.MaterialImages[qoMaterialId] = matImages;
        }

        data.ThumbnailW = thumb.PdfWidth;
        data.ThumbnailH = thumb.PdfHeight;
        data.ThumbCover = thumb.FitMode.Equals("Cover", StringComparison.OrdinalIgnoreCase);

        var smtp = await _settings.GetSmtpConfigAsync();
        if (!string.IsNullOrWhiteSpace(smtp.SiteName)) data.SiteName = smtp.SiteName;

        return data;
    }

    private string ToAbsolute(string webPath)
    {
        var rel = webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_env.WebRootPath, rel);
    }

    // Returns the absolute path to a file inside wwwroot/branding only when
    // the supplied name has no directory components and the file exists.
    // Guards against a tampered settings row trying to escape wwwroot via
    // "../" segments.
    private string? ResolveBrandingFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var safe = Path.GetFileName(fileName);
        if (!string.Equals(safe, fileName, StringComparison.Ordinal)) return null;
        var candidate = Path.Combine(_env.WebRootPath, "branding", safe);
        return System.IO.File.Exists(candidate) ? candidate : null;
    }

    // Pre-resizes + JPEG-compresses each image so the PDF embeds the bytes
    // directly. Keeps the PDF self-contained (no URLs, no server refs) and
    // small enough to email to suppliers. Skips images whose original file
    // is missing or whose preprocessing fails -- one bad photo should not
    // break the whole report. Per-image work runs in parallel so a report
    // with many photos doesn't serialize 100ms+ resizes on the request
    // thread.
    private List<SharbatlyQMS.Web.Services.Pdf.ImageRef> PreprocessImages(
        IEnumerable<ViewModels.ImageInfo> assets, int targetW, int targetH)
    {
        var inputs = assets.ToArray();
        if (inputs.Length == 0) return new List<SharbatlyQMS.Web.Services.Pdf.ImageRef>();

        var tasks = inputs.Select(i => Task.Run(() => TryPreprocess(i, targetW, targetH))).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).Where(r => r != null).Cast<SharbatlyQMS.Web.Services.Pdf.ImageRef>().ToList();
    }

    private SharbatlyQMS.Web.Services.Pdf.ImageRef? TryPreprocess(
        ViewModels.ImageInfo i, int targetW, int targetH)
    {
        var origAbs = ToAbsolute(i.StorageUrl);
        if (!System.IO.File.Exists(origAbs)) return null;
        try
        {
            using var src = SixLabors.ImageSharp.Image.Load(origAbs);
            src.Mutate(x => x.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
            {
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(targetW, targetH)
            }));
            using var ms = new MemoryStream();
            src.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 80 });
            return new SharbatlyQMS.Web.Services.Pdf.ImageRef
            {
                InlineBytes  = ms.ToArray(),
                OriginalName = i.OriginalName,
                Category     = i.Category
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Skipping image '{Original}': preprocessing failed", origAbs);
            return null;
        }
    }
}
