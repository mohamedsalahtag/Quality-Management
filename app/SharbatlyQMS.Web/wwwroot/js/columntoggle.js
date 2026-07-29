// columntoggle: let the user hide columns they don't need, and remember it.
//
// Opt in by adding `data-coltoggle` to a <table> that also has an `id`:
//   <table id="pending-containers" data-coltoggle>
//     <th data-col-key="bol">BOL</th>        <!-- hideable -->
//     <th data-col-key="action" data-col-lock>Action</th>   <!-- always shown -->
//
// A "Columns" dropdown is injected above the table. Ticking a box applies
// immediately and writes to localStorage — there is no save button, by design.
//
// Why CSS instead of touching cells: tablekit (the sorting/pagination layer on
// the same tables) reads th.cellIndex and caches its row list at enhance()
// time, so removing or display:none-ing individual cells would break sorting
// and desync its pager. Instead this writes ONE <style> element per table with
//   #tableId tr > :nth-child(k) { display: none }
// which hides the header and every body cell of that column in one rule and
// leaves cellIndex untouched. tablekit owns tr.style; this owns a stylesheet;
// the two never collide.
//
// Preferences are stored per-table by column KEY (not index), so adding a
// column later doesn't shift what the user hid.
(function () {
    var STORAGE_PREFIX = 'tablekit.columns.';

    function safeStorageGet(key) {
        try { return window.localStorage.getItem(key); } catch (_) { return null; }
    }
    function safeStorageSet(key, value) {
        try { window.localStorage.setItem(key, value); } catch (_) { /* quota / private mode */ }
    }

    function readHidden(tableId) {
        var raw = safeStorageGet(STORAGE_PREFIX + tableId);
        if (!raw) return [];
        try {
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        } catch (_) { return []; }
    }

    function enhance(table) {
        if (!table.id) return;                       // storage is keyed by id
        var headRow = table.tHead && table.tHead.rows[0];
        if (!headRow) return;

        var cols = Array.prototype.slice.call(headRow.cells)
            .map(function (th, i) {
                return {
                    key:    th.getAttribute('data-col-key'),
                    label:  (th.textContent || '').trim(),
                    locked: th.hasAttribute('data-col-lock'),
                    nth:    i + 1                    // :nth-child is 1-based
                };
            })
            .filter(function (c) { return !!c.key; });
        if (cols.length === 0) return;

        var hidden = readHidden(table.id).filter(function (k) {
            // Drop stale keys, and never honour a stored hide on a locked column.
            return cols.some(function (c) { return c.key === k && !c.locked; });
        });

        var style = document.createElement('style');
        document.head.appendChild(style);

        function apply() {
            var rules = cols
                .filter(function (c) { return hidden.indexOf(c.key) !== -1; })
                .map(function (c) {
                    return '#' + table.id + ' tr > :nth-child(' + c.nth + '){display:none}';
                });
            style.textContent = rules.join('\n');
            safeStorageSet(STORAGE_PREFIX + table.id, JSON.stringify(hidden));
            if (countBadge) {
                countBadge.textContent = hidden.length ? String(hidden.length) : '';
                countBadge.classList.toggle('d-none', hidden.length === 0);
            }
        }

        // ---- Dropdown UI -------------------------------------------------
        var wrap = document.createElement('div');
        wrap.className = 'dropdown d-inline-block coltoggle';

        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary dropdown-toggle';
        btn.setAttribute('data-bs-toggle', 'dropdown');
        btn.setAttribute('data-bs-auto-close', 'outside');   // keep open while ticking
        btn.setAttribute('aria-expanded', 'false');
        btn.innerHTML = '<i class="bi bi-layout-three-columns me-1"></i>Columns ';

        var countBadge = document.createElement('span');
        countBadge.className = 'badge bg-secondary ms-1 d-none';
        countBadge.title = 'Columns currently hidden';
        btn.appendChild(countBadge);

        var menu = document.createElement('div');
        menu.className = 'dropdown-menu dropdown-menu-end p-2';
        menu.style.minWidth = '13rem';

        cols.forEach(function (c) {
            var item = document.createElement('div');
            item.className = 'form-check form-switch mb-1';

            var input = document.createElement('input');
            input.className = 'form-check-input';
            input.type = 'checkbox';
            input.id = table.id + '-col-' + c.key;
            input.checked = hidden.indexOf(c.key) === -1;
            input.disabled = c.locked;

            var label = document.createElement('label');
            label.className = 'form-check-label small';
            label.setAttribute('for', input.id);
            label.textContent = c.label || c.key;
            if (c.locked) label.textContent += ' (always shown)';

            input.addEventListener('change', function () {
                var at = hidden.indexOf(c.key);
                if (input.checked) { if (at !== -1) hidden.splice(at, 1); }
                else if (at === -1) { hidden.push(c.key); }
                apply();
            });

            item.appendChild(input);
            item.appendChild(label);
            menu.appendChild(item);
        });

        var reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'btn btn-sm btn-link p-0 mt-1';
        reset.textContent = 'Show all';
        reset.addEventListener('click', function () {
            hidden.length = 0;
            menu.querySelectorAll('input[type=checkbox]').forEach(function (i) { i.checked = true; });
            apply();
        });
        menu.appendChild(document.createElement('hr')).className = 'my-2';
        menu.appendChild(reset);

        wrap.appendChild(btn);
        wrap.appendChild(menu);

        // Park the control in the table's toolbar if the page provides one,
        // otherwise directly above the table.
        var host = document.querySelector('[data-coltoggle-host="' + table.id + '"]');
        if (host) {
            host.appendChild(wrap);
        } else {
            var bar = document.createElement('div');
            bar.className = 'd-flex justify-content-end mb-2';
            bar.appendChild(wrap);
            table.parentNode.insertBefore(bar, table);
        }

        apply();
    }

    function init() {
        document.querySelectorAll('table[data-coltoggle]').forEach(enhance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
