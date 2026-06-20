# data-hub-pack — SPEC

Stack-agnostic behavioural contract. The pack ships an ASP.NET Core .NET 9 + Dapper + SQL Server reference implementation, but the contract below is what any port must honour.

## 1. Storage

### `qms_perspective`

Single table. One row per saved analyzer configuration.

| Column | Type | Notes |
|---|---|---|
| perspective_id | BIGINT IDENTITY PK | |
| report_key | VARCHAR(60) NOT NULL | host's logical report identifier (`'flat_defects'`, `'containers'`, etc.). Mirrors the `PivotReport.Key`. |
| name | NVARCHAR(150) NOT NULL | user-facing label |
| owner_username | NVARCHAR(80) NOT NULL | sAMAccountName (or whatever the host uses for `User.Identity.Name`) |
| scope | VARCHAR(10) NOT NULL CHECK IN (`'private'`, `'shared'`) | `private` = owner-only; `shared` = visible to all readers |
| is_default | BIT NOT NULL DEFAULT 0 | a user's auto-load perspective for that report |
| config_json | NVARCHAR(MAX) NOT NULL | opaque payload — every UI setting (rows, cols, measures, drills, scope, renderer, format, hideZero, topN, chartMeasure) round-trips here |
| created_at / created_by | DATETIME2 / NVARCHAR(80) | required |
| updated_at / updated_by | DATETIME2 / NVARCHAR(80) | nullable |

Indexes:

- `IX_qms_perspective_owner_report (owner_username, report_key)` — list-for-user lookup.
- `IX_qms_perspective_shared (report_key) WHERE scope = 'shared'` — shared-perspective discovery.
- `UX_qms_perspective_default UNIQUE (owner_username, report_key) WHERE is_default = 1` — filtered-unique index guarantees at most one default per (user, report).

### Host-supplied flat view

The pack does **not** define the data view. The host provides a denormalised SQL view (or table) named however they like (e.g. `vw_<domain>_flat`) that contains one row per fact and the following column shape:

- Every column the registry references as a `PivotDimension.SqlExpression` must be selectable. Expressions are interpolated verbatim into the SELECT and GROUP BY, so the view must expose anything those expressions reference.
- Numeric columns the registry references as `PivotMeasure.SqlInner` must be aggregatable with `SUM / AVG / MIN / MAX / COUNT / COUNT(DISTINCT …)` as listed in the measure's `AllowedAggs`.

The pack's reference `schema.sql` ships a commented template view to copy + edit.

## 2. The plug-in seam

Three hooks, in order of how much code the integrator writes for each:

### `IDataHubFilterAdapter` (REQUIRED)

```csharp
public interface IDataHubFilterAdapter
{
    void AppendFilterWhere(StringBuilder sb, DynamicParameters p, object? filter);
}
```

The host's controller deserialises its filter shape from the querystring or body and hands the object to the adapter. The adapter inspects the type, emits parameterised `AND <expr>` fragments per active slot. Anything the host wants the analyzer to scope by (date ranges, status enums, vendor substrings, etc.) is realised here.

A `DefaultDataHubFilterAdapter` ships that emits **no** fragments — good enough to bring the analyzer up end-to-end before adding constraints.

### `PivotRegistry.Register(PivotReport)` (REQUIRED)

```csharp
public sealed record PivotDimension(string Key, string Display, string SqlExpression);
public sealed record PivotMeasure  (string Key, string Display, string SqlInner, string[] AllowedAggs);
public sealed record PivotReport   (string Key, string ViewName,
                                    IReadOnlyList<PivotDimension> Dimensions,
                                    IReadOnlyList<PivotMeasure>   Measures);
```

Every dimension key + measure key the analyzer is allowed to group / aggregate over must live in the registry. The HTTP body only carries **keys** — `Plant`, `DefectName`, `Variety` — never raw SQL. The registry resolves them to constant `SqlExpression` strings. **This is the SQL-injection guardrail; nothing else stands between a request body and the database.**

Time-bucket dims (`YEAR(PoDate)`, `FORMAT(PoDate, 'yyyy-MM')`, etc.) work the same way — constant strings in the registry, no user input ever in the expression.

### `IDataHubAuthContext` (OPTIONAL)

The pack uses `User.IsInRole(...)` and `User.Identity?.Name`. If the host has a non-AspNetCore auth model, replace those two calls with whatever it uses for caller identity + role check. Otherwise leave them alone.

## 3. Endpoints

All require `User.IsAuthenticated`. The reference `DataHubController` gates everything with `[Authorize(Policy = "SupervisorOrAbove")]` — the host substitutes its own policy.

| Method | Route | Purpose | Body |
|---|---|---|---|
| GET | `/Reports/PivotSchema?report={key}` | Returns the registry: `{ dimensions, measures, renderers }`. UI populates dropdowns. | — |
| POST | `/Reports/Pivot` | Runs one GROUP BY. Returns `PivotResult`. | `PivotRequest` |
| POST | `/Reports/PivotExcel` | Same query as `Pivot`, response is an `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` file. | `PivotRequest` |
| GET | `/Reports/PivotValues?report=&dim=&q=&ignorePageFilter=&<filter>` | Distinct values of one dim under the active scope. Used by the drill picker. Cached 60s in `IMemoryCache`. | — |
| GET | `/Reports/Perspectives?report={key}` | Lists perspectives the caller can see: own (private + shared) + everyone else's shared. Default first, then own, then shared. | — |
| POST | `/Reports/SavePerspective` | Save / update. `scope='shared'` enforced server-side to `private` for non-Manager/SiteAdmin. | `PerspectiveSaveRequest` |
| POST | `/Reports/SetDefaultPerspective` | Flip default for the caller. Editing a shared perspective from another user clones it locally as a private default. | form `id=` |
| POST | `/Reports/DeletePerspective` | Owner-or-SiteAdmin only. | form `id=` |

All POST endpoints require the `X-CSRF-TOKEN` header carrying the antiforgery token. The pack assumes `AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN")`.

## 4. Auth

- **Read** (everything except saving): `SupervisorOrAbove` (or whatever the host calls it).
- **Save shared**: `Manager` or `SiteAdmin`. Server re-checks this in `PerspectiveService.SaveAsync` regardless of what the UI sent.
- **Delete**: owner can delete their own; `SiteAdmin` can delete any. Manager **cannot** delete another manager's shared perspective — gives shared content a soft governance gate.

## 5. Hard rules

These are non-negotiable; breaking any breaks safety or correctness:

1. **Registry-only identifiers.** Every SQL column / expression in the dynamic GROUP BY / WHERE / SELECT comes from `PivotRegistry`. HTTP carries keys, not SQL.
2. **Parameterised filter values.** `IDataHubFilterAdapter` and the drill builder bind every value with `DynamicParameters`. No string concatenation of user input.
3. **`(null)` sentinel.** The analyzer renders SQL `NULL` cells as the literal string `"(null)"`. Drill filters use the same sentinel; the SQL builder translates it to `({expr}) IS NULL OR ({expr}) IN (...)`.
4. **One default per (owner, report).** Enforced by the filtered unique index.
5. **Shared scope re-checked server-side.** Hand-crafted POSTs with `scope='shared'` from non-managers are demoted to `private`. Do not trust the client.
6. **JSON response casing is camelCase.** ASP.NET Core's default `System.Text.Json` lowercases the first letter; the client reads `result.rowKeys` etc. Don't override the policy unless you also rewrite the JS.
7. **Vendored Plotly.** `plotly-basic.min.js` ships with the pack. No CDN; no internet at runtime.

## 6. UI contract

The analyzer is a single Razor partial `_PerspectiveAnalyzer.cshtml` + one vanilla-JS module `perspective-analyzer.js`. Bootstrap 5 + jQuery 3 + Bootstrap Icons assumed; the partial uses no extra CSS framework. Components, top to bottom:

- **Toolbar** — saved-perspective `<select>` (grouped: My perspectives / Shared, default chip marked ★), Save / Save as… / Set as default / Delete buttons.
- **Scope bar** — radio toggle `Match page filters` / `Whole dataset` + live breadcrumb of the current filter.
- **Three picker cards** side-by-side — Available fields (with search), Rows zone, Columns zone. Each chip is draggable; click + buttons add to a zone; × removes.
- **Drill / Filter strip** — chip zone holding any drill filters. `+ Add filter` opens a values-picker modal.
- **Values strip** — Excel-style Σ Values zone. `+ Add measure` opens the measure picker.
- **Controls row** — number-format fallback, renderer (Table / Bar / Stacked Bar / Line / Area / Heatmap), optional measure-to-chart picker (shown only when ≥2 measures and renderer ≠ Table), Top-N input, Heat + Hide-0s checkboxes.
- **Run / Reset / Export to Excel** buttons.
- **Output area** — either a Bootstrap crosstab table (with per-measure heat shading) or a Plotly chart.

The partial is reusable: each `report_key` reuses the same partial; the host page passes the key + its filter querystring + the `CanShare` flag through the view model.

## 7. JSON contracts (reference)

### `PivotRequest`

```jsonc
{
  "ReportKey":        "flat_defects",
  "Rows":             ["Plant", "DefectName"],
  "Cols":             ["VendorName"],
  "Measures": [
    { "Key": "DefectValue", "Agg": "SUM", "Format": "int",  "Label": "Defect count" },
    { "Key": "SampleSize",  "Agg": "AVG", "Format": "dec1", "Label": "Avg sample size" }
  ],
  "TopN":             null,
  "Filter":            { /* host filter shape */ },
  "IgnorePageFilter": false,
  "Drills": [
    { "DimensionKey": "DefectName", "Values": ["Creasing", "Molds"] }
  ]
}
```

### `PivotResult`

```jsonc
{
  "rowDimensions": ["Plant", "Defect"],
  "colDimensions": ["Supplier"],
  "measures": [
    { "key":"DefectValue", "label":"Defect count", "agg":"SUM", "format":"int" },
    { "key":"SampleSize",  "label":"Avg sample size", "agg":"AVG", "format":"dec1" }
  ],
  "rowKeys":   [["BU01","Creasing"], ["BU01","Molds"]],
  "colKeys":   [["Arabian Agricultural"]],
  "cells":     [{ "rowIndex":0, "colIndex":0, "values":[3, 7.5] }],
  "rowTotals": [[3, 7.5], [1, 6.0]],
  "colTotals": [[4, 6.8]],
  "grandTotals": [4, 6.8],
  "rowsScanned": 2,
  "truncated":   false
}
```

`Cell.values[mi]` is `null` when SQL returned `NULL` for that measure (e.g. `AVG` over an empty set) — distinct from `0`.

### `PerspectiveSaveRequest`

```jsonc
{
  "Id":         null,                  // null = insert; long = update
  "ReportKey":  "flat_defects",
  "Name":       "Defect breakdown",
  "Scope":      "private",             // or "shared"; server demotes non-managers
  "IsDefault":  true,
  "ConfigJson": "{\"rows\":[…],\"cols\":[…],\"measures\":[…],\"drills\":[…],\"ignorePage\":false,\"renderer\":\"Table\",\"format\":\"auto\",\"hideZero\":true,\"topN\":null,\"chartMeasure\":\"\"}"
}
```
