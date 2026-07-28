---
app_name: "Sharbatly QMS"
version: "1.0"
purpose: "Inspect every fruit shipment from container arrival to finished quality order — record defects, measurements and photos, then slice the results into reports."
audiences: ["Operators", "Supervisors", "Managers", "Site administrators"]
site_url: "http://192.168.3.15:5244"
base_url: "http://192.168.3.15:5244"
roles: ["Viewer", "Operator", "Supervisor", "Manager", "ClaimManager", "SiteAdmin"]
theme: "default"
formats: ["html", "pdf", "docx", "pptx"]
lang: "en"
direction: "ltr"
login:
  url: "/Account/Login"
  username_field: "Username"
  password_field: "Password"
  submit_text: "Sign in"
---

# Sharbatly QMS — Help & User Guide

## 1. Overview

Sharbatly QMS is the in-house Quality Management System for the Sharbatly fruit team. It captures the full journey of every imported container — from the moment SAP says it's on its way, through the cold-store arrival, the sample inspection on the floor, the supervisor's review, all the way to the finished Quality Order and any insurance claim that follows.

Five kinds of people use it:

- **Operators** open arrivals, run sample checks (sizes, brix, defects, photos), and submit a Quality Order for review.
- **Supervisors** review submitted Quality Orders and Finish them (or send them back for a fix).
- **Managers** maintain the rules everyone follows — the defect catalog, the measurement types, the email templates — and read the data hub.
- **Claim Managers** decide which finished Quality Orders become insurance claims.
- **Site administrators** look after users, plant scoping, the audit log, and the configuration.

Everything is built around one Windows-login: you sign in with your normal company username and password, the app figures out what you're allowed to see, and it never asks you to register or set a password of its own.

## 2. Getting Started

> **Quick Start** — Your first inspection in five minutes:
> 1. Open the app at `{{site_url}}` and sign in with your Windows username.
> 2. On the home page, open **Pending Containers** from the top menu.
> 3. Click a row → press **Create Arrival** → fill in the seal and temperatures.
> 4. Open the new arrival → **Create Quality Order** → **Add sample**.
> 5. Type the defects and readings you see, snap a photo, then **Submit for review**. Done.

### Signing in

- **Route:** `/Account/Login`
- **Screenshot:** `screenshots/account-login.png`

1. Open your browser and go to **{{site_url}}**.
2. Type your **Windows username** (the same one you use for email or the file share) and your password.
3. Click **Sign in**. You'll land on the home page.

> **Tip** — There's no "Register" button. Your account is created for you by IT once your role on the team is decided.

> **Warning** — If you mistype your password three times in a row, Windows itself will lock your account for a while. Wait a few minutes before trying again, or call IT.

### The top menu

After you sign in, the dark blue strip at the top is the menu. Which items you see depends on your role.

| Menu item | Who sees it |
|---|---|
| **Pending Containers** | Operator and above |
| **Arrivals** | Operator and above |
| **Quality Orders** | Operator and above |
| **Claims** | ClaimManager + SiteAdmin |
| **Data Hub** | Supervisor and above |
| **Admin ▾** | Manager and SiteAdmin |
| **Audit log** | SiteAdmin only |
| Your name (top-right) | Everyone — sign out, view profile |

On a phone or narrow window the menu collapses behind the three-line button on the right; tap it to open the same items.

> **Note** — If a page ever shows **Access denied**, you tried to open something that needs a higher role than yours. Use the browser's **Back** button and pick a different menu item, or ask your supervisor whether your role should be changed.

## 3. Screens & Pages

### Login

- **Route:** `/Account/Login`
- **Who uses it:** Everyone
- **What it's for:** Signing in with your Windows username and password.
- **Screenshot:** `screenshots/account-login.png`

**How to use it**
1. Type your username (no `@domain` — just the short name).
2. Type your password.
3. Click **Sign in**.

> **For Admins** — Authentication binds to Active Directory at the moment of sign-in only. There is no local password store and no self-registration. The AD domain and the LDAP service account live in `appsettings.Production.json` under `Ad:Domain`, `Ad:LdapPath`, `Ad:ServiceUser`, `Ad:ServicePassword`.

### Profile

- **Route:** `/Account/Profile`
- **Who uses it:** Everyone
- **What it's for:** Seeing who you're signed in as, your role, and your plant scope (if any).
- **Screenshot:** `screenshots/account-profile.png`

**How to use it**
1. Click your name in the top-right.
2. Click **Profile** in the drop-down.
3. You'll see your username, your role, and — if you're an operator scoped to one plant — which plant code that is.

### Access denied

- **Route:** `/Account/AccessDenied`
- **Who uses it:** Everyone (shown only when needed)
- **What it's for:** Shown when you open a page your role can't reach.
- **Screenshot:** `screenshots/account-accessdenied.png`

Read the short message and click **Back** in your browser, or open a different menu item.

### Home dashboard

- **Route:** `/`
- **Who uses it:** Everyone
- **What it's for:** Your landing page after sign-in. Shows recent activity tailored to your role.
- **Screenshot:** `screenshots/home-index.png`

**How to use it**
1. After sign-in this opens automatically.
2. Use the tiles or the top menu to go where you need.

### Pending Containers

- **Route:** `/Arrivals/Pending`
- **Who uses it:** Operator and above
- **What it's for:** The list of containers that SAP says are on their way but haven't been opened in QMS yet.
- **Screenshot:** `screenshots/arrivals-pending.png`

**How to use it**
1. Open **Pending Containers** from the top menu.
2. You'll see one row per container with PO, BOL, plant, storage location, vendor, and dates.
3. Use the filters at the top (container, BOL, PO, plant, PO type, storage location) to narrow the list.
4. If you don't see a container you're expecting, click **Retrieve latest containers** in the header. A status indicator shows the SAP fetch progress.
5. Click any row → the arrival is created and you land on the Arrival Details page.

> **Tip** — As an operator, you only see containers in your assigned plant. If you cover several plants, ask the admin to widen your scope.

> **For Admins** — Plant scoping lives in `Users.PlantCode` (added by migration V29). Set it to `NULL` to give a user every plant.

### Arrivals list

- **Route:** `/Arrivals`
- **Who uses it:** Operator and above
- **What it's for:** Every arrival that has been opened in QMS, filtered by plant for operators.
- **Screenshot:** `screenshots/arrivals-index.png`

**How to use it**
1. Click **Arrivals** in the top menu.
2. Filter by status, container, vendor, or date.
3. Click an arrival number to open it.

### Arrival search

- **Route:** `/Arrivals/Search`
- **Who uses it:** Operator and above
- **What it's for:** Quick search by container number, BOL or PO when you don't have the arrival ID.
- **Screenshot:** `screenshots/arrivals-search.png`

**How to use it**
1. Type all or part of the container, BOL or PO.
2. Click **Search**.
3. Open the matching row.

### Arrival details

- **Route:** `/Arrivals/Details`
- **Who uses it:** Operator and above
- **What it's for:** The full arrival — checklist (seal, temperatures, data logger, photos), material lines, shipment dates, and the Quality Order link.
- **Screenshot:** `screenshots/arrivals-details.png`

**How to use it**
1. Open an arrival from the Pending list or the Arrivals list.
2. Fill in the **Checklist** — seal intact? Pulp temperatures (front / middle / back)? Data-logger serial? Notes?
3. Attach any photos (driver paperwork, seal, damage) using **Add photo**.
4. Click **Save** to persist the checklist.
5. When the arrival is ready, click **Create Quality Order** — you land on the Quality Order Details page.
6. Use **Download checklist PDF** for the supplier copy.

### Quality Orders list

- **Route:** `/QualityOrders`
- **Who uses it:** Operator and above
- **What it's for:** Every Quality Order you can see, filtered by plant and status.
- **Screenshot:** `screenshots/qualityorders-index.png`

**How to use it**
1. Click **Quality Orders** in the top menu.
2. Filter by status (Initial / Open / Submitted / Closed / Cancelled), plant, vendor.
3. Click a QO number to open it.

### Quality Order details

- **Route:** `/QualityOrders/Details`
- **Who uses it:** Operator and above
- **What it's for:** Everything about one Quality Order — material header, samples, defects, supervisor decision.
- **Screenshot:** `screenshots/qualityorders-details.png`

**How to use it**
1. Open a Quality Order from the list.
2. Fill the **Material header** — pack type, brand, variety etc. (the button highlights yellow until it's complete).
3. Click **Add sample** to record an inspection.
4. When all samples are in, click **Submit for review** (Operator) or **Finish** (Supervisor) or **Reopen** (Manager).
5. Click **Send report to supplier** to email the PDF.

> **For Admins** — The Submit / Finish / Reopen / Cancel-submit gates are enforced by `QualityOrderStatus` transitions in `QualityOrderService`. Don't bypass them in scripts; the audit trail won't be consistent.

### Sample inspection

- **Route:** `/QualityOrders/Sample`
- **Who uses it:** Operator and above
- **What it's for:** Recording one sample — sample size, scope (one carton vs multi-carton), defects, readings, photos.
- **Screenshot:** `screenshots/qualityorders-sample.png`

**How to use it**
1. From a Quality Order, click **Add sample** or open an existing sample row.
2. Pick **Sample scope** (One Carton or Multi-Carton).
3. Confirm **Sample size** (it pre-fills from MARA; you can override).
4. Enter defects — each one has a number and a category. The sum of defects can't exceed the sample size.
5. Enter readings (e.g. Brix, firmness) — the dropdown only shows reading types matched to this material group.
6. Use **Add photo** for visual evidence — drag-drop, browse, or camera capture.
7. Click **Save sample**.

> **Tip** — If a defect with a percentage like "Soft 10%" tips you over the sample size, lower the number; the page tells you exactly how many more defects you can still add for that sample.

### Per-sample photos drawer

- **Route:** `/QualityOrders/SamplePhotosPanel`
- **Who uses it:** Operator and above
- **What it's for:** A side panel listing every photo attached to one sample, with thumbnail, original filename, who uploaded it, and delete.
- **Screenshot:** `screenshots/qualityorders-samplephotospanel.png`

**How to use it**
1. On a sample row, click the camera icon.
2. The drawer slides in from the right.
3. Click a thumbnail to preview the original.
4. Click **Delete** under a photo to remove it; the counter on the row updates immediately (no full reload needed).

### Claim management

- **Route:** `/ClaimManagement`
- **Who uses it:** Claim Managers and Site administrators
- **What it's for:** Approving, holding or releasing insurance claims that originate from Finished QOs.
- **Screenshot:** `screenshots/claimmanagement-index.png`

**How to use it**
1. Open **Claims** from the top menu.
2. Filter by status (Open / Approved / On hold / Closed).
3. Open a claim → review the supplier copy, the photos, the defect summary.
4. Click **Approve**, **Hold** or **Close** as required. Each click is captured in the audit log.

### Data hub (Flat defects)

- **Route:** `/Reports/FlatDefects`
- **Who uses it:** Supervisors and above
- **What it's for:** One row per (sample × defect), filtered by 15 slots, with Excel export and the Perspective Analyzer card sitting underneath.
- **Screenshot:** `screenshots/reports-flatdefects.png`

**How to use it**
1. Open **Data Hub** from the top menu.
2. The filter card defaults to the last 30 days of PO date. Adjust if you need.
3. Click **Filter** to refresh the preview.
4. Click **Download Excel** for the full unaggregated rowset.
5. Scroll down to the **Perspective analyzer** card to pivot.

> **For Admins** — The preview hides zero-value rows and three SAP columns (Arrival No, STO, Severity) for clarity; the Excel export keeps every column AND zero rows so analytics has full coverage.

### Admin → Users

- **Route:** `/Admin/Users`
- **Who uses it:** Administrators only
- **What it's for:** Looking up users, changing their role, setting their plant scope, deactivating them.
- **Screenshot:** `screenshots/admin-users.png`

**How to use it**
1. Open **Admin → Users**.
2. Use the search box to find someone by name or username.
3. Open a row, change the role or set **Plant code** for operators.
4. Click **Save**. The change is audited.

### Admin → Settings

- **Route:** `/Admin/Settings`
- **Who uses it:** Administrators only
- **What it's for:** Site-wide settings — branding logo, favicon, SMTP server, thumbnail sizes, mail toggles, SAP sync schedules.
- **Screenshot:** `screenshots/admin-settings.png`

**How to use it**
1. Open **Admin → Settings**.
2. Tab through the sections (Branding, Mail, Thumbnails, SAP sync).
3. Change a value, click **Save** at the bottom of each tab.

### Admin → Defect catalog

- **Route:** `/Admin/DefectCatalog`
- **Who uses it:** Administrators only
- **What it's for:** The master list of defects per material group — code, name, default unit, severity, percentage-yes/no.
- **Screenshot:** `screenshots/admin-defectcatalog.png`

**How to use it**
1. Open **Admin → Defect catalog**.
2. Pick a material group from the dropdown.
3. Add or edit a defect. Mark **Active** to make it visible to inspectors.

### Admin → Defect categories

- **Route:** `/Admin/DefectCategories`
- **Who uses it:** Administrators only
- **What it's for:** The Major / Minor / Critical / Other category list used by every defect.
- **Screenshot:** `screenshots/admin-defectcategories.png`

**How to use it**
1. Open **Admin → Defect categories**.
2. Add a new category if your team needs one beyond Major / Minor / Critical / Other (rare).

### Admin → Reading types

- **Route:** `/Admin/ReadingTypes`
- **Who uses it:** Administrators only
- **What it's for:** The catalog of measurements (Brix, firmness, etc.) per material group, with units and display order.
- **Screenshot:** `screenshots/admin-readingtypes.png`

**How to use it**
1. Open **Admin → Reading types**.
2. Pick a material group.
3. Add or edit a reading type. Mark **Mandatory** if the sample form should refuse to save without it.

### Admin → Sample headers

- **Route:** `/Admin/SampleHeaders`
- **Who uses it:** Administrators only
- **What it's for:** Custom header fields shown on every sample (e.g. Grower, Pack code, Date code).
- **Screenshot:** `screenshots/admin-sampleheaders.png`

**How to use it**
1. Open **Admin → Sample headers**.
2. Add a field with name, code, type (text / number / date), and active flag.
3. Save. Operators see it on the next sample they open.

### Admin → Mail template

- **Route:** `/Admin/MailTemplate`
- **Who uses it:** Administrators only
- **What it's for:** The body and subject of the supplier email sent from a Closed Quality Order.
- **Screenshot:** `screenshots/admin-mailtemplate.png`

**How to use it**
1. Open **Admin → Mail template**.
2. Use the placeholders listed on the right (e.g. `{{QO_NUMBER}}`, `{{VENDOR_NAME}}`).
3. Toggle **Enabled** to allow operators to send.
4. **Save**.

### Audit log

- **Route:** `/Audit`
- **Who uses it:** Administrators only
- **What it's for:** Every interesting state change in the system — who did what, when, on which record.
- **Screenshot:** `screenshots/audit-index.png`

**How to use it**
1. Open **Audit log** from the Admin menu.
2. Filter by date, actor, entity type, or action.
3. Click **Export** for the Excel copy.

## 4. Features

### Signing in for the first time

- **Who:** Everyone with a Windows account
- **Screenshot:** `screenshots/account-login.png`

**Steps**
1. Go to **{{site_url}}** in your browser (Edge, Chrome and Firefox all work).
2. Type your **Windows username** — just the short name, not `@sharbatlyfruit.com`.
3. Type your **password**.
4. Click **Sign in**.

**What you'll see next:** the home page, with your name at the top-right and a menu tailored to your role.

> **Tip** — If a colleague says "the app says I'm not allowed", first check that they're on the company network or VPN. The app only accepts traffic from inside the Sharbatly LAN.

> **For Admins** — There's no password reset inside QMS. Passwords come from AD; reset there. Account creation: add the user in AD then add a row in `Users` with their role (`Operator`, `Supervisor`, `Manager`, `ClaimManager`, or `SiteAdmin`).

### Picking up a container from Pending

- **Who:** Operators
- **Screenshot:** `screenshots/arrivals-pending.png`

**Steps**
1. Open **Pending Containers** from the top menu.
2. If your container isn't there, click **Retrieve latest containers** in the header — the status indicator tells you when the SAP fetch is done.
3. Filter by container, BOL or PO if the list is long.
4. Click the row of the container you want.
5. The arrival is created and you land on the Arrival details page.

**What you'll see next:** a fresh checklist waiting for the seal and temperatures.

### Completing an Arrival checklist

- **Who:** Operators
- **Screenshot:** `screenshots/arrivals-details.png`

**Steps**
1. Open the arrival you just created.
2. Fill the seal number, the seal-intact tick, and any visible-damage tick.
3. Type the three pulp temperatures (front, middle, back).
4. If a data logger is present, type its serial and tick the photos-taken box.
5. Add notes if anything's unusual.
6. Attach photos using **Add photo** — drag, browse, or camera.
7. Click **Save**.
8. When the checklist is complete, click **Create Quality Order**.

**What you'll see next:** a new Quality Order with the material lines already populated from SAP.

> **Tip** — You can come back to the checklist later. It saves any time you click **Save**; you don't have to finish in one go.

### Running a sample inspection

- **Who:** Operators
- **Screenshot:** `screenshots/qualityorders-sample.png`

**Steps**
1. On a Quality Order, click **Add sample**.
2. Pick **Sample scope** (One Carton or Multi-Carton).
3. Confirm or override **Sample size** — it pre-fills from MARA.
4. Type the defects you observe. The sum across all defects can't exceed the sample size; the form tells you when you've hit the cap.
5. Type the readings (Brix, firmness, etc.) — the dropdown only shows the reading types valid for this material group.
6. Attach photos — drag-drop, browse, or camera capture. The counter on the sample row updates immediately.
7. Click **Save sample**.

**What you'll see next:** the new sample appears on the QO details page with its size, scope, and defect summary.

> **Tip** — Need to redo a sample? Open it, change the values, and **Save**. The first save creates a sample; later saves update it. Operators can delete a sample only while the Quality Order is in Open state.

### Submitting a Quality Order for review

- **Who:** Operators
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Make sure the **Material header** is complete (the button glows yellow until it is).
2. Make sure every sample you intended to inspect is saved.
3. Click **Submit for review** at the top of the QO.
4. Type a short note if you want to tell the supervisor anything.
5. Click **Confirm**.

**What you'll see next:** the QO status changes to **Submitted**. You can no longer edit samples until the supervisor either Finishes the QO or sends it back.

### Finishing a Quality Order (Supervisor)

- **Who:** Supervisors and above
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Open a **Submitted** Quality Order.
2. Review every sample and the operator's note.
3. If everything is good, click **Finish** — the QO becomes **Closed** and the supplier email button unlocks.
4. If something needs fixing, click **Cancel submission** — the QO goes back to **Open** so the operator can edit.

**What you'll see next:** Either an email button you can use to send the supplier report, or the operator picks the QO back up.

> **For Admins** — `Cancel submission` is a Supervisor or higher action only; it's distinct from `Cancel QO` which closes the order entirely (Manager only).

### Sending the report to the supplier

- **Who:** Supervisors and above (only on Closed QOs)
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Open a **Closed** Quality Order.
2. Click **Send report to supplier**.
3. The dialog pre-fills To / CC / Subject / Body from the mail template. Edit if you need.
4. Click **Send**.

**What you'll see next:** a green banner confirms the email left the building.

> **For Admins** — The body template is editable under **Admin → Mail template**. The feature stays disabled if the **Enabled** toggle is off there.

### Filing a claim

- **Who:** Claim Managers and Site administrators
- **Screenshot:** `screenshots/claimmanagement-index.png`

**Steps**
1. Open **Claims** from the top menu.
2. Click **New claim**, link it to a Closed QO, and pick a claim reason.
3. The defect summary and supplier copy come along automatically.
4. **Save** → status starts as **Open**.
5. As decisions are taken, **Approve**, **Hold** or **Close** the claim. Each action is audited.

**What you'll see next:** the claim appears in the list with its status badge and the supplier sees only the agreed copy.

### Using the data hub

- **Who:** Supervisors and above
- **Screenshot:** `screenshots/reports-flatdefects.png`

**Steps**
1. Open **Data Hub** from the menu.
2. The filter card is collapsed by default (it remembers its state between visits). Expand it.
3. PO Date defaults to the last 30 days; widen the range if you need older shipments.
4. Pick any combination of the 15 filter slots (plant, vendor, variety, etc.). Click **Filter**.
5. The preview shows up to the first 200 rows. Use **Download Excel** for the complete stream.

**What you'll see next:** an Excel file with friendly column names for analytics (e.g. "Supplier" instead of `VendorName`, "Defect Count" instead of `DefectValue`).

### Building a perspective (Perspective Analyzer)

- **Who:** Supervisors and above
- **Screenshot:** `screenshots/reports-flatdefects.png`

**Steps**
1. Below the data-hub preview, find the **Perspective analyzer** card.
2. The top strip shows the **Scope**: by default it follows the filter card above. Toggle to **Whole dataset** if you want to ignore that filter for this analysis.
3. From the **Available fields** list on the left, click **+ Rows** next to a field (e.g. Plant) or **+ Cols** next to another (e.g. Defect). You can also drag chips.
4. In the **Values** zone, click **+ Add measure**. Pick a measure (e.g. Defect count), an aggregation (Sum), an optional number format and label. **Apply**. Repeat to add more measures (Excel-style Σ Values).
5. Click **Run**. The result table appears with the **Heat** shading on. Numbers are formatted per measure.
6. To restrict further, click any cell in the table to **Drill** into it, or click **+ Add filter** in the Drill / Filter strip and pick specific values.
7. Click **Save as…** to keep the configuration. Pick **Private** (default) or **Shared** (Managers / SiteAdmin only). Tick **Open with this perspective by default** if you want it loaded automatically next time.
8. Click **Export pivot to Excel** for a polished workbook (title, filter summary, merged headers when you have ≥2 measures, frozen panes, totals row).

**What you'll see next:** a saved perspective in the dropdown at the top of the card. A ★ marks your default; **Shared** entries are grouped separately so you can tell yours apart.

> **Tip** — Switch the renderer to **Bar**, **Stacked Bar**, **Line**, **Area** or **Heatmap** for a quick chart. When you have two or more measures, a small **Measure to chart** picker appears so you can pick which series to plot.

> **For Admins** — The pivot uses a server-side SQL `GROUP BY` against `dbo.vw_qms_flat_defects` (created by V34). The dimension and measure lists come from `PivotRegistry.cs`; only registered keys can reach SQL, which is the injection guardrail. The XLSX export is built server-side via ClosedXML.

### Maintaining the defect catalog

- **Who:** Administrators only
- **Screenshot:** `screenshots/admin-defectcatalog.png`

**Steps**
1. Open **Admin → Defect catalog**.
2. Pick a **Material group** from the dropdown.
3. Click **Add defect** — fill code, name, default unit (Count / kg / %), and pick a category (Major / Minor / Critical / Other).
4. Tick **Active**. Untick it later to retire a defect without losing history.
5. Repeat for every defect operators need to record.

**What you'll see next:** the next time an operator opens a sample for that material group, the new defect is in the list.

## 5. Administration & Setup

### Where the app runs

- **Host:** Windows server `192.168.3.15` (Saudi LAN).
- **Process:** Windows service `SharbatlyQMS` hosting `SharbatlyQMS.Web.exe`.
- **Reverse proxy:** none — Kestrel serves directly on port `5244`.
- **Deploy folder:** `C:\Websites\QualityManagemet\deploy\SharbatlyQMS\`.
- **Source backup folder on the same server:** `C:\Websites\QualityManagemet\` (mirrored from the dev box).

### Publishing a new build

There are two PowerShell scripts in `deploy\`:

| Script | What it does |
|---|---|
| `Republish.ps1` | Builds Release locally and publishes to your dev-box deploy folder. |
| `Republish-Remote.ps1` | Stops the Windows service on 192.168.3.15, publishes Release straight to the remote deploy folder via SMB, restarts the service, probes `http://192.168.3.15:5244/Account/Login` for HTTP 200. |

Run both after every meaningful change so your dev copy and production stay in sync.

### Database & migrations

- **Server:** `192.168.3.10` SQL Server, database `SharbatlyQMS`.
- **Connection string:** `appsettings.Production.json` under `ConnectionStrings:Default`. The file is gitignored — never commit it. The dev placeholder in `appsettings.json` is intentionally `__OVERRIDE_IN_ENV_OR_LOCAL_CONFIG__`.
- **Migrations:** SQL files under `app\db\V*.sql`. Applied by `app\SharbatlyQMS.Migrate` (a small .NET tool). Every migration is idempotent — re-running is safe.

| Migration | What it added |
|---|---|
| V28 | `qms_sap_container_cache.sto` column |
| V29 | `Users.PlantCode` (plant scoping for operators) |
| V30 | One-shot purge of pre-plant test data |
| V31 | `Submitted` status + Supervisor role |
| V32 | Index on `qms_sample(created_at)` for the data hub |
| V33 | Backfill of 113 orphan image-link rows |
| V34 | `qms_perspective` table + `vw_qms_flat_defects` view for the analyzer |

### Authentication (Active Directory)

- The app **binds against AD at the moment of sign-in only** — no `DirectorySearcher` calls, no service-account searches per request. This is a hard security rule.
- Config keys:
  - `Ad:Domain` — short domain name.
  - `Ad:LdapPath` — LDAP path for the user OU.
  - `Ad:ServiceUser`, `Ad:ServicePassword` — for AD lookups during user-list refresh only (admin pages).
- Usernames are stored as the `sAMAccountName` (no `@domain` suffix) in the `Users` table.

### SAP integration

- Read-only. The app **never writes back to SAP** — every SAP call goes through `ISapClient`, which has no write methods. Hard rule.
- Material master and vendor master are mass-synced into the cache tables (`qms_sap_material_cache`, `qms_sap_vendor_cache`) on a schedule defined under **Admin → Settings → SAP sync**.
- PO / shipment / container data is search-only — pulled live when the operator looks it up, snapshotted at arrival creation into `qms_arrival_item` / `qms_shipment_snapshot` / `qms_sap_container_cache`.

### Roles & permissions

Hierarchy: **Viewer < Operator < Supervisor < Manager < SiteAdmin**. `ClaimManager` is a peer role next to Manager, scoped to the Claim Management module.

| Policy | Roles allowed |
|---|---|
| `AdminOnly` | SiteAdmin |
| `ManagerOrAdmin` | Manager, SiteAdmin |
| `SupervisorOrAbove` | Supervisor, Manager, SiteAdmin |
| `OperatorOrAbove` | Operator, Supervisor, Manager, SiteAdmin |
| `ClaimManagerOrAdmin` | ClaimManager, SiteAdmin |

> **For Admins** — `AdminController` is class-level gated by `ManagerOrAdmin`. Every SiteAdmin-only action inside it (Users, Settings, Audit, View-As) carries its own `[Authorize(Policy = AdminOnly)]`. Do not remove that — managers should never see the Users page.

### Plant scoping for operators

`Users.PlantCode` (added by V29) restricts an operator's Pending / Arrivals / Quality-Order lists to one plant. Leave it `NULL` to give the user every plant. Supervisors and above ignore the scope.

### View-As (manager impersonation for testing)

A Manager or SiteAdmin can preview the app as another role. The real role is stored in the `OriginalRole` claim; the impersonated role is in the standard role claim. Only real admins see the **View as** drop-down in the top-right.

### Settings tabs

- **Branding** — site name, logo, favicon. The favicon is served from `Settings.FaviconHref` so it survives package updates.
- **Mail** — SMTP host, port, username, password, default sender. Toggle the QO mail template enabled here.
- **Thumbnails** — PDF and grid sizes for embedded photos, with a "cover" vs "fit" toggle.
- **SAP sync** — per-endpoint enable + cron-style schedule for Material Master and Vendor Master.

### Per-feature setup checklist when going to a new plant

1. AD: confirm the LDAP path lists the plant's users.
2. SQL: run all migrations through V34 against the new plant's DB if it's a separate instance.
3. Defect catalog: seed for the material groups the new plant handles.
4. Reading types: same.
5. Sample headers + material headers: same.
6. Users: create rows with the correct `PlantCode`.
7. Settings → Branding: confirm the right logo.
8. Settings → Mail: confirm SMTP works (test send).

### The data-hub-pack

The data hub + Perspective Analyzer is packaged under `data-hub-pack/` for reuse on future projects. If you start a new ASP.NET Core app that needs a pivot-style reporting layer, paste `data-hub-pack/INSTALL_PROMPT.md` into a fresh Claude Code session and follow it. The pack is self-contained and includes the Plotly bundle, the analyzer JS, the SQL view template and the ClosedXML export builder.

## 6. Troubleshooting & FAQ

**I can't sign in — it says invalid credentials.**
Type your normal Windows username (the short name, no `@domain`) and password. If you typed it correctly and it still fails, you're either off the company LAN/VPN, your AD account is locked, or you've never been added to QMS. Call IT and tell them you need QMS access — they'll add you to AD and create your row in QMS.

**I'm an operator and I don't see Pending Containers — the list is empty.**
You're scoped to one plant and there are no pending containers for it right now. Try **Retrieve latest containers** in the header. If you cover several plants, ask the admin to widen your `PlantCode` (or set it to blank).

**The sample form says "sum of defects exceeds sample size".**
The total of every defect number you typed can't be higher than the sample size. Either lower a number or raise the sample size for that sample.

**I uploaded a photo and the page said success but I don't see it.**
Hard-refresh the page (Ctrl+F5). The counter and gallery now update live, but if you uploaded before that fix (before 2026-06-20) you may need to refresh once.

**The Perspective analyzer says "Pivot failed".**
Open your browser's developer tools (F12), click the **Network** tab, click the failed POST to `/Reports/Pivot`. The response usually tells you whether the registry rejected your dim/measure, the antiforgery token was missing, or the SQL view is missing a column. If nothing's obvious, copy the message and send it to IT.

**Export pivot to Excel downloads a zero-byte file.**
The app couldn't build the workbook. This is almost always a service restart issue — call IT and ask them to restart the `SharbatlyQMS` Windows service. The next click should work.

**Where do I find an older audit entry?**
Open **Audit log** from the Admin menu. Filter by date, actor or entity. Export the result to Excel if you need to share it.

**I want to retire a defect without losing history.**
Open **Admin → Defect catalog**, find the defect, untick **Active**, save. New samples won't see it; old samples still show it.

**I deleted a sample by mistake.**
You can't undo it from the UI — the row is soft-deleted (`is_deleted = 1`). Ask IT to flip the flag back if it matters. Audit log keeps the deletion record either way.

**How do I reopen a Finished Quality Order?**
You need Manager or SiteAdmin. Open the closed QO and click **Reopen**, type a short reason, **Confirm**. The QO returns to **Open** state and supervisors can edit it again.

**How do I run a migration on the shared database?**
`app\SharbatlyQMS.Migrate\bin\Release\net9.0\SharbatlyQMS.Migrate.exe apply "<connection-string>" "app\db\V35__your-new-migration.sql"`. The tool prints each statement and the file is idempotent so a re-run is safe. Don't apply migrations on the dev box first — the database is shared.

## 7. Glossary

- **Arrival** — One container that has been opened in QMS, with checklist + material lines + Quality Order link.
- **BOL** — Bill of Lading, a shipping document number SAP exposes.
- **Brix** — Sugar-content reading taken on fruit samples.
- **Claim** — An insurance claim filed against a Closed Quality Order when the supplier owes the company.
- **Container** — Refrigerated shipping container; the unit SAP tracks.
- **Data Hub** — The flat-row report at `/Reports/FlatDefects`, with 15 filters and Excel export.
- **Defect** — A specific quality issue recorded against a sample (e.g. Creasing, Hail damage).
- **Defect catalog** — The master list of defects per material group (Admin → Defect catalog).
- **Drill filter** — A "include-only" filter you add inside the Perspective analyzer to limit the pivot to specific values.
- **Ebeln** — SAP purchase-order number.
- **Finish** — Supervisor action that closes a Submitted Quality Order.
- **MARA** — SAP material-master table; the source for material codes, descriptions, categories.
- **Material group** — SAP grouping of similar materials; drives which defects and reading types apply.
- **Material header** — Per-QO fields about the material in this shipment (pack type, brand, variety, etc.).
- **PDF report** — The PDF the app generates for each Quality Order to send to the supplier.
- **Perspective** — A saved pivot configuration in the Perspective analyzer.
- **Perspective analyzer** — The pivot UI sitting under the data hub.
- **Plant** — A Sharbatly facility (warehouse / cold store). Operators are usually scoped to one.
- **PO** — Purchase order. The SAP document under which a container ships.
- **Quality Order (QO)** — The QMS workflow record for inspecting one arrival.
- **Reading type** — A measurement type with unit and range (e.g. Brix, firmness).
- **Sample** — One inspection of one carton (or set of cartons) under a Quality Order.
- **Sample header** — Per-sample fields the inspector fills (grower, pack code, date code).
- **Sample scope** — One Carton or Multi-Carton — the basis on which the sample was taken.
- **Sample size** — How many fruit/items are in the sample being inspected.
- **SAP** — The company ERP; the system of record for purchase, container and material data.
- **Severity** — Defect severity (Major / Minor / Critical / Other) from the catalog.
- **Site URL** — The address you open in your browser. For Sharbatly QMS this is `{{site_url}}`.
- **STO** — Stock Transport Order, a SAP document type SAP exposes alongside POs.
- **Submit / Finish** — The two-stage workflow: operator submits a QO for review, supervisor finishes it.
- **Supervisor** — The role that reviews submitted QOs. Above Operator, below Manager.
- **View-As** — Manager/SiteAdmin feature for previewing the app as another role for testing.
