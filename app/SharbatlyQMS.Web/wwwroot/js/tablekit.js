// tablekit: progressive client-side sorting + pagination for list tables.
//
// Opt in by adding `data-tablekit` to a <table>. Optional:
//   data-page-size="50"          rows per page (also user-selectable, see below)
//   <th data-sort>               sortable column (text compare)
//   <th data-sort="num">         numeric compare
//
// Page size: defaults to 50; users can pick 25 / 50 / 100 via a built-in
// selector next to the pager. The chosen value is persisted in localStorage
// (per-table when the table has an id, plus a global fallback) so it survives
// reloads and navigation.
//
// Empty-state rows (a <tr> whose cell uses colspan) are ignored, so the
// "No records yet" placeholder is never sorted or paginated. No server,
// controller, or query changes — purely a rendering enhancement.
(function () {
    var DEFAULT_PAGE_SIZE = 50;
    var PAGE_SIZE_OPTIONS = [25, 50, 100];
    var GLOBAL_STORAGE_KEY = 'tablekit.pageSize';

    function safeStorageGet(key) {
        try { return window.localStorage.getItem(key); } catch (_) { return null; }
    }
    function safeStorageSet(key, value) {
        try { window.localStorage.setItem(key, value); } catch (_) { /* quota / private mode */ }
    }

    function resolveInitialPageSize(table) {
        var tableKey = table.id ? GLOBAL_STORAGE_KEY + '.' + table.id : null;
        var stored = tableKey ? safeStorageGet(tableKey) : null;
        if (!stored) stored = safeStorageGet(GLOBAL_STORAGE_KEY);
        var fromStored = parseInt(stored, 10);
        if (!isNaN(fromStored) && PAGE_SIZE_OPTIONS.indexOf(fromStored) !== -1) {
            return fromStored;
        }
        var fromAttr = parseInt(table.getAttribute('data-page-size'), 10);
        if (!isNaN(fromAttr) && fromAttr > 0) return fromAttr;
        return DEFAULT_PAGE_SIZE;
    }

    function persistPageSize(table, value) {
        var tableKey = table.id ? GLOBAL_STORAGE_KEY + '.' + table.id : null;
        if (tableKey) safeStorageSet(tableKey, String(value));
        safeStorageSet(GLOBAL_STORAGE_KEY, String(value));
    }

    function enhance(table) {
        var tbody = table.tBodies[0];
        if (!tbody) return;

        var allRows = Array.prototype.slice.call(tbody.rows);
        // Data rows = real rows; skip placeholder rows that span columns.
        var rows = allRows.filter(function (r) {
            return !r.querySelector('td[colspan]');
        });
        if (rows.length === 0) return;

        var pageSize = resolveInitialPageSize(table);
        var state = { page: 1, sortTh: null, dir: 'asc' };

        // ---- Sorting -----------------------------------------------------
        var headerRow = table.tHead ? table.tHead.rows[table.tHead.rows.length - 1] : null;
        var carets = [];
        if (headerRow) {
            Array.prototype.forEach.call(headerRow.cells, function (th) {
                if (!th.hasAttribute('data-sort')) return;
                th.classList.add('tk-sortable');
                var caret = document.createElement('i');
                caret.className = 'tk-caret bi bi-arrow-down-up';
                caret.setAttribute('aria-hidden', 'true');
                th.appendChild(document.createTextNode(' '));
                th.appendChild(caret);
                carets.push(caret);
                // Accessibility: make sortable headers keyboard-operable and
                // announce sort state to screen readers.
                th.setAttribute('tabindex', '0');
                th.setAttribute('role', 'button');
                th.setAttribute('aria-sort', 'none');
                th.addEventListener('click', function () { sortBy(th, caret); });
                th.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar') {
                        e.preventDefault();
                        sortBy(th, caret);
                    }
                });
            });
        }

        function cellText(row, idx) {
            var c = row.cells[idx];
            return c ? c.textContent.trim() : '';
        }

        function sortBy(th, caret) {
            var idx = th.cellIndex;
            var numeric = th.getAttribute('data-sort') === 'num';
            state.dir = (state.sortTh === th && state.dir === 'asc') ? 'desc' : 'asc';
            state.sortTh = th;
            var mul = state.dir === 'asc' ? 1 : -1;

            rows.sort(function (a, b) {
                var av = cellText(a, idx), bv = cellText(b, idx);
                if (numeric) {
                    var an = parseFloat(av.replace(/[^0-9.\-]/g, ''));
                    var bn = parseFloat(bv.replace(/[^0-9.\-]/g, ''));
                    an = isNaN(an) ? -Infinity : an;
                    bn = isNaN(bn) ? -Infinity : bn;
                    return (an - bn) * mul;
                }
                return av.localeCompare(bv, undefined, { numeric: true, sensitivity: 'base' }) * mul;
            });

            carets.forEach(function (c) {
                c.className = 'tk-caret bi bi-arrow-down-up';
                if (c.parentElement) c.parentElement.setAttribute('aria-sort', 'none');
            });
            caret.className = 'tk-caret bi ' + (state.dir === 'asc' ? 'bi-caret-up-fill' : 'bi-caret-down-fill');
            th.setAttribute('aria-sort', state.dir === 'asc' ? 'ascending' : 'descending');

            rows.forEach(function (r) { tbody.appendChild(r); });
            state.page = 1;
            render();
        }

        // ---- Pager UI (always built; selector + info + page nav) ---------
        var pager = document.createElement('div');
        pager.className = 'tk-pager d-flex justify-content-between align-items-center flex-wrap gap-2 px-3 py-2 border-top';

        var sizeWrap = document.createElement('div');
        sizeWrap.className = 'd-flex align-items-center gap-2 small text-muted';
        var sizeLabel = document.createElement('label');
        sizeLabel.className = 'mb-0';
        sizeLabel.textContent = 'Show';
        var sizeSelect = document.createElement('select');
        sizeSelect.className = 'form-select form-select-sm tk-size-select';
        sizeSelect.style.width = 'auto';
        PAGE_SIZE_OPTIONS.forEach(function (n) {
            var opt = document.createElement('option');
            opt.value = String(n);
            opt.textContent = String(n);
            if (n === pageSize) opt.selected = true;
            sizeSelect.appendChild(opt);
        });
        // If the resolved pageSize isn't one of the standard options, inject it
        // so the select reflects the current state instead of silently snapping.
        if (PAGE_SIZE_OPTIONS.indexOf(pageSize) === -1) {
            var custom = document.createElement('option');
            custom.value = String(pageSize);
            custom.textContent = String(pageSize);
            custom.selected = true;
            sizeSelect.insertBefore(custom, sizeSelect.firstChild);
        }
        var sizeSuffix = document.createElement('span');
        sizeSuffix.textContent = 'per page';
        sizeWrap.appendChild(sizeLabel);
        sizeWrap.appendChild(sizeSelect);
        sizeWrap.appendChild(sizeSuffix);

        var info = document.createElement('span');
        info.className = 'text-muted small tk-info';

        var nav = document.createElement('nav');
        nav.setAttribute('aria-label', 'Table pages');
        var pagesUl = document.createElement('ul');
        pagesUl.className = 'pagination pagination-sm mb-0';
        nav.appendChild(pagesUl);

        pager.appendChild(sizeWrap);
        pager.appendChild(info);
        pager.appendChild(nav);

        var host = table.closest('.table-responsive') || table;
        host.parentNode.insertBefore(pager, host.nextSibling);

        sizeSelect.addEventListener('change', function () {
            var n = parseInt(sizeSelect.value, 10);
            if (isNaN(n) || n <= 0) return;
            pageSize = n;
            persistPageSize(table, n);
            state.page = 1;
            render();
        });

        function pageItem(label, page, opts) {
            opts = opts || {};
            var li = document.createElement('li');
            li.className = 'page-item' + (opts.active ? ' active' : '') + (opts.disabled ? ' disabled' : '');
            var a = document.createElement('a');
            a.className = 'page-link';
            a.href = '#';
            a.textContent = label;
            if (opts.disabled) a.setAttribute('aria-disabled', 'true');
            if (!opts.disabled && !opts.active) {
                a.addEventListener('click', function (e) { e.preventDefault(); state.page = page; render(); });
            } else {
                a.addEventListener('click', function (e) { e.preventDefault(); });
            }
            li.appendChild(a);
            return li;
        }

        function render() {
            var total = rows.length;
            var pageCount = Math.max(1, Math.ceil(total / pageSize));
            if (state.page > pageCount) state.page = pageCount;
            if (state.page < 1) state.page = 1;
            var start = (state.page - 1) * pageSize;
            var end = start + pageSize;

            rows.forEach(function (r, i) {
                r.style.display = (i >= start && i < end) ? '' : 'none';
            });

            info.textContent = 'Showing ' + (total ? start + 1 : 0) + '–' + Math.min(end, total) + ' of ' + total;

            pagesUl.replaceChildren();
            // Only render page navigation when there is more than one page.
            if (pageCount <= 1) return;
            pagesUl.appendChild(pageItem('«', state.page - 1, { disabled: state.page === 1 }));
            // Windowed page numbers: first, last, and a span around the current page.
            var win = 2;
            var from = Math.max(1, state.page - win);
            var to = Math.min(pageCount, state.page + win);
            if (from > 1) {
                pagesUl.appendChild(pageItem('1', 1, {}));
                if (from > 2) pagesUl.appendChild(pageItem('…', 0, { disabled: true }));
            }
            for (var p = from; p <= to; p++) {
                pagesUl.appendChild(pageItem(String(p), p, { active: p === state.page }));
            }
            if (to < pageCount) {
                if (to < pageCount - 1) pagesUl.appendChild(pageItem('…', 0, { disabled: true }));
                pagesUl.appendChild(pageItem(String(pageCount), pageCount, {}));
            }
            pagesUl.appendChild(pageItem('»', state.page + 1, { disabled: state.page === pageCount }));
        }

        render();
    }

    function init() {
        document.querySelectorAll('table[data-tablekit]').forEach(enhance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
