# theme-pack — AI Integration Guide

> **Read this first** if you (the AI) were just handed the `theme-pack` folder and asked to add a theme system to a web project. Do these steps EXACTLY; nothing about themes needs further negotiation with the user.

## What this pack provides

A complete, drop-in theme system for any **Bootstrap 5.3+** web app. After installation, the user gets:

- **6 themes** — Light, Dark, Blue, Sepia, Forest, Rose. Switchable from a navbar palette icon.
- **Persistence** in `localStorage["app-theme"]`.
- **No flash of unstyled theme** on page load (FOUC prevented by an inline `<head>` script).
- **Coloured picker icons** (sun yellow / indigo / sky blue / sienna / forest / rose) so each theme is visually identifiable.
- Themes cover: body, headings, labels, links, cards, card-headers, tables (incl. `thead.table-light`/`tfoot.table-light`), form controls, input groups, dropdowns, modals, navbar (`bg-primary`), `bg-light` utility, `<pre>` blocks, alerts.
- Bootstrap accent card-headers (`bg-info`, `bg-success`, `bg-primary`, `bg-secondary`, `bg-warning`, `bg-danger`) keep their original contrast colours — they're treated as section identifiers.

## Files in this pack

| File | Purpose |
|------|---------|
| `theme-pack.css` | All theme styles |
| `theme-pack.js`  | Click handler + public `window.ThemePack` API |
| `partial-fouc-head.html` | Inline `<head>` script — paste contents BEFORE any CSS |
| `partial-theme-picker.html` | Navbar dropdown HTML — paste inside the navbar's right-side `<ul>` |
| `examples/` | Reference layouts for ASP.NET Core and plain HTML |

## Required dependencies in the host app

- **Bootstrap 5.3+** (uses `data-bs-theme` for built-in dark mode of components we don't override).
- **Bootstrap Icons** (for the palette / sun / moon / etc. icons).

If either is missing, add them via CDN in the host's layout `<head>`.

## Installation steps

Apply these in the host project regardless of framework. Adapt only the static-asset paths to the host's conventions.

### 1. Copy the static files

Copy these files into the host project's static-assets folder (commonly `wwwroot/css/` and `wwwroot/js/` for ASP.NET Core, `public/` for Express, `static/` for Django, etc.):

- `theme-pack.css`
- `theme-pack.js`

### 2. Add the FOUC-prevention script (must be FIRST in `<head>`)

Open the host's main layout/template file. Find the `<head>` element. As the FIRST `<script>` in `<head>`, paste the contents of `partial-fouc-head.html`:

```html
<script>
    (function () {
        var t = localStorage.getItem("app-theme") || "light";
        document.documentElement.setAttribute("data-theme", t);
        document.documentElement.setAttribute("data-bs-theme", t === "dark" ? "dark" : "light");
    })();
</script>
```

This MUST run before any CSS loads so the `data-theme` attribute is set before paint. **Do not move it later in the document.**

### 3. Link the theme stylesheet — load LAST

In the host's `<head>`, AFTER any other site CSS:

```html
<link rel="stylesheet" href="/path/to/theme-pack.css" />
```

> **Critical CSS load order:**
> ```
> 1. bootstrap.min.css
> 2. bootstrap-icons.css
> 3. site.css         (host's own structural CSS — must NOT hardcode body bg/color)
> 4. theme-pack.css   ← MUST BE LAST
> ```
> Reason: `theme-pack.css` defines `body { background-color: var(--theme-bg); }` and similar rules that must beat any hardcoded color in the host's own CSS. If the host's site.css has `body { background-color: #fff; }` and loads after theme-pack, dark theme will be broken.
>
> If the host has hardcoded body colors in its own CSS, **remove them**. The theme owns body bg/color.

### 4. Insert the theme picker

Paste the contents of `partial-theme-picker.html` inside the host's main navbar `<ul class="navbar-nav">`. Conventionally to the right of the user/profile dropdown.

If the host has no navbar yet, scaffold a minimal Bootstrap navbar with `class="navbar navbar-expand-lg navbar-dark bg-primary"` and put the picker inside it.

### 5. Load the controller script

Just before `</body>`, AFTER Bootstrap's bundle JS:

```html
<script src="/path/to/theme-pack.js"></script>
```

### 6. Verify

After deploying / refreshing:

- Click the palette icon in the navbar → dropdown shows 6 colored entries.
- Click each one → page background, text colour, navbar all change.
- Refresh the page → the chosen theme persists.
- No flash of light theme on page load.
- `h1`–`h6` page headings are readable on every theme.
- Form labels readable on every theme.
- Card-headers with `bg-info` / `bg-success` / `bg-primary` keep their original colours (they identify sections).

## Customizations the user might request

| Request | What to change |
|--------|--------------|
| "Use my company's brand color for navbar" | Edit `--theme-navbar-bg` in the relevant `[data-theme="<name>"]` block in `theme-pack.css`. |
| "Add a [name] theme" | Append a new `html[data-theme="<name>"]` block in `theme-pack.css`, add a `<li>` to the picker HTML, add a `data-theme="<name>"` icon-color rule near the bottom of the CSS, and add `"<name>"` to `KNOWN` in `theme-pack.js`. |
| "Remove a theme" | Remove its CSS block, picker `<li>`, icon-color rule, and entry in `KNOWN`. |
| "Change picker location" | Move the `<li class="nav-item dropdown">` block — the JS auto-wires any `.theme-pick` element. |
| "Default to dark theme" | Change `var DEFAULT = "light"` to `"dark"` in both `theme-pack.js` AND the FOUC inline script. |
| "Change storage key" | Replace `"app-theme"` everywhere (CSS unaffected, but both scripts must agree). |

## Programmatic API (host code can call)

```js
ThemePack.get();              // current theme name (e.g. "dark")
ThemePack.set("forest");      // change theme programmatically
ThemePack.themes;             // ["light","dark","blue","sepia","forest","rose"]
ThemePack.darkThemes;         // ["dark"]  (themes that map to data-bs-theme="dark")

// Listen for changes
document.addEventListener("theme-pack:change", e => {
    console.log("Theme is now", e.detail.theme);
});
```

## Critical do's and don'ts (avoid the bugs we've already hit)

- **DO** load `theme-pack.css` LAST in the `<head>`. Otherwise hardcoded colours in site.css silently override theme tokens.
- **DO** put the FOUC script FIRST in `<head>`. Inline. Synchronous. Otherwise the page flashes light theme on load.
- **DO NOT** hardcode `body { background-color: ... }` anywhere in the host's CSS.
- **DO NOT** apply `.text-muted` or `.text-white-50` to elements that need a SPECIFIC contrast on a theme-coloured background — use the explicit `text-white` / `text-dark` carve-outs already provided.
- **DO NOT** use `a` element selectors at body scope — scope link recolours to `main a`, `.card-body a`, `.card-footer a` so navbar and dropdown links remain themed by their own rules.
- **DO** test all 6 themes visually after integration, especially Dark, Sepia, and Forest — those are most prone to contrast issues if site.css has hardcoded colors.

## Versioning

Bump the comment header in `theme-pack.css` and `theme-pack.js` if you make a breaking change. Keep variable names (`--theme-*`) stable across versions; renames break host CSS that references them.
