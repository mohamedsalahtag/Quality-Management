// V39 (2026-07-15). AJAX wiring for document lists flagged data-ajax="true".
// Loaded once on every full page load via _Layout.cshtml; listeners are
// document-level so they also catch events from partials rendered into the
// grid after the initial load.
//
// Deliberately slimmer than image-gallery-ajax.js: documents have no drop-zone,
// no clipboard paste and no camera capture -- a picker is the only input
// surface. Upload and delete forms both refresh the grid in place so the
// Documents tab stays active instead of resetting to Checklist on reload.
//
// XSS note: grid.innerHTML below is fed by `/Documents/Upload` and
// `/Documents/Delete`, which render the _DocumentListGrid Razor partial
// server-side. Razor HTML-encodes user data (filenames, uploader names) by
// default, and both endpoints require OperatorOrAbove auth + antiforgery -- the
// response is trusted first-party markup, not raw user input. If a future
// refactor returns JSON or stitches user data without encoding, replace these
// assignments with a sanitised template build.
(function () {
    // Styled popup when dialogs.js is loaded (V38); native alert as fallback.
    function uiAlert(msg) {
        if (window.appDialogs) window.appDialogs.alert(msg, { kind: 'error' });
        else alert(msg);
    }

    function ajaxListOf(el) {
        var l = el.closest && el.closest('.document-list');
        return (l && l.getAttribute('data-ajax') === 'true') ? l : null;
    }

    document.addEventListener('submit', function (e) {
        if (e.defaultPrevented) return;
        var form = e.target;
        var list = ajaxListOf(form);
        if (!list) return;
        var grid = list.querySelector('.dl-grid');

        var isUpload = form.classList.contains('dl-upload-form');
        var isDelete = !isUpload && grid && grid.contains(form);
        if (!isUpload && !isDelete) return;

        e.preventDefault();
        var btn = form.querySelector('button[type="submit"]');
        if (btn) btn.disabled = true;

        fetch(form.action, {
            method: 'POST',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            body: new FormData(form)
        })
        .then(function (r) { if (!r.ok) throw new Error('Request failed'); return r.text(); })
        .then(function (html) {
            if (grid) grid.innerHTML = html;
            if (isUpload) {
                var file = form.querySelector('input[type="file"]');
                if (file) file.value = '';
            }
        })
        .catch(function () {
            uiAlert(isUpload ? 'Upload failed. Please try again.' : 'Could not remove the document.');
        })
        .finally(function () { if (btn) btn.disabled = false; });
    });
})();
