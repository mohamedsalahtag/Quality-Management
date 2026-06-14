// Global form-submit guard: after a form starts submitting, disable any
// submit button inside it so a double-click on a slow network cannot
// produce duplicate POSTs. Honors a `data-no-double-guard` opt-out for the
// rare form that intercepts submit and wants to keep the button
// interactive (e.g. inline AJAX forms that already manage their own state).
(function () {
    document.addEventListener('submit', function (ev) {
        var form = ev.target;
        if (!form || form.tagName !== 'FORM') return;
        if (form.hasAttribute('data-no-double-guard')) return;
        var btns = form.querySelectorAll('button[type="submit"], input[type="submit"]');
        // setTimeout 0 -- defer until after the browser collects the form
        // payload, otherwise the disabled button's name=value is dropped.
        setTimeout(function () {
            btns.forEach(function (b) {
                if (b.disabled) return;
                b.dataset._origHtml = b.innerHTML;
                b.disabled = true;
                if (b.tagName === 'BUTTON' && !b.querySelector('.spinner-border')) {
                    // Use safe DOM APIs (createElement + prepend) instead
                    // of innerHTML += ... so untrusted markup can never end
                    // up in the assignment, even if a future change moves
                    // user-controlled data nearby. XSS-safe by construction.
                    var spinner = document.createElement('span');
                    spinner.className = 'spinner-border spinner-border-sm me-1';
                    b.prepend(spinner);
                }
            });
        }, 0);
    }, true);
})();

// Global Bootstrap tooltip activation. Previously each view that needed
// tooltips repeated the same init block; centralising it here means any
// element with [data-bs-toggle="tooltip"] gets a tooltip without per-view
// glue code. Honors the [data-bs-html="true"] / placement / title
// attributes Bootstrap already understands. Idempotent: an element already
// promoted to a tooltip is skipped via the `_bsInitTip` marker.
(function () {
    function init(root) {
        if (!window.bootstrap || !window.bootstrap.Tooltip) return;
        var scope = root || document;
        var nodes = scope.querySelectorAll
            ? scope.querySelectorAll('[data-bs-toggle="tooltip"]')
            : [];
        nodes.forEach(function (el) {
            if (el._bsInitTip) return;
            try {
                new bootstrap.Tooltip(el, { container: 'body' });
                el._bsInitTip = true;
            } catch (e) { /* ignore individual tooltip errors */ }
        });
    }
    document.addEventListener('DOMContentLoaded', function () { init(document); });
    // Re-scan when new content is injected (AJAX partials, sample drawer,
    // tablekit re-render). MutationObserver is cheap on a 1-shot batch.
    if (window.MutationObserver) {
        var mo = new MutationObserver(function (muts) {
            muts.forEach(function (m) {
                m.addedNodes && m.addedNodes.forEach(function (n) {
                    if (n.nodeType === 1) init(n);
                });
            });
        });
        document.addEventListener('DOMContentLoaded', function () {
            mo.observe(document.body, { childList: true, subtree: true });
        });
    }
    // Expose for explicit callers (e.g. after a manual DOM swap).
    window.QmsInitTooltips = init;
})();

// L6 a11y: icon-only buttons / links need an `aria-label` for screen readers.
// Most already carry a `title` (tooltip text) -- promote it to `aria-label` so
// AT users get the same label without the visual tooltip. Heuristic:
// element with no visible text content but at least one <i class="bi-*"> child.
// Idempotent: skip if `aria-label` already set or empty title.
(function () {
    function promote(scope) {
        var nodes = (scope || document).querySelectorAll('button, a');
        nodes.forEach(function (n) {
            if (n.getAttribute('aria-label')) return;
            var title = n.getAttribute('title');
            if (!title) return;
            // Visible text content (ignoring whitespace + icons).
            var text = (n.textContent || '').trim();
            if (text.length > 0) return;
            // Confirm there's at least one icon child so we don't tag
            // genuinely empty buttons by mistake.
            if (!n.querySelector('i.bi, .bi, [class*=" bi-"], [class^="bi-"]')) return;
            n.setAttribute('aria-label', title);
            // Mark icon decorative so a11y trees don't read "icon" + label.
            n.querySelectorAll('i.bi, .bi').forEach(function (i) {
                if (!i.hasAttribute('aria-hidden')) i.setAttribute('aria-hidden', 'true');
            });
        });
    }
    document.addEventListener('DOMContentLoaded', function () { promote(document); });
    // Re-run after dynamic insertions (sample drawer, ajax row reload).
    if (window.MutationObserver) {
        var mo = new MutationObserver(function (muts) {
            muts.forEach(function (m) {
                m.addedNodes && m.addedNodes.forEach(function (n) {
                    if (n.nodeType === 1) promote(n);
                });
            });
        });
        document.addEventListener('DOMContentLoaded', function () {
            mo.observe(document.body, { childList: true, subtree: true });
        });
    }
})();
