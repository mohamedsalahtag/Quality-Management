<!--
SYNC IMPACT REPORT
==================
Version change : (template / unfilled) → 1.0.0
Bump rationale : Initial population of the constitution from the empty template.
                 Treated as a MINOR bump from a 0.0.0 placeholder baseline because
                 the document is being introduced with a complete set of governing
                 principles for the first time.

Principles added (6):
  I.   Existing Architecture Is Authoritative
  II.  Dapper + Versioned SQL Migrations (NON-NEGOTIABLE)
  III. Service-as-Repository — Interface + Implementation Pairs
  IV.  AD-First Cookie Authentication with Policy-Based Authorization
  V.   SAP OData Is Read-Only (NON-NEGOTIABLE)
  VI.  Surgical Changes, Append-Only Audit, Living Project Memory

Principles removed : none (template placeholders only — nothing pre-existed).

Sections added :
  • Stack & Tooling Constraints
  • Development Workflow & Quality Gates
  • Governance

Sections removed : none.

User-request reconciliation (important):
  The /speckit-constitution invocation described the stack as "EF Core
  migrations" with "Controllers/Services/Repositories". The actual codebase
  (verified by reading SharbatlyQMS.Web.csproj and Services/) uses **Dapper
  + raw Microsoft.Data.SqlClient** with **versioned SQL migrations under
  `app/db/V##__*.sql`** applied by a custom `SharbatlyQMS.Migrate` console
  tool. There is no Repository layer — Service interfaces own their SQL
  directly. The constitution is therefore grounded in what really exists,
  not in the EF-Core/Repository description.

Templates / docs requiring updates:
  ✅ `.specify/templates/plan-template.md` — already references the
     constitution generically via "Constitution Check"; no edit required.
     The new principles populate the gate.
  ✅ `.specify/templates/spec-template.md` — stack-agnostic; no edit.
  ✅ `.specify/templates/tasks-template.md` — stack-agnostic; no edit.
  ✅ `.specify/templates/checklist-template.md` — no constitution coupling.
  ✅ `CLAUDE.md` — already aligns: behavioral rules (think before coding,
     simplicity, surgical changes, hard rules) match Principles I and VI.
  ✅ `PROJECT_STATE.md` — already the canonical decisions log invoked by
     Principle VI; no constitution-driven edit needed.

Follow-up TODOs : none. RATIFICATION_DATE captured as 2026-05-20 (today),
                  matching the initial adoption of the constitution.
-->

# Sharbatly QMS Constitution

## Core Principles

### I. Existing Architecture Is Authoritative

The Sharbatly QMS web app under `app/SharbatlyQMS.Web/` is an ASP.NET Core 9 MVC monolith
with a stable, deliberate folder layout. Every new feature MUST fit into the existing
layout without inventing new top-level folders or new architectural layers:

- Controllers live in `app/SharbatlyQMS.Web/Controllers/<Feature>Controller.cs` (PascalCase, suffix `Controller`).
- Services and their interfaces live in `app/SharbatlyQMS.Web/Services/`. Each domain service is a
  matched pair `I<Name>Service.cs` + `<Name>Service.cs` (e.g. `IClaimService.cs` + `ClaimService.cs`).
- Persisted domain entities live in `app/SharbatlyQMS.Web/Models/<Entity>.cs`. Page-specific
  view models live in `app/SharbatlyQMS.Web/ViewModels/<Name>Vm.cs` (suffix `Vm`).
- Razor views live in `app/SharbatlyQMS.Web/Views/<Controller>/<Action>.cshtml`. Shared partials
  live in `Views/Shared/`; feature-private partials use the `_` prefix
  (e.g. `Views/ClaimManagement/_ClaimChatPanel.cshtml`).
- Static assets live under `wwwroot/`; uploaded user content lives under `wwwroot/uploads/`
  or `wwwroot/branding/`.

**Rule:** A new feature MUST extend these folders by adding new files. Existing files SHOULD NOT
be modified unless the feature genuinely requires it (e.g. registering a new service in
`Program.cs`, adding a nav item to `_Layout.cshtml`, or extending a shared partial). When in
doubt, copy the pattern used by the nearest sibling feature (e.g. mirror `QualityOrdersController`
+ `QualityOrderService` + `Views/QualityOrders/` when scaffolding new MVC slices).

**Rationale:** The codebase already encodes a working set of conventions. Re-deriving them per
feature causes drift, dead code, and review churn. The folder structure IS the architecture.

---

### II. Dapper + Versioned SQL Migrations (NON-NEGOTIABLE)

Data access uses **Dapper over `Microsoft.Data.SqlClient`** against SQL Server. **EF Core is
not used and MUST NOT be introduced.** Every new service MUST follow the pattern established
by `ClaimService`, `QualityOrderService`, etc.:

- Inject `IConfiguration`; read the connection string from `ConnectionStrings:Default`.
- A private `SqlConnection Open() => new(_cs);` helper; create a fresh connection per call.
- Mutating operations open the connection explicitly and run inside a single
  `BeginTransaction()` block where multiple statements must succeed together.
- All SQL is **parameterized** via Dapper's `@param` syntax — never string-concatenate user input.
- Snake-case column names in SQL map to PascalCase properties via Dapper aliasing
  (`SELECT col AS Prop`).

**Schema changes ship as versioned SQL migrations under `app/db/V##__<name>.sql`:**

- File names MUST be `V<two-digit-number>__<snake_case_summary>.sql`, sequentially numbered
  (current top is `V14__claim_read_markers.sql`).
- Migrations are applied by the `SharbatlyQMS.Migrate` console tool with
  `dotnet run -- apply "<conn>" "<scriptPath>"`. They are NOT applied at app startup.
- Migrations MUST be **idempotent** where reasonable (`IF OBJECT_ID('X','U') IS NULL CREATE TABLE...`,
  `IF EXISTS (...) ALTER TABLE...`). A re-run on a partially-applied database MUST NOT fail.
- A migration that has been applied to production (i.e. shipped in a deploy) MUST NOT be edited.
  Corrections ship as a NEW migration with the next available number.
- Each migration ends with a small sanity `SELECT` so the migrate tool can confirm objects
  exist (the tool prints SELECT results to console).

**Rationale:** The team has rejected EF Core deliberately. Dapper keeps SQL visible and
debuggable; SQL migrations keep schema history append-only and reviewable in the same diff
as the consuming service code.

---

### III. Service-as-Repository — Interface + Implementation Pairs

There is **no separate Repository layer.** A "Service" in this codebase is the unit that owns
both business rules AND the SQL for its aggregate. Every domain service MUST:

- Expose a public interface `I<Name>Service` in `Services/I<Name>Service.cs` containing
  ONLY `Task`-returning members (async throughout).
- Provide one implementation `<Name>Service` in `Services/<Name>Service.cs`.
- Be registered in `Program.cs` as `builder.Services.AddScoped<I<Name>Service, <Name>Service>();`
  next to the existing `AddScoped` block.
- Be depended on by Controllers via the interface only — never by concrete type.
- Return tuple-of-result-and-error `(bool ok, string? error)` for mutations that can fail
  validation, matching the convention in `ClaimService.MarkClaimRequestAsync`, etc. Throw only
  for invariant violations the caller cannot meaningfully recover from.

`DbService` is a low-level helper for the `SiteConfiguration` key/value table and similar
cross-cutting reads; it is NOT a generic repository and MUST NOT grow into one. New aggregates
get their own service.

**Rationale:** Two parallel layers (Repository + Service) for a Dapper codebase produces
ceremony without isolation benefits — the service already speaks SQL. Keeping them merged
matches what every existing service does today.

---

### IV. AD-First Cookie Authentication with Policy-Based Authorization

Authentication and authorization MUST follow the wiring already in `Program.cs`. New features
MUST NOT introduce a new auth scheme, a new claims pipeline, or bypass the role policies.

**Authentication:**

- Cookie auth via `AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(...)`.
  Cookie name `SharbatlyQMS.Auth`, 30-day sliding expiration, `SameSite=Lax`, `HttpOnly=true`.
- Data-protection keys persisted to `<ContentRoot>/keys/` with `SetApplicationName("SharbatlyQMS")`
  so cookies survive app restarts.
- Login uses **AD bind-only** via `System.DirectoryServices` — no `DirectorySearcher` before a
  successful bind. Wrong-password from AD (`0x8007052E` / `0x80070005`) is definitive.
- BCrypt local fallback (`BCrypt.Net-Next`) remains for any pre-AD seed user.
- Login normalization: store the AD-returned `sAMAccountName` as `Users.Username` (strip `@domain`).
- A global `AuthorizeFilter` in `AddControllersWithViews` means every controller requires auth
  by default. Public endpoints opt in with `[AllowAnonymous]`.

**Authorization:**

- Roles MUST be drawn from `Models/User.cs` → `UserRoles.All`
  (`Viewer`, `Operator`, `Manager`, `ClaimManager`, `SiteAdmin`).
- Policies MUST be referenced by the constants in `AuthPolicies.*`
  (`AdminOnly`, `ManagerOrAdmin`, `OperatorOrAbove`, `ClaimManagerOrAdmin`) — never by raw role
  strings inline.
- `AdminController`'s class-level gate is `ManagerOrAdmin` (the loosest action). Every
  SiteAdmin-only action MUST add its own `[Authorize(Policy = AuthPolicies.AdminOnly)]`.
  This rule extends to any future controller that mixes role tiers: the class-level attribute
  is the LOOSEST gate any action needs; tighter actions add their own attribute.
- `IClaimsTransformation` (`ViewAsClaimsTransformer`) handles SiteAdmin role impersonation via
  the `SharbatlyQMS.ViewAs` cookie. New features MUST work correctly when the live `Role` claim
  has been swapped — i.e. they MUST authorize against the live claim, not against `OriginalRole`.

**Rationale:** Authentication and role gating are the single most security-sensitive surface
in the app. Drift here means real privilege escalation. The policy-and-constant pattern keeps
the gate enumerable in code review.

---

### V. SAP OData Is Read-Only (NON-NEGOTIABLE)

The QMS is an external execution layer that reads SAP S/4HANA via OData but **never writes
back to SAP**. Every interaction with SAP MUST:

- Go through `ISapClient` (and `ISapODataClient` / `ISapSyncService` where applicable). Controllers
  MUST NOT call SAP OData URLs directly.
- Use only HTTP GET against SAP. POST / PUT / PATCH / DELETE against an SAP endpoint is forbidden
  even if the OData service technically permits it.
- Treat the SAP service password as **write-only** in the admin UI: rendered blank, only saved
  when the posted value is non-empty.
- Be tolerant of SAP downtime — cache where appropriate (see `MaraService`, `VendorService`,
  `CatalogCache`), surface a friendly error to the UI, and never block the user from doing local
  work because of an SAP outage.

**Rationale:** SAP is the system of record for purchasing/logistics. Writing back from a
secondary system risks data corruption and breaks the contract with the ERP team. Concentrating
the boundary in `ISapClient` keeps the rule mechanically enforceable in review.

---

### VI. Surgical Changes, Append-Only Audit, Living Project Memory

Three rules that govern HOW change lands, not WHAT changes:

**Surgical changes.** When extending the app:

- Touch only files the new feature genuinely requires. Do not reformat, rename, or "improve"
  adjacent unrelated code.
- New behavior SHOULD ship as new files (new controller, new service pair, new views) rather
  than by enlarging an existing god-class. Mirror the layout of the nearest sibling feature.
- When in doubt, copy the pattern that already exists — do not invent a parallel one.

**Append-only audit trails.** The following tables are append-only — code MUST NOT UPDATE or
DELETE rows in them outside of a documented data-cleanup migration:

- `qms_status_history` — every state transition for arrivals, quality orders, claims.
- `qms_audit_log` — admin overrides, configuration changes.
- `qms_claim_note` — Quality-Manager / Claim-Manager chat history.
- `qms_report_log` — outgoing PDF and email events.

INSERTs into these tables MUST happen inside the same transaction as the change they describe,
so a partial failure cannot leave the audit and the live row out of sync.

**Living project memory.** After any meaningful change (a new feature, a new migration, a
production-affecting decision):

- Prepend a dated bullet to `PROJECT_STATE.md` §8 (Decisions log) capturing the change and
  rationale.
- If the change altered paths, ports, services, or auth flow, also patch the relevant facts in
  §2 (Where everything lives), §5 (Production deployment), or §6 (Authentication & users).
- Reusable packs under `<name>-pack/` follow the convention established by the existing five
  packs (`alert-pack/`, `email-notification-pack/`, `theme-pack/`, `user-management-pack/`,
  `view-as-pack/`): each has `README.md`, `SPEC.md`, `INSTALL_PROMPT.md`,
  `How to use it for a new project.txt`, `reference/`, and `examples/`. New packs MUST follow
  this layout.

**Rationale:** This codebase is maintained primarily by AI assistants picking up where the
previous session left off. The decisions log IS the handoff; audit tables ARE the rollback
story; surgical-change discipline keeps reviews humanly possible.

---

## Stack & Tooling Constraints

The locked technology choices below are part of the constitution. Adding a runtime dependency
not on this list is an amendment requiring the procedure in **Governance**.

| Concern              | Locked choice |
| -------------------- | --- |
| Runtime              | .NET 9 / ASP.NET Core MVC monolith |
| Razor + CSS          | Bootstrap 5.3 (no SPA framework) |
| Data access          | Dapper 2.x + `Microsoft.Data.SqlClient` 7.x — **no EF Core** |
| Database             | SQL Server (current production host: `192.168.3.10`, database `SharbatlyQMS`) |
| Migrations           | Versioned SQL files in `app/db/V##__*.sql`, applied by `SharbatlyQMS.Migrate` |
| Authentication       | Cookie auth + `System.DirectoryServices` (AD bind) + `BCrypt.Net-Next` fallback |
| Authorization        | Policy-based via `AuthPolicies.*` constants in `Models/User.cs` |
| PDF generation       | QuestPDF (community licence) |
| SMTP                 | MailKit |
| Image processing     | SixLabors.ImageSharp |
| Hosting              | Windows Service via `Microsoft.Extensions.Hosting.WindowsServices` |
| Production binding   | Kestrel on `http://0.0.0.0:5244` (LAN: `http://192.168.3.192:5244`) |
| Deployment           | `deploy/Republish.ps1` (stop service → `dotnet publish -c Release` → start service) |

The reusable packs (`alert-pack/`, `email-notification-pack/`, `theme-pack/`,
`user-management-pack/`, `view-as-pack/`) target this exact combination. New features should
copy from the relevant pack's `reference/*` rather than re-deriving the pattern.

---

## Development Workflow & Quality Gates

Every feature, fix, or chore MUST pass the following gates in order:

1. **Read `PROJECT_STATE.md` first** (and the relevant pack `SPEC.md` if the feature overlaps a
   pack's scope). The decisions log captures live constraints that are not always obvious from
   code alone.
2. **State assumptions explicitly** before writing code. If the requirements admit multiple
   reasonable interpretations, surface them and ask before picking one.
3. **Plan small.** For multi-step work, write the steps down and verify each one before moving
   on. For trivial fixes, judgement applies.
4. **Build clean.** `dotnet build app/SharbatlyQMS.Web` MUST report zero warnings and zero
   errors before redeploy. Warnings are not deferred TODOs.
5. **Migrate explicitly.** Schema changes are applied via `dotnet run -- apply` against the
   `SharbatlyQMS.Migrate` tool — never via app startup.
6. **Redeploy via `deploy/Republish.ps1`.** The script stops the service, publishes Release
   bits, and starts the service back up. After
   `Grant-ServiceRights.ps1` has been run once, the redeploy proceeds without UAC prompts.
7. **Smoke-check the running app** (`http://localhost:5244/Account/Login` → HTTP 200) before
   declaring the work done.
8. **Update `PROJECT_STATE.md` §8.** A dated bullet describing what changed and why, in the
   handoff style of existing entries.

For UI-affecting changes, exercise the golden path AND at least one edge case in a real browser
before reporting the task complete. Type-checking and a clean build are not feature
verification.

---

## Governance

**Supremacy.** This constitution supersedes ad-hoc conventions and prior chat history. When a
chat instruction conflicts with a principle here, the principle wins and the conflict MUST be
surfaced to the user.

**Amendments.** Changing or adding a principle requires:

1. A written proposal explaining the change and the rationale.
2. Bumping `Version` per semver:
   - **MAJOR** — a principle is removed or its meaning is materially redefined.
   - **MINOR** — a new principle is added, or an existing one is expanded with new
     non-negotiable rules.
   - **PATCH** — clarifications, wording fixes, or examples that do not change which
     code is acceptable.
3. Updating `LAST_AMENDED_DATE` to the date of merge.
4. Re-running the Sync Impact comment block at the top of this file.
5. Re-validating dependent templates under `.specify/templates/` and `PROJECT_STATE.md` for
   consistency.

**Compliance review.** Any pull request, planning document, or implementation handoff MUST
declare which principles it touched (typically Principle I + one or two others) and confirm it
satisfies them. The **Constitution Check** gate in `.specify/templates/plan-template.md` runs
this verification before Phase 0 and again after Phase 1 design.

**Justified deviations.** A feature that genuinely cannot satisfy a principle MUST document
the deviation in its plan's *Complexity Tracking* section with: the principle violated, why
the violation is needed, and the simpler alternative that was rejected and why.

**Runtime guidance.** Day-to-day execution guidance for AI assistants and humans lives in
`CLAUDE.md` (behavioural rules, simplicity, surgical-change discipline) and `PROJECT_STATE.md`
(live state, decisions log, hard rules). Both are aligned with this constitution and MUST be
kept in sync when principles change.

**Version**: 1.0.0 | **Ratified**: 2026-05-20 | **Last Amended**: 2026-05-20
