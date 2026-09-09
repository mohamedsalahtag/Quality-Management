// V38 (2026-07-08). AppDialogs — styled replacements for the native browser
// alert() / confirm() popups ("192.168.3.15:5244 says ..."), which look raw
// and cannot be branded.
//
// Two ways to use it:
//   1. Declarative (most forms/links): put data-confirm="message" on a <form>
//      or <a>. The interceptors below show a Bootstrap modal and only proceed
//      when the user accepts. Optional attributes:
//          data-confirm-title="..."    (default "Please confirm")
//          data-confirm-ok="..."       (default "OK")
//          data-confirm-danger         (red OK button + warning icon)
//   2. Programmatic (inline scripts):
//          appDialogs.confirm("msg", {danger:true}).then(ok => { ... })
//          appDialogs.alert("msg", {kind:"error"|"info"|"success"})
//
// Requires bootstrap.bundle (Modal). Loaded from _Layout on every page.
(function () {
    "use strict";
    if (typeof bootstrap === "undefined" || !bootstrap.Modal) return;

    var modalEl = null, titleEl, msgEl, iconEl, okBtn, cancelBtn, resolver = null, okClicked = false;

    function ensureModal() {
        if (modalEl) return;
        modalEl = document.createElement("div");
        modalEl.className = "modal fade";
        modalEl.id = "appDialogModal";
        modalEl.setAttribute("tabindex", "-1");
        modalEl.innerHTML =
            '<div class="modal-dialog modal-dialog-centered">' +
            '  <div class="modal-content shadow">' +
            '    <div class="modal-header py-2">' +
            '      <h5 class="modal-title d-flex align-items-center gap-2 fs-6">' +
            '        <i id="appDialogIcon" class="bi bi-question-circle text-primary fs-4"></i>' +
            '        <span id="appDialogTitle">Please confirm</span>' +
            '      </h5>' +
            '      <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>' +
            '    </div>' +
            '    <div class="modal-body" id="appDialogMsg" style="white-space: pre-line;"></div>' +
            '    <div class="modal-footer py-2">' +
            '      <button type="button" class="btn btn-outline-secondary btn-sm" data-bs-dismiss="modal" id="appDialogCancel">Cancel</button>' +
            '      <button type="button" class="btn btn-primary btn-sm" id="appDialogOk">OK</button>' +
            '    </div>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(modalEl);
        titleEl   = modalEl.querySelector("#appDialogTitle");
        msgEl     = modalEl.querySelector("#appDialogMsg");
        iconEl    = modalEl.querySelector("#appDialogIcon");
        okBtn     = modalEl.querySelector("#appDialogOk");
        cancelBtn = modalEl.querySelector("#appDialogCancel");
        okBtn.addEventListener("click", function () {
            okClicked = true;
            bootstrap.Modal.getOrCreateInstance(modalEl).hide();
        });
        // A single hidden handler resolves every path: OK, Cancel, ESC, backdrop.
        modalEl.addEventListener("hidden.bs.modal", function () {
            if (resolver) { var r = resolver; resolver = null; r(okClicked); }
            // Reused-modal hygiene: if no modal remains open, clear any stray
            // backdrop / body state Bootstrap left behind. Without this, a
            // cancelled dialog can leave an invisible dimming overlay that
            // swallows the next click on the page.
            setTimeout(function () {
                if (!document.querySelector(".modal.show")) {
                    document.querySelectorAll(".modal-backdrop").forEach(function (b) { b.remove(); });
                    document.body.classList.remove("modal-open");
                    document.body.style.removeProperty("overflow");
                    document.body.style.removeProperty("padding-right");
                }
            }, 0);
        });
    }

    var ICONS = {
        question: "bi bi-question-circle text-primary fs-4",
        danger:   "bi bi-exclamation-triangle-fill text-danger fs-4",
        warning:  "bi bi-exclamation-triangle text-warning fs-4",
        error:    "bi bi-x-circle-fill text-danger fs-4",
        info:     "bi bi-info-circle text-primary fs-4",
        success:  "bi bi-check-circle-fill text-success fs-4"
    };

    function show(message, opts) {
        ensureModal();
        opts = opts || {};
        // If a dialog is somehow still open, resolve it as cancelled first.
        if (resolver) { var prev = resolver; resolver = null; prev(false); }
        okClicked = false;
        titleEl.textContent = opts.title || (opts.alertOnly ? "Notice" : "Please confirm");
        msgEl.textContent   = message == null ? "" : String(message);
        iconEl.className    = ICONS[opts.icon] ||
            (opts.alertOnly ? ICONS[opts.kind || "info"] : (opts.danger ? ICONS.danger : ICONS.question));
        okBtn.textContent = opts.okText || "OK";
        okBtn.className   = "btn btn-sm " + (opts.danger ? "btn-danger" : "btn-primary");
        cancelBtn.classList.toggle("d-none", !!opts.alertOnly);
        return new Promise(function (resolve) {
            resolver = resolve;
            bootstrap.Modal.getOrCreateInstance(modalEl).show();
        });
    }

    window.appDialogs = {
        confirm: function (message, opts) { return show(message, opts || {}); },
        alert:   function (message, opts) {
            opts = Object.assign({}, opts || {}, { alertOnly: true });
            return show(message, opts);
        }
    };

    // -- declarative interceptors ------------------------------------------
    // Forms: <form data-confirm="..."> — capture phase so we run before any
    // other submit handler. On acceptance we re-dispatch a REAL submit via
    // requestSubmit() with a one-shot pass-through flag, so downstream
    // document-level submit handlers (e.g. the AJAX image gallery) still see
    // the event; plain forms then continue with the browser's own submission.
    document.addEventListener("submit", function (ev) {
        var f = ev.target;
        if (!f || !f.getAttribute) return;
        var msg = f.getAttribute("data-confirm");
        if (!msg) return;
        if (f.dataset.appDialogConfirmed === "1") {
            delete f.dataset.appDialogConfirmed;      // confirmed — let it through
            return;
        }
        ev.preventDefault();
        ev.stopImmediatePropagation();
        window.appDialogs.confirm(msg, {
            title:  f.getAttribute("data-confirm-title") || undefined,
            okText: f.getAttribute("data-confirm-ok") || undefined,
            danger: f.hasAttribute("data-confirm-danger")
        }).then(function (ok) {
            if (!ok) return;
            f.dataset.appDialogConfirmed = "1";
            if (f.requestSubmit) f.requestSubmit();
            else f.submit();
        });
    }, true);

    // Links: <a data-confirm="..."> — navigate only after acceptance.
    document.addEventListener("click", function (ev) {
        var a = ev.target && ev.target.closest && ev.target.closest("a[data-confirm]");
        if (!a) return;
        ev.preventDefault();
        ev.stopImmediatePropagation();
        window.appDialogs.confirm(a.getAttribute("data-confirm"), {
            title:  a.getAttribute("data-confirm-title") || undefined,
            okText: a.getAttribute("data-confirm-ok") || undefined,
            danger: a.hasAttribute("data-confirm-danger")
        }).then(function (ok) { if (ok && a.href) window.location.href = a.href; });
    }, true);
})();
