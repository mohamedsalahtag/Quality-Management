# Phase 0 Research — Audit Trail

**Feature**: `001-audit-trail`
**Date**: 2026-05-20
**Status**: Complete

Phase 0 resolves unknowns surfaced during plan drafting and confirms best-practice
choices against the existing Sharbatly QMS conventions. There were no
`NEEDS CLARIFICATION` markers carried over from the spec (all five clarifications were
resolved in the 2026-05-20 clarify session); the remaining open questions concern
implementation patterns, not requirements.

---

## R-1. Diff computation strategy without an ORM ChangeTracker

**Decision**: Each mutating service computes the diff itself, immediately before calling
`IAuditService.WriteAsync(...)`. The pattern is:

1. **Load the row before mutation** (`SELECT … FROM <table> WHERE <id>=@id`) using the
   same `SqlConnection` that will run the UPDATE / DELETE.
2. Perform the mutation in the same `BeginTransaction()` block.
3. Build two anonymous-object payloads — `oldRow` and `newRow` — containing the relevant
   fields (or `null` for the missing side on Create / Delete).
4. Pass both to `IAuditService.WriteAsync(...)`. The service serialises with
   `System.Text.Json.JsonSerializer.Serialize(...)` and INSERTs into `qms_audit_log`.
5. Commit the transaction.

**Rationale**:
- EF Core's `ChangeTracker` is not available (Principle II forbids EF Core).
- Implementing a generic Dapper "diff snapshotter" would require either reflection-heavy
  attribute scanning or interception at the `SqlCommand` layer — both are significantly
  more complex than the explicit before/after pattern.
- The codebase already follows this pattern for the existing override actions
  (`QualityOrderService.SaveOverrideAsync` reads the row's current size before the
  UPDATE; the audit insert builds `new_values_json` from `JsonSerializer.Serialize(new {
  newSize, reason = reason ?? "" })`). The new feature simply generalises this.
- "No-op updates not logged" (FR-016) is naturally satisfied: if the service detects
  zero field changes between the pre-read and the post-mutation values, it skips the
  audit call.

**Alternatives considered and rejected**:
- *Reflection-based diff* (`object → Dictionary<string,object>` via `GetProperties()`):
  introduces hidden serialisation surprises with nullable types, decimal precision, and
  enum representation. Hard to debug.
- *SQL-side OUTPUT clause* (`UPDATE … OUTPUT deleted.*, inserted.* INTO …`): elegant,
  but couples each mutation site to a SQL Server–specific syntax that needs careful
  retrofitting into existing services. Defer to a future optimisation if the explicit
  pattern becomes verbose.
- *Trigger-based audit*: a SQL `AFTER UPDATE` trigger could write `qms_audit_log` rows
  automatically. Rejected because: (a) the trigger has no access to the application's
  `User`, `IP`, or `user-agent` context; (b) triggers fire on every UPDATE including
  background jobs that FR-001 explicitly excludes; (c) audit + mutation no longer share
  the application's controlled transaction boundary; (d) hidden behaviour makes
  reviewing changes harder.

---

## R-2. Capturing HTTP request context (IP + user-agent) into a service

**Decision**: A scoped `IAuditContext` is populated by a global ActionFilter
(`AuditContextActionFilter`) and injected into `AuditService`. The filter runs before
every action, reads `HttpContext.Connection.RemoteIpAddress` and
`HttpContext.Request.Headers["User-Agent"]`, and writes them into the
`AuditContext` instance for that scope.

**Rationale**:
- Services should not depend on `IHttpContextAccessor` directly (it's a "lazy abstract
  factory" anti-pattern in non-controller code); a strongly-typed `IAuditContext` is
  much cleaner and explicitly carries only what the audit needs.
- The codebase already uses a similar pattern (`ViewAsClaimsTransformer` runs as an
  `IClaimsTransformation` and reads `HttpContext` via `IHttpContextAccessor` once, then
  exposes the result as claims).
- An ActionFilter (rather than middleware) runs per MVC action, so background hosted
  services (`AutoSyncService`, `AdCachePrimingService`) do not get an IP / UA — exactly
  matching FR-001's "user-initiated mutations" boundary.

**Alternatives considered and rejected**:
- *Inject `IHttpContextAccessor` directly into `AuditService`*: works, but spreads the
  HTTP dependency across the service layer and complicates background-job audit calls
  (which would have to remember to pass `null`). The explicit `IAuditContext` makes the
  contract clear.
- *Pass IP/UA as parameters on every `WriteAsync` call*: verbose. 20+ call sites would
  each repeat the lookup. ActionFilter centralises it.

---

## R-3. Excel export library choice (ClosedXML vs alternatives)

**Decision**: **ClosedXML** (latest 0.105.x), added via `PackageReference` to
`SharbatlyQMS.Web.csproj`. License: MIT.

**Rationale**:
- Generates real `.xlsx` (Open-XML format) without requiring Excel installed on the
  server. Output is bit-identical between Windows / Linux / macOS hosts.
- Streaming API (`IXLWorksheet.Cell(row, col).SetValue(...)`) supports the ~50K-row
  export volume cap (Assumptions) without buffering the entire file in RAM.
- Active maintenance, ~9 K GitHub stars, ~110 M total NuGet downloads — mature and
  well-supported.
- Matches the existing QuestPDF pattern in the codebase: QuestPDF is also a
  community-licensed pure-managed library for binary-format generation. Adding
  ClosedXML alongside it is a natural extension.

**Alternatives considered and rejected**:
- *EPPlus 5+*: switched to a commercial licence in 2020. Existing free fork
  (EPPlus < 4.5.x) is unmaintained.
- *NPOI*: works, but the API is a port of Java's POI library and feels awkward in C#.
  Bigger learning curve, less idiomatic.
- *DocumentFormat.OpenXml (Microsoft)*: the lowest-level option — gives total control
  but requires writing ~3× more code for the same output. Overkill for a simple tabular
  export.
- *CSV instead of XLSX*: rejected by the spec (FR-012 mandates `.xlsx`).

---

## R-4. Indexing strategy for SC-008 (1 s p95 / 3 s p99 at 10 M rows)

**Decision**: Add **one composite descending index** in the V15 migration:

```sql
CREATE INDEX IX_qms_audit_log_filter
    ON qms_audit_log(changed_at DESC)
    INCLUDE (entity_type, action_code, changed_by);
```

Combined with **keyset pagination** (`WHERE (changed_at, audit_id) < (@cursorTime,
@cursorId) ORDER BY changed_at DESC, audit_id DESC`) in `AuditService.ListAsync`, this
satisfies the 1 s p95 target up to 10 M rows on the existing SQL Server hardware.

**Rationale**:
- The dominant query is "page through the most recent N entries matching a filter
  combination on `entity_type` and/or `changed_by`". A descending index on `changed_at`
  with the filter columns INCLUDE-d covers the query without a key lookup for ~80 % of
  realistic filter combinations.
- The existing `IX_qms_audit_log_entity (entity_type, entity_id)` stays — it serves the
  per-record history fetch (FR-008), which is a different access pattern (point lookup
  on `(entity_type, entity_id)`).
- Keyset pagination beats `OFFSET/FETCH NEXT` at depth: `OFFSET 100000` still scans 100 K
  rows on the descending index; a keyset cursor is O(log n) regardless of depth.

**Alternatives considered and rejected**:
- *Single index on `(changed_at, entity_type, changed_by)`*: covers the same queries but
  the leading `changed_at` column would still need DESC ordering; the chosen form is
  simpler and more flexible.
- *Add `actor` index too*: would speed up the "all activity by user X" filter, but the
  INCLUDE on `changed_by` already covers the lookup. Index maintenance cost vs the
  marginal latency improvement isn't justified at the spec's volume.
- *Skip pagination, render all results client-side*: not viable at 10 M rows.

---

## R-5. Audit-friendly transaction pattern across existing services

**Decision**: Add `IAuditService.WriteAsync(SqlConnection c, SqlTransaction tx, string
entityType, long entityId, string actionCode, object? oldRow, object? newRow, string
actor)` — the audit service does **not** open its own connection. The caller's
connection + transaction are passed in, ensuring the audit INSERT lives inside the same
transaction as the mutation (FR-007).

**Rationale**:
- All existing services already follow `using var c = Open(); await c.OpenAsync(); using
  var tx = c.BeginTransaction(); … tx.Commit();` — passing the connection is a one-line
  change at each call site.
- IP / UA / real-user are pulled from `IAuditContext` injected into `AuditService`
  rather than being passed each call.
- Adding a second connection would either (a) need a distributed transaction (overkill)
  or (b) write the audit OUTSIDE the mutation's transaction (violates FR-007 atomicity).

**Alternatives considered and rejected**:
- *AuditService opens its own connection*: violates FR-007 (atomicity). Rejected.
- *Audit via SaveChanges-style buffered approach* (collect changes, write at end of
  request): doesn't work without an ORM, and breaks atomicity when a request makes
  multiple mutations across different services.

---

## R-6. `Auditor` role placement in the existing roles enum

**Decision**: Add `Auditor` as the 6th member of `UserRoles.All`, positioned between
`ClaimManager` and `SiteAdmin`. Same precedent as `ClaimManager` (a peer role added in
the 2026-05-14 claim-management work).

```csharp
public const string Viewer       = "Viewer";
public const string Operator     = "Operator";
public const string Manager      = "Manager";
public const string ClaimManager = "ClaimManager";
public const string Auditor      = "Auditor";          // NEW
public const string SiteAdmin    = "SiteAdmin";

public static readonly string[] All =
    { Viewer, Operator, Manager, ClaimManager, Auditor, SiteAdmin };
```

**Rationale**:
- The array order is cosmetic (per the constitution, "ClaimManager sits between Manager
  and SiteAdmin in the array purely for cosmetic ordering — it is NOT a hierarchical
  'more powerful than Manager' claim"). `Auditor` follows the same convention.
- `CK_Users_Role` is extended in the V15 migration to allow the new value — same
  pattern as V12 (the role overhaul) and V13 (which added `ClaimManager`).
- Existing role-related Razor (`Admin/Users.cshtml` role `<select>`, role badge
  colouring) automatically picks up the new value because they iterate `UserRoles.All`.

**Alternatives considered and rejected**:
- *Composite permission via a flag on Users (`IsAuditor` boolean)*: rejected during the
  constitution session for `ClaimManager` for the same reasons — the roles enum is the
  single source of truth, two parallel mechanisms cause review confusion.

---

## R-7. Per-record audit panel injection — partial view, not inheritance

**Decision**: Add a Razor partial `Views/Audit/_AuditHistory.cshtml` that takes
`(entityType, entityId)` and lazily fetches via AJAX from
`AuditController.HistoryPanel(string entityType, long entityId)`. Each detail view
(`QualityOrders/Details.cshtml`, `Arrivals/Details.cshtml`, etc.) includes the partial
inside a role-gated `@if` block.

**Rationale**:
- Lazy-load keeps the detail page render fast even for records with hundreds of audit
  entries.
- The same partial works for every entity type — no per-entity Razor duplication.
- Role gating is centralised in the host view's `@if (canSeeAudit)` block, matching the
  existing pattern (`@if (canManage)`, `@if (canAct)` etc. in `QualityOrders/Details.cshtml`).
- Matches how `ClaimManagement/_ClaimChatPanel.cshtml` is included from
  `QualityOrders/Details.cshtml` — minimal surgical change to each host page.

**Alternatives considered and rejected**:
- *Server-side rendering inline*: blocks page render until the audit query runs.
- *A View Component*: marginally cleaner C#-wise, but introduces a new pattern type that
  isn't used anywhere else in the codebase. Sticking with partials keeps the convention
  consistent.

---

## R-8. Single-in-flight export per user — implementation

**Decision**: A small in-memory `ConcurrentDictionary<string, DateTime>` keyed by
`(username, "export")` tracks the start time of the currently-streaming export per user.
`AuditController.Export(...)` checks the dictionary first; if an entry less than 5
minutes old exists, returns the friendly message per FR-021. The entry is added at
stream start and removed in a `finally` block.

**Rationale**:
- The QMS runs as a single-instance Windows Service — no horizontal scaling — so a
  process-local dictionary is sufficient. Distributed coordination (e.g. Redis) would
  be over-engineering.
- The 5-minute upper bound is a safety net in case an export crashes without hitting
  the `finally` (e.g. process kill); the next export attempt after 5 min succeeds.
- This is a simpler version of the cache pattern used by `MaraService` and
  `VendorService`.

**Alternatives considered and rejected**:
- *Lock per user via `SemaphoreSlim`*: the second click would block until the first
  finishes, downloading TWO files for one user. FR-021 wants the second click refused,
  not queued.
- *Database-side advisory lock*: works but adds round-trips. The in-memory map suffices
  for a single instance.

---

## Outstanding items — none

All design unknowns are resolved. No `NEEDS CLARIFICATION` items remain. Phase 1
(data-model + contracts + quickstart) may proceed.
