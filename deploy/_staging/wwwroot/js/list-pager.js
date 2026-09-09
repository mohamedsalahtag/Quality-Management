// "Rows per page" selector for server-side paged lists.
//
// Used by /Arrivals and /Arrivals/Pending. Page navigation itself is plain <a>
// links that fully reload: a page is 25/50/100 rows rather than the whole
// table, so a reload is cheap, and it lets tablekit sorting and the Columns
// menu re-initialise cleanly on each load (neither has a re-attach hook).
//
// This only wires the selector: changing it reloads at page 1 with the new
// size, preserving every other query-string filter.
//
// Replaces the page-specific pending-pager.js; the markup contract is a
// <select data-pagesize-select> anywhere on the page.
(function () {
    function init() {
        document.querySelectorAll('[data-pagesize-select]').forEach(function (sel) {
            sel.addEventListener('change', function () {
                var url = new URL(window.location.href);
                url.searchParams.set('pageSize', sel.value);
                // Back to page 1: the row you were looking at on page 7 of 50
                // is not on page 7 of 100, so holding the number would land
                // somewhere arbitrary.
                url.searchParams.set('page', '1');
                window.location.assign(url.toString());
            });
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
