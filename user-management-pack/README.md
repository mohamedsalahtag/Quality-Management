# User Management Pack

A drop-in module that captures **everything you need for user management**
in an AD-first internal web app: user schema, configurable roles, cookie-auth
login, Active Directory bind authentication, AD user browse + mass-create,
auto-create-on-login, per-user profile editing, group memberships with
permission flags, bulk delete, and profile pictures. Hand the folder to an
AI in a fresh project and it adapts the whole concept to your stack.

Role-impersonation ("View as: {role}") used to live in this pack and has
been split out — see the sibling [view-as-pack](../view-as-pack/) and
install it alongside this one if you want SiteAdmins to test the site as
lower roles without re-logging-in.

Modeled on the same stand-alone-pack pattern as `theme-pack` and
`email-notification-pack`.

---

## What this pack delivers

| Concept                                        | Where it lives in the pack                  |
| ---------------------------------------------- | ------------------------------------------- |
| Behavioral spec — *what* and *why*             | `SPEC.md`                                   |
| One-paste install prompt                       | `INSTALL_PROMPT.md`                         |
| User / role / group schema                     | `reference/schema.sql`                      |
| Domain models (User, AdUser, TechnicianGroup)  | `reference/*.cs`                            |
| AD service (LDAP bind + browse + test conn.)   | `reference/AdService.cs`                    |
| DB access methods                              | `reference/DbService.snippet.cs`            |
| Login / logout / avatar upload                 | `reference/AccountController.snippet.cs`    |
| Users CRUD, bulk delete, technicians, AD mgmt  | `reference/AdminController.snippet.cs`      |
| DI + cookie auth + role policies               | `reference/Program.cs.snippet`              |
| View fragments (Login, Users, AdManagement)    | `reference/*.snippet.cshtml`                |
| Login auto-create flow, bulk-delete UX, role gates | `examples/`                              |

---

## Quick install (any stack)

1. Copy this folder into your new project's repo.
2. Open a fresh AI session for that project.
3. Paste `INSTALL_PROMPT.md` as your first message.
4. The AI inventories your stack, reads `SPEC.md`, adapts the reference, and
   produces a plan for you to approve.

The pack is **stack-agnostic at the spec level** and **.NET 9 + ASP.NET Core
MVC + Dapper-concrete at the reference level**. AI translates patterns to
Node, Django, Rails, etc. Active Directory integration in non-.NET stacks
typically uses `ldap3` (Python), `ldapjs` (Node), or `python-ldap`.

---

## What ships out-of-the-box

- **Configurable role set** (reference uses `Requester`, `Technician`,
  `FirstLevelSupport`, `SiteAdmin` — adapt freely; the QMS install of this
  pack uses `Viewer`, `Operator`, `Manager`, `SiteAdmin`).
- **Two authorization policies**: `TechOrAdmin` and `AdminOnly`.
- **Role impersonation** — moved to the sibling [view-as-pack](../view-as-pack/).
  Install it alongside this one for one-click "View as: {role}" testing.
- **Cookie auth** with 30-day sliding expiration; "Remember Me" persists
  the cookie for 30 days, otherwise the session is browser-lifetime.
- **AD bind-only authentication** (no LDAP search during login — avoids
  referral timeouts). Falls back to BCrypt local hash for the seeded admin.
- **AD user browse** with caching (10-minute MemoryCache) so the admin UI
  is responsive — refresh button forces a re-fetch.
- **Mass-create from AD** — bulk-import every enabled AD user as Requester,
  optionally assigned to a support group.
- **Auto-create on login** — when an unknown AD user passes auth, they are
  inserted as Requester and signed in atomically (gated by a SiteAdmin toggle).
- **Bulk delete** with master "Select All" + per-row exclude pattern.
- **Self-protection** — admins can't delete or disable their own account.
- **Profile pictures** — upload to `wwwroot/avatars/{userId}.{ext}`, served as
  `/avatars/{userId}.{ext}?v={cachebust}`.
- **Admin bootstrap** — *optional, deprecated for AD-first installs.*
  A `/Account/Setup` one-time endpoint can create/reset a seed admin
  (`admin` / `Admin@123`). On AD-first installs (recommended) leave it
  out: provision the first SiteAdmin via the AD auto-create flow and bump
  their `Role` in SQL once. See SPEC §4.5.
- **Group memberships with permissions**: `CanDelete`, `CanAssign`,
  `CanPickupOthers` per-user-per-group.

---

## Files at a glance

```
user-management-pack/
├── README.md                              # this file
├── SPEC.md                                # full behavioral spec
├── INSTALL_PROMPT.md                      # paste this into an AI session
├── reference/
│   ├── schema.sql                         # Users + Groups + Members + AD config keys
│   ├── User.cs                            # model + UserRoles constants
│   ├── AdUser.cs                          # AD lookup model
│   ├── TechnicianGroup.cs                 # group + member model
│   ├── AdService.cs                       # LDAP bind / browse / test
│   ├── DbService.snippet.cs               # user + group + member CRUD
│   ├── AccountController.snippet.cs       # login, logout, avatar
│   ├── AdminController.snippet.cs         # users, technicians, AD mgmt
│   ├── Program.cs.snippet                 # DI + cookie auth + policies
│   ├── Login.snippet.cshtml               # login form
│   ├── Users.snippet.cshtml               # user mgmt page (with bulk delete)
│   └── AdManagement.snippet.cshtml        # AD config + test connection
└── examples/
    ├── autocreate-on-login.cs
    ├── bulk-delete-pattern.cs
    └── role-policy-protection.cs
```

See `SPEC.md` for the full requirements and `INSTALL_PROMPT.md` for the
hand-off prompt.
