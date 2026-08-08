// Pending Containers pager.
//
// The table is paginated server-side (the controller ships one page of rows),
// so page navigation uses plain <a> links that fully reload — fast now that a
// page is 50/100 rows, and it lets tablekit sorting and the Columns menu
// re-initialise cleanly on each load (neither has a re-attach hook).
//
// This script only wires the "per page" selector: changing it reloads at
// page 1 with the new size, preserving every other query-string filter.
(function () {
    function init() {
        var sel = document.querySelector('[data-pending-pagesize]');
        if (!sel) return;
        sel.addEventListener('change', function () {
            var url = new URL(window.location.href);
            url.searchParams.set('pageSize', sel.value);
            url.searchParams.set('page', '1');
            window.location.assign(url.toString());
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
