# Quickstart — Verify the Audit Trail end-to-end

**Feature**: `001-audit-trail`
**Date**: 2026-05-20

Run-through to confirm every spec acceptance scenario after the feature is implemented
and deployed. Total time: ≈ 15 min for the happy paths, +10 min for the negative
tests. The host is the existing Windows Service (`SharbatlyQMS`) running on
`http://localhost:5244` / `http://192.168.3.192:5244`. After grant-service-rights was
run earlier, redeploy is silent — no UAC prompts during this verification.

---

## 0. Pre-flight (one-time, after `/speckit-implement` finishes)

```powershell
# Apply the V15 migration to the live database.
Set-Location 'C:\QualityManagemet\app\SharbatlyQMS.Migrate'
dotnet run -- apply `
    'Server=192.168.3.10;Database=SharbatlyQMS;User ID=linkserver;Password=P@ssw0rd;TrustServerCertificate=True;Connect Timeout=15' `
    'C:\QualityManagemet\app\db\V15__audit_trail.sql'

# Build + redeploy (silent — no UAC).
& 'C:\QualityManagemet\deploy\Republish.ps1'

# Confirm the app is up.
Invoke-WebRequest -Uri 'http://localhost:5244/Account/Login' -UseBasicParsing -TimeoutSec 10 |
    Select-Object StatusCode
```

Verify in SQL Server:

```sql
-- 1. New column exists.
SELECT TOP 1 source_user_agent FROM qms_audit_log;
-- 2. New index exists.
SELECT name FROM sys.indexes WHERE name='IX_qms_audit_log_filter';
-- 3. CK_Users_Role contains Auditor.
SELECT definition FROM sys.check_constraints WHERE name='CK_Users_Role';
-- Expected: '...Auditor...' substring present.
```

Promote one user to the new `Auditor` role via `/Admin/Users` (sign in as SiteAdmin
first, then edit the user, pick `Auditor` from the role dropdown — it should appear
automatically because the dropdown iterates `UserRoles.All`).

---

## 1. User Story 1 — Per-record audit history (P1, MVP)

**Maps to**: Spec User Story 1; acceptance scenarios 1.1 – 1.3.

1. Sign in as a user with role `Manager` (or use `View as → Manager`).
2. Open any Quality Order in `Initial` status. Click **Open** → confirm the modal.
3. Add a sample, save it. Edit the sample, change one reading, save again.
4. Close the Quality Order from the Finish-order modal.
5. Reopen it via the Reopen modal with a reason.
6. Confirm: the QO detail page now shows an **Audit History** panel below the existing
   material cards, containing **5 entries** in reverse-chronological order:
   - `Reopened` by you, just now, with the reopen reason in the new-values diff.
   - `Closed` by you, a moment earlier.
   - `Updated` on the sample (1 reading changed; diff shows old → new).
   - `Created` on the sample (full row in green).
   - `Opened` by you, the earliest.
7. Each `Updated` entry MUST render the changed field(s) in a **git-diff style** —
   old value in red with strikethrough, new value in green. (FR-022.)
8. Verify your IP and `User-Agent` appear in each entry's metadata line.

**Pass criterion**: All 5 entries present, ordered correctly, diff rendering
visible, IP + UA captured.

---

## 2. User Story 2 — Global audit log with filters (P2)

**Maps to**: Spec User Story 2; acceptance scenarios 2.1 – 2.4.

1. Click the new **Audit Log** item in the top navbar. Confirm the page loads in
   well under 1 second (SC-008).
2. Filter by your username + date range "last 7 days". Confirm only your entries from
   the last week appear, newest first.
3. Add an `entityTypes=QualityOrder` filter. Confirm only QO-related entries remain.
4. Add an `actionCodes=Deleted` filter. If you have no deletes in the period, the
   page MUST render an **empty-state message** with a "Reset filters" link.
5. Click the empty-state reset → filters clear → results return.
6. Scroll to the bottom of a long result set. Click **Load more** (or the next-page
   link). Confirm the next 25 entries load AND the filter chips stay highlighted (the
   filter context persists across pages).

**Pass criterion**: Each filter (user / date / entity-type / action-type) narrows
results correctly; the empty state is informative and reversible; pagination
preserves the filter context.

---

## 3. User Story 3 — Auditor Excel export (P3)

**Maps to**: Spec User Story 3; acceptance scenarios 3.1 – 3.3.

1. Sign out of the Manager session. Sign in as the user you promoted to `Auditor`
   in step 0.
2. Confirm the Auditor sees **Audit Log** in the navbar (E-1 visible), can browse
   the list, but does **not** see Arrivals / Quality Orders edit affordances. (Read-only
   role.)
3. Open `/Audit`. Pick a date range that contains ~2,000 entries (or whatever exists
   in your test data). Click **Export**.
4. Confirm an `.xlsx` file downloads with the filename pattern
   `qms-audit-YYYYMMDD-to-YYYYMMDD.xlsx`.
5. Open the file in Excel. Confirm columns: `audit_id`, `changed_at_utc`,
   `changed_at_local`, `changed_by`, `source_ip`, `source_user_agent`, `entity_type`,
   `entity_id`, `action_code`, `old_values_json`, `new_values_json`. One row per entry,
   newest first.
6. Pick a date range with **no** entries (e.g. year 2024). Export → confirm the file
   downloads with only the header row and a "no entries matched" banner appears on the
   page.
7. While an export is streaming, click **Export** again rapidly. The second click MUST
   be refused with a friendly "Your previous export is still running" message (FR-021).
   Use Edge / Chrome dev tools → Network panel to confirm no second request reaches
   the server (or that the second response is a 429 with a friendly message).

**Pass criterion**: Export produces a real `.xlsx` with correct columns; empty range
produces header-only file with banner; double-click rejected.

---

## 4. Security & negative tests

| # | Test | Expected |
|---|---|---|
| 4.1 | Sign in as `Viewer`. Look for the Audit Log nav item. | Not present. Visiting `/Audit` directly → `403` (redirect to `/Account/AccessDenied`). |
| 4.2 | Sign in as `Operator`. Open any Quality Order detail page. | Audit History panel not rendered (FR-017). |
| 4.3 | Sign in as `ClaimManager`. Open `/Audit` directly. | `403`. |
| 4.4 | Sign in as `Manager`. POST to `/Audit/Export` with a date range. | `403` — Manager can view, only Auditor / SiteAdmin can export. |
| 4.5 | Sign in as `Auditor`. POST any mutating endpoint (e.g. `/QualityOrders/Open?id=...`). | `403` — Auditor has no operational write privileges. |
| 4.6 | Sign in as SiteAdmin. Use `View as → Operator`. Perform a sample edit. | Audit entry is written with `changed_by` = SiteAdmin's real username, NOT "Operator" (FR-015). |
| 4.7 | Manually attempt to edit an audit entry by issuing an `UPDATE qms_audit_log` from any in-app surface (e.g. a future hypothetical admin action). | Surface does not exist — no UI, no API. SQL-level audit immutability is documented as a constitution-level expectation (FR-006). |
| 4.8 | Sign in as SiteAdmin. Trigger a mutation while the SQL audit INSERT is artificially broken (e.g. drop the new index temporarily and observe behaviour during a forced failure during insert). | The originating mutation rolls back too (FR-007 atomicity). Restore the index and verify the next attempt succeeds + audits cleanly. |

---

## 5. Performance sanity (SC-008)

Once the audit table has a meaningful row count (≥10,000), run:

```sql
SET STATISTICS TIME ON;
SELECT TOP 25 audit_id, changed_at, entity_type, action_code, changed_by
FROM   qms_audit_log
WHERE  changed_at < SYSUTCDATETIME()
ORDER BY changed_at DESC, audit_id DESC;
```

**Expected**: CPU time + elapsed time both well under 100 ms on the production host
(192.168.3.10). The query plan MUST show an Index Seek on `IX_qms_audit_log_filter`
— if it shows a scan, the index didn't get used; check that the filter columns are
correctly INCLUDE-d.

For the on-page measurement, hit `/Audit` with a heavy filter and read the response
time from browser dev tools → Network → "Time" column. p95 target: 1 s. p99: 3 s.

---

## 6. PROJECT_STATE.md update

After the feature is verified, prepend a dated bullet to `PROJECT_STATE.md §8`
documenting:
- The V15 migration applied (new column, new index, new role).
- The new `IAuditService` + `AuditController` + `Auditor` role.
- The fact that this was the first feature to follow the full
  `/speckit-specify → /speckit-clarify → /speckit-plan` workflow against
  Constitution v1.0.0.
- Note the constitution-conflict reconciliation (EF Core → Dapper) so future agents
  understand why the plan looks different from the original `/speckit-plan` input.

---

## 7. Rollback procedure (if the feature must be removed)

V15 is **forward-only** by design (audit table cannot lose rows — Principle VI). If the
feature must be disabled in a hurry:

1. Set the `[Authorize(Policy = AuthPolicies.AuditViewer)]` on `AuditController` to a
   non-existent policy via a SiteAdmin-only flag — effectively hides the page.
2. Remove the navbar link from `_Layout.cshtml`.

DO NOT drop `qms_audit_log` rows or columns added by V15. The append-only contract
applies to rollback too — any old audit entries must survive a feature retirement.
The `Auditor` role can be deprecated by promoting affected users to a different role;
the role string itself can stay in `CK_Users_Role` indefinitely.
