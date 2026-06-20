# data-hub-pack

Portable, drop-in bundle that gives any ASP.NET Core .NET 9 + Dapper + SQL Server app a **Data Hub** (one filtered, flat, Excel-exportable row stream over a denormalised view) and a **Perspective Analyzer** sitting on top of it (server-side SQL pivot, drag-drop Rows / Cols / Values chip zones, drill-down, scope toggle, multi-measure, table + Plotly chart renderers, polished XLSX export, saved/shared/default perspectives per user).

Built once for [Sharbatly QMS](../) on a fruit-quality defects view; this pack is what you'd copy into a new project to skip rebuilding all of that from scratch.

---

## What you get

- **Saved perspectives** in `qms_perspective` — per-user private + Manager-shared, default-per-user, opaque JSON config that round-trips every UI choice.
- **Server-side pivot engine** — SQL `GROUP BY` against a whitelist of dimensions + measures (the *registry*); no user-supplied SQL ever reaches the database; arguments bind as parameters.
- **Drill-down** — multi-value picker per dimension + click-any-cell-to-drill on the result table, with a values picker fed by a cached `SELECT DISTINCT` endpoint.
- **Scope toggle** — analyzer respects the host page's filter card by default, or pivots over the whole dataset with one click; live breadcrumb shows what's in effect.
- **Multi-measure** — Excel-style Σ Values zone; nested column headers when 2+ measures; per-measure heat shading; chart-measure picker.
- **Polished XLSX export** — server-side ClosedXML build: title, scope/drill summary, merged headers, bold + frozen panes, per-measure number formats, auto-width.
- **Hide-zero rows, top-N cap, per-measure formatting**, etc.
- **No license cost, no internet dependency** — Plotly basic 2.35.2 is vendored (MIT) inside the pack.

## Reuse model

| Project | What it implements | What the pack provides |
|---|---|---|
| Sharbatly QMS (this repo) | `vw_qms_flat_defects` view + `FlatDefectFilter` + `IDataHubFilterAdapter` impl + `PivotRegistry.FlatDefects` | Everything else |
| Any future project | Their own flat view + filter shape + adapter + one `PivotReport` entry | Everything else |

## Quick install (TL;DR)

1. Copy `data-hub-pack/reference/` into your project, mirroring its folder tree.
2. Apply `reference/schema.sql` to your DB — it creates `qms_perspective`.
3. Adapt `reference/schema.sql`'s **template view** (`vw_yourdomain_flat`) — replace the columns with whatever you want to pivot over.
4. Implement `IDataHubFilterAdapter` against your host's filter type — see `examples/fruit-quality-defects-data-source.cs`.
5. Edit `reference/Services/PivotRegistry.cs` so its `FlatDefects` entry points at YOUR view name + dimensions + measures (or add a second `PivotReport` and leave the canonical one).
6. Merge `reference/Program.cs.snippet` into your `Program.cs` (`AddMemoryCache`, antiforgery `X-CSRF-TOKEN`, three `AddScoped` lines).
7. Use `reference/Controllers/DataHubController.snippet.cs` as the basis of your controller. Six JSON endpoints + the host preview/export actions are pre-wired.
8. Pull `reference/Views/_PerspectiveAnalyzer.cshtml` into `Views/Reports/` (or wherever).
9. Pull `reference/wwwroot/js/perspective-analyzer.js` + `reference/wwwroot/lib/plotly/plotly-basic.min.js` into `wwwroot/`.
10. In your host data-hub page, `@await Html.PartialAsync("_PerspectiveAnalyzer", new PerspectiveAnalyzerVm { ReportKey = "your_report", FilterQuery = Context.Request.QueryString.Value, CanShare = userIsManager })`. Done.

A future AI session can do steps 3–10 for you if you paste the contents of `INSTALL_PROMPT.md` into the chat and let it inventory your repo.

## Files at a glance

```
data-hub-pack/
├── README.md                                      this file
├── SPEC.md                                        stack-agnostic behavioural contract
├── INSTALL_PROMPT.md                              AI hand-off prompt for fresh sessions
├── How to use it for a new project.txt           human TL;DR
├── reference/
│   ├── schema.sql                                qms_perspective table + indexes + template view
│   ├── Models/
│   │   ├── PivotRequest.cs                       request body (Rows / Cols / Measures / Drills / Filter / IgnorePageFilter / TopN)
│   │   ├── PivotResult.cs                        response body (RowKeys / ColKeys / Cells / per-measure totals)
│   │   ├── Perspective.cs                        domain model — mirrors qms_perspective row
│   │   ├── PerspectiveDto.cs                     wire DTO returned to the UI
│   │   ├── PerspectiveAnalyzerVm.cs              view-model the partial expects
│   │   └── ExampleFilter.cs                      minimal filter shape the integrator adapts
│   ├── Services/
│   │   ├── IDataHubFilterAdapter.cs              THE PLUG-IN SEAM — host implements this
│   │   ├── DefaultDataHubFilterAdapter.cs        reference impl (emits no constraints)
│   │   ├── PivotRegistry.cs                      dimension + measure whitelist (the injection guardrail)
│   │   ├── IPivotService.cs / PivotService.cs    dynamic GROUP BY + distinct-values for the drill picker
│   │   └── IPerspectiveService.cs / PerspectiveService.cs   Dapper CRUD for saved perspectives
│   ├── Controllers/
│   │   └── DataHubController.snippet.cs          eight JSON endpoints + share-rights helper
│   ├── Views/
│   │   ├── _PerspectiveAnalyzer.cshtml           the analyzer card
│   │   └── _DataHub.cshtml.snippet               FlatDefects host-page template (filter form + preview table)
│   ├── wwwroot/
│   │   ├── js/perspective-analyzer.js            vanilla JS — chip zones, drill, scope, table+Plotly renderers, XLSX POST
│   │   └── lib/plotly/
│   │       ├── plotly-basic.min.js               vendored MIT 2.35.2
│   │       └── LICENSE.txt
│   ├── Program.cs.snippet                        DI + antiforgery config to merge
│   └── _Layout.cshtml.snippet                    script-section integration notes
└── examples/
    ├── README.md
    └── fruit-quality-defects-data-source.cs      canonical mapping: how Sharbatly QMS wired the pack
```

## License

MIT for the pack code. Plotly basic.min.js is also MIT (see `reference/wwwroot/lib/plotly/LICENSE.txt`). The pack carries no other third-party dependencies beyond what an ASP.NET Core 9 / Dapper / ClosedXML / Bootstrap 5 / jQuery 3 host already has.
