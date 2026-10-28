// "Send report to supplier" dialog (Views/QualityOrders/_SendReportModal.cshtml).
// One dialog serves the Claims panel, the Quality Order Details page and every
// row of the Quality Orders list: the button that opens it says which order to
// send and which endpoints to call --
//   data-qo-id, data-url-prepare, data-url-send, data-url-save
// so the Claims page keeps its own permission and the Quality Orders buttons
// theirs. After a successful send the opening button turns green (see markSent).
(function () {
    "use strict";
    const modal = document.getElementById("sendReportModal");
    if (!modal) return;
    const $ = id => document.getElementById(id);
    const TOKEN = (document.querySelector("#srm-token input[name=__RequestVerificationToken]") || {}).value || "";
    let opener = null, vendorNo = "";

    // ---- small helpers: this script also runs on pages without their own ----
    function toast(message, kind) {
        let host = $("qoToastHost") || $("srmToastHost");
        if (!host) {
            host = document.createElement("div");
            host.id = "srmToastHost";
            host.className = "position-fixed bottom-0 end-0 p-3";
            host.style.zIndex = "1080";
            document.body.appendChild(host);
        }
        const div = document.createElement("div");
        div.className = "toast align-items-center text-bg-" + (kind || "success") + " border-0 show mb-2";
        div.setAttribute("role", "status");
        div.innerHTML = '<div class="d-flex"><div class="toast-body"></div><button class="btn-close btn-close-white me-2 m-auto" type="button" aria-label="Close"></button></div>';
        div.querySelector(".toast-body").textContent = message;
        host.appendChild(div);
        const dismiss = () => { div.classList.remove("show"); setTimeout(() => div.remove(), 300); };
        div.querySelector(".btn-close").addEventListener("click", dismiss);
        setTimeout(dismiss, kind === "danger" ? 8000 : 3000);
    }
    async function post(url, fd) {
        if (!fd.has("__RequestVerificationToken")) fd.append("__RequestVerificationToken", TOKEN);
        // X-CSRF-TOKEN: the antiforgery header name set in Program.cs (AddAntiforgery).
        const res = await fetch(url, { method: "POST", headers: { "X-CSRF-TOKEN": TOKEN, Accept: "application/json" }, credentials: "same-origin", body: fd });
        if (!res.ok) {
            if (res.status === 403) throw new Error("you do not have permission to send reports");
            throw new Error("HTTP " + res.status);
        }
        return res.json();
    }

    // ---- the button turns green once the report has gone ------------------
    function markSent(btn, info) {
        if (!btn) return;
        const title = `Sent to ${info.to || "the supplier"} by ${info.sentBy || "?"} on ${info.sentAt || ""}. Click to send again.`;
        btn.dataset.sent = "true";
        btn.title = title;
        btn.setAttribute("aria-label", title);
        if (btn.classList.contains("btn-outline-danger")) { btn.classList.remove("btn-outline-danger"); btn.classList.add("btn-outline-success"); }
        if (btn.classList.contains("btn-info")) { btn.classList.remove("btn-info"); btn.classList.add("btn-success"); }
        const icon = btn.querySelector("i.bi");
        if (icon) { icon.classList.remove("bi-envelope-fill", "bi-envelope-paper"); icon.classList.add("bi-envelope-check-fill"); }
    }

    // ---- documents that can go with the report ----------------------------
    function renderDocs(docs) {
        const wrap = $("srm-docs-wrap"), box = $("srm-docs");
        if (!wrap || !box) return;
        box.innerHTML = "";
        if (!docs.length) { wrap.classList.add("d-none"); return; }
        wrap.classList.remove("d-none");
        const bySource = {};
        docs.forEach(d => { (bySource[d.source] = bySource[d.source] || []).push(d); });
        Object.keys(bySource).forEach(src => {
            const h = document.createElement("div");
            h.className = "text-muted small fw-semibold mt-1 mb-1";
            h.textContent = src;
            box.appendChild(h);
            bySource[src].forEach(d => {
                const row = document.createElement("div");
                row.className = "form-check";
                const id = "srm-doc-" + d.id;
                row.innerHTML = '<input class="form-check-input" type="checkbox" value="' + d.id + '" id="' + id + '"><label class="form-check-label small" for="' + id + '"></label>';
                const lbl = row.querySelector("label");
                lbl.textContent = d.name;
                const meta = document.createElement("span");
                meta.className = "text-muted";
                meta.textContent = "  " + (d.category || "") + " · " + d.sizeKb + " KB";
                lbl.appendChild(meta);
                row.querySelector("input").addEventListener("change", total);
                box.appendChild(row);
            });
        });
        total();
        // Mail servers reject oversized messages; show the weight building up.
        function total() {
            let kb = 0;
            document.querySelectorAll("#srm-docs input[type=checkbox]:checked").forEach(c => {
                const d = docs.find(x => String(x.id) === c.value);
                if (d) kb += d.sizeKb;
            });
            $("srm-docs-total").textContent = kb === 0 ? "" : (kb / 1024).toFixed(1) + " MB selected, plus the report";
        }
    }

    // ---- open: fill the dialog for the order behind the clicked button ----
    modal.addEventListener("show.bs.modal", async ev => {
        opener = ev.relatedTarget || null;
        const qoId = opener && opener.dataset.qoId;
        $("srm-load").classList.remove("d-none");
        $("srm-form").classList.add("d-none");
        if (!qoId || !opener.dataset.urlPrepare) { toast("Could not tell which order to send.", "danger"); return; }
        try {
            const res = await fetch(opener.dataset.urlPrepare + "?id=" + encodeURIComponent(qoId), { headers: { Accept: "application/json" } });
            if (!res.ok) throw new Error(res.status === 403 ? "you do not have permission to send reports" : "HTTP " + res.status);
            const data = await res.json();
            if (!data.ok) throw new Error(data.error || "the order cannot be sent");
            $("srm-to").value = data.to || "";
            $("srm-cc").value = "";
            $("srm-subject").value = data.subject || "";
            $("srm-body").value = data.body || "";
            $("srm-filename").textContent = data.fileName || "report.pdf";
            vendorNo = data.vendorNo || "";

            const always = data.alwaysCc || [];
            $("srm-alwayscc").textContent = always.join(", ");
            $("srm-alwayscc-wrap").classList.toggle("d-none", always.length === 0);

            const last = data.lastSent;
            $("srm-last").classList.toggle("d-none", !last);
            if (last) $("srm-last-text").textContent = `Already sent to ${last.to || "the supplier"} by ${last.by || "?"} on ${last.at}. Sending again sends a new copy.`;

            const name = data.supplierName || "this supplier";
            $("srm-supplier-a").textContent = name;
            $("srm-supplier-b").textContent = name;
            const banners = { none: $("srm-no-email"), qms: $("srm-email-qms"), sap: $("srm-email-sap") };
            Object.values(banners).forEach(b => b.classList.add("d-none"));
            const which = !data.emailKnown ? "none" : (data.emailSource === "sap" ? "sap" : "qms");
            banners[which].classList.remove("d-none");

            // Nothing to store an address against without a vendor number, and
            // an address coming FROM SAP should not be shadowed by a QMS copy.
            const remember = $("srm-remember");
            const canRemember = !!vendorNo && which !== "sap" && !!opener.dataset.urlSave;
            remember.checked = canRemember;
            remember.disabled = !canRemember;
            renderDocs(data.documents || []);

            $("srm-load").classList.add("d-none");
            $("srm-form").classList.remove("d-none");
        } catch (e) {
            toast("Could not prepare the e-mail: " + e.message, "danger");
            (window.bootstrap && bootstrap.Modal.getInstance(modal))?.hide();
        }
    });

    // ---- send ---------------------------------------------------------------
    $("srm-send").addEventListener("click", async () => {
        if (!opener) return;
        const fd = new FormData();
        fd.append("id", opener.dataset.qoId);
        fd.append("to", $("srm-to").value);
        fd.append("cc", $("srm-cc").value);
        fd.append("subject", $("srm-subject").value);
        fd.append("body", $("srm-body").value);
        document.querySelectorAll("#srm-docs input[type=checkbox]:checked").forEach(c => fd.append("documentIds", c.value));
        if (!fd.get("to") || !fd.get("subject") || !fd.get("body")) { toast("Recipient, subject and body are required.", "warning"); return; }
        const btn = $("srm-send");
        btn.disabled = true;
        btn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Sending…';
        try {
            const data = await post(opener.dataset.urlSend, fd);
            if (!data.ok) { toast("Send failed: " + (data.error || "unknown error"), "danger"); return; }
            // Only after a successful send -- remembering an address that just
            // bounced would bake the mistake in for everyone.
            const remember = $("srm-remember");
            if (remember.checked && !remember.disabled && vendorNo && opener.dataset.urlSave) {
                const vf = new FormData();
                vf.append("vendorNo", vendorNo);
                vf.append("email", $("srm-to").value);
                try {
                    const sv = await post(opener.dataset.urlSave, vf);
                    if (!sv.ok) toast("Report sent, but the address was not saved: " + (sv.error || "unknown error"), "warning");
                } catch (e) { toast("Report sent, but the address was not saved: " + e.message, "warning"); }
            }
            markSent(opener, data);
            (window.bootstrap && bootstrap.Modal.getInstance(modal))?.hide();
            toast("Report sent.");
        } catch (e) {
            toast("Send failed: " + e.message, "danger");
        } finally {
            btn.disabled = false;
            btn.innerHTML = '<i class="bi bi-send me-1"></i>Send';
        }
    });
})();
