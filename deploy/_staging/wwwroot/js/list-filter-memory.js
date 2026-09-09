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
    var KEY_PREFIX  = 'qms.listfilter.';
    // sessionStorage marker set just before an automatic restore, read once on
    // the resulting load so the banner can say why the list is filtered.
    var FLAG_PREFIX = 'qms.listfilter.restored.';

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
            // If we got here by RESTORING (below), say so. Opening a list from
            // the nav menu and silently landing on last week's filters looks
            // like the page filtered itself: the panel springs open, the badge
            // counts, and the row count is short for no visible reason.
            announceRestore(host, pageKey);
            return;
        }

        // Bare URL: restore the last remembered filter once (re-cleaned, so a
        // stale entry saved before this fix can't reintroduce a forced param).
        var saved = clean(get(pageKey) || '', ignore);
        if (saved.length > 1) {
            // Flag it for the reload so the banner can explain what happened.
            try { window.sessionStorage.setItem(FLAG_PREFIX + pageKey, '1'); } catch (_) { }
            window.location.replace(window.location.pathname + saved);
        } else {
            del(pageKey);
        }
    }

    // Shows a dismissible line above the filter form after an automatic
    // restore, with a one-click way out. Only ever appears on the load that
    // immediately follows a restore — a filter the user typed themselves is
    // not announced.
    function announceRestore(host, pageKey) {
        var flagKey = FLAG_PREFIX + pageKey;
        var flagged;
        try {
            flagged = window.sessionStorage.getItem(flagKey);
            window.sessionStorage.removeItem(flagKey);
        } catch (_) { return; }
        if (!flagged) return;

        // Built with DOM nodes rather than innerHTML: the "show all" href is
        // location.pathname, which is caller-influenced, and concatenating it
        // into markup would be an injection hole for the sake of one link.
        var bar = document.createElement('div');
        bar.className = 'alert alert-info alert-dismissible py-2 px-3 small d-flex align-items-center gap-2 mb-2';
        bar.setAttribute('role', 'status');

        var icon = document.createElement('i');
        icon.className = 'bi bi-funnel-fill';

        var text = document.createElement('span');
        text.textContent = 'Showing the filters you last used on this screen.';

        var link = document.createElement('a');
        link.className = 'btn btn-sm btn-outline-secondary py-0';
        link.href = window.location.pathname;      // property assignment, not markup
        link.textContent = 'Show all records';
        // The "show all" link must also forget the memory, or the next visit
        // restores the same filters again.
        link.addEventListener('click', function () { del(pageKey); });

        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'btn-close';
        close.setAttribute('data-bs-dismiss', 'alert');
        close.setAttribute('aria-label', 'Close');

        bar.appendChild(icon);
        bar.appendChild(text);
        bar.appendChild(link);
        bar.appendChild(close);
        host.parentNode.insertBefore(bar, host);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
