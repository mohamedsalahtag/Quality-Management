using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
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
        var scoped = User.GetScopedPlant();
        var rows = await _qos.ListAsync(string.IsNullOrEmpty(status) ? null : status, search, scoped);
        ViewBag.Status = status;
        ViewBag.Search = search;
        ViewBag.PlantScopeLocked = scoped;
        return View(rows);
    }

    /// <summary>Forbid() when the user is plant-scoped and the QO belongs to a
    /// different plant; null when access is OK.</summary>
    private async Task<IActionResult?> EnsureCanReadQoAsync(long qualityOrderId)
    {
        var scoped = User.GetScopedPlant();
        if (scoped == null) return null;
        var plant = await _qos.GetPlantForQoAsync(qualityOrderId);
        return string.Equals(plant, scoped, StringComparison.OrdinalIgnoreCase)
            ? null
            : Forbid();
    }

    /// <summary>Forbid() when the user is plant-scoped and the arrival's plant
    /// (where this QO would attach) doesn't match. Used by CreateForArrival.</summary>
    private async Task<IActionResult?> EnsureCanReadArrivalAsync(long arrivalId)
    {
        var scoped = User.GetScopedPlant();
        if (scoped == null) return null;
        var plant = await _arrivals.GetPlantAsync(arrivalId);
        return string.Equals(plant, scoped, StringComparison.OrdinalIgnoreCase)
            ? null
            : Forbid();
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> CreateForArrival(long arrivalId)
    {
        if (await EnsureCanReadArrivalAsync(arrivalId) is { } block) return block;
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
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
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

        // V31 (2026-06-20): photos moved from per-material to per-sample.
        // Count per-sample photos so each sample row shows its own badge; the
        // per-material count is no longer rendered.
        var photoCounts = await _images.CountByOwnersAsync(
            "Sample", samples.Select(s => s.SampleId));

        // Per-sample header values (incl. Material-scoped values copied down)
        // so the sample-table rows show live Grower/Pallet/Lot/Date-code.
        var sampleHeaders = await _qos.GetSampleHeaderValuesBatchAsync(samples.Select(s => s.SampleId));
        // Some material groups store Grower/Date Code/Brix/Firmness as reading
        // types, not header fields — the preview row falls back to these.
        var sampleReadings = await _qos.GetReadingsBatchAsync(samples.Select(s => s.SampleId));

        // V31: material-header completeness map for the "Add sample" gate.
        // qoMaterialId -> true when every mandatory Material-scoped header
        // field has a value. Used to disable + pulse-animate the button.
        var headerComplete = await _qos.GetMaterialHeaderCompleteMapAsync(id);

        var editable = qo.StatusCode == QualityOrderStatus.Open;

        var mailTemplate = await _settings.GetQoMailTemplateAsync();

        ViewBag.Arrival             = arrival;
        ViewBag.Shipment            = shipment;
        ViewBag.Materials           = materials;
        ViewBag.Samples             = samples;
        ViewBag.PhotoCounts         = photoCounts;
        ViewBag.SampleHeaders       = sampleHeaders;
        ViewBag.SampleReadings      = sampleReadings;
        ViewBag.HeaderComplete      = headerComplete;
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
            var loaded = await _qos.GetSampleAsync(sampleId.Value);
            if (loaded == null) return NotFound();
            sample = loaded;
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

        // Plant-scope gate (was missing): a plant-scoped Operator must not be able
        // to read another plant's sample/material data by supplying an id.
        if (await EnsureCanReadQoAsync(qo.QualityOrderId) is { } scopeBlock) return scopeBlock;

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
            // Material-scoped fields are now editable inline in the sample form
            // (they update the material + every sample on save).
            MaterialHeaderFields = (await _cat.GetActiveSampleHeaderFieldsAsync())
                                  .Where(f => f.Scope == "Material").ToList(),
            MaterialHeaderValues = mat != null
                                  ? await _qos.GetMaterialHeaderValuesAsync(mat.QoMaterialId)
                                  : Array.Empty<MaterialHeaderValue>(),
            Categories       = await _cat.GetActiveCategoriesAsync(),
            Editable         = qo.StatusCode == QualityOrderStatus.Open
        };
        return PartialView("_SampleForm", vm);
    }

    /// <summary>
    /// V31 (2026-06-20): dedicated photos-only side panel for a sample. Lighter
    /// than SamplePanel -- no readings / defects / header / autoload -- so the
    /// operator can attach photos to an existing sample in two clicks without
    /// the sample-edit drawer loading.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> SamplePhotosPanel(long sampleId)
    {
        var sample = await _qos.GetSampleAsync(sampleId);
        if (sample == null) return NotFound();
        if (await EnsureCanReadQoAsync(sample.QualityOrderId) is { } block) return block;
        var qo = await _qos.GetAsync(sample.QualityOrderId);
        if (qo == null) return NotFound();
        ViewBag.Sample   = sample;
        ViewBag.Qo       = qo;
        ViewBag.Editable = qo.StatusCode == QualityOrderStatus.Open;
        return PartialView("SamplePhotosPanel");
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
        // Plant-scope gate (was missing).
        if (await EnsureCanReadQoAsync(qo.QualityOrderId) is { } scopeBlock) return scopeBlock;
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
            Editable     = qo.StatusCode == QualityOrderStatus.Open
        };
        return PartialView("_MaterialForm", vm);
    }

    /// <summary>Saves the per-material Sample Size + Material-scoped header
    /// values, and propagates the size to every sample. Returns JSON for
    /// in-place DOM update of the material card.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveMaterialHeaderAjax(long qoMaterialId, IFormCollection form)
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
            await _qos.SaveMaterialHeaderValuesAsync(qoMaterialId, values, user);

            // Echo back a display summary so the card can update without reload.
            var summary = values
                .OrderBy(v => v.SortOrder)
                .Select(v => new { v.FieldName, value = v.TextValue ?? v.NumericValue?.ToString() ?? v.DateValue?.ToString("yyyy-MM-dd") })
                .ToList();
            return Json(new { ok = true, qoMaterialId, values = summary });
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
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        return await TransitionAsync(id, (qos, user, _) => qos.OpenAsync(id, user), null);
    }

    // Shared precondition for Submit (operator) and Close/Finish (supervisor):
    // every material on the QO must have at least one sample recorded. Returns
    // the user-facing error message OR null when OK to proceed.
    private async Task<string?> CheckAllMaterialsSampledAsync(long id)
    {
        var materials = await _qos.GetMaterialsAsync(id);
        var samples   = await _qos.ListSamplesAsync(id);
        var sampledMaterialIds = samples.Select(s => s.QoMaterialId).ToHashSet();
        var unsampled = materials.Where(m => !sampledMaterialIds.Contains(m.QoMaterialId)).ToList();
        if (unsampled.Count == 0) return null;
        return "The following material(s) have no samples — "
            + string.Join(", ", unsampled.Select(m => m.MaterialNo))
            + ". Add at least one sample per material first.";
    }

    /// <summary>V31 (2026-06-20): operator marks data entry complete. Locks the
    /// QO until a Supervisor finishes or cancel-submits it.
    /// V38 (2026-07-08): the "all materials sampled" precondition became a
    /// bypassable warning — the Details page shows a styled popup listing the
    /// unsampled materials with a "Submit anyway" button that re-posts with
    /// <paramref name="bypassNoSamples"/> = true.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> Submit(long id, bool bypassNoSamples = false, string? reason = null)
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        if (!bypassNoSamples && await CheckAllMaterialsSampledAsync(id) is { } err)
        {
            TempData["BypassWarning"] = err;
            TempData["BypassAction"]  = "Submit";
            return RedirectToAction(nameof(Details), new { id });
        }
        // Bypassing the no-samples rule requires a written justification. Re-open
        // the warning modal (with the message + a nudge) instead of proceeding.
        if (bypassNoSamples && string.IsNullOrWhiteSpace(reason))
        {
            TempData["BypassWarning"] = await CheckAllMaterialsSampledAsync(id)
                ?? "Some materials on this order have no samples.";
            TempData["BypassAction"]  = "Submit";
            TempData["Error"] = "A reason is required to submit with materials that have no samples.";
            return RedirectToAction(nameof(Details), new { id });
        }
        // Only carry the reason when it's the bypass justification.
        return await TransitionAsync(id,
            (qos, user, r) => qos.SubmitAsync(id, user, bypassNoSamples, r),
            bypassNoSamples ? reason : null);
    }

    /// <summary>V31 (2026-06-20): Supervisor returns a Submitted QO to Open so
    /// the operator can fix mistakes. Reason optional but recorded.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> CancelSubmit(long id, string? reason)
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        return await TransitionAsync(id, (qos, user, r) => qos.CancelSubmitAsync(id, user, r), reason);
    }

    /// <summary>"Finish order" — V31 (2026-06-20): policy widened to
    /// SupervisorOrAbove; source status is now Submitted (was Open). The same
    /// "all materials sampled" precondition still applies as a safety net,
    /// though the Submit step should have enforced it earlier.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.SupervisorOrAbove)]
    public async Task<IActionResult> Close(long id, string? reason, bool bypassNoSamples = false)
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        // V38: bypassable, mirroring Submit — otherwise a QO submitted with the
        // bypass could never be finished by the supervisor.
        if (!bypassNoSamples && await CheckAllMaterialsSampledAsync(id) is { } err)
        {
            TempData["BypassWarning"] = err;
            TempData["BypassAction"]  = "Close";
            TempData["BypassReason"]  = reason;
            return RedirectToAction(nameof(Details), new { id });
        }
        // Finishing while bypassing the no-samples rule also requires a reason.
        if (bypassNoSamples && string.IsNullOrWhiteSpace(reason))
        {
            TempData["BypassWarning"] = await CheckAllMaterialsSampledAsync(id)
                ?? "Some materials on this order have no samples.";
            TempData["BypassAction"]  = "Close";
            TempData["Error"] = "A reason is required to finish with materials that have no samples.";
            return RedirectToAction(nameof(Details), new { id });
        }
        return await TransitionAsync(id, (qos, user, r) => qos.CloseAsync(id, user, r, bypassNoSamples), reason);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Reopen(long id, string? reason)
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        return await TransitionAsync(id, (qos, user, r) => qos.ReopenAsync(id, user, r), reason);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.ManagerOrAdmin)]
    public async Task<IActionResult> Cancel(long id, string? reason)
    {
        if (await EnsureCanReadQoAsync(id) is { } block) return block;
        return await TransitionAsync(id, (qos, user, r) => qos.CancelAsync(id, user, r), reason);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> SaveOverride(long qoMaterialId, long quality_order_id,
        string newSize, string? reason)
    {
        // V31 (2026-06-20): editable-gate restored. Override endpoints were
        // missing this check before; now they refuse on any non-Open status.
        if (await EnsureEditableRedirectAsync(quality_order_id) is { } block) return block;
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
        // V31: editable-gate restored (was missing).
        if (await EnsureEditableRedirectAsync(qualityOrderId) is { } block) return block;
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
        if (await EnsureCanReadQoAsync(qualityOrderId) is { } block) return block;
        if (await EnsureEditableRedirectAsync(qualityOrderId) is { } block2) return block2;
        // 2026-07-26: material header completeness is no longer a gate for adding
        // a sample — the operator maintains material details from inside the
        // sample form instead. (The previous V31 hard block was removed.)
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
        // Plant-scope gate (was missing) on the standalone Sample page.
        if (await EnsureCanReadQoAsync(sample.QualityOrderId) is { } scopeBlock) return scopeBlock;
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
        var materialFields = (await _cat.GetActiveSampleHeaderFieldsAsync())
                                .Where(f => f.Scope == "Material").ToList();
        var materialValues = await _qos.GetMaterialHeaderValuesAsync(sample.QoMaterialId);

        ViewBag.MaterialHeaderFields = materialFields;
        ViewBag.MaterialHeaderValues = materialValues;
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

        // 2026-06-20: V24 override-detection restored. The sample-size input on
        // the form is editable; blank input falls back to the material's
        // EffectiveSampleSize (numeric column when set, else parsed from MARA's
        // MaterialSize text). Any non-null posted value that differs from that
        // default is flagged as a per-sample override and protected from the
        // material-level propagation UPDATE.
        var materialDefaultSize = matForReadings?.EffectiveSampleSize;
        if (!sample.SampleSize.HasValue)
        {
            sample.SampleSize     = materialDefaultSize;
            sample.SizeOverridden = false;
        }
        else
        {
            sample.SizeOverridden = sample.SampleSize != materialDefaultSize;
        }

        // Reject a non-positive per-sample size (mirrors the material-level
        // override validation). A 0/negative size would otherwise be stored and
        // force the unvalidated client-percentage fallback below.
        if (sample.SampleSize.HasValue && sample.SampleSize.Value <= 0)
            return (null, false, 0, "Sample size must be a positive whole number.");

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

        // NET_WEIGHT is reserved + computed = GROSS - TARA. The form renders
        // it read-only and JS keeps it in sync; we recompute here as the
        // authoritative source so a tampered POST or stale page can't store
        // an inconsistent value. Skip NET when either side is missing.
        bool IsGrossReading(string c) =>
            string.Equals(c, "GROSS_WEIGHT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "GROSS",        StringComparison.OrdinalIgnoreCase);
        bool IsTaraReading(string c) =>
            string.Equals(c, "TARA",         StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "TARA_WEIGHT",  StringComparison.OrdinalIgnoreCase);
        bool IsNetReading(string c) =>
            string.Equals(c, "NET_WEIGHT",   StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "NET",          StringComparison.OrdinalIgnoreCase);
        decimal? grossPosted = null, taraPosted = null;
        foreach (var rt in readingTypes)
        {
            var numStr = form[$"reading_{rt.ReadingTypeCode}_num"].ToString();
            if (!decimal.TryParse(numStr, out var dval)) continue;
            if (IsGrossReading(rt.ReadingTypeCode)) grossPosted = dval;
            else if (IsTaraReading(rt.ReadingTypeCode)) taraPosted = dval;
        }
        decimal? netComputed = (grossPosted.HasValue && taraPosted.HasValue)
            ? decimal.Round(grossPosted.Value - taraPosted.Value, 2)
            : (decimal?)null;

        var readings = new List<SampleReading>();
        int seq = 0;
        foreach (var rt in readingTypes)
        {
            var num  = form[$"reading_{rt.ReadingTypeCode}_num"].ToString();
            var text = form[$"reading_{rt.ReadingTypeCode}_text"].ToString();
            decimal? n = decimal.TryParse(num, out var dec) ? dec : null;
            // Server-side NET_WEIGHT recomputation: ignore the posted value
            // and use Gross - Tara. If either side is missing, drop the
            // reading entirely so blanks don't masquerade as zero.
            if (IsNetReading(rt.ReadingTypeCode))
            {
                if (!netComputed.HasValue) continue;
                n = netComputed;
            }
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
        // Denominator is the SAMPLE's own size (V24-aware) falling back to
        // the material's default. The SUM of defect values is capped at the
        // sample size (you can't find more defective items than you inspected);
        // each value is clamped to whatever room is left after the defects
        // already processed in catalog order -- this mirrors the client-side
        // running-total clamp + banner.
        var sampleSize = sample.SampleSize ?? matForReadings?.SampleSize ?? 0;
        var defects = new List<SampleDefect>();
        decimal runningTotal = 0m;
        foreach (var dc in defectsCatalog)
        {
            var v = form[$"defect_{dc.DefectId}_value"].ToString();
            var p = form[$"defect_{dc.DefectId}_pct"].ToString();
            decimal value = decimal.TryParse(v, out var dv) ? dv : 0m;
            if (value < 0m) value = 0m;
            if (sampleSize > 0)
            {
                var remaining = sampleSize - runningTotal;
                if (remaining < 0m) remaining = 0m;
                if (value > remaining) value = remaining;
                runningTotal += value;
            }
            decimal pct;
            if (sampleSize > 0)                  pct = Math.Round(value / sampleSize * 100m, 4);
            // No valid denominator: fall back to the posted percentage but clamp
            // it to [0,100] so a tampered/stray value can't skew the KPIs.
            else if (decimal.TryParse(p, out var dp)) pct = Math.Clamp(dp, 0m, 100m);
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

        // 2026-07-26: Material-scoped header values are now editable inline in the
        // sample form (mheader_<CODE> inputs). Saving a sample also saves the
        // material details and propagates them to every sample of this material,
        // so an operator can maintain material details from any sample and never
        // lose track of them. Only touch the material values when the form
        // actually carried them (guards any caller that doesn't render them).
        var materialFields = headerFields.Where(f => f.Scope == "Material").ToList();
        if (materialFields.Any(f => form.ContainsKey($"mheader_{f.FieldCode}")))
        {
            var materialValues = new List<MaterialHeaderValue>();
            foreach (var mf in materialFields)
            {
                var raw = form[$"mheader_{mf.FieldCode}"].ToString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var mv = new MaterialHeaderValue { QoMaterialId = resolvedQoMatId, FieldId = mf.FieldId };
                switch (mf.ValueKind)
                {
                    case "Numeric": if (decimal.TryParse(raw, out var mdec)) mv.NumericValue = mdec; break;
                    case "Date":    if (DateTime.TryParse(raw, out var mdt)) mv.DateValue = mdt.Date; break;
                    default:        mv.TextValue = raw.Trim(); break;
                }
                if (mv.NumericValue.HasValue || mv.DateValue.HasValue || !string.IsNullOrEmpty(mv.TextValue))
                    materialValues.Add(mv);
            }
            await _qos.SaveMaterialHeaderValuesAsync(resolvedQoMatId, materialValues, user);
        }

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
        if (sample.QualityOrderId > 0 && await EnsureCanReadQoAsync(sample.QualityOrderId) is { } block)
            return block;
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
        if (sample.QualityOrderId > 0 && await EnsureCanReadQoAsync(sample.QualityOrderId) is { } block)
            return block;
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        var (saved, isNew, newCount, error) = await SaveSampleCoreAsync(sample, form, user);
        if (saved == null)
            return Json(new { ok = false, error });

        // Reload from DB so we have CreatedAt/CreatedBy/SampleNo populated.
        var fresh = await _qos.GetSampleAsync(saved.SampleId) ?? saved;
        var rowVm = new SampleRowVm
        {
            Sample = fresh,
            Editable = true,
            HeaderValues = await _qos.GetSampleHeaderValuesAsync(saved.SampleId),
            Readings     = await _qos.GetReadingsAsync(saved.SampleId)
        };
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
            if (s != null && await EnsureCanReadQoAsync(s.QualityOrderId) is { } block) return block;
            if (s == null)
            {
                _log.LogWarning("DeleteSample: sample {SampleId} not found (may already be deleted)", sampleId);
                TempData["Error"] = "Sample not found (it may already have been deleted).";
                if (long.TryParse(Request.Form["qualityOrderId"], out var qoFromForm) && qoFromForm > 0)
                    return RedirectToAction(nameof(Details), new { id = qoFromForm });
                return RedirectToAction(nameof(Index));
            }
            // V31 edit-lock: samples may only be deleted while the QO is Open.
            // Without this, inspection samples could be removed from an already
            // Submitted/Closed order (after the PDF was issued / a claim decided).
            if (await EnsureEditableRedirectAsync(s.QualityOrderId) is { } editBlock) return editBlock;
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
            if (await EnsureCanReadQoAsync(s.QualityOrderId) is { } _) return Forbid();
            // V31 edit-lock (see DeleteSample): Open-only.
            if (await EnsureEditableAjaxAsync(s.QualityOrderId) is { } editBlock) return editBlock;
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
        // V31 (2026-06-20): editable-gate restored. Resolve the parent QO from
        // the material, then refuse on any non-Open status.
        var qoIdNullable = await _qos.GetQoIdForMaterialAsync(qoMaterialId);
        if (qoIdNullable == null) return Json(new { ok = false, error = "Material not found." });
        if (await EnsureEditableAjaxAsync(qoIdNullable.Value) is { } block) return block;
        if (!short.TryParse(newSize, out var n) || n < 1)
            return Json(new { ok = false, error = "Sample size must be a positive whole number." });
        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        await _qos.SaveOverrideAsync(qoMaterialId, n.ToString(System.Globalization.CultureInfo.InvariantCulture), reason, user);
        return Json(new
        {
            ok              = true,
            qoMaterialId,
            materialSize    = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sizeOverridden  = true,
            overrideReason  = reason ?? ""
        });
    }

    /// <summary>AJAX twin of ClearOverride. Returns the restored size.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Policy = AuthPolicies.OperatorOrAbove)]
    public async Task<IActionResult> ClearOverrideAjax(long qoMaterialId, long qualityOrderId)
    {
        // V31 (2026-06-20): editable-gate restored.
        if (await EnsureEditableAjaxAsync(qualityOrderId) is { } block) return block;
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

    // V31 (2026-06-20): central editable-gate. Returns a Json error result if
    // the QO is not Open; null if OK. Use at the top of every AJAX mutator.
    private async Task<IActionResult?> EnsureEditableAjaxAsync(long qoId)
    {
        var qo = await _qos.GetAsync(qoId);
        if (qo == null) return Json(new { ok = false, error = "Quality Order not found." });
        if (qo.StatusCode != QualityOrderStatus.Open)
            return Json(new { ok = false, error = $"Quality Order is {QualityOrderStatus.DisplayName(qo.StatusCode)} — only Open orders are editable." });
        return null;
    }

    // Same guard, but for endpoints that redirect on error (non-AJAX form posts).
    private async Task<IActionResult?> EnsureEditableRedirectAsync(long qoId)
    {
        var qo = await _qos.GetAsync(qoId);
        if (qo == null) return NotFound();
        if (qo.StatusCode != QualityOrderStatus.Open)
        {
            TempData["Error"] = $"Quality Order is {QualityOrderStatus.DisplayName(qo.StatusCode)} — only Open orders are editable.";
            return RedirectToAction(nameof(Details), new { id = qoId });
        }
        return null;
    }
}
