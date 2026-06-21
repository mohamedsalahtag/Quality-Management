// =====================================================================
// data-hub-pack — DataHubController.snippet.cs
//
// Reference controller skeleton with all eight analyzer endpoints +
// the two host-side actions (the HTML preview + an XLSX export of the
// flat row stream itself, separate from PivotExcel).
//
// To install: rename "DataHubController" / "YourHubAction" / "YourRow" /
// "YourFilter" / "vw_yourdomain_flat" to whatever you call them, then drop
// this into Controllers/. Adjust the policy name to match yours.
//
// The constructor takes everything via DI -- no static dependencies on
// the Sharbatly QMS code.
// =====================================================================

using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourApp.Models.Reports;
using YourApp.Services.Reports;

namespace YourApp.Controllers;

[Authorize]
public class DataHubController : Controller
{
    private readonly IPivotService       _pivot;
    private readonly IPerspectiveService _perspectives;
    private readonly ILogger<DataHubController> _log;

    public DataHubController(
        IPivotService pivot,
        IPerspectiveService perspectives,
        ILogger<DataHubController> log)
    {
        _pivot        = pivot;
        _perspectives = perspectives;
        _log          = log;
    }

    // ===================================================================
    // Host-side data-hub actions
    // ===================================================================

    /// <summary>
    /// HTML preview page. Filter form + a small flat-row preview + the
    /// _PerspectiveAnalyzer partial. Wire your filter shape via [FromQuery].
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "SupervisorOrAbove")]
    public IActionResult YourHubAction([FromQuery] ExampleFilter filter)
    {
        // ApplyDefaultDateWindow(filter); // optional: default-to-last-30-days
        ViewBag.Filter = filter;
        // Materialise the first N rows from your view for the preview table.
        // Or pass an empty model -- the analyzer card below the preview
        // is the main attraction.
        return View(Array.Empty<object>());
    }

    /// <summary>
    /// Streams the full flat row set as XLSX. Use ClosedXML's streaming
    /// pattern (don't materialise everything in memory). See Sharbatly QMS
    /// FlatDefectsExcel for a worked example.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "SupervisorOrAbove")]
    public IActionResult YourHubExcel([FromQuery] ExampleFilter filter)
    {
        // Run your domain query, stream rows into an XLWorkbook.
        // The analyzer's own PivotExcel endpoint exports the *aggregated*
        // result; this action exports the flat *unaggregated* row stream.
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("YourHub");
        ws.Cell(1, 1).Value = "Replace with your domain export.";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"yourhub-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    // ===================================================================
    // Perspective Analyzer endpoints (8) -- copied from the canonical
    // ReportsController. Renames: ReportsController -> DataHubController.
    // ===================================================================

    [HttpGet]
    [Authorize(Policy = "SupervisorOrAbove")]
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
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> Pivot([FromBody] PivotRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { error = "Empty request" });
        try
        {
            var result = await _pivot.RunAsync(req, ct);
            return Json(result);
        }
        catch (ArgumentException ax) { return BadRequest(new { error = ax.Message }); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pivot failed. report={Report}", req.ReportKey);
            return StatusCode(500, new { error = "Pivot failed. See server log." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> PivotExcel([FromBody] PivotRequest req, CancellationToken ct)
    {
        if (req == null) return BadRequest(new { error = "Empty request" });
        PivotResult result;
        try { result = await _pivot.RunAsync(req, ct); }
        catch (ArgumentException ax) { return BadRequest(new { error = ax.Message }); }
        catch (Exception ex)
        {
            _log.LogError(ex, "PivotExcel run failed. report={Report}", req.ReportKey);
            return StatusCode(500, new { error = "Pivot failed. See server log." });
        }

        // BuildPivotWorkbook is the polished XLSX builder. The body lives in
        // the canonical ReportsController in the Sharbatly QMS repo --
        // copy that method here (it's ~150 lines).
        var bytes = BuildPivotWorkbook(result, req, User.Identity?.Name ?? "");
        var name  = $"pivot-{req.ReportKey ?? "report"}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            name);
    }

    [HttpGet]
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> PivotValues(string report, string dim,
        [FromQuery] ExampleFilter filter, bool ignorePageFilter = false,
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
        catch (ArgumentException ax) { return BadRequest(new { error = ax.Message }); }
        catch (Exception ex)
        {
            _log.LogError(ex, "PivotValues failed. report={Report} dim={Dim}", report, dim);
            return StatusCode(500, new { error = "PivotValues failed. See server log." });
        }
    }

    [HttpGet]
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> Perspectives(string report, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(report))
            return BadRequest(new { error = "report required" });
        var user = User.Identity?.Name ?? "";
        var list = await _perspectives.ListForUserAsync(report, user, ct);
        return Json(new { canShare = CanShare(), items = list });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "SupervisorOrAbove")]
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
        catch (ArgumentException ax) { return BadRequest(new { error = ax.Message }); }
        catch (UnauthorizedAccessException ux) { return StatusCode(403, new { error = ux.Message }); }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> SetDefaultPerspective(long id, CancellationToken ct)
    {
        var user = User.Identity?.Name ?? "";
        var ok   = await _perspectives.SetDefaultAsync(id, user, ct);
        return ok ? Json(new { ok = true })
                  : NotFound(new { error = "Perspective not found." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "SupervisorOrAbove")]
    public async Task<IActionResult> DeletePerspective(long id, CancellationToken ct)
    {
        var user        = User.Identity?.Name ?? "";
        var isSiteAdmin = User.IsInRole("SiteAdmin");
        var ok          = await _perspectives.DeleteAsync(id, user, isSiteAdmin, ct);
        return ok ? Json(new { ok = true })
                  : NotFound(new { error = "Perspective not found or not yours." });
    }

    // -- Share-rights helper --------------------------------------------
    private bool CanShare() =>
        User.IsInRole("Manager") || User.IsInRole("SiteAdmin");

    // ===================================================================
    // BuildPivotWorkbook -- ready to use. Only FilterSummary needs adapting
    // to your filter shape.
    // ===================================================================
    private static byte[] BuildPivotWorkbook(PivotResult r, PivotRequest req, string user)
    {
        int M = r.Measures.Length;
        int rowDimCount = r.RowDimensions.Length;
        int colKeyCount = r.ColKeys.Length;
        int dataCols    = Math.Max(colKeyCount, 1) * M + M;
        int totalCols   = rowDimCount + dataCols;
        bool multiM     = M >= 2;

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Pivot");

        // 1. Header strip ----------------------------------------------------
        var title = "Perspective Analyzer";
        if (r.RowDimensions.Length > 0 || r.ColDimensions.Length > 0)
        {
            var rd = string.Join(" / ", r.RowDimensions);
            var cd = string.Join(" / ", r.ColDimensions);
            title += " — " + (rd.Length > 0 ? rd : "(none)") + " × " + (cd.Length > 0 ? cd : "(none)");
        }
        ws.Cell(1, 1).Value = title;
        ws.Range(1, 1, 1, Math.Max(totalCols, 1)).Merge().Style.Font.SetBold().Font.SetFontSize(13);

        ws.Cell(2, 1).Value = $"Generated for {user} on {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        ws.Range(2, 1, 2, Math.Max(totalCols, 1)).Merge().Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);

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
        }

        int headerTopRow   = (req.Drills != null && req.Drills.Count > 0) ? 6 : 5;
        int headerInnerRow = multiM ? headerTopRow + 1 : headerTopRow;
        int firstDataRow   = headerInnerRow + 1;

        // 2. Header rows -----------------------------------------------------
        for (int i = 0; i < rowDimCount; i++)
        {
            ws.Cell(headerTopRow, i + 1).Value = r.RowDimensions[i];
            if (multiM) ws.Range(headerTopRow, i + 1, headerInnerRow, i + 1).Merge();
        }

        int dataColStart = rowDimCount + 1;
        if (colKeyCount == 0)
        {
            for (int m = 0; m < M; m++)
                ws.Cell(headerTopRow, dataColStart + m).Value = r.Measures[m].Label;
        }
        else
        {
            for (int ci = 0; ci < colKeyCount; ci++)
            {
                int colStart = dataColStart + ci * M;
                ws.Cell(headerTopRow, colStart).Value = string.Join(" / ", r.ColKeys[ci]);
                if (multiM) ws.Range(headerTopRow, colStart, headerTopRow, colStart + M - 1).Merge();
                if (multiM)
                    for (int m = 0; m < M; m++)
                        ws.Cell(headerInnerRow, colStart + m).Value = r.Measures[m].Label;
            }
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

        // 3. Body rows -------------------------------------------------------
        var cellMap = new Dictionary<(int, int), decimal?[]>(r.Cells.Length);
        foreach (var c in r.Cells) cellMap[(c.RowIndex, c.ColIndex)] = c.Values;
        int outRow = firstDataRow;
        for (int ri = 0; ri < r.RowKeys.Length; ri++)
        {
            for (int i = 0; i < rowDimCount; i++)
                ws.Cell(outRow, i + 1).Value = r.RowKeys[ri][i];
            if (colKeyCount == 0)
            {
                for (int m = 0; m < M; m++)
                    SetMeasureCell(ws.Cell(outRow, dataColStart + m), r.RowTotals[ri][m], r.Measures[m].Format);
            }
            else
            {
                for (int ci = 0; ci < colKeyCount; ci++)
                {
                    cellMap.TryGetValue((ri, ci), out var values);
                    for (int m = 0; m < M; m++)
                    {
                        decimal? v = values != null && m < values.Length ? values[m] : null;
                        SetMeasureCell(ws.Cell(outRow, dataColStart + ci * M + m), v, r.Measures[m].Format);
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

        // 4. Totals row ------------------------------------------------------
        if (colKeyCount > 0)
        {
            ws.Cell(outRow, 1).Value = "Total";
            for (int ci = 0; ci < colKeyCount; ci++)
                for (int m = 0; m < M; m++)
                    SetMeasureCell(ws.Cell(outRow, dataColStart + ci * M + m), r.ColTotals[ci][m], r.Measures[m].Format);
            int totalStart = dataColStart + colKeyCount * M;
            for (int m = 0; m < M; m++)
                SetMeasureCell(ws.Cell(outRow, totalStart + m), r.GrandTotals[m], r.Measures[m].Format);
            var trange = ws.Range(outRow, 1, outRow, totalCols);
            trange.Style.Font.SetBold();
            trange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E9ECEF");
            trange.Style.Border.TopBorder     = XLBorderStyleValues.Thin;
        }

        // 5. Polish ----------------------------------------------------------
        // No freeze panes -- Excel's split-bar rendering uglifies a short
        // report. AdjustToContents is bounded to the column-header row + data
        // rows so the merged 13-pt title doesn't bias widths. Min/max clamps
        // give short row-dim labels a comfortable column and stop one outlier
        // from blowing the layout.
        int lastUsedRow = ws.LastRowUsed()?.RowNumber() ?? firstDataRow;
        ws.Columns().AdjustToContents(headerTopRow, lastUsedRow);
        foreach (var col in ws.ColumnsUsed())
        {
            if (col.Width < 10) col.Width = 10;
            if (col.Width > 60) col.Width = 60;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetMeasureCell(IXLCell cell, decimal? v, string? format)
    {
        if (v == null) { cell.Value = ""; return; }
        cell.Value = (double)v.Value;
        // Number-format note: the previous "auto" format `#,##0.##` rendered
        // the integer 2 as "2." because Excel emits the literal `.` even when
        // no decimals follow. Per-cell branch picks the clean integer format
        // when the value has no fractional part.
        cell.Style.NumberFormat.Format = format switch
        {
            "int"     => "#,##0",
            "dec1"    => "#,##0.0",
            "dec2"    => "#,##0.00",
            "percent" => "0.00%",
            _         => v.Value == Math.Truncate(v.Value)
                            ? "#,##0"       // auto + integer
                            : "#,##0.##",   // auto + fraction
        };
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
    }

    // Adapt to your filter shape. Returns a "Slot=Value · Slot=Value" line
    // that goes into the XLSX subtitle row. Empty bits are skipped.
    private static string FilterSummary(object f)
    {
        if (f is not ExampleFilter ef) return "(custom filter)";
        var bits = new List<string>();
        if (ef.FromDate.HasValue) bits.Add("From " + ef.FromDate.Value.ToString("yyyy-MM-dd"));
        if (ef.ToDate.HasValue)   bits.Add("To "   + ef.ToDate.Value.ToString("yyyy-MM-dd"));
        if (!string.IsNullOrWhiteSpace(ef.Status))     bits.Add("Status="     + ef.Status);
        if (!string.IsNullOrWhiteSpace(ef.Plant))      bits.Add("Plant="      + ef.Plant);
        if (!string.IsNullOrWhiteSpace(ef.VendorName)) bits.Add("Vendor~"     + ef.VendorName);
        if (ef.PrimaryId.HasValue)                     bits.Add("PrimaryId="  + ef.PrimaryId);
        return bits.Count == 0 ? "(no filter)" : string.Join("  ·  ", bits);
    }
}
