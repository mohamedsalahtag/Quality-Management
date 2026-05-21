# Install Prompt — Alert + Email-Group Pack

> Paste this as your **first message** into a fresh AI session for the project
> you want to add scheduled alerts to. Make sure the AI can read the rest of
> the `alert-pack/` folder (`SPEC.md`, `reference/`, `examples/`).

---

You are installing a reusable **scheduled-alert + email-group module** into this project. A folder named `alert-pack/` has been provided. Use it as the authoritative source for the feature.

## Your job

1. **Read the pack first.** Read `alert-pack/SPEC.md` end to end — it is the behavioral contract. Then skim `reference/AlertService.cs`, `reference/AlertScheduler.cs`, `reference/IAlertDataSource.cs`, `reference/schema.sql`, and the examples. The reference code is .NET 9 + ASP.NET Core MVC + Dapper + MailKit; **adapt it to this project's stack** (see "Stack mapping" below) but preserve every behavior listed in `SPEC.md`.

2. **Inventory the target project.** Tell me:
   - The web framework + language.
   - The DB layer / ORM.
   - Whether SMTP send already exists (e.g. via `email-notification-pack` or your own `IEmailService`). If yes, the alert engine will call your existing `SendToEmailGroupAsync` — confirm it accepts `(int groupId, string subject, string htmlBody)` or describe what does.
   - Whether a "groups" table exists (Fleet reuses `TechnicianGroups` from `user-management-pack`). If not, propose where the recipient lists should live.
   - **What domain entity is being scanned** — vehicles? contracts? certificates? deadlines? Tell me which table(s) and which date column(s) are the "expiry" being watched.

3. **Produce a written plan** before writing code. The plan must list:
   - The exact files you will create or modify.
   - The schema migration (`AlertRules` + `EmailGroupAddresses` tables, plus any FK to your groups table).
   - **The `IAlertDataSource` implementation** — pseudocode showing which table(s) you'll walk, how you'll classify Red vs Yellow, what the `PrimaryFilter` and `SecondaryFilter` CSV axes will represent in this project's domain (e.g. `EntityTypes / DateFields`).
   - The DI wiring in `Program.cs`.
   - The five admin endpoints (SaveAlert, DeleteAlert, ToggleAlert, RunAlertNow, RunAllAlertsNow).
   - The admin UI surface — typically a new tab on a Site Configuration page + an Email Groups admin page (or extension of an existing one with a To/CC dropdown).

4. **Wait for me to approve the plan**, then implement in small commits.

5. **Acceptance test**: I create a rule, click "Run Now", and the email lands with a well-formed HTML body containing at least one matching record. The rule's `LastSentAt` updates in the DB. The hourly scheduler picks up the rule on the next tick without my intervention.

## Stack mapping cheatsheet

| Reference (.NET 9)                     | Likely target translation                              |
| -------------------------------------- | ------------------------------------------------------ |
| `IAlertDataSource` interface           | Same — host implements one method                      |
| `AlertService` orchestrator             | Class/module exposing `RunAlertAsync` + `RunAllDueAsync`  |
| `AlertScheduler : BackgroundService`   | Hosted service / cron worker / Celery beat in your stack |
| `MimeMessage` (MailKit)                | Whatever your existing email service uses              |
| `IDbService` (Dapper)                  | Your project's DB access layer                         |
| Razor `@Html.AntiForgeryToken()`        | Your view engine's CSRF helper                        |
| `[Authorize(Policy = "AdminOnly")]`    | Your stack's role-based middleware / decorator         |

## Hard rules — do not violate

- **Never block the request on email send.** Always background it.
- **Never propagate SMTP errors** — catch and log; user's action stays clean.
- **Today-rule guard** — at most one fire per rule per day, gated by `LastSentDate == today`.
- **A `Once` rule with `OnceSent = true` never fires again** (admin re-fires by toggling Active).
- **`Recipient` column is "To" or "CC"** — invalid values default to "To".
- **Email body styles must be inline** — many email clients strip `<style>`.
- **Use one `MimeMessage` per group send** with multiple To/CC entries — do not loop through addresses sending separate messages.
- **Antiforgery tokens** required on every state-changing POST.

## Things to ask me before deciding

- "What's the domain entity? What table does the data source walk?"
- "What date column(s) on that entity should be checked for expiry?"
- "What are the two filter axes for `PrimaryFilter` and `SecondaryFilter`? (e.g. for HR: ContractType + DateField; for QM: CertificateType + Issuer)"
- "Where should the Alerts admin tab live — inside an existing Site Configuration page or its own page?"
- "Does your project already have an `EmailService.SendToEmailGroupAsync` method? If yes I'll target it; if no I'll implement one alongside."
- "Are 'Email Groups' a new concept here, or should I extend an existing groups table?"

## Output format I expect

- A short stack inventory.
- The plan (files + schema + dependencies + the `IAlertDataSource` pseudocode).
- Then, after I approve: code changes split into reviewable commits.

Begin by reading `SPEC.md`.
