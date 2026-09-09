// ---------------------------------------------------------------------------
// file-preview.js — show a file in place instead of downloading it.
//
// Used by the QC report preview on the Quality Order page and by the document
// lists on both the arrival and quality-order pages. Extracted from the report
// modal so the three share one implementation rather than three copies that
// drift.
//
// The file is fetched into a BLOB rather than pointed at with an iframe src,
// for two reasons that matter in practice:
//   * an iframe gives no way to tell "still loading" from "loaded but blank",
//     so a slow render and a broken one look identical;
//   * a non-200 (403, or a server error) would paint the raw error page inside
//     the frame, which reads as the document itself being corrupt.
// With fetch we own the whole progress and error story, and the blob URL is
// revoked when the viewer closes so a large PDF is not held in memory.
//
// Usage:
//   FilePreview.open({ frame, status, url, label });
//   FilePreview.close();          // aborts, revokes, hides
// or declaratively, on any element:
//   data-preview-url="/Documents/Preview/12" data-preview-label="invoice.pdf"
//   inside a container carrying [data-preview-host].
// ---------------------------------------------------------------------------
(function () {
    'use strict';

    var state = { blobUrl: null, inFlight: null, timer: null, frame: null, status: null };

    function releaseBlob() {
        if (state.blobUrl) { URL.revokeObjectURL(state.blobUrl); state.blobUrl = null; }
    }

    function stopTimer() {
        if (state.timer) { clearInterval(state.timer); state.timer = null; }
    }

    function spinner(label) {
        return '' +
            '<div class="d-flex flex-column justify-content-center align-items-center h-100 text-center px-4 gap-2">' +
              '<div class="spinner-border text-info" role="status" aria-hidden="true"></div>' +
              '<div class="fw-semibold" data-preview-msg>Opening ' + escapeHtml(label || 'file') + '…</div>' +
              '<div class="progress w-50" style="height:.4rem;" role="progressbar" aria-label="Loading">' +
                '<div class="progress-bar progress-bar-striped progress-bar-animated bg-info" style="width:100%"></div>' +
              '</div>' +
              '<div class="text-muted small"><span data-preview-elapsed>0s</span></div>' +
            '</div>';
    }

    function failure(message) {
        return '<div class="d-flex align-items-center justify-content-center h-100 px-4">' +
               '<div class="alert alert-danger mb-0">' + escapeHtml(message) + '</div></div>';
    }

    function escapeHtml(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function close() {
        if (state.inFlight) { try { state.inFlight.abort(); } catch (e) { } state.inFlight = null; }
        stopTimer();
        releaseBlob();
        if (state.frame) {
            state.frame.removeAttribute('src');
            state.frame.classList.add('d-none');
        }
    }

    function open(opts) {
        var frame  = opts.frame;
        var status = opts.status;
        var url    = opts.url;
        var label  = opts.label;
        if (!frame || !status || !url) return;

        state.frame = frame;
        state.status = status;

        // A second open while the first is still loading would leave two
        // responses racing for the same frame.
        close();

        status.classList.remove('d-none');
        status.innerHTML = spinner(label);
        var msg     = status.querySelector('[data-preview-msg]');
        var elapsed = status.querySelector('[data-preview-elapsed]');

        state.inFlight = (typeof AbortController !== 'undefined') ? new AbortController() : null;
        var started = Date.now();
        state.timer = setInterval(function () {
            var s = Math.round((Date.now() - started) / 1000);
            if (elapsed) elapsed.textContent = s + 's';
            // Only once it is genuinely slow: saying it up front makes every
            // fast open look like a problem.
            if (s === 8 && msg) msg.textContent = 'Still working — a large file takes longer…';
        }, 1000);

        fetch(url, {
            credentials: 'same-origin',
            signal: state.inFlight ? state.inFlight.signal : undefined
        })
        .then(function (res) {
            if (!res.ok) {
                var m = res.status === 403
                    ? 'You do not have permission to open this file.'
                    : (res.status === 404
                        ? 'That file is no longer available.'
                        : 'Could not open the file (' + res.status + ').');
                throw new Error(m);
            }
            return res.blob();
        })
        .then(function (blob) {
            state.blobUrl = URL.createObjectURL(blob);
            frame.src = state.blobUrl;
            frame.classList.remove('d-none');
            status.classList.add('d-none');
        })
        .catch(function (e) {
            if (e && e.name === 'AbortError') return;      // superseded, not failed
            status.classList.remove('d-none');
            status.innerHTML = failure(e && e.message ? e.message : String(e));
        })
        .then(function () { stopTimer(); });
    }

    // ---- declarative wiring ------------------------------------------------
    // Any [data-preview-url] inside a [data-preview-host] opens into that
    // host's frame. Delegated, so AJAX-replaced rows keep working.
    document.addEventListener('click', function (ev) {
        var trigger = ev.target.closest ? ev.target.closest('[data-preview-url]') : null;
        if (!trigger) return;
        var host = trigger.closest('[data-preview-host]');
        if (!host) return;
        ev.preventDefault();

        var frame  = host.querySelector('[data-preview-frame]');
        var status = host.querySelector('[data-preview-status]');
        var title  = host.querySelector('[data-preview-title]');
        var label  = trigger.getAttribute('data-preview-label') || '';
        if (title) title.textContent = label;

        // Mark the active row so the reader can see which file is showing.
        Array.prototype.forEach.call(
            host.querySelectorAll('[data-preview-url].active'),
            function (el) { el.classList.remove('active'); });
        trigger.classList.add('active');

        host.classList.add('preview-open');
        open({ frame: frame, status: status, url: trigger.getAttribute('data-preview-url'), label: label });
    });

    // Closing the modal that contains a viewer must release the blob.
    document.addEventListener('hidden.bs.modal', function (ev) {
        if (ev.target && ev.target.querySelector && ev.target.querySelector('[data-preview-frame]')) close();
    });

    window.FilePreview = { open: open, close: close };
})();
