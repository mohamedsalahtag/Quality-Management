# alert-pack

A drop-in **scheduled alert + email-group module** for ASP.NET Core 9 / Bootstrap 5.3 admin apps. Originally extracted from the Fleet Management project.

This is the same pattern Fleet Management uses to email document-expiry warnings to support groups, generalized so any project that needs *"scan some records on a schedule, classify each by severity, fan out an email to a list of addresses"* can drop it in.

## What you get

- **Storage** — `AlertRules`, `EmailGroupAddresses` (with To/CC) tables.
- **Domain-agnostic alert engine** (`AlertService`) that owns:
  - Schedule logic (`Daily` / `Every N days` / `Once`)
  - **Today-rule override** — any record matching the rule and dated *today* force-sends regardless of schedule
  - HTML email body with severity-coded sections (🔴 Red, 🟡 Yellow), today-banner, sortable table per severity
  - Email-group fan-out as a single multi-recipient `MimeMessage` (proper To + CC headers)
- **Background scheduler** (`AlertScheduler : BackgroundService`) — wakes hourly, runs every active rule whose schedule is due.
- **Plug-in seam** (`IAlertDataSource`) — your project supplies a tiny class that returns matches for a rule. The pack handles everything else.
- **Admin UI fragments** for managing alert rules (Bootstrap 5 modals + tab pane) and the email group (To/CC dropdown per address).

## Reuse model

| Project           | What it implements          | What the pack handles                 |
|-------------------|-----------------------------|---------------------------------------|
| Fleet Management  | `FleetAlertDataSource`      | Schedule, today-rule, HTML body, fan-out |
| HR / Contracts    | `ContractAlertDataSource`   | (same)                                |
| Quality Mgmt      | `CertificateAlertDataSource`| (same)                                |
| Production Ctrl   | `OrderDeadlineAlertSource`  | (same)                                |

A consumer writes one class implementing `IAlertDataSource.CollectMatchesAsync(rule)`, registers it via DI, and gets the full alert system without re-implementing schedules or HTML emails.

## Dependencies

- **email-notification-pack** (or any equivalent `IEmailService`) — the alert engine calls `IEmailService.SendToEmailGroupAsync(groupId, subject, htmlBody)`. The pack ships an **EmailGroupExtensions** snippet that adds the To/CC fan-out method on top of the email pack's `IEmailService`.
- **user-management-pack** (or any cookie-auth + admin policy) — the admin UI is gated by `[Authorize(Policy = "AdminOnly")]`.
- A "groups" table — Fleet reuses `TechnicianGroups` from user-management-pack; the pack assumes any table with `(GroupId PK, GroupName, ShortCode)`.

## Install

See [INSTALL_PROMPT.md](INSTALL_PROMPT.md). TL;DR:

1. Apply [reference/schema.sql](reference/schema.sql) (or an equivalent migration in your stack).
2. Drop in the `AlertRule` / `AlertMatch` / `EmailGroupAddress` models.
3. Implement `IAlertDataSource` for your domain (one method).
4. Wire `AlertService` + `AlertScheduler` in DI.
5. Paste the Razor partials into your Site Configuration tab and Email Groups admin page.

## Files

| File | What it is |
|------|------------|
| `SPEC.md` | Behavioral contract — read first |
| `INSTALL_PROMPT.md` | Paste-into-fresh-AI-session install prompt |
| `reference/schema.sql` | DDL for `AlertRules` + `EmailGroupAddresses` |
| `reference/*.cs` | Reference C# (models, service, scheduler, plug-in interface, controller endpoints) |
| `reference/*.cshtml` | Razor partials (admin tab pane + alert form modal + email groups page) |
| `examples/fleet-data-source.cs` | The actual Fleet Management `IAlertDataSource` impl |
| `examples/contract-data-source.cs` | A different domain to show the seam works |

## License

MIT-style — copy / modify freely.
