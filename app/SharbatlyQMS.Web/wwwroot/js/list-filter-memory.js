// Remembers the last-used filters for a list page and restores them when the
// page is opened fresh (no querystring) — e.g. from the nav menu or after
// reopening the browser. Filters + the status buttons already serialize into
// the GET querystring (the status chips are <button name="status"> submits),
// so capturing location.search captures everything, including the active status.
//
// Opt in by putting data-filter-memory="<pageKey>" on the filter <form>, and
// data-filter-clear on any Clear/Reset control (so clearing also forgets the
// remembered filter). Mirrors the localStorage try/catch idiom in tablekit.js.
(function () {
    var KEY_PREFIX = 'qms.listfilter.';

    function get(k) { try { return window.localStorage.getItem(k); } catch (_) { return null; } }
    function set(k, v) { try { window.localStorage.setItem(k, v); } catch (_) { /* private mode / quota */ } }
    function del(k) { try { window.localStorage.removeItem(k); } catch (_) { } }

    function init() {
        var host = document.querySelector('[data-filter-memory]');
        if (!host) return;
        var pageKey = KEY_PREFIX + host.getAttribute('data-filter-memory');
        var search = window.location.search; // includes the leading '?'

        // Clear/Reset: forget the remembered filter, then let the link navigate.
        document.querySelectorAll('[data-filter-clear]').forEach(function (el) {
            el.addEventListener('click', function () { del(pageKey); });
        });

        if (search && search.length > 1) {
            // A filtered (or explicitly unfiltered-via-form) view — remember it.
            set(pageKey, search);
            return;
        }

        // Bare URL: restore the last remembered filter once. replace() keeps the
        // back button sane; the restored load carries a querystring so this
        // branch won't run again (no loop).
        var saved = get(pageKey);
        if (saved && saved.length > 1) {
            window.location.replace(window.location.pathname + saved);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
