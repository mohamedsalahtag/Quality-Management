# view-as-pack

A drop-in **SiteAdmin role-impersonation** feature for cookie-authenticated web apps. Lets a real SiteAdmin temporarily browse the site as any lower role (Viewer / Operator / Manager / ...) **without re-logging-in**. Every role gate — `[Authorize(Policy=...)]`, `User.IsInRole(...)`, menu visibility — responds as if the user actually had the impersonated role.

This pack was extracted from the [Sharbatly QMS](../) project (where it lives under `app/SharbatlyQMS.Web/`) so it can be reused in any future web app **by handing this folder to an AI coding assistant** along with the prompt:

> *"Add the View-as feature from this folder to the site you're building. Read `SPEC.md` and `INSTALL_PROMPT.md`."*

The reference implementation is ASP.NET Core MVC, but the contract in `SPEC.md` is stack-agnostic — the same design works in Express/Node, Django, Rails, etc.

## Install

See [INSTALL_PROMPT.md](INSTALL_PROMPT.md) for the full AI-driven flow. TL;DR:

1. Copy `reference/ViewAsClaimsTransformer.cs` into the host project's services folder. Rename the namespace, `CookieName`, and `ImpersonatorRole` to match the host project.
2. Paste the `ViewAs` action from `reference/AccountController.ViewAs.snippet.cs` into the host project's auth/account controller.
3. Paste the contents of `reference/_ViewAsDropdown.snippet.cshtml` into the host project's `_Layout.cshtml` — the dropdown `<li>` inside the navbar's right-aligned `<ul>`, the banner block immediately after `</nav>`.
4. Add the two DI lines from `reference/Program.cs.snippet` to the host project's `Program.cs`.

Preconditions the host project must already provide: cookie-based authentication, antiforgery middleware, persistent data-protection keys, `IHttpContextAccessor`, and a roles enumeration. See [SPEC.md §5 Stack assumptions](SPEC.md).

## Examples

- [examples/aspnet-core-wiring.cs.txt](examples/aspnet-core-wiring.cs.txt) — minimal end-to-end `Program.cs` + a tiny controller for a fresh ASP.NET Core MVC app, showing where every piece goes.

## Public API

```csharp
// Constants (rename CookieName + ImpersonatorRole per host project)
ViewAsClaimsTransformer.CookieName        // "MyApp.ViewAs"
ViewAsClaimsTransformer.OriginalRoleClaim // "OriginalRole"
ViewAsClaimsTransformer.ImpersonatorRole  // e.g. UserRoles.SiteAdmin

// HTTP endpoint
POST /Account/ViewAs
    form: role=<roleName | "">, returnUrl=<localPath>
    auth: real role must be ImpersonatorRole (else 403)

// Claims behaviour (read in Razor / controllers while impersonating)
User.FindFirst(ClaimTypes.Role)?.Value                       // impersonated role
User.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim)    // real role (SiteAdmin)
```

## How it works

1. SiteAdmin clicks a role in the navbar dropdown → form `POST`s to `/Account/ViewAs`.
2. The controller validates the requester is really SiteAdmin, then writes an `HttpOnly` 8-hour cookie named e.g. `MyApp.ViewAs` with the chosen role.
3. On every subsequent request, `ViewAsClaimsTransformer` runs as part of authentication: if the real `Role` claim is `ImpersonatorRole` AND the cookie names a valid lower role, it **clones** the principal, swaps the `Role` claim to the cookie value, and adds an `OriginalRole` claim preserving the real role.
4. A yellow banner under the navbar shows the impersonated role and a "Become self" button while the cookie is set.
5. Posting `role=""` clears the cookie and reverts to self.

A non-admin who manually forges the cookie gets **no elevation** — the transformer ignores the cookie unless the real Role claim is already the ImpersonatorRole. See [SPEC.md §4 Security guarantees](SPEC.md).

## License

MIT-style — copy / modify freely.
