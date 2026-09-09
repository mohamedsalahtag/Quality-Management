/* theme-pack v1.0 — drop-in theme controller for Bootstrap 5.3 web apps.
   Place this <script> at the end of <body> AFTER Bootstrap JS.
   The FOUC-prevention snippet (partial-fouc-head.html) goes inline in <head>.

   API:
     ThemePack.get()              -> current theme name
     ThemePack.set(name)          -> set theme programmatically
     ThemePack.themes             -> list of theme names
     ThemePack.darkThemes         -> themes that should set data-bs-theme="dark"
     document.addEventListener("theme-pack:change", e => e.detail.theme)
*/
(function () {
    "use strict";

    var STORAGE_KEY  = "app-theme";
    var DEFAULT      = "light";
    var KNOWN        = ["light", "dark", "blue", "sepia", "forest", "rose"];
    var DARK_THEMES  = ["dark"]; // themes that should set data-bs-theme="dark"

    function applyTheme(t) {
        if (KNOWN.indexOf(t) < 0) t = DEFAULT;
        document.documentElement.setAttribute("data-theme", t);
        document.documentElement.setAttribute(
            "data-bs-theme",
            DARK_THEMES.indexOf(t) >= 0 ? "dark" : "light"
        );
        try { localStorage.setItem(STORAGE_KEY, t); } catch (e) { /* private mode etc. */ }
        document.dispatchEvent(new CustomEvent("theme-pack:change", { detail: { theme: t } }));
    }

    // Public API for advanced use.
    window.ThemePack = {
        get:    function () { return document.documentElement.getAttribute("data-theme") || DEFAULT; },
        set:    applyTheme,
        themes: KNOWN.slice(),
        darkThemes: DARK_THEMES.slice()
    };

    // Wire click handlers for any element with .theme-pick + data-theme attribute.
    function wirePickers() {
        document.querySelectorAll(".theme-pick").forEach(function (a) {
            if (a.dataset.themePackBound === "1") return;
            a.dataset.themePackBound = "1";
            a.addEventListener("click", function (e) {
                e.preventDefault();
                applyTheme(a.dataset.theme || DEFAULT);
            });
        });
    }
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", wirePickers);
    } else {
        wirePickers();
    }
})();
