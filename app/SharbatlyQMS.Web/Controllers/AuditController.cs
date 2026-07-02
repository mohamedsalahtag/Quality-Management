using System.Collections.Concurrent;
using System.Security.Claims;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Audit-trail UI: per-record history panel (Phase 3 / US1), global
/// filtered audit log (Phase 4 / US2), Excel export (Phase 5 / US3).
///
/// Class-level gate is plain [Authorize]; each action carries its own
/// per-action [Authorize(Policy = ...)] -- matches the AdminController
/// pattern documented in PROJECT_STATE.md §6.
/// </summary>
[Authorize]
public class AuditController : Controller
{
    private readonly IAuditService _audit;

    // T036 (US3) -- Single in-flight export per user (FR-021). Stale slots
    // older than 5 minutes are reclaimable automatically -- catches the case
    // where an export crashed without releasing.
    private static readonly ConcurrentDictionary<string, DateTime> _exportsInFlight = new();
    private static readonly TimeSpan ExportSlotMaxAge = TimeSpan.FromMinutes(5);

    // Soft cap (~spec.md) on rows per audit export to bound memory.
    private const int MaxAuditExportRows = 50_000;

    public AuditController(IAuditService audit)
    {
        _audit = audit;
    }

    private bool TryAcquireExportSlot(string user)
    {
        var now = DateTime.UtcNow;
        if (_exportsInFlight.TryGetValue(user, out var startedAt))
        {
            if (now - startedAt < ExportSlotMaxAge) return false;     // still running
            _exportsInFlight.TryRemove(user, out _);                  // stale; reclaim
        }
        return _exportsInFlight.TryAdd(user, now);
    }

    private void ReleaseExportSlot(string user) => _exportsInFlight.TryRemove(user, out _);

    /// <summary>
    /// Global audit log page with chip-style filters + keyset pagination.
    /// SiteAdmin-only as of 2026-05-21: cross-record forensic browsing is
    /// treated as admin work. Per-record audit panels on detail pages
    /// remain accessible to Manager + Auditor + SiteAdmin via the
    /// HistoryPanel endpoint below.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Index([FromQuery] AuditFilter filter)
    {
        filter ??= new AuditFilter();
        // Clamp the requested page size before passing to the service so the
        // view's pager links can echo back exactly the value that will be used.
        if (filter.PageSize <= 0) filter.PageSize = 25;
        if (filter.PageSize > 100) filter.PageSize = 100;

        var rows = await _audit.ListAsync(filter);

        // ListAsync requests pageSize+1 so the controller can compute HasMore
        // without a separate count query. Trim back to pageSize for display.
        var hasMore = rows.Count > filter.PageSize;
        var trimmed = hasMore ? rows.Take(filter.PageSize).ToList() : rows;
        var lastRow = trimmed.Count > 0 ? trimmed[^1] : null;

        var vm = new AuditListVm
        {
            Filter         = filter,
            Rows           = trimmed,
            HasMore        = hasMore,
            NextCursorTime = lastRow?.ChangedAt,
            NextCursorId   = lastRow?.AuditId
        };
        return View(vm);
    }

    /// <summary>
    /// T037 (US3) -- Excel export of the audit log. SiteAdmin-only as of
    /// 2026-05-21 (was AuditorOrAdmin). The date range is required (FR-013).
    /// Streams the .xlsx file directly to the response so memory stays
    /// bounded regardless of result size. Single in-flight export per user
    /// (FR-021).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public async Task<IActionResult> Export([FromQuery] AuditFilter filter, CancellationToken ct)
    {
        if (filter == null || !filter.FromUtc.HasValue || !filter.ToUtc.HasValue)
        {
            TempData["Error"] = "Start date and end date are required for export.";
            return RedirectToAction(nameof(Index));
        }

        var user = User.FindFirst(ClaimTypes.Name)?.Value ?? "system";
        if (!TryAcquireExportSlot(user))
        {
            TempData["Error"] = "Your previous export is still running — please wait.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Audit");

            // Header row -- matches contracts/audit-endpoints.md E-3, with
            // source_device_name added 2026-05-21.
            ws.Cell(1, 1).Value  = "audit_id";
            ws.Cell(1, 2).Value  = "changed_at_utc";
            ws.Cell(1, 3).Value  = "changed_at_local";
            ws.Cell(1, 4).Value  = "changed_by";
            ws.Cell(1, 5).Value  = "source_ip";
            ws.Cell(1, 6).Value  = "source_device_name";
            ws.Cell(1, 7).Value  = "source_user_agent";
            ws.Cell(1, 8).Value  = "entity_type";
            ws.Cell(1, 9).Value  = "entity_id";
            ws.Cell(1, 10).Value = "action_code";
            ws.Cell(1, 11).Value = "old_values_json";
            ws.Cell(1, 12).Value = "new_values_json";
            ws.Row(1).Style.Font.Bold = true;

            var rowIdx = 2;
            var exportTruncated = false;
            await foreach (var e in _audit.ExportAsync(filter, ct))
            {
                if (rowIdx - 2 >= MaxAuditExportRows) { exportTruncated = true; break; }
                ws.Cell(rowIdx, 1).Value  = e.AuditId;
                ws.Cell(rowIdx, 2).Value  = e.ChangedAt.ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(rowIdx, 3).Value  = e.ChangedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                ws.Cell(rowIdx, 4).Value  = e.ChangedBy;
                ws.Cell(rowIdx, 5).Value  = e.SourceIp ?? "unknown";
                ws.Cell(rowIdx, 6).Value  = e.SourceDeviceName ?? AuditService.DeviceLabel(e.SourceUserAgent);
                ws.Cell(rowIdx, 7).Value  = e.SourceUserAgent ?? "unknown";
                ws.Cell(rowIdx, 8).Value  = e.EntityType;
                ws.Cell(rowIdx, 9).Value  = e.EntityId;
                ws.Cell(rowIdx, 10).Value = e.ActionCode;
                // Excel's hard cell limit is 32,767 chars; a large record
                // snapshot (NVARCHAR(MAX)) would otherwise throw and abort the
                // whole export. Truncate for display only — the DB keeps the
                // verbatim value (FR-019 is a write-time guarantee).
                ws.Cell(rowIdx, 11).Value = TruncateForCell(e.OldValuesJson);
                ws.Cell(rowIdx, 12).Value = TruncateForCell(e.NewValuesJson);
                rowIdx++;
            }

            if (rowIdx == 2)
            {
                // E-1 edge case: empty range. Return a clear note rather than a
                // headers-only file that looks like an error.
                ws.Cell(2, 1).Value = "No audit entries in the selected date range.";
            }
            else if (exportTruncated)
            {
                ws.Cell(rowIdx, 1).Value =
                    $"NOTE: export truncated at {MaxAuditExportRows:N0} rows. Narrow the date range for the full result.";
                ws.Row(rowIdx).Style.Font.SetBold();
            }
            ws.Columns().AdjustToContents();

            // Stream to the response instead of ToArray() to avoid a second full
            // in-memory copy of the workbook.
            var ms = new MemoryStream();
            wb.SaveAs(ms);
            ms.Position = 0;

            var fileName = $"qms-audit-{filter.FromUtc:yyyyMMdd}-to-{filter.ToUtc:yyyyMMdd}.xlsx";
            return File(ms,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }
        finally
        {
            ReleaseExportSlot(user);
        }
    }

    // Excel cells cap at 32,767 characters. Cap below that and mark truncation.
    private const int ExcelCellMax = 32760;
    private static string TruncateForCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= ExcelCellMax ? value : value[..ExcelCellMax] + "…[truncated]";
    }
}
