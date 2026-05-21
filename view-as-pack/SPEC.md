# View-as Module — Specification

> Stack-agnostic behavioural contract. The reference code under `reference/` is ASP.NET Core MVC, but the same design works in any framework with cookie-based authentication and a `Role` claim (Express+Passport, Django, Rails, etc.).

Lets a real **SiteAdmin** (the "ImpersonatorRole") temporarily see the site as a lower role (`Viewer`, `Operator`, `Manager`, ...) **without re-logging-in**. Every role gate — `[Authorize(Policy=...)]`, `User.IsInRole(...)`, menu visibility — responds as if the user actually had the impersonated role.

---

## 1. Mechanism

Implemented as a post-authentication claims transformer (ASP.NET Core's standard `IClaimsTransformation` hook; in other stacks, the equivalent middleware that runs after the cookie has been authenticated but before authorisation policies are evaluated). On every authenticated request:

1. Read the `Role` claim from the authenticated cookie principal — call this `actualRole`.
2. If `actualRole != ImpersonatorRole`, **return the principal unchanged**. A non-admin can never be impersonated, and only an admin can impersonate.
3. Read the `ViewAs` cookie. If absent / empty / not a valid role / equal to `ImpersonatorRole`, **return the principal unchanged**.
4. Otherwise **clone** the principal, remove the `Role` claim, insert a new `Role` claim with the cookie value, and add an `OriginalRole` claim preserving `actualRole`. Return the clone.

The principal MUST be cloned, not mutated — the framework may call the transformer multiple times with the same cached instance and unrelated requests share that cache.

`OriginalRole` is what the controller checks when deciding whether to honour a `POST /Account/ViewAs` — **never** the live `Role` claim, which has already been swapped.

---

## 2. Endpoint contract

`POST /Account/ViewAs` with form body `{ role: string, returnUrl: string }`. Antiforgery token required.

**Authorization:** reject unless `OriginalRole == ImpersonatorRole` (when mid-impersonation) OR live `Role == ImpersonatorRole` (when not yet impersonating). Return HTTP 403 otherwise.

**Validation:**

| `role` value | Action |
| --- | --- |
| empty / whitespace | delete the cookie (admin reverts to self) |
| `ImpersonatorRole` itself | delete the cookie |
| not in `UserRoles.All` (or whatever the host's roles enumeration is) | delete the cookie |
| any other valid lower role | set the cookie |

**Cookie shape on set:**

| Property | Value |
| --- | --- |
| Name | configurable (e.g. `MyApp.ViewAs`) |
| Value | the role string (e.g. `"Operator"`) |
| HttpOnly | `true` |
| SameSite | `Lax` |
| Secure | `Request.IsHttps` (adaptive) |
| MaxAge | `8 hours` |

**Redirect:** to `returnUrl` if it is a local URL, otherwise to the home page.

---

## 3. UI

Two pieces, both rendered in the host layout:

### 3.1 Navbar dropdown

A top-navbar dropdown labelled `View as: {currentRole}` (incognito icon). **Visible only to real SiteAdmins** — gate with `realRole == ImpersonatorRole` where `realRole = OriginalRole ?? Role`.

Items:
- One dropdown item per role in `UserRoles.All` EXCEPT `ImpersonatorRole`. Each item is a small POST form to `/Account/ViewAs` with `role=<thatRole>` and `returnUrl=<currentPath>`.
- A divider, then a final "Become self ({ImpersonatorRole})" item that posts `role=""`.
- The currently-impersonated role is rendered with the `active` class.

While impersonating, the dropdown trigger gets a `text-warning` class so the admin sees they're in an unusual state.

### 3.2 Yellow banner

Below the navbar, rendered **only when `OriginalRole != null && OriginalRole != Role`** (i.e. impersonation is active). Says:

> Viewing the site as **{role}** (your real role: **{originalRole}**).

with a "Become self" button that posts `role=""` to clear the cookie and reload.

Reference: [reference/_ViewAsDropdown.snippet.cshtml](reference/_ViewAsDropdown.snippet.cshtml) is a paste-ready Razor partial covering both pieces.

---

## 4. Security guarantees

- The cookie is **honoured only** when the underlying user's `Role` claim is `ImpersonatorRole`. A non-admin who manually forges or steals the cookie gets **no elevation** — the transformer's first check rules it out.
- The cookie value is just a role string — **no secret material** — so it is safe to be `HttpOnly` + 8-hour lifetime. Even if stolen, it cannot lower another user's privileges (the would-be victim isn't an admin, so step 2 of the mechanism returns the principal unchanged).
- All standard authorisation continues to apply. Impersonation does **not** bypass `[Authorize]`; it **does** change which policies pass for the duration of the request.
- The controller MUST verify against `OriginalRole` (when present) rather than the live `Role` claim — otherwise an admin impersonating a Viewer could not toggle out, because by then their live `Role` claim already says "Viewer".

---

## 5. Stack assumptions

The pack does NOT install these. They must already exist (or be added) in the host project:

| Dependency | What the pack needs | Typical .NET wiring |
| --- | --- | --- |
| Cookie authentication | The host issues a cookie that carries a `Role` claim after login. | `AddAuthentication().AddCookie(...)` |
| Antiforgery middleware | The `ViewAs` action is `[ValidateAntiForgeryToken]`. | `AddAntiforgery(...)` + `@Html.AntiForgeryToken()` in each form |
| Persistent data-protection keys | So the auth cookie survives app restarts. Without this, the admin gets logged out on every redeploy and would have to re-impersonate. | `AddDataProtection().PersistKeysToFileSystem(...)` |
| `IHttpContextAccessor` | The transformer reads the request cookie collection through this. | `AddHttpContextAccessor()` |
| Roles enumeration | A static `UserRoles` (or equivalent) with: `All` (string array of valid roles) and `IsValid(string)` (membership check). The pack references both. | typically in `Models/User.cs` |

If your host project doesn't already have a roles model, install [user-management-pack](../user-management-pack/) first — it provides one.

---

## 6. Hard rules — do not violate

- **Never trust the live `Role` claim to authorise the toggle.** Use `OriginalRole` when present, otherwise the live `Role`. Inverting this lets an admin lose the ability to toggle out.
- **Always clone the principal — never mutate.** The framework caches and may call the transformer multiple times. Mutation corrupts other requests.
- **Always delete the cookie on empty / self / unknown role.** This is the toggle-off path; a buggy validation that overlooks it leaves the admin stuck mid-impersonation.
- **Never expose `ImpersonatorRole` as an impersonatable target.** It's a no-op (the transformer's step 3 short-circuits when `viewAs == actualRole`), but offering it in the UI is confusing.
- **Always render the yellow banner while impersonating.** It's the only mechanism that prevents an admin from forgetting they're in another role and acting on what they see.

---

## 7. What an AI must produce when installing this pack

Concrete checklist for the AI doing the port:

1. **Rename** template names to match the host project:
   - `HelpDesk.Models` / `HelpDesk.Services` → host project's actual namespaces.
   - `HelpDesk.ViewAs` cookie name → `<AppName>.ViewAs`.
   - `ImpersonatorRole = UserRoles.SiteAdmin` → whatever the host calls its top role (often `SiteAdmin`, sometimes `Admin` or `Owner`).
2. **Copy** `reference/ViewAsClaimsTransformer.cs` into the host's services folder. Adjust `using` directives.
3. **Paste** the action from `reference/AccountController.ViewAs.snippet.cs` into the host's auth/account controller. Verify the controller already has antiforgery and the `Authorize` defaults compatible with the action.
4. **Paste** the dropdown + banner from `reference/_ViewAsDropdown.snippet.cshtml` into the host's `_Layout.cshtml`. Position the dropdown `<li>` inside the right-aligned `<ul>` (just before the user-avatar dropdown). Position the banner block immediately after `</nav>`.
5. **Add** the two DI lines from `reference/Program.cs.snippet` to the host's `Program.cs` (registering `IHttpContextAccessor` + the transformer).
6. **Verify** the five preconditions in §5 are met. If any are missing, either add them or recommend `user-management-pack` to the user.
7. **Acceptance test**: log in as the ImpersonatorRole, click each role in the dropdown, confirm the banner appears, confirm a previously-allowed page becomes Forbidden if that role lacks the relevant `[Authorize]` policy, then click "Become self" and confirm the original role is restored.

If the host project lacks a `UserRoles` enumeration (step 1's `UserRoles.IsValid` will fail to compile), STOP and ask the user whether to install `user-management-pack` first or whether they have an equivalent type to point the pack at.
