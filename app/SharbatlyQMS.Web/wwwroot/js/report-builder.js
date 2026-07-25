// Report Builder client (2026-07-25).
// - Palette (statics/defects/readings/headers) loaded per material group from
//   /Reports/ReportBuilderPalette.
// - Drag or click a palette field into the layout; each layout column carries a
//   header/column toggle, totals selector, defect count/% and (for calc) a
//   validated formula.
// - Save/load/delete reuse the generic Perspectives CRUD with report=report_builder.
// - Browse by supplier (cascades to containers) / BOL / PO / PO date range,
//   preview the first rows, and export a professional xlsx.
(function () {
    "use strict";

    var root = document.getElementById("reportBuilder");
    if (!root) return;

    var REPORT   = root.getAttribute("data-report") || "report_builder";
    var CAN_SHARE = root.getAttribute("data-can-share") === "true";

    // ---- state -------------------------------------------------------------
    var state = {
        materialGroup: "",
        columns: [],          // {key,kind,label,placement,defectValue,total,formula}
        loadedId: null        // saved perspective id when editing
    };
    var palette = { statics: [], defects: [], readings: [], sampleHeaders: [], materialHeaders: [] };
    var calcSeq = 0, blankSeq = 0;

    // ---- helpers -----------------------------------------------------------
    function $(id) { return document.getElementById(id); }
    function token() {
        var t = document.querySelector('#rbAfForm input[name="__RequestVerificationToken"]');
        return t ? t.value : "";
    }
    function postJson(url, body) {
        return fetch(url, {
            method: "POST",
            headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token() },
            body: JSON.stringify(body)
        });
    }
    function status(msg, isErr) {
        var el = $("rbStatus");
        el.textContent = msg || "";
        el.className = "small ms-2 " + (isErr ? "text-danger" : "text-muted");
    }
    var NUMERIC_KINDS = { defect: true, calc: true };
    function isNumeric(col) {
        if (NUMERIC_KINDS[col.kind]) return true;
        if (col.kind === "static") {
            var f = palette.statics.find(function (s) { return s.key === col.key; });
            return !!(f && f.numeric);
        }
        if (col.kind === "reading") {
            var r = palette.readings.find(function (x) { return "r:" + x.code === col.key; });
            return !!(r && (r.valueKind === "Numeric"));
        }
        return false;
    }

    // ---- palette -----------------------------------------------------------
    function loadPalette() {
        var url = "/Reports/ReportBuilderPalette?materialGroup=" + encodeURIComponent(state.materialGroup || "");
        return fetch(url, { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.json(); })
            .then(function (p) {
                palette = {
                    statics: p.statics || [],
                    defects: p.defects || [],
                    readings: p.readings || [],
                    sampleHeaders: p.sampleHeaders || [],
                    materialHeaders: p.materialHeaders || []
                };
                renderPalette();
            });
    }

    function paletteItem(label, sub, descriptor) {
        var div = document.createElement("div");
        div.className = "rb-item";
        div.draggable = true;
        div.innerHTML = '<i class="bi bi-grip-vertical text-muted"></i>' +
            '<span>' + esc(label) + '</span>' +
            (sub ? '<span class="rb-kind ms-auto">' + esc(sub) + '</span>' : '');
        div.addEventListener("dragstart", function (e) {
            e.dataTransfer.setData("text/plain", JSON.stringify(descriptor));
        });
        div.addEventListener("click", function () { addColumn(descriptor); });
        return div;
    }

    function groupTitle(t) {
        var d = document.createElement("div");
        d.className = "rb-group-title";
        d.textContent = t;
        return d;
    }

    function renderPalette() {
        var host = $("rbPalette");
        host.innerHTML = "";

        host.appendChild(groupTitle("Arrival / PO / Shipment / Sample fields"));
        palette.statics.forEach(function (f) {
            host.appendChild(paletteItem(f.label, f.numeric ? "#" : "", { key: f.key, kind: "static", label: f.label }));
        });

        host.appendChild(groupTitle("Defects" + (state.materialGroup ? " — " + state.materialGroup : " (pick a group)")));
        palette.defects.forEach(function (d) {
            host.appendChild(paletteItem(d.name, d.category, { key: "d:" + d.id, kind: "defect", label: d.name, defectValue: "count" }));
        });

        host.appendChild(groupTitle("Reading types"));
        palette.readings.forEach(function (r) {
            host.appendChild(paletteItem(r.name, r.unit || r.valueKind, { key: "r:" + r.code, kind: "reading", label: r.name }));
        });

        host.appendChild(groupTitle("Sample header fields"));
        palette.sampleHeaders.forEach(function (h) {
            host.appendChild(paletteItem(h.name, h.unit || "", { key: "sh:" + h.code, kind: "sampleHeader", label: h.name }));
        });

        host.appendChild(groupTitle("Material header fields"));
        palette.materialHeaders.forEach(function (h) {
            host.appendChild(paletteItem(h.name, h.unit || "", { key: "mh:" + h.code, kind: "materialHeader", label: h.name }));
        });
    }

    // ---- layout ------------------------------------------------------------
    function addColumn(desc, atIndex) {
        var col = {
            key: desc.key, kind: desc.kind, label: desc.label || desc.key,
            placement: "column",
            defectValue: desc.kind === "defect" ? "count" : undefined,
            total: null,
            formula: desc.kind === "calc" ? "" : undefined
        };
        if (typeof atIndex === "number") state.columns.splice(atIndex, 0, col);
        else state.columns.push(col);
        renderLayout();
    }

    function renderLayout() {
        var host = $("rbLayout");
        host.innerHTML = "";
        if (state.columns.length === 0) {
            host.innerHTML = '<div class="text-muted small p-3 text-center">Drag fields here, or click them in the palette.</div>';
            return;
        }
        state.columns.forEach(function (col, idx) {
            host.appendChild(colChip(col, idx));
        });
    }

    function colChip(col, idx) {
        var el = document.createElement("div");
        el.className = "rb-col" + (col.placement === "header" ? " header" : "");
        el.draggable = true;
        el.dataset.idx = idx;

        // handle + kind badge
        var kindBadge = { static: "field", defect: "defect", reading: "reading",
            sampleHeader: "s.header", materialHeader: "m.header", calc: "calc", empty: "empty" }[col.kind] || col.kind;

        var html = '<i class="bi bi-grip-vertical rb-handle"></i>' +
            '<span class="rb-kind">' + esc(kindBadge) + '</span>' +
            '<input class="rb-label" value="' + esc(col.label || "") + '" />';

        // header / column toggle
        html += '<select class="rb-place form-select form-select-sm" style="width:auto">' +
            '<option value="column"' + (col.placement === "column" ? " selected" : "") + '>Column</option>' +
            '<option value="header"' + (col.placement === "header" ? " selected" : "") + '>Header</option>' +
            '</select>';

        // defect count/%
        if (col.kind === "defect") {
            html += '<select class="rb-defval form-select form-select-sm" style="width:auto" title="Defect value">' +
                '<option value="count"' + (col.defectValue !== "percent" ? " selected" : "") + '>Count</option>' +
                '<option value="percent"' + (col.defectValue === "percent" ? " selected" : "") + '>%</option>' +
                '</select>';
        }

        // totals (numeric only, column placement only)
        if (isNumeric(col)) {
            html += '<select class="rb-total form-select form-select-sm" style="width:auto" title="Totals">' +
                '<option value=""' + (!col.total ? " selected" : "") + '>No total</option>' +
                '<option value="sum"' + (col.total === "sum" ? " selected" : "") + '>Sum</option>' +
                '<option value="avg"' + (col.total === "avg" ? " selected" : "") + '>Average</option>' +
                '</select>';
        }

        // calc formula
        if (col.kind === "calc") {
            html += '<input class="rb-formula form-control form-control-sm" placeholder="e.g. ([Bruising] + [Decay]) / [Sample Size] * 100" value="' + esc(col.formula || "") + '" />';
        }

        html += '<i class="bi bi-x-circle rb-x" title="Remove"></i>';
        el.innerHTML = html;

        // wire controls
        el.querySelector(".rb-label").addEventListener("input", function (e) { col.label = e.target.value; });
        el.querySelector(".rb-place").addEventListener("change", function (e) {
            col.placement = e.target.value; renderLayout();
        });
        var dv = el.querySelector(".rb-defval");
        if (dv) dv.addEventListener("change", function (e) { col.defectValue = e.target.value; });
        var tot = el.querySelector(".rb-total");
        if (tot) tot.addEventListener("change", function (e) { col.total = e.target.value || null; });
        var fx = el.querySelector(".rb-formula");
        if (fx) fx.addEventListener("input", function (e) { col.formula = e.target.value; validateFormula(fx, col); });
        el.querySelector(".rb-x").addEventListener("click", function () {
            state.columns.splice(idx, 1); renderLayout();
        });

        // reorder DnD
        el.addEventListener("dragstart", function (e) {
            el.classList.add("dragging");
            e.dataTransfer.setData("application/x-rb-reorder", String(idx));
        });
        el.addEventListener("dragend", function () { el.classList.remove("dragging"); });
        return el;
    }

    function validateFormula(input, col) {
        var cols = state.columns.filter(function (c) { return c !== col; }).map(labelOf);
        postJson("/Reports/ReportBuilderValidateFormula", { formula: col.formula, columns: cols })
            .then(function (r) { return r.json(); })
            .then(function (res) {
                input.classList.toggle("invalid", !res.ok);
                input.title = res.ok ? "" : (res.error || "Invalid formula");
            }).catch(function () {});
    }

    function labelOf(c) { return c.label || c.key; }

    // layout drop target (append from palette, or reorder)
    (function wireLayoutDrop() {
        var host = $("rbLayout");
        host.addEventListener("dragover", function (e) { e.preventDefault(); host.classList.add("drag-over"); });
        host.addEventListener("dragleave", function () { host.classList.remove("drag-over"); });
        host.addEventListener("drop", function (e) {
            e.preventDefault(); host.classList.remove("drag-over");
            var reorder = e.dataTransfer.getData("application/x-rb-reorder");
            if (reorder !== "") {
                var from = parseInt(reorder, 10);
                var to = dropIndex(host, e.clientY);
                moveColumn(from, to);
                return;
            }
            var raw = e.dataTransfer.getData("text/plain");
            if (!raw) return;
            try { addColumn(JSON.parse(raw), dropIndex(host, e.clientY)); } catch (_) {}
        });
    })();

    function dropIndex(host, y) {
        var chips = Array.prototype.slice.call(host.querySelectorAll(".rb-col"));
        for (var i = 0; i < chips.length; i++) {
            var r = chips[i].getBoundingClientRect();
            if (y < r.top + r.height / 2) return i;
        }
        return chips.length;
    }
    function moveColumn(from, to) {
        if (from === to || from + 1 === to) { renderLayout(); return; }
        var item = state.columns.splice(from, 1)[0];
        if (from < to) to--;
        state.columns.splice(to, 0, item);
        renderLayout();
    }

    // ---- add calc / empty --------------------------------------------------
    $("rbAddCalc").addEventListener("click", function (e) {
        e.preventDefault();
        addColumn({ key: "calc:" + (++calcSeq), kind: "calc", label: "Calculated " + calcSeq });
    });
    $("rbAddEmpty").addEventListener("click", function (e) {
        e.preventDefault();
        addColumn({ key: "blank:" + (++blankSeq), kind: "empty", label: "Column " + blankSeq });
    });

    // ---- material group ----------------------------------------------------
    $("rbGroup").addEventListener("change", function (e) {
        state.materialGroup = e.target.value;
        loadPalette();
    });

    // ---- saved designs -----------------------------------------------------
    function listSaved() {
        return fetch("/Reports/Perspectives?report=" + encodeURIComponent(REPORT), { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                var sel = $("rbSaved");
                sel.innerHTML = '<option value="">— new report —</option>';
                (data.items || []).forEach(function (it) {
                    var o = document.createElement("option");
                    o.value = it.id;
                    o.textContent = it.name + (it.isOwn ? "" : " (shared)");
                    o.dataset.config = it.configJson;
                    o.dataset.name = it.name;
                    o.dataset.scope = it.scope;
                    o.dataset.own = it.isOwn ? "1" : "0";
                    sel.appendChild(o);
                });
            });
    }

    $("rbSaved").addEventListener("change", function (e) {
        var opt = e.target.selectedOptions[0];
        if (!opt || !opt.value) { newReport(); return; }
        var cfg = {};
        try { cfg = JSON.parse(opt.dataset.config || "{}"); } catch (_) {}
        state.loadedId = parseInt(opt.value, 10);
        state.materialGroup = cfg.materialGroup || "";
        state.columns = (cfg.columns || []).map(function (c) { return Object.assign({}, c); });
        $("rbName").value = opt.dataset.name || "";
        $("rbGroup").value = state.materialGroup;
        if ($("rbShared")) $("rbShared").checked = (opt.dataset.scope === "shared");
        loadPalette().then(renderLayout);
    });

    function newReport() {
        state.loadedId = null; state.columns = []; state.materialGroup = "";
        $("rbName").value = ""; $("rbGroup").value = "";
        if ($("rbShared")) $("rbShared").checked = false;
        loadPalette().then(renderLayout);
    }

    function currentConfigJson() {
        return JSON.stringify({ materialGroup: state.materialGroup, columns: state.columns });
    }

    function doSave(asNew) {
        var name = ($("rbName").value || "").trim();
        if (!name) { status("Give the report a name first.", true); return; }
        if (state.columns.length === 0) { status("Add at least one column.", true); return; }
        var body = {
            id: asNew ? null : state.loadedId,
            reportKey: REPORT,
            name: name,
            scope: ($("rbShared") && $("rbShared").checked) ? "shared" : "private",
            isDefault: false,
            configJson: currentConfigJson()
        };
        postJson("/Reports/SavePerspective", body)
            .then(function (r) { if (!r.ok) return r.json().then(function (x) { throw new Error(x.error || "Save failed"); }); return r.json(); })
            .then(function (dto) {
                state.loadedId = dto.id;
                status("Saved.");
                return listSaved().then(function () { $("rbSaved").value = String(dto.id); });
            })
            .catch(function (err) { status(err.message, true); });
    }
    $("rbSave").addEventListener("click", function (e) { e.preventDefault(); doSave(false); });
    $("rbSaveAs").addEventListener("click", function (e) { e.preventDefault(); doSave(true); });

    $("rbDelete").addEventListener("click", function (e) {
        e.preventDefault();
        if (!state.loadedId) { newReport(); return; }
        if (!confirm("Delete this saved report design?")) return;
        var body = new URLSearchParams();
        body.append("id", state.loadedId);
        body.append("__RequestVerificationToken", token());
        fetch("/Reports/DeletePerspective", { method: "POST", headers: { "X-CSRF-TOKEN": token() }, body: body })
            .then(function (r) { if (!r.ok) throw new Error("Delete failed"); status("Deleted."); newReport(); return listSaved(); })
            .catch(function (err) { status(err.message, true); });
    });

    // ---- browse filters + cascade -----------------------------------------
    function loadVendors() {
        return fetch("/Reports/ReportBuilderVendors", { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.json(); })
            .then(function (rows) {
                var sel = $("rbVendor");
                sel.innerHTML = '<option value="">— all —</option>';
                rows.forEach(function (v) {
                    var o = document.createElement("option");
                    o.value = v.vendorNo; o.textContent = v.vendorName || v.vendorNo;
                    sel.appendChild(o);
                });
            });
    }
    function loadContainers(vendorNo) {
        var url = "/Reports/ReportBuilderContainers" + (vendorNo ? "?vendorNo=" + encodeURIComponent(vendorNo) : "");
        return fetch(url, { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.json(); })
            .then(function (rows) {
                var sel = $("rbContainer");
                sel.innerHTML = '<option value="">— all —</option>';
                rows.forEach(function (c) {
                    var o = document.createElement("option");
                    o.value = c.containerNo;
                    o.textContent = c.containerNo + (c.ebeln ? "  (PO " + c.ebeln + ")" : "");
                    o.dataset.bol = c.bolNo || ""; o.dataset.ebeln = c.ebeln || "";
                    sel.appendChild(o);
                });
            });
    }
    $("rbVendor").addEventListener("change", function (e) { loadContainers(e.target.value); });
    $("rbContainer").addEventListener("change", function (e) {
        var opt = e.target.selectedOptions[0];
        if (opt && opt.value) { $("rbBol").value = opt.dataset.bol || ""; $("rbPo").value = opt.dataset.ebeln || ""; }
    });

    function currentFilter() {
        return {
            materialGroup: state.materialGroup || null,
            vendorNo: $("rbVendor").value || null,
            containerNo: $("rbContainer").value || null,
            bolNo: $("rbBol").value || null,
            ebeln: $("rbPo").value || null,
            poFrom: $("rbPoFrom").value || null,
            poTo: $("rbPoTo").value || null
        };
    }
    function requestBody() {
        return { perspectiveId: null, name: ($("rbName").value || "Report"), definition: { materialGroup: state.materialGroup, columns: state.columns }, filter: currentFilter() };
    }

    // ---- preview -----------------------------------------------------------
    $("rbPreview").addEventListener("click", function (e) {
        e.preventDefault();
        if (state.columns.length === 0) { status("Add columns first.", true); return; }
        status("Loading preview…");
        postJson("/Reports/ReportBuilderPreview", requestBody())
            .then(function (r) { if (!r.ok) return r.json().then(function (x) { throw new Error(x.error || "Preview failed"); }); return r.json(); })
            .then(function (p) { renderPreview(p); status(p.sampleCount + " sample(s)" + (p.truncated ? " (showing first 100)" : "")); })
            .catch(function (err) { status(err.message, true); });
    });

    function renderPreview(p) {
        $("rbPreviewWrap").style.display = "";
        var hdr = $("rbPreviewHeader");
        hdr.innerHTML = (p.header || []).map(function (kv) {
            return '<span class="me-3"><strong>' + esc(kv.key) + ':</strong> ' + esc(kv.value) + '</span>';
        }).join("");
        var t = $("rbPreviewTable");
        var thead = "<thead><tr>" + (p.columns || []).map(function (c) { return "<th>" + esc(c) + "</th>"; }).join("") + "</tr></thead>";
        var tbody = "<tbody>" + (p.rows || []).map(function (row) {
            return "<tr>" + row.map(function (v) { return "<td>" + esc(v) + "</td>"; }).join("") + "</tr>";
        }).join("") + "</tbody>";
        t.innerHTML = thead + tbody;
    }

    // ---- export ------------------------------------------------------------
    $("rbExport").addEventListener("click", function (e) {
        e.preventDefault();
        if (state.columns.length === 0) { status("Add columns first.", true); return; }
        status("Building Excel…");
        postJson("/Reports/ReportBuilderExport", requestBody())
            .then(function (r) {
                if (!r.ok) return r.json().then(function (x) { throw new Error(x.error || "Export failed"); });
                var disp = r.headers.get("Content-Disposition") || "";
                var m = /filename\*?=(?:UTF-8'')?"?([^\";]+)"?/i.exec(disp);
                var fname = m ? decodeURIComponent(m[1]) : "report.xlsx";
                return r.blob().then(function (b) { return { b: b, fname: fname }; });
            })
            .then(function (x) {
                var url = URL.createObjectURL(x.b);
                var a = document.createElement("a");
                a.href = url; a.download = x.fname; document.body.appendChild(a); a.click();
                a.remove(); URL.revokeObjectURL(url);
                status("Exported.");
            })
            .catch(function (err) { status(err.message, true); });
    });

    // ---- utils -------------------------------------------------------------
    function esc(s) {
        return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
        });
    }

    // ---- boot --------------------------------------------------------------
    loadPalette().then(renderLayout);
    listSaved();
    loadVendors();
    loadContainers("");
})();
