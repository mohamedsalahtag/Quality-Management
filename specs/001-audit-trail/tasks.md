---

description: "Task list for the Audit Trail feature (001-audit-trail)"
---

# Tasks: Audit Trail

> **Note (2026-07-02):** Some tasks below are marked complete for work that was
> later removed or changed (per-record history panel T023–T028; the `Auditor`
> role). See the "Implementation deviations" note at the top of `spec.md` for the
> authoritative current state — SiteAdmin-only global audit, no per-record panel,
> append-only enforced, audit coverage extended to admin/config/user/image changes.

**Input**: Design documents from `/specs/001-audit-trail/`
**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/audit-endpoints.md](contracts/audit-endpoints.md), [quickstart.md](quickstart.md)
**Tests**: NOT requested in the spec — test tasks are omitted per the `/speckit-tasks` rules.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently. US1 is the MVP slice; US2 and US3 add value on top.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1, US2, US3). Setup, Foundational, and Polish phases have no story label.
- Every task has an exact file path.

## Path Conventions

This is the existing ASP.NET Core MVC monolith at `app/SharbatlyQMS.Web/`. Paths follow Constitution Principle I: `Controllers/`, `Services/`, `Models/`, `Views/<Controller>/`, `ViewModels/`. Schema lives in `app/db/V##__*.sql`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Get the new file, new dependency, and new database column in place before any code uses them.

- [X] T001 Write versioned SQL migration `app/db/V15__audit_trail.sql` containing: (a) idempotent `ALTER TABLE qms_audit_log ADD source_user_agent NVARCHAR(500) NULL`, (b) idempotent `CREATE INDEX IX_qms_audit_log_filter ON qms_audit_log(changed_at DESC) INCLUDE (entity_type, action_code, changed_by)`, (c) drop-and-recreate `CK_Users_Role` to include `'Auditor'` (mirror V12 / V13 pattern), (d) closing sanity `SELECT`. Use the exact body sketched in [data-model.md §4](data-model.md#4-v15-migration-sketch).
- [X] T002 [P] Add ClosedXML NuGet PackageReference to `app/SharbatlyQMS.Web/SharbatlyQMS.Web.csproj` (version 0.105.x; License: MIT). Insert in the existing `<ItemGroup>` block alongside `Dapper` / `QuestPDF`.
- [X] T003 Apply V15 to the development SQL Server instance: `cd app/SharbatlyQMS.Migrate && dotnet run -- apply "<conn>" "C:\QualityManagemet\app\db\V15__audit_trail.sql"`. Verify the sanity SELECT prints `OK` for all three rows.

**Checkpoint**: Schema in place, NuGet pulled. No code uses the new column or library yet.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Bring the new role / models / service / DI registration / pre-existing-bug fixes into the codebase. Until this phase is done, no audit calls can be added to controllers because the infrastructure they call doesn't exist yet.

**⚠️ CRITICAL**: No user story work (Phase 3+) can begin until this phase is complete.

### Constants and DTOs

- [X] T004 [P] Extend `app/SharbatlyQMS.Web/Models/User.cs`: add `public const string Auditor = "Auditor";` to `UserRoles`, append `Auditor` to `UserRoles.All`, add two new `AuthPolicies` constants `AuditViewer` and `AuditorOrAdmin` with XML docs matching the precedent set by `ClaimManagerOrAdmin`.
- [X] T005 [P] Create `app/SharbatlyQMS.Web/Models/AuditEntry.cs` containing the `AuditEntry` class, `AuditEntryListRow` class (with `DisplayLabel` and `DiffRows`), `DiffRow` record, `AuditFilter` class, and the two constant classes `EntityTypes` and `ActionCodes`. Match the shapes in [data-model.md §2](data-model.md#2-in-memory-domain-models-c).

### Audit infrastructure

- [X] T006 [P] Create `app/SharbatlyQMS.Web/Services/IAuditContext.cs` (interface) and `app/SharbatlyQMS.Web/Services/AuditContext.cs` (scoped class implementing it). Expose `string? RemoteIp { get; set; }` and `string? UserAgent { get; set; }` setters so the ActionFilter can populate, getters so `AuditService` can read.
- [X] T007 [P] Create `app/SharbatlyQMS.Web/Services/AuditContextActionFilter.cs` implementing `IAsyncActionFilter`. On `OnActionExecutionAsync`, read `context.HttpContext.Connection.RemoteIpAddress?.ToString()` into `IAuditContext.RemoteIp` and `context.HttpContext.Request.Headers["User-Agent"].ToString()` into `IAuditContext.UserAgent` (truncate UA to 500 chars to match the column size). Then await `next()`.
- [X] T008 Create `app/SharbatlyQMS.Web/Services/IAuditService.cs` with the interface signature exactly as in [data-model.md §2.6](data-model.md#26-new-service-contracts-signatures-only--implementation-lives-in-services): `WriteAsync(SqlConnection conn, SqlTransaction tx, string entityType, long entityId, string actionCode, object? oldValues, object? newValues, string actor)`, `GetForRecordAsync(string entityType, long entityId)`, `ListAsync(AuditFilter filter)` (signature only — body added in Phase 4), `ExportAsync(AuditFilter filter)` (signature only — body added in Phase 5).
- [X] T009 Create `app/SharbatlyQMS.Web/Services/AuditService.cs` implementing `IAuditService` partially: implement `WriteAsync` (serialize old/new with `System.Text.Json.JsonSerializer`, INSERT into `qms_audit_log` using the passed-in `conn` + `tx`, including `source_ip` and `source_user_agent` from the injected `IAuditContext`), implement `GetForRecordAsync` (Dapper query against `IX_qms_audit_log_entity`), and stub `ListAsync` / `ExportAsync` to `throw new NotImplementedException()` (Phase 4 / 5 fills them).

### DI registration

- [X] T010 Modify `app/SharbatlyQMS.Web/Program.cs`: (a) register `builder.Services.AddScoped<IAuditContext, AuditContext>()`, `builder.Services.AddScoped<IAuditService, AuditService>()`, and `builder.Services.AddScoped<AuditContextActionFilter>()` next to the existing `AddScoped` block; (b) wire the filter globally with `opt.Filters.AddService<AuditContextActionFilter>()` inside the existing `AddControllersWithViews(opt => ...)` block; (c) add two new authorisation policies in the `AddAuthorization` block: `AuditViewer` → `RequireRole(UserRoles.Manager, UserRoles.Auditor, UserRoles.SiteAdmin)` and `AuditorOrAdmin` → `RequireRole(UserRoles.Auditor, UserRoles.SiteAdmin)`.

### Pre-Implementation Hardening (per [plan.md §Pre-Implementation Hardening](plan.md#pre-implementation-hardening))

**PH-1 — wrap non-transactional mutating methods in `BeginTransaction`**:

- [X] T011 [P] Refactor `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::SaveOverrideAsync` (line 197): change to `using var c = Open(); await c.OpenAsync(); using var tx = c.BeginTransaction(); … tx.Commit();` — wrap both the UPDATE and the existing inline audit INSERT inside one tx.
- [X] T012 [P] Refactor `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::ClearOverrideAsync` (line 223): same pattern as T011.
- [X] T013 [P] Refactor `app/SharbatlyQMS.Web/Services/ArrivalService.cs::SaveChecklistAsync` (line 301): wrap its `ExecuteAsync` in a `BeginTransaction` block. Single-statement today, but the transaction enables the audit hook in Phase 3.
- [X] T014 [P] Refactor `app/SharbatlyQMS.Web/Services/ArrivalService.cs::SaveShipmentAsync` (line 344): same pattern as T013.
- [X] T015 [P] Refactor `app/SharbatlyQMS.Web/Services/ArrivalService.cs::CreateFromSapAsync` (line 154): wrap the multi-step shipment / arrival insert in a single `BeginTransaction` block so partial failures roll back cleanly and the future audit hook lands atomically.

**PH-2 — replace existing inline `INSERT INTO qms_audit_log` with `IAuditService.WriteAsync`**:

- [X] T016 Modify `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::SaveOverrideAsync` lines 211-220: replace the inline `INSERT INTO qms_audit_log` with `await _audit.WriteAsync(c, tx, EntityTypes.MaterialOverride, qoMaterialId, ActionCodes.Override, oldRow: null, newRow: new { newSize, reason = reason ?? "" }, user);`. Add `IAuditService _audit` to the constructor and DI-injection list. **Depends on T009 + T011.**
- [X] T017 Modify `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::ClearOverrideAsync` lines 240-243: replace the inline INSERT with `await _audit.WriteAsync(c, tx, EntityTypes.MaterialOverride, qoMaterialId, ActionCodes.OverrideCleared, oldRow: null, newRow: null, user);`. **Depends on T009 + T012.**

**Checkpoint**: Build is clean, all services compile against `IAuditService`, existing override actions go through the new write path, no dual write paths remain. User story phases can begin.

---

## Phase 3: User Story 1 — Quality Manager investigates the history of a single record (Priority: P1) 🎯 MVP

**Goal**: A Quality Manager opens any tracked record and sees its full audit history inline on the detail page, with old/new field values rendered as a git-diff (old red, new green).

**Independent Test**: Sign in as Manager, open a Quality Order in Initial status, Open → add sample → Close → Reopen. Confirm the QO detail page now shows the new Audit History panel with 4 entries in reverse-chronological order, each with actor + IP + user-agent + timestamp + diff. (Maps to spec acceptance scenarios 1.1 – 1.3.)

### Instrument existing mutating actions with `_audit.WriteAsync`

Each task below opens the named service method, captures the row's BEFORE state (a `SELECT … WHERE …` inside the existing transaction), performs the mutation, then calls `_audit.WriteAsync(c, tx, entityType, entityId, actionCode, oldRow, newRow, user)` before `tx.Commit()`. Skip the audit call if the diff is empty (no-op updates per FR-016).

- [X] T018 [P] [US1] Instrument `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::Transition` (private helper at line 158): inside the existing `tx`, capture the QO row before the UPDATE, then after, and write one audit entry with `entityType=EntityTypes.QualityOrder`, `entityId=qoId`, `actionCode` derived from `toStatus` (`Opened` / `Closed` / `Reopened` / `Cancelled`), `oldRow` = the pre-update QO snapshot, `newRow` = the post-update QO snapshot. Existing callers `OpenAsync` / `CloseAsync` / `ReopenAsync` / `CancelAsync` continue to call `Transition` unchanged.
- [X] T019 [P] [US1] Instrument `app/SharbatlyQMS.Web/Services/QualityOrderService.cs::CreateForArrivalAsync` (line 101): write an audit entry with `entityType=EntityTypes.QualityOrder`, `actionCode=ActionCodes.Created`, `oldRow=null`, `newRow` = the full new QO row. Capture inside the existing `tx`.
- [X] T020 [P] [US1] Instrument every Sample / SampleReading / SampleDefect mutation in `QualityOrderService` (search for `qms_sample`, `qms_sample_reading`, `qms_sample_defect` UPDATE / INSERT / DELETE sites): wrap the existing method body in a transaction if absent, capture before/after row state, call `_audit.WriteAsync` with the appropriate `EntityTypes.Sample` / `EntityTypes.SampleReading` / `EntityTypes.SampleDefect` + `Created` / `Updated` / `Deleted` action codes. Touch points: `_SampleForm.cshtml` AJAX save handler in `QualityOrdersController.SamplePanel` flow.
- [X] T021 [P] [US1] Instrument `app/SharbatlyQMS.Web/Services/ClaimService.cs` mutating methods (`MarkClaimRequestAsync`, `MarkPassedQcAsync`, `ApproveAsync`, `HoldAsync`, `AddNoteAsync`): inside each method's existing `tx`, capture the claim row's before-state, perform the mutation, then write an audit entry with `entityType=EntityTypes.Claim` (or `EntityTypes.ClaimNote` for `AddNoteAsync`) and the matching domain `actionCode` (`ClaimRequest` / `PassedQC` / `Approved` / `Hold` / `Created` respectively). Inject `IAuditService` into the constructor.
- [X] T022 [P] [US1] Instrument `app/SharbatlyQMS.Web/Services/ArrivalService.cs` mutating methods (`CreateFromSapAsync`, `SaveChecklistAsync`, `SaveShipmentAsync`, `CompleteAsync`, `ReopenForEditAsync`, `DeleteAsync`, and any arrival-item save/delete): same pattern — capture before/after, call `_audit.WriteAsync` with `EntityTypes.Arrival` / `EntityTypes.ArrivalItem` / `EntityTypes.ArrivalChecklist` + the action code. Inject `IAuditService`.

### AuditController shell + AJAX history endpoint

- [X] T023 [US1] Create `app/SharbatlyQMS.Web/Controllers/AuditController.cs` containing the class `[Authorize] public class AuditController : Controller` with constructor-injected `IAuditService _audit` and an action `[Authorize(Policy = AuthPolicies.AuditViewer)] public async Task<IActionResult> HistoryPanel(string entityType, long entityId)` that calls `_audit.GetForRecordAsync(entityType, entityId)`, parses each entry's old/new JSON into a `DiffRow[]` via the helper from T029, and returns `PartialView("_AuditHistoryRows", rows)`. Reject unknown `entityType` (not in `EntityTypes.All`) with 404.
- [X] T024 [US1] Create `app/SharbatlyQMS.Web/Views/Audit/_AuditHistory.cshtml` — the host-side partial that hosts a small loading skeleton, an `@Html.AntiForgeryToken()`, and an inline `<script>` that fetches `/Audit/HistoryPanel?entityType=…&entityId=…` and injects the response HTML into a `<div id="auditHistoryRows">`. Takes `(entityType, entityId)` from the host view via `ViewBag.AuditEntityType` + `ViewBag.AuditEntityId`.
- [X] T025 [US1] Create `app/SharbatlyQMS.Web/Views/Audit/_AuditHistoryRows.cshtml` — the AJAX-target partial that takes `IReadOnlyList<AuditEntryListRow>` and renders each entry: actor + role + timestamp header, IP / user-agent meta line, action badge, then for `Updated` rows a diff section: one row per `DiffRow` showing `field-name | <old value bg-danger-subtle text-decoration-line-through> | <new value bg-success-subtle>`. For `Created` rows show only the green side; for `Deleted` rows show only the red side (per FR-022). Long values get a "show more" toggle via Bootstrap collapse.
- [X] T026 [P] [US1] Modify `app/SharbatlyQMS.Web/Views/QualityOrders/Details.cshtml`: at the top compute `var canSeeAudit = role == UserRoles.Manager || role == UserRoles.Auditor || role == UserRoles.SiteAdmin;`. Just before the closing of the materials section, insert `@if (canSeeAudit) { ViewBag.AuditEntityType = "QualityOrder"; ViewBag.AuditEntityId = Model.QualityOrderId; @await Html.PartialAsync("~/Views/Audit/_AuditHistory.cshtml"); }`. Mirror the existing pattern for `_ClaimChatPanel.cshtml`.
- [X] T027 [P] [US1] Modify `app/SharbatlyQMS.Web/Views/Arrivals/Details.cshtml`: same pattern as T026 with `entityType = "Arrival"` and `entityId = Model.ArrivalId`.
- [X] T028 [P] [US1] Modify `app/SharbatlyQMS.Web/Views/QualityOrders/Details.cshtml` (one more edit) — when `ViewBag.IsClaimContext == true` (the existing flag set by `ClaimManagementController.Details`), inject a second `_AuditHistory` partial for the claim itself with `entityType = "Claim"` and `entityId = ViewBag.Claim?.ClaimId` so claim-context viewers see both QO audit AND claim audit. Gate by `canSeeAudit`.
- [X] T029 [US1] In `app/SharbatlyQMS.Web/Services/AuditService.cs`, add a private static helper `static IReadOnlyList<DiffRow> ParseDiffs(string? oldJson, string? newJson)` that deserialises each to `Dictionary<string, JsonElement>` (treating null as empty), iterates the union of keys, and emits one `DiffRow(fieldName, oldVal?.ToString(), newVal?.ToString())` per field where the two sides differ. Used by both `GetForRecordAsync` and `ListAsync` (Phase 4) to populate `AuditEntryListRow.DiffRows`.

**Checkpoint**: All 11 entity types produce audit entries when mutated; per-record history panel renders on QO / Arrival / Claim detail pages with git-diff coloured rows. **MVP complete** — feature can be demoed.

---

## Phase 4: User Story 2 — Quality Manager investigates team activity over a period (Priority: P2)

**Goal**: A Quality Manager opens a dedicated `/Audit` page, applies filter combinations (user / date / record-type / action-type), and pages through results.

**Independent Test**: From the navbar click "Audit Log" → apply each filter type individually and combined → confirm result narrowing + empty state + keyset pagination. (Maps to spec acceptance scenarios 2.1 – 2.4.)

- [X] T030 [US2] In `app/SharbatlyQMS.Web/Services/AuditService.cs`, implement `ListAsync(AuditFilter filter)` using Dapper: keyset-paginated `SELECT … FROM qms_audit_log WHERE …` with a `WHERE (changed_at, audit_id) < (@cursorTime, @cursorId)` cursor clause, `ORDER BY changed_at DESC, audit_id DESC`, `TOP @pageSize` (clamped to 100). Multi-value filters use `IN @users` style. Each returned row goes through the `ParseDiffs` helper from T029 to populate `AuditEntryListRow.DiffRows`.
- [X] T031 [US2] Create `app/SharbatlyQMS.Web/ViewModels/AuditListVm.cs` containing `AuditFilter Filter`, `IReadOnlyList<AuditEntryListRow> Rows`, `DateTime? NextCursorTime`, `long? NextCursorId`, `bool HasMore`. Used by `Views/Audit/Index.cshtml`.
- [X] T032 [US2] Add `Index(AuditFilter filter)` action to `app/SharbatlyQMS.Web/Controllers/AuditController.cs` gated by `[Authorize(Policy = AuthPolicies.AuditViewer)]`. Binds `AuditFilter` from query string, calls `_audit.ListAsync(filter)`, builds the `AuditListVm`, returns `View(vm)`. Compute the next-page cursor from the last row of the page (if any) and the `HasMore` flag (compare returned count to requested pageSize).
- [X] T033 [US2] Create `app/SharbatlyQMS.Web/Views/Audit/Index.cshtml` with: `@model AuditListVm`; top filter form with text input for user(s), two date pickers (`fromUtc` / `toUtc`), two button-group filters (record type chips from `EntityTypes.All`, action-type chips from `ActionCodes.CrudOnly`); results table with the diff renderer from T025 reused as `@await Html.PartialAsync("_AuditHistoryRows", Model.Rows)`; bottom "Load more" link that POST-GETs with the keyset cursor preserved. Mirror the chip-filter pattern from `Views/ClaimManagement/Index.cshtml`.
- [X] T034 [US2] Add a new nav `<li>` to `app/SharbatlyQMS.Web/Views/Shared/_Layout.cshtml` immediately after the existing "Claim Management" nav item. Gate by `@if (canSeeAudit) { ... }` where `canSeeAudit` is computed at the top of the layout next to `canSeeParameters` / `canSeeAdmin`. Label "Audit Log", icon `<i class="bi bi-shield-shaded">`, points to `asp-controller="Audit" asp-action="Index"`.

**Checkpoint**: Global audit log page works; filters narrow results correctly; keyset pagination preserves filter context; empty state renders cleanly. **US2 complete**.

---

## Phase 5: User Story 3 — Auditor exports the audit log for compliance review (Priority: P3)

**Goal**: A user with the new `Auditor` role logs in, picks a date range, clicks Export, gets a real `.xlsx`. Manager can VIEW but cannot Export; Auditor cannot perform operational mutations.

**Independent Test**: Promote a test user to `Auditor` via `/Admin/Users`, sign in, click Export with a date range that contains entries → `.xlsx` downloads with the right columns. Double-click to test FR-021. Try mutating endpoints as Auditor → 403. (Maps to spec acceptance scenarios 3.1 – 3.3.)

- [X] T035 [P] [US3] In `app/SharbatlyQMS.Web/Services/AuditService.cs`, implement `ExportAsync(AuditFilter filter)` as an `async IAsyncEnumerable<AuditEntry>` that streams matching rows in chunks of 1,000 via Dapper's `QueryUnbufferedAsync` (or a manual `OFFSET/FETCH` loop if Unbuffered isn't available in 2.1.72). Honours the same WHERE clauses as `ListAsync` but no `TOP` cap.
- [X] T036 [P] [US3] In `app/SharbatlyQMS.Web/Controllers/AuditController.cs`, add a `private static readonly ConcurrentDictionary<string, DateTime> _exportsInFlight = new();` field for the FR-021 single-in-flight tracking. Add a helper `bool TryAcquireExportSlot(string user)` that checks for an existing entry less than 5 minutes old and rejects if found, else inserts.
- [X] T037 [US3] In `app/SharbatlyQMS.Web/Controllers/AuditController.cs`, add `[Authorize(Policy = AuthPolicies.AuditorOrAdmin)] public async Task<IActionResult> Export(AuditFilter filter)` action. Validate `filter.FromUtc` and `filter.ToUtc` are both set (return 400 friendly banner otherwise). Call `TryAcquireExportSlot(User.FindFirst(ClaimTypes.Name).Value)`; if blocked, return a 200 re-render of the Index page with a "Your previous export is still running — please wait" alert (FR-021). Otherwise stream `_audit.ExportAsync(filter)` rows into a ClosedXML `XLWorkbook`, set headers and one row per entry per [contracts/audit-endpoints.md E-3](contracts/audit-endpoints.md#e-3-get-auditexportfilter--excel-export), `Content-Disposition: attachment; filename="qms-audit-{from-yyyymmdd}-to-{to-yyyymmdd}.xlsx"`, write to the response stream, and ensure the in-flight slot is released in a `finally` block. **Depends on T035 + T036.**
- [X] T038 [P] [US3] Modify `app/SharbatlyQMS.Web/Views/Shared/_Layout.cshtml` (Gap 1a): extend the hardcoded View-as dropdown role array at line 131 from `new[] { UserRoles.Viewer, UserRoles.Operator, UserRoles.Manager, UserRoles.ClaimManager }` to include `UserRoles.Auditor` so SiteAdmin can impersonate Auditor for testing. Also mirror the same edit in `view-as-pack/reference/_ViewAsDropdown.snippet.cshtml` so the pack stays in sync if anyone consumes it.
- [X] T039 [P] [US3] Modify `app/SharbatlyQMS.Web/Views/Admin/Users.cshtml` (Gap 1b): extend the `roleBadge` switch at lines 100-105 with an explicit case `UserRoles.Auditor => "info text-dark"` (or another colour distinct from the existing five) so Auditor users render with a recognisable badge instead of the default grey.

**Checkpoint**: Auditor role can be assigned, exports produce a valid `.xlsx`, double-click is refused, Auditor cannot perform operational mutations. **All three user stories complete**.

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Build verification, production rollout, acceptance walk-through, and decisions-log update.

- [X] T040 Run `dotnet build app/SharbatlyQMS.Web --nologo` from the repo root and confirm **zero warnings, zero errors** (Constitution §Development Workflow). Fix any warning that surfaces before moving on.
- [X] T041 Apply V15 to production: `cd app/SharbatlyQMS.Migrate && dotnet run -- apply "Server=192.168.3.10;Database=SharbatlyQMS;User ID=linkserver;Password=P@ssw0rd;TrustServerCertificate=True;Connect Timeout=15" "C:\QualityManagemet\app\db\V15__audit_trail.sql"`. Confirm the sanity SELECT prints `OK` for all three rows. (Production DB; ensure the dev application from T003 hasn't drifted before re-applying.)
- [X] T042 Redeploy the Windows Service via `& 'C:\QualityManagemet\deploy\Republish.ps1'` (silent — no UAC prompts post-Grant-ServiceRights). Confirm `Get-Service SharbatlyQMS` returns `Running` and `Invoke-WebRequest http://localhost:5244/Account/Login` returns HTTP 200.
- [X] T043 Walk through every step of [quickstart.md](quickstart.md) end-to-end against the deployed app: pre-flight, US1, US2, US3, security & negative tests, performance sanity. Each step must pass. Capture any failure as a follow-up task before declaring done.
- [X] T044 Prepend a dated decisions-log bullet to `PROJECT_STATE.md §8` summarising: V15 applied (new column + index + Auditor role), new `IAuditService` + `AuditController`, 11 entity types instrumented, PH-1 / PH-2 hardening completed (lists which methods got wrapped/deduped), Constitution v1.0.0 governance reconciliation (Path A — Dapper, no EF Core), and acknowledgement that this is the first feature delivered through the full `/speckit-specify` → `/speckit-clarify` → `/speckit-plan` → `/speckit-tasks` workflow.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — can start immediately.
- **Foundational (Phase 2)**: Depends on Setup completion. Blocks all user stories.
- **User Stories (Phase 3+)**: All depend on Foundational completion. Can then proceed in parallel (if staffed) or sequentially in priority order.
- **Polish (Phase N)**: Depends on all desired user stories being complete (or at least US1 for MVP polish).

### User Story Dependencies

- **US1 (P1)**: Can start after T017 (last Foundational task) completes. No dependency on US2 or US3.
- **US2 (P2)**: Can start after T017. Reuses `_AuditHistoryRows.cshtml` partial from US1 (T025) for its results table — soft dependency, US2 can develop with a placeholder render if US1 isn't yet finished.
- **US3 (P3)**: Can start after T017. Independent of US1 / US2 functionally; the role check + export path are standalone.

### Within Each Phase / Story

- **Foundational (Phase 2)**: T004 + T005 + T006 + T007 are pure-new-file [P]; T008 + T009 must run sequentially (interface before impl); T010 depends on T008 + T009 (registers them); T011-T015 are independent service methods [P] (no shared file); T016 depends on T009 + T011; T017 depends on T009 + T012.
- **US1 (Phase 3)**: T018-T022 are independent controllers / services [P]; T023 depends on T009; T024 needs no prior; T025 depends on T029; T026-T028 depend on T024; T029 is internal to AuditService.cs (same file as T009, so technically sequential, but trivial).
- **US2 (Phase 4)**: T030 (Service impl) before T032 (Controller action); T031 (ViewModel) [P]; T033 depends on T031 + T025 (partial reuse); T034 [P] independent.
- **US3 (Phase 5)**: T035 and T036 [P] (different files); T037 depends on T035 + T036; T038 + T039 are independent file edits [P].

### Parallel Opportunities

- Phase 2: all of T004 / T005 / T006 / T007 / T011-T015 can run concurrently (8 parallel slots if staffed).
- Phase 3: T018 / T019 / T020 / T021 / T022 hit different services and can run concurrently; T026 / T027 / T028 hit different view files.
- Phase 5: T038 + T039 are independent quick fixes.

---

## Parallel Example: User Story 1

```bash
# Once T017 (last Foundational task) is committed, US1 work can fan out:
Task: "T018 [US1] Instrument QualityOrderService.Transition with audit calls"
Task: "T019 [US1] Instrument QualityOrderService.CreateForArrivalAsync"
Task: "T020 [US1] Instrument Sample/Reading/Defect mutations"
Task: "T021 [US1] Instrument ClaimService mutating methods"
Task: "T022 [US1] Instrument ArrivalService mutating methods"
# Concurrently, on a separate file:
Task: "T024 [US1] Create Views/Audit/_AuditHistory.cshtml"
Task: "T026 [US1] Inject _AuditHistory into QualityOrders/Details.cshtml"
Task: "T027 [US1] Inject _AuditHistory into Arrivals/Details.cshtml"
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Complete Phase 1: Setup (T001 → T002 → T003).
2. Complete Phase 2: Foundational (T004 → … → T017, parallelisable as noted).
3. Complete Phase 3: User Story 1 (T018 → … → T029).
4. **STOP and VALIDATE**: Walk steps 1-1 to 1-3 of [quickstart.md](quickstart.md). The per-record audit history is the demo-able MVP.
5. Run T040 (build) + T042 (deploy) + the US1 subset of T043 (quickstart §1). Demo-ready.

### Incremental Delivery

1. MVP as above → demo / collect feedback.
2. Add US2 (T030 → T034) → walk quickstart §2 → demo.
3. Add US3 (T035 → T039) → walk quickstart §3 + §4 (security) → demo.
4. Polish: T041 (prod migration) + T043 (full quickstart) + T044 (PROJECT_STATE update).

### Parallel Team Strategy

If multiple developers are available after Foundational completes (T017):

- Developer A: US1 instrumentation block (T018-T022) — touches 3 services.
- Developer B: US1 UI block (T023-T029) — touches AuditController + Views.
- Developer C: US2 (T030-T034) — touches AuditService + new view + nav.
- Developer D: US3 (T035-T039) — touches AuditService.ExportAsync + Controller export + 2 small edits.

All four work streams can land independently and merge cleanly because the file ownership doesn't overlap (each developer modifies a different subset of services / views).

---

## Notes

- **No test tasks**: the spec did not request tests; per the `/speckit-tasks` rule, none were generated. Manual acceptance follows the [quickstart.md](quickstart.md) script.
- `[P]` tasks = different files, no incomplete dependencies.
- `[Story]` label maps each task to its user story for traceability.
- Each user story is independently completable and testable per its quickstart section.
- Commit after each task (or each logical group within Foundational) so the per-task atomicity matches the audit feature's own atomicity promise.
- Stop at each phase's checkpoint to validate the partial outcome — particularly after Phase 2 (audit infrastructure usable) and Phase 3 (MVP demo-ready).
