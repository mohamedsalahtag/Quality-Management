# Install Prompt — Email Notification Pack

> Paste this as your **first message** into a fresh AI session for the project
> you want to add email notifications to. Attach (or copy-paste) the rest of
> the `email-notification-pack/` folder so the AI can read `SPEC.md`,
> `reference/`, and `examples/`.

---

You are installing a reusable **Email Notification module** into this project.
A folder named `email-notification-pack/` has been provided. Use it as the
authoritative source for the feature.

## Your job

1. **Read the pack first.** Read `email-notification-pack/SPEC.md` end to end —
   it is the behavioral contract. Then skim `reference/EmailService.cs`,
   `reference/schema.sql`, and the `examples/` files. The reference code is
   .NET 9 + MailKit; **adapt it to this project's stack** (see "Stack mapping"
   below) but preserve every behavior listed in `SPEC.md`.

2. **Inventory the target project.** Tell me what stack it is (framework,
   ORM/DB layer, view engine), where config/secrets live, and where I already
   have a service-registration / DI surface. Also check whether any kind of
   email sending already exists — if so, propose whether to extend or replace.

3. **Produce a written plan** before writing code. The plan must list:

   - The exact files you will create or modify.
   - The schema migration (table or key/value rows from `SPEC.md` §1).
   - The dependency you will add for SMTP (e.g. MailKit, nodemailer, smtplib,
     `@nestjs/mailer`, etc.).
   - How fire-and-forget sending will be done in this stack (don't block the
     request — see `SPEC.md` §2).
   - The six trigger call sites you will add (per `examples/`).
   - The admin Site Configuration UI surface (per `SPEC.md` §6).
   - The test-email endpoint.

4. **Wait for me to approve the plan**, then implement in small commits.

5. **Acceptance test**: an admin can save SMTP settings, click "Send Test
   Email", receive the test in their inbox; submitting a ticket fires #1, #2,
   and #6 from `SPEC.md` §2 without blocking the request.

## Stack mapping cheatsheet

| Reference (.NET 9)         | Likely target translation                              |
| -------------------------- | ------------------------------------------------------ |
| `IEmailService` interface  | A class/module exporting the same six methods          |
| `MailKit` / `MimeKit`      | Whatever your stack uses (nodemailer, smtplib, etc.)   |
| `IDbService` (Dapper)      | Your project's DB access layer                         |
| `_ = Task.Run(async ...)`  | `setImmediate` (Node), `threading.Thread` (Python), background queue, etc. |
| `SiteConfiguration` table  | Whatever K/V config table you have (or settings file + DB hybrid) |
| `GroupMailConfig` table    | Same shape — separate table, FK to your support-groups table       |
| Razor `@Html.AntiForgeryToken()` | Your view engine's CSRF helper                  |

## Hard rules — do not violate

- **Never block the user request on an email send.** Always background it.
- **Never crash the user request** if SMTP fails. Catch and log; proceed.
- **Internal replies must not trigger emails.** Filter `IsInternal` first.
- **Empty / placeholder email addresses are skipped silently** with a log line.
- **The SMTP password field in the admin UI must be blanked on render** and
  treated as "leave blank = keep existing" on save. Never echo the saved
  secret back to the page.
- **Per-group SMTP overrides fall back to global** when missing/disabled —
  the spec defines exactly when (`SPEC.md` §3).
- **All HTML email styles must be inline.** Many email clients strip `<style>`.

## Things to ask me before deciding

- "Should the test-email button send to the current admin's email, or accept a
  custom address?"
- "Do you want per-group SMTP overrides on day one, or only the global SMTP?"
- "Where should the General Ticket Inbox CC be configured — UI or env var?"
- "Are there extra trigger events specific to this project I should add to the
  six in the spec?"

## Output format I expect

- A short stack inventory.
- The plan (file list + schema + dependency + DI + UI surface).
- Then, after I approve: code changes split into reviewable commits.

Begin by reading `SPEC.md`.
