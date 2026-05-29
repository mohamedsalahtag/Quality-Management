using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

[Authorize]
public class QualityOrdersController : Controller
{
    private readonly IQualityOrderService _qos;
    private readonly IArrivalService _arrivals;
    private readonly IImageService _images;
    private readonly IMaraService _mara;
    private readonly ICatalogCache _cat;
    private readonly ISettingsService _settings;
    private readonly ILogger<QualityOrdersController> _log;

    public QualityOrdersController(IQualityOrderService qos, IArrivalService arrivals,
        IImageService images, IMaraService mara, ICatalogCache cat,
        ISettingsService settings, ILogger<QualityOrdersController> log)
    {
        _qos = qos; _arrivals = arrivals; _images = images; _mara = mara;
        _cat = cat; _settings = settings; _log = log;
    }

    public async Task<IActionResult> Index(string? status, string? search)
    {
        var rows = await _qos.ListAsync(string.IsNullOrEmpty(status) ? null : status, search);
        ViewBag.Status = status;
        ViewBag.Search = search;
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> CreateForArrival(long arrivalId)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        try
        {
            var qoId = await _qos.CreateForArrivalAsync(arrivalId, user);
            TempData["Success"] = "Quality Order created.";
            return RedirectToAction(nameof(Details), new { id = qoId });
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction("Details", "Arrivals", new { id = arrivalId });
        }
    }

    public async Task<IActionResult> Details(long id)
    {
        var qo = await _qos.GetAsync(id);
        if (qo == null) return NotFound();
        // Round-trips for Details: QO, Arrival, Shipment, Materials, Samples,
        // MARA lookup, photo counts (1 GROUP BY) = 7 round trips total --
        // independent of sample / material count. The sample drawer form is
        // loaded lazily over AJAX (see SamplePanel below).
        var arrival   = await _arrivals.GetAsync(qo.ArrivalId);
        var shipment  = await _arrivals.GetShipmentAsync(qo.ArrivalId);
        var materials = (await _qos.GetMaterialsAsync(id)).ToList();
        var samples   = await _qos.ListSamplesAsync(id);

        var mara = await _mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (mara.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);

        var photoCounts = await _images.CountByOwnersAsync(
            "QualityOrderMaterial", materials.Select(m => m.QoMaterialId));

        var editable = qo.StatusCode == "Open";

        var mailTemplate = await _settings.GetQoMailTemplateAsync();

        ViewBag.Arrival             = arrival;
        ViewBag.Shipment            = shipment;
        ViewBag.Materials           = materials;
        ViewBag.Samples             = samples;
        ViewBag.PhotoCounts         = photoCounts;
        ViewBag.Editable            = editable;
        ViewBag.SendMailEnabled     = mailTemplate.Enabled;
        return View(qo);
    }

    /// <summary>
    /// Returns the <c>_SampleForm</c> partial HTML for one sample (edit) or
    /// for a brand-new sample on a given material (create). Loaded over
    /// AJAX when the user opens the side drawer, so the main Details page
    /// doesn't have to pre-render dozens of hidden forms.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SamplePanel(long? sampleId, long? qoId, long? qoMaterialId, bool isNew = false)
    {
        QualityOrder? qo;
        Sample sample;
        QualityOrderMaterial? mat;
        IReadOnlyList<SampleReading> readings = Array.Empty<SampleReading>();
        IReadOnlyList<SampleDefect>  defects  = Array.Empty<SampleDefect>();

        if (!isNew && sampleId.HasValue && sampleId.Value > 0)
        {
            sample = (await _qos.GetSampleAsync(sampleId.Value)) ?? throw new InvalidOperationException("Sample not found.");
            qo = await _qos.GetAsync(sample.QualityOrderId);
            if (qo == null) return NotFound();
            var mats = await _qos.GetMaterialsAsync(qo.QualityOrderId);
            mat = mats.FirstOrDefault(m => m.QoMaterialId == sample.QoMaterialId);
            readings = await _qos.GetReadingsAsync(sample.SampleId);
            defects  = await _qos.GetDefectsAsync(sample.SampleId);
        }
        else if (isNew && qoId.HasValue && qoMaterialId.HasValue)
        {
            qo = await _qos.GetAsync(qoId.Value);
            if (qo == null) return NotFound();
            var mats = await _qos.GetMaterialsAsync(qo.QualityOrderId);
            mat = mats.FirstOrDefault(m => m.QoMaterialId == qoMaterialId.Value);
            if (mat == null) return NotFound();
            sample = new Sample
            {
                SampleId       = 0,
                QualityOrderId = qo.QualityOrderId,
                QoMaterialId   = qoMaterialId.Value,
                SampleScope    = "OneCarton"
            };
        }
        else
        {
            return BadRequest("Provide either sampleId or qoId+qoMaterialId+isNew=true.");
        }

        // Enrich material with MARA for the form header subtitle.
        if (mat != null)
        {
            var maraDict = await _mara.LookupAsync(new[] { mat.MaterialNo });
            if (maraDict.TryGetValue(mat.MaterialNo, out var mm)) mat.ApplyMara(mm);
        }

        var vm = new SampleFormVm
        {
            Qo               = qo,
            Sample           = sample,
            QoMaterial       = mat,
            // Both readings and defects are scoped to the material's group
            // so the operator only sees parameters defined for that product.
            // Major / Minor split for defects comes from each defect's own
            // category column.
            ReadingTypes     = await _cat.GetActiveReadingTypesForGroupAsync(mat?.MaterialGroup),
            Defects          = await _cat.GetActiveDefectsForGroupAsync(mat?.MaterialGroup),
            SectionMap       = new Dictionary<string,string>(),
            ExistingReadings = readings,
            ExistingDefects  = defects,
            // V20+: dynamic sample header fields. Only Sample-scoped fields are
            // entered per sample; Material-scoped fields live on the material
            // (Material details panel) and are inherited. The existing values
            // join the catalog so the form pre-fills the inputs in edit mode.
            HeaderFields     = (await _cat.GetActiveSampleHeaderFieldsAsync())
                                  .Where(f => f.Scope == "Sample").ToList(),
            ExistingHeader   = sample.SampleId > 0
                                  ? await _qos.GetSampleHeaderValuesAsync(sample.SampleId)
                                  : Array.Empty<SampleHeaderValue>(),
            Categories       = await _cat.GetActiveCategoriesAsync(),
            Editable         = qo.StatusCode == "Open"
        };
        return PartialView("_SampleForm", vm);
    }

    /// <summary>
    /// Returns the <c>_MaterialForm</c> partial for the per-material "Material
    /// details" panel: the Material-scoped header fields + the material's
    /// Sample Size, entered once and inherited by every sample. Loaded over
    /// AJAX when the user opens the material's modal.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> MaterialPanel(long qoMaterialId)
    {
        // GetMaterialsAsync is keyed by QO, so resolve the parent QO first.
        var qoId = await _qos.GetQoIdForMaterialAsync(qoMaterialId);
        if (qoId == null) return NotFound();
        var qo = await _qos.GetAsync(qoId.Value);
        if (qo == null) return NotFound();
        var mats = await _qos.GetMaterialsAsync(qo.QualityOrderId);
        var mat = mats.FirstOrDefault(m => m.QoMaterialId == qoMaterialId);
        if (mat == null) return NotFound();

        var maraDict = await _mara.LookupAsync(new[] { mat.MaterialNo });
        if (maraDict.TryGetValue(mat.MaterialNo, out var mm)) mat.ApplyMara(mm);

        var vm = new MaterialFormVm
        {
            Qo           = qo,
            Material     = mat,
            HeaderFields = (await _cat.GetActiveSampleHeaderFieldsAsync())
                              .Where(f => f.Scope == "Material").ToList(),
            ExistingValues = await _qos.GetMaterialHeaderValuesAsync(qoMaterialId),
            SampleSize   = mat.SampleSize,
            Editable     = qo.StatusCode == "Open"
        };
        return PartialView("_MaterialForm", vm);
    }

    /// <summary>Saves the per-material Sample Size + Material-scoped header
    /// values, and propagates the size to every sample. Returns JSON for
    /// in-place DOM update of the material card.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveMaterialHeaderAjax(long qoMaterialId, short? sampleSize, IFormCollection form)
    {
        try
        {
            var qoId = await _qos.GetQoIdForMaterialAsync(qoMaterialId);
            if (qoId == null) return Json(new { ok = false, error = "Material not found." });
            var qo = await _qos.GetAsync(qoId.Value);
            if (qo == null || qo.StatusCode != QualityOrderStatus.Open)
                return Json(new { ok = false, error = "Quality Order is not editable." });

            var fields = (await _cat.GetActiveSampleHeaderFieldsAsync())
                            .Where(f => f.Scope == "Material").ToList();

            // Authoritative mandatory check (browser `required` is convenience).
            var missing = new List<string>();
            foreach (var hf in fields.Where(f => f.IsMandatory))
            {
                var v = form[$"header_{hf.FieldCode}"].ToString();
                bool ok = hf.ValueKind switch
                {
                    "Numeric" => decimal.TryParse(v, out _),
                    "Date"    => DateTime.TryParse(v, out _),
                    _         => !string.IsNullOrWhiteSpace(v)
                };
                if (!ok) missing.Add(hf.FieldName);
            }
            if (missing.Count > 0)
                return Json(new { ok = false, error = $"These mandatory fields have no value: {string.Join(", ", missing)}." });

            var values = new List<MaterialHeaderValue>();
            foreach (var hf in fields)
            {
                var raw = form[$"header_{hf.FieldCode}"].ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var hv = new MaterialHeaderValue { QoMaterialId = qoMaterialId, FieldId = hf.FieldId };
                switch (hf.ValueKind)
                {
                    case "Numeric": if (decimal.TryParse(raw, out var dec)) hv.NumericValue = dec; break;
                    case "Date":    if (DateTime.TryParse(raw, out var dt)) hv.DateValue = dt.Date; break;
                    default:        hv.TextValue = raw.Trim(); break;
                }
                if (hv.NumericValue.HasValue || hv.DateValue.HasValue || !string.IsNullOrEmpty(hv.TextValue))
                    values.Add(hv);
            }

            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            await _qos.SaveMaterialHeaderValuesAndSizeAsync(qoMaterialId, sampleSize, values, user);

            // Echo back a display summary so the card can update without reload.
            var summary = values
                .OrderBy(v => v.SortOrder)
                .Select(v => new { v.FieldName, value = v.TextValue ?? v.NumericValue?.ToString() ?? v.DateValue?.ToString("yyyy-MM-dd") })
                .ToList();
            return Json(new { ok = true, qoMaterialId, sampleSize, values = summary });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SaveMaterialHeaderAjax failed for QoMaterialId={Id}", qoMaterialId);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Open(long id)
        => await TransitionAsync(id, (qos, user, _) => qos.OpenAsync(id, user), null);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Close(long id, string? reason)
    {
        // Business rule: every material line on the QO must have at least one
        // sample recorded before the QO can be closed. Surface the missing
        // materials so the QC operator knows exactly what's left to record.
        var materials = await _qos.GetMaterialsAsync(id);
        var samples   = await _qos.ListSamplesAsync(id);
        var sampledMaterialIds = samples.Select(s => s.QoMaterialId).ToHashSet();
        var unsampled = materials.Where(m => !sampledMaterialIds.Contains(m.QoMaterialId)).ToList();
        if (unsampled.Count > 0)
        {
            TempData["Error"] = "Cannot close: the following material(s) have no samples — "
                + string.Join(", ", unsampled.Select(m => m.MaterialNo))
                + ". Add at least one sample per material before closing.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return await TransitionAsync(id, (qos, user, r) => qos.CloseAsync(id, user, r), reason);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Reopen(long id, string? reason)
        => await TransitionAsync(id, (qos, user, r) => qos.ReopenAsync(id, user, r), reason);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Cancel(long id, string? reason)
        => await TransitionAsync(id, (qos, user, r) => qos.CancelAsync(id, user, r), reason);

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveOverride(long qoMaterialId, long quality_order_id,
        string newSize, string? reason)
    {
        if (string.IsNullOrWhiteSpace(newSize))
        {
            TempData["Error"] = "Override size is required.";
            return RedirectToAction(nameof(Details), new { id = quality_order_id });
        }
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _qos.SaveOverrideAsync(qoMaterialId, newSize, reason, user);
        TempData["Success"] = "Material size override saved.";
        return RedirectToAction(nameof(Details), new { id = quality_order_id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> ClearOverride(long qoMaterialId, long qualityOrderId)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _qos.ClearOverrideAsync(qoMaterialId, user);
        TempData["Success"] = "Material size override cleared.";
        return RedirectToAction(nameof(Details), new { id = qualityOrderId });
    }

    // ---- Samples ----

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> CreateSample(long qualityOrderId, long qoMaterialId)
    {
        var qo = await _qos.GetAsync(qualityOrderId);
        if (qo == null) return NotFound();
        if (qo.StatusCode != QualityOrderStatus.Open)
        {
            TempData["Error"] = "Quality Order must be Open to add samples.";
            return RedirectToAction(nameof(Details), new { id = qualityOrderId });
        }
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var sampleId = await _qos.CreateSampleAsync(new Sample
        {
            QualityOrderId = qualityOrderId,
            QoMaterialId   = qoMaterialId,
            CreatedBy      = user,
            SampleScope    = "OneCarton"
        });
        // Return to the QO Details and auto-open the new sample's drawer.
        return RedirectToAction(nameof(Details), new { id = qualityOrderId, openSample = sampleId });
    }

    public async Task<IActionResult> Sample(long id)
    {
        var sample = await _qos.GetSampleAsync(id);
        if (sample == null) return NotFound();
        var qo = await _qos.GetAsync(sample.QualityOrderId);
        var materials = await _qos.GetMaterialsAsync(sample.QualityOrderId);
        var qoMaterial = materials.FirstOrDefault(m => m.QoMaterialId == sample.QoMaterialId);
        var readingTypes = await _qos.GetActiveReadingTypesAsync();
        var defects      = await _qos.GetActiveDefectsAsync();
        var sectionMap   = await _qos.GetDisplaySectionMapAsync(qoMaterial?.MaterialGroup, qoMaterial?.MajorCategory);
        var existingReadings = await _qos.GetReadingsAsync(id);
        var existingDefects  = await _qos.GetDefectsAsync(id);
        // V20+ dynamic sample header fields, for the standalone Sample page
        // (uses the same partial as the QO Details drawer). Only Sample-scoped
        // fields are entered per sample; Material-scoped values are inherited.
        var headerFields   = (await _cat.GetActiveSampleHeaderFieldsAsync())
                                .Where(f => f.Scope == "Sample").ToList();
        var existingHeader = await _qos.GetSampleHeaderValuesAsync(id);

        ViewBag.QualityOrder    = qo;
        ViewBag.QoMaterial      = qoMaterial;
        ViewBag.ReadingTypes    = readingTypes;
        ViewBag.Defects         = defects;
        ViewBag.SectionMap      = sectionMap;
        ViewBag.ExistingReadings = existingReadings;
        ViewBag.ExistingDefects  = existingDefects;
        ViewBag.HeaderFields     = headerFields;
        ViewBag.ExistingHeader   = existingHeader;
        ViewBag.Categories       = await _cat.GetActiveCategoriesAsync();
        return View(sample);
    }

    /// <summary>
    /// Shared save logic for both the AJAX and redirect-style endpoints.
    /// Returns the saved sample (with SampleId populated) and the up-to-date
    /// sample count for the parent material. Returns null + error if the
    /// QO is not editable or the references are missing.
    /// </summary>
    private async Task<(Sample? saved, bool isNew, int newSampleCount, string? error)>
        SaveSampleCoreAsync(Sample sample, IFormCollection form, string user)
    {
        // Resolve QO + material BEFORE any writes so we can validate mandatory
        // readings up-front. Otherwise a new sample row would be inserted and
        // then rejected, leaving an orphan with no readings.
        long resolvedQoId;
        long resolvedQoMatId;
        Sample? existing = null;
        if (sample.SampleId <= 0)
        {
            if (sample.QualityOrderId <= 0 || sample.QoMaterialId <= 0)
                return (null, false, 0, "Missing QO or material reference; sample not saved.");
            resolvedQoId    = sample.QualityOrderId;
            resolvedQoMatId = sample.QoMaterialId;
        }
        else
        {
            existing = await _qos.GetSampleAsync(sample.SampleId);
            if (existing == null) return (null, false, 0, "Sample not found.");
            resolvedQoId    = existing.QualityOrderId;
            resolvedQoMatId = existing.QoMaterialId;
        }

        var qoRecord = await _qos.GetAsync(resolvedQoId);
        if (qoRecord == null) return (null, false, 0, "Quality Order not found.");
        if (qoRecord.StatusCode != QualityOrderStatus.Open)
            return (null, false, 0, "Quality Order is not editable.");

        var materialsForSave = await _qos.GetMaterialsAsync(resolvedQoId);
        var matForReadings = materialsForSave.FirstOrDefault(mm => mm.QoMaterialId == resolvedQoMatId);
        var readingTypes = await _cat.GetActiveReadingTypesForGroupAsync(matForReadings?.MaterialGroup);

        // Enforce mandatory readings: every reading type flagged is_mandatory
        // must have a non-empty value. Sample is rejected before any DB write
        // so a failed save leaves no orphan rows behind. Browser-side
        // `required` attribute is a convenience -- this is the authoritative
        // check.
        var missingMandatory = new List<string>();
        foreach (var rt in readingTypes.Where(r => r.IsMandatory))
        {
            var num  = form[$"reading_{rt.ReadingTypeCode}_num"].ToString();
            var text = form[$"reading_{rt.ReadingTypeCode}_text"].ToString();
            bool hasNum  = decimal.TryParse(num, out _);
            bool hasText = !string.IsNullOrWhiteSpace(text);
            if (rt.ValueKind == "Numeric" && !hasNum)
                missingMandatory.Add(rt.ReadingName);
            else if (rt.ValueKind != "Numeric" && !hasText)
                missingMandatory.Add(rt.ReadingName);
        }
        // Same up-front check for the V20+ sample header fields -- but only
        // Sample-scoped ones; Material-scoped fields are entered on the material
        // panel, not here. Mandatory header inputs use `header_<CODE>` naming.
        var headerFields = await _cat.GetActiveSampleHeaderFieldsAsync();
        foreach (var hf in headerFields.Where(f => f.IsMandatory && f.Scope == "Sample"))
        {
            var v = form[$"header_{hf.FieldCode}"].ToString();
            bool hasVal = hf.ValueKind switch
            {
                "Numeric" => decimal.TryParse(v, out _),
                "Date"    => DateTime.TryParse(v, out _),
                _         => !string.IsNullOrWhiteSpace(v)
            };
            if (!hasVal) missingMandatory.Add(hf.FieldName);
        }
        if (missingMandatory.Count > 0)
            return (null, false, 0,
                $"Sample cannot be saved — these mandatory fields have no value: {string.Join(", ", missingMandatory)}.");

        bool isNew;
        if (existing == null)
        {
            sample.CreatedBy = user;
            sample.SampleId  = await _qos.CreateSampleAsync(sample);
            isNew = true;
        }
        else
        {
            sample.UpdatedBy      = user;
            sample.QualityOrderId = existing.QualityOrderId;
            sample.QoMaterialId   = existing.QoMaterialId;
            sample.SampleNo       = existing.SampleNo;
            sample.CreatedAt      = existing.CreatedAt;
            sample.CreatedBy      = existing.CreatedBy;
            await _qos.UpdateSampleAsync(sample);
            isNew = false;
        }

        var readings = new List<SampleReading>();
        int seq = 0;
        foreach (var rt in readingTypes)
        {
            var num  = form[$"reading_{rt.ReadingTypeCode}_num"].ToString();
            var text = form[$"reading_{rt.ReadingTypeCode}_text"].ToString();
            decimal? n = decimal.TryParse(num, out var dec) ? dec : null;
            if (n == null && string.IsNullOrWhiteSpace(text)) continue;
            readings.Add(new SampleReading
            {
                ReadingTypeCode = rt.ReadingTypeCode,
                NumericValue    = n,
                TextValue       = string.IsNullOrWhiteSpace(text) ? null : text,
                UnitCode        = rt.DefaultUnit,
                ReadingSequence = seq++
            });
        }
        await _qos.SaveReadingsAsync(sample.SampleId, readings, user);

        // Pull defects (inputs named defect_<id>_value/_pct). Catalog is scoped
        // to this sample's material group so we don't try to save defects that
        // don't belong here. Every catalog defect is stored with value 0 when
        // the operator left it blank -- downstream sums / averages assume
        // zero rather than NULL, per business rule.
        var defectsCatalog = await _cat.GetActiveDefectsForGroupAsync(matForReadings?.MaterialGroup);
        // Denominator is the MATERIAL's sample size (inherited by every sample);
        // the per-sample form no longer collects it.
        var sampleSize = matForReadings?.SampleSize ?? 0;
        var defects = new List<SampleDefect>();
        foreach (var dc in defectsCatalog)
        {
            var v = form[$"defect_{dc.DefectId}_value"].ToString();
            var p = form[$"defect_{dc.DefectId}_pct"].ToString();
            decimal value = decimal.TryParse(v, out var dv) ? dv : 0m;
            decimal pct;
            if (decimal.TryParse(p, out var dp)) pct = dp;
            else if (sampleSize > 0)             pct = Math.Round(value / sampleSize * 100m, 4);
            else                                 pct = 0m;
            defects.Add(new SampleDefect
            {
                DefectId         = dc.DefectId,
                DefectValue      = value,
                DefectPercentage = pct,
                SeverityCode     = dc.DefectCategory
            });
        }
        await _qos.SaveDefectsAsync(sample.SampleId, defects, user);

        // V20+: persist the dynamic sample header values. One row per
        // configured field that has a value; SaveSampleHeaderValuesAsync
        // does DELETE+INSERT atomically so blanking a previously-saved
        // field clears it from storage.
        var headerValues = new List<SampleHeaderValue>();
        foreach (var hf in headerFields.Where(f => f.Scope == "Sample"))
        {
            var raw = form[$"header_{hf.FieldCode}"].ToString();
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var hv = new SampleHeaderValue { SampleId = sample.SampleId, FieldId = hf.FieldId };
            switch (hf.ValueKind)
            {
                case "Numeric":
                    if (decimal.TryParse(raw, out var dec)) hv.NumericValue = dec;
                    break;
                case "Date":
                    if (DateTime.TryParse(raw, out var dt)) hv.DateValue = dt.Date;
                    break;
                default:
                    hv.TextValue = raw.Trim();
                    break;
            }
            if (hv.NumericValue.HasValue || hv.DateValue.HasValue || !string.IsNullOrEmpty(hv.TextValue))
                headerValues.Add(hv);
        }
        await _qos.SaveSampleHeaderValuesAsync(sample.SampleId, headerValues, user);

        // Count of active samples on the parent material -- used by the AJAX
        // response so the client can update the card-header badge in place.
        var siblings = await _qos.ListSamplesAsync(sample.QualityOrderId);
        var newCount = siblings.Count(s => s.QoMaterialId == sample.QoMaterialId);

        return (sample, isNew, newCount, null);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveSample(Sample sample, IFormCollection form)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (saved, _, _, error) = await SaveSampleCoreAsync(sample, form, user);
        if (saved == null)
        {
            TempData["Error"] = error ?? "Save failed.";
            if (sample.QualityOrderId > 0)
                return RedirectToAction(nameof(Details), new { id = sample.QualityOrderId });
            return RedirectToAction(nameof(Index));
        }
        TempData["Success"] = "Sample saved.";
        return RedirectToAction(nameof(Details), new { id = saved.QualityOrderId, openSample = saved.SampleId });
    }

    /// <summary>
    /// AJAX twin of <see cref="SaveSample"/>. Returns JSON with the saved
    /// sample's row HTML (server-rendered via <c>_SampleRow</c>) so the client
    /// can update the table in place without reloading the page.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveSampleAjax(Sample sample, IFormCollection form)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (saved, isNew, newCount, error) = await SaveSampleCoreAsync(sample, form, user);
        if (saved == null)
            return Json(new { ok = false, error });

        // Reload from DB so we have CreatedAt/CreatedBy/SampleNo populated.
        var fresh = await _qos.GetSampleAsync(saved.SampleId) ?? saved;
        var rowVm = new SampleRowVm { Sample = fresh, Editable = true };
        var rowHtml = await this.RenderPartialToStringAsync("_SampleRow", rowVm);
        return Json(new
        {
            ok            = true,
            isNew,
            qoMaterialId  = fresh.QoMaterialId,
            sampleId      = fresh.SampleId,
            sampleNo      = fresh.SampleNo,
            newSampleCount= newCount,
            rowHtml
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> DeleteSample(long sampleId)
    {
        _log.LogInformation("DeleteSample requested for SampleId={SampleId}", sampleId);
        try
        {
            var s = await _qos.GetSampleAsync(sampleId);
            if (s == null)
            {
                _log.LogWarning("DeleteSample: sample {SampleId} not found (may already be deleted)", sampleId);
                TempData["Error"] = "Sample not found (it may already have been deleted).";
                if (long.TryParse(Request.Form["qualityOrderId"], out var qoFromForm) && qoFromForm > 0)
                    return RedirectToAction(nameof(Details), new { id = qoFromForm });
                return RedirectToAction(nameof(Index));
            }
            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            await _qos.SoftDeleteSampleAsync(sampleId, user);
            TempData["Success"] = $"Sample #{s.SampleNo} deleted.";
            return RedirectToAction(nameof(Details), new { id = s.QualityOrderId });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeleteSample failed for SampleId={SampleId}", sampleId);
            TempData["Error"] = $"Delete failed: {ex.Message}";
            return Redirect(Request.Headers["Referer"].ToString() is { Length: > 0 } r ? r : Url.Action(nameof(Index))!);
        }
    }

    /// <summary>AJAX twin of DeleteSample. Returns JSON; client removes the row from the table and decrements the badge.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> DeleteSampleAjax(long sampleId)
    {
        try
        {
            var s = await _qos.GetSampleAsync(sampleId);
            if (s == null) return Json(new { ok = false, error = "Sample not found." });
            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            await _qos.SoftDeleteSampleAsync(sampleId, user);
            var siblings = await _qos.ListSamplesAsync(s.QualityOrderId);
            var newCount = siblings.Count(x => x.QoMaterialId == s.QoMaterialId);
            return Json(new
            {
                ok             = true,
                sampleId,
                qoMaterialId   = s.QoMaterialId,
                newSampleCount = newCount
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "DeleteSampleAjax failed for SampleId={SampleId}", sampleId);
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>AJAX twin of SaveOverride. Returns the new size + override state for in-place DOM update.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveOverrideAjax(long qoMaterialId, string newSize, string? reason)
    {
        if (string.IsNullOrWhiteSpace(newSize))
            return Json(new { ok = false, error = "Override size is required." });
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _qos.SaveOverrideAsync(qoMaterialId, newSize, reason, user);
        return Json(new
        {
            ok              = true,
            qoMaterialId,
            materialSize    = newSize,
            sizeOverridden  = true,
            overrideReason  = reason ?? ""
        });
    }

    /// <summary>AJAX twin of ClearOverride. Returns the restored size.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> ClearOverrideAjax(long qoMaterialId, long qualityOrderId)
    {
        _log.LogInformation("ClearOverrideAjax: qoMaterialId={Qm} qualityOrderId={Qo}", qoMaterialId, qualityOrderId);
        try
        {
            if (qoMaterialId <= 0 || qualityOrderId <= 0)
                return Json(new { ok = false, error = "Missing qoMaterialId or qualityOrderId." });
            var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
            await _qos.ClearOverrideAsync(qoMaterialId, user);
            var mats = await _qos.GetMaterialsAsync(qualityOrderId);
            var mat = mats.FirstOrDefault(m => m.QoMaterialId == qoMaterialId);
            // Apply MARA enrichment so the returned size matches what the
            // Details page would show after a reload (the QO snapshot field
            // can be terse / numeric, MARA carries the descriptive form).
            if (mat != null)
            {
                var maraDict = await _mara.LookupAsync(new[] { mat.MaterialNo });
                if (maraDict.TryGetValue(mat.MaterialNo, out var mm)) mat.ApplyMara(mm);
            }
            return Json(new
            {
                ok             = true,
                qoMaterialId,
                materialSize   = mat?.MaterialSize ?? "",
                sizeOverridden = false
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ClearOverrideAjax failed");
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private async Task<IActionResult> TransitionAsync(long id,
        Func<IQualityOrderService, string, string?, Task<(bool ok, string? error)>> op, string? reason)
    {
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (ok, error) = await op(_qos, user, reason);
        TempData[ok ? "Success" : "Error"] = ok ? "Status updated." : error;
        return RedirectToAction(nameof(Details), new { id });
    }
}
