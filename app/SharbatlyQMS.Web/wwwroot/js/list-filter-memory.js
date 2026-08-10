// Remembers the last-used filters for a list page and restores them when the
// page is opened fresh (no querystring) — e.g. from the nav menu or after
// reopening the browser. Filters + the status buttons already serialize into
// the GET querystring, so capturing location.search captures everything.
//
// Opt in with data-filter-memory="<pageKey>" on the filter <form>, and
// data-filter-clear on any Clear/Reset control. Inputs that are forced by the
// server rather than chosen by the user — e.g. a plant-scoped user's locked
// plant hidden input — carry data-filter-ignore so they are NOT remembered
// (otherwise they'd look like a filter: a phantom "1" badge + a panel that
// re-opens every visit). Empty params are dropped too, keeping the URL clean.
(function () {
    var KEY_PREFIX = 'qms.listfilter.';

    function get(k) { try { return window.localStorage.getItem(k); } catch (_) { return null; } }
    function set(k, v) { try { window.localStorage.setItem(k, v); } catch (_) { /* private mode / quota */ } }
    function del(k) { try { window.localStorage.removeItem(k); } catch (_) { } }

    // Keep only meaningful, user-chosen params: drop empties and any param whose
    // name matches a data-filter-ignore field (server-forced values).
    function clean(search, ignore) {
        var out = [];
        var params = new URLSearchParams(search || '');
        params.forEach(function (v, k) {
            if (!v) return;
            if (ignore.indexOf(k) !== -1) return;
            out.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
        });
        return out.length ? '?' + out.join('&') : '';
    }

    function init() {
        var host = document.querySelector('[data-filter-memory]');
        if (!host) return;
        var pageKey = KEY_PREFIX + host.getAttribute('data-filter-memory');

        var ignore = Array.prototype.map
            .call(document.querySelectorAll('[data-filter-ignore]'), function (el) { return el.getAttribute('name'); })
            .filter(Boolean);

        // Clear/Reset: forget the remembered filter, then let the link navigate.
        document.querySelectorAll('[data-filter-clear]').forEach(function (el) {
            el.addEventListener('click', function () { del(pageKey); });
        });

        if (window.location.search && window.location.search.length > 1) {
            // A submitted view — remember only the meaningful filters.
            var cleaned = clean(window.location.search, ignore);
            if (cleaned.length > 1) set(pageKey, cleaned); else del(pageKey);
            return;
        }

        // Bare URL: restore the last remembered filter once (re-cleaned, so a
        // stale entry saved before this fix can't reintroduce a forced param).
        var saved = clean(get(pageKey) || '', ignore);
        if (saved.length > 1) {
            window.location.replace(window.location.pathname + saved);
        } else {
            del(pageKey);
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
