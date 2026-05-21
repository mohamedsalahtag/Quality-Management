# theme-pack

A drop-in theme system for **Bootstrap 5.3+** web apps. Six themes (Light, Dark, Blue, Sepia, Forest, Rose), no flash on load, persists in localStorage, framework-agnostic (works with ASP.NET Core, Express/Node, plain HTML, Django, Flask, Rails — anything that serves Bootstrap-based HTML).

This pack was extracted from the [Production Control](../) project so it can be reused in any future web app **by handing this folder to an AI coding assistant** along with the prompt:

> *"Add the theme system from this folder to the site you're building. Read INTEGRATION.md."*

## Install

See [INTEGRATION.md](INTEGRATION.md) for the full step-by-step guide. TL;DR:

1. Copy `theme-pack.css` and `theme-pack.js` into the host project's static assets.
2. Paste the contents of `partial-fouc-head.html` as the FIRST `<script>` in the host layout's `<head>`.
3. Add `<link rel="stylesheet" href=".../theme-pack.css">` as the LAST stylesheet in `<head>`.
4. Paste the contents of `partial-theme-picker.html` inside the navbar's `<ul class="navbar-nav">`.
5. Add `<script src=".../theme-pack.js"></script>` just before `</body>`.

## Examples

- [examples/aspnet-core-layout.cshtml](examples/aspnet-core-layout.cshtml.txt) — minimal Razor `_Layout.cshtml` with the pack wired in.
- [examples/plain-html.html](examples/plain-html.html) — single-file HTML demo of all 6 themes.

## Themes included

| Name   | Vibe                          | Navbar bg   | Body bg   | Body text |
|--------|-------------------------------|-------------|-----------|-----------|
| light  | Default Bootstrap-style       | `#0d6efd`   | `#f8f9fa` | `#212529` |
| dark   | Pure dark mode (`data-bs-theme="dark"`) | `#0d2237`   | `#15181c` | `#e9ecef` |
| blue   | Cool corporate blue           | `#0a3a78`   | `#e7eef9` | `#0a2540` |
| sepia  | Warm reading-room             | `#6b4319`   | `#f4ecd8` | `#5b4636` |
| forest | Outdoor green                 | `#1f5731`   | `#eaf3eb` | `#143821` |
| rose   | Soft rose                     | `#8c2a44`   | `#fdeef0` | `#4a1c2a` |

## Customizing

Edit the `[data-theme="<name>"]` block in `theme-pack.css` to change a theme's colours, or append a new block to add a theme. See INTEGRATION.md → "Customizations".

## Public API

```js
ThemePack.get();         // current theme name
ThemePack.set("dark");   // switch programmatically
ThemePack.themes;        // ["light","dark","blue","sepia","forest","rose"]

document.addEventListener("theme-pack:change", e => {
    console.log(e.detail.theme);
});
```

## License

MIT-style — copy / modify freely.
