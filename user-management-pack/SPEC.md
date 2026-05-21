# User Management Module — Specification

This is the **stack-agnostic** behavioral contract. The reference in
`reference/` is .NET 9 + ASP.NET Core + Dapper + System.DirectoryServices.
An AI adapting this pack should preserve **every behavior in this document**
while idiomatically translating the code to the target stack.

---

## 1. Storage

### 1.1 `Users` table

```
UserId          PK identity
EmployeeId      string(50)        nullable
AdUsername      string(100)       UNIQUE NOT NULL          # the login name
FullName        string(200)       NOT NULL
Email           string(200)
Department      string(200)
ProfilePicture  string(300)       nullable                 # /avatars/{id}.{ext}
PasswordHash    string(500)       NOT NULL                 # BCrypt local fallback
Role            string(50)        NOT NULL = 'Requester'
IsActive        bool              NOT NULL = 1
IsOnline        bool              NOT NULL = 0
LastLogin       datetime          nullable
LastSeen        datetime          nullable
CreatedAt       datetime          NOT NULL = now
CreatedBy       FK Users          nullable
DisabledAt      datetime          nullable
DisabledBy      FK Users          nullable
```

`AdUsername` is the canonical login key. `Role` is one of:
`Requester`, `Technician`, `FirstLevelSupport`, `SiteAdmin`.

### 1.2 `TechnicianGroups` + `TechnicianGroupMembers`

```
TechnicianGroups (
    GroupId      PK identity,
    GroupName    string(100) NOT NULL,
    ShortCode    string(10)  NOT NULL,            # 3-letter prefix, e.g. SAP
    Description  string(500),
    IsActive     bool        NOT NULL = 1,
    CreatedAt    datetime    NOT NULL = now
)

TechnicianGroupMembers (
    MemberId        PK identity,
    UserId          FK Users  NOT NULL,
    GroupId         FK TechnicianGroups NOT NULL,
    CanDelete       bool NOT NULL = 0,            # may delete tickets in this group
    CanAssign       bool NOT NULL = 0,            # may assign tickets to others
    CanPickupOthers bool NOT NULL = 0,            # may take tickets already assigned to teammates
    AssignedAt      datetime NOT NULL = now,
    UNIQUE (UserId, GroupId)
)
```

### 1.3 AD configuration (rows in `SiteConfiguration` key/value table)

| Key                  | Purpose                                                       |
| -------------------- | ------------------------------------------------------------- |
| `AdDomain`           | e.g. `company.com` — used to build UPN `user@company.com`     |
| `AdLdapPath`         | e.g. `LDAP://192.168.1.10/OU=Users,DC=company,DC=com`         |
| `AdServiceUser`      | Service account for browsing AD (mass-create lookups)         |
| `AdServicePassword`  | Service account password — never echoed in admin UI           |
| `AutoCreateAdUsers`  | `true`/`false` — auto-provision unknown AD users as Requester |

DDL for both tables and the seed for AD keys is in `reference/schema.sql`.

---

## 2. Authentication

### 2.1 Login flow

1. POST `/Account/Login` with `{ Username, Password, RememberMe }`.
2. Look up user by `AdUsername` in DB.
3. **If not found**:
   - If `AutoCreateAdUsers=true` AND AD configured: try AD bind (see §2.2).
     On success, INSERT user as Requester (full name / email / department
     from AD lookup, BCrypt of entered password as local fallback,
     `IsActive=1`) and continue with the new user record.
   - Otherwise return `"Invalid credentials or account not registered."`.
4. If user is `IsActive=0` return `"Your account has been disabled..."`.
5. Verify password — try AD first, fall back to BCrypt local hash.
6. **Bootstrap path** for the seeded `admin` account: if AD failed AND local
   hash matches one of the well-known seed hashes, re-hash the entered
   password and save — this self-corrects the placeholder seed on first
   real login.
7. On success: update `LastLogin` + `LastSeen` + `IsOnline=1`, then issue
   a cookie with `ClaimTypes.NameIdentifier`, `Name`, `GivenName`, `Email`,
   `Role`, `Department`, `EmployeeId`.
8. `IsPersistent`: `RememberMe ? 30-day cookie : 8-hour session`.
9. Redirect to `ReturnUrl` (validated as local) or `/Home`.

### 2.2 AD bind authentication (LOGIN path — bind-only, no LDAP search)

**Crucial**: do **not** issue an LDAP search during login. Many AD setups
return referrals that time out, killing the login. The reference does:

```
sam = username before '@'
upn = "{sam}@{AdDomain}"
netbios = "{netbiosDomain}\{sam}"
ldapUrl = "LDAP://{server}"

for bindAs in [upn, netbios]:
    try:
        DirectoryEntry(ldapUrl, bindAs, password, AuthenticationTypes.Secure)
        force NativeObject access     # this is what actually authenticates
        try optional DirectorySearcher(sAMAccountName=sam) for displayName/mail/department
            (failures here are non-fatal; login still succeeds)
        return AdUser populated from results (or sensible defaults)
    catch COMException 0x8007052E or 0x80070005:
        # Definitive wrong password — stop, do NOT try other bind formats
        return null
    catch other:
        # try next bind format
        continue
```

### 2.3 Cookie / session policy

- Cookie name: `HelpDesk.Auth` (rename per project), `HttpOnly`, `SameSite=Lax`,
  `SecurePolicy=None` (HTTP intranet). Sliding expiration 30 days.
- `IdleTimeout` and session cookie `MaxAge` both 30 days.
- Antiforgery: `HeaderName="X-CSRF-TOKEN"`, `SameSite=Lax`, `SecurePolicy=None`
  — required because the site is reached via IP/HTTP from other PCs on the LAN.

### 2.4 Roles + policies

```
"TechOrAdmin" = Role in { Technician, FirstLevelSupport, SiteAdmin }
"AdminOnly"   = Role == SiteAdmin
```

Apply as method/class-level authorization filters.

---

## 3. AD Service (browse + test)

### 3.1 `GetAllActiveUsersAsync` (used by AD Browse and Mass Create)

Strategy ladder — try each in order, take the first that returns rows:

| # | LDAP URL                            | Bind        |
| - | ----------------------------------- | ----------- |
| 1 | `LDAP://{server}/{baseDn}`          | `{svcUser}@{domain}` (UPN) |
| 2 | `LDAP://{server}`                   | UPN          |
| 3 | `LDAP://{server}/{baseDn}`          | `{netbios}\{svcUser}` |
| 4 | `LDAP://{server}`                   | NetBIOS      |
| 5 | `LDAP://{domain}`                   | UPN          |
| 6 | `LDAP://{server}/{baseDn}`          | anonymous    |

Filter: only enabled person/user accounts —

```
(&(objectCategory=person)(objectClass=user)
  (|(userAccountControl=512)(userAccountControl=514)
    (userAccountControl=66050)(userAccountControl=66048)))
```

Properties to load: `sAMAccountName`, `displayName`, `cn`, `givenName`,
`sn`, `mail`, `department`, `userAccountControl`. Filter out machine
accounts (sAMAccountName ending in `$`). Map `userAccountControl == 512 ||
== 66048` to `IsActive=true`.

### 3.2 `TestConnectionAsync` (AD Management page button)

Try connecting (with optional service-account bind) to `LDAP://{server}`,
`LDAP://{domain}`, `LDAP://{server}/{baseDn}`. Force `NativeObject` to
authenticate, do a 1-result search to verify read access. Return a
human-readable success/failure message.

### 3.3 LDAP path parsing

Accept any of these formats and extract `(server, baseDn)`:

```
LDAP://192.168.1.10/OU=Users,DC=company,DC=com
LDAP://server.company.com/DC=company,DC=com
LDAP://DC=company,DC=com                    # no server -> use domain
LDAP://192.168.1.10:389/...                 # strip port
LDAP://192.168.1.10                         # no baseDn -> derive from domain
```

If only the domain is given, derive baseDn as `DC=part1,DC=part2,...`.

---

## 4. Admin pages

### 4.1 `/Admin/Users` — User Management

- Filters: `search` (name/username/email), `dept`, `roleFilter`, `statusFilter`.
- Table columns: Full Name, Username, Email, Department, Role badge, Status, Actions.
- Per-row actions:
  - **Edit Profile** modal — Employee ID, Full Name, Email, Department.
  - **Change Role** modal — picks one of the four roles; if Technician/FLS,
    shows a Group dropdown.
  - **Enable/Disable** — confirms, sets `IsActive`, `DisabledAt`/`DisabledBy`.
  - **Delete** — only allowed if no FK references; on `SqlException 547`
    show "still referenced — disable instead".
- **Bulk select + delete**:
  - Master "Select All" checkbox in table header (tri-state with
    `indeterminate` when partially selected).
  - Per-row checkbox, `name="userIds"`, `form="bulkDeleteForm"` (so they
    post to a top-level form and don't nest inside per-row action forms).
  - The current admin's own row shows a person icon instead of a checkbox.
  - Floating action bar appears on first selection: "N user(s) selected",
    Clear button, red "Delete Selected" button, confirm dialog listing
    first 10 names.
  - Server endpoint distincts the IDs, defensively skips self, groups
    results into Deleted / Blocked (FK 547) / Skipped-self lines.

### 4.2 `/Admin/Technicians` — Technician Access

- List of users with role Technician or FirstLevelSupport, alongside the
  groups they belong to and per-row checkboxes for `CanDelete`,
  `CanAssign`, `CanPickupOthers`.
- Add-to-group form: pick user + pick group + permissions.
- Remove-from-group action.

### 4.3 `/Admin/AdManagement` — AD Config + Manual Create

- Domain, LDAP path, service account, service password (treated as
  "blank = keep existing" on save like the SMTP password).
- "Test Connection" button calls `IAdService.TestConnectionAsync`.
- Manual create form: any AD username + name + email + dept + role + group.

### 4.4 `/Admin/AdUsers` — AD Browse

- Server-side cached (`MemoryCache`, key `"AdUsers_All"`, 10-minute TTL).
- `?refresh=true` forces re-fetch.
- For each AD user the table shows:
  - Already registered? (yes/no) with role + status if registered.
  - "Create" button if not registered (one-click → CreateUserFromAd).
- "Mass Create" button at top fires `MassCreateUsers(groupId?)` —
  imports every enabled AD user not yet in the system, optional bulk
  assign to a group as Requester. Skips individual failures, reports
  total count.

### 4.5 `/Account/Setup` — Bootstrap admin (OPTIONAL, deprecated for AD-first installs)

- Creates `admin` user with password `Admin@123` if it doesn't exist.
- If it exists, resets the password to `Admin@123` and `IsActive=true`.
- **Recommended:** omit this endpoint entirely on AD-first installs. The
  first SiteAdmin is provisioned by the AD auto-create flow (set
  `AutoCreateAdUsers=true`, log in as a domain user, then SQL-bump that
  row's `Role` to `SiteAdmin`). Reference: the QMS install removes
  `/Account/Setup` and any BCrypt placeholder seed hash because once AD
  works the bootstrap path becomes a back-door rather than a recovery
  route.
- If you DO ship it, gate it by env flag and remove it once the first
  admin is operational.

---

## 5. Profile pictures

- Upload via `POST /Account/UploadProfilePicture` (multipart file).
- Allowed: `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`. Max 2MB.
- Store at `wwwroot/avatars/{userId}.{ext}`. Delete any prior file for the
  same userId before saving (so extension changes don't leak old files).
- DB column `Users.ProfilePicture` stores the *URL path* `/avatars/{userId}.{ext}`.
- The view layer appends `?v={unixSeconds}` for cache busting.
- `POST /Account/RemoveProfilePicture` deletes the file and clears the column.

---

## 6. View site as — moved

The role-impersonation feature ("View as: {role}") has been extracted to
its own pack: see [../view-as-pack/SPEC.md](../view-as-pack/SPEC.md).

Install `view-as-pack/` alongside this pack if you want SiteAdmins to be
able to test the site as lower roles without re-logging-in. The two
packs are independent; `view-as-pack/` depends on the roles enumeration
and cookie auth that this pack provides.

---

## 7. Hard rules — do not violate

- **No self-registration UI.** Login page must NOT have a "Sign up" link.
  Users are created by admins or auto-provisioned via the toggle.
- **Login uses AD bind-only**, never LDAP search at login time.
- **Self-protection**: an admin cannot delete or disable their own account
  (server-side check on UserId == current claims subject).
- **Bulk delete must skip self** even if the form posts the admin's ID.
- **FK violations on delete** show a friendly "disable instead" message,
  never a stack trace.
- **AD service password and SMTP password are write-only** in the admin UI:
  the field is blank on render and only saved when the posted value is
  non-empty.
- **Antiforgery tokens** required on every state-changing POST.
- **AD Browse is cached** (10 min) so admin pages don't stall on a slow
  domain controller. Force-refresh button must invalidate the cache.

---

## 8. What an AI must produce when installing this pack

1. Add the storage from `schema.sql` (or its migration).
2. Translate `AdService.cs` to your stack's LDAP library — preserve the
   bind-only login rule, the strategy ladder for browse, and the LDAP
   path parser.
3. Translate `AccountController` and `AdminController` snippets — keep
   the routes, request/response shapes, role policies, and the
   self-protection / bulk-delete / auto-create rules.
4. Wire DI / module exports.
5. Port the login + users + AD management views (Bootstrap 5.3 as the
   reference style; any framework's components work as long as the
   page layout and modals are equivalent).
6. Document the AD config keys in your project's master spec.

The acceptance test for a successful install: an admin can log in with
the seed account, configure AD, click "Test Connection" (success),
browse AD users, mass-create as Requester, and a freshly-created user
can log in with their AD password (auto-create toggle) without going
through the admin UI.
