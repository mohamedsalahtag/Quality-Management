# Install Prompt — User Management Pack

> Paste this as your **first message** into a fresh AI session for the project
> you want to add user management to. Make sure the AI can read the rest of
> the `user-management-pack/` folder (`SPEC.md`, `reference/`, `examples/`).

---

You are installing a reusable **User Management module** into this project.
A folder named `user-management-pack/` has been provided. Use it as the
authoritative source for the feature.

## Your job

1. **Read the pack first.** Read `user-management-pack/SPEC.md` end to end —
   it is the behavioral contract. Then skim `reference/AdService.cs`,
   `reference/AccountController.snippet.cs`,
   `reference/AdminController.snippet.cs`, `reference/schema.sql`, and the
   `examples/`. The reference code is .NET 9 + ASP.NET Core MVC + Dapper
   + System.DirectoryServices; **adapt it to this project's stack** (see
   "Stack mapping" below) but preserve every behavior listed in `SPEC.md`.
   Role-impersonation has moved to the sibling `view-as-pack/` — if the
   user wants "View as: {role}" testing, install that pack as well.

2. **Inventory the target project.** Tell me:
   - The web framework + language.
   - The DB layer / ORM.
   - Whether any auth (cookie / JWT / OAuth / SSO) already exists. If so,
     propose whether to extend or replace.
   - Whether AD/LDAP integration is required for this install or whether
     local-only auth is acceptable on day 1.

3. **Produce a written plan** before writing code. The plan must list:
   - The exact files you will create or modify.
   - The schema migration (Users + groups + members + AD config keys).
   - The dependency you will add for LDAP (e.g. `System.DirectoryServices`,
     `python-ldap`/`ldap3`, `ldapjs`, `ldap-authentication`, etc.).
   - The dependency you will add for password hashing (e.g. `BCrypt.Net-Next`,
     `bcrypt`, `argon2-cffi`, `passlib`).
   - How you will wire cookie auth + role policies in this stack.
   - The four routes: login, logout, user CRUD, AD browse.
   - The admin bootstrap path (`/Account/Setup`-equivalent).

4. **Wait for me to approve the plan**, then implement in small commits.

5. **Acceptance test** — all of the following must work:
   1. An admin can log in (AD bind or seed-admin fallback), configure AD,
      click Test Connection successfully, browse AD users, and mass-create
      them.
   2. A new AD user can log in via auto-create-on-login without admin help.

   (Role-impersonation acceptance test lives in `view-as-pack/INSTALL_PROMPT.md`.
   If the user installed view-as-pack alongside this one, run that test too.)

## Stack mapping cheatsheet

| Reference (.NET 9)               | Likely target translation                              |
| -------------------------------- | ------------------------------------------------------ |
| `System.DirectoryServices`       | `ldap3` (Python), `ldapjs` (Node), `ldap-authentication` |
| `BCrypt.Net-Next`                | `bcrypt`, `argon2-cffi`, `passlib`                     |
| `IDbService` (Dapper)            | Your project's DB access layer                         |
| Cookie auth `AddCookie`          | `cookie-session` / `next-auth` / Django sessions       |
| Authorization policies           | Your stack's role-based middleware / decorators        |
| `MemoryCache` (10-min AD cache)  | In-process cache or Redis                              |
| Razor + `@Html.AntiForgeryToken`  | Your view engine's CSRF helper                        |

## Hard rules — do not violate

- **No self-registration UI.** Login page MUST NOT show a "Sign up" link.
- **Login uses AD bind-only.** Do NOT issue an LDAP search during login;
  only after a successful bind, and even then any failure is non-fatal.
- **Wrong-password from AD is definitive** — when AD returns the
  invalid-credentials error code (`0x8007052E` on Windows; equivalent
  `LDAPException` on other libraries), stop trying alternate bind formats
  and return null.
- **Self-protection on every destructive action.** Admin cannot delete or
  disable their own account; bulk delete must defensively skip self.
- **Service-account / AD password and SMTP password are write-only fields**
  in the admin UI. Blank-on-save means "keep existing".
- **AD Browse must be cached** (10-minute window). Force-refresh invalidates.
- **All FK-violation deletes** show "still referenced — disable instead",
  not a stack trace.
- **`AdUsername` is the canonical login key**, not `Email`. The user can
  have a placeholder email (`{username}@placeholder.com`) and still log in.

## Things to ask me before deciding

- "Is AD/LDAP required on day 1, or is local-only auth OK first?"
- "Do you want all four roles, or a subset (e.g. admin + user only)?
  What role names should I use?"
- "Should auto-create-on-login default ON or OFF?"
- "Does this project need group memberships with permission flags, or
  is a flat role good enough?"
- "Do you want profile pictures on day 1?"
- "Do you want View Site As impersonation? (Recommended for any app with
  more than two role tiers. It's now a sibling pack — see
  `../view-as-pack/INSTALL_PROMPT.md`. I can install it alongside this
  one if you say yes.)"

## Output format I expect

- A short stack inventory.
- The plan (files + schema + dependencies + auth wiring + routes).
- Then, after I approve: code changes split into reviewable commits.

Begin by reading `SPEC.md`.
