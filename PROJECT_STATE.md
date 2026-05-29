# Sharbatly QMS — Project State

> **For any AI agent (Claude Code, OpenAI Codex, Cursor, ChatGPT, GitHub Copilot, ...) picking up this project: read this file first.** It captures the live state of the QMS web application — what is built, where it runs, the decisions that shaped it, and the files that contain the authoritative truth.

Last updated: **2026-05-14** (Claim Management module).

---

## 1. What this project is

**Project name:** Sharbatly QMS — Quality Management System for **Sharbatly Fruit** (fruit & vegetable importer, Saudi Arabia).

**Purpose:** Internal web app that runs the import-quality inspection workflow. SAP S/4HANA handles purchasing/logistics/receiving; QMS is an **external execution layer** that reads SAP via OData (read-only) and owns every record from Customer Arrival forward in its own SQL Server database.

**Primary flow:** SAP shipment → Arrival Overview → Quality Order → Samples → Readings + Defects → PDF report.

**Authoritative spec:** `QMS_Extended_Assessment_and_Execution_Plan.md` (this repo root). 16-week phased build with normalized SQL schema and full architecture. Treat it as the source of truth for table names, status codes, role logic, and build-phase order — do not invent schema, follow §6.

**Owner / single user contact:** Mohamed Tag, IT Manager — `mohamed.tag@sharbatlyfruit.com`. Non-technical; prefers defaults; wants outcomes framed in business terms.

---

## 2. Where everything lives

| Concern | Path |
| --- | --- |
| Working repository root | `C:\QualityManagemet\` (note the typo — the folder name is **QualityManagemet**, not QualityManagement) |
| Web app source | `app\SharbatlyQMS.Web\` |
| DB migration tool source | `app\SharbatlyQMS.Migrate\` |
| Solution file | `app\SharbatlyQMS.slnx` |
| SQL migration files | `app\db\V*.sql` (versioned, run by the migrate tool) |
| Reusable packs | `alert-pack\`, `email-notification-pack\`, `theme-pack\`, `user-management-pack\`, `view-as-pack\` |
| Production deployment | `deploy\SharbatlyQMS\` (published Release build) |
| Deployment scripts | `deploy\Install-...ps1`, `Uninstall-...ps1`, `Republish.ps1`, `README.txt` |
| Master plan | `QMS_Extended_Assessment_and_Execution_Plan.md` |
| Behavioral guides for AI | `CLAUDE.md`, `AGENTS.md` (both point here) |

---

## 3. Stack (locked decisions)

- **.NET 9** / ASP.NET Core MVC monolith
- **Razor** views + **Bootstrap 5.3** (no SPA framework)
- **Dapper** for data access (no EF Core)
- **SQL Server** as the data store
- **System.DirectoryServices** for AD bind (Windows-only — the host runs Windows)
- **QuestPDF** (community license) for PDF reports
- **MailKit** for SMTP
- **SixLabors.ImageSharp** for thumbnails
- **BCrypt.Net-Next** as the local-password fallback
- **Microsoft.Extensions.Hosting.WindowsServices** so the app runs as a Windows Service in production

Reason for the stack: all four reusable packs already target this exact combination, so no rework was needed.

---

## 4. Database

- **Server:** `192.168.3.10` (SQL Server on the office LAN)
- **Database:** `SharbatlyQMS`
- **Login:** `linkserver` / `P@ssw0rd`
- **Connection string** (in `appsettings.json`):
  ```
  Server=192.168.3.10;Database=SharbatlyQMS;User ID=linkserver;Password=P@ssw0rd;
  TrustServerCertificate=True;Persist Security Info=True;Connect Timeout=15;
  ConnectRetryCount=5;ConnectRetryInterval=3;Pooling=True;Min Pool Size=1;Max Pool Size=200
  ```
- **Migrations** live in `app\db\V01__pack_schema.sql` … `V12__role_overhaul.sql` and are applied by the `SharbatlyQMS.Migrate` console tool.
- **`verify.sql`** is a one-shot diagnostic query, not a migration.

---

## 5. Production deployment (LIVE)

- **Host:** This same Windows 11 Pro PC (the dev box). One-machine deployment for v1.
- **Windows Service:** `SharbatlyQMS` — Automatic startup, auto-restart on failure (5 s / 5 s / 10 s).
- **Binary:** `C:\QualityManagemet\deploy\SharbatlyQMS\SharbatlyQMS.Web.exe`
- **URL binding (Kestrel):** `http://0.0.0.0:5244` (set via `appsettings.Production.json`)
- **Firewall:** Windows Defender Firewall rule "SharbatlyQMS HTTP 5244" — Inbound, TCP 5244, Domain + Private profiles.
- **LAN URL for end users:** **`http://192.168.3.192:5244`** (192.168.3.x subnet — same network as the SQL server).
- **Local URL on this PC:** `http://localhost:5244`.

### Operating the service (any elevated PowerShell)

```powershell
Get-Service     SharbatlyQMS          # status
Start-Service   SharbatlyQMS
Stop-Service    SharbatlyQMS
Restart-Service SharbatlyQMS
```

### Redeploying after code changes

```powershell
& 'C:\QualityManagemet\deploy\Republish.ps1'
```

It stops the service (UAC prompt), runs `dotnet publish -c Release -o ...\deploy\SharbatlyQMS`, then starts the service back up. Total downtime is a few seconds.

### Logs

Event Viewer → Windows Logs → Application, filter by `Source = SharbatlyQMS`.

### Removing the deployment

`deploy\Uninstall-SharbatlyQMSService.ps1` (admin). Removes the service + firewall rule but leaves the published files in place.

---

## 6. Authentication & users

- **Active Directory** is the primary login mechanism. Domain: `sharbatlyfruit.com`. AD config lives in the `SiteConfiguration` table (Domain, LdapPath, ServiceUser, ServicePassword, AutoCreateOnLogin).
- **AD bind-only at login.** Never issue an LDAP search during the login POST — referrals can time out and kill the page. `AdService.AuthenticateAsync` tries UPN then NetBIOS bind formats; the profile-enrichment search runs only **after** a successful bind and any failure there is non-fatal.
- **Wrong-password from AD is definitive** — error codes `0x8007052E` / `0x80070005` stop the loop and return `"Invalid credentials"`. No other bind formats are tried.
- **BCrypt local fallback** still exists in `AccountController.Login` for any pre-AD user (e.g., the original `admin` row). The "seed admin re-hash" path and the `/Account/Setup` bootstrap endpoint were both **removed on 2026-05-13** — they were a back-door once AD started working.
- **Auto-create on login:** when `AdConfig.AutoCreateOnLogin = true`, a successful AD bind for an unknown user creates a local row with role `Viewer`. SiteAdmin promotes from there.
- **Username normalization (2026-05-14):** Both `mohamed.tag` and `mohamed.tag@sharbatlyfruit.com` now map to the same local row — `Login()` strips everything from `@` onwards and uses the `sAMAccountName` returned by AD as the canonical DB key.

### Roles (current — `UserRoles.All`)

Low → high (array order is cosmetic; `ClaimManager` is a **peer** of `Manager`, not above it):

| Role | Capability |
| --- | --- |
| `Viewer` | Read-only |
| `Operator` | Operational mutations |
| `Manager` | Everything except admin pages (Quality Manager — also the QM in Claim Management) |
| `ClaimManager` | Read-only on operational pages; full access to Claim Management (Approve / Hold) |
| `SiteAdmin` | Everything including Users / Site Configuration; acts as both QM and CM |

### Authorization policies (`AuthPolicies.*`)

- `AdminOnly` → `SiteAdmin`
- `ManagerOrAdmin` → `Manager`, `SiteAdmin`
- `OperatorOrAbove` → `Operator`, `Manager`, `SiteAdmin`
- `ClaimManagerOrAdmin` → `ClaimManager`, `SiteAdmin` (Approve / Hold actions)

### How AdminController is gated (important — easy to break)

- **Class-level attribute** is `[Authorize(Policy = ManagerOrAdmin)]` (the loosest gate any action there needs).
- **Every SiteAdmin-only action** adds an explicit `[Authorize(Policy = AdminOnly)]`. ASP.NET Core AND-combines them, lifting those actions to SiteAdmin.
- The Parameters-menu actions (`DefectCatalog`, `SaveDefect`, `ReadingTypes`, `SaveReadingType`, `MailTemplate`, `SaveMailTemplate`) deliberately rely on the class-level gate so Manager users can reach them.
- **When adding a new action to `AdminController`, you MUST add `[Authorize(Policy = AdminOnly)]` if it should stay SiteAdmin-only.** Forgetting silently grants Manager access.

### View site as — role impersonation (2026-05-13)

A real SiteAdmin can swap their effective `Role` claim to any lower role without re-logging-in. Useful for testing security gates.

- **Cookie:** `SharbatlyQMS.ViewAs` — HttpOnly, SameSite=Lax, 8-hour MaxAge, just stores the role string.
- **Claims swap:** `Services/ViewAsClaimsTransformer.cs` (an `IClaimsTransformation`). Replaces the `Role` claim on every authenticated request and preserves the real role in an `OriginalRole` claim.
- **Endpoint:** `POST /Account/ViewAs` with `{ role, returnUrl }`.
- **UI:** Top-nav dropdown labelled `View as: <currentRole>` (incognito icon) — visible only to real SiteAdmins. Yellow banner sits under the navbar while impersonating, with a one-click "Become self" button.
- **Security:** The cookie is honored **only** if the underlying user's real `Role` is SiteAdmin. A non-admin who forges it gets no elevation. Source of truth: `SPEC.md §6` of the user-management-pack.

---

## 7. Reusable packs (alongside the app)

Each pack has its own `README.md`, `SPEC.md`, `INSTALL_PROMPT.md`, `reference/` (real code), and `examples/`. Read `SPEC.md` before adapting.

- **`alert-pack/`** — hourly scheduled alert engine. Severity-coded HTML emails. Plug-in seam: `IAlertDataSource.CollectMatchesAsync(rule)`. Depends on `email-notification-pack` and `user-management-pack`.
- **`email-notification-pack/`** — MailKit-based `IEmailService`, per-group SMTP overrides, fire-and-forget background sending, admin Site-Config UI for SMTP, test-email action.
- **`theme-pack/`** — 6 Bootstrap 5.3 themes (light/dark/blue/sepia/forest/rose). FOUC-free, localStorage persistence, navbar dropdown picker. Pure CSS+JS, no backend.
- **`user-management-pack/`** — full user / role / group system. AD bind, AD browse, mass-create, auto-create-on-login, profile pictures, bulk delete, group memberships with permission flags. Pair with `view-as-pack/` for role-impersonation.
- **`view-as-pack/`** — SiteAdmin role-impersonation ("View as: {role}"). Drop-in cookie + `IClaimsTransformation` pair that lets a SiteAdmin temporarily browse the site as any lower role without re-logging-in, with a yellow banner while active. Depends on the host project already having cookie auth + antiforgery + persistent data-protection + a roles enumeration (all provided by `user-management-pack/`).

When extending a QMS feature whose scope overlaps a pack, copy from the pack's `reference/*.cs` and adapt — do not rewrite from scratch. Pack reference names are HelpDesk/Fleet-flavoured; rename to QMS terms.

---

## 8. Decisions log (newest first)

### 2026-05-29 (Material details copied onto each sample; read-only "inherited" recaps removed)
- **What changed.** The Material-scoped header values + sample_size entered on the QO "Material details" panel are now **copied down onto every sample** (each sample owns its own `qms_sample_header_value` rows), instead of living only on the material and shown via read-only "inherited" recaps. Both recap displays are **removed**: the QO-page material-card "Material details (inherited by every sample)" block and the sample-form "From material" strip. Requested by Mohamed (screenshot).
- **Why / decisions.** Confirmed via AskUserQuestion: remove **both** recaps; **copy onto each sample** (new + already-created). The data must reflect on each sample and in the PDF.
- **How it works.** `SaveMaterialHeaderValuesAndSizeAsync` now, in the same tx, wipes the Material-scoped rows on all of the material's samples and re-inserts them from `qms_qo_material_header_value` (sample_size propagation unchanged). `CreateSampleAsync` copies the material's header values onto the new sample. **`SaveSampleHeaderValuesAsync` now deletes only `scope='Sample'` rows** (was: all rows) so a per-sample save can't wipe the copied Material-scoped values. `qms_qo_material_header_value` remains the master store (modal pre-fill + copy-down source). **How to apply:** Material-scoped values now live on the sample; read them from `qms_sample_header_value` (joined to the field catalog returns both scopes).
- **PDF.** The per-sample card no longer merges `MaterialHeaderValues` separately — the sample's own `HeaderValues` (from `GetSampleHeaderValuesBatchAsync`) already include the copied Material-scoped fields, so a single loop prints both (no duplication). `ReportsController` no longer fetches material header values for the PDF.
- **Backfill.** `app/db/V23__copy_material_header_to_samples.sql` (idempotent: delete `scope='Material'` rows on all samples, re-insert from each sample's material) backfilled **existing** samples so they reflect the data immediately without a re-save. Applied + re-applied to prod (53 values copied).
- **Cleanup.** Removed the now-dead `ViewBag.MaterialHeaderValues` (Details + Sample GET), the `SampleFormVm.MaterialHeaderValues` population (SamplePanel/Sample.cshtml), and the `mat-details-block` refresh JS. (Unused properties `SampleFormVm.MaterialHeaderValues` / `SampleBundle.MaterialHeaderValues` / `GetMaterialHeaderValuesBatchAsync` left in place — harmless.) Note: after saving Material details there's now **no on-page recap**; a toast confirms "applied to all samples", and the values surface in the PDF (the QO sample-rows table still shows the frozen legacy Grower/Pallet/Lot/Date-code columns — pre-existing, untouched).
- **Verified.** Full `dotnet build` 0/0; V23 applied idempotently. **Republished to prod 2026-05-29 ~21:39Z.** End-to-end UI check (save material details, confirm values land on existing + new samples and in the PDF, with no recap boxes) is the next manual step.

### 2026-05-29 (Defect categories are now fully dynamic — admin-managed master list, one section per category)
- **What changed.** Defect categories are no longer a hardcoded 4-value CHECK collapsed into two fixed Major/Minor buckets. New master table **`qms_defect_category`** (name, sort_order, **color_hex**, is_active) managed on a new **Admin → Defect Categories** page. The Defect Catalog category dropdown is populated from it. The sample form and the QO PDF now render **one section per category**, ordered by `sort_order` and coloured by `color_hex`. Adding a category (e.g. "Progressive") and assigning defects makes a new section appear in both places automatically.
- **Why.** Requested by Mohamed — categories should be definable, not fixed. Confirmed via AskUserQuestion: each category its own section (**Critical is no longer merged into Major**), admin-chosen colour per category, one global list.
- **Key design.** `qms_defect_catalog.defect_category` stays the VARCHAR **name** (the de-facto key everywhere) with a **FK → `qms_defect_category(category_name)` `ON UPDATE CASCADE`** (renames propagate; existing string-keyed read paths unchanged). Old `CK_qms_defect_catalog_category` dropped. Delete of an in-use category is blocked at the app level. The 4 existing categories are seeded with colours so nothing changes visually until new ones are added — **except** Critical now renders as its own section instead of inside Major.
- **Implementation.** `app/db/V22__defect_categories.sql` (table + seed + orphan backfill + drop CHECK + FK; idempotent; applied + re-applied to prod). Models: `DefectCategory`, `DefectCategorySection`; `MaterialGroupSummary.MajorDefects/MinorDefects/MajorTotalPct/MinorTotalPct` → `List<DefectSections>`; deleted dead `SampleBundle.MajorDefectTotal/MinorDefectTotal`. `QualityOrderService`: `GetActiveCategoriesAsync`; `GetDisplaySectionMapAsync` now returns defect_code→real category name (no Major/Minor collapse); `BuildGroupSummariesAsync` groups into `DefectSections` ordered by category. `ICatalogCache`/`CatalogCache`: cached `GetActiveCategoriesAsync` + `Invalidate()` drops it. `AdminController`: `DefectCategories`/`SaveDefectCategory`/`DeleteDefectCategory` (inline SQL, `ManagerOrAdmin`); `SaveDefect` validates category against the active list; `DefectCatalog` GET exposes `ViewBag.Categories`. Views: new `DefectCategories.cshtml` (colour picker); `DefectCatalog.cshtml` dropdown+badge from the list; `_Layout.cshtml` menu item; `_SampleForm.cshtml` dynamic per-category sections (colour inline; `SampleFormVm.Categories`); `Sample.cshtml`/`SamplePanel`/`Sample` GET pass Categories. PDF (`QualityReportData`/`QualityReportPdf`/`ReportsController`): `Categories` on the data bundle; `TintHex` + `FallbackPalette` colour helpers; grouped summary + per-sample card loop `DefectSections`; `RenderGroupDefectGrid` lost its `isMajor` param.
- **Behaviour change to note.** Any QO with Critical defects now shows a separate "CRITICAL DEFECTS" section (own Σ%) instead of folding into Major — intended.
- **Verified.** Full `dotnet build` 0/0; V22 applied idempotently (4 categories). **Republished to prod 2026-05-29 ~21:11Z.** End-to-end UI check (define a category, assign a defect, confirm form + PDF sections + colour) is the next manual step.

### 2026-05-29 (Sample-header fields can be Material-scoped; entered once per material, inherited by samples)
- **What changed.** Header fields in the Sample Header catalog (`qms_sample_header_field`) now have a **`scope`** (`Sample` | `Material`). Material-scoped fields are entered **once per QO material** on a new **"Material details"** panel (button on each material card on the QO Details page) and **inherited by every sample** — they are no longer entered per sample. Defaulted to Material scope: **Grower Pallet, Pack Code, Date Code, Label**, plus a new **Label Number** field. **Sample Size** also moved to the material level (entered on the same panel).
- **Why.** Requested by Mohamed: these identify the material, not the individual sample, so re-typing per sample was wasteful. Reuses the existing V20 dynamic-field system rather than a parallel mechanism. Confirmed via AskUserQuestion: admin-configurable scope; Label + Label Number are two fields; migrate existing QOs; per-material panel UX. Plan: `~/.claude/plans/in-quality-order-all-jolly-trinket.md`.
- **Key design — sample_size stays the defect-% denominator.** `qms_quality_order_material.sample_size` is the source of truth; `qms_sample.sample_size` is kept as an **inherited cache** (copied on sample create; propagated to all of a material's samples on every material save). So `BuildGroupSummariesAsync` (Σ sample_size, `sum_over_size`), the PDF (per-sample % + grouped Σ), and the live-% JS all keep working untouched. `UpdateSampleAsync` no longer writes sample_size (would blank the cache); `SaveSampleCoreAsync` takes the denominator from the material. **How to apply:** never collect sample_size or Material-scoped fields on the per-sample form; they belong to the material.
- **Schema — `app/db/V21__material_level_header_fields.sql`** (idempotent; applied + re-applied to prod 2026-05-29): `scope` column + CHECK on `qms_sample_header_field`; seed `LABEL_NUMBER`; `sample_size` on `qms_quality_order_material`; new `qms_qo_material_header_value` (qo_material_id+field_id, FK cascade) mirroring `qms_sample_header_value`; **existing-QO roll-up** = each material takes the value from its **lowest-numbered sample** (header values + sample_size), then sample_size propagated back to all samples.
- **Implementation.** `MaterialHeaderValue` model; `QualityOrderMaterial.SampleSize`; `SampleHeaderField.Scope`. Service: `GetMaterialHeaderValuesAsync`/`…BatchAsync`, `SaveMaterialHeaderValuesAndSizeAsync` (updates material size, propagates to samples, replaces header values, audited), `GetQoIdForMaterialAsync`; `GetActiveSampleHeaderFieldsAsync` + `GetMaterialsAsync` select the new columns; `CreateSampleAsync` inherits size. Controller: `MaterialPanel` GET + `SaveMaterialHeaderAjax` POST; sample form / save filtered to `Scope=="Sample"`; Details exposes `ViewBag.MaterialHeaderValues`. Views: new `_MaterialForm.cshtml` + `materialDetailsModal` on Details (lazy-load + AJAX save, in-place card update); `_SampleForm` drops the Sample Size input, shows a read-only "From material" strip, and carries `data-sample-size` for the defect-% JS; `Sample.cshtml` JS reads `data-sample-size`. Admin Sample Headers page gains a Scope column + select. PDF: `SampleBundle.MaterialHeaderValues` merged into the per-sample header cells (`ReportsController` batches them by material).
- **Left as-is (pre-existing).** `_SampleRow.cshtml` Grower/Pallet/Lot/Date-code columns still read the frozen legacy `qms_sample.*` columns (stale since V20) — not regressed by this change; the authoritative material values now show in the material card body. The legacy `qms_sample` identification columns remain (vestigial).
- **Verified.** Full `dotnet build` 0/0; V21 applied idempotently (5 Material-scoped fields). **Republished to prod 2026-05-29 ~20:36Z.** End-to-end UI check (enter material details, add a sample, confirm inheritance + defect-% + PDF) is the next manual step.

### 2026-05-29 (Quality Order material size now reads SizeID, not Size_Name)
- **What changed.** `MaraService.LookupAsync` now sources `MaterialSize` from the MARA cache column **`size_id`** (CDS `SizeID`) instead of `size_name` (CDS `Size_Name`); the `material_weight_name` fallback is unchanged. Requested by Mohamed.
- **How to apply / blast radius.** `MaterialSize` flows into every consumer of `MaraMaterial` via `ApplyMara` — i.e. the **Quality Order Details** screen and the **Quality Report PDF**. Manual size overrides still win (`SizeOverridden` guard in `ApplyMara`). One-line change in `Services/MaraService.cs`; no schema change (both `size_id` and `size_name` already exist on `qms_sap_material_cache` from V07).
- **Verified.** `dotnet build` → 0 warnings, 0 errors. Republished to the live service.

### 2026-05-29 (Carrier from CDS + lock SAP-sourced fields read-only; Materials tab Variety/Class + group code)
- **What changed.**
  - **Carrier Name** (checklist tab identity strip) now reads from the SAP CDS field **`Carrier`** and is **read-only**. Previously `carrier_name` was seeded from the *vendor name* at creation; now it comes from `Carrier`.
  - The following **SAP-sourced shipment fields are now read-only** (disabled inputs, locked to the creation snapshot): **Vessel name, Voyage number, Loading port, Country of origin, Loading date (backed by `sailing_date`), Examination date, Arrival date, Transit days** — joining the already-locked **Inspection date** and **Receive date**. Still editable (inspector-entered): Unloading date, Pull-out date, Time bar (days), Arrival port, Inspection point, Joint Survey, Time Bar Exceeded.
  - **Materials tab** now shows **Variety** and **Class** (read live from the MARA cache, not snapshotted on `qms_arrival_item`), and the **Material group** column shows the **code only** (`material_group`) instead of `code — name`.
- **Why.** Requested by Mohamed: SAP/CDS is the source of truth for these fields, so they shouldn't be editable; and the Materials tab needed variety/class plus the group code.
- **How it works / how to apply.** Read-only enforcement follows the established pattern: the disabled inputs carry **no `name`** (nothing posts) AND the fields are **excluded from the `SaveShipmentAsync` / `SaveChecklistAsync` UPDATE + audit**, so a user can't change them and saves can't null them or log phantom diffs. To make any of them editable again, re-add a `name`, the `SET` clause, and the audit key. After this change `SaveShipmentAsync` writes only: unloading_date, time_bar, arrival_place, pullout_date, time_bar_exceeded, inspection_point, joint_survey. `SaveChecklistAsync` no longer touches carrier_name. Materials Variety/Class are enriched in `ArrivalsController.Details` via `IMaraService.LookupAsync(materialNos)` onto new display-only `ArrivalItem.Variety`/`ArrivalItem.MaterialClass` (not persisted).
- **Implementation.** `SapShipmentRow.Carrier` added; `HybridSapClient.MapRow` maps `Get(d,"Carrier")`; stub rows given a `Carrier`. `CreateFromSapAsync` checklist INSERT seeds `carrier_name` from `first.Carrier`. `ArrivalService.SaveChecklistAsync` / `SaveShipmentAsync` UPDATE+audit trimmed (see above). `Models/Arrival.cs` `ArrivalItem` gains display-only `Variety`/`MaterialClass`. `Controllers/ArrivalsController.cs` injects `IMaraService` and enriches items in `Details`. `Views/Arrivals/Details.cshtml`: carrier + the 8 shipment fields disabled; Materials tab adds Variety/Class columns and shows group code only. **No DB migration.**
- **Data caveat (pre-existing arrivals).** Old arrivals have `carrier_name` = the vendor name (the previous seed); since the field is now read-only and `SaveChecklistAsync` no longer writes it, those won't change. New arrivals get the CDS `Carrier`. Variety/Class on the Materials tab depend on the material being present in `qms_sap_material_cache` (material-master sync); missing rows render blank. The `Carrier` CDS alias is assumed to be exactly `"Carrier"` — adjust `HybridSapClient.MapRow` if the real ZQC_Data column differs.
- **Verified.** `dotnet build` → 0 warnings, 0 errors. Not yet republished. **Rides along with the two prior 2026-05-29 changes that are also un-deployed** (Container×BOL×PO duplicate-disable was deployed at 18:13Z, but the shipment date-fields change and this one are not) — one `Republish.ps1` will take all pending changes live. End-to-end UI check is the next manual step.

### 2026-05-29 (Shipment-tab date fields: Loading/Sailing relabel, Receive from CDS, Inspection = creation date)
- **What changed (Shipment tab of `/Arrivals/Details`).**
  - **Removed** the "Loading date" field. **Renamed** "Sailing date" → **"Loading date"** — the on-screen "Loading date" is now backed by `sailing_date` (CDS `Sailing_Date`). `loading_date` (CDS `LoadingDate`) is no longer surfaced or written anywhere.
  - **Receive date** is now read from the CDS field **`Receive_Date`** and is **read-only** (disabled input, "From SAP" hint).
  - **Inspection date** is now the **arrival/checklist creation date**, **read-only** (disabled input, "Auto — arrival creation date" hint).
  - **Quality Report PDF**: "Sailing Date" label renamed to "Loading Date" to match (confirmed via AskUserQuestion). Receive/Inspection PDF fields unchanged in code (reflect the new stored values).
- **Why.** Requested by Mohamed. Confirmed choices: update the PDF too; Receive Date read-only (CDS is source of truth); Inspection Date read-only (= creation date).
- **How it works / how to apply.** `receive_date` and `inspection_date` are now **system-owned**: set once in `CreateFromSapAsync` (`receive_date` = SAP `Receive_Date`; `inspection_date` = `CAST(SYSUTCDATETIME() AS date)`) and **deliberately excluded from the `SaveShipmentAsync` UPDATE** so a user can't change them and saves can't null them. If you ever need them editable again, re-add them to that UPDATE. The two disabled inputs intentionally carry **no `name`** (nothing to post — the server ignores them). `loading_date` was dropped from both the create INSERT and the SaveShipment UPDATE/audit (it would otherwise be nulled + logged as a phantom diff on every save now that its input is gone).
- **Implementation.** `SapShipmentRow.ReceiveDate` added; `HybridSapClient.MapRow` maps `Get(d,"Receive_Date")`; `StubSapClient` sample rows given a `ReceiveDate`. `ArrivalService.CreateFromSapAsync` snapshot INSERT now writes `receive_date` + `inspection_date` (and no longer `loading_date`). `ArrivalService.SaveShipmentAsync` UPDATE/audit no longer touch `loading_date`, `receive_date`, `inspection_date`. `Views/Arrivals/Details.cshtml` Shipment tab updated. `Services/Pdf/QualityReportPdf.cs` label rename. **No DB migration** — all columns already exist (`qms_shipment_snapshot` in V02); `loading_date` is left in the schema, now vestigial.
- **Data caveat (pre-existing arrivals).** Arrivals created *before* this change have `receive_date`/`inspection_date` = NULL (or whatever a user had typed). Since `SaveShipmentAsync` no longer writes them, they won't backfill: on screen the Inspection date falls back to display the arrival's CreatedAt, but the stored value stays NULL, so the PDF's existing null-fallbacks apply (Inspection → report-generation date; Receive → blank). New arrivals are fully populated. Backfilling history was out of scope.
- **Verified.** `dotnet build` → 0 warnings, 0 errors. Not yet republished — needs `Republish.ps1` to go live (running service is a precompiled publish). End-to-end UI check (create a fresh arrival, confirm Loading=Sailing value, Receive from SAP locked, Inspection = today locked; regenerate a QO PDF to confirm "Loading Date") is the next manual step.

### 2026-05-29 (Arrival identity changed to Container × BOL × PO; duplicate Create disabled in SAP search)
- **What changed.** An arrival's identity is now **(Container + BOL + PO/EBELN)** instead of the previous (Container + BOL). On the SAP search grid (`/Arrivals/Search`), result cards are grouped per PO, and a card's **Create arrival** button is **disabled** (with an inline "Already created — open ARR-…" link to the existing arrival) when an arrival already exists for that exact triple.
- **Why.** The same container number legitimately recurs under different BOLs or POs; the old (Container+BOL) dedup blocked or merged shipments that were actually distinct. Requested by Mohamed: don't let a duplicate be created, but key it on container **and** BOL **and** PO. Confirmed via AskUserQuestion: "Split per PO" + "Disable + link to existing".
- **How to apply.** Treat one container+BOL+PO as one arrival. When adding any arrival-creation/dedup logic, key on all three. The shared key helper is `ArrivalsController.ShipmentKey(container, bol, po)` (case-insensitive, `UPPER`).
- **Implementation.**
  - `IArrivalService` / `ArrivalService`: `FindByContainerAndBolAsync` → **`FindByShipmentAsync(container, bol, po)`** (now also filters `ebeln`; switched to `QueryFirstOrDefault` so a historical duplicate triple can't throw). New **`FindByContainersAsync(containers)`** bulk-loads arrivals for the search result's containers so the grid can pre-flag them in one query. `CreateFromSapAsync` row-consistency guard now also requires identical `Ebeln`.
  - `ArrivalsController`: `Search` collects distinct containers, loads existing arrivals, and exposes `ViewBag.ExistingByShipment` (a `Dictionary<string,Arrival>` keyed by `ShipmentKey`). `Create` now takes `po`, dedups on the triple, filters SAP rows by container+BOL+PO, and the duplicate modal/TempData carries `DuplicatePo`. New `public static ShipmentKey(...)` shared with the view.
  - `Views/Arrivals/Search.cshtml`: groups SAP rows by `{ContainerNo, BolNo, Ebeln}`; card header shows PO; per-card disabled button + "open existing" link via `ExistingByShipment`; duplicate modal gained a PO row and "container × BOL × PO" wording.
- **Data caveat (pre-existing multi-PO arrivals).** Arrivals created under the **old** model bundled *all* PO lines for a container+BOL but stored only the **first** line's PO in `qms_arrival.ebeln`. Under the new model such an arrival only "covers"/blocks its header PO; the other POs from that same container+BOL will now appear as separate, creatable cards. If production so far only ever had one PO per container+BOL this is a non-issue; otherwise a one-off reconciliation may be wanted. **No DB uniqueness constraint was added** (dedup stays app-level, matching the prior approach) — a `UNIQUE(container_no, bol_no, ebeln)` index is a possible follow-up.
- **Verified / deployed.** `dotnet build` → 0 warnings, 0 errors. **Republished to the live `SharbatlyQMS` service on 2026-05-29 ~18:13Z** (DLL rebuilt; service restarted; `http://localhost:5244/` → 302). End-to-end UI check (search a container, confirm per-PO cards + disabled button on an already-created PO) is the next manual step. **Known follow-up if a draft still shows enabled:** because every *pre-change* arrival stored only the first PO of its container+BOL in `qms_arrival.ebeln`, a multi-PO old arrival only blocks its header PO. The robust fix is to match the searched PO against the arrival's **item lines** (`qms_arrival_item.ebeln`) instead of just the header — covers every PO an old arrival bundled.

### 2026-05-29 (Arrival Images tab — add/remove without losing the tab)
- **Problem.** On `/Arrivals/Details`, uploading or deleting an image POSTed to `ImagesController` which redirected back via `returnUrl`. The full-page reload reset the Bootstrap tabs to the default (`#tab-checklist`, which is hardcoded `show active`), so the inspector was bounced off the Images tab on every add/remove.
- **Fix.** Image add/remove on the Arrival page now submits via AJAX (`fetch`, no reload) and re-renders only the gallery grid in place, so the Images tab stays active.
- **Opt-in, surgical.** Added `ImageGalleryVm.Ajax` (default `false`). Only the Arrival gallery sets `Ajax = true`; the QualityOrder material galleries (same shared partial) keep their original POST→redirect behaviour unchanged.
- **How it works.** `_ImageGallery.cshtml` wraps the upload form + grid in `.image-gallery[data-ajax]` and emits a once-registered, document-delegated submit handler (vanilla JS, no jQuery) that intercepts forms only inside an `data-ajax="true"` gallery. The grid markup moved to a new `_ImageGalleryGrid.cshtml` partial so the server can re-render just the grid. `ImagesController.Upload`/`Delete` detect `X-Requested-With: XMLHttpRequest` and return `PartialView("_ImageGalleryGrid", …)` (Editable=true — the controls are only reachable from a draft gallery) instead of redirecting. The delete form now carries hidden `ownerType`/`ownerId` so the grid can be rebuilt. Native `required` validation and the delete `confirm()` still work (handler bails on `e.defaultPrevented`).
- **Files touched.** NEW: `Views/Shared/_ImageGalleryGrid.cshtml`. MODIFIED: `Views/Shared/_ImageGallery.cshtml`, `ViewModels/ImageGalleryVm.cs` (+`Ajax`), `Controllers/ImagesController.cs` (`IsAjax`/`GridPartial`; `Delete` gains `ownerType`/`ownerId`), `Views/Arrivals/Details.cshtml` (`Ajax = true`).
- **Verified / deployed.** `dotnet build` → 0 warnings, 0 errors. **Republished to the live `SharbatlyQMS` service on 2026-05-29** (`deploy\SharbatlyQMS\SharbatlyQMS.Web.dll` rebuilt; service restarted; `http://localhost:5244/` → 302 `/Account/Login`). Note: the running service is a precompiled Release publish — `.cshtml`/`.cs` edits do **not** go live until `Republish.ps1` runs, even if `dotnet build` succeeds. End-to-end UI check (upload/delete on a draft arrival staying on the Images tab) is the next manual step.

### 2026-05-23 (Quality Order PDF — grouped summary by Brand/Variety/Grade)
- **What changed.** The Quality Order PDF's page-1 per-sample summary blocks are replaced by a single grouped-summary block per `(material_group, Brand, Variety, Grade)` tuple. Each group rolls up every contributing material's sample data:
  - `Sample Size` = Σ `qms_sample.sample_size` across the group's samples.
  - `Gross Weight` = Σ over materials of (`qms_arrival_item.quantity` × MARA `Weight`). MARA weight comes live through `MaraService.LookupAsync` (no snapshot duplication).
  - `TARA Weight` = Σ `numeric_value` of the `TARA` reading across the group's samples.
  - `Net Weight` = `Gross − TARA` (derived; no schema column).
  - **All** active defects from `qms_defect_catalog` for the group's `material_group` render — zero values included — so a Closed QO with no recorded Crack still shows "Crack 0.00%". Per-defect `%` = `Σ defect_value / Σ sample_size × 100`. Bucketed Major (`Major` + `Critical`) / Minor (everything else) — matches the existing `_SampleForm.cshtml` convention. `Major Defects (Σ %)` / `Minor Defects (Σ %)` are arithmetic sums of the per-defect percentages in each bucket.
  - Readings render per the new `display_mode` (see below); rows with `formula` are skipped until the formula language is designed.
- **Page 2 unchanged.** Per-sample detail cards still appear so the auditor can see the raw per-sample readings + defects that feed the page-1 totals. The page-1 swap removes only the per-sample summary blocks (now redundant with the group summary).
- **New `display_mode` field on `qms_reading_type`** (`V18__reading_type_display_mode.sql`). Five values: `text` (distinct text joined), `count` (sample-with-value count), `sum` (Σ numeric), `sum_over_size` (Σ value ÷ Σ sample_size × 100, rendered as %), `formula` (placeholder; em-dash for now). Migration backfills existing rows by `value_kind` (Text→`text`, Numeric→`sum`), adds a CHECK constraint, a DEFAULT `'sum'` (so a pre-deploy INSERT from the old service can't fail), then promotes to NOT NULL. Idempotent on re-run. Admin UI: `/Admin/ReadingTypes` modal has a new "PDF summary mode" dropdown + the table shows the active mode as a badge; switching `Value kind` Numeric↔Text in the modal auto-syncs the default mode (one-way hint, user can override).
- **Reading-type usage** elsewhere is unaffected. `display_mode` was added to both `GetActiveReadingTypesAsync` / `…ForGroupAsync` SELECTs so any future caller that reads it gets the actual value, but no existing consumer cares about it today.
- **Read-through, no new snapshot.** Grouping reads Brand/Variety/Grade/Weight from the already-MARA-enriched `QualityOrderMaterial` (via `MaraService.LookupAsync` + the existing `ApplyMara` extension) — the report doesn't duplicate that data into `qms_quality_order_material`. **Deferred follow-up:** the snapshot columns `brand`, `variety`, `material_class`, `net_weight`, `material_size`, `pack_type`, `pack_code` on `qms_quality_order_material` are now mostly redundant (live-replaced by `ApplyMara` at view-time). A column cleanup is its own migration with its own blast-radius testing; intentionally not bundled here per the CLAUDE.md simplicity rule.
- **Defect Major/Minor reclassification reflects automatically.** Switching a defect's `defect_category` in `/Admin/DefectCatalog` is read live on the next PDF render (the report queries `qms_defect_catalog` directly inside `BuildGroupSummariesAsync`; no caching of the bucketing). Matches the audit-trail feature's read-through pattern.
- **Performance.** `BuildGroupSummariesAsync` issues **six fixed queries** regardless of how many materials / groups / samples the QO has (samples, arrival-item quantities, all sample-defects, all sample-readings, defect catalog for distinct groups, reading-type catalog for distinct groups). Everything else is in-memory `GroupBy` + `Sum`. No N+1 even for big containers.
- **Out of scope** (kept for follow-ups): formula language design + parser; on-screen group summary on `/QualityOrders/Details.cshtml` (PDF only); Excel/CSV export of the group summary; column cleanup on `qms_quality_order_material`.
- **Files touched.**
  - NEW: `app/db/V18__reading_type_display_mode.sql`.
  - MODIFIED: `Models/QualityOrder.cs` (+`DisplayMode` on `ReadingTypeEntry`, new `MaterialGroupSummary` / `DefectAggRow` / `ReadingAggRow`); `Services/IQualityOrderService.cs` + `Services/QualityOrderService.cs` (new `BuildGroupSummariesAsync`; +`display_mode` selected in the two existing reading-type SELECTs); `Controllers/AdminController.cs` (`ReadingTypes` GET selects `display_mode`; `SaveReadingType` signature + INSERT/UPDATE include it, with whitelist + value_kind fallback); `Views/Admin/ReadingTypes.cshtml` (new column, modal dropdown, kind→mode sync hook); `Controllers/ReportsController.cs` (`BuildDataAsync` calls the new aggregator); `Services/Pdf/QualityReportData.cs` (+`GroupSummaries`); `Services/Pdf/QualityReportPdf.cs` (`RenderSampleSummary` deleted; replaced with `RenderGroupSummary` + `RenderGroupReadings` + `RenderGroupDefectGrid`; doc header rewritten; empty-state line added when a QO has no samples yet).
- **Verified.** V18 applied idempotently (sanity SELECT: `OK`; rows: 8 `sum`, 4 `text`). `dotnet build SharbatlyQMS.slnx -c Debug` → 0 warnings, 0 errors. Dev boot on `localhost:5245` (port-shifted to avoid colliding with the running production service) returned HTTP 302→`/Account/Login` on `/`, 200 on `/Account/Login`, 302 on `/Admin/ReadingTypes` (auth gate), 302 on `/Reports/QualityOrderPdf/1` (auth gate). End-to-end UI verification with a real QO + an authenticated session is the next manual step (per `specs/plan` verification list); to deploy, run `& C:\QualityManagemet\deploy\Republish.ps1`.
- **Follow-up bugfix (same day).** Mohamed reported that `WASTE_DECAY` is set to **Minor** in `/Admin/DefectCatalog` but the PDF bucketed it into **Major Defects**. Root cause: `IQualityOrderService.GetDisplaySectionMapAsync` was still reading the legacy `qms_material_group_defect.display_section` junction column, which had drifted away from the catalog. Data showed 4 mismatches on APPLE: `WASTE_DECAY`, `SCALD`, `LENTICELS_BREAKDOWN`, `CALYX_MOLDS` were all `Minor` in the catalog but `Major` in the junction. The 2026-05-21 audit-trail change made `qms_defect_catalog.defect_category` the authoritative source for everything except this one query, which was missed. **Fix:** `GetDisplaySectionMapAsync` now queries `qms_defect_catalog` directly and translates Major+Critical → `"Major"`, everything else → `"Minor"` (matches the bucketing convention `_SampleForm.cshtml` and `BuildGroupSummariesAsync` already use). `display_section` and the entire `qms_material_group_defect` table now have zero readers in the code; the table is left in place (no rush — empty schema cleanup is its own follow-up). The `majorCategory` parameter on `GetDisplaySectionMapAsync` is preserved for call-site compatibility but is now ignored (catalog is already per-group, category is per-defect). Build clean, no schema change. Goes live with the same Republish that takes the grouped-summary feature into production.

### 2026-05-21 (audit-trail — Auditor role retired + "… and N more" made clickable)
- **Auditor role removed.** Now that the global `/Audit` page and Excel export are SiteAdmin-only, the Auditor role added no distinct capability over a regular Manager (both see the per-record audit panel; only SiteAdmin sees the global page + can export). Migration `V16__remove_auditor_role.sql` defensively demotes any `Role='Auditor'` user to `'Manager'` and drops `'Auditor'` from `CK_Users_Role`. At time of the change zero users had the role in production. Back to the 5-role set: `Viewer`, `Operator`, `Manager`, `ClaimManager`, `SiteAdmin`.
- **Code cleanup that landed with the removal.** Dropped `UserRoles.Auditor` constant; dropped `AuthPolicies.AuditViewer` and `AuthPolicies.AuditorOrAdmin` constants plus their `AddPolicy` registrations in `Program.cs`. `AuditController.HistoryPanel` switched from `AuditViewer` to the existing `ManagerOrAdmin` policy (same effective gate). `Views/QualityOrders/Details.cshtml` and `Views/Arrivals/Details.cshtml` `canSeeAudit` variables now check `Manager` or `SiteAdmin` only. `_Layout.cshtml` View-as dropdown role array no longer includes Auditor. `Views/Admin/Users.cshtml` `roleBadge` switch dropped its Auditor case.
- **"… and N more" now actually expands.** The hover-tooltip approach on the global Audit Log page wasn't initialising Bootstrap tooltips reliably and was hover-only — clicking did nothing. Replaced with an inline click-to-expand toggle: the short list (`changes-short` span) and the full list (`changes-full` span, initially `d-none`) are both rendered; the "… and N more" anchor swaps their visibility and hides itself. No JS framework dependency.
- **Files touched:** `app/db/V16__remove_auditor_role.sql` (NEW), `Models/User.cs`, `Program.cs`, `Controllers/AuditController.cs` (HistoryPanel policy), `Views/Audit/Index.cshtml` (click-to-expand + CSS), `Views/Shared/_Layout.cshtml` (View-as array), `Views/QualityOrders/Details.cshtml` + `Views/Arrivals/Details.cshtml` (canSeeAudit), `Views/Admin/Users.cshtml` (roleBadge). Build clean; redeployed silently via `deploy/Republish.ps1`. Verified: CK_Users_Role now lists 5 roles, app HTTP 200, `/Audit` 302 for unauthenticated.

### 2026-05-21 (audit-trail — layout fix + admin-only lockdown of the global page)
- **Layout fix on the global `/Audit` page.** The "Changes" column was rendering the full comma-separated list of changed field names, which could grow past half the page width on entries with many fields. Now: the cell uses a fixed `table-layout: fixed` table with capped column widths (When 11rem / Actor 9rem / Record 13rem / Action 8rem / Source 9rem / Changes takes the rest, wraps), and the field list itself is truncated to the **first 3 names + "… and N more"** with a Bootstrap tooltip on hover exposing the full comma list. Row layout stays compact regardless of diff size.
- **Audit Log nav moved under Admin dropdown.** Was a top-level navbar item visible to Manager / Auditor / SiteAdmin (via `canSeeAudit`). Now an entry inside the existing Admin dropdown (`asp-controller="Audit" asp-action="Index"`), so the top-level menu has one fewer item. The `canSeeAudit` variable in `_Layout.cshtml` is removed; visibility now follows the existing `canSeeAdmin` gate (SiteAdmin only). Per-record audit panels in `Views/QualityOrders/Details.cshtml` and `Views/Arrivals/Details.cshtml` still compute their own local `canSeeAudit` and are unaffected.
- **Global page + Excel export now SiteAdmin-only.** `AuditController.Index` and `AuditController.Export` switched from `AuthPolicies.AuditViewer` / `AuthPolicies.AuditorOrAdmin` to `AuthPolicies.AdminOnly`. `AuditController.HistoryPanel` (the per-record AJAX endpoint used by the detail-page panel) **stays at `AuditViewer`** — so Manager and Auditor can still see the audit history of a specific record they're working on (preserves spec FR-008), but can no longer browse the cross-record forensic page or run a bulk export.
- **Auditor role status.** Still exists, still assignable via `/Admin/Users`, still gets the AuditViewer policy for per-record panels. With the global page now SiteAdmin-only, the role's distinct value over a regular Manager is now limited — kept in place rather than removed to avoid disrupting role assignments that may already exist. If the team decides Auditor is redundant, removing it is a small follow-up (drop from `UserRoles.All`, drop from the View-as dropdown + `roleBadge` switch; no schema change required).
- **Files touched:** `Controllers/AuditController.cs` (2 policy attributes), `Views/Audit/Index.cshtml` (CSS + truncation logic + Bootstrap tooltip init), `Views/Shared/_Layout.cshtml` (move nav item + remove canSeeAudit). Build clean. Redeployed silently via `deploy/Republish.ps1`. Verified: `/Audit` returns 302 to login when unauthenticated; `/Account/Login` returns 200.

### 2026-05-21 (audit-trail bugfix — checklist / shipment diff inflation)
- **Fix: every checklist or shipment save reported ~11 "changed" fields even when only one Yes/No flag was actually flipped.** Root cause: `SaveChecklistAsync` and `SaveShipmentAsync` called `IAuditService.WriteAsync` with `oldValues: null`. `ParseDiffs` treats null as an empty dictionary, so every populated field in the new payload appears as a diff vs. nothing. Found by mohamed.tag during a real checklist edit on Arrival #2 (the audit row listed 11 fields including unchanged `SealNo`, `CarrierName`, `SetTemperature`, etc.).
- **Fix.** Both methods now `SELECT` the affected row inside the same transaction BEFORE the UPDATE, with column aliases matching the PascalCase keys of the `newValues` anonymous object. The result is passed as `oldValues`. `ParseDiffs` correctly emits only the fields that actually changed. The `WriteAsync` no-op short-circuit (`oldJson == newJson`) now also kicks in for true no-op saves of these two entities, satisfying FR-016 properly.
- **Also removed** the synthetic `shipment_snapshot_updated = true` marker from `SaveShipmentAsync`'s newValues — it forced every save to appear as a "change" even when the shipment fields were untouched. The captured old row is enough.
- **Scope verified.** Confirmed by code review that the same `oldValues: null` mistake is NOT present elsewhere — `UpdateSampleAsync` / `SoftDeleteSampleAsync` / `SaveReadingsAsync` / `SaveDefectsAsync` all properly read the row first; `Created` / `Deleted` actions correctly use null on the appropriate side; transition methods (Open/Close/Reopen/Cancel/Approve/Hold) use small single-field oldValues that already match the relevant newValues.
- **Files touched:** `app/SharbatlyQMS.Web/Services/ArrivalService.cs` (SaveChecklistAsync + SaveShipmentAsync only). Build clean, redeployed via `deploy/Republish.ps1`. No schema or NuGet change.

### 2026-05-20 (audit-trail feature)
- **Audit Trail module shipped end-to-end** — the first feature delivered through the full Spec Kit workflow (`/speckit-constitution` → `/speckit-specify` → `/speckit-clarify` → `/speckit-plan` → `/speckit-tasks` → `/speckit-implement`). Live in production at `http://192.168.3.192:5244` as of this deploy.
- **What it does.** Every CREATE / UPDATE / DELETE on the 10 tracked operational entity types (Arrival + ArrivalItem + ArrivalChecklist, QualityOrder + QualityOrderMaterial, Sample + SampleReading + SampleDefect, Claim + ClaimNote) now produces an immutable row in `qms_audit_log` carrying actor, action, old/new field JSON, timestamp, IP, and user-agent. Quality Managers see a per-record audit-history panel on QO / Arrival / Claim detail pages with git-diff coloured rendering (old in red strikethrough, new in green). A new global page `/Audit` provides chip-filtered keyset-paginated browsing across the whole table. A new `Auditor` site role can export filtered rows to `.xlsx` via ClosedXML, with single-in-flight enforcement.
- **Schema delta.** Migration `app/db/V15__audit_trail.sql` extends the existing `qms_audit_log` (V02) with `source_user_agent NVARCHAR(500) NULL` + composite read-path index `IX_qms_audit_log_filter (changed_at DESC) INCLUDE (entity_type, action_code, changed_by)`, and re-creates `CK_Users_Role` to allow `Auditor`. Idempotent; mirrors V12/V13 patterns.
- **Constitution conflict resolved (Path A).** The `/speckit-plan` invocation requested EF Core + DbContext + ChangeTracker + Razor Pages — all of which conflict with Constitution v1.0.0 Principle II (NON-NEGOTIABLE: *EF Core MUST NOT be introduced*) and Principle I (project uses MVC, not Razor Pages). The conflict was surfaced; user chose **Path A** = keep the constitution, reinterpret in Dapper + V15 SQL migration + MVC terms. The functional intent (audit table, injected service, no new projects, ClosedXML) was preserved end-to-end.
- **`(SqlConnection, SqlTransaction)` parameter shape on `IAuditService.WriteAsync`.** New convention introduced for this feature — no other service in the codebase takes the caller's transaction. Necessary for FR-007 atomicity (audit + mutation share one transaction). Documented in [specs/001-audit-trail/research.md §R-5](specs/001-audit-trail/research.md). All 19 mutation paths in QualityOrderService / ClaimService / ArrivalService now invoke it from inside their own `BeginTransaction()` block.
- **Pre-existing-bug fix (PH-1).** During plan review we found that `SaveOverrideAsync`, `ClearOverrideAsync`, `SaveChecklistAsync`, and `SaveShipmentAsync` performed multi-statement mutations on a single connection **without** an explicit transaction — a partial-failure window that the audit feature would have inherited. All four are now wrapped in `BeginTransaction()` + `tx.Commit()`. `CreateFromSapAsync` was already transactional (the plan's PH-1.5 was a no-op on inspection).
- **Pre-existing-bug fix (PH-2).** The two inline `INSERT INTO qms_audit_log` calls in the override methods were replaced with `_audit.WriteAsync(c, tx, …)` — the codebase now has a **single audit-write path**, eliminating drift risk between two parallel writers (different field coverage, missing IP/UA, etc.).
- **New role + policies.** `UserRoles.Auditor` added as the 6th site role (peer to Manager / ClaimManager — not hierarchically higher). Two new `AuthPolicies` constants: `AuditViewer` (Manager / Auditor / SiteAdmin — read the log) and `AuditorOrAdmin` (Auditor / SiteAdmin — export only). `_Layout.cshtml` View-as dropdown extended to include Auditor for testing; `Admin/Users.cshtml` role badge switch extended with an Auditor case so the new role doesn't render as the default grey.
- **Hidden from lower roles.** The audit-history panel on detail pages is gated by the same `AuditViewer` policy: Viewer / Operator / ClaimManager continue to see record data but not its audit trail (FR-017, locked via the 2026-05-20 clarify session). The Audit Log nav link is hidden from those roles too.
- **No EF Core. No new projects. No new top-level folders.** Per Constitution Principle I. The new `AuditContextActionFilter.cs` lives under `Services/` alongside its siblings; the `Filters/` folder originally proposed in the plan was eliminated during the post-design review. All other new files land under existing folders (`Controllers/`, `Services/`, `Models/`, `Views/Audit/`, `ViewModels/`, `app/db/`).
- **Files touched** (44 tasks total, all marked `[X]` in `specs/001-audit-trail/tasks.md`):
  - NEW: `app/db/V15__audit_trail.sql`, `Models/AuditEntry.cs`, `Services/IAuditService.cs`, `Services/AuditService.cs`, `Services/IAuditContext.cs`, `Services/AuditContext.cs`, `Services/AuditContextActionFilter.cs`, `Controllers/AuditController.cs`, `Views/Audit/Index.cshtml`, `Views/Audit/_AuditHistory.cshtml`, `Views/Audit/_AuditHistoryRows.cshtml`, `ViewModels/AuditListVm.cs`, `.gitignore`.
  - MODIFIED: `SharbatlyQMS.Web.csproj` (+ ClosedXML 0.105.0), `Program.cs` (DI + 2 policies + filter), `Models/User.cs` (+ Auditor + 2 policies), `Services/QualityOrderService.cs` (audit hooks on Transition / CreateForArrival / Save+Update+Delete Sample / SaveReadings / SaveDefects / SaveOverride / ClearOverride + PH-1 transaction wraps), `Services/ClaimService.cs` (audit hooks on QM+CM transitions + AddNote), `Services/ArrivalService.cs` (audit hooks on CreateFromSap / SaveChecklist / SaveShipment / Complete / ReopenForEdit / Delete + PH-1 transaction wraps), `Views/QualityOrders/Details.cshtml` (audit panel + claim-context dual panel), `Views/Arrivals/Details.cshtml` (audit panel), `Views/Shared/_Layout.cshtml` (Audit Log nav + Auditor in View-as), `Views/Admin/Users.cshtml` (Auditor in role badge switch), `CLAUDE.md` (SPECKIT pointer at the plan).
- **Verified.** `dotnet build` → 0 warnings, 0 errors. Production redeploy via `deploy/Republish.ps1` ran silent (no UAC). Schema sanity SELECT confirms `source_user_agent`, `IX_qms_audit_log_filter`, and the `Auditor` value in `CK_Users_Role` all `OK`. `qms_audit_log` row count visible: 15 pre-existing entries (override/claim actions from earlier features) + new entries will accumulate as users start interacting with the deployed feature. Manual quickstart (per `specs/001-audit-trail/quickstart.md`) recommended next: log in as Manager → exercise CRUD on a Quality Order → confirm the new Audit History panel renders the entries with git-diff colours.

### 2026-05-20
- **`view-as-pack/` extracted as a standalone reusable pack.** The "View as: {role}" SiteAdmin role-impersonation feature used to live inside `user-management-pack/` (SPEC §6 + `reference/ViewAsClaimsTransformer.cs` + `reference/_ViewAsDropdown.snippet.cshtml` + the `ViewAs` action inside `reference/AccountController.snippet.cs`). It now ships as its own pack `view-as-pack/` so new projects can install role-impersonation independently of full user-management. Folder layout matches the other four packs (`README.md`, `SPEC.md`, `INSTALL_PROMPT.md`, `How to use it for a new project.txt`, `reference/`, `examples/`). `user-management-pack/SPEC.md` §6 now contains only a 4-line pointer to `../view-as-pack/SPEC.md`; the two reference files were deleted and the `ViewAs` action stripped from the AccountController snippet (replaced with a one-line pointer). The live QMS app under `app/SharbatlyQMS.Web/` is **untouched** — it still runs its own copy of `ViewAsClaimsTransformer`. Reference code in the new pack keeps the generic `HelpDesk.*` template namespaces and the `ImpersonatorRole` constant (one place to rename the admin role at install time), matching the convention used by sibling packs. The numbering typo `### 8.1`/`### 8.2` in the old §6 (subsections incorrectly nested under §8) was fixed when restarting at `## 1`–`## 7` in the new SPEC.

### 2026-05-14
- **Claim Management module added.** New top-level page `/ClaimManagement` sits on top of every Closed Quality Order and captures the supplier-claim decision that previously happened off-system (WhatsApp / email). Workflow: a Closed QO starts as **Pending** (no claim row yet) → Quality Manager (`Manager` role) marks it **Claim Request** (red) or **Passed QC** (green) → Claim Manager (new `ClaimManager` role) has the final commercial call, **Claim Request Approved** (dark) or **Hold Claim** (amber). QM may flip Request ↔ PassedQC freely **until** CM has decided (then locked); CM may flip Approved ↔ Hold at any time. Notes are required on every status change and form an append-only **chat history** rendered only when the QO is opened via `/ClaimManagement/Details/{id}` — opening the same QO from `/QualityOrders/Details/{id}` does **not** show the chat (operational QO page stays unchanged). New role `ClaimManager` (5th site role; peer to Manager, not above) with new policy `ClaimManagerOrAdmin` gating Approve/Hold. New tables `qms_claim` (lazy-created; `decided_at IS NULL` gates QM further flips) and `qms_claim_note` (append-only; `note_kind` = `StatusChange`|`Comment`; `author_role` snapshotted). Model class is **`QualityClaim`** (not `Claim`) to avoid collision with `System.Security.Claims.Claim`. List page shows ALL Closed QOs with fast-filter chips (All / Pending / 4 claim statuses). View reuse: `ClaimManagementController.Details` calls `View("/Views/QualityOrders/Details.cshtml", qo)` with `ViewBag.IsClaimContext = true`; the QO view forces `editable = canEdit = canManage = false` so all existing mutation gates collapse — no duplicate render. Files: `app/db/V13__claim_management.sql`, `Models/Claim.cs`, `Models/User.cs`, `Program.cs`, `Services/IClaimService.cs` + `ClaimService.cs`, `Controllers/ClaimManagementController.cs`, `Views/ClaimManagement/{Index,_ClaimChatPanel}.cshtml`, `Views/QualityOrders/Details.cshtml`, `Views/Shared/_Layout.cshtml`, `Views/Admin/Users.cshtml`. Out of scope: email notifications, claim attachments, claim KPIs, PDF chat export, automatic claim reset on Reopen (the row stays — if it matters, add a delete inside `QualityOrderService.Transition` for the `Reopened` branch).
- **Defect Catalog & Reading Types — Delete when not yet used.** Each row in `/Admin/DefectCatalog` and `/Admin/ReadingTypes` now has a Delete (trash) button next to Edit. The catalog GET query computes an `IsInUse` flag per row (defect: `EXISTS in qms_sample_defect`; reading type: `EXISTS in qms_sample_reading` joined to `qms_sample → qms_quality_order_material` on the same `material_group`, since codes can repeat across groups). When `IsInUse=true` the button renders disabled with a tooltip *"Used by at least one Quality Order sample — cannot delete. Mark inactive instead."* When `IsInUse=false` the button posts to a new `Admin/DeleteDefect` or `Admin/DeleteReadingType` action which **re-validates server-side** (never trust the UI), removes the matching `qms_material_group_defect` / `qms_material_group_reading` binding row first to satisfy the FK, then deletes the catalog row. Both actions require `ManagerOrAdmin`. Reason: testing churn left orphan catalog rows the admin wanted to clean up without resorting to SQL. Files touched: `Models/QualityOrder.cs` (`IsInUse` flag on both entries), `Controllers/AdminController.cs` (queries + two new actions), `Views/Admin/DefectCatalog.cshtml` & `Views/Admin/ReadingTypes.cshtml` (Actions column + Delete form).
- **Site Configuration — "Danger Zone" tab with Purge All.** New rightmost tab in `/Admin/Settings` (red, exclamation-octagon icon). Lets a SiteAdmin wipe every operational record so the system can move from testing to production with a clean slate.
  - **What is deleted:** all rows in `qms_arrival` + dependents (items, checklists, SAP/shipment snapshots), `qms_quality_order` + dependents (materials, samples, readings, observations, sample_defects), `qms_image_asset` + `qms_image_link`, `qms_status_history`, `qms_audit_log`, `qms_report_log`, plus every file under `wwwroot/uploads/`.
  - **What is preserved:** Users, AD config, email groups, defect/reading catalogs, Site Configuration, SAP material/vendor sync caches.
  - **Sequences reseeded:** `seq_qms_arrival_no`, `seq_qms_shipment_no`, `seq_qms_quality_order_no` → restart with 1. `DBCC CHECKIDENT(...,RESEED,0)` runs on every cleared table so identity counters also restart.
  - **Safety:** SiteAdmin-only (`POST /Admin/PurgeAll`, antiforgery, `[Authorize(Policy = AdminOnly)]`). User must type the exact phrase `PURGE ALL` (case-sensitive) to enable the button, then confirm a JS dialog. All deletes + reseeds run in one SQL transaction; image-file deletion is best-effort (locked files skipped). Both start and completion are logged at Warning level via `_adminLog` (Event Viewer → Application, source `SharbatlyQMS`).
  - **File touched:** `Views/Admin/Settings.cshtml` (new tab + form + JS guard) and `Controllers/AdminController.cs` (`PurgeAll` action).
- **Quality Order — material panels collapsed by default.** `Views/QualityOrders/Details.cshtml` now renders every `.mat-card` with `collapsed-card` and the body without `show`. Persistence flipped: sessionStorage key is `qoExpanded:<qoId>:<materialId>` (presence-only — its existence means "user expanded; keep open across reloads"). Removing the key reverts to the collapsed default. Header badges and sample count update even when the panel is collapsed, so add-sample / delete-sample is still observable. Reason: large QOs with many materials were unscrollable; users wanted to scan headers and dive only into the material they're working on.
- **Login normalization fix.** `AccountController.Login()` strips the `@domain` suffix and uses the AD-returned `sAMAccountName` as the canonical DB key. Both `user` and `user@domain` map to the same row. Deleted two duplicate Users rows (`UserId 7` and `UserId 8`, both UPN-form). No FK references blocked the delete.

### 2026-05-13
- **Production deployment to LAN.** Published as a Windows Service on this PC, bound to `0.0.0.0:5244`. Firewall opened on TCP 5244 (Domain + Private). LAN URL: `http://192.168.3.192:5244`. Republish flow scripted (`Republish.ps1`).
- **Hardcoded admin removed.** Deleted `/Account/Setup`, `SeedHashes`, the bootstrap re-hash block, and the `INSERT INTO Users` for the admin seed in `V03__seed.sql`. The existing `admin` row in the production DB was deliberately **not** deleted to avoid lock-out.
- **View site as added.** New `ViewAsClaimsTransformer`, `Account/ViewAs` action, navbar dropdown + banner. Also lifted into `user-management-pack` so future apps can reuse it.
- **AdminController authorization re-shaped.** Class-level attribute lowered from `AdminOnly` to `ManagerOrAdmin`; every SiteAdmin-only action got an explicit `[Authorize(Policy = AdminOnly)]`. Manager users can now reach the Parameters menu (Defect Catalog, Reading Types, Mail Template) — they used to be blocked by the class-level gate.
- **AD picker modal layout fix** on `/Admin/Users` — modal goes full-screen on laptops < 992 px, action column pinned to the right with `position: sticky` so the Add button is always visible.

### 2026-05-04 (initial decisions)
- Stack locked: .NET 9 / MVC / Dapper / SQL Server / Bootstrap 5.3.
- Project location: `C:\QualityManagemet\app\`.
- First fruit family: **Apple** only (seed defects from plan §7).
- Themes: keep all 6 stock themes from `theme-pack`.
- Image storage: local filesystem (`wwwroot/uploads/`).
- PDF library: QuestPDF (community license).
- Email: SMTP, config-driven.
- Alert v1 scenarios: (a) arrivals stuck in Draft > N days, (b) quality orders open > N days, (c) defect % over threshold.

### Original spec (locked at the time, since superseded)
- Auth was originally specced as **local-only BCrypt** with 3 roles (`QCStaff` / `QCManager` / `SiteAdmin`). It was reworked into AD-first with 4 roles (`Viewer` / `Operator` / `Manager` / `SiteAdmin`) — see `V12__role_overhaul.sql`.

---

## 9. Hard rules (do not violate)

- **SAP OData is read-only.** Never write back to SAP. Always go through `ISapClient`; never let controllers call OData directly.
- **No self-registration UI.** Login page must not show a "Sign up" link. Users come from AD auto-create or admin creation.
- **AD bind-only at login.** No LDAP search during the login POST. Profile-enrichment searches run only after a successful bind, and their failure is non-fatal.
- **AD service password is write-only** in the admin UI — blank-on-save means "keep existing".
- **Self-protection on destructive actions.** Admin cannot delete or disable their own account; bulk-delete must defensively skip self.
- **FK delete failures** show "still referenced — disable instead", never a stack trace.
- **AdminController gating:** every SiteAdmin-only action needs its own `[Authorize(Policy = AdminOnly)]` — the class-level gate is `ManagerOrAdmin`.
- **Don't add features beyond the current phase** (per `CLAUDE.md` / `AGENTS.md` simplicity rule).

---

## 10. Common operations

### Run locally (dev)

```powershell
cd C:\QualityManagemet\app\SharbatlyQMS.Web
dotnet run
```

Binds to `http://localhost:5244` in Development. **Cannot run at the same time as the production service** — they'd both try to grab port 5244.

### Build a release without deploying

```powershell
dotnet publish C:\QualityManagemet\app\SharbatlyQMS.Web -c Release -o C:\QualityManagemet\deploy\SharbatlyQMS --nologo
```

### Inspect Users table

```powershell
$cs = 'Server=192.168.3.10;Database=SharbatlyQMS;User ID=linkserver;Password=P@ssw0rd;TrustServerCertificate=True;Connect Timeout=15'
$conn = New-Object System.Data.SqlClient.SqlConnection($cs); $conn.Open()
$cmd  = $conn.CreateCommand()
$cmd.CommandText = 'SELECT UserId, Username, Role, IsActive FROM Users ORDER BY UserId'
$r = $cmd.ExecuteReader(); while ($r.Read()) { "$($r['UserId']) | $($r['Username']) | $($r['Role']) | active=$($r['IsActive'])" }
$r.Close(); $conn.Close()
```

### Re-apply migrations

```powershell
cd C:\QualityManagemet\app\SharbatlyQMS.Migrate
dotnet run
```

---

## 11. Anti-patterns to watch for

- **Bumping `AdminController`'s class-level attribute back to `AdminOnly`.** Silently locks Managers out of the Parameters menu again. The fix history is in §8 (2026-05-13).
- **Re-introducing `/Account/Setup` or a SeedHashes array.** Removed deliberately; AD-first installs don't need a back-door.
- **Storing `model.Username` verbatim** as the Users.Username key. Use the AD-returned `sAMAccountName` (or strip `@domain`) so login is idempotent regardless of input format.
- **Calling `DirectorySearcher` inside the login POST before a successful bind.** Has historically caused referral timeouts; do the search only after `entry.NativeObject` succeeds, and treat any search failure as non-fatal.
- **Writing changes to SAP.** Hard rule — QMS is read-only against SAP.
- **Running `dotnet run` while the Windows Service is also running.** Port collision.

---

## 12. Where else to look

- `QMS_Extended_Assessment_and_Execution_Plan.md` — original 16-week plan, normalized schema, full architecture. Still the source of truth for table names / status codes / build-phase order.
- `user-management-pack/SPEC.md` — full behavioral contract for the auth + user-mgmt feature set.
- `view-as-pack/SPEC.md` — role-impersonation contract (extracted from `user-management-pack` §6 on 2026-05-20).
- `alert-pack/SPEC.md`, `email-notification-pack/SPEC.md`, `theme-pack/README.md` — pack-specific specs.
- `CLAUDE.md`, `AGENTS.md` — behavioral guides for AI agents (think-before-coding, simplicity, surgical changes). Generic; they point back here for project context.
- Claude Code's persistent memory (Claude-only): `~/.claude/projects/<dir-slug>/memory/MEMORY.md` indexes user/feedback/project/reference memories — but other AI tools don't see it, so this file (`PROJECT_STATE.md`) is the cross-AI source of truth.
