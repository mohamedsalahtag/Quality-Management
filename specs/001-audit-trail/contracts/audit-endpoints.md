# HTTP Endpoint Contracts — Audit Trail

**Feature**: `001-audit-trail`
**Date**: 2026-05-20

Documents the public HTTP surface added by this feature. Every endpoint is hosted by
the new `AuditController` (lives in `app/SharbatlyQMS.Web/Controllers/AuditController.cs`),
uses cookie authentication, and is gated by the policies registered in `Program.cs`.

There are **no public-API contracts** in the REST sense — this is an internal ASP.NET
Core MVC app and the endpoints are Razor-rendered. The contract format below is the
HTTP request/response shape.

---

## E-1. `GET /Audit` — Global audit log page

**Auth**: `[Authorize(Policy = AuthPolicies.AuditViewer)]` — Manager, Auditor, or
SiteAdmin only. Viewer, Operator, ClaimManager → 403.

**Query parameters** (all optional, all sourced from the `AuditFilter` model):

| Key | Type | Multiplicity | Notes |
|---|---|---|---|
| `users` | `string` | 0..N | One or more usernames. Repeated key (e.g. `?users=alice&users=bob`). |
| `fromUtc` | `DateTime` (ISO 8601 UTC) | 0..1 | Inclusive lower bound. |
| `toUtc` | `DateTime` (ISO 8601 UTC) | 0..1 | Inclusive upper bound. |
| `entityTypes` | `string` | 0..N | Subset of `EntityTypes.All`. |
| `actionCodes` | `string` | 0..N | Subset of `ActionCodes.CrudOnly` (per FR-010 the chip group exposes Created/Updated/Deleted; domain action labels are still queryable when supplied explicitly). |
| `cursorTime` | `DateTime` (UTC) | 0..1 | Set by the "Next page" link from the previous render. |
| `cursorId` | `long` | 0..1 | Same — paired with `cursorTime`. |
| `pageSize` | `int` | 0..1 | Default 25, capped to 100 server-side. |

**Response**: `200 OK` rendering `Views/Audit/Index.cshtml`. The Razor view receives an
`AuditListVm` containing the filter (echoed back for form persistence), the page of
`AuditEntryListRow` items, and the next-page cursor (if there are more rows).

**Error cases**:
- Caller not authorised → `403 Forbidden` via the standard cookie-auth deny path
  (redirected to `/Account/AccessDenied`).
- `pageSize > 100` → silently clamped to 100; no error.
- `fromUtc > toUtc` → renders empty result with a friendly "Start date is after end
  date" warning (no exception).

---

## E-2. `GET /Audit/HistoryPanel?entityType={t}&entityId={id}` — Per-record AJAX partial

**Auth**: `[Authorize(Policy = AuthPolicies.AuditViewer)]`.

**Purpose**: lazy-loaded by the `_AuditHistory.cshtml` partial embedded in record
detail pages (Quality Orders, Claims, Arrivals). The host detail page renders a small
loading skeleton; an inline `<script>` calls this endpoint with the host record's
`(entityType, entityId)` after the page paints. Keeps the detail page's main render
fast even for records with hundreds of audit entries.

**Query parameters**:

| Key | Type | Required | Notes |
|---|---|---|---|
| `entityType` | `string` | yes | One of `EntityTypes.All`. Unknown values → 404. |
| `entityId` | `long` | yes | The host record's PK. |

**Response**: `200 OK` with `Content-Type: text/html` — the rendered
`_AuditHistoryRows.cshtml` partial (HTML fragment, no layout, no `<html>` wrapper) for
direct insertion into the host page's panel slot.

**Error cases**:
- Unknown `entityType` → `404 Not Found`.
- Caller not authorised → `403 Forbidden`.

---

## E-3. `GET /Audit/Export?{filter}` — Excel export

**Auth**: `[Authorize(Policy = AuthPolicies.AuditorOrAdmin)]` — Auditor or SiteAdmin
only. Manager → 403. (Note: Manager can VIEW per E-1 but cannot EXPORT — per the spec,
export is the Auditor's privilege.)

**Query parameters**: same as E-1, **except** `pageSize` / `cursorTime` / `cursorId`
are ignored. `fromUtc` and `toUtc` are **required** (FR-013).

**Response on success**:

```
HTTP/1.1 200 OK
Content-Type: application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
Content-Disposition: attachment; filename="qms-audit-{from-yyyymmdd}-to-{to-yyyymmdd}.xlsx"
```

Body: the streamed `.xlsx` file (one worksheet, header row + one data row per audit
entry). Columns (matching the global view per FR-012):

1. `audit_id`
2. `changed_at_utc` (formatted YYYY-MM-DD HH:mm:ss UTC)
3. `changed_at_local` (formatted YYYY-MM-DD HH:mm:ss host-local)
4. `changed_by`
5. `source_ip` (or `unknown`)
6. `source_user_agent` (or `unknown`)
7. `entity_type`
8. `entity_id`
9. `action_code`
10. `old_values_json`
11. `new_values_json`

**Response on missing required filter**:
- Empty / missing `fromUtc` or `toUtc` → `400 Bad Request` rendering a friendly error
  banner ("Start date and end date are required for export").

**Response when another export is in flight for the same user (FR-021)**:
- `429 Too Many Requests` (or a friendly `200` re-render of the export form with a
  prominent "Your previous export is still running — please wait" alert — choice of
  HTTP code TBD at implementation, see quickstart.md for the smoke test).

**Response when zero rows match (acceptance scenario 3.2)**:
- `200 OK` with a one-row file (header only). The Razor banner above the Export form
  shows a hint: "No audit entries match this range — your file contains only the
  header row."

**Error cases**:
- Caller not Auditor / SiteAdmin → `403 Forbidden`.
- Stream interrupted mid-write → no partial file committed (the response stream is
  abandoned by the framework; the client's browser shows a download failure).

---

## E-4. Audit-write — Internal (NOT an HTTP endpoint)

There is intentionally **no HTTP endpoint** to write audit entries directly. Per
FR-001 and Principle VI:
- Audit writes happen only as a side effect of an audited mutation (Quality Order
  open / close, Sample edit, Claim status change, …).
- The audit write goes through `IAuditService.WriteAsync(...)` inside the mutating
  service's transaction — never via a public surface.

This is enumerated here so that a future code reviewer or security auditor can confirm
"the audit log can only be appended to by the application itself, never by a request"
in one glance.

---

## E-5. Existing endpoints — instrumentation contract

The following existing controller actions get a one-line audit hook added per Phase 2's
task list (`/speckit-tasks`). The actions themselves are **not** modified — only an
audit call is added inside their existing transaction. The HTTP contract of each
existing action is **unchanged**.

| Controller / Action | Entity type | Action code(s) |
|---|---|---|
| `ArrivalsController.Create` | `Arrival` | `Created` |
| `ArrivalsController.Update` | `Arrival` | `Updated` |
| `ArrivalsController.Complete` | `Arrival` | `Updated` (status change) |
| `ArrivalsController.SaveItem` | `ArrivalItem` | `Created` / `Updated` |
| `ArrivalsController.DeleteItem` | `ArrivalItem` | `Deleted` |
| `ArrivalsController.SaveChecklist` | `ArrivalChecklist` | `Created` / `Updated` |
| `QualityOrdersController.CreateForArrival` | `QualityOrder` | `Created` |
| `QualityOrdersController.Open` | `QualityOrder` | `Opened` |
| `QualityOrdersController.Close` | `QualityOrder` | `Closed` |
| `QualityOrdersController.Reopen` | `QualityOrder` | `Reopened` |
| `QualityOrdersController.Cancel` | `QualityOrder` | `Cancelled` |
| `QualityOrdersController.SaveOverride` | `MaterialOverride` | `Override` |
| `QualityOrdersController.ClearOverride` | `MaterialOverride` | `OverrideCleared` |
| `QualityOrdersController.SaveSample` (TBD path) | `Sample` | `Created` / `Updated` |
| `QualityOrdersController.DeleteSample` (TBD path) | `Sample` | `Deleted` |
| `QualityOrdersController.SaveReading` (TBD path) | `SampleReading` | `Created` / `Updated` / `Deleted` |
| `QualityOrdersController.SaveDefect` (TBD path) | `SampleDefect` | `Created` / `Updated` / `Deleted` |
| `ClaimManagementController.MarkClaimRequest` | `Claim` | `ClaimRequest` |
| `ClaimManagementController.MarkPassedQc` | `Claim` | `PassedQC` |
| `ClaimManagementController.Approve` | `Claim` | `Approved` |
| `ClaimManagementController.Hold` | `Claim` | `Hold` |
| `ClaimManagementController.AddNote` | `ClaimNote` | `Created` |

(Some sample / reading / defect routes are marked TBD because the existing controller
actions live behind the `_SampleForm.cshtml` AJAX panel rather than as dedicated routes;
the exact handler names will be resolved during `/speckit-tasks`.)
