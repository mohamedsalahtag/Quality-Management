# Email Notification Module — Specification

This is the **stack-agnostic** behavioral contract. The reference implementation
in `reference/` is .NET 9 + MailKit; an AI adapting this pack should preserve
**every behavior in this document** while idiomatically translating the code
to the target stack.

---

## 1. Storage — config keys and tables

### 1.1 Global SMTP configuration (in a key/value `SiteConfiguration` table)

| Key                  | Default                  | Purpose                                    |
| -------------------- | ------------------------ | ------------------------------------------ |
| `SmtpHost`           | `smtp.sendgrid.net`      | SMTP server hostname                       |
| `SmtpPort`           | `587`                    | SMTP server port                           |
| `SmtpUser`           | `apikey` (for SendGrid)  | SMTP auth user                             |
| `SmtpPassword`       | (the API key / password) | SMTP auth password — **not** echoed in UI  |
| `SmtpFromEmail`      | `noreply@yourdomain.com` | Default sender address                     |
| `SmtpEnableSsl`      | `true`                   | Use StartTLS when true                     |
| `GeneralTicketEmail` | (empty)                  | Shared inbox CC'd on every new ticket      |
| `SiteName`           | `IT HelpDesk`            | Used as the From display name + in headers |
| `SiteUrl`            | `http://...:5000`        | Base URL for clickable ticket links        |

### 1.2 Per-group SMTP override (separate `GroupMailConfig` table)

When a support group needs to send mail from its own mailbox (e.g. `network@`,
`sap@`), it can have its own SMTP entry. Used **only when** `IsEnabled = 1`
and `SmtpHost` / `SmtpUser` / `SmtpPassword` are all populated. Otherwise the
service silently falls back to the global config.

```
GroupMailConfig (
    ConfigId      PK,
    GroupId       FK → TechnicianGroups (UNIQUE),
    SmtpHost,
    SmtpPort        default 587,
    SmtpUser,
    SmtpPassword,
    SmtpFromEmail,
    SmtpFromName,
    SmtpEnableSsl   default true,
    IsEnabled       default false,
    UpdatedAt
)
```

DDL is in `reference/schema.sql`.

---

## 2. The six triggers

All triggers are **fire-and-forget**: the controller action returns to the user
without awaiting the email send. In .NET this is `_ = Task.Run(async () => { try { ... } catch { } });`.
In Node use `setImmediate` / a queued worker; in Python use `threading.Thread`
or a Celery task — adapt idiomatically but **never block the request**.

| #   | Trigger event                  | Recipient(s)                                                                    | Notes                                                                                  |
| --- | ------------------------------ | ------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| 1   | Ticket created                 | The requester                                                                   | Confirmation email, includes ticket #, subject, status badge.                          |
| 2   | Ticket created                 | All technicians in the ticket's group **+** all SiteAdmins                       | One message with the request details + a "Click for Details" CTA pointing at `SiteUrl/Ticket/Detail/{id}`. |
| 3   | Ticket picked up               | The requester                                                                   | Tells them who is now handling it (technician's full name).                             |
| 4   | Reply added (public only)      | The requester                                                                   | Email contains the **entire conversation thread**, NEW reply highlighted blue + "NEW" badge. Internal replies must be filtered out. |
| 5   | Status changed                 | The requester                                                                   | Specific copy for `Closed`, `Cancelled`, `Escalated`; generic for anything else.        |
| 6   | New ticket → general inbox     | `GeneralTicketEmail` (if set)                                                   | Skipped silently if the config value is empty.                                          |

### Special rules

- **Internal replies never trigger emails.** Only `IsInternal = false` replies cause #4.
- **Placeholder/empty emails are skipped.** Strings containing `placeholder`
  (case-insensitive) or empty/null are not sent. Log a warning and continue.
- **#2 fan-out** uses one outbound message per technician (parallel `Task.Run`
  per recipient) so a slow SMTP server doesn't queue the whole list.
- **All errors are caught and logged**, never propagated — a broken SMTP must
  not break the user's action.

---

## 3. SMTP resolution algorithm

When sending a ticket-related email, the service resolves the SMTP config
**per call**, looking up the ticket's `GroupId`:

```
function resolveSmtp(groupId):
    if groupId is set:
        cfg = GroupMailConfig.where(groupId).firstOrDefault()
        if cfg != null
           and cfg.IsEnabled
           and cfg.SmtpHost  is non-empty
           and cfg.SmtpUser  is non-empty
           and cfg.SmtpPassword is non-empty:
            return cfg                  # use group-specific SMTP

    return SiteConfiguration            # fall back to global keys
```

The same `From` is used for both global and group sends.
`fromName` defaults to `SmtpFromName` (group) or `SiteName` (global).

---

## 4. HTML template — the look

Every transactional email shares one HTML shell (`Template()` in the reference):

- Max-width 620px centered card on a `#f4f4f4` body, `border-radius:8px`.
- Header band: `#1a56db` background, white "IT HelpDesk Portal" title +
  `Ticket #{number}` subtitle.
- Body: 32px padding, headline `<h2>` then the per-trigger content block.
- An optional "callout" box with `#1a56db` left border + `#f0f7ff` background
  for the most important fact (e.g. "Assigned Technician: …").
- `<hr>` separator, then a metadata table (Ticket #, Subject, Category,
  Priority pill, Status pill, Submitted timestamp).
- Footer: `#f8fafc` band with "automated notification, do not reply".

### Status / priority pill colors

```
Status:   New=#6c757d  Open=#fd7e14  InProgress|Assigned=#0d6efd
          Escalated=#dc3545  Closed=#198754  Cancelled=#6c757d
Priority: Critical=#dc3545  High=#fd7e14  Medium=#0dcaf0  Low=#6c757d
```

### Subject lines

```
[HelpDesk] ✅ Ticket #N Submitted — {subject}
[HelpDesk] 🔧 Ticket #N Picked Up — {subject}
[HelpDesk] 💬 New Reply on Ticket #N — {subject}
[HelpDesk] ✅|⬆️|🔄 Ticket #N {Status} — {subject}
[HelpDesk] 🎫 New Ticket #N — {subject} [{Priority}]
[HelpDesk] New Request #N — {subject}                # technician fan-out (no emoji to avoid spam folders)
```

Reuse this shell + subject pattern in your target stack (HTML email is
universal). Keep all styles **inline** — many email clients strip `<style>`.

---

## 5. Reply-thread email (#4) — special behavior

The reply email is the only one that needs more than the metadata table:

1. Filter `allReplies` to public-only and order by `CreatedAt` ASC.
2. Render each reply as a card:
   - Background colors: NEW reply = `#eff6ff`, staff = `#f8fafc`, requester =
     `#f0fdf4`. NEW reply also gets a `2px solid #1a56db` border.
   - Author label: `🔧 {name} — IT Support` for staff, `👤 {name}` for the requester.
   - NEW reply gets a `<span style="background:#1a56db">NEW</span>` badge next
     to the author label.
   - HTML-encode the reply message and preserve newlines (`white-space:pre-wrap`).
3. Include the metadata table at the bottom like all other emails.

---

## 6. Admin UI surface

A SiteAdmin-only **Site Configuration** page should expose:

- All global SMTP keys.
- Password field marked "Saved — leave blank to keep" when one already exists,
  so editing the page doesn't accidentally clear it.
- A **"Send Test Email"** button that dispatches a one-shot email to the
  current SMTP user using the saved settings.
- Optional: a per-group SMTP override grid that lets admins set a `GroupMailConfig`
  row per support group (with `IsEnabled` toggle).

UI fragment in `reference/ConfigView.snippet.cshtml`. Test endpoint in
`reference/ConfigController.snippet.cs`.

---

## 7. Provider notes

The system is **provider-agnostic** but has been tested against:

- **SendGrid** *(recommended)*: host `smtp.sendgrid.net`, port `587`, user
  literal `apikey`, password = the API key. The `SmtpFromEmail` must be a
  Single Sender (or the domain authenticated) in SendGrid.
- **Microsoft 365 with SMTP AUTH**: works only if SMTP AUTH is not disabled
  on the tenant; many tenants now block it (error 535). When it fails switch
  to SendGrid or Microsoft Graph.
- **Internal Postfix / Exchange**: set `SmtpEnableSsl=false` if it accepts
  plain on port 25 / 587 inside the LAN, otherwise true.

---

## 8. What an AI must produce when installing this pack

1. Add the storage from `schema.sql` (or the equivalent migration in your stack).
2. Translate `EmailService.cs` to your stack's idioms — preserve the trigger
   shapes, the SMTP resolution algorithm, the HTML template, and the
   fire-and-forget rule.
3. Wire up DI / module exports so controllers can call the service.
4. Add the admin UI fragment and the test-email endpoint.
5. Add the trigger calls at the six points listed in `examples/`.
6. Document the config keys in your project's master spec.

The acceptance test for a successful install: an admin can save SMTP settings,
hit "Send Test Email", receive it; submitting a new ticket fires emails to
the requester, the technician group, and the general inbox without blocking
the request.
