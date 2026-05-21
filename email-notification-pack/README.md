# Email Notification Pack

A drop-in module that captures **everything the IT HelpDesk Portal does for email
notifications** — config schema, triggers, templates, per-group SMTP override,
background sending, test-email tooling, admin UI fragment — so any new web app
can inherit the whole concept by handing this folder to an AI assistant.

Modeled on the same "stand-alone pack" pattern as `theme-pack`. Hand the folder
to an AI, paste `INSTALL_PROMPT.md`, and it adapts to your target stack
(.NET / Node / Django / etc.) using `SPEC.md` for intent and `reference/` for
exact patterns.

---

## What this pack delivers

| Concept                                | Where it lives in the pack                     |
| -------------------------------------- | ---------------------------------------------- |
| Behavioral spec — *what* and *why*     | `SPEC.md`                                      |
| One-paste install prompt for an AI     | `INSTALL_PROMPT.md`                            |
| Reference implementation (.NET 9 / MailKit) | `reference/EmailService.cs`               |
| Schema (config keys + per-group SMTP)  | `reference/schema.sql`                         |
| Per-group SMTP override model          | `reference/GroupMailConfig.cs`                 |
| DB access methods needed by the service | `reference/DbService.snippet.cs`              |
| DI registration                         | `reference/Program.cs.snippet`                |
| Admin Site Config view fragment         | `reference/ConfigView.snippet.cshtml`         |
| Test-email controller action           | `reference/ConfigController.snippet.cs`        |
| Call-site patterns (how to fire each event) | `examples/`                               |

---

## Quick install (any stack)

1. Open a fresh AI session for your new project.
2. Hand it this whole folder (or copy-paste the contents into the conversation).
3. Paste `INSTALL_PROMPT.md` as your first message — that prompt tells the AI
   how to read the pack and what to produce in your target stack.
4. Review the AI's adaptation, then iterate.

The pack is **stack-agnostic at the spec level** and **.NET 9 / MailKit-concrete
at the reference level**. AI uses the spec to understand intent and adapts the
reference patterns to whatever framework you're building (e.g. it would
translate `Task.Run` fire-and-forget into Node.js `setImmediate`, or Django
`threading.Thread`, etc.).

---

## What ships out-of-the-box

Six fully-templated transactional emails:

1. **Ticket created** — confirmation to the requester
2. **Ticket created** — fan-out to all technicians in the ticket's group + SiteAdmins
3. **Ticket picked up** — notifies the requester which technician owns it
4. **Reply added (public)** — sends the requester the *full* conversation thread,
   with the new reply highlighted in blue and a NEW badge
5. **Status changed** — Closed / Cancelled / Escalated branches with distinct copy
6. **General inbox copy** — every new ticket also CC's a shared mailbox

All sends are non-blocking (`Task.Run` fire-and-forget). Per-group SMTP overrides
fall back to the global SMTP defaults. SendGrid is the recommended provider
(host `smtp.sendgrid.net`, port 587, user `apikey`, password = the API key).

---

## Files at a glance

```
email-notification-pack/
├── README.md                              # this file
├── SPEC.md                                # full behavioral spec
├── INSTALL_PROMPT.md                      # paste this into an AI session
├── reference/
│   ├── EmailService.cs                    # MailKit-based service (the real impl)
│   ├── GroupMailConfig.cs                 # per-group SMTP override model
│   ├── DbService.snippet.cs               # config + per-group SMTP queries
│   ├── schema.sql                         # SiteConfiguration keys + GroupMailConfig DDL
│   ├── Program.cs.snippet                 # DI registration line
│   ├── ConfigController.snippet.cs        # TestEmail action + config save
│   └── ConfigView.snippet.cshtml          # SMTP settings UI fragment
└── examples/
    ├── trigger-ticket-created.cs
    ├── trigger-ticket-reply.cs
    └── trigger-status-changed.cs
```

See `SPEC.md` for the full requirements and `INSTALL_PROMPT.md` for the
hand-off prompt.
