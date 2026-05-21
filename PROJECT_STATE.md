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
