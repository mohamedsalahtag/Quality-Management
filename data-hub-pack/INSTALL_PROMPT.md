# INSTALL_PROMPT (paste into a fresh AI session)

You are about to wire the **data-hub-pack** into an existing project. Behave exactly as follows:

## Step 0 — read first

1. Read `data-hub-pack/SPEC.md` end-to-end. It is the contract.
2. Read `data-hub-pack/README.md` for the install TL;DR.
3. Read `data-hub-pack/examples/fruit-quality-defects-data-source.cs` to see one complete mapping.

Do not start writing code until you have done this.

## Step 1 — inventory the target project

Find out and write down:

- The web framework (ASP.NET Core version), DI container, ORM (Dapper / EF Core / hand-rolled), Razor or Blazor, MVC or Minimal APIs, target database.
- Whether `IMemoryCache` is registered. If not, the pack needs it.
- The antiforgery configuration. The pack assumes `AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN")`; if the host uses a different header name, plan to change either the host config OR the JS module's `X-CSRF-TOKEN` header — never both.
- The auth model. The pack reads `User.Identity.Name` (sAMAccountName) and `User.IsInRole(...)`. Tell the user if their host uses a different identity primitive.
- The existing CSS/JS stack — Bootstrap version, jQuery version, Bootstrap Icons. The analyzer partial uses BS5 + BI 1.x + jQuery 3.

## Step 2 — ask the user

Stop and ask **before designing the registration**:

1. What domain (and what SQL view) will the analyzer pivot on? If they don't have one yet, ask whether you should propose a denormalised view based on their schema.
2. Which dimensions and measures matter? Ask for a list of at least 5 dims and 2 measures — anything missing they can add later by editing `PivotRegistry.cs`.
3. Which roles are allowed to read (default `SupervisorOrAbove`) and which are allowed to save shared perspectives (default `Manager` or `SiteAdmin`)?
4. Does the host already have a filter type the analyzer should scope by? If yes, ask for that class. If no, you'll generate a minimal one from the dims the user picked.

## Step 3 — propose a plan (do not write code yet)

Produce a written plan that includes:

- **The host's `PivotReport` registration** — exact `Dimensions[]` + `Measures[]` based on the user's answers. Cite each dim's SQL expression by column name in the view.
- **The host's `IDataHubFilterAdapter` implementation** — for each filter slot, the SQL fragment + bound parameter.
- **The migration plan** — copy `reference/schema.sql` to the host's migration tool's "next slot" (V35, M0042, whatever the host calls it).
- **The view migration** — either confirm the host has a view, or draft one from their tables.
- **Files to copy** — exact tree under the host's repo root.
- **Files to merge** — exact lines to add to `Program.cs`, `_Layout.cshtml`, etc.
- **Verification steps** — start the app, browse to `/Reports/{HostHubAction}`, drag two dims, pick a measure, Run; click Export to Excel; save a perspective.

Wait for the user's approval. Do not start implementing until they say go.

## Step 4 — implement

Follow the plan you wrote. Order:

1. Apply `schema.sql` (table + view).
2. Copy `reference/Models/`, `reference/Services/`, `reference/Views/_PerspectiveAnalyzer.cshtml`, `reference/wwwroot/js/perspective-analyzer.js`, `reference/wwwroot/lib/plotly/*` into the host repo at matching paths.
3. Merge the `.snippet` files into their host counterparts (`Program.cs`, `_Layout.cshtml`, the host's report controller).
4. Edit `PivotRegistry.cs` so its sole entry matches the user's view + dims + measures. (Or add a second `PivotReport` and leave the canonical `flat_defects` entry as documentation.)
5. Implement `IDataHubFilterAdapter` against the host's filter type. Bind every value with `DynamicParameters`. No string concat of user input.
6. In the host's report page (the one that already has the filter card + flat preview), add:

   ```cshtml
   @await Html.PartialAsync("_PerspectiveAnalyzer", new PerspectiveAnalyzerVm {
       ReportKey   = "your_report_key",
       FilterQuery = Context.Request.QueryString.Value,
       CanShare    = User.IsInRole("Manager") || User.IsInRole("SiteAdmin")
   })
   <form id="perspectiveAfForm" method="post" class="d-none">@Html.AntiForgeryToken()</form>

   @section Scripts {
       <script src="~/lib/plotly/plotly-basic.min.js" asp-append-version="true"></script>
       <script src="~/js/perspective-analyzer.js" asp-append-version="true"></script>
   }
   ```

## Step 5 — verify and report

- Run the app. Open the host report page.
- Drag two dimensions to Rows, one to Cols, pick a measure + agg, Run. Numbers match a manual SUM against the same filter in Excel.
- Switch renderer to a chart. Plotly renders inline; no network requests outside the host LAN.
- Save a perspective. Reload. The perspective appears in the dropdown and loads back identically.
- Click Export to Excel. Open the file. Title, scope summary, merged headers when ≥2 measures, frozen panes, totals row, per-measure formats.

Report what worked, what didn't, and what migration / file changes you produced.

## Hard rules that you (the AI) must not violate

1. Do not bypass the registry. Every SQL identifier in the pivot or the drill comes from `PivotRegistry`. Do not let a host filter parameter touch a SELECT, GROUP BY, or column list.
2. Do not change the JSON casing on the wire. Respond as camelCase; the JS reads camelCase.
3. Do not skip the antiforgery header.
4. Do not write to the live database from a JSON POST without `[ValidateAntiForgeryToken]`.
5. Do not vendor Plotly from a CDN; use the file under `reference/wwwroot/lib/plotly/`.
