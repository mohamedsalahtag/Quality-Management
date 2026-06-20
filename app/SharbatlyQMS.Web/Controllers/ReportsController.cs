using System.Security.Claims;
using ClosedXML.Excel;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Reports;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using SharbatlyQMS.Web.Services.Reports;
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
    private readonly IPivotService _pivot;
    private readonly IPerspectiveService _perspectives;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<ReportsController> _log;

    public ReportsController(IQualityOrderService qos, IArrivalService arrivals,
        IImageService images, ISettingsService settings, IMaraService mara,
        IVendorService vendors, IEmailService email,
        IPivotService pivot, IPerspectiveService perspectives,
        IConfiguration config, IWebHostEnvironment env, ILogger<ReportsController> log)
    {
        _qos = qos; _arrivals = arrivals; _images = images;
        _settings = settings; _mara = mara;
        _vendors = vendors; _email = email;
        _pivot = pivot; _perspectives = perspectives;
        _config = config; _env = env; _log = log;
    }

    /// <summary>
    /// Arrival Checklist PDF -- renders every checklist field, pulp temps,
    /// data logger info, notes, attachments, and a signature block. Layout
    /// mirrors the sample template the user provided (see chat).
    /// When the arrival has images attached, an Images Appendix page is
    /// added at the end with the photos grouped by category.
    /// </summary>
    public async Task<IActionResult> ArrivalChecklistPdf(long id, CancellationToken ct = default)
    {
        var arrival = await _arrivals.GetAsync(id);
        if (arrival == null) return NotFound();
        ct.ThrowIfCancellationRequested();

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
        var images = await PreprocessImagesAsync(await _images.ListAsync("Arrival", id), targetW, targetH);

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
        var name  = $"{arrival.ArrivalNo}-checklist-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf";
        return File(bytes, "application/pdf", name);
    }

    /// <summary>
    /// AJAX endpoint used by the "Send report to supplier" dialog on the QO
    /// Details page. Returns the resolved To / CC / subject / body so the
    /// dialog can pre-populate (operator may edit before sending).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> PrepareSendQualityReport(long id)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();
        // Server-side gates: the UI hides the dialog unless the QO is Closed
        // and the mail template is enabled, but a direct GET would bypass both
        // checks. Refuse here so a tampered request can't pre-populate a
        // dialog for a non-closeable QO or a disabled feature.
        if (qo.StatusCode != QualityOrderStatus.Closed)
            return Json(new { ok = false, error = "Report can only be sent for Closed Quality Orders." });
        var arrival = await _arrivals.GetAsync(qo.ArrivalId);
        var template = await _settings.GetQoMailTemplateAsync();
        if (!template.Enabled)
            return Json(new { ok = false, error = "Sending the quality report by email is disabled in Settings." });

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
            fileName      = $"{qo.QualityOrderNo}-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf"
        });
    }

    /// <summary>
    /// Builds the same PDF as the download flow, then sends it to the
    /// supplier via SMTP. To / CC / subject / body are whatever the operator
    /// confirmed in the dialog. Returns JSON for the AJAX caller.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SendQualityReport(long id, string to, string? cc, string subject, string body, CancellationToken ct = default)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return Json(new { ok = false, error = "Quality Order not found." });
        // Server-side gates -- the UI hides the button when these are false,
        // but a direct POST must not bypass them. Closed QO + mail template
        // enabled. Order matters: state check first so a feature-flag error
        // doesn't leak QO existence.
        if (qo.StatusCode != QualityOrderStatus.Closed)
            return Json(new { ok = false, error = "Report can only be sent for Closed Quality Orders." });
        var template = await _settings.GetQoMailTemplateAsync();
        if (!template.Enabled)
            return Json(new { ok = false, error = "Sending the quality report by email is disabled in Settings." });
        if (string.IsNullOrWhiteSpace(to))
            return Json(new { ok = false, error = "Recipient email is required." });

        var data  = await BuildDataAsync(qo);
        var bytes = QualityReportPdf.Build(data);
        var fileName = $"{qo.QualityOrderNo}-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf";

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

    public async Task<IActionResult> QualityOrderPdf(long id, CancellationToken ct = default)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();
        ct.ThrowIfCancellationRequested();

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

        var fileName = $"{qo.QualityOrderNo}-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf";
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

        // V31 (2026-06-20): photos are now per-sample. Each SampleBundle
        // carries its own Images list, populated by the parallel fetch
        // below. The appendix groups them by material -> sample.
        var sampleImageTasks = samples.ToDictionary(
            s => s.SampleId,
            s => _images.ListAsync("Sample", s.SampleId));
        await Task.WhenAll(sampleImageTasks.Values);
        var sampleImages = new Dictionary<long, List<SharbatlyQMS.Web.Services.Pdf.ImageRef>>();
        foreach (var (sampleId, task) in sampleImageTasks)
        {
            var imgs = await PreprocessImagesAsync(await task, targetW, targetH);
            if (imgs.Count > 0) sampleImages[sampleId] = imgs;
        }
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
                Images       = sampleImages.TryGetValue(s.SampleId, out var simg) ? simg : new List<SharbatlyQMS.Web.Services.Pdf.ImageRef>()
            });
        }

        // V31: Arrival photos still render as their own appendix block.
        // Per-material photos are no longer fetched -- legacy
        // owner_type='QualityOrderMaterial' rows stay on disk + in DB but
        // are not surfaced in new reports.
        data.ArrivalImages = await PreprocessImagesAsync(
            await _images.ListAsync("Arrival", qo.ArrivalId), targetW, targetH);
        data.MaterialImages = new Dictionary<long, List<SharbatlyQMS.Web.Services.Pdf.ImageRef>>();

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
    private async Task<List<SharbatlyQMS.Web.Services.Pdf.ImageRef>> PreprocessImagesAsync(
        IEnumerable<ViewModels.ImageInfo> assets, int targetW, int targetH)
    {
        var inputs = assets.ToArray();
        if (inputs.Length == 0) return new List<SharbatlyQMS.Web.Services.Pdf.ImageRef>();

        // Each TryPreprocess is CPU-bound (ImageSharp resize + JPEG encode);
        // dispatch to the thread pool with Task.Run, then await Task.WhenAll
        // so the request thread is freed while the workers run. Replaces a
        // sync-over-async Task.WaitAll that risked thread-pool starvation.
        var tasks = inputs.Select(i => Task.Run(() => TryPreprocess(i, targetW, targetH))).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<SharbatlyQMS.Web.Services.Pdf.ImageRef>().ToList();
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

    // ===================================================================
    // V31 (2026-06-20) -- Flat per-defect data hub.
    //
    // GET  /Reports/FlatDefects        HTML filter form + preview (first 200 rows).
    // GET  /Reports/FlatDefectsExcel   ClosedXML streaming export of the same query.
    // Auth: SupervisorOrAbove (data hub is for review/analysis, not operators).
    // ===================================================================
    [HttpGet]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> FlatDefects(
        [FromQuery] FlatDefectFilter filter, CancellationToken ct = default)
    {
        ApplyDefaultPoWindow(filter);
        try
        {
            var rows = new List<FlatDefectRow>();
            const int previewLimit = 200;
            await foreach (var row in _qos.StreamFlatDefectRowsAsync(filter, ct))
            {
                rows.Add(row);
                if (rows.Count >= previewLimit) break;
            }
            ViewBag.Filter = filter;
            ViewBag.PreviewLimit = previewLimit;
            ViewBag.Truncated    = rows.Count == previewLimit;
            return View(rows);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FlatDefects failed. qoId={QoId} arrivalId={ArrId} poFrom={From} poTo={To} status={Status} plant={Plant} matGroup={MatGroup} variety={Var} vendor={Vendor}",
                filter.QualityOrderId, filter.ArrivalId, filter.PoFrom, filter.PoTo,
                filter.Status, filter.Plant, filter.MaterialGroup, filter.Variety, filter.VendorName);
            throw;
        }
    }

    // V31 (2026-06-20): default to last 30 days on PO date when the request
    // has no explicit identity filter and no explicit PO range. Keeps the
    // initial page load fast and focused on operational data.
    private static void ApplyDefaultPoWindow(FlatDefectFilter f)
    {
        bool hasIdentity = f.QualityOrderId.HasValue || f.ArrivalId.HasValue;
        bool hasPoRange  = f.PoFrom.HasValue || f.PoTo.HasValue;
        if (hasIdentity || hasPoRange) return;
        var today = DateTime.UtcNow.Date;
        f.PoFrom = today.AddDays(-30);
        f.PoTo   = today;
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> FlatDefectsExcel(
        [FromQuery] FlatDefectFilter filter, CancellationToken ct = default)
    {
        ApplyDefaultPoWindow(filter);
        try
        {
        // ClosedXML streaming pattern -- mirrors AuditController.Export. We
        // discover the dynamic header / reading column set on the FIRST row
        // and write the header row at that point. From row 2 onward we map
        // each row's wide bags into the right columns.
        // V31 (2026-06-20): SAP-native columns keep their technical names;
        // other columns get a human-friendly label. Excel keeps every column
        // (no hiding) AND every row (zero-value defects too) so analytics has
        // complete coverage even though the HTML preview hides some of these.
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("FlatDefects");

        // (technical-property-name, friendly-header-for-Excel)
        var staticHeaders = new (string Prop, string Header)[]
        {
            ("ArrivalNo",        "Arrival Number"),
            ("Plant",            "Plant"),                       // SAP
            ("StorageLocation",  "StorageLocation"),             // SAP
            ("ContainerNo",      "ContainerNo"),                 // SAP
            ("BolNo",            "BolNo"),                       // SAP
            ("Ebeln",            "Ebeln"),                       // SAP (PO number)
            ("Sto",              "Sto"),                         // SAP
            ("PoDate",           "PO Date"),
            ("LoadingDate",      "Loading Date"),
            ("ShippingDate",     "Shipping Date"),
            ("ArrivalDate",      "Arrival Date"),
            ("ReceiveDate",      "Receive Date"),
            ("TransitDays",      "Transit Days"),
            ("VendorNo",         "VendorNo"),                    // SAP
            ("VendorName",       "Supplier"),
            ("QualityOrderNo",   "QC Order Number"),
            ("QoStatus",         "QC Status (raw)"),
            ("QoStatusDisplay",  "QC Status"),
            ("QoCreatedAt",      "QC Order Created At"),
            ("MaterialNo",       "MaterialNo"),                  // SAP
            ("MaterialDesc",     "Material Description"),
            ("MaterialGroup",    "MaterialGroup"),               // SAP
            ("MaterialGroupDesc","Material Group Description"),
            ("MajorCategory",    "Major Category"),
            ("SubMajorCategory", "Sub-Major Category"),
            ("Variety",          "Variety"),
            ("MaterialClass",    "Class"),
            ("Origin",           "Origin"),
            ("Brand",            "Brand"),
            ("PackType",         "Pack Type"),
            ("PackCode",         "Pack Code"),
            ("NetWeight",        "Net Weight"),
            ("ArrivalItemQuantity","Quantity"),
            ("ArrivalItemUom",   "Uom"),                         // SAP
            ("MaterialSize",     "Material Size"),
            ("MaterialSampleSize","Material Sample Size"),
            ("SampleNo",         "Sample Number"),
            ("SampleScope",      "Sample Scope"),
            ("SampleSize",       "Sample Size (inspected qty)"),
            ("SizeOverridden",   "Size Overridden"),
            ("Grower",           "Grower"),
            ("PalletNo",         "Pallet Number"),
            ("GrowerPallet",     "Grower Pallet"),
            ("PackCodeSample",   "Pack Code (sample)"),
            ("DateCode",         "Date Code"),
            ("LabelValue",       "Label"),
            ("LotNo",            "Lot Number"),
            ("SampleCreatedAt",  "Sample Created At"),
            ("SampleCreatedBy",  "Sample Created By"),
            ("DefectCode",       "Defect Code"),
            ("DefectName",       "Defect Name"),
            ("DefectCategory",   "Defect Category"),
            ("SeverityCode",     "Severity"),
            ("DefectValue",      "Defect Count"),
            ("DefectPercentage", "Defect %"),
            ("DefectComment",    "Defect Comment"),
        };
        var staticPropNames = staticHeaders.Select(x => x.Prop).ToArray();
        var staticLabels    = staticHeaders.Select(x => x.Header).ToArray();

        var matHeaderCodes    = new List<string>();
        var sampleHeaderCodes = new List<string>();
        var readingCodes      = new List<string>();
        int rowIdx = 0;
        bool headersWritten = false;

        await foreach (var r in _qos.StreamFlatDefectRowsAsync(filter, ct))
        {
            if (!headersWritten)
            {
                matHeaderCodes.AddRange(r.MaterialHeaderValues.Keys);
                sampleHeaderCodes.AddRange(r.SampleHeaderValues.Keys);
                readingCodes.AddRange(r.Readings.Keys);
                WriteHeaderRow(ws, staticLabels, matHeaderCodes, sampleHeaderCodes, readingCodes);
                headersWritten = true;
                rowIdx = 2;
            }
            else
            {
                foreach (var k in r.MaterialHeaderValues.Keys) if (!matHeaderCodes.Contains(k))
                { matHeaderCodes.Add(k); WriteHeaderRow(ws, staticLabels, matHeaderCodes, sampleHeaderCodes, readingCodes); }
                foreach (var k in r.SampleHeaderValues.Keys) if (!sampleHeaderCodes.Contains(k))
                { sampleHeaderCodes.Add(k); WriteHeaderRow(ws, staticLabels, matHeaderCodes, sampleHeaderCodes, readingCodes); }
                foreach (var k in r.Readings.Keys) if (!readingCodes.Contains(k))
                { readingCodes.Add(k); WriteHeaderRow(ws, staticLabels, matHeaderCodes, sampleHeaderCodes, readingCodes); }
            }

            int col = 1;
            ws.Cell(rowIdx, col++).Value = r.ArrivalNo;
            ws.Cell(rowIdx, col++).Value = r.Plant;
            ws.Cell(rowIdx, col++).Value = r.StorageLocation;
            ws.Cell(rowIdx, col++).Value = r.ContainerNo;
            ws.Cell(rowIdx, col++).Value = r.BolNo;
            ws.Cell(rowIdx, col++).Value = r.Ebeln;
            ws.Cell(rowIdx, col++).Value = r.Sto;
            ws.Cell(rowIdx, col++).Value = r.PoDate?.ToString("yyyy-MM-dd");
            ws.Cell(rowIdx, col++).Value = r.LoadingDate?.ToString("yyyy-MM-dd");
            ws.Cell(rowIdx, col++).Value = r.ShippingDate?.ToString("yyyy-MM-dd");
            ws.Cell(rowIdx, col++).Value = r.ArrivalDate?.ToString("yyyy-MM-dd");
            ws.Cell(rowIdx, col++).Value = r.ReceiveDate?.ToString("yyyy-MM-dd");
            ws.Cell(rowIdx, col++).Value = (int?)r.TransitDays;
            ws.Cell(rowIdx, col++).Value = r.VendorNo;
            ws.Cell(rowIdx, col++).Value = r.VendorName;
            ws.Cell(rowIdx, col++).Value = r.QualityOrderNo;
            ws.Cell(rowIdx, col++).Value = r.QoStatus;
            ws.Cell(rowIdx, col++).Value = r.QoStatusDisplay;
            ws.Cell(rowIdx, col++).Value = r.QoCreatedAt?.ToString("yyyy-MM-dd HH:mm");
            ws.Cell(rowIdx, col++).Value = r.MaterialNo;
            ws.Cell(rowIdx, col++).Value = r.MaterialDesc;
            ws.Cell(rowIdx, col++).Value = r.MaterialGroup;
            ws.Cell(rowIdx, col++).Value = r.MaterialGroupDesc;
            ws.Cell(rowIdx, col++).Value = r.MajorCategory;
            ws.Cell(rowIdx, col++).Value = r.SubMajorCategory;
            ws.Cell(rowIdx, col++).Value = r.Variety;
            ws.Cell(rowIdx, col++).Value = r.MaterialClass;
            ws.Cell(rowIdx, col++).Value = r.Origin;
            ws.Cell(rowIdx, col++).Value = r.Brand;
            ws.Cell(rowIdx, col++).Value = r.PackType;
            ws.Cell(rowIdx, col++).Value = r.PackCode;
            ws.Cell(rowIdx, col++).Value = (double?)r.NetWeight;
            ws.Cell(rowIdx, col++).Value = (double?)r.ArrivalItemQuantity;
            ws.Cell(rowIdx, col++).Value = r.ArrivalItemUom;
            ws.Cell(rowIdx, col++).Value = r.MaterialSize;
            ws.Cell(rowIdx, col++).Value = (int?)r.MaterialSampleSize;
            ws.Cell(rowIdx, col++).Value = r.SampleNo;
            ws.Cell(rowIdx, col++).Value = r.SampleScope;
            ws.Cell(rowIdx, col++).Value = (int?)r.SampleSize;
            ws.Cell(rowIdx, col++).Value = r.SizeOverridden;
            ws.Cell(rowIdx, col++).Value = r.Grower;
            ws.Cell(rowIdx, col++).Value = r.PalletNo;
            ws.Cell(rowIdx, col++).Value = r.GrowerPallet;
            ws.Cell(rowIdx, col++).Value = r.PackCodeSample;
            ws.Cell(rowIdx, col++).Value = r.DateCode;
            ws.Cell(rowIdx, col++).Value = r.LabelValue;
            ws.Cell(rowIdx, col++).Value = r.LotNo;
            ws.Cell(rowIdx, col++).Value = r.SampleCreatedAt.ToString("yyyy-MM-dd HH:mm");
            ws.Cell(rowIdx, col++).Value = r.SampleCreatedBy;
            ws.Cell(rowIdx, col++).Value = r.DefectCode;
            ws.Cell(rowIdx, col++).Value = r.DefectName;
            ws.Cell(rowIdx, col++).Value = r.DefectCategory;
            ws.Cell(rowIdx, col++).Value = r.SeverityCode;
            ws.Cell(rowIdx, col++).Value = (double?)r.DefectValue;
            ws.Cell(rowIdx, col++).Value = (double?)r.DefectPercentage;
            ws.Cell(rowIdx, col++).Value = r.DefectComment;
            // Dynamic columns -- material headers, sample headers, readings.
            foreach (var code in matHeaderCodes)
            {
                r.MaterialHeaderValues.TryGetValue(code, out var v);
                ws.Cell(rowIdx, col++).Value = v;
            }
            foreach (var code in sampleHeaderCodes)
            {
                r.SampleHeaderValues.TryGetValue(code, out var v);
                ws.Cell(rowIdx, col++).Value = v;
            }
            foreach (var code in readingCodes)
            {
                r.Readings.TryGetValue(code, out var v);
                ws.Cell(rowIdx, col++).Value = v;
            }
            rowIdx++;
        }

        if (!headersWritten)
        {
            // Empty result -- still write headers so the file is well-formed.
            WriteHeaderRow(ws, staticLabels, matHeaderCodes, sampleHeaderCodes, readingCodes);
        }
        ws.Row(1).Style.Font.Bold = true;
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        var fileName = $"qms-flat-defects-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "FlatDefectsExcel failed. qoId={QoId} arrivalId={ArrId} poFrom={From} poTo={To}",
                filter.QualityOrderId, filter.ArrivalId, filter.PoFrom, filter.PoTo);
            throw;
        }
    }

    private static void WriteHeaderRow(IXLWorksheet ws, string[] staticHeaders,
        List<string> matHeaderCodes, List<string> sampleHeaderCodes, List<string> readingCodes)
    {
        int col = 1;
        foreach (var h in staticHeaders) ws.Cell(1, col++).Value = h;
        foreach (var c in matHeaderCodes)    ws.Cell(1, col++).Value = "mat_" + c;
        foreach (var c in sampleHeaderCodes) ws.Cell(1, col++).Value = "smp_" + c;
        foreach (var c in readingCodes)      ws.Cell(1, col++).Value = "rd_"  + c;
    }

    // ===================================================================
    // V34 (2026-06-20) -- Perspective Analyzer (server-side pivot).
    //
    // GET    /Reports/PivotSchema?report=flat_defects     dimension / measure metadata
    // POST   /Reports/Pivot                               run GROUP BY, return tall matrix
    // GET    /Reports/Perspectives?report=flat_defects    list saved perspectives
    // POST   /Reports/Perspectives                        save / update
    // POST   /Reports/Perspectives/{id}/Default           toggle default for caller
    // DELETE /Reports/Perspectives/{id}                   delete (owner or SiteAdmin)
    //
    // Auth: SupervisorOrAbove for everything. Save re-checks Manager / SiteAdmin
    // before honouring scope='shared'.
    // ===================================================================
    [HttpGet]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public IActionResult PivotSchema(string report)
    {
        if (string.IsNullOrWhiteSpace(report)
            || !PivotRegistry.All.TryGetValue(report, out var rpt))
            return BadRequest(new { error = $"Unknown report '{report}'" });

        return Json(new
        {
            report     = rpt.Key,
            dimensions = rpt.Dimensions.Select(d => new { key = d.Key, display = d.Display }),
            measures   = rpt.Measures  .Select(m => new { key = m.Key, display = m.Display, aggs = m.AllowedAggs }),
            renderers  = PivotRegistry.Renderers,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> Pivot([FromBody] PivotRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { error = "Empty request" });
        try
        {
            var result = await _pivot.RunAsync(req, ct);
            return Json(result);
        }
        catch (ArgumentException ax)
        {
            return BadRequest(new { error = ax.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pivot failed. report={Report} rows={Rows} cols={Cols} measure={Measure} agg={Agg}",
                req.ReportKey, string.Join(",", req.Rows), string.Join(",", req.Cols),
                req.Measure, req.Agg);
            return StatusCode(500, new { error = "Pivot failed. See server log." });
        }
    }

    /// <summary>
    /// V34.3 (2026-06-20). Polished XLSX export of the analyzer's current
    /// pivot. Runs the same <see cref="PivotRequest"/> as the on-screen Run
    /// button so the download mirrors the screen. Title row, scope/drill
    /// summary, merged column headers when there are multiple measures, bold
    /// + frozen header, per-measure number formats, totals row, auto-width.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> PivotExcel([FromBody] PivotRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { error = "Empty request" });
        PivotResult result;
        try
        {
            result = await _pivot.RunAsync(req, ct);
        }
        catch (ArgumentException ax)
        {
            return BadRequest(new { error = ax.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PivotExcel run failed. report={Report}", req.ReportKey);
            return StatusCode(500, new { error = "Pivot failed. See server log." });
        }

        var bytes = BuildPivotWorkbook(result, req, User.Identity?.Name ?? "");
        var name  = $"pivot-{req.ReportKey ?? "report"}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            name);
    }

    private static byte[] BuildPivotWorkbook(PivotResult r, PivotRequest req, string user)
    {
        int M = r.Measures.Length;
        int rowDimCount = r.RowDimensions.Length;
        int colKeyCount = r.ColKeys.Length;
        int dataCols    = Math.Max(colKeyCount, 1) * M + M;        // body cols + total cols (M)
        int totalCols   = rowDimCount + dataCols;
        bool multiM     = M >= 2;

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Pivot");

        // ----- 1. Header strip --------------------------------------------
        var title = "Perspective Analyzer";
        if (r.RowDimensions.Length > 0 || r.ColDimensions.Length > 0)
        {
            var rd = string.Join(" / ", r.RowDimensions);
            var cd = string.Join(" / ", r.ColDimensions);
            title += " — " + (rd.Length > 0 ? rd : "(none)") + " × " + (cd.Length > 0 ? cd : "(none)");
        }
        ws.Cell(1, 1).Value = title;
        ws.Range(1, 1, 1, Math.Max(totalCols, 1)).Merge().Style
            .Font.SetBold().Font.SetFontSize(13);

        ws.Cell(2, 1).Value = $"Generated for {user} on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        ws.Range(2, 1, 2, Math.Max(totalCols, 1)).Merge().Style
            .Font.SetItalic().Font.SetFontColor(XLColor.Gray);

        ws.Cell(3, 1).Value = (req.IgnorePageFilter ? "Scope: whole dataset" : "Scope: page filters")
            + (req.Filter != null && !req.IgnorePageFilter ? "  ·  " + FilterSummary(req.Filter) : "");
        ws.Range(3, 1, 3, Math.Max(totalCols, 1)).Merge();

        if (req.Drills != null && req.Drills.Count > 0)
        {
            var drillBits = req.Drills
                .Where(d => d.DimensionKey != null && d.Values != null && d.Values.Length > 0)
                .Select(d => d.DimensionKey + " ∈ {" + string.Join(", ", d.Values!) + "}");
            ws.Cell(4, 1).Value = "Drill filters: " + string.Join("  ·  ", drillBits);
            ws.Range(4, 1, 4, Math.Max(totalCols, 1)).Merge();
            // Row 5 left blank.
        }

        int headerTopRow    = (req.Drills != null && req.Drills.Count > 0) ? 6 : 5;
        int headerInnerRow  = multiM ? headerTopRow + 1 : headerTopRow;
        int firstDataRow    = headerInnerRow + 1;

        // ----- 2. Header rows ----------------------------------------------
        // Row-dim labels (rowspan over both header rows when multi-measure).
        for (int i = 0; i < rowDimCount; i++)
        {
            var c = ws.Cell(headerTopRow, i + 1);
            c.Value = r.RowDimensions[i];
            if (multiM) ws.Range(headerTopRow, i + 1, headerInnerRow, i + 1).Merge();
        }

        int dataColStart = rowDimCount + 1;
        if (colKeyCount == 0)
        {
            // Single-measure: one column per measure label.
            for (int m = 0; m < M; m++)
            {
                ws.Cell(headerTopRow, dataColStart + m).Value = r.Measures[m].Label;
            }
        }
        else
        {
            // Top header: one cell per col key, spanning M measure columns.
            for (int ci = 0; ci < colKeyCount; ci++)
            {
                int colStart = dataColStart + ci * M;
                ws.Cell(headerTopRow, colStart).Value = string.Join(" / ", r.ColKeys[ci]);
                if (multiM) ws.Range(headerTopRow, colStart, headerTopRow, colStart + M - 1).Merge();
                // Inner header: measure labels.
                if (multiM)
                    for (int m = 0; m < M; m++)
                        ws.Cell(headerInnerRow, colStart + m).Value = r.Measures[m].Label;
            }
            // Total spans the last M columns.
            int totalStart = dataColStart + colKeyCount * M;
            ws.Cell(headerTopRow, totalStart).Value = "Total";
            if (multiM) ws.Range(headerTopRow, totalStart, headerTopRow, totalStart + M - 1).Merge();
            if (multiM)
                for (int m = 0; m < M; m++)
                    ws.Cell(headerInnerRow, totalStart + m).Value = r.Measures[m].Label;
        }

        var headerRange = ws.Range(headerTopRow, 1, headerInnerRow, totalCols);
        headerRange.Style.Font.SetBold();
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9ECEF");
        headerRange.Style.Border.BottomBorder  = XLBorderStyleValues.Thin;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // ----- 3. Body rows ------------------------------------------------
        // Build a (rowIndex, colIndex) -> Values[] lookup so we can write cells
        // in dense table order, leaving blanks where no row was emitted.
        var cellMap = new Dictionary<(int, int), decimal?[]>(r.Cells.Length);
        foreach (var c in r.Cells) cellMap[(c.RowIndex, c.ColIndex)] = c.Values;

        int outRow = firstDataRow;
        for (int ri = 0; ri < r.RowKeys.Length; ri++)
        {
            // Row-dim cells.
            for (int i = 0; i < rowDimCount; i++)
                ws.Cell(outRow, i + 1).Value = r.RowKeys[ri][i];

            if (colKeyCount == 0)
            {
                for (int m = 0; m < M; m++)
                    SetMeasureCell(ws.Cell(outRow, dataColStart + m),
                        r.RowTotals[ri][m], r.Measures[m].Format);
            }
            else
            {
                for (int ci = 0; ci < colKeyCount; ci++)
                {
                    cellMap.TryGetValue((ri, ci), out var values);
                    for (int m = 0; m < M; m++)
                    {
                        decimal? v = values != null && m < values.Length ? values[m] : null;
                        SetMeasureCell(ws.Cell(outRow, dataColStart + ci * M + m),
                            v, r.Measures[m].Format);
                    }
                }
                int totalStart = dataColStart + colKeyCount * M;
                for (int m = 0; m < M; m++)
                {
                    var cell = ws.Cell(outRow, totalStart + m);
                    SetMeasureCell(cell, r.RowTotals[ri][m], r.Measures[m].Format);
                    cell.Style.Font.SetBold();
                }
            }
            outRow++;
        }

        // ----- 4. Totals row ----------------------------------------------
        if (colKeyCount > 0)
        {
            ws.Cell(outRow, 1).Value = "Total";
            for (int ci = 0; ci < colKeyCount; ci++)
                for (int m = 0; m < M; m++)
                    SetMeasureCell(ws.Cell(outRow, dataColStart + ci * M + m),
                        r.ColTotals[ci][m], r.Measures[m].Format);
            int totalStart = dataColStart + colKeyCount * M;
            for (int m = 0; m < M; m++)
                SetMeasureCell(ws.Cell(outRow, totalStart + m),
                    r.GrandTotals[m], r.Measures[m].Format);
            var trange = ws.Range(outRow, 1, outRow, totalCols);
            trange.Style.Font.SetBold();
            trange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9ECEF");
            trange.Style.Border.TopBorder     = XLBorderStyleValues.Thin;
        }

        // ----- 5. Polish ---------------------------------------------------
        ws.SheetView.FreezeRows(headerInnerRow);
        if (rowDimCount > 0) ws.SheetView.FreezeColumns(rowDimCount);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetMeasureCell(IXLCell cell, decimal? v, string? format)
    {
        if (v == null) { cell.Value = ""; return; }
        cell.Value = (double)v.Value;
        cell.Style.NumberFormat.Format = format switch
        {
            "int"     => "#,##0",
            "dec1"    => "#,##0.0",
            "dec2"    => "#,##0.00",
            "percent" => "0.00%",
            _         => "#,##0.##",   // auto
        };
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    private static string FilterSummary(FlatDefectFilter f)
    {
        var bits = new List<string>();
        if (f.PoFrom.HasValue) bits.Add("PO ≥ " + f.PoFrom.Value.ToString("yyyy-MM-dd"));
        if (f.PoTo.HasValue)   bits.Add("PO ≤ " + f.PoTo.Value.ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(f.Status))          bits.Add("Status=" + f.Status);
        if (!string.IsNullOrWhiteSpace(f.Plant))           bits.Add("Plant=" + f.Plant);
        if (!string.IsNullOrWhiteSpace(f.MaterialGroup))   bits.Add("MaterialGroup=" + f.MaterialGroup);
        if (!string.IsNullOrWhiteSpace(f.MajorCategory))   bits.Add("MajorCat=" + f.MajorCategory);
        if (!string.IsNullOrWhiteSpace(f.Variety))         bits.Add("Variety=" + f.Variety);
        if (!string.IsNullOrWhiteSpace(f.Origin))          bits.Add("Origin=" + f.Origin);
        if (!string.IsNullOrWhiteSpace(f.MaterialClass))   bits.Add("Class=" + f.MaterialClass);
        if (!string.IsNullOrWhiteSpace(f.VendorName))      bits.Add("Vendor~" + f.VendorName);
        if (!string.IsNullOrWhiteSpace(f.VendorNo))        bits.Add("VendorNo=" + f.VendorNo);
        if (!string.IsNullOrWhiteSpace(f.StorageLocation)) bits.Add("StorageLoc=" + f.StorageLocation);
        if (!string.IsNullOrWhiteSpace(f.ContainerNo))     bits.Add("Container=" + f.ContainerNo);
        if (!string.IsNullOrWhiteSpace(f.BolNo))           bits.Add("BOL=" + f.BolNo);
        if (!string.IsNullOrWhiteSpace(f.Ebeln))           bits.Add("PO=" + f.Ebeln);
        if (!string.IsNullOrWhiteSpace(f.SampleScope))     bits.Add("Scope=" + f.SampleScope);
        if (f.QualityOrderId.HasValue)                     bits.Add("QO=" + f.QualityOrderId);
        if (f.ArrivalId.HasValue)                          bits.Add("Arrival=" + f.ArrivalId);
        return bits.Count == 0 ? "(no filter)" : string.Join("  ·  ", bits);
    }

    /// <summary>
    /// V34.1 (2026-06-20). Distinct values for a single dimension, scoped by
    /// the active filter (or whole dataset when ignorePageFilter=true). Drives
    /// the drill-filter values picker. The host page passes its filter as a
    /// nested [FromQuery] FlatDefectFilter so we match the scope the user is
    /// looking at, with a `search` prefix for typeahead. Capped at 200 rows;
    /// short-term cached by PivotService.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> PivotValues(string report, string dim,
        [FromQuery] FlatDefectFilter filter, bool ignorePageFilter = false,
        string? q = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(dim))
            return BadRequest(new { error = "report and dim are required" });
        try
        {
            var (values, truncated) = await _pivot.GetDistinctValuesAsync(
                report, dim, filter, ignorePageFilter, q, ct);
            return Json(new { values, truncated });
        }
        catch (ArgumentException ax)
        {
            return BadRequest(new { error = ax.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PivotValues failed. report={Report} dim={Dim} q={Q}", report, dim, q);
            return StatusCode(500, new { error = "PivotValues failed. See server log." });
        }
    }

    [HttpGet]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> Perspectives(string report, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(report)) return BadRequest(new { error = "report required" });
        var user = User.Identity?.Name ?? "";
        var list = await _perspectives.ListForUserAsync(report, user, ct);
        return Json(new
        {
            canShare = CanShare(),
            items    = list,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> SavePerspective([FromBody] PerspectiveSaveRequest req,
        CancellationToken ct)
    {
        if (req == null) return BadRequest(new { error = "Empty request" });
        try
        {
            var user = User.Identity?.Name ?? "";
            var dto  = await _perspectives.SaveAsync(req, user, CanShare(), ct);
            return Json(dto);
        }
        catch (ArgumentException ax)
        {
            return BadRequest(new { error = ax.Message });
        }
        catch (UnauthorizedAccessException ux)
        {
            return StatusCode(403, new { error = ux.Message });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> SetDefaultPerspective(long id, CancellationToken ct)
    {
        var user = User.Identity?.Name ?? "";
        var ok   = await _perspectives.SetDefaultAsync(id, user, ct);
        return ok ? Json(new { ok = true }) : NotFound(new { error = "Perspective not found." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> DeletePerspective(long id, CancellationToken ct)
    {
        var user        = User.Identity?.Name ?? "";
        var isSiteAdmin = User.IsInRole(UserRoles.SiteAdmin);
        var ok          = await _perspectives.DeleteAsync(id, user, isSiteAdmin, ct);
        return ok ? Json(new { ok = true }) : NotFound(new { error = "Perspective not found or not yours." });
    }

    /// <summary>Manager / SiteAdmin gate for saving with scope='shared'.</summary>
    private bool CanShare() =>
        User.IsInRole(UserRoles.Manager) || User.IsInRole(UserRoles.SiteAdmin);
}
