# Alert + Email-Group Module — Specification

This is the **stack-agnostic** behavioral contract for the alert system. The reference implementation in `reference/` is .NET 9 + ASP.NET Core MVC + Dapper + MailKit; an AI adapting this pack should preserve **every behavior in this document** while idiomatically translating the code to the target stack.

---

## 1. Storage

### 1.1 `AlertRules`

```
AlertId         PK identity
Name            string(150)            NOT NULL    -- admin label
GroupId         FK → groups            NOT NULL    -- recipient list
PrimaryFilter   string(100)            NOT NULL    -- CSV (e.g. "Passenger,Trucks")
SecondaryFilter string(100)            NOT NULL    -- CSV (e.g. "MVPI,Registration")
Severities      string(30)             NOT NULL    -- CSV: Red,Yellow
Subject         string(300)            NOT NULL    -- prefix; counts auto-appended
Schedule        string(20)             NOT NULL    -- Daily | EveryN | Once
ScheduleN       int                    NOT NULL    -- only used when Schedule=EveryN
IsActive        bit                    NOT NULL = 1
LastSentAt      datetime               NULL        -- last successful dispatch
LastSentDate    date                   NULL        -- date-only of last dispatch (today-rule guard)
OnceSent        bit                    NOT NULL = 0  -- becomes 1 after a "Once" fires
CreatedAt       datetime               NOT NULL = now
CreatedBy       int                    NULL
```

The three CSV filter columns are **opaque to the alert engine** — the data source consumes them and emits matches. An empty string means "no filter on this axis" (all records pass on that axis).

### 1.2 `EmailGroupAddresses`

```
AddressId       PK identity
GroupId         FK → groups            NOT NULL
EmailAddress    string(200)            NOT NULL
Recipient       string(10)             NOT NULL = 'To'   -- "To" or "CC"
AddedAt         datetime               NOT NULL = now
AddedBy         int                    NULL
UNIQUE (GroupId, EmailAddress)
```

DDL is in `reference/schema.sql`.

---

## 2. The plug-in seam — `IAlertDataSource`

A consumer implements this single-method interface. The pack does everything else.

```csharp
public interface IAlertDataSource
{
    Task<List<AlertMatch>> CollectMatchesAsync(AlertRule rule, CancellationToken ct = default);
}
```

### `AlertMatch` (what the data source emits)

```csharp
public class AlertMatch
{
    public string    PrimaryType   { get; set; } = "";   // e.g. "Passenger" / "ContractType"
    public string    SecondaryType { get; set; } = "";   // e.g. "MVPI" / "BackgroundCheck"
    public string    Identifier    { get; set; } = "";   // e.g. plate # / contract ID
    public string    Label         { get; set; } = "";   // human-readable record name
    public string    DueRaw        { get; set; } = "";   // original date string for display
    public DateTime? DueDate       { get; set; }         // parsed; null if can't parse
    public string    Severity      { get; set; } = "";   // "Red" or "Yellow"
    public int       DaysFromToday { get; set; }
    public string    Owner         { get; set; } = "";   // assignee / department / etc.
    public string    Branch        { get; set; } = "";   // optional location/region
}
```

The data source is responsible for:
- Parsing `rule.PrimaryFilter / SecondaryFilter / Severities` CSVs.
- Reading the records to scan from the project's database.
- Classifying each candidate as Red / Yellow / (skip).
- Returning a flattened `AlertMatch` list.

### Severity classification convention

- **Red** = the record's relevant date is **strictly before today** (past-due / expired).
- **Yellow** = the record's date is **between today (inclusive) and `today + WarningDays`** — the warning window. `WarningDays` is project-defined (Fleet uses 30 by default, configurable per site).
- Anything beyond the warning window does **not** become an `AlertMatch`.

The pack does not impose a specific date format — `DueRaw` flows through to the email body verbatim if it can't be parsed.

---

## 3. Schedule logic

Each rule fires **once per evaluation pass** when one of these conditions holds:

```
function shouldFire(rule, matches, now):
    hasTodayMatch = any(m.DueDate == now.Date for m in matches)
    sentAlreadyToday = rule.LastSentDate == now.Date

    # Today-rule override — overrides every schedule, but only once per day.
    if hasTodayMatch and not sentAlreadyToday:
        return (true, "today-rule")

    # No matches at all → never fire (don't email empty alerts).
    if matches is empty:
        return (false, "no-matches")

    if rule.Schedule == "Once":
        return (not rule.OnceSent, "once")

    if rule.Schedule == "Daily":
        return (not sentAlreadyToday, "daily")

    if rule.Schedule == "EveryN":
        if rule.LastSentDate is null:
            return (true, "every-N-first-run")
        elapsedDays = (now.Date - rule.LastSentDate).Days
        return (elapsedDays >= rule.ScheduleN, "every-N")
```

After a successful dispatch:
- Set `LastSentAt = now`, `LastSentDate = now.Date`.
- For `Schedule = Once`, also set `OnceSent = true` so it never fires again.

The background scheduler ticks **every hour** (`TimeSpan.FromHours(1)`). That's enough granularity since all schedules are day-based.

---

## 4. Email-group fan-out

`SendToEmailGroupAsync(groupId, subject, htmlBody)`:

1. Load `EmailGroupAddresses` for that group.
2. Bucket into a `to` list (Recipient = "To") and a `cc` list (Recipient = "CC"), deduped.
3. **SMTP requires at least one To.** If the To list is empty but the CC list has at least one entry, promote the first CC to To.
4. Build **one** `MimeMessage` with multiple `msg.To.Add(...)` and `msg.Cc.Add(...)`. **Do not send N separate messages** — recipients should see each other in the headers (true CC behavior).
5. Skip placeholder / empty addresses silently with a log warning.
6. Catch all SMTP errors — never propagate, never block the caller's request.

Returns the total number of dispatched recipients (To + CC).

---

## 5. HTML email body

The reference template is in `reference/AlertService.cs::BuildAlertHtml`. Required structure:

### Header
- A 24px × 32px slate (`#0f172a`) band.
- Eyebrow text (uppercase, muted, letter-spaced): `"<App Name> · Notification"`.
- Title: the rule's `Name` (white, 20px).
- 4 px **accent stripe** at the bottom whose color reflects severity:
  - Red `#dc2626` if any `DueDate == today` or any Red matches
  - Amber `#f59e0b` if only Yellow matches
  - Blue `#2563eb` otherwise (no matches — only fires when forced)

### Body
1. Intro line with today's date.
2. **TODAY callout** — a red-bordered panel listing all records dated today, if any.
3. **Expired** section — heading with red dot, then a sortable HTML table.
4. **Expiring soon** section — heading with amber dot, then a table.
5. Total count + dispatch timestamp footer.

### Per-row table format
- 8-column table: indicator dot, identifier, label, primary-type, secondary-type, due-date (right-aligned), days-remaining, owner.
- Dot color: red for Red severity, amber for Yellow.
- Days cell: `"TODAY"` for today rows, `"Nd ago"` for past-due, `"Nd"` for upcoming.
- Zebra-striped backgrounds (`#ffffff` / `#f8fafc`).

### Hard rules
- All styles **inline** — many email clients strip `<style>`.
- Use a **table-based** outer layout — Outlook hates flex/grid in emails.
- HTML-encode every user-supplied string in the table cells.

### Subject line
```
"{rule.Subject}" + summary
```
where summary is appended automatically based on counts:
- `" — N expired, M expiring soon"` if both > 0
- `" — N expired"` / `" — M expiring soon"` if only one > 0
- `""` if no matches (rule shouldn't fire then anyway)

---

## 6. Admin UI surface

A SiteAdmin-only page should expose:

1. **Alerts tab** (typically inside Site Configuration):
   - Table of every `AlertRule` with name, group badge, target summary (Primary/Secondary CSV), severity badges, schedule indicator, last-sent, status.
   - Per-row actions: ▶ Run now (force-fires regardless of schedule) / ✏ Edit (modal) / ⏸ Toggle active / 🗑 Delete (confirm).
   - Top-right: ⚡ "Run All Now" — fires every active rule with matching records.
2. **Alert form** (modal, used by both new + edit):
   - Name + Group picker
   - Three filter checkbox groups (Primary / Secondary / Severities) — labels are project-specific, hint "unticked = all"
   - Schedule selector (Daily / Every N / Once) with conditional N input
   - Subject line + Active toggle
3. **Email Groups page** with To/CC dropdown per address row, add/remove buttons.

UI fragments in `reference/AlertsTab.snippet.cshtml`, `reference/_AlertForm.snippet.cshtml`, `reference/EmailGroupsPage.snippet.cshtml`.

---

## 7. Hard rules — do not violate

- **Never block the user request on email send.** All recipient sends are fire-and-forget.
- **Never crash if SMTP fails.** Catch and log; the caller's HTTP response is unaffected.
- **Today-rule fires at most once per day per rule.** Guard via `LastSentDate == now.Date`.
- **A rule with zero matches never fires** (except `Once`-with-empty isn't useful; still skip).
- **`OnceSent` makes a `Once` rule permanently silent.** Admins re-fire by toggling Active or deleting.
- **`Recipient` is "To" or "CC" only.** Default to "To" when invalid input.
- **Empty / placeholder addresses skipped silently.**
- **Schedule `Run All Now` and per-row `Run Now`** must use `forceIgnoreSchedule = true` so manual fires don't get blocked by daily-already-sent.
- **Antiforgery tokens required** on every state-changing POST.

---

## 8. What an AI must produce when installing this pack

1. Add the storage from `schema.sql` (or its migration).
2. Drop in the models, the `IAlertService` / `IEmailService` extensions, the `AlertScheduler` background service.
3. **Implement `IAlertDataSource` for the host project's domain** — usually 30–80 lines that walk one or more tables and emit `AlertMatch` rows.
4. Wire DI in `Program.cs`: `AddScoped<IAlertService, AlertService>(); AddScoped<IAlertDataSource, ProjectAlertDataSource>(); AddHostedService<AlertScheduler>();`.
5. Add the admin UI fragments to the host's Site Configuration tab + a new Email Groups page.
6. Document the `AlertRules.PrimaryFilter` / `SecondaryFilter` semantics in the host project's master spec — for Fleet that's "VehicleTypes / DocumentTypes"; for Contracts it might be "ContractType / DateField".

The acceptance test for a successful install: an admin can create a rule, click "Run Now", receive a well-formed HTML email with at least one matching record, and the rule's `LastSentAt` updates in the DB.
