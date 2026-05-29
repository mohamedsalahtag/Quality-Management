# Phase 1 Data Model — Audit Trail

**Feature**: `001-audit-trail`
**Date**: 2026-05-20

This document defines the persisted data shape, the in-memory domain objects, the
migration delta, and the access patterns. Everything lives under the existing
`SharbatlyQMS` database (`192.168.3.10`) and the existing `app/SharbatlyQMS.Web/`
project — no new database, no new project.

---

## 1. Persisted Storage

### 1.1 Existing table — `qms_audit_log` (created in V02; extended by V15)

The audit table **already exists** from the initial QMS schema (V02). It currently
stores material-override actions only, but the column shape is 90 % of what we need.
V15 extends it in place rather than introducing a parallel table.

**Current columns (V02 → V14, unchanged):**

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `audit_id` | `BIGINT IDENTITY(1,1)` | PK | Insertion order = monotonic in practice; used as the keyset cursor tie-breaker. |
| `entity_type` | `VARCHAR(40)` | NOT NULL | One of the 11 tracked types: `Arrival`, `ArrivalItem`, `ArrivalChecklist`, `QualityOrder`, `QualityOrderMaterial`, `Sample`, `SampleReading`, `SampleDefect`, `Claim`, `ClaimNote`, `MaterialOverride`. Free-text VARCHAR — no FK to a `qms_entity_type` lookup; the canonical list lives in the application's `EntityTypes` constants class. |
| `entity_id` | `BIGINT` | NOT NULL | The PK of the tracked record (no FK — see FR-014). |
| `action_code` | `VARCHAR(40)` | NOT NULL | `Created`, `Updated`, `Deleted`, or a domain action (`Closed`, `Reopened`, `Approved`, `Hold`, `Override`, `OverrideCleared`, …). |
| `old_values_json` | `NVARCHAR(MAX)` | NULL | JSON object with only the changed fields' OLD values. `NULL` on `Created`. |
| `new_values_json` | `NVARCHAR(MAX)` | NULL | JSON object with only the changed fields' NEW values. `NULL` on `Deleted`. |
| `changed_at` | `DATETIME2` | NOT NULL DEFAULT `SYSUTCDATETIME()` | UTC timestamp. Indexed for filter / pagination. |
| `changed_by` | `NVARCHAR(80)` | NOT NULL | The real underlying username (`ClaimTypes.Name`), even under View-as impersonation (per FR-015). |
| `source_ip` | `VARCHAR(45)` | NULL | IPv4 or IPv6 string. Populated by the ActionFilter. Empty string when unavailable (rendered as `unknown`). |

**Columns ADDED by V15:**

| Column | Type | Nullable | Notes |
|---|---|---|---|
| `source_user_agent` | `NVARCHAR(500)` | NULL | Raw `User-Agent` HTTP header. Truncated to 500 chars (sufficient for any real-world UA). Empty string when unavailable (rendered as `unknown`). |

**Existing index (kept):**

```sql
CREATE INDEX IX_qms_audit_log_entity
    ON qms_audit_log(entity_type, entity_id);
```

Serves the per-record history fetch (FR-008) — point lookup pattern.

**Index ADDED by V15:**

```sql
CREATE INDEX IX_qms_audit_log_filter
    ON qms_audit_log(changed_at DESC)
    INCLUDE (entity_type, action_code, changed_by);
```

Serves the global audit list with filter + keyset pagination (FR-009, SC-008).

**CHECK constraints:** None on `qms_audit_log` itself. The `entity_type` /
`action_code` values are validated in the C# layer (`EntityTypes`, `ActionCodes`
classes) rather than in SQL — keeps the table forward-compatible with future entity
types added in later features without requiring another migration.

**Archive-friendliness check (FR-020):**
- ✅ Single sortable timestamp column (`changed_at`).
- ✅ No FKs from this table outward (already satisfied: `entity_id` is plain BIGINT, no
  REFERENCES clause).
- ✅ No FKs from operational tables INTO this table (verified — `qms_claim`,
  `qms_quality_order`, etc. do not reference `audit_id`).
- ✅ A future range-delete partition swap is mechanically possible without cascade
  effects.

### 1.2 Existing role table — `Users` (constraint extended by V15)

V15 extends `CK_Users_Role` to allow `Auditor`, using the same idempotent drop-and-recreate
pattern established by V12 and V13:

```sql
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name='CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Auditor','Operator','Viewer'));
```

No data migration — existing rows keep their roles untouched.

---

## 2. In-Memory Domain Models (C#)

Lives in `app/SharbatlyQMS.Web/Models/AuditEntry.cs`.

### 2.1 `AuditEntry`

```csharp
public class AuditEntry
{
    public long      AuditId         { get; set; }
    public string    EntityType      { get; set; } = "";
    public long      EntityId        { get; set; }
    public string    ActionCode      { get; set; } = "";
    public string?   OldValuesJson   { get; set; }
    public string?   NewValuesJson   { get; set; }
    public DateTime  ChangedAt       { get; set; }
    public string    ChangedBy       { get; set; } = "";
    public string?   SourceIp        { get; set; }
    public string?   SourceUserAgent { get; set; }
}
```

Mirrors `qms_audit_log` columns 1:1 — Dapper hydrates this directly via
`SELECT col AS Prop` aliasing as per Principle II.

### 2.2 `AuditEntryListRow`

The shape returned by `AuditService.ListAsync` — adds two derived values for the global
audit log page:

```csharp
public class AuditEntryListRow : AuditEntry
{
    /// <summary>Human-readable label, e.g. "Quality Order QO-2026-000042 — Closed".</summary>
    public string DisplayLabel       { get; set; } = "";
    /// <summary>Computed in C# from OldValuesJson + NewValuesJson — the field-by-field
    /// (field-name, old-value, new-value) tuples ready for the Razor diff renderer.</summary>
    public IReadOnlyList<DiffRow> DiffRows { get; set; } = Array.Empty<DiffRow>();
}

public record DiffRow(string FieldName, string? OldValue, string? NewValue);
```

`DiffRow` parsing happens once per row at hydration time so the Razor view stays clean
(no `JsonDocument` parsing in `.cshtml`).

### 2.3 `AuditFilter`

Stateless URL-binding object for the global page.

```csharp
public class AuditFilter
{
    public string[]?  Users        { get; set; }   // 0..N usernames
    public DateTime?  FromUtc      { get; set; }   // inclusive
    public DateTime?  ToUtc        { get; set; }   // inclusive
    public string[]?  EntityTypes  { get; set; }   // 0..N — empty = all
    public string[]?  ActionCodes  { get; set; }   // 0..N — empty = all
    // Keyset cursor (set by the previous page link):
    public DateTime?  CursorTime   { get; set; }
    public long?      CursorId     { get; set; }
    public int        PageSize     { get; set; } = 25;
}
```

### 2.4 `EntityTypes` / `ActionCodes` constants

```csharp
public static class EntityTypes
{
    public const string Arrival              = "Arrival";
    public const string ArrivalItem          = "ArrivalItem";
    public const string ArrivalChecklist     = "ArrivalChecklist";
    public const string QualityOrder         = "QualityOrder";
    public const string QualityOrderMaterial = "QualityOrderMaterial";
    public const string Sample               = "Sample";
    public const string SampleReading        = "SampleReading";
    public const string SampleDefect         = "SampleDefect";
    public const string Claim                = "Claim";
    public const string ClaimNote            = "ClaimNote";
    public const string MaterialOverride     = "MaterialOverride";

    public static readonly string[] All = { Arrival, ArrivalItem, ArrivalChecklist,
        QualityOrder, QualityOrderMaterial, Sample, SampleReading, SampleDefect,
        Claim, ClaimNote, MaterialOverride };
}

public static class ActionCodes
{
    // Generic CRUD
    public const string Created  = "Created";
    public const string Updated  = "Updated";
    public const string Deleted  = "Deleted";

    // Domain-specific labels preserved from existing services (per FR-002 and the
    // 2026-05-20 clarification on domain action preservation).
    public const string Opened              = "Opened";    // QO
    public const string Closed              = "Closed";    // QO
    public const string Reopened            = "Reopened";  // QO
    public const string Cancelled           = "Cancelled"; // QO
    public const string Approved            = "Approved";  // Claim
    public const string Hold                = "Hold";      // Claim
    public const string PassedQC            = "PassedQC";  // Claim
    public const string ClaimRequest        = "ClaimRequest"; // Claim
    public const string Override            = "Override";       // Material size override
    public const string OverrideCleared     = "OverrideCleared"; // Material size override

    public static readonly string[] All = { Created, Updated, Deleted, Opened, Closed,
        Reopened, Cancelled, Approved, Hold, PassedQC, ClaimRequest, Override,
        OverrideCleared };

    /// <summary>The simple-CRUD subset for the global filter chip group (FR-010).</summary>
    public static readonly string[] CrudOnly = { Created, Updated, Deleted };
}
```

### 2.5 New role constant — extends `Models/User.cs`

```csharp
public static class UserRoles
{
    public const string Viewer       = "Viewer";
    public const string Operator     = "Operator";
    public const string Manager      = "Manager";
    public const string ClaimManager = "ClaimManager";
    public const string Auditor      = "Auditor";          // NEW
    public const string SiteAdmin    = "SiteAdmin";

    public static readonly string[] All =
        { Viewer, Operator, Manager, ClaimManager, Auditor, SiteAdmin };

    public static bool IsValid(string role) => Array.IndexOf(All, role) >= 0;
}

public static class AuthPolicies
{
    public const string AdminOnly             = "AdminOnly";
    public const string ManagerOrAdmin        = "ManagerOrAdmin";
    public const string OperatorOrAbove       = "OperatorOrAbove";
    public const string ClaimManagerOrAdmin   = "ClaimManagerOrAdmin";

    // NEW
    /// <summary>Manager, Auditor, or SiteAdmin. Read-only access to audit views.</summary>
    public const string AuditViewer           = "AuditViewer";

    /// <summary>Auditor or SiteAdmin. Excel export of the audit log.</summary>
    public const string AuditorOrAdmin        = "AuditorOrAdmin";
}
```

### 2.6 New service contracts (signatures only — implementation lives in `Services/`)

```csharp
public interface IAuditContext
{
    string? RemoteIp        { get; }
    string? UserAgent       { get; }
}

public interface IAuditService
{
    /// <summary>
    /// Writes one audit-log row. MUST be called inside the caller's open transaction
    /// (the connection + tx are passed in) so the audit row commits atomically with the
    /// originating mutation (FR-007).
    /// </summary>
    Task WriteAsync(SqlConnection conn, SqlTransaction tx,
        string entityType, long entityId, string actionCode,
        object? oldValues, object? newValues, string actor);

    /// <summary>Per-record history fetch (FR-008) — point lookup on
    /// IX_qms_audit_log_entity.</summary>
    Task<IReadOnlyList<AuditEntryListRow>> GetForRecordAsync(
        string entityType, long entityId);

    /// <summary>Global filtered list (FR-009, FR-010) — keyset-paginated.</summary>
    Task<IReadOnlyList<AuditEntryListRow>> ListAsync(AuditFilter filter);

    /// <summary>Streams every matching entry within the date range (FR-012). Pulls in
    /// chunks to avoid buffering the full export in memory.</summary>
    IAsyncEnumerable<AuditEntry> ExportAsync(AuditFilter filter);
}
```

---

## 3. Access Patterns and Their Indexes

| Pattern | SQL (sketch) | Index used | Expected freq |
|---|---|---|---|
| Per-record history | `SELECT … FROM qms_audit_log WHERE entity_type=@t AND entity_id=@id ORDER BY changed_at DESC` | `IX_qms_audit_log_entity` (existing) | High — every detail-page render |
| Global filter | `SELECT TOP @n … FROM qms_audit_log WHERE changed_at < @cursor AND (filter clauses) ORDER BY changed_at DESC, audit_id DESC` | `IX_qms_audit_log_filter` (new in V15) | Moderate — Quality Manager investigations |
| Excel export | Same WHERE as global filter, no `TOP`; streamed via `IAsyncEnumerable` | `IX_qms_audit_log_filter` | Low — periodic |
| INSERT (audit write) | `INSERT INTO qms_audit_log (…) VALUES (…)` | (heap) | Very high — one per mutation across all 11 entity types |

---

## 4. V15 Migration Sketch

`app/db/V15__audit_trail.sql` — applied via `dotnet run -- apply <conn> V15__audit_trail.sql`.

```sql
-- ============================================================================
-- V15  Audit Trail feature
--
-- Extends qms_audit_log (introduced in V02) with the source_user_agent column
-- + a composite filter index for the global audit log read path. Adds Auditor
-- to the Users role CHECK constraint. No data migration required.
-- ============================================================================

-- 1. Add user-agent column (existing rows get NULL — backward compatible).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_user_agent')
BEGIN
    ALTER TABLE qms_audit_log
        ADD source_user_agent NVARCHAR(500) NULL;
END
GO

-- 2. Filter / pagination index for the global audit log page (SC-008).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_qms_audit_log_filter'
      AND object_id = OBJECT_ID('qms_audit_log'))
BEGIN
    CREATE INDEX IX_qms_audit_log_filter
        ON qms_audit_log(changed_at DESC)
        INCLUDE (entity_type, action_code, changed_by);
END
GO

-- 3. Extend CK_Users_Role to allow 'Auditor' (same idempotent pattern as V12/V13).
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Users_Role')
    ALTER TABLE Users DROP CONSTRAINT CK_Users_Role;
GO
ALTER TABLE Users ADD CONSTRAINT CK_Users_Role
    CHECK (Role IN ('SiteAdmin','Manager','ClaimManager','Auditor','Operator','Viewer'));
GO

-- 4. Sanity report.
SELECT 'qms_audit_log.source_user_agent' AS check_item,
       CASE WHEN EXISTS (SELECT 1 FROM sys.columns
            WHERE object_id = OBJECT_ID('qms_audit_log') AND name = 'source_user_agent')
            THEN 'OK' ELSE 'MISSING' END AS status
UNION ALL
SELECT 'IX_qms_audit_log_filter',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes
            WHERE name = 'IX_qms_audit_log_filter') THEN 'OK' ELSE 'MISSING' END
UNION ALL
SELECT 'CK_Users_Role contains Auditor',
       CASE WHEN EXISTS (SELECT 1 FROM sys.check_constraints
            WHERE name = 'CK_Users_Role' AND definition LIKE '%Auditor%')
            THEN 'OK' ELSE 'MISSING' END;
GO
```

---

## 5. State Transitions

`AuditEntry` rows have **no state transitions** — the audit log is append-only
(FR-006, Principle VI). Once written, a row is never updated and never deleted by any
in-app surface.

The Quality Order state machine and Claim state machine that produce audit entries
are unchanged by this feature; they already had their own state diagrams in the spec
and PROJECT_STATE.md and remain authoritative.

---

## 6. Validation Rules Surfaced by Requirements

| Rule | Source FR | Where enforced |
|---|---|---|
| `entity_type` MUST be one of the 11 tracked types. | FR-018 | C# (`EntityTypes.All` check at write time). Validation by SQL not used so future entity types don't require another migration. |
| `action_code` MUST be one of `ActionCodes.All`. | FR-002 | C# (`ActionCodes.All` check at write time). |
| `changed_by` MUST be the real user, not the impersonated role. | FR-015 | `User.FindFirst(ClaimTypes.Name)?.Value` — `ViewAsClaimsTransformer` only swaps the `Role` claim, not the identity name. |
| Audit write MUST share the mutation's transaction. | FR-007 | Service signature requires `SqlConnection conn, SqlTransaction tx` to be passed in — no way to write outside a caller's transaction. |
| No-op updates MUST NOT produce an entry. | FR-016 | Service-layer: caller skips `WriteAsync` if its diff comparison shows zero changed fields. |
| Audit entries MUST persist when their record is deleted. | FR-014 | No FK from `qms_audit_log` to operational tables. Deletes of operational rows leave audit rows orphan-by-design. |
| Excel export MUST honour the same filter set. | FR-013 | `AuditService.ExportAsync(AuditFilter)` shares the filter parser with `ListAsync`. |
| One in-flight export per user. | FR-021 | In-memory `ConcurrentDictionary<string, DateTime>` keyed on username in `AuditController.Export`. |
