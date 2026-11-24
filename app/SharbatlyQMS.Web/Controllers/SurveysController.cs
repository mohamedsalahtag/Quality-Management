using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Survey management: the Survey tab of Claim Management, the survey page, and
/// the one request endpoint both the arrival and the quality order post to.
///
/// A survey is INTERNAL. Nothing here feeds the QC report, the supplier mail
/// or its attachment list -- see QualityReportData, which carries no survey
/// field at all.
///
/// Three separate rights, on purpose: opening the page (the Surveys screen),
/// asking for a survey (Surveys.Request -- inspectors hold it without the
/// page), and deciding one (Surveys.Decide). Every action below also checks
/// the caller's branches against the arrival's.
/// </summary>
[Authorize]
public class SurveysController : Controller
{
    /// <summary>Most rows one Excel export will carry.</summary>
    private const int MaxExportRows = 5000;

    private readonly ISurveyService _surveys;
    private readonly IArrivalService _arrivals;
    private readonly IQualityOrderService _qos;
    private readonly IMaraService _mara;
    private readonly IAuditService _audit;
    private readonly IUserPermissions _me;

    public SurveysController(ISurveyService surveys, IArrivalService arrivals, IQualityOrderService qos,
        IMaraService mara, IAuditService audit, IUserPermissions me)
    {
        _surveys = surveys; _arrivals = arrivals; _qos = qos; _mara = mara; _audit = audit; _me = me;
    }

    private string CurrentUser => User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

    // ---- List -------------------------------------------------------

    [RequireScreen(Screens.Surveys, Seed.ManagerOrClaimManagerOrAdmin, "Open Surveys")]
    public async Task<IActionResult> Index([FromQuery] SurveyListFilter filter)
    {
        var scope = User.GetPlantScope();
        var page  = await _surveys.ListAsync(filter, scope);
        ViewBag.Filter           = filter;
        ViewBag.FilterOptions    = await _surveys.GetFilterOptionsAsync(scope);
        ViewBag.Counts           = await _surveys.CountsAsync(scope);
        ViewBag.PlantScopeLocked = User.SinglePlantOrNull();
        ViewBag.TotalCount       = page.Total;
        ViewBag.PageNo           = page.Page;
        ViewBag.PageSize         = page.PageSize;
        return View(page.Rows);
    }

    [HttpGet]
    [RequirePermission(Perm.Surveys.Export, Seed.ManagerOrClaimManagerOrAdmin, "Export surveys to Excel", ReadOnly = true)]
    public async Task<IActionResult> Export([FromQuery] SurveyListFilter filter)
    {
        var rows = await _surveys.ListForExportAsync(filter, User.GetPlantScope(), MaxExportRows);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Surveys");
        var heads = new[]
        {
            "Survey number", "Status", "Requested from", "Arrival number", "QC order number", "Container",
            "BOL", "PO", "Supplier", "Products", "Country of origin", "Branch", "Receive date",
            "Requested by", "Requested at", "Reason", "Comments", "Decided by", "Decided at",
            "Decision comments", "QC order status", "Claim status", "Last updated"
        };
        for (var i = 0; i < heads.Length; i++) ws.Cell(1, i + 1).Value = heads[i];
        ws.Row(1).Style.Font.Bold = true;

        var r = 2;
        foreach (var s in rows)
        {
            var col = 1;
            void Put(string? v) => ws.Cell(r, col++).Value = v ?? "";
            Put(s.SurveyNo);
            Put(SurveyStatus.Label(s.StatusCode));
            Put(SurveySource.Label(s.SourceCode));
            Put(s.ArrivalNo);
            Put(s.QualityOrderNo);
            Put(s.ContainerNo);
            Put(s.BolNo);
            Put(s.Ebeln);
            Put(s.VendorName);
            Put((s.Products ?? "").Replace("\n", "; "));
            Put(s.Origins);
            Put(s.Plant);
            Put(s.ReceiveDate?.ToString("yyyy-MM-dd"));
            Put(s.RequestedBy);
            Put(LocalStamp(s.RequestedAt));
            Put(s.RequestReason);
            Put(s.RequestComments);
            Put(s.DecidedBy);
            Put(LocalStamp(s.DecidedAt));
            Put(s.DecisionComments);
            Put(s.QualityOrderId.HasValue ? QualityOrderStatus.DisplayName(s.QualityOrderStatus) : "Not created");
            Put(s.ClaimStatus == null ? "" : ClaimStatus.Label(s.ClaimStatus));
            Put(LocalStamp(s.UpdatedAt));
            r++;
        }
        ws.Columns().AdjustToContents(1, Math.Min(r, 200));
        foreach (var c in ws.ColumnsUsed()) if (c.Width > 60) c.Width = 60;
        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"surveys-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>requested_at and friends are UTC; Dapper hands them back with
    /// Kind=Unspecified, so the conversion has to be explicit.</summary>
    private static string LocalStamp(DateTime? utc) => utc.HasValue
        ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "";

    // ---- Details ----------------------------------------------------

    [RequireScreen(Screens.Surveys)]
    public async Task<IActionResult> Details(long id)
    {
        var survey = await _surveys.GetAsync(id);
        if (survey == null) return NotFound();
        if (!User.GetPlantScope().Allows(survey.Plant)) return Forbid();

        ViewBag.Shipment = await _arrivals.GetShipmentAsync(survey.ArrivalId);
        // The QC results are read from the live order every time the page
        // opens -- never copied -- so the survey always shows what the
        // inspection says now.
        ViewBag.ToleranceLines = survey.QualityOrderId.HasValue
            ? await OverToleranceLinesAsync(survey.QualityOrderId.Value)
            : (IReadOnlyList<SurveyToleranceLine>)Array.Empty<SurveyToleranceLine>();
        ViewBag.History = await _audit.GetForRecordAsync(EntityTypes.Survey, id);
        return View(survey);
    }

    /// <summary>The lines of an order's QC summary that are at or over their
    /// tolerance -- the same lines the QC report prints in red. Shown in the
    /// request dialog on the quality order page, so it needs only that page's
    /// own access.</summary>
    [HttpGet]
    [RequireScreen(Screens.QoDetails)]
    public async Task<IActionResult> OverTolerance(long qualityOrderId)
    {
        var plant = await _qos.GetPlantForQoAsync(qualityOrderId);
        if (!User.GetPlantScope().Allows(plant)) return Forbid();
        var lines = await OverToleranceLinesAsync(qualityOrderId);
        return Json(new
        {
            ok = true,
            lines = lines.Select(l => new
            {
                group = l.Group, item = l.Item, total = l.IsCategoryTotal,
                result = l.Result, tolerance = l.Tolerance, over = l.Over
            })
        });
    }

    private async Task<IReadOnlyList<SurveyToleranceLine>> OverToleranceLinesAsync(long qualityOrderId)
    {
        // Same steps as the claim page's summary: enrich from the material
        // master first, because the grouping key (variety, grade, weight)
        // comes from there.
        var materials = (await _qos.GetMaterialsAsync(qualityOrderId)).ToList();
        if (materials.Count == 0) return Array.Empty<SurveyToleranceLine>();
        var mara = await _mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (mara.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);
        var groups = await _qos.BuildGroupSummariesAsync(
            qualityOrderId, materials, await _qos.GetReportUnitsAsync());

        var lines = new List<SurveyToleranceLine>();
        foreach (var g in groups)
        {
            var name = string.Join(" · ", new[]
            {
                string.IsNullOrWhiteSpace(g.MaterialGroupDesc) ? g.MaterialGroup : g.MaterialGroupDesc,
                g.Variety, g.Grade, g.WeightText
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            foreach (var sec in g.DefectSections)
            {
                foreach (var row in sec.Rows.Where(x => x.ExceedsTolerance))
                    lines.Add(new SurveyToleranceLine(name, row.Name, false,
                        Math.Round(row.Percentage, 2, MidpointRounding.AwayFromZero), row.Tolerance!.Value));
                if (sec.ExceedsTolerance)
                    lines.Add(new SurveyToleranceLine(name, sec.CategoryName, true,
                        sec.TotalPct, sec.Tolerance!.Value));
            }
        }
        return lines;
    }

    // ---- Request ----------------------------------------------------

    /// <summary>
    /// Asks for a survey. Posted by the Joint Survey = Yes answer on the
    /// arrival (arrivalId) and by Potential Survey on the quality order
    /// (qualityOrderId); both end in the same service call. Answers JSON so
    /// the arrival form keeps whatever else was typed into it.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Request, Seed.OperatorOrAbove, "Request a survey")]
    public async Task<IActionResult> RequestSurvey(long? arrivalId, long? qualityOrderId,
        string? reason, string? comments)
    {
        string source;
        if (qualityOrderId.HasValue)
        {
            var qo = await _qos.GetAsync(qualityOrderId.Value);
            if (qo == null) return Json(new { ok = false, error = "Quality order not found." });
            arrivalId = qo.ArrivalId;
            source    = SurveySource.QualityOrder;
        }
        else if (arrivalId.HasValue)
        {
            source = SurveySource.Arrival;
        }
        else
        {
            return Json(new { ok = false, error = "No arrival or quality order was given." });
        }

        var arrival = await _arrivals.GetAsync(arrivalId.Value);
        if (arrival == null) return Json(new { ok = false, error = "Arrival not found." });
        if (!User.GetPlantScope().Allows(arrival.Plant)) return Forbid();

        var res = await _surveys.RequestAsync(arrivalId.Value, source, qualityOrderId, reason, comments, CurrentUser);
        if (!res.Ok) return Json(new { ok = false, error = res.Error });

        var canView = _me.CanView(Screens.Surveys);
        return Json(new
        {
            ok          = true,
            existed     = res.AlreadyExisted,
            surveyNo    = res.SurveyNo,
            status      = res.StatusCode,
            statusLabel = SurveyStatus.Label(res.StatusCode),
            // Only someone who may open the page is handed its address.
            url         = canView ? Url.Action(nameof(Details), new { id = res.SurveyId }) : null,
            message     = res.AlreadyExisted
                ? $"A survey was already requested for this container: {res.SurveyNo} ({SurveyStatus.Label(res.StatusCode)})."
                : $"Survey {res.SurveyNo} requested. It is now waiting for a decision."
        });
    }

    // ---- Decision ---------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Decide, Seed.ManagerOrClaimManagerOrAdmin, "Confirm / Pass a survey")]
    public Task<IActionResult> Confirm(long id, string? comments) => DecideAsync(id, true, comments);

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Decide, Seed.ManagerOrClaimManagerOrAdmin, "Confirm / Pass a survey")]
    public Task<IActionResult> Pass(long id, string? comments) => DecideAsync(id, false, comments);

    private async Task<IActionResult> DecideAsync(long id, bool confirm, string? comments)
    {
        var survey = await _surveys.GetAsync(id);
        if (survey == null) return NotFound();
        if (!User.GetPlantScope().Allows(survey.Plant)) return Forbid();

        var (ok, error) = await _surveys.DecideAsync(id, confirm, comments, CurrentUser);
        if (ok)
            TempData["Success"] = confirm
                ? $"Survey {survey.SurveyNo} confirmed."
                : $"Survey {survey.SurveyNo} passed. No survey is required for this container.";
        else
            TempData["Error"] = error;
        return RedirectToAction(nameof(Details), new { id });
    }
}
