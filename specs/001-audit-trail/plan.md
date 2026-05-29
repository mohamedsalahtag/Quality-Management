# Implementation Plan: Audit Trail

**Branch**: `001-audit-trail` | **Date**: 2026-05-20 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-audit-trail/spec.md`

## Summary

Add an end-to-end audit-trail feature covering every CREATE / UPDATE / DELETE on the
eleven tracked operational entities (Arrivals → Quality Orders → Samples / Defects →
Claims, plus Material Overrides). Each entry captures actor, action, record-type +
record-id, old-values JSON, new-values JSON, timestamp, IP address, and user-agent.
Quality Managers see a per-record history panel on existing detail pages plus a global
audit log page with filtering (user / date / record-type / action-type). A new
`Auditor` site role exports the log to `.xlsx` for a chosen date range.

**Technical approach (Path A — constitution-aligned):** extend the existing
`qms_audit_log` table in-place via a new V15 SQL migration (the table already has 9 of
the 10 fields we need; only `source_user_agent` is missing). Capture happens inside each
mutating service's existing `BeginTransaction()` block via a new `IAuditService.WriteAsync`
helper that handles JSON serialisation + INSERT. A scoped `IAuditContext` populated by an
ActionFilter carries the HTTP-request IP + user-agent into the call. An `AuditController`
+ `Views/Audit/Index.cshtml` deliver the global page; an `_AuditHistory.cshtml` partial
plugs into each existing detail view, gated by role. Excel export streams via
**ClosedXML** (new NuGet dependency).

> **Note on input reconciliation.** The `/speckit-plan` invocation requested EF Core +
> DbContext + ChangeTracker + EF migrations + Razor Pages. Those directives conflict
> with Constitution v1.0.0 Principle II (NON-NEGOTIABLE: *EF Core MUST NOT be
> introduced*) and Principle I (codebase is MVC, not Razor Pages). The conflict was
> surfaced to the user, who chose **Path A** — keep the constitution; reinterpret the
> directive in Dapper + V15 SQL migration + MVC terms. The functional intent (audit
> table, injected AuditService, no new projects, ClosedXML) is fully preserved.

## Technical Context

**Language/Version**: C# 12 / .NET 9

**Primary Dependencies**: Dapper 2.1.72, Microsoft.Data.SqlClient 7.0.1,
Microsoft.AspNetCore.Authentication.Cookies 2.3.9, System.DirectoryServices 9.0.0,
QuestPDF 2026.2.4, MailKit 4.16.0, SixLabors.ImageSharp 3.1.12, BCrypt.Net-Next 4.1.0,
Microsoft.Extensions.Hosting.WindowsServices 9.0.0. **New for this feature:** ClosedXML
0.105.x (Excel export).

**Storage**: SQL Server 2019+ (production database `SharbatlyQMS` at `192.168.3.10`).
The existing `qms_audit_log` table is extended in place; no new audit table is created.

**Testing**: Manual acceptance per the spec's acceptance scenarios + the
`quickstart.md` script. The codebase has no automated test framework registered today;
introducing one is out of scope for this feature.

**Target Platform**: Windows Server / Windows 11 Pro, hosted as a Windows Service
(`SharbatlyQMS` service, Kestrel on `0.0.0.0:5244`).

**Project Type**: Existing ASP.NET Core MVC monolith (`app/SharbatlyQMS.Web/`). No new
projects, no new top-level folders — only new files within `Controllers/`, `Services/`,
`Models/`, `Views/`, and `app/db/`.

**Performance Goals**:
- SC-008: 1 s p95 / 3 s p99 for one page (25 rows) of filtered audit list, sustained at
  10 M total entries.
- SC-005: Excel export for one calendar month (~10 K entries) completes in under 2 min.
- SC-001: 100 % of mutating actions on tracked entities produce an audit entry within
  the same transaction.

**Constraints**:
- Append-only: no UPDATE / DELETE on audit entries from any in-app surface (FR-006,
  Principle VI).
- Atomic with mutation: audit INSERT + mutation share a single transaction (FR-007).
- Verbatim values: no redaction / hashing of old/new (FR-019).
- One in-flight export per user (FR-021).
- Archive-friendly schema: no inbound FK references into `qms_audit_log`; sortable
  timestamp column kept (FR-020). The existing schema already satisfies this — no
  re-engineering required.
- View-as impersonation: the recorded actor is the real underlying user, not the
  impersonated role. `User.FindFirst(ClaimTypes.Name)?.Value` already returns the real
  user (the existing `ViewAsClaimsTransformer` swaps only the `Role` claim, not the
  `Name` claim), so this is naturally satisfied (FR-015).

**Scale/Scope**:
- ~10 M audit entries online before the optional future archival job kicks in.
- 11 tracked entity types instrumented in v1 (full FR-018 list).
- 6 site roles after this change (existing 5 + new `Auditor`).

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Evaluated against Constitution v1.0.0 (ratified 2026-05-20). **All six principles PASS
under Path A.**

| Principle | Status | How this plan satisfies it |
|-----------|--------|----------------------------|
| **I. Existing Architecture Is Authoritative** | ✅ PASS | New files land in existing folders: `Controllers/AuditController.cs`, `Services/IAuditService.cs` + `AuditService.cs`, `Models/AuditEntry.cs`, `Views/Audit/Index.cshtml` + `_AuditHistory.cshtml`. Existing files (`Program.cs`, `Models/User.cs`, `_Layout.cshtml`, detail views) get surgical edits — DI registration, new role constant, nav item, partial render. No new top-level folders, no new projects, no architectural layers. |
| **II. Dapper + Versioned SQL Migrations (NON-NEGOTIABLE)** | ✅ PASS | Data access stays on Dapper + `Microsoft.Data.SqlClient`. Schema delta ships as `app/db/V15__audit_trail.sql` (one migration: extend `qms_audit_log` with `source_user_agent`, add composite read-path index, extend `CK_Users_Role` to allow `Auditor`). EF Core is **not** introduced. |
| **III. Service-as-Repository — Interface + Impl Pairs** | ✅ PASS (with one deliberate convention extension) | `IAuditService` + `AuditService` follow the `IClaimService` / `ClaimService` template: injected via `AddScoped` in `Program.cs`; owns its SQL via Dapper; `Task<(bool ok, string? error)>` return shape on validation paths; no repository layer. **Deliberate extension:** `IAuditService.WriteAsync(SqlConnection conn, SqlTransaction tx, …)` accepts the caller's connection + transaction as parameters (no other service in the codebase has this shape today). This is **unavoidable** for FR-007 — audit + mutation must share a single transaction, and the alternatives (`TransactionScope` ambient enlistment, deferred buffered writes) are both worse for this Dapper codebase (see [research.md §R-5](research.md)). Flagged here so a reviewer recognises the new shape as intentional rather than a slip. |
| **IV. AD-First Cookie Auth + Policy-Based Authz** | ✅ PASS | No change to the cookie auth pipeline. Two new authorisation policies registered in `Program.cs`: `AuthPolicies.AuditViewer` (Manager + Auditor + SiteAdmin) and `AuthPolicies.AuditorOrAdmin` (Auditor + SiteAdmin). The `Auditor` role is added to `UserRoles.All` via `Models/User.cs` and to the DB CHECK constraint via the V15 migration. View-as impersonation works unmodified because the audit captures `ClaimTypes.Name` (the real user) rather than `Role`. |
| **V. SAP OData Is Read-Only** | ✅ PASS | This feature does not touch SAP. No SAP services are added, no SAP OData calls are made. |
| **VI. Surgical Changes + Append-Only Audit + Living Memory** | ✅ PASS | `qms_audit_log` is already the canonical append-only table per `PROJECT_STATE.md`. The feature reinforces (rather than violates) this rule: no code path UPDATE-s or DELETE-s an audit entry; the audit INSERT shares the same transaction as the originating mutation. `PROJECT_STATE.md §8` will be updated post-implementation with a dated decision-log entry. |

**Gate verdict: PASS.** Phase 0 may proceed.

## Project Structure

### Documentation (this feature)

```text
specs/001-audit-trail/
├── plan.md                        # This file (/speckit-plan output)
├── spec.md                        # Behavioural spec (already written)
├── research.md                    # Phase 0 output (this command)
├── data-model.md                  # Phase 1 output (this command)
├── quickstart.md                  # Phase 1 output (this command)
├── contracts/
│   └── audit-endpoints.md         # HTTP endpoint contracts
└── checklists/
    └── requirements.md            # Already written; spec quality gate
```

### Source Code (repository root)

```text
app/db/
└── V15__audit_trail.sql                   # NEW — extends qms_audit_log + CK_Users_Role

app/SharbatlyQMS.Web/
├── SharbatlyQMS.Web.csproj                # MODIFY — add ClosedXML PackageReference
├── Program.cs                             # MODIFY — register IAuditService + 2 policies + AuditContextActionFilter
├── Models/
│   ├── User.cs                            # MODIFY — add UserRoles.Auditor + AuthPolicies.AuditViewer + AuditorOrAdmin
│   └── AuditEntry.cs                      # NEW — AuditEntry, AuditFilter, ActionCode constants, role helpers
├── Services/
│   ├── IAuditService.cs                   # NEW — interface
│   ├── AuditService.cs                    # NEW — Dapper implementation
│   ├── IAuditContext.cs                   # NEW — per-request IP/UA carrier
│   ├── AuditContext.cs                    # NEW — scoped implementation populated by filter
│   └── AuditContextActionFilter.cs        # NEW — captures IP + UA from HttpContext into IAuditContext (lives in Services/ to avoid a new top-level folder)
├── Controllers/
│   ├── AuditController.cs                 # NEW — Index (global list), Export (.xlsx), HistoryPanel (partial fetch)
│   ├── QualityOrdersController.cs         # MODIFY — wrap Open/Close/Reopen/SaveOverride/etc. with audit calls
│   ├── ClaimManagementController.cs       # MODIFY — wrap MarkClaimRequest/Approve/Hold/AddNote with audit calls
│   ├── ArrivalsController.cs              # MODIFY — wrap arrival mutations with audit calls
│   └── AdminController.cs                 # MODIFY — wrap user-role-changes / settings if in scope (out-of-scope per FR-018)
├── Views/
│   ├── Audit/
│   │   ├── Index.cshtml                   # NEW — global audit log + filters
│   │   └── _AuditHistory.cshtml           # NEW — per-record audit panel partial (used by Detail views)
│   ├── QualityOrders/Details.cshtml       # MODIFY — render _AuditHistory partial if role allows
│   ├── ClaimManagement/Details.cshtml     # MODIFY — same (only if claim context — TBD)
│   ├── Arrivals/Details.cshtml            # MODIFY — same
│   ├── Admin/Users.cshtml                 # MODIFY — (a) role <select> auto-picks Auditor via UserRoles.All; (b) extend the `roleBadge` switch at line 98-104 with an explicit case `UserRoles.Auditor => "info text-dark"` (or similar) so Auditor users do not render with the default grey badge
│   └── Shared/_Layout.cshtml              # MODIFY — (a) add "Audit Log" nav link gated by `canSeeAudit` (Manager / Auditor / SiteAdmin); (b) extend the hardcoded View-as dropdown array at line 131 to include `UserRoles.Auditor` so SiteAdmin can impersonate Auditor for testing
└── ViewModels/
    └── AuditListVm.cshtml                 # NEW — Index view model (filters + paged rows)

PROJECT_STATE.md                            # MODIFY (post-impl) — §8 dated decision log entry
```

**Structure Decision**: This is a **single-project, in-place extension** of the existing
ASP.NET Core MVC monolith. No new projects, no new top-level folders. Every new file
lands in an existing folder (`app/db/`, `Controllers/`, `Services/`, `Models/`, `Views/`,
`ViewModels/`). The one new ActionFilter (`AuditContextActionFilter.cs`) lives under
`Services/` alongside its closely-coupled siblings `IAuditContext` / `AuditContext` —
this avoids introducing a brand-new `Filters/` directory and keeps Principle I's "extend
existing folders" rule satisfied without justification.

## Pre-Implementation Hardening

The post-design review (2026-05-20) identified two pieces of existing-code hardening
that MUST land **before or alongside** the audit-trail instrumentation, so that the
"audit + mutation share a transaction" guarantee (FR-007) actually holds. These are
small, mechanical changes; flagged here so `/speckit-tasks` picks them up as explicit
tasks rather than smuggling them into the audit-write tasks.

### PH-1. Wrap non-transactional mutating methods in `BeginTransaction()`

Several existing service methods on tracked entities perform their UPDATE / INSERT
**without** an explicit transaction — a pre-existing atomicity gap that the audit
feature would otherwise inherit. Each must be retrofitted with the standard
`using var tx = c.BeginTransaction(); … tx.Commit();` pattern so the audit INSERT
that follows lands in the same transaction.

Known offenders (verified by grepping for `BeginTransaction` against the public mutating
methods on each service):

| Service · Method | Current shape | Required change |
|---|---|---|
| `QualityOrderService.SaveOverrideAsync` (line 197) | Two sequential `c.ExecuteAsync` calls on one connection, no `BeginTransaction` | Wrap both in a transaction |
| `QualityOrderService.ClearOverrideAsync` (line 223) | Same | Wrap both in a transaction |
| `ArrivalService.SaveChecklistAsync` (line 301) | Single `ExecuteAsync`, no tx | Wrap in a transaction (still single-statement, but consistent + allows audit INSERT to share) |
| `ArrivalService.SaveShipmentAsync` (line 344) | Same | Same |
| `ArrivalService.CreateFromSapAsync` (line 154) | Several inserts without an explicit tx | Wrap in a transaction |

Other `ArrivalService` methods that already use `BeginTransaction` (line 168 `Save…`,
373 `Complete…`, 399 `Reopen…`, 423 `Delete…`) need no PH-1 fix. Same for `ClaimService`
(all mutating methods already use transactions) and the `QualityOrderService.Transition`
helper.

This hardening is **independently valuable** — it fixes pre-existing partial-failure
windows — so it is delivered as commits that ship before the audit hooks attach. Each
commit is a one-method change with a one-line transaction wrap; trivial to review.

### PH-2. Replace the two inline `INSERT INTO qms_audit_log` writes with `IAuditService.WriteAsync`

Two existing methods write to `qms_audit_log` directly:

| Service · Method | Existing inline write |
|---|---|
| `QualityOrderService.SaveOverrideAsync` lines 215-220 | `INSERT INTO qms_audit_log (entity_type, entity_id, action_code, new_values_json, changed_at, changed_by) VALUES ('QualityOrderMaterial', …, 'Override', @auditJson, …, @user)` |
| `QualityOrderService.ClearOverrideAsync` lines 240-243 | Same shape, `action_code='OverrideCleared'` |

After the audit feature lands there will be **two write paths to the same table** —
the new `IAuditService.WriteAsync(…)` and these legacy inline INSERTs. They will drift
(missing IP, missing user-agent, missing the OriginalRole guarantee, etc.) within months.

Required change: replace each inline INSERT with the new
`await _audit.WriteAsync(c, tx, EntityTypes.MaterialOverride, qoMaterialId,
ActionCodes.Override, oldRow: null, newRow: new { newSize, reason = reason ?? "" }, user)`
call. `IAuditService` becomes the single audit-write path in the codebase.

This change depends on PH-1 (each method must be transactional first so the audit call
inside it has a `tx` to share).

---

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

*(None. Path A satisfies all six principles without deviation. After the post-design
review (2026-05-20), the originally-proposed new `Filters/` folder was eliminated by
relocating the single ActionFilter into `Services/` — no new top-level folders, no new
architectural layers.)*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| *(none)* | — | — |
