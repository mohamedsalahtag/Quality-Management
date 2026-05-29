# CLAUDE.md — Sharbatly QMS

Behavioral guide for Claude Code (and any AI assistant that reads this file).

## READ FIRST

Before doing anything in this repository, read these two files in order:

1. **`PROJECT_STATE.md`** (this folder) — live state of the QMS web app, where it runs, what is built, the decisions log, and the hard rules. The single best file to bring a fresh AI session up to speed.
2. **`QMS_Extended_Assessment_and_Execution_Plan.md`** — the original 16-week plan + normalized SQL schema. Still the source of truth for table names, status codes, role logic, and build-phase order.

When working inside a pack folder (`alert-pack/`, `email-notification-pack/`, `theme-pack/`, `user-management-pack/`), also read that pack's `SPEC.md` and `README.md`.

Claude Code only: auto-memory lives in `~/.claude/projects/<dir-slug>/memory/MEMORY.md`. That index is loaded into context automatically. Other AI tools do not see it — `PROJECT_STATE.md` is the cross-AI source of truth and should be kept current.

---

## Behavioral rules

These rules bias toward caution over speed. For trivial tasks, use judgment.

### 1. Think before coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them — don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

### 2. Simplicity first

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

### 3. Surgical changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it — don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that **your** changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: every changed line should trace directly to the user's request.

### 4. Goal-driven execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

### 5. Project-specific hard rules

See `PROJECT_STATE.md §9` for the full list. Summary:

- **SAP OData is read-only** — never write back to SAP; always go through `ISapClient`.
- **AD bind-only at login** — no `DirectorySearcher` before a successful bind.
- **No self-registration UI.**
- **AdminController class-level gate is `ManagerOrAdmin`** — every SiteAdmin-only action needs its own `[Authorize(Policy = AdminOnly)]`.
- **Login normalization** — store the `sAMAccountName` (no `@domain`) as `Users.Username`.
- **No `/Account/Setup` and no seed-hash back-door** — removed deliberately.

### 6. After meaningful changes

Update `PROJECT_STATE.md`:

- **§8 Decisions log** — prepend a dated bullet for the change and its rationale.
- **§2 Where everything lives**, **§5 Production deployment**, **§6 Authentication & users** — patch if you altered paths, ports, services, or auth flow.

This is how the next AI session (or human) picks up where you left off.

---

**These guidelines are working if:** fewer unnecessary changes in diffs, fewer rewrites due to overcomplication, and clarifying questions come before implementation rather than after mistakes.

<!-- SPECKIT START -->
For additional context about technologies to be used, project structure,
shell commands, and other important information, read the current plan at
`specs/001-audit-trail/plan.md` (with companion files `research.md`,
`data-model.md`, `quickstart.md`, and `contracts/audit-endpoints.md` in the
same directory).
<!-- SPECKIT END -->
