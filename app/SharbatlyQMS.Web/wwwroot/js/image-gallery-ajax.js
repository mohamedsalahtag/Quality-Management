// V31 (2026-06-20). AJAX wiring for image galleries flagged data-ajax="true".
// Loaded once on every full page load via _Layout.cshtml; all listeners are
// document-level so they catch events from partials injected later via
// drawerBody.innerHTML = ... (those <script> tags do not execute).
//
// Three input surfaces share the same submitFiles -> POST -> grid-refresh path:
//   - .photo-dropzone (drag/drop, paste, click-to-pick)
//   - .ig-extra-file  (Browse files button, Camera button)
//   - <form class="ig-upload-form"> (legacy synchronous upload form)
// Delete forms inside the grid also go through AJAX.
//
// XSS note: grid.innerHTML below is fed by `/Images/Upload` which renders the
// _ImageGalleryGrid Razor partial server-side. Razor HTML-encodes user data
// (filenames, captions, uploader names) by default, and the endpoint requires
// OperatorOrAbove auth + antiforgery -- the response is therefore trusted
// first-party markup, not raw user input. If a future refactor switches that
// endpoint to return JSON or stitches user data without encoding, replace
// these assignments with a sanitised template build.
(function () {
    function ajaxGalleryOf(el) {
        var g = el.closest && el.closest('.image-gallery');
        return (g && g.getAttribute('data-ajax') === 'true') ? g : null;
    }

    // V31 (2026-06-20): after any AJAX upload or delete, sync the photo-count
    // badge on the matching sample row. Currently only Sample owner_type has a
    // visible counter on the QO Details table; other owner_types are no-ops.
    function syncSampleCounterFromGallery(gallery) {
        if (!gallery) return;
        if ((gallery.getAttribute('data-owner-type') || '') !== 'Sample') return;
        var ownerId = gallery.getAttribute('data-owner-id'); if (!ownerId) return;
        var count = gallery.querySelectorAll('.ig-grid .ig-card').length;
        document.querySelectorAll('.sample-photos[data-sample-id="' + ownerId + '"]').forEach(function (btn) {
            var badge = btn.querySelector('.badge');
            if (count > 0) {
                if (!badge) {
                    badge = document.createElement('span');
                    badge.className = 'badge bg-primary ms-1';
                    btn.appendChild(badge);
                }
                badge.textContent = count;
            } else if (badge) {
                badge.remove();
            }
        });
    }

    function submitFiles(form, files) {
        if (!form || !files || !files.length) return;
        var gallery = ajaxGalleryOf(form); if (!gallery) return;
        var grid = gallery.querySelector('.ig-grid');
        var fd = new FormData(form);
        // Replace the form's file field with the dropped/pasted/picked files
        // so the server-side IFormFile binder picks them up under the same
        // key name ("files") used by the picker.
        fd.delete('files');
        for (var i = 0; i < files.length; i++) fd.append('files', files[i]);
        fetch(form.action, {
            method: 'POST',
            headers: { 'X-Requested-With': 'XMLHttpRequest' },
            body: fd
        })
        .then(function (r) { if (!r.ok) throw new Error('Request failed'); return r.text(); })
        .then(function (html) {
            if (grid) grid.innerHTML = html;
            syncSampleCounterFromGallery(gallery);
        })
        .catch(function () { alert('Upload failed. Please try again.'); });
    }

    document.addEventListener('submit', function (e) {
        if (e.defaultPrevented) return;
        var form = e.target;
        var gallery = ajaxGalleryOf(form);
        if (!gallery) return;
        var grid = gallery.querySelector('.ig-grid');

        var isUpload = form.classList.contains('ig-upload-form');
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
            syncSampleCounterFromGallery(gallery);
        })
        .catch(function () {
            alert(isUpload ? 'Upload failed. Please try again.' : 'Could not remove the image.');
        })
        .finally(function () { if (btn) btn.disabled = false; });
    });

    document.addEventListener('click', function (e) {
        var zone = e.target.closest && e.target.closest('.photo-dropzone');
        if (!zone) return;
        var fi = zone.querySelector('input[type="file"]');
        if (fi) fi.click();
    });

    document.addEventListener('change', function (e) {
        // Dropzone input + .ig-extra-file (Browse / Camera). All share the
        // submitFiles AJAX upload path.
        if (e.target.matches && (e.target.matches('.photo-dropzone input[type="file"]')
            || e.target.matches('.ig-extra-file'))) {
            var form = e.target.closest('form');
            submitFiles(form, e.target.files);
            e.target.value = '';
        }
    });

    ['dragover', 'dragenter'].forEach(function (ev) {
        document.addEventListener(ev, function (e) {
            var zone = e.target.closest && e.target.closest('.photo-dropzone');
            if (!zone) return;
            e.preventDefault();
            zone.classList.add('dragover');
        });
    });
    ['dragleave', 'dragend', 'drop'].forEach(function (ev) {
        document.addEventListener(ev, function (e) {
            var zone = e.target.closest && e.target.closest('.photo-dropzone');
            if (!zone) return;
            zone.classList.remove('dragover');
            if (ev === 'drop') {
                e.preventDefault();
                var form = zone.closest('form');
                submitFiles(form, e.dataTransfer.files);
            }
        });
    });

    document.addEventListener('paste', function (e) {
        // Only act when an AJAX-mode gallery + dropzone is visible on the page.
        var zone = document.querySelector('.image-gallery[data-ajax="true"] .photo-dropzone');
        if (!zone || !e.clipboardData) return;
        var pasted = [];
        for (var i = 0; i < e.clipboardData.items.length; i++) {
            var it = e.clipboardData.items[i];
            if (it.kind === 'file') {
                var f = it.getAsFile();
                if (f) pasted.push(f);
            }
        }
        if (!pasted.length) return;
        e.preventDefault();
        var form = zone.closest('form');
        submitFiles(form, pasted);
    });
})();
