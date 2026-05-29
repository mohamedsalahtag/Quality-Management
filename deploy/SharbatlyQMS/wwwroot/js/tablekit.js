// tablekit: progressive client-side sorting + pagination for list tables.
//
// Opt in by adding `data-tablekit` to a <table>. Optional:
//   data-page-size="15"          rows per page (0 / absent with few rows = no pager)
//   <th data-sort>               sortable column (text compare)
//   <th data-sort="num">         numeric compare
//
// Empty-state rows (a <tr> whose cell uses colspan) are ignored, so the
// "No records yet" placeholder is never sorted or paginated. No server,
// controller, or query changes — purely a rendering enhancement.
(function () {
    function enhance(table) {
        var tbody = table.tBodies[0];
        if (!tbody) return;

        var allRows = Array.prototype.slice.call(tbody.rows);
        // Data rows = real rows; skip placeholder rows that span columns.
        var rows = allRows.filter(function (r) {
            return !r.querySelector('td[colspan]');
        });
        if (rows.length === 0) return;

        var pageSize = parseInt(table.getAttribute('data-page-size'), 10);
        if (isNaN(pageSize) || pageSize <= 0) pageSize = 0;
        var paginate = pageSize > 0 && rows.length > pageSize;

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
                th.appendChild(document.createTextNode(' '));
                th.appendChild(caret);
                carets.push(caret);
                th.addEventListener('click', function () { sortBy(th, caret); });
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

            carets.forEach(function (c) { c.className = 'tk-caret bi bi-arrow-down-up'; });
            caret.className = 'tk-caret bi ' + (state.dir === 'asc' ? 'bi-caret-up-fill' : 'bi-caret-down-fill');

            rows.forEach(function (r) { tbody.appendChild(r); });
            state.page = 1;
            render();
        }

        // ---- Pagination --------------------------------------------------
        var pager = null, info = null, pagesUl = null;
        if (paginate) {
            pager = document.createElement('div');
            pager.className = 'tk-pager d-flex justify-content-between align-items-center flex-wrap gap-2 px-3 py-2 border-top';
            info = document.createElement('span');
            info.className = 'text-muted small tk-info';
            var nav = document.createElement('nav');
            nav.setAttribute('aria-label', 'Table pages');
            pagesUl = document.createElement('ul');
            pagesUl.className = 'pagination pagination-sm mb-0';
            nav.appendChild(pagesUl);
            pager.appendChild(info);
            pager.appendChild(nav);
            var host = table.closest('.table-responsive') || table;
            host.parentNode.insertBefore(pager, host.nextSibling);
        }

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
            if (!paginate) return;
            var pageCount = Math.ceil(total / pageSize);
            if (state.page > pageCount) state.page = pageCount;
            if (state.page < 1) state.page = 1;
            var start = (state.page - 1) * pageSize;
            var end = start + pageSize;

            rows.forEach(function (r, i) {
                r.style.display = (i >= start && i < end) ? '' : 'none';
            });

            info.textContent = 'Showing ' + (total ? start + 1 : 0) + '–' + Math.min(end, total) + ' of ' + total;

            pagesUl.replaceChildren();
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
