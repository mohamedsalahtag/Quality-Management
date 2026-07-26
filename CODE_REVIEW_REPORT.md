# Sharbatly QMS — Comprehensive Code, Logic & UI/UX Review

**Date:** 2026-07-02
**Reviewed branch:** `001-audit-trail` @ commit `3d99509` (including the uncommitted `ReportsController.cs` change in the working tree)
**Live system walked:** `http://192.168.3.15:5244` (logged in as `axuser`, effective role SiteAdmin)
**Reviewer:** automated multi-pass code review (security, business logic, code quality, reports/data, frontend) + live browser walkthrough of 21 pages, all 6 themes, desktop + mobile.
**Scope of change made by this review:** none — this is a report only. No application code was modified. Screenshots were written under `docs/review/screenshots/`.

---

## 1. Executive summary

The QMS is a **well-architected, genuinely solid application**. The security fundamentals are strong (parameterised SQL everywhere including the dynamic pivot engine, CSRF on every POST, safe role-impersonation, LDAP escaping, sensible cookie flags), the data model is normalised with real constraints and race-proof unique indexes, background sync and audit writes are transactional, and the UI is fast, consistent, themeable and error-free in normal use. Nothing here is a rewrite; the findings are targeted fixes on a healthy codebase.

The review found **no remotely-exploitable critical vulnerability**. The issues that matter cluster into four themes:

1. **Concurrency** — every status transition (Quality Order, Arrival, Claim) is *read-then-unconditionally-write* with no `WHERE status_code = @expected` guard, and the schema's `ROWVERSION` columns are never used. Two users acting at once can double-process or silently overwrite each other's decisions.
2. **Edit-lock / authorization gaps** — sample-delete and all image upload/delete endpoints skip the "Open only" edit lock and the plant-scope check that every other mutator enforces, so **inspection evidence on already-submitted/closed orders (and decided claims) can be altered, unaudited**. Several read endpoints also leak cross-plant data by ID.
3. **Reporting data-correctness** — the flat-defects Excel export silently misaligns columns, and pivot totals are arithmetically wrong for 5 of 6 aggregations. These produce **wrong numbers that look right**.
4. **Audit-trail completeness (the current feature branch)** — the SiteAdmin "Purge All" deletes the audit log itself (contradicting the append-only guarantee the feature exists to provide); and whole categories of change (user/role edits, catalog config, settings, images, report sends) write no audit entry at all.

### Findings by severity

| Severity | Count | What it means |
|---|---|---|
| **High** | 14 | Data integrity, access control, or compliance impact; fix before relying on the audit trail or the analytics exports for decisions. |
| **Medium** | 28 | Real defects with a workaround or limited blast radius; fix in the normal cycle. |
| **Low** | 20 | Polish, hygiene, maintainability, minor UX. |
| **Verified-OK** | 50+ | Things checked and confirmed correct — see §9. |

### Top 10 actions (do these first)

1. **Make every status transition conditional** (`AND status_code=@current`, check rows-affected) — Quality Orders, Arrivals, Claims. [H-1]
2. **Add the Open-only edit gate + plant scope + audit to sample-delete and image upload/delete.** [H-2, H-3]
3. **Remove `qms_audit_log` from the "Purge All" delete list** (and add the missing claim tables so purge stops crashing). [H-4]
4. **Fix the flat-defects Excel column misalignment** (pre-compute the dynamic column set before writing rows). [H-6]
5. **Fix pivot totals** for AVG/MIN/MAX/COUNT_DISTINCT (or suppress totals for non-additive aggregations). [H-7]
6. **Cap export size / stream to response** to remove the out-of-memory risk on large exports. [H-8]
7. **Extend audit coverage** to user/catalog/settings/image/report-send mutations; fix the meaningless `UpdateSample` diff. [H-9]
8. **Guard the SAP master-cache prune** so a truncated-but-"successful" pull can't wipe the material/vendor cache. [H-10]
9. **Add plant-scope checks to `SamplePanel`/`MaterialPanel`/`Sample`** and owner/traversal validation to image endpoints. [H-3, H-5]
10. **Decide the audit-trail scope**: restore the per-record history panel or amend the spec/tasks to match reality; add login rate-limiting. [H-13, H-11]

---

## 2. Scope & method

Five independent static passes read the code (≈18,000 lines of backend C#, 8,800 lines of Razor, and the JS bundle) and cited findings to `file:line`. A parallel live walkthrough drove the running production app read-only with Playwright — 21 pages at 1440×900 and 390×844, all six themes, tablekit sorting, the Perspective Analyzer pivot run, and deliberate 404/bad-ID probes — capturing screenshots, console errors and failed network requests (screenshots in `docs/review/screenshots/`). Every High/Critical finding in this report was re-read at its cited lines by the lead reviewer to confirm it before inclusion; false positives were dropped.

**Dimensions covered:** security & access control · business logic & state machines · data integrity & concurrency · data layer & schema · reports (PDF/Excel) & analytics · audit-trail vs its spec · code quality & error handling · frontend, accessibility & visual/UX.

---

## 3. High-severity findings

### H-1 — Status transitions have no concurrency guard (Quality Order, Arrival, Claim)
**Where:** [`QualityOrderService.cs:225`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:225) (`Transition`), [`ArrivalService.cs:500`](app/SharbatlyQMS.Web/Services/ArrivalService.cs:500) (`CompleteAsync`/`ReopenForEditAsync`), [`ClaimService.cs:159`](app/SharbatlyQMS.Web/Services/ClaimService.cs:159). `row_version` columns exist ([`V02__qms_schema.sql:147`](app/db/V02__qms_schema.sql:147)) but are never read.
**What:** The code `SELECT`s the current status, validates it in C#, then runs `UPDATE ... SET status_code='Closed' WHERE quality_order_id=@qoId` — with **no `AND status_code=@current`** — under plain READ COMMITTED. Two concurrent requests both read the old status, both pass validation, both commit.
**Impact:** Two supervisors can both "Finish" the same QO (duplicate Closed audit + status-history rows, second overwrites `closed_by`/`closed_at`); a Cancel and a Close can interleave so the audit trail records a decision that was silently overwritten; **a Quality Manager can overwrite a Claim Manager's just-made claim decision** (a claim shows "Passed QC" with the CM's decision timestamp — a contradictory commercial record).
**Fix:** Make every transition `UPDATE` conditional on the expected from-status and check `rows-affected == 1`, returning the existing "cannot transition" error otherwise. Optionally use the existing `row_version` for optimistic concurrency on edit forms.
*(Sources: LOG-01, LOG-07, LOG-11 — independently confirmed by re-reading the code.)*

### H-2 — Samples can be deleted from Submitted/Closed Quality Orders (edit-lock bypass)
**Where:** [`QualityOrdersController.cs:803`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:803) (`DeleteSample`), [`:836`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:836) (`DeleteSampleAjax`).
**What:** Both actions check only plant scope (`EnsureCanReadQoAsync`) then soft-delete. Every other mutator (`CreateSample`, `SaveSample`, `SaveOverride`, `SaveMaterialHeaderAjax`) refuses when `qo.StatusCode != Open`. The delete pair has no such check.
**Impact:** Any Operator can delete inspection samples from a QO that was already Submitted for review or Finished — **after the PDF report was issued and after a claim decision was made on that data**. This is exactly the tampering window the V31 edit-lock was built to close.
**Fix:** Add the same `EnsureEditable*` gate at the top of both delete actions (or enforce QO status inside `SoftDeleteSampleAsync`).
*(Source: LOG-02 — confirmed.)*

### H-3 — Image upload/delete: no edit-lock, no owner/plant check, no audit, and `ownerType` reaches the filesystem path
**Where:** [`ImagesController.cs:16`](app/SharbatlyQMS.Web/Controllers/ImagesController.cs:16); [`ImageService.cs:97`](app/SharbatlyQMS.Web/Services/ImageService.cs:97).
**What:** `Upload(ownerType, ownerId, …)` and `Delete(imageLinkId, …)` gate only on the `OperatorOrAbove` policy. They do not verify the owner record exists, is editable (Open/Draft), or is in the caller's plant, and they write no audit entry. A controller comment even states editability is assumed "because the controls are only reachable from an editable gallery" — i.e. enforced client-side only. `UploadAsync` builds `Path.Combine(WebRootPath, "uploads", ownerType, …)` from the **raw posted string**.
**Impact:** Photo evidence on a Closed QO or decided claim can be added/removed by any Operator with no trace, altering the QMS PDF and the claim record after the fact. A crafted `ownerType` (e.g. `..\..`) can also write files outside `wwwroot/uploads`.
**Fix:** Whitelist `ownerType` against the known set, resolve the owner, apply the parent's editable + plant gate, and write audit entries for attach/detach.
*(Sources: LOG-03, SEC-02 — confirmed.)*

### H-4 — "Purge All Data" deletes the audit log and crashes on any claim
**Where:** [`AdminController.cs:303`](app/SharbatlyQMS.Web/Controllers/AdminController.cs:303) (`DELETE FROM qms_audit_log`), reseed at [`:314`](app/SharbatlyQMS.Web/Controllers/AdminController.cs:314); delete list [`:286`](app/SharbatlyQMS.Web/Controllers/AdminController.cs:286).
**What:** The SiteAdmin Danger-Zone reset deletes every row of `qms_audit_log` and reseeds its identity — directly contradicting the append-only guarantee (`spec.md`: "The audit log MUST be append-only … This includes SiteAdmin accounts"). Separately, the delete list omits `qms_claim`/`qms_claim_note`/`qms_claim_read_marker`, so with any claim present the `DELETE FROM qms_quality_order` throws a foreign-key violation and the whole purge rolls back (unhandled 500). It also never resets `qms_sap_container_cache.has_arrival`, so after a purge every previously-arrived container stays hidden from Pending Containers.
**Impact:** A single confirmed click destroys the forensic trail the audit feature exists to guarantee; and in its current form the purge cannot even complete once real claim data exists.
**Fix:** Remove `qms_audit_log` from the purge list (record instead an audit entry that an operational purge occurred); add the claim tables to the delete order; reset the container cache flags in the same transaction. Any sanctioned audit removal should be a governed DB migration.
*(Sources: RPT-01, LOG-05, LOG-12 — confirmed.)*

### H-5 — Plant-scope IDOR on Quality-Order sub-resource endpoints
**Where:** [`QualityOrdersController.cs:139`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:139) (`SamplePanel`), [`:240`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:240) (`MaterialPanel`), [`:475`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:475) (`Sample`), and the material-header/override save/clear AJAX actions.
**What:** `Details` and `SamplePhotosPanel` call `EnsureCanReadQoAsync`; these do not. A plant-scoped Operator (the only role carrying a non-empty `PlantCode` claim) can read — and via the save/override actions, mutate — another plant's sample/material data by supplying an out-of-plant id.
**Impact:** Cross-plant information disclosure and (on the save endpoints) cross-plant tampering. Blast radius is limited to operator accounts on an internal LAN, hence High-but-not-critical.
**Fix:** Resolve the parent QO from `sampleId`/`qoMaterialId` and call `EnsureCanReadQoAsync` in each, mirroring `SaveSampleAjax`.
*(Sources: SEC-01, LOG-17, QUA-04 — confirmed via the guard pattern.)*

### H-6 — Flat-defects Excel export silently misaligns columns
**Where:** [`ReportsController.cs:597`](app/SharbatlyQMS.Web/Controllers/ReportsController.cs:597)–[`679`](app/SharbatlyQMS.Web/Controllers/ReportsController.cs:679).
**What:** Dynamic columns (material headers, sample headers, readings) are discovered from the first row. When a later row introduces a *new* code, it is appended and the header row rewritten — shifting the following blocks one column right — **but the rows already written keep the old layout**. Because different material groups have different reading/header sets by design, this is hit by any multi-group export.
**Impact:** Silent data corruption: earlier rows' sample-header/reading values sit under the wrong column headers in the file that feeds downstream analysis. The file looks complete, so the error goes unnoticed.
**Fix:** Pre-compute the full dynamic key set (cheap `SELECT DISTINCT` over the header/reading catalogs) before writing any data row, or buffer rows until the key set is stable.
*(Sources: RPT-03, QUA-01 — confirmed.)*

### H-7 — Pivot totals are wrong for AVG / MIN / MAX / COUNT_DISTINCT
**Where:** [`PivotService.cs:203`](app/SharbatlyQMS.Web/Services/Reports/PivotService.cs:203)–[`215`](app/SharbatlyQMS.Web/Services/Reports/PivotService.cs:215).
**What:** Row/column/grand totals are computed by summing each cell's aggregated value regardless of aggregation. Only SUM and COUNT are correct. For `AVG(DefectPercentage)` the "Total" is the *sum of per-column averages* (three 10% columns show "Total 30%"); `COUNT_DISTINCT` double-counts across columns; MIN/MAX totals are meaningless sums. The registry explicitly allows these aggregations.
**Impact:** The most prominent numbers in the analyzer and its Excel export (bold totals row/column and grand total) are wrong for 5 of 6 supported aggregations.
**Fix:** Compute totals with the measure's own aggregation (ROLLUP/GROUPING SETS, or track count+sum for AVG etc.), or suppress totals for non-additive aggregations.
*(Source: RPT-04 — confirmed.)*

### H-8 — Excel exports are fully in-memory with no row cap (out-of-memory risk)
**Where:** [`ReportsController.cs:692`](app/SharbatlyQMS.Web/Controllers/ReportsController.cs:692) (`FlatDefectsExcel`), [`AuditController.cs:88`](app/SharbatlyQMS.Web/Controllers/AuditController.cs:88)–[`154`](app/SharbatlyQMS.Web/Controllers/AuditController.cs:154) (`Export`).
**What:** Both build the whole ClosedXML workbook in memory, then `SaveAs(ms)`, then `File(ms.ToArray(), …)` — a second full copy. `FlatDefectsExcel` emits one row per (sample × every catalog defect of its group) with no cap; the 30-day default window is bypassed by picking a wide date range. The audit `Export` XML-doc comment claims it "streams … so memory stays bounded" — it does not.
**Impact:** A single large export can spike the worker by hundreds of MB; concurrent exports (the audit in-flight guard is per-user) can recycle the Windows service for everyone.
**Fix:** Return the `MemoryStream` directly (drop `ToArray()`), add a hard row cap with a friendly "narrow your filter" message, and correct the misleading comment. The audit soft-cap (~50k) and wide-range warning from the spec are also unimplemented.
*(Sources: RPT-05, QUA-02 — confirmed.)*

### H-9 — Audit-trail coverage gaps: whole categories of change are never audited
**Where:** `WriteAsync` is called only from `ArrivalService`, `ClaimService`, `QualityOrderService`. Not called by: user management ([`DbService.cs:108`](app/SharbatlyQMS.Web/Services/DbService.cs:108) via AdminController create/edit/reset-password/toggle/delete), catalog config ([`AdminController.cs:740`](app/SharbatlyQMS.Web/Controllers/AdminController.cs:740)+ defects/categories/reading-types/header-fields), settings ([`DbService.cs:49`](app/SharbatlyQMS.Web/Services/DbService.cs:49) — SAP credentials, schedules, thresholds), images (H-3), report sends. Weak diff: [`QualityOrderService.cs:489`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:489) logs `{touched:true}` for a sample-size change.
**Impact:** On the audit-trail branch, the log answers "who edited the inspection" but not "who changed the defect catalog / thresholds / user roles that the inspection depends on"; a sample-size edit — which changes every defect percentage — is recorded as an empty, meaningless diff.
**Fix:** Add `WriteAsync` calls (Config/User/Catalog entity types) to the admin mutation paths; fix `UpdateSampleAsync` to capture old/new `sample_size` + `size_overridden`; audit image attach/detach and report sends.
*(Sources: LOG-05, QUA-09 — confirmed.)*

### H-10 — SAP master-cache prune can wipe valid rows after a truncated-but-"successful" pull
**Where:** [`SapSyncService.cs:84`](app/SharbatlyQMS.Web/Services/Sap/SapSyncService.cs:84) (prune), [`SapODataClient.cs:152`](app/SharbatlyQMS.Web/Services/Sap/SapODataClient.cs:152) (`FetchAllAsync`).
**What:** After a pull with `ok && rows > 0`, the service runs `DELETE FROM {cache} WHERE synced_at < @startedAt`. A mid-run HTTP failure correctly skips the prune. But `FetchAllAsync` returns `ok=true` when it hits the 500-page safety cap or when SAP returns an incomplete HTTP-200 result — with no sanity threshold, one row returned is enough to prune the other ~28,000.
**Impact:** A single bad SAP response can empty the material/vendor cache, blanking Variety/Class/Origin/Brand on QO details/PDF/data-hub and nulling the sample form's effective sample size. (Amplified by H-11-adjacent LOG-08: the sync double-fires each scheduled hour.)
**Fix:** Only prune when the fetched count is plausible (refuse if it would delete more than N% of the table); treat hitting the safety cap as `ok=false` for prune purposes.
*(Source: LOG-04 — confirmed.)*

### H-11 — No brute-force protection on login
**Where:** [`AccountController.cs:46`](app/SharbatlyQMS.Web/Controllers/AccountController.cs:46) (Login POST); `Program.cs` has no rate limiter.
**What:** No failed-attempt counter, lockout, IP throttle or CAPTCHA. The local BCrypt fallback (bootstrap admin + any pre-AD account) is open to unlimited online guessing; BCrypt slows but does not stop sustained brute force. Compounded by the 6-character minimum password (SEC-06).
**Fix:** Add ASP.NET rate limiting keyed on username+IP for the login endpoint and/or a failed-attempt lockout; raise the local-account password floor.
*(Source: SEC-03 — confirmed.)*

### H-12 — Icons and dashboard charts load from a public CDN (breaks offline / air-gapped LAN)
**Where:** [`_Layout.cshtml:71`](app/SharbatlyQMS.Web/Views/Shared/_Layout.cshtml:71) and [`Login.cshtml:20`](app/SharbatlyQMS.Web/Views/Account/Login.cshtml:20) (Bootstrap Icons via `cdn.jsdelivr.net`); [`Home/Index.cshtml:315`](app/SharbatlyQMS.Web/Views/Home/Index.cshtml:315) (Chart.js via CDN).
**What:** Bootstrap, jQuery, Plotly are self-hosted, but Bootstrap Icons and Chart.js are not. If the server has no internet route, every `bi-*` glyph renders as an empty box (icon-only buttons become blank) and the dashboard charts silently fail (`dashboard.js` bails when `Chart` is undefined).
**Impact:** Potential visible functional break depending on whether the production host has outbound internet. **Verify this** — if the box reaches the internet it is cosmetic-risk only; if it is firewalled to the LAN + SAP + AD it is a real break.
**Fix:** Vendor `bootstrap-icons` (woff2+css) and `chart.js` into `wwwroot/lib/` and reference locally; remove the CDN links.
*(Source: FE-01 — confirmed present; impact conditional on network topology.)*

### H-13 — Audit per-record history panel removed, but the spec/tasks still say it's done; role model diverged
**Where:** [`QualityOrders/Details.cshtml:496`](app/SharbatlyQMS.Web/Views/QualityOrders/Details.cshtml:496) ("Per-entity audit panels removed 2026-06-13"); `AuditController` has no `HistoryPanel` action; `AuditService.GetForRecordAsync`/`GetForCompositeRecordAsync` have zero callers (dead code); `_AuditHistoryRows.cshtml` is orphaned.
**What:** User Story 1 (the P1 MVP — "investigate the history of a single record") is not reachable in the UI, yet `tasks.md` T023–T028 are marked done. The `Auditor` role was retired (V16) and the global audit page is `AdminOnly`, but `spec.md` still mandates an Auditor role and Quality-Manager visibility (FR-009/FR-011). The audit "Load more" and "Export" links also drop the entity-type/action filters (`Views/Audit/Index.cshtml`), so paging past page 1 widens the result set and a filtered export contains more rows than the screen showed.
**Impact:** Spec, tasks and reality have diverged on the flagship feature; filtered audit paging/export silently loses the filter (a compliance-report mismatch).
**Fix:** Decide the scope — either restore the `HistoryPanel` endpoint + partial (the service layer is intact) and the Manager/Auditor visibility, **or** formally amend `spec.md`/`tasks.md` to record the SiteAdmin-only, global-only decision; and add `entityTypes`/`actionCodes`/`pageSize` to the load-more and export links.
*(Sources: RPT-02, RPT-11, RPT-19, plus the audit spec-gap table in §7 — confirmed.)*

### H-14 — Deleting an arrival breaks the Pending-Containers pipeline
**Where:** [`ArrivalService.cs:567`](app/SharbatlyQMS.Web/Services/ArrivalService.cs:567) (`DeleteAsync`); container flag logic [`ContainerCacheService.cs:244`](app/SharbatlyQMS.Web/Services/ContainerCacheService.cs:244).
**What:** `DeleteAsync` blocks only on an *active* QO; a *cancelled* QO still references the arrival, so the final `DELETE FROM qms_arrival` throws a FK violation and rollback — the arrival can never be deleted once it has had a cancelled QO. And nothing ever resets `qms_sap_container_cache.has_arrival` to 0, so when an arrival *is* deleted its container keeps `has_arrival=1` with a dangling `arrival_id` and silently drops out of the QC intake queue forever.
**Fix:** In `DeleteAsync`, handle cancelled-QO children explicitly and add `UPDATE qms_sap_container_cache SET has_arrival=0, arrival_id=NULL WHERE arrival_id=@arrivalId` in the same transaction.
*(Source: LOG-06 — confirmed schema/logic.)*

---

## 4. Medium-severity findings

### Business logic & data integrity
- **M-1 Arrival Complete/Reopen check-then-act race** — same class as H-1, across separate connections; a QO can end up attached to a still-editable Draft arrival. [`ArrivalService.cs:500`](app/SharbatlyQMS.Web/Services/ArrivalService.cs:500) *(LOG-07)*
- **M-2 Per-sample `sample_size` accepts 0/negative; defect % falls back to unvalidated client value** — a tampered/buggy POST stores impossible percentages (e.g. 4000%) that skew the "Avg defect %" KPI. The material-level override validates ≥1; the per-sample path does not. [`QualityOrdersController.cs:588`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:588) *(LOG-10)*
- **M-3 Dashboard "Avg defect %" is a distorted average-of-averages over zero-filled rows** — the KPI moves when the defect catalog is edited (adding a defect type lowers the number with no quality change) and over-weights heavily-sampled QOs. [`DashboardService.cs:93`](app/SharbatlyQMS.Web/Services/DashboardService.cs:93) *(LOG-13, RPT-08)*
- **M-4 "All materials sampled" precondition enforced only in the controller, not the service, and not inside the transition transaction** — a QO can be Submitted/Finished with unsampled materials via timing (trivially, given H-2). [`QualityOrdersController.cs:345`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:345) *(LOG-15)*
- **M-5 Duplicate reading rows in the QO PDF / sample drawer** when a reading code exists in multiple material groups — the flat export dedupes; the PDF and drawer do not. [`QualityOrderService.cs:534`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:534) *(QUA-06)*
- **M-6 Flat data hub silently drops samples/defects** — a sample whose material group has an empty catalog yields zero rows (the sample vanishes from preview and Excel); entered-but-deactivated defects are dropped; analyzer AVG (entered-only) differs from the hub's zero-filled AVG on the same page. [`QualityOrderService.cs:1561`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:1561) *(RPT-08)*

### Reports, exports & data layer
- **M-7 `percent` number format double-scales 0–100 measures** — a 12.5% value renders as 1,250.00% in both the grid and the workbook. [`ReportsController.cs:1000`](app/SharbatlyQMS.Web/Controllers/ReportsController.cs:1000), [`perspective-analyzer.js:660`](app/SharbatlyQMS.Web/wwwroot/js/perspective-analyzer.js:660) *(RPT-07)*
- **M-8 Quality-Report PDF `ShowEntire` cards can exceed one page and throw** — a large defect catalog makes QuestPDF raise a layout exception, failing every PDF for that QO (data-driven breakage). [`QualityReportPdf.cs:153`](app/SharbatlyQMS.Web/Services/Pdf/QualityReportPdf.cs:153) *(RPT-06)*
- **M-9 Pivot measure fan-out** — SUM/AVG of sample-grain columns over the defect-grain view inflates "Sample size (SUM)" and skews transit-day averages by defect count. [`PivotRegistry.cs:90`](app/SharbatlyQMS.Web/Services/Reports/PivotRegistry.cs:90) *(RPT-09)*
- **M-10 Nondeterministic `OUTER APPLY … TOP 1` without `ORDER BY`** — PO date / STO per arrival can differ between runs, so the default PO window and PO-month buckets classify the same arrival differently; exports aren't reproducible. [`V34__add_qms_perspective.sql:125`](app/db/V34__add_qms_perspective.sql:125), [`QualityOrderService.cs:1452`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:1452) *(RPT-10)*
- **M-11 Audit Excel export can crash on a JSON snapshot > 32,767 chars** — Excel's hard cell limit; one oversized Deleted-record snapshot aborts the whole export. [`AuditController.cs:143`](app/SharbatlyQMS.Web/Controllers/AuditController.cs:143) *(RPT-12)*
- **M-12 Missing indexes for common query paths** — `qms_sample(quality_order_id)`, `qms_audit_log(changed_by)` / `(entity_type)`, `qms_quality_order(status_code)` are unindexed; audit filtering without a date range scans the whole index (risk vs the spec's 10M-row / p95<1s target). [`V02__qms_schema.sql`](app/db/V02__qms_schema.sql) *(RPT-14)*
- **M-13 V33's orphan-image root cause not prevented** — asset + link INSERTs still run without a transaction, so any failure between them recreates the orphan class V33 cleaned up. [`ImageService.cs:117`](app/SharbatlyQMS.Web/Services/ImageService.cs:117) *(RPT-13, LOG-16)*

### Sync & background services
- **M-14 AutoSync runs each scheduled sync twice per hour** — the 50-minute suppression window is shorter than the 60-minute hour match, so a sync completing before minute :09 fires again. Doubles SAP load and the H-10 prune exposure. [`AutoSyncService.cs:64`](app/SharbatlyQMS.Web/Services/AutoSyncService.cs:64) *(LOG-08)*
- **M-15 Container-polling retry storm + manual/auto overlap** — during a SAP outage the due-cursor never advances (it reads the newest *success*), so a full pull retries every 30s; the scheduler's in-process guard and the admin's DB-row guard don't share state, so pulls can collide. [`ContainerPollingService.cs:94`](app/SharbatlyQMS.Web/Services/ContainerPollingService.cs:94) *(LOG-09)*
- **M-16 Container cache refresh does a per-row MERGE** — each 200-row SAP page becomes 200 sequential statements; a full sweep runs every poll. `SapSyncService` already solved this with `SqlBulkCopy` + staged MERGE. [`ContainerCacheService.cs:67`](app/SharbatlyQMS.Web/Services/ContainerCacheService.cs:67) *(QUA-08)*
- **M-17 Background sync/prime failures are surfaced nowhere an admin routinely looks** — only the SAP settings tab shows them; AD-cache-priming failures have no UI at all. A silently failing nightly Material Master sync degrades MARA enrichment for days. [`AutoSyncService.cs:80`](app/SharbatlyQMS.Web/Services/AutoSyncService.cs:80) *(QUA-05)*

### Code quality & error handling
- **M-18 AJAX/panel endpoints return HTML 500 (or 500-for-not-found) instead of JSON** — `SamplePanel` throws for a missing id (should be `NotFound()`); `BrowseAdUsersAjax`, `SyncStatusAjax`, `MaterialPanel`, image up/delete aren't wrapped. Users get a generic "request failed" and support must read the Event Log. [`QualityOrdersController.cs:149`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:149) *(QUA-03)*
- **M-19 Admin catalog SQL is 14 hand-rolled `SqlConnection` sites via service-locator, untestable, and unaudited** — see also H-9. [`AdminController.cs`](app/SharbatlyQMS.Web/Controllers/AdminController.cs) *(QUA-09)*
- **M-20 `CatalogCache.Invalidate()` can't clear the dynamic section-map keys** — up to 60 minutes of stale display-section grouping after an admin edit, with no way to force-refresh short of a restart. [`CatalogCache.cs:98`](app/SharbatlyQMS.Web/Services/CatalogCache.cs:98) *(QUA-10)*
- **M-21 `AdService` does synchronous LDAP I/O on the login path** — a slow/unreachable DC pins a thread-pool thread for the full timeout per attempt; a morning login burst can starve the pool. [`AdService.cs:36`](app/SharbatlyQMS.Web/Services/AdService.cs:36) *(QUA-11)*

### Frontend (Medium)
- **M-22 Unbounded list pages + client-only pagination + "Select all" selects paged-out rows** — the Users bulk-delete can submit invisible page-2 rows; Arrivals/QO/Pending Index ship every matching row as HTML with leading-wildcard `LIKE` scans. [`tablekit.js:186`](app/SharbatlyQMS.Web/wwwroot/js/tablekit.js:186), [`Admin/Users.cshtml:392`](app/SharbatlyQMS.Web/Views/Admin/Users.cshtml:392); server side [`QualityOrderService.cs:47`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:47) *(FE-03, QUA-07; live UI-03)*
- **M-23 jquery-validation shipped but never referenced; three different validation UX patterns coexist** — Login alert box vs Profile inline text vs native browser bubbles; most forms round-trip to the server to show an error. [`_ValidationScriptsPartial.cshtml`](app/SharbatlyQMS.Web/Views/Shared/_ValidationScriptsPartial.cshtml) *(FE-02)*
- **M-24 Sepia theme muted-text contrast below WCAG AA; several views hard-code light-only colors** — `.text-muted`/`small`/`.form-text` fail AA on sepia; the claim chat panel, Yes/No toggles and pivot zones stay light-gray under dark/forest/rose/blue. [`theme-pack.css:44`](app/SharbatlyQMS.Web/wwwroot/css/theme-pack.css:44) *(FE-07)*
- **M-25 Sortable table headers are mouse-only and unannounced to screen readers** — no `tabindex`/`role`/`aria-sort`/keydown on `th[data-sort]`. [`tablekit.js:64`](app/SharbatlyQMS.Web/wwwroot/js/tablekit.js:64) *(FE-06)*
- **M-26 ~350 lines of duplicated inline `<style>` belong in site.css; the audit-badge palette diverges between two files** so the same action renders two shades on different pages. [`Arrivals/Details.cshtml:310`](app/SharbatlyQMS.Web/Views/Arrivals/Details.cshtml:310), [`Audit/Index.cshtml:86`](app/SharbatlyQMS.Web/Views/Audit/Index.cshtml:86) *(FE-08)*
- **M-27 `escapeHtml` reimplemented in three files; AJAX rows built via innerHTML concatenation** — XSS-safe today (all call sites escape) but fragile; centralize the helper. [`Admin/Users.cshtml:417`](app/SharbatlyQMS.Web/Views/Admin/Users.cshtml:417) *(FE-05)*
- **M-28 Duplicated defect-stepper/weight JS between the QO drawer and the standalone Sample page, with a decimal-rounding divergence** — the Sample-page `bump()` uses `parseInt` and loses decimal precision. [`QualityOrders/Sample.cshtml:166`](app/SharbatlyQMS.Web/Views/QualityOrders/Sample.cshtml:166) *(FE-04)*

---

## 5. Low-severity findings

**Logic / data:** status-history records `new_status='Reopened'` while the stored value is `'Open'`, breaking the timeline chain (LOG-14 · [`QualityOrderService.cs:257`](app/SharbatlyQMS.Web/Services/QualityOrderService.cs:257)); image soft-delete removes the shared *asset*, hiding it from all galleries, and neither upload nor delete is transactional (LOG-16); defect-entry clamp caps the *sum* of defects at sample size in catalog order, silently under-recording later defects if a fruit can have two (LOG-19 · [`QualityOrdersController.cs:684`](app/SharbatlyQMS.Web/Controllers/QualityOrdersController.cs:684)); `MarkSeen` MERGE without `HOLDLOCK` can throw a transient PK violation on double-open (LOG-18).

**Reports:** Excel date columns are written as *text*, not typed date cells (RPT-15); PDF/Excel formatting is culture-sensitive — an `ar-SA` host would change report content (RPT-16 · consider `CultureInfo.InvariantCulture` at startup); the Quality-Report logo load isn't exception-guarded like the Arrival one, so one bad branding upload disables all QO reports (RPT-17 · [`QualityReportPdf.cs:177`](app/SharbatlyQMS.Web/Services/Pdf/QualityReportPdf.cs:177)); `qms_report_log` covers only 1 of 6 generation paths and stubs `page_count=0` — the externally-emailed supplier PDF is unlogged (RPT-18); pivot Top-N leaves empty columns and doesn't flag truncation in the workbook (RPT-20).

**Code quality:** magic strings where constants exist — 106 `TempData` keys, status literals in `DashboardService`, owner-type strings (QUA-12); zero `ILogger` in the highest-write services so "my sample didn't save" has no production trace (QUA-13); PDF endpoints re-do all image resizing per request with no caching (QUA-14); the uncommitted `ReportsController.cs` diff is **sound** — two cosmetic nits only (QUA-17); `EmailService` fetches SMTP config twice per send (QUA-16).

**Frontend / UX:** four inconsistent feedback channels — bespoke toast vs native `alert()` vs TempData banner vs inline alert, sometimes on the same page (FE-12); date formats differ across views (FE-13, overlaps UI-01 below); Plotly instances never purged between renders (FE-10); pivot chip drag-drop has no keyboard reorder path (FE-11); dead `wwwroot/lib` assets — the two validation libs are 100% unreferenced (FE-14); Back-button placement and primary-action color vary per page (FE-15); no `UseStatusCodePages` so bare 404s (QUA-15, overlaps UI-02).

---

## 6. Live UI/UX walkthrough

Walked read-only as SiteAdmin across 21 pages, both viewports, all six themes. **Overall the UI is fast and clean**: dashboard rendered in 0.06s, the heaviest page (Pending Containers, 1,097 rows) in 0.77s, a 1,097-row tablekit sort in 0.47s, and a pivot run in 0.85s — with **no console errors or failed network requests anywhere** except the deliberate 404 probes. Screenshots are in [`docs/review/screenshots/`](docs/review/screenshots).

| # | Finding | Severity | Evidence |
|---|---|---|---|
| UI-01 | **Malformed timestamps** — headers/rows show `6/30/2026 11:14:39 AM:g` (a stray `:g` because `@Model.CreatedAt.ToLocalTime():g` was meant to be `.ToString("g")`). 4 views. | Medium | `QualityOrders/Details.cshtml:75`, `Arrivals/Details.cshtml:68`, `Audit/_AuditHistoryRows.cshtml:89`, `QualityOrders/_SampleRow.cshtml:40` · screenshots `14`,`15` |
| UI-02 | **Bad/unknown URLs return a blank white page** (bare 404, no layout, no message, no way back). | Medium | `/QualityOrders/Details/999999999`, `/NoSuchPage` · screenshot `16-bad-id` (overlaps QUA-15) |
| UI-03 | Pending Containers ships all **1,097 rows** in one HTML response (fine now, grows with the SAP backlog). | Medium | `03-pending-containers` (overlaps M-22) |
| UI-04 | Read-only Material/Sample modal still shows an **active "Save" button** despite the "Read-only because … Submitted" banner. | Low | `QualityOrders/Details.cshtml:490` · screenshot `44` |
| UI-05 | Dashboard **"Defect Rate" gauge label is clipped** ("Avg defect % (30 d)" cut off), both themes. | Low | `02-dashboard`, `20-theme-dark` |
| UI-06 | Audit page mixes timezones — filters say **"From/To (UTC)"** but the column is **"WHEN (LOCAL)"** (off-by-timezone day boundaries). | Low | `09-audit` |
| UI-07 | Audit "Changes" column shows **raw JSON fragments** to end users. | Low | `09-audit` |
| UI-08 | QO **"Download PDF report" styled destructive-red** (btn-danger) though it's a safe action. | Low | `15-qo-details` |
| UI-09 | Mobile: material-description column **wraps one word per line**. | Low | `33-mobile-qo-details` |
| UI-10 | Dark theme: harsh near-white **chart gridlines** on "Throughput vs QC pace". | Info | `20-theme-dark` |
| UI-11 | SAP material descriptions arrive **truncated at source** ("PPLE KIZURI…") — data-quality note, not an app bug. | Info | `15-qo-details` |

**Confirmed good in the live app:** all 6 themes render FOUC-free and switch cleanly; login validation and AD guidance are clear; the Audit log UI (chip filters, actor/device/source columns, old→new colored diff) is genuinely well done; the data hub caps preview at 200 rows with a clear notice and streams the full Excel; read-only gating on a Completed arrival correctly locks the fieldsets; the mobile navbar collapses properly and tables stay usable; empty states ("No open arrivals", "No closed QOs with defect data…") are present and clear.

---

## 7. Audit-trail branch: spec-compliance gap list

The current branch implements the write path well (atomic, transactional, no-op suppression, keyset pagination, device capture, git-diff rendering). Gaps vs `specs/001-audit-trail/`:

| Requirement | Status |
|---|---|
| FR-006 append-only, no delete surface incl. SiteAdmin | **Violated** — `PurgeAll` deletes `qms_audit_log` (H-4) |
| FR-008 per-record history panel on detail pages | **Not implemented** — removed 2026-06-13 (H-13) |
| FR-009 global page viewable by Quality Manager | **Deviation** — `AdminOnly`; Manager gets 403 |
| FR-011 Auditor role (view + export, no mutations) | **Not implemented** — role deliberately removed (V16); spec never amended |
| FR-013 export honours on-screen filters | **Partial** — server honours them but the Export link forwards only dates (H-13) |
| FR-010 scenario 2.4 filter persistence on paging | **Broken** — "Load more" drops entity/action filters |
| Wide-range export warning / ~50k soft cap | **Not implemented** (H-8) |
| Empty export → "range contained no entries" banner | **Not implemented** — header-only file downloads silently |
| E-1 `fromUtc > toUtc` friendly warning | **Not implemented** — renders empty with no message |
| FR-009 user/role change auditing | **Not implemented** (H-9) |
| FR-001/007 atomic capture; FR-016 no-op suppression; FR-020 archive-friendly; FR-021 one export per user | **Implemented correctly** |

**The single most important decision:** reconcile the code with the spec — either restore the per-record panel + Manager/Auditor roles, or amend `spec.md`/`tasks.md` to the SiteAdmin-only, global-only reality — and resolve the `PurgeAll` contradiction. `tasks.md` currently marks removed work as complete.

---

## 8. Gaps & enhancement roadmap

**Quick wins (hours):** fix the `:g` timestamp (UI-01); add `UseStatusCodePagesWithReExecute` for friendly 404s (UI-02/QUA-15); vendor the CDN assets locally (H-12); hide the Save button on read-only modals (UI-04); fix the gauge-label clipping (UI-05); drop the `ms.ToArray()` copy on exports (part of H-8); add the claim tables to `PurgeAll` and stop deleting the audit log (H-4).

**Short term (days):** conditional status UPDATEs across QO/Arrival/Claim (H-1); edit-lock + plant-scope + audit on sample-delete and image endpoints (H-2/H-3/H-5); fix the flat-defects column misalignment and pivot totals (H-6/H-7); extend audit coverage (H-9); guard the SAP prune and fix the double-fire window (H-10/M-14); login rate-limiting (H-11); server-side row caps on the big list pages and exports (H-8/M-22).

**Strategic:** **automated tests** — the suite is 80 lines covering only two security helpers; there is nothing on the QO state machine, arrivals, claims, audit writes, PDF/Excel generation, or SAP sync (add these before the next big change). Extract the monolith services (`QualityOrderService` 1,705 lines, `AdminController` 1,473, `ReportsController` 1,126) into focused units with a small data-access layer. Add operational visibility: a "last sync failed" dashboard signal (M-17), targeted logging in the mutation paths (QUA-13), and document the backup/restore story for the SQL database. Consider HTTPS on the LAN and the baseline security headers (SEC-04). Revisit the dashboard "Avg defect %" definition (M-3) so the headline KPI is comparable month-over-month.

---

## 9. What's done well (verified correct)

- **Security fundamentals:** all SQL parameterised — *including* the dynamic pivot engine (identifiers come only from a compile-time registry whitelist; every value binds as a parameter); CSRF `[ValidateAntiForgeryToken]` on every state-changing POST incl. AJAX with an `X-CSRF-TOKEN` header; LDAP filter escaping (RFC 4515); `returnUrl` gated by `Url.IsLocalUrl`; generic login errors; no confirmed XSS (all `@Html.Raw` renders trusted server markup; client innerHTML builders escape their inputs).
- **Role impersonation ("View as") is safe** — honored only when the *real* cookie role is SiteAdmin; a forged cookie cannot elevate.
- **Authorization matrix is otherwise correct** — global authenticated-by-default fallback; `AdminController`'s class-level `ManagerOrAdmin` + per-action `AdminOnly` pattern has no missing attribute.
- **Concurrency that *is* protected:** filtered unique indexes make one-active-QO-per-arrival, the arrival triplet, and sample numbering race-proof at the database.
- **Transactions & audit:** all Arrival/QO/Claim mutations are transactional with the audit INSERT sharing the same transaction; FR-016 no-op suppression works; the 2026-05-21 old-row-capture bugfix is correctly implemented.
- **Uploads:** extension whitelist + size caps + GUID filenames (no traversal on the *filename*); branding removal rejects path segments.
- **PDF/analytics engineering:** images are resized + JPEG-compressed before embedding with per-image error fallback; missing data renders null-safe; pivots aggregate in SQL (not in memory) with distinct-value pickers capped and cached.
- **Background services:** correct DI scoping per tick, no `.Result`/`async void`, fire-and-forget done with its own scope + try/catch, loops survive a tick exception; the audit filter's reverse-DNS is time-boxed (500ms) and cached.
- **Migrations:** guarded/idempotent; CHECK constraints on every status/enum column; FKs on operational tables; `NVARCHAR(MAX)` only where genuinely unbounded; the audit table deliberately has no inbound FKs.
- **Live app:** fast, error-free across 21 pages; six working themes; strong audit-log UI; good empty states; proper read-only gating and mobile collapse.

---

## 10. Finding index (traceability)

Consolidated High findings map back to the raw per-pass IDs: **H-1** ← LOG-01/07/11 · **H-2** ← LOG-02 · **H-3** ← LOG-03/SEC-02 · **H-4** ← RPT-01/LOG-05/LOG-12 · **H-5** ← SEC-01/LOG-17/QUA-04 · **H-6** ← RPT-03/QUA-01 · **H-7** ← RPT-04 · **H-8** ← RPT-05/QUA-02 · **H-9** ← LOG-05/QUA-09 · **H-10** ← LOG-04 · **H-11** ← SEC-03 · **H-12** ← FE-01 · **H-13** ← RPT-02/11/19 · **H-14** ← LOG-06. Full per-pass findings (with every Low and the complete Verified-OK lists) are preserved in the review scratchpad and summarised above; nothing was dropped in consolidation.
