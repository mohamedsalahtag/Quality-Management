# Feature Specification: Audit Trail

**Feature Branch**: `001-audit-trail`

**Created**: 2026-05-20

**Status**: Draft

> **Implementation deviations (recorded 2026-07-02, during the code-review fix pass).**
> The shipped implementation differs from the original spec below in these ways;
> they are intentional and this note is the source of truth where they conflict:
> - **No separate `Auditor` role.** It was introduced (V15) then retired (V16). The
>   global audit log and Excel export are gated **SiteAdmin-only** (`AuthPolicies.AdminOnly`),
>   not Manager/Auditor. FR-009/FR-011 are superseded accordingly.
> - **No per-record history panel (FR-008 / contract E-2).** The per-entity panel was
>   removed 2026-06-13; per-record history is reached by filtering the global
>   `/Audit` page by entity type + id. `IAuditService.GetForRecordAsync` /
>   `GetForCompositeRecordAsync` remain in code for that path but no `HistoryPanel`
>   endpoint is exposed.
> - **Append-only is enforced (FR-006).** The Danger-Zone "Purge All" no longer deletes
>   `qms_audit_log` (fixed 2026-07-02); the purge itself is now audited.
> - **Audit coverage extended (2026-07-02)** beyond the original operational entities to
>   user/role, configuration, and image mutations, and the Danger-Zone purge.

**Input**: User description: "Add an Audit Trail feature to the Quality Management System. Every time a user creates, updates, or deletes a quality record (inspections, defects, corrective actions), the system should log who made the change, what changed, the old value, the new value, and when it happened. Quality managers can view the full audit history for any record. They can filter the audit log by user, date range, record type, and action type (created/updated/deleted). The audit log is read-only — no one can edit or delete audit entries. Auditors (a separate role) can export the audit log to Excel for a selected date range."

## Clarifications

### Session 2026-05-20

- Q: Should the audit log redact or hash sensitive values in `old_values` / `new_values` snapshots? → A: No redaction. Audit entries store old/new values verbatim; sensitive-value protection is achieved purely through role-based access control (Quality Manager / Auditor / SiteAdmin gates) rather than through masking the stored data. Rationale: this is an internal QMS where every value already lives in the operational tables, so the audit log adds no new external exposure surface; redaction would defeat investigability.
- Q: What is the read-page latency target for the global audit log page? → A: One page (25 rows) of filtered results renders in **under 1 second at p95 and under 3 seconds at p99**, sustained up to a total of **10 million audit entries** in the store. Matches the feel of existing operational pages (Quality Orders, Claim Management) on the same host. Achievable with composite indexing on `(timestamp, entity_type, actor)` + keyset pagination; no async loading needed at this target.
- Q: What is the audit-log retention policy? → A: **Indefinite online retention with an archive-friendly schema.** No automatic purge in v1; every audit entry stays queryable on the live table. The schema MUST be partition-friendly (sortable timestamp, no hard FKs pointing into the audit table from operational tables) so that a future archival job — moving entries older than N years into cold storage — is a configuration change rather than a destructive migration. Zero operational change today; no painful rework when the table reaches 50M+ rows in a few years.
- Q: How is Excel export protected against accidental or scripted overload? → A: **One concurrent export per user.** A second export request from the same user while one is still streaming gets a friendly "Your previous export is still running — please wait" message and is not enqueued. No per-minute quota, no IP rate-limiting; this is a LAN-only internal app where every user is on payroll. Matches the single-in-flight pattern already used by the Quality Order PDF download.
- Q: Who sees the per-record audit history panel on a record's detail page? → A: **Manager, Auditor, and SiteAdmin only.** Viewer, Operator, and ClaimManager do NOT see the panel — they continue to see the record itself but not its audit trail on the same page. Definitive (replaces the previous "MAY be hidden" wording). Rationale: Operators don't need oversight tooling on the page they work from; ClaimManager is a claim-domain peer of Manager but not an inspection auditor; Auditors and Quality Managers explicitly need the panel for their job.
- Q: Should audit entries capture the user's IP address and browser (user-agent)? → A: **Yes — both are mandatory fields on every audit entry.** Captured from the HTTP request context at write time. The previous "IP address is best-effort" assumption is REMOVED; IP and user-agent are now hard requirements on FR-002. Background / scheduled actions are out of scope (FR-001 already limits audit capture to user-initiated mutations).
- Q: How should the UI render the difference between old and new values for an Updated action? → A: **Git-diff-style side-by-side render**: the old value gets a red background / strikethrough; the new value gets a green background. Applies to both the per-record audit panel (FR-008) and the global audit log page (FR-009). Tracked as new FR-022.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Quality Manager investigates the history of a single record (Priority: P1)

A Quality Manager opens a specific quality record (for example, a Quality Order that a supplier
is disputing) and wants to see exactly who changed what on that record, in chronological order,
without leaving the record's detail page. Each entry shows the actor, the action they took, the
exact fields that changed (with before/after values), and the moment it happened.

**Why this priority**: This is the most common audit need in day-to-day work — settling
"who closed this?", "when was this status changed?", and "what did it look like before the
override?" questions. It is also the simplest and most-self-contained slice of the feature,
making it the natural MVP.

**Independent Test**: Open any audited quality record after the feature ships, perform at least
one create/update/delete on it, and confirm the record's detail page shows the action with the
correct actor, timestamp, field-level diff, and action label. Closing the page and reopening
it MUST show the same entries.

**Acceptance Scenarios**:

1. **Given** a Quality Order has been opened, closed, and reopened over the course of a week,
   **when** a Quality Manager opens that Quality Order's detail page,
   **then** the audit history section shows three entries (Opened, Closed, Reopened) in
   chronological order, each labelled with the actor's username, the action, the changed
   fields with before/after values, and the timestamp in local time.
2. **Given** a sample defect was created and later edited twice,
   **when** a Quality Manager views that defect's audit history,
   **then** all three entries appear (1× Created, 2× Updated) and the Updated entries each
   list only the fields that changed.
3. **Given** a record was deleted,
   **when** the Quality Manager searches for it,
   **then** the record itself is no longer visible in the operational lists, but its audit
   history is still accessible (via the global audit log — see Story 2) and shows the Deleted
   entry with the full snapshot of the record at deletion time.

---

### User Story 2 - Quality Manager investigates team activity over a period (Priority: P2)

A Quality Manager wants to see all activity by a specific user over the last week, or all
deletions across the entire system this month, or every change to Claims of a particular
status. They open a dedicated audit log page and apply filters by user, date range, record
type, and action type.

**Why this priority**: This is the broader compliance/oversight use case — useful for
periodic reviews, suspicious-activity investigations, and onboarding new managers — but it
is not as frequent as Story 1 and depends on Story 1's underlying capture being in place.

**Independent Test**: Open the global audit log page, apply each filter type at least once
(individually and combined), and confirm the results match what was logged during testing.
Filters with no matching entries must show a clear empty state.

**Acceptance Scenarios**:

1. **Given** there are audit entries for ten different users across the past month,
   **when** the Quality Manager filters by user = "mohamed.tag" AND date range = "last 7 days",
   **then** only entries authored by that user in that window appear, sorted newest first.
2. **Given** the Quality Manager has applied a filter combination that yields zero results,
   **when** the page renders,
   **then** a clear empty-state message explains that no entries match the filters and offers
   to reset them.
3. **Given** the Quality Manager filters by record type = "Quality Order" AND action type =
   "Deleted",
   **then** only delete actions on Quality Orders are shown, regardless of which user
   performed them.
4. **Given** the Quality Manager pages through a long result set,
   **when** they scroll to the bottom and request more entries,
   **then** the next page loads without losing the current filter context.

---

### User Story 3 - Auditor exports the audit log for compliance review (Priority: P3)

A compliance officer or external auditor logs in (with the new Auditor role) and needs to
produce a spreadsheet of all audit entries within a specific date window — say, the previous
quarter — to share with regulators or attach to a compliance report. They pick the start and
end dates and click Export; the system produces an Excel file with one row per audit entry.

**Why this priority**: Compliance exports are typically performed monthly or quarterly, not
daily. This story depends on Stories 1 and 2 being in place (you cannot export what you cannot
capture or view).

**Independent Test**: Sign in as an Auditor, pick a date range that is known to contain audit
entries, click Export, and confirm an `.xlsx` file downloads with one row per audit entry in
the range. Open the file in Excel and confirm the columns are populated correctly.

**Acceptance Scenarios**:

1. **Given** an Auditor selects a date range covering one calendar month containing
   approximately 2,000 audit entries,
   **when** they click Export,
   **then** an Excel file downloads containing one row per entry, with columns for timestamp,
   actor, record type, record identifier, action, old value, and new value, sorted newest
   first.
2. **Given** an Auditor selects a date range with no audit entries,
   **when** they click Export,
   **then** an Excel file still downloads, containing only the header row, with a clear
   message advising that the range contained no entries.
3. **Given** a user attempts to access the Export endpoint without the Auditor role (or
   SiteAdmin),
   **when** they click the link or POST directly,
   **then** access is denied and no file is produced.

---

### Edge Cases

- **Concurrent edits to the same record**: Two managers update the same Quality Order at
  nearly the same moment. Both updates MUST produce separate audit entries, ordered by
  timestamp (ties broken by entry identifier so order is stable).
- **The originating user is later disabled or deleted**: Their historical audit entries
  remain visible and continue to display the username they had at the time of the action.
- **The audited record itself is later deleted**: All audit entries for that record remain
  visible in the global audit log; clicking through to the record from an audit entry shows
  a "this record has been deleted" placeholder rather than a broken page.
- **An audited operation fails partway through**: No audit entry MAY be persisted for a
  mutation that itself rolled back. Audit and mutation succeed or fail together.
- **A SiteAdmin attempts to clear or edit the audit log**: Refused. No UI, no API, no
  configuration switch can remove audit entries; the only sanctioned removal path is a
  documented data-retention migration agreed by governance.
- **Export of a very wide date range**: When the range would produce more entries than the
  export can comfortably stream (working assumption: tens of thousands), the Auditor is
  warned and offered to narrow the range or chunk the export by week/month.
- **Filtering by a username that does not exist**: Empty state, not an error.
- **Network interruption during export**: The user can retry; partial files are not committed
  to disk.
- **Double-click on Export while a previous export is still streaming**: The second click is
  refused with a friendly "Your previous export is still running — please wait" message; no
  second extraction is started. See FR-021.
- **Very long values in the diff view** (e.g. a 5,000-character claim note edited to a 6,000-
  character one): The diff cell renders the first ~300 characters of each side inline with a
  "show more" toggle that expands to the full text on demand. Avoids blowing out the page
  layout while still showing the change at a glance. See FR-022.
- **IP address or user-agent missing from the request context** (e.g. action invoked from a
  test harness that didn't set headers): The audit entry is still written; missing field
  stored as empty string and rendered as `unknown` in the UI. The audit is not blocked by
  metadata gaps.
- **Updates that produce no actual change** (e.g. saving a form with the same values):
  Whether to log them is a UX choice — default is to NOT log no-op updates (see Assumptions).
- **An audit entry's actor was impersonating another role** (via the existing View-as
  feature): The audit entry MUST capture the real underlying user, not the impersonated role.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST automatically record an audit entry every time a user creates,
  updates, or deletes a tracked quality record. There is no manual "log this change" surface.
- **FR-002**: Each audit entry MUST capture: the actor's username, the action type (one of
  `Created`, `Updated`, `Deleted`, or a domain-specific action label such as `Closed`,
  `Reopened`, `Approved`, `Override`), the record type (e.g. Quality Order, Sample Defect,
  Claim, Claim Note), the record identifier, the old field values, the new field values, the
  timestamp (recorded in UTC, displayed in local time), the **client IP address** captured
  from the HTTP request context, and the **client user-agent string** (browser identifier)
  captured from the HTTP request headers. IP and user-agent are mandatory fields — if either
  cannot be determined the audit entry MUST still be written, with the missing field stored
  as an empty string and the global audit log rendering it as `unknown`.
- **FR-003**: For a Created action, the new values MUST include the full inserted record; the
  old values MUST be empty.
- **FR-004**: For an Updated action, the old and new values MUST include only the fields that
  actually changed.
- **FR-005**: For a Deleted action, the old values MUST include the full deleted record; the
  new values MUST be empty.
- **FR-006**: The audit log MUST be append-only. The system MUST NOT expose any surface (UI,
  API, admin tool) that edits or deletes audit entries. This includes SiteAdmin accounts.
- **FR-007**: Audit writes MUST be atomic with the audited mutation. If the underlying mutation
  rolls back, the audit entry MUST also be rolled back. Conversely, if the audit write fails,
  the mutation MUST also fail.
- **FR-008**: A Quality Manager MUST be able to view the audit history of a specific quality
  record from that record's detail page, in chronological order, with the most recent entry
  first. For `Updated` entries, each changed field MUST be rendered as a coloured diff per
  FR-022.
- **FR-009**: A Quality Manager MUST be able to open a dedicated global audit log page showing
  all audit entries across all tracked record types. `Updated` entries on this page MUST
  render their changed fields as a coloured diff per FR-022.
- **FR-010**: The global audit log MUST support filtering by: actor (one or many users), date
  range (inclusive start and end date), record type (one or many), and action type (one or
  many of `Created`, `Updated`, `Deleted`).
- **FR-011**: A new site role `Auditor` MUST be introduced. Members of the Auditor role MUST be
  able to view the global audit log and perform the Excel export. The Auditor role MUST NOT
  grant any other operational permission (no creating, updating, or deleting any quality
  record).
- **FR-012**: An Auditor (or SiteAdmin) MUST be able to export the audit log to an Excel file
  (`.xlsx`) by selecting a date range. The export MUST include the same columns as the global
  view (timestamp, actor, record type, record identifier, action, old value, new value) and
  one row per audit entry.
- **FR-013**: The export MUST honour the same filters as the on-screen view if any are applied
  at the time of export, with the date range being mandatory.
- **FR-014**: Audit entries MUST persist independently of the records they describe. Deleting
  an underlying record MUST NOT delete its audit entries, and disabling a user MUST NOT delete
  the audit entries they authored.
- **FR-015**: When a user performs an action while role-impersonating (via the existing
  View-as feature), the audit entry MUST record the real underlying user, not the impersonated
  role.
- **FR-016**: Updates that result in no actual field changes (saving a form without
  modifications) MUST NOT produce an audit entry — they are no-ops.
- **FR-017**: The audit-history panel on a record's detail page MUST be visible **only** to
  the Quality Manager, Auditor, and SiteAdmin roles. It MUST be hidden from Viewer, Operator,
  and ClaimManager — those users continue to see the record itself but not its audit trail
  on the same page. They retain no path to read audit data (the global audit log page and
  the Excel export are also gated by the same three roles, per FR-009 and FR-011).
- **FR-018**: The system MUST capture audit entries for the following set of operational
  quality record types in v1 (chosen 2026-05-20 — full operational coverage):
  1. **Arrivals** (the inbound shipment record).
  2. **Arrival Items** (line items on an arrival).
  3. **Arrival Checklist** items.
  4. **Quality Orders** (the "inspection" record — top-level QC ticket).
  5. **Quality Order Materials** (the material lines snapshotted onto a QO).
  6. **Samples** (the inspection samples taken against a QO material).
  7. **Sample Readings** (numeric / categorical readings on a sample).
  8. **Sample Defects** (defect occurrences recorded against a sample).
  9. **Claims** (the post-QC commercial claim — the "corrective action" record).
  10. **Claim Notes** (the chat-history rows attached to a claim).
  11. **Material Size Overrides** (admin-approved size changes on a QO material — already
      partially audited today via `qms_audit_log`; this brings them under the unified feature).

  Out of scope for v1: admin / configuration changes (Site Configuration, user role
  assignments, AD config edits, mail templates) — see Assumptions. These may be added in a
  later phase.

- **FR-019**: Audit entries MUST store old and new values **verbatim** — no redaction,
  hashing, masking, or field-name-only mode. Protection of sensitive values is achieved
  solely through the role-based access controls in FR-011 and FR-017 (only Quality Manager,
  Auditor, and SiteAdmin can read audit entries). This is a deliberate choice for an
  internal-only QMS; if the deployment context changes (e.g. multi-tenant SaaS, external
  third-party access), this requirement MUST be revisited.

- **FR-020**: The audit-entry store MUST be **archive-friendly by design**, even though
  no archival job ships in v1. Concretely: entries MUST be sortable by a single timestamp
  column without joins; operational tables MUST NOT hold inbound foreign-key references
  *into* the audit table; deleting a range of old audit entries (a hypothetical future
  cold-storage migration) MUST be a pure DELETE / partition-swap operation with no FK
  cascade implications. This keeps the door open for a retention amendment in years 3–5
  without requiring a destructive schema migration.

- **FR-021**: The Excel export endpoint MUST enforce **one in-flight export per user**.
  While a user has an export streaming, a second export request from the same user MUST
  return a friendly message ("Your previous export is still running — please wait") and
  MUST NOT spawn a second concurrent extraction. The constraint is per-user, not
  per-installation; two different users may export concurrently. No per-minute quota, no
  per-IP rate limiting (this is an authenticated LAN-only application).

- **FR-022**: For `Updated` action entries, the UI MUST render each changed field as a
  **git-diff-style coloured diff**:
  - **Old value** displayed with a red background tint and strikethrough text (Bootstrap
    `bg-danger-subtle text-decoration-line-through` or equivalent).
  - **New value** displayed with a green background tint (Bootstrap `bg-success-subtle`
    or equivalent).
  - **Field name** displayed above (or to the left of) the old/new pair.
  - When multiple fields changed in one entry, each field gets its own old/new diff row;
    fields that did not change MUST NOT appear at all.
  - `Created` entries show only the new value rendered with the green tint (no red row).
  - `Deleted` entries show only the old value rendered with the red tint (no green row).
  - When old or new value is very long (see Edge Cases), the diff cell MUST collapse with a
    "show more" toggle so it does not blow out the page layout.
  - The diff format applies on both the per-record audit panel (FR-008) and the global
    audit log page (FR-009). It does NOT apply to the Excel export (FR-012) — the export
    keeps `old value` and `new value` in separate columns without colour coding.

### Key Entities *(include if feature involves data)*

- **Audit Entry**: a single immutable record of one action on one quality record. Attributes:
  an identifier, the actor's username at the time of the action, the action type, the tracked
  record's type, the tracked record's identifier, the old values snapshot, the new values
  snapshot, and the timestamp. Once written, an Audit Entry is never edited and never
  deleted.
- **Tracked Record**: any quality-domain record whose creates, updates, and deletes produce
  Audit Entries. The full v1 set is enumerated in FR-018 (eleven entity types covering the
  end-to-end inspection workflow: Arrivals → Quality Orders → Samples / Defects → Claims).
  Tracked Records are referenced by `(record type, record identifier)` rather than by a hard
  foreign key, so the audit entry survives the deletion of the record.
- **Audit Filter**: a combination of actor(s), date range, record type(s), and action type(s)
  used to narrow the global audit log. The filter is stateless (kept in the URL/query string,
  not persisted per user).
- **Auditor Role**: a new site role distinct from Quality Manager. Permissioned for read-only
  access to the global audit log and the Excel export. Not permissioned for any operational
  mutation on quality records.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Within the first month after launch, 100% of create, update, and delete actions
  on tracked quality records produce an audit entry. Coverage is verified by running a sample
  of representative actions and confirming the resulting count of audit entries equals the
  number of mutations performed.
- **SC-002**: Zero audit entries can be edited or deleted through any in-app surface (verified
  by attempting to do so with each of the six roles, including SiteAdmin, and confirming the
  action is unavailable or refused).
- **SC-003**: A Quality Manager can locate the most recent change to any tracked record from
  that record's detail page in under 30 seconds, with no scrolling beyond the initial viewport
  for records that have fewer than 10 entries.
- **SC-004**: A Quality Manager can produce a filtered audit view (e.g. "all deletions by user
  X in the last 7 days") in under 1 minute starting from the global audit log page.
- **SC-005**: An Auditor can produce an Excel export covering one full calendar month (assumed
  up to ~10,000 entries at typical volume) and receive the downloaded file in under 2 minutes.
- **SC-006**: After a tracked record is deleted, 100% of that record's prior audit entries
  remain visible in the global audit log.
- **SC-007**: Users with no audit permission (Viewer, Operator, ClaimManager — see FR-017 and
  Assumptions) are unable to reach either the global audit log page or the Excel export
  endpoint.
- **SC-008**: One page (25 rows) of filtered results on the global audit log renders in under
  1 second at the 95th percentile and under 3 seconds at the 99th percentile, measured
  with a store of up to 10 million audit entries and a single composite filter (e.g.
  user + 7-day range). Measured by capturing server response time at the page-rendering
  layer; user-perceived latency may add typical network overhead on top.

## Assumptions

- **Retention is indefinite online, with an archive-friendly schema.** Audit entries are
  never automatically purged in v1; every entry remains queryable on the live table. The
  schema is designed to be partition-friendly (sortable timestamp column, no inbound FKs
  from operational tables) so a future archival job — moving entries older than N years
  into cold storage — becomes a configuration change rather than a destructive migration.
  This matches the existing project rule that audit / status-history tables are append-only.
  An archival or hard-retention policy may be added later via a formal governance amendment.
- **Auditor role can view AND export**, not export-only. Reasoning: the export's contents are
  exactly what is shown on screen, so denying the Auditor the on-screen view would produce a
  worse — not safer — separation of duties.
- **Audit-history visibility on record detail pages** is definitively limited to Quality
  Manager, Auditor, and SiteAdmin (locked 2026-05-20 via the clarification session — see
  FR-017). Operator, Viewer, and ClaimManager continue to see the record itself but not its
  audit panel on the same page, and have no other path to audit data.
- **No-op updates are not logged.** Saving a form without any field change produces no audit
  entry. Rationale: filling the log with noise reduces its value for real investigations.
- **Domain action labels are preserved.** Existing meaningful actions (`Closed`, `Reopened`,
  `Approved`, `Override`, etc.) are recorded as their domain names rather than being reduced
  to a generic `Updated`. Where a domain action exists for an event, the audit entry uses it.
- **The actor identity recorded is the underlying user**, even when they are using the
  View-as impersonation feature. The impersonated role is not the source of truth for
  accountability.
- **The new Auditor role is the 6th site role** (existing five: Viewer, Operator, Manager,
  ClaimManager, SiteAdmin).
- **Time zone for display is the host operating system's local time;** the stored timestamp is
  UTC. Exported Excel files include both the UTC timestamp column AND a formatted local-time
  column for human readability.
- **Excel export volume cap is approximately 50,000 entries per file** (a soft cap; if the
  filtered range exceeds this the Auditor is warned and offered a smaller range or a chunked
  export). Hard limits are deferred to implementation.
- **The feature does not extend to admin/configuration changes** (Site Configuration, user
  role assignments, AD config edits, mail templates). Those changes already have a separate,
  lighter audit story today and are out of scope for v1 unless explicitly added via the
  FR-018 clarification.
- **The export file format is `.xlsx`** (modern Excel). CSV, ODS, and PDF exports are not in
  scope for v1.
