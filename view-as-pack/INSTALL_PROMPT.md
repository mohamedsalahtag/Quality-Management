# Install Prompt — View-as Pack

> Paste this as your **first message** into a fresh AI session for the project
> you want to add role-impersonation to. Make sure the AI can read the rest
> of the `view-as-pack/` folder (`SPEC.md`, `reference/`, `examples/`).

---

You are installing a reusable **View-as / role-impersonation module** into
this project. A folder named `view-as-pack/` has been provided. Use it as
the authoritative source for the feature.

## Your job

1. **Read the pack first.** Read `view-as-pack/SPEC.md` end to end — it is
   the behavioural contract. Then read every file in `reference/` and the
   one example in `examples/`. The reference code is .NET 9 + ASP.NET Core
   MVC; **adapt it to this project's stack** (see "Stack mapping" below)
   but preserve every behaviour listed in `SPEC.md`.

2. **Inventory the target project.** Tell me:
   - The web framework + language.
   - How users currently authenticate (cookie, JWT, OAuth, SSO). The pack
     requires cookie-based auth — if the host uses something else, propose
     either an adapter or recommend installing `user-management-pack`
     first.
   - The name of the top-level admin role (`SiteAdmin`? `Admin`? `Owner`?).
   - Whether a roles enumeration with `All` + `IsValid(string)` already
     exists. If not, recommend `user-management-pack`.
   - Whether `IHttpContextAccessor` / persistent data-protection keys /
     antiforgery are already wired (see SPEC §5). For each missing piece,
     propose adding it.

3. **Produce a written plan** before writing code. The plan must list:
   - The exact files you will create or modify.
   - The host-project namespace, cookie name, and admin role you will
     plug into the templated reference code.
   - Where in `_Layout.cshtml` (or equivalent) you will paste the dropdown
     and banner blocks.
   - The DI registrations you will add.
   - Any precondition (cookie auth, antiforgery, data-protection,
     `IHttpContextAccessor`, roles enum) that needs to be added first.

4. **Wait for me to approve the plan**, then implement in small commits.

5. **Acceptance test** — all of the following must work:
   1. Signed in as the admin role, the navbar shows a `View as: <role>`
      dropdown. The dropdown lists every other role exactly once, plus
      a "Become self" item.
   2. Clicking another role refreshes the page; the navbar role label
      updates; the yellow banner appears under the navbar with a
      "Become self" button.
   3. While impersonating, a page gated by `[Authorize(Policy=AdminOnly)]`
      (or equivalent) returns Forbidden / redirects to access-denied;
      becoming self restores access.
   4. Clicking "Become self" clears the cookie and restores the real role.
   5. **Security**: signed in as a lower role, manually forging the
      `ViewAs` cookie via dev tools does NOT grant elevation — every
      role gate still treats the user as the lower role.

## Stack mapping cheatsheet

| Reference (.NET 9)                       | Likely target translation                                  |
| ---------------------------------------- | ---------------------------------------------------------- |
| `IClaimsTransformation`                  | Middleware that runs after cookie-auth, before authorisation. Express: `app.use((req,res,next)=>{...})` after `passport.session()`. Django: a custom auth backend that re-wraps `request.user`. Rails: an `around_action` in `ApplicationController`. |
| `[ValidateAntiForgeryToken]`             | Your stack's CSRF helper (csurf, django.middleware.csrf, etc.) |
| `IHttpContextAccessor`                   | Your stack's request context (Flask `request`, Django middleware, Express `req`). |
| `Response.Cookies.Append(..., new CookieOptions {...})` | Your stack's cookie API. `HttpOnly`/`SameSite=Lax`/`Secure=isHttps`/`MaxAge=8h` are non-negotiable. |
| Razor `@Html.AntiForgeryToken()`          | Your view engine's CSRF token helper.                      |
| `UserRoles.All` / `UserRoles.IsValid`     | Your stack's roles model.                                  |

## Hard rules — do not violate

(From SPEC §6.)

- **Never trust the live `Role` claim to authorise the `ViewAs` toggle.**
  Use `OriginalRole` when present, otherwise the live `Role`. Inverting
  this lets an admin lose the ability to toggle out.
- **Always clone the principal — never mutate.** The framework caches and
  may call the transformer multiple times. Mutation corrupts other
  requests.
- **Always delete the cookie on empty / self / unknown role.** This is
  the toggle-off path; a buggy validation that overlooks it leaves the
  admin stuck mid-impersonation.
- **Never expose `ImpersonatorRole` as an impersonatable target** in the
  dropdown. It's a no-op anyway, but offering it is confusing.
- **Always render the yellow banner while impersonating.** It's the only
  mechanism that prevents an admin from forgetting they're in another
  role and acting on what they see.
- **`Secure` cookie flag MUST be `Request.IsHttps`-adaptive**, not
  hard-coded `false`. Hard-coding `false` in production is a CSRF amp.

## Things to ask me before deciding

- "What is the top admin role name in this project? (`SiteAdmin`?
  `Admin`? `Owner`? something else?)"
- "What app prefix should the cookie use? (default is
  `<AppName>.ViewAs`)"
- "How many lower roles are there, and what are they called? The
  dropdown will list each one."
- "Is the host project already using cookie auth + antiforgery +
  persistent data-protection keys? If any of these is missing I need
  to add it first."

## Output format I expect

- A short stack inventory.
- The plan (files + namespace mapping + DI registrations + view paste
  points + any preconditions to add first).
- Then, after I approve: code changes split into reviewable commits.

Begin by reading `SPEC.md`.
