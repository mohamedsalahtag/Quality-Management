// V34 (2026-06-20). Perspective Analyzer client.
// - Talks to /Reports/PivotSchema, /Reports/Pivot, /Reports/Perspectives,
//   SavePerspective, SetDefaultPerspective, DeletePerspective.
// - UI: drag-and-drop / click chip-zones for Rows + Columns (mutual exclusion).
// - Renderers: Table (with optional heatmap shading) + Plotly chart shapes.
// - Numeric format presets: auto / 0 / 0.0 / 0.00 / 0%.
// - "Export pivot to Excel (CSV)" builds the file in the browser from the
//   already-rendered result (no server roundtrip; opens cleanly in Excel).
(function () {
    "use strict";

    var card = document.getElementById("perspectiveCard");
    if (!card) return;

    var REPORT      = card.getAttribute("data-report");
    var FILTER_QS   = card.getAttribute("data-filter-query") || "";
    var CAN_SHARE   = card.getAttribute("data-can-share") === "true";

    var $available  = document.getElementById("availableList");
    var $rowsZone   = document.getElementById("rowsZone");
    var $colsZone   = document.getElementById("colsZone");
    var $search     = document.getElementById("dimSearch");
    // V34.3: $measure / $agg are gone — measures now live in the chip zone.
    var $valuesZone = document.getElementById("valuesZone");
    var $btnAddMeasure = document.getElementById("btnAddMeasure");
    var $measureModal       = document.getElementById("measurePickerModal");
    var $measureEditingIdx  = document.getElementById("measureEditingIndex");
    var $measureSelect      = document.getElementById("measureSelect");
    var $measureAgg         = document.getElementById("measureAgg");
    var $measureFormat      = document.getElementById("measureFormat");
    var $measureLabelInput  = document.getElementById("measureLabel");
    var $btnApplyMeasure    = document.getElementById("btnApplyMeasure");
    var $measureModalTitle  = document.getElementById("measureModalTitle");
    var $chartMeasure       = document.getElementById("chartMeasure");
    var $chartMeasureLabel  = document.getElementById("chartMeasureLabel");

    var $format     = document.getElementById("pivotFormat");
    var $renderer   = document.getElementById("pivotRenderer");
    var $topN       = document.getElementById("pivotTopN");
    var $heatmap    = document.getElementById("pivotHeatmap");
    var $hideZero   = document.getElementById("pivotHideZero");
    var $output     = document.getElementById("pivotOutput");
    var $meta       = document.getElementById("pivotMeta");
    var $msg        = document.getElementById("pivotMessage");
    var $select     = document.getElementById("perspectiveSelect");
    var $btnRun     = document.getElementById("btnRunPivot");
    var $btnReset   = document.getElementById("btnResetPivot");
    var $btnSaveA   = document.getElementById("btnSaveAs");
    var $btnSave    = document.getElementById("btnSave");
    var $btnDef     = document.getElementById("btnSetDefault");
    var $btnDel     = document.getElementById("btnDelete");
    var $btnExport  = document.getElementById("btnExportXlsx");

    var $modal      = document.getElementById("savePerspectiveModal");
    var $saveId     = document.getElementById("saveId");
    var $saveName   = document.getElementById("saveName");
    var $saveDef    = document.getElementById("saveIsDefault");
    var $btnConfirm = document.getElementById("btnConfirmSave");

    // V34.1: drill + scope DOM
    var $scopeCrumbText    = document.getElementById("scopeCrumbText");
    var $scopeCrumb        = document.getElementById("scopeCrumb");
    var $drillZone         = document.getElementById("drillZone");
    var $btnAddDrill       = document.getElementById("btnAddDrill");
    var $drillModal        = document.getElementById("drillPickerModal");
    var $drillEditingKey   = document.getElementById("drillEditingKey");
    var $drillDimSelect    = document.getElementById("drillDimSelect");
    var $drillValueSearch  = document.getElementById("drillValueSearch");
    var $drillValuesLoading= document.getElementById("drillValuesLoading");
    var $drillValuesTrunc  = document.getElementById("drillValuesTruncated");
    var $drillValuesList   = document.getElementById("drillValuesList");
    var $drillSelectAll    = document.getElementById("drillSelectAll");
    var $drillClearAll     = document.getElementById("drillClearAll");
    var $btnApplyDrill     = document.getElementById("btnApplyDrill");
    var $drillModalTitle   = document.getElementById("drillModalTitle");

    // -- in-memory state -------------------------------------------------
    var schema       = null;     // { dimensions:[{key,display}], measures:[{key,display,aggs:[]}], renderers:[] }
    var dimByKey     = {};       // key -> {display}
    var measureAggs  = {};
    var perspectives = [];
    var current      = null;
    var rowsSel      = [];       // [{key, display}] in display order
    var colsSel      = [];
    var measuresSel  = [];       // V34.3: [{ key, display, agg, format?, label? }]
    var drills       = [];       // [{ key, values:[string] }]
    var ignorePage   = false;
    var lastResult   = null;
    var pageFilter   = null;     // memoised parse of FILTER_QS
    var lastReqBody  = null;     // last PivotRequest sent (used for XLSX POST)

    // -- antiforgery helper ----------------------------------------------
    function antiForgeryToken() {
        var t = document.querySelector('#perspectiveAfForm input[name="__RequestVerificationToken"]');
        return t ? t.value : "";
    }
    // The app configures AntiforgeryOptions.HeaderName = "X-CSRF-TOKEN"; that's
    // the only header the middleware accepts for AJAX/JSON POSTs.
    function jsonHeaders() {
        return {
            "Content-Type":     "application/json",
            "X-CSRF-TOKEN":     antiForgeryToken(),
            "X-Requested-With": "XMLHttpRequest"
        };
    }
    function formHeaders() {
        return {
            "Content-Type":     "application/x-www-form-urlencoded",
            "X-CSRF-TOKEN":     antiForgeryToken(),
            "X-Requested-With": "XMLHttpRequest"
        };
    }

    // -- safe response parser -------------------------------------------
    // Handles empty bodies so we never throw "Unexpected end of JSON input"
    // on 4xx / 5xx responses that don't carry a payload.
    function parseResponse(r) {
        return r.text().then(function (txt) {
            var j = null;
            if (txt) {
                try { j = JSON.parse(txt); } catch (e) { j = { error: txt }; }
            }
            if (!r.ok) {
                var msg = (j && j.error) ? j.error
                       : ("HTTP " + r.status + " " + r.statusText);
                throw new Error(msg);
            }
            return j || {};
        });
    }

    // -- schema bootstrap ------------------------------------------------
    function loadSchema() {
        return fetch("/Reports/PivotSchema?report=" + encodeURIComponent(REPORT), {
            headers: { "X-Requested-With": "XMLHttpRequest" }
        })
        .then(parseResponse)
        .then(function (s) {
            schema      = s;
            dimByKey    = {};
            measureAggs = {};
            measuresByKey = {};
            s.dimensions.forEach(function (d) { dimByKey[d.key] = d; });
            s.measures.forEach(function (m) {
                measureAggs[m.key] = m.aggs;
                measuresByKey[m.key] = m;
            });
            s.renderers.forEach(function (r) {
                $renderer.appendChild(new Option(r, r));
            });
            // Default: a single measure chip (first registered measure with
            // its first allowed agg) so a fresh page lands on something
            // runnable, matching the v1 behaviour.
            if (measuresSel.length === 0 && s.measures.length > 0) {
                var m0 = s.measures[0];
                measuresSel.push({
                    key: m0.key, display: m0.display,
                    agg: m0.aggs[0] || "SUM", format: null, label: null
                });
            }
            renderAvailable("");
            renderZone($rowsZone, rowsSel, "rows");
            renderZone($colsZone, colsSel, "cols");
            renderValuesZone();
        });
    }

    var measuresByKey = {};

    function aggLabel(a) {
        return a === "COUNT_DISTINCT" ? "Distinct count" : (a.charAt(0) + a.slice(1).toLowerCase());
    }

    // -- chip / zone rendering -------------------------------------------
    function dimUsedKeys() {
        var used = {};
        rowsSel.forEach(function (d) { used[d.key] = "rows"; });
        colsSel.forEach(function (d) { used[d.key] = "cols"; });
        return used;
    }
    function renderAvailable(filterText) {
        $available.innerHTML = "";
        var used = dimUsedKeys();
        var q = (filterText || "").trim().toLowerCase();
        (schema && schema.dimensions || []).forEach(function (d) {
            if (used[d.key]) return;                                // mutual exclusion
            if (q && d.display.toLowerCase().indexOf(q) < 0
                  && d.key.toLowerCase().indexOf(q) < 0) return;
            var row = document.createElement("div");
            row.className = "available-item";
            row.setAttribute("draggable", "true");
            row.setAttribute("data-key", d.key);
            row.innerHTML =
              '<span><i class="bi bi-grip-vertical text-muted me-1"></i>' + escapeHtml(d.display) + '</span>' +
              '<span class="ai-btns">' +
                '<button type="button" class="btn btn-sm btn-outline-warning me-1" title="Add to Rows" data-target="rows"><i class="bi bi-arrow-bar-right"></i> Rows</button>' +
                '<button type="button" class="btn btn-sm btn-outline-primary"      title="Add to Cols" data-target="cols"><i class="bi bi-arrow-bar-down"></i> Cols</button>' +
              '</span>';
            row.addEventListener("dragstart", onChipDragStart);
            row.addEventListener("dragend",   onChipDragEnd);
            row.querySelectorAll("button[data-target]").forEach(function (b) {
                b.addEventListener("click", function (ev) {
                    ev.stopPropagation();
                    moveDim(d.key, "available", b.getAttribute("data-target"), -1);
                });
            });
            $available.appendChild(row);
        });
    }
    function renderZone(zoneEl, list, zoneKey) {
        zoneEl.innerHTML = "";
        if (list.length === 0) {
            var empty = document.createElement("div");
            empty.className = "text-muted small p-2";
            empty.textContent = "Drop dimensions here, or click + on a field on the left.";
            zoneEl.appendChild(empty);
            return;
        }
        list.forEach(function (d, i) {
            var chip = document.createElement("span");
            chip.className = "pivot-chip";
            chip.setAttribute("draggable", "true");
            chip.setAttribute("data-key", d.key);
            chip.setAttribute("data-index", String(i));
            chip.setAttribute("data-zone", zoneKey);
            chip.innerHTML =
              '<i class="bi bi-grip-vertical"></i> ' +
              escapeHtml(d.display) +
              ' <span class="chip-x" title="Remove">&times;</span>';
            chip.addEventListener("dragstart", onChipDragStart);
            chip.addEventListener("dragend",   onChipDragEnd);
            chip.querySelector(".chip-x").addEventListener("click", function (ev) {
                ev.stopPropagation();
                moveDim(d.key, zoneKey, "available", -1);
            });
            zoneEl.appendChild(chip);
        });
    }
    function moveDim(key, fromZone, toZone, insertIndex) {
        if (fromZone === toZone) {
            // Reorder within the same zone.
            var list = (toZone === "rows") ? rowsSel : colsSel;
            var fromIdx = list.findIndex(function (d) { return d.key === key; });
            if (fromIdx < 0) return;
            var item = list.splice(fromIdx, 1)[0];
            var dest = (insertIndex < 0 || insertIndex > list.length) ? list.length : insertIndex;
            if (fromIdx < dest) dest -= 1;
            list.splice(dest, 0, item);
            renderZone(toZone === "rows" ? $rowsZone : $colsZone, list, toZone);
            return;
        }
        // Remove from source.
        if (fromZone === "rows") rowsSel = rowsSel.filter(function (d) { return d.key !== key; });
        else if (fromZone === "cols") colsSel = colsSel.filter(function (d) { return d.key !== key; });
        // Add to destination.
        var dim = dimByKey[key]; if (!dim) return;
        if (toZone === "rows") {
            var idx = (insertIndex < 0 || insertIndex > rowsSel.length) ? rowsSel.length : insertIndex;
            rowsSel.splice(idx, 0, { key: dim.key, display: dim.display });
        } else if (toZone === "cols") {
            var idx2 = (insertIndex < 0 || insertIndex > colsSel.length) ? colsSel.length : insertIndex;
            colsSel.splice(idx2, 0, { key: dim.key, display: dim.display });
        }
        renderAvailable($search.value);
        renderZone($rowsZone, rowsSel, "rows");
        renderZone($colsZone, colsSel, "cols");
    }

    // -- drag and drop ---------------------------------------------------
    var dragState = null;
    function onChipDragStart(ev) {
        dragState = {
            key:  ev.currentTarget.getAttribute("data-key"),
            from: ev.currentTarget.getAttribute("data-zone") || "available"
        };
        ev.currentTarget.classList.add("dragging");
        try { ev.dataTransfer.setData("text/plain", dragState.key); } catch (e) {}
        ev.dataTransfer.effectAllowed = "move";
    }
    function onChipDragEnd(ev) {
        ev.currentTarget.classList.remove("dragging");
        dragState = null;
        $rowsZone.classList.remove("drag-over");
        $colsZone.classList.remove("drag-over");
        $available.classList.remove("drag-over");
    }
    function setupDropZone(el, zoneKey) {
        el.addEventListener("dragover", function (ev) {
            if (!dragState) return;
            ev.preventDefault();
            el.classList.add("drag-over");
            ev.dataTransfer.dropEffect = "move";
        });
        el.addEventListener("dragleave", function (ev) {
            if (ev.target === el) el.classList.remove("drag-over");
        });
        el.addEventListener("drop", function (ev) {
            ev.preventDefault();
            el.classList.remove("drag-over");
            if (!dragState) return;
            // Find insertion index from the cursor's x-coord relative to chips.
            var insertIdx = -1;
            var chips = Array.prototype.slice.call(el.querySelectorAll(".pivot-chip"));
            for (var i = 0; i < chips.length; i++) {
                var r = chips[i].getBoundingClientRect();
                var horizontal = (zoneKey === "cols");
                var mid = horizontal ? (r.left + r.width / 2) : (r.top + r.height / 2);
                var pos = horizontal ? ev.clientX : ev.clientY;
                if (pos < mid) { insertIdx = i; break; }
            }
            moveDim(dragState.key, dragState.from, zoneKey, insertIdx);
        });
    }
    setupDropZone($rowsZone, "rows");
    setupDropZone($colsZone, "cols");
    $available.addEventListener("dragover", function (ev) {
        if (!dragState || dragState.from === "available") return;
        ev.preventDefault();
        $available.classList.add("drag-over");
    });
    $available.addEventListener("drop", function (ev) {
        if (!dragState || dragState.from === "available") return;
        ev.preventDefault();
        $available.classList.remove("drag-over");
        moveDim(dragState.key, dragState.from, "available", -1);
    });

    // -- search ----------------------------------------------------------
    $search.addEventListener("input", function () { renderAvailable($search.value); });

    // -- perspective list ------------------------------------------------
    function listPerspectives() {
        return fetch("/Reports/Perspectives?report=" + encodeURIComponent(REPORT), {
            headers: { "X-Requested-With": "XMLHttpRequest" }
        })
        .then(parseResponse)
        .then(function (j) {
            perspectives = j.items || [];
            renderSelect();
            return j;
        });
    }
    function renderSelect() {
        $select.innerHTML = '<option value="">(no perspective loaded)</option>';
        var mine   = perspectives.filter(function (p) { return p.isOwn; });
        var shared = perspectives.filter(function (p) { return !p.isOwn; });
        function addGroup(label, items) {
            if (items.length === 0) return;
            var og = document.createElement("optgroup"); og.label = label;
            items.forEach(function (p) {
                var name = p.name + (p.isDefault ? " ★" : "")
                         + (p.scope === "shared" && !p.isOwn ? " (by " + p.ownerUsername + ")" : "");
                og.appendChild(new Option(name, String(p.id)));
            });
            $select.appendChild(og);
        }
        addGroup("My perspectives", mine);
        addGroup("Shared",          shared);
    }
    $select.addEventListener("change", function () {
        var id = parseInt($select.value, 10);
        if (!id) { current = null; updateButtonState(); return; }
        var p = perspectives.find(function (x) { return x.id === id; });
        if (!p) return;
        current = p;
        applyConfig(p.configJson);
        updateButtonState();
        runPivot();
    });
    function updateButtonState() {
        var isOwn = !!(current && current.isOwn);
        $btnSave.disabled = !isOwn;
        $btnDel.disabled  = !isOwn;
        $btnDef.disabled  = !current;
    }

    // -- config <-> UI ---------------------------------------------------
    function readConfig() {
        return {
            rows:       rowsSel.map(function (d) { return d.key; }),
            cols:       colsSel.map(function (d) { return d.key; }),
            measures:   measuresSel.map(function (m) {
                return { key: m.key, agg: m.agg, format: m.format || null, label: m.label || null };
            }),
            renderer:   $renderer.value,
            chartMeasure: $chartMeasure ? $chartMeasure.value : "",
            format:     $format.value,
            heatmap:    $heatmap.checked,
            hideZero:   $hideZero.checked,
            topN:       $topN.value ? parseInt($topN.value, 10) : null,
            drills:     drills.map(function (d) { return { key: d.key, values: d.values.slice() }; }),
            ignorePage: ignorePage
        };
    }
    function applyConfig(json) {
        var cfg; try { cfg = JSON.parse(json || "{}"); } catch (e) { cfg = {}; }
        rowsSel = (cfg.rows || []).map(function (k) { return dimByKey[k]; })
                  .filter(Boolean).map(function (d) { return { key: d.key, display: d.display }; });
        colsSel = (cfg.cols || []).map(function (k) { return dimByKey[k]; })
                  .filter(Boolean).map(function (d) { return { key: d.key, display: d.display }; });
        // V34.3: measures live in measuresSel. Older saved configs use the
        // singular { measure, agg } shape -- promote that into a one-element
        // list so the old perspectives still load.
        var savedMs = [];
        if (cfg.measures && cfg.measures.length > 0) savedMs = cfg.measures;
        else if (cfg.measure) savedMs = [{ key: cfg.measure, agg: cfg.agg || "SUM" }];
        measuresSel = savedMs.map(function (m) {
            var registered = measuresByKey[m.key];
            if (!registered) return null;
            return {
                key: registered.key, display: registered.display,
                agg: m.agg || (registered.aggs[0] || "SUM"),
                format: m.format || null, label: m.label || null
            };
        }).filter(Boolean);
        if (cfg.renderer) $renderer.value = cfg.renderer;
        if (cfg.format)   $format.value   = cfg.format;
        if (typeof cfg.heatmap === "boolean") $heatmap.checked = cfg.heatmap;
        if (typeof cfg.hideZero === "boolean") $hideZero.checked = cfg.hideZero;
        $topN.value = cfg.topN || "";
        drills = (cfg.drills || []).filter(function (d) { return d && d.key && dimByKey[d.key]; })
            .map(function (d) { return { key: d.key, values: (d.values || []).slice() }; });
        setScope(cfg.ignorePage ? "all" : "page");
        renderAvailable($search.value);
        renderZone($rowsZone, rowsSel, "rows");
        renderZone($colsZone, colsSel, "cols");
        renderValuesZone();
        renderDrillZone();
        // chartMeasure restored later, after values zone re-renders the picker.
        if (cfg.chartMeasure) {
            var idx = measuresSel.findIndex(function (m) { return m.key === cfg.chartMeasure; });
            if (idx >= 0 && $chartMeasure) $chartMeasure.value = String(idx);
        }
    }

    // -- filter parsing (host page's querystring) ------------------------
    function readFilterFromQs() {
        if (pageFilter) return pageFilter;
        var q = FILTER_QS || "";
        if (q.indexOf("?") === 0) q = q.substring(1);
        var f = {};
        if (q) {
            q.split("&").forEach(function (kv) {
                if (!kv) return;
                var i = kv.indexOf("=");
                var k = i < 0 ? decodeURIComponent(kv) : decodeURIComponent(kv.substring(0, i));
                var v = i < 0 ? "" : decodeURIComponent(kv.substring(i + 1).replace(/\+/g, " "));
                if (!v) return;
                f[k.charAt(0).toUpperCase() + k.slice(1)] = v;
            });
        }
        pageFilter = f;
        return f;
    }
    function effectivePageFilter() { return ignorePage ? {} : readFilterFromQs(); }
    function activeScopeFilterPayload() { return ignorePage ? {} : readFilterFromQs(); }

    // -- scope breadcrumb ------------------------------------------------
    var SCOPE_LABELS = {
        Status: "Status", Plant: "Plant", MaterialGroup: "Material group",
        MajorCategory: "Major cat.", Variety: "Variety", Origin: "Origin",
        MaterialClass: "Class", VendorName: "Supplier", VendorNo: "Vendor No",
        StorageLocation: "Storage Loc.", ContainerNo: "Container", BolNo: "BOL",
        Ebeln: "PO", SampleScope: "Sample scope",
        QualityOrderId: "QO #", ArrivalId: "Arrival #"
    };
    function renderScopeCrumb() {
        if (ignorePage) {
            $scopeCrumb.classList.add("muted");
            $scopeCrumbText.textContent = "ignoring page filters — pivot covers the whole dataset.";
            return;
        }
        $scopeCrumb.classList.remove("muted");
        var f = readFilterFromQs();
        var bits = [];
        if (f.PoFrom || f.PoTo)
            bits.push("PO " + (f.PoFrom || "…") + " → " + (f.PoTo || "…"));
        Object.keys(SCOPE_LABELS).forEach(function (k) {
            if (f[k]) bits.push(SCOPE_LABELS[k] + "=" + f[k]);
        });
        $scopeCrumbText.textContent = bits.length ? bits.join(" · ")
            : "matching the page above — no filters set, so the whole 30-day default window applies.";
    }
    function setScope(val) {
        ignorePage = (val === "all");
        var radio = document.querySelector('input[name="pivotScope"][value="' + val + '"]');
        if (radio) radio.checked = true;
        renderScopeCrumb();
    }
    document.querySelectorAll('input[name="pivotScope"]').forEach(function (r) {
        r.addEventListener("change", function () {
            ignorePage = (r.value === "all");
            renderScopeCrumb();
        });
    });

    // -- run pivot -------------------------------------------------------
    function runPivot() {
        var cfg = readConfig();
        if (cfg.rows.length === 0 && cfg.cols.length === 0) {
            showMessage("Pick at least one row or column dimension to pivot on.", "warning");
            return;
        }
        if (!cfg.measures || cfg.measures.length === 0) {
            showMessage("Add at least one measure in the Values zone.", "warning");
            return;
        }
        hideMessage();
        $btnRun.disabled = true;
        $btnRun.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Running…';
        var body = {
            ReportKey:        REPORT,
            Rows:             cfg.rows,
            Cols:             cfg.cols,
            Measures:         cfg.measures.map(function (m) {
                return { Key: m.key, Agg: m.agg, Format: m.format, Label: m.label };
            }),
            TopN:             cfg.topN,
            Filter:            activeScopeFilterPayload(),
            IgnorePageFilter: ignorePage,
            Drills:           drills.map(function (d) {
                return { DimensionKey: d.key, Values: d.values };
            })
        };
        lastReqBody = body;
        fetch("/Reports/Pivot", {
            method:  "POST",
            headers: jsonHeaders(),
            body:    JSON.stringify(body)
        })
        .then(parseResponse)
        .then(function (result) {
            var originalRows = result.rowKeys.length;
            var M = (result.measures || []).length;
            // V34.3: a row is "zero-total" only when every measure's row total
            // is 0 (otherwise we'd hide rows that are zero for measure A but
            // meaningful for measure B).
            var hiddenZero = 0;
            if (cfg.hideZero) {
                var keep = [];
                for (var i = 0; i < result.rowKeys.length; i++) {
                    var totals = result.rowTotals[i] || [];
                    var any = false;
                    for (var mi = 0; mi < M; mi++) {
                        if (Number(totals[mi]) !== 0) { any = true; break; }
                    }
                    if (any) keep.push(i);
                }
                if (keep.length !== result.rowKeys.length) {
                    hiddenZero = result.rowKeys.length - keep.length;
                    var keepSet = {};
                    keep.forEach(function (idx, ni) { keepSet[idx] = ni; });
                    result.rowKeys   = keep.map(function (idx) { return result.rowKeys[idx]; });
                    result.rowTotals = keep.map(function (idx) { return result.rowTotals[idx]; });
                    var newCells = [];
                    var hasColKeys = result.colKeys.length > 0;
                    var newGrand   = new Array(M);
                    var newColTotals = hasColKeys
                        ? result.colKeys.map(function () { return new Array(M).fill(0); })
                        : [];
                    for (var mz = 0; mz < M; mz++) newGrand[mz] = 0;
                    result.cells.forEach(function (c) {
                        if (!(c.rowIndex in keepSet)) return;
                        var nc = { rowIndex: keepSet[c.rowIndex], colIndex: c.colIndex, values: c.values };
                        newCells.push(nc);
                        for (var mz2 = 0; mz2 < M; mz2++) {
                            var v = Number(c.values[mz2]) || 0;
                            newGrand[mz2] += v;
                            if (hasColKeys && newColTotals[c.colIndex]) newColTotals[c.colIndex][mz2] += v;
                        }
                    });
                    result.cells       = newCells;
                    result.grandTotals = newGrand;
                    result.colTotals   = newColTotals;
                }
            }
            lastResult = result;
            // Build the chart-measure dropdown lazily based on the result.
            populateChartMeasurePicker(result);
            renderResult(result, cfg);
            // Spell out what each drill is doing in the meta line so the user
            // can verify the pivot really IS restricted by their drill picks,
            // e.g. "filtered to 8 Defect, 2 Plant".
            var drillSummary = "";
            if (drills.length > 0) {
                var parts = drills.map(function (d) {
                    var lbl = (dimByKey[d.key] && dimByKey[d.key].display) || d.key;
                    return d.values.length + " " + lbl;
                });
                drillSummary = "  ·  filtered to " + parts.join(", ");
            }
            var hiddenNote = hiddenZero > 0
                ? "  ·  " + hiddenZero + " of " + originalRows + " rows hidden (zero total)"
                : "";
            // V34.3: when there are 2+ measures, render the grand totals as
            // "label: value" pairs so the user can see each at a glance.
            var grandStr;
            if (M === 1) {
                grandStr = "Grand total: " + formatValue(result.grandTotals[0], measureFormat(result.measures[0], cfg.format));
            } else {
                grandStr = "Grand totals: " + result.measures.map(function (m, mi) {
                    return m.label + " " + formatValue(result.grandTotals[mi], measureFormat(m, cfg.format));
                }).join(" · ");
            }
            $meta.textContent = "Result: " + result.rowKeys.length + " rows × "
                + (result.colKeys.length || 1) + " cols × "
                + M + " measure" + (M === 1 ? "" : "s") + ". "
                + result.rowsScanned + " grouped rows. "
                + (result.truncated ? "Top-N applied. " : "")
                + grandStr
                + drillSummary
                + hiddenNote
                + "  ·  scope: " + (ignorePage ? "whole dataset" : "page filters");
            $btnExport.disabled = false;
        })
        .catch(function (e) {
            showMessage("Pivot failed: " + e.message, "danger");
            $output.innerHTML = "";
            $btnExport.disabled = true;
        })
        .finally(function () {
            $btnRun.disabled = false;
            $btnRun.innerHTML = '<i class="bi bi-play me-1"></i>Run';
        });
    }
    $btnRun.addEventListener("click", runPivot);
    $btnReset.addEventListener("click", function () {
        rowsSel = []; colsSel = []; drills = [];
        // Reset measures to a single chip = first registered measure.
        measuresSel = [];
        if (schema && schema.measures && schema.measures.length > 0) {
            var m0 = schema.measures[0];
            measuresSel.push({
                key: m0.key, display: m0.display,
                agg: m0.aggs[0] || "SUM", format: null, label: null
            });
        }
        $topN.value = "";
        $select.value = "";
        current = null;
        lastResult = null;
        $btnExport.disabled = true;
        setScope("page");
        updateButtonState();
        renderAvailable($search.value);
        renderZone($rowsZone, rowsSel, "rows");
        renderZone($colsZone, colsSel, "cols");
        renderValuesZone();
        renderDrillZone();
        $output.innerHTML = "";
        $meta.textContent = "";
    });

    // -- formatting ------------------------------------------------------
    function formatValue(n, fmt) {
        if (n == null || isNaN(n)) return "";
        if (fmt === "int")     return Number(n).toLocaleString(undefined, { maximumFractionDigits: 0 });
        if (fmt === "dec1")    return Number(n).toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
        if (fmt === "dec2")    return Number(n).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
        if (fmt === "percent") return Number(n).toLocaleString(undefined, { style: "percent", maximumFractionDigits: 2 });
        // auto
        var abs = Math.abs(n);
        if (abs >= 100) return Number(n).toLocaleString(undefined, { maximumFractionDigits: 0 });
        if (abs >= 10)  return Number(n).toLocaleString(undefined, { maximumFractionDigits: 1 });
        return Number(n).toLocaleString(undefined, { maximumFractionDigits: 2 });
    }

    // -- rendering -------------------------------------------------------
    function renderResult(result, cfg) {
        $output.innerHTML = "";
        if (result.rowKeys.length === 0 && result.colKeys.length === 0) {
            $output.innerHTML = '<div class="text-muted py-3">No data for this configuration.</div>';
            return;
        }
        if (cfg.renderer === "Table") renderTable(result, cfg);
        else if (window.Plotly) renderPlotly(result, cfg);
        else renderTable(result, cfg);
    }

    function measureFormat(m, fallback) { return m && m.format ? m.format : fallback; }

    function renderTable(result, cfg) {
        var rk = result.rowKeys, ck = result.colKeys;
        var ms = result.measures || [];
        var M  = ms.length;
        var matrix = buildMatrix3D(result);

        // Per-measure heatmap min/max so a 0-100 percent column doesn't get
        // washed out by a 0-1000 count column.
        var heatRange = ms.map(function () { return { min: Infinity, max: -Infinity }; });
        if (cfg.heatmap) {
            for (var i = 0; i < matrix.length; i++)
                for (var j = 0; j < matrix[i].length; j++)
                    for (var mi = 0; mi < M; mi++) {
                        var v = matrix[i][j][mi];
                        if (v == null) continue;
                        if (v < heatRange[mi].min) heatRange[mi].min = v;
                        if (v > heatRange[mi].max) heatRange[mi].max = v;
                    }
            heatRange.forEach(function (h) {
                if (!isFinite(h.min)) h.min = 0;
                if (!isFinite(h.max)) h.max = 0;
            });
        }

        var html = ['<div class="table-responsive" style="max-height: 540px; overflow:auto;">'];
        html.push('<table class="table table-sm table-bordered align-middle small mb-0 pivot-table">');

        // V34.3: nested headers when multi-measure. Top row = col key (spans
        // M measure columns) + "Total" (spans M). Second row = measure labels
        // under each top span. Row-dim labels rowspan=2 on the left.
        var multiM = M >= 2;
        html.push("<thead class='table-light'><tr>");
        result.rowDimensions.forEach(function (d) {
            html.push('<th' + (multiM ? ' rowspan="2"' : '') + '>' + escapeHtml(d) + "</th>");
        });
        if (ck.length === 0) {
            ms.forEach(function (m) {
                html.push("<th class='text-end'>" + escapeHtml(m.label) + "</th>");
            });
        } else {
            ck.forEach(function (k) {
                html.push("<th class='text-end' colspan='" + M + "'>" + escapeHtml(k.join(" / ")) + "</th>");
            });
            html.push("<th class='text-end fw-bold' colspan='" + M + "'>Total</th>");
        }
        html.push("</tr>");
        if (multiM && ck.length > 0) {
            html.push("<tr>");
            ck.forEach(function () {
                ms.forEach(function (m) {
                    html.push("<th class='text-end'><small>" + escapeHtml(m.label) + "</small></th>");
                });
            });
            ms.forEach(function (m) {
                html.push("<th class='text-end fw-bold'><small>" + escapeHtml(m.label) + "</small></th>");
            });
            html.push("</tr>");
        }
        html.push("</thead><tbody>");

        rk.forEach(function (rkey, ri) {
            html.push("<tr>");
            rkey.forEach(function (part) { html.push("<td>" + escapeHtml(part) + "</td>"); });
            var rkAttr = ' data-row-key="' + escapeHtml(JSON.stringify(rkey)) + '"';
            if (ck.length === 0) {
                ms.forEach(function (m, mi) {
                    var v = result.rowTotals[ri][mi];
                    var style = heatStyleFor(cfg, v, heatRange[mi]);
                    html.push('<td class="text-end heat"' + style + rkAttr + ' data-col-key="[]" title="Click to drill into this row">'
                        + formatValue(v, measureFormat(m, cfg.format)) + "</td>");
                });
            } else {
                ck.forEach(function (ckey, ci) {
                    var ckAttr = ' data-col-key="' + escapeHtml(JSON.stringify(ckey)) + '"';
                    ms.forEach(function (m, mi) {
                        var v = matrix[ri][ci][mi];
                        var style = heatStyleFor(cfg, v, heatRange[mi]);
                        html.push('<td class="text-end heat"' + style + rkAttr + ckAttr
                            + ' title="Click to drill into this cell">'
                            + (v == null ? "" : formatValue(v, measureFormat(m, cfg.format))) + "</td>");
                    });
                });
                ms.forEach(function (m, mi) {
                    html.push("<td class='text-end fw-bold'>" + formatValue(result.rowTotals[ri][mi], measureFormat(m, cfg.format)) + "</td>");
                });
            }
            html.push("</tr>");
        });
        if (ck.length > 0) {
            html.push("<tr class='table-light fw-bold'>");
            for (var i2 = 0; i2 < result.rowDimensions.length; i2++)
                html.push("<td>" + (i2 === 0 ? "Total" : "") + "</td>");
            result.colTotals.forEach(function (colT) {
                ms.forEach(function (m, mi) {
                    html.push("<td class='text-end'>" + formatValue(colT[mi], measureFormat(m, cfg.format)) + "</td>");
                });
            });
            ms.forEach(function (m, mi) {
                html.push("<td class='text-end'>" + formatValue(result.grandTotals[mi], measureFormat(m, cfg.format)) + "</td>");
            });
            html.push("</tr>");
        }
        html.push("</tbody></table></div>");
        $output.innerHTML = html.join("");
    }

    function heatStyleFor(cfg, v, range) {
        if (!cfg.heatmap || v == null || !range || range.max <= range.min) return "";
        var t = (v - range.min) / (range.max - range.min);
        return ' style="background-color: ' + heatColor(t) + ';"';
    }

    function heatColor(t) {
        // 0 -> light blue, 0.5 -> light yellow, 1 -> light red. Soft pastels so
        // the text stays readable on default Bootstrap typography.
        t = Math.max(0, Math.min(1, t));
        var stops = [
            [220, 235, 252],     // soft blue
            [255, 245, 195],     // soft yellow
            [252, 207, 195]      // soft red
        ];
        var seg = t < 0.5 ? 0 : 1;
        var k   = t < 0.5 ? (t / 0.5) : ((t - 0.5) / 0.5);
        var a = stops[seg], b = stops[seg + 1];
        var r = Math.round(a[0] + (b[0] - a[0]) * k);
        var g = Math.round(a[1] + (b[1] - a[1]) * k);
        var bl= Math.round(a[2] + (b[2] - a[2]) * k);
        return "rgb(" + r + "," + g + "," + bl + ")";
    }

    function renderPlotly(result, cfg) {
        var rk = result.rowKeys, ck = result.colKeys;
        var ms = result.measures || [];
        // V34.3: charts show one measure at a time. The picker stores the
        // measure INDEX in the result.measures array; default = 0.
        var mi = 0;
        if ($chartMeasure && $chartMeasure.value !== "") {
            var v = parseInt($chartMeasure.value, 10);
            if (!isNaN(v) && v >= 0 && v < ms.length) mi = v;
        }
        var measure  = ms[mi] || { label: "" };
        var matrix3D = buildMatrix3D(result);
        var xLabels = (ck.length > 0 ? ck : [["(total)"]]).map(function (k) { return k.join(" / "); });
        var yLabels = rk.map(function (k) { return k.join(" / "); });
        var data, layout;
        if (cfg.renderer === "Heatmap") {
            data = [{
                type: "heatmap",
                x: xLabels, y: yLabels,
                z: matrix3D.map(function (row) {
                    return row.map(function (cell) { return cell ? cell[mi] : null; });
                }),
                colorscale: "YlOrRd",
                hovertemplate: "%{y}<br>%{x}<br>" + measure.label + ": %{z:.2f}<extra></extra>"
            }];
        } else {
            var traceShape = function (ci, name) {
                var t = {
                    name: name,
                    x: yLabels.length > 0 ? yLabels : ["(all)"],
                    y: rk.map(function (_, ri) {
                        var cell = matrix3D[ri] && matrix3D[ri][ci];
                        return cell ? cell[mi] : null;
                    })
                };
                if (cfg.renderer === "Bar" || cfg.renderer === "Stacked Bar") t.type = "bar";
                else if (cfg.renderer === "Line") { t.type = "scatter"; t.mode = "lines+markers"; }
                else if (cfg.renderer === "Area") { t.type = "scatter"; t.mode = "lines"; t.fill = "tozeroy"; }
                else t.type = "bar";
                return t;
            };
            if (ck.length === 0) {
                data = [{
                    type: cfg.renderer === "Line" || cfg.renderer === "Area" ? "scatter" : "bar",
                    mode: cfg.renderer === "Line" ? "lines+markers" : (cfg.renderer === "Area" ? "lines" : undefined),
                    fill: cfg.renderer === "Area" ? "tozeroy" : undefined,
                    name: measure.label,
                    x: yLabels.length > 0 ? yLabels : ["(all)"],
                    y: rk.map(function (_, ri) { return result.rowTotals[ri][mi]; })
                }];
            } else {
                data = ck.map(function (k, ci) { return traceShape(ci, k.join(" / ")); });
            }
        }
        layout = {
            margin: { l: 80, r: 20, t: 30, b: 100 },
            title: { text: measure.label + (rk.length ? " by " + result.rowDimensions.join(" / ") : ""), font: { size: 13 } },
            barmode: cfg.renderer === "Stacked Bar" ? "stack" : "group",
            xaxis: { title: result.rowDimensions.join(" / ") || result.colDimensions.join(" / "),
                     automargin: true, tickangle: -25 },
            yaxis: { title: measure.label, automargin: true },
            legend: { orientation: "h" }
        };
        Plotly.newPlot($output, data, layout, { responsive: true, displaylogo: false });
    }

    function buildMatrix3D(result) {
        var rk = result.rowKeys.length || 1, ck = result.colKeys.length || 1;
        var M  = (result.measures || []).length;
        var m = new Array(rk);
        for (var i = 0; i < rk; i++) {
            m[i] = new Array(ck);
            for (var j = 0; j < ck; j++) m[i][j] = null;
        }
        result.cells.forEach(function (c) {
            if (c.rowIndex < rk && c.colIndex < ck) m[c.rowIndex][c.colIndex] = c.values;
        });
        return m;
    }

    function populateChartMeasurePicker(result) {
        if (!$chartMeasure) return;
        $chartMeasure.innerHTML = "";
        var ms = result.measures || [];
        ms.forEach(function (m, mi) {
            $chartMeasure.appendChild(new Option(m.label + " (" + aggLabel(m.agg) + ")", String(mi)));
        });
        var visible = ms.length > 1 && $renderer.value !== "Table";
        $chartMeasure.classList.toggle("d-none", !visible);
        $chartMeasureLabel.classList.toggle("d-none", !visible);
    }
    // Re-render the chart when the user changes the chart-measure picker
    // (no need to re-run the query -- the same result drives the new chart).
    if ($chartMeasure) {
        $chartMeasure.addEventListener("change", function () {
            if (lastResult && $renderer.value !== "Table") {
                renderResult(lastResult, readConfig());
            }
        });
    }
    $renderer.addEventListener("change", function () {
        if (lastResult) {
            populateChartMeasurePicker(lastResult);
            renderResult(lastResult, readConfig());
        }
    });

    // -- save / delete / default -----------------------------------------
    $btnSaveA.addEventListener("click", function () {
        $saveId.value = ""; $saveName.value = ""; $saveDef.checked = false;
        document.getElementById("scopePrivate").checked = true;
        bootstrap.Modal.getOrCreateInstance($modal).show();
    });
    $btnSave.addEventListener("click", function () {
        if (!current) return;
        $saveId.value    = current.id;
        $saveName.value  = current.name;
        $saveDef.checked = current.isDefault;
        var radio = document.querySelector('input[name="saveScope"][value="' + current.scope + '"]');
        if (radio && !radio.disabled) radio.checked = true;
        else document.getElementById("scopePrivate").checked = true;
        bootstrap.Modal.getOrCreateInstance($modal).show();
    });
    $btnConfirm.addEventListener("click", function () {
        var name = $saveName.value.trim();
        if (!name) { alert("Name is required."); return; }
        var scope = (document.querySelector('input[name="saveScope"]:checked') || {}).value || "private";
        var idVal = $saveId.value ? parseInt($saveId.value, 10) : null;
        var cfg = readConfig();
        var body = {
            Id:         idVal,
            ReportKey:  REPORT,
            Name:       name,
            Scope:      scope,
            IsDefault:  $saveDef.checked,
            ConfigJson: JSON.stringify(cfg)
        };
        fetch("/Reports/SavePerspective", {
            method:  "POST",
            headers: jsonHeaders(),
            body:    JSON.stringify(body)
        })
        .then(parseResponse)
        .then(function (dto) {
            bootstrap.Modal.getOrCreateInstance($modal).hide();
            return listPerspectives().then(function () {
                $select.value = String(dto.id);
                current = perspectives.find(function (p) { return p.id === dto.id; }) || dto;
                updateButtonState();
            });
        })
        .catch(function (e) { alert("Save failed: " + e.message); });
    });
    $btnDef.addEventListener("click", function () {
        if (!current) return;
        var body = new URLSearchParams();
        body.append("id", current.id);
        body.append("__RequestVerificationToken", antiForgeryToken());
        fetch("/Reports/SetDefaultPerspective", {
            method:  "POST",
            headers: formHeaders(),
            body:    body
        })
        .then(parseResponse)
        .then(function () { return listPerspectives(); })
        .catch(function (e) { alert("Set-default failed: " + e.message); });
    });
    $btnDel.addEventListener("click", function () {
        if (!current) return;
        if (!confirm("Delete perspective \"" + current.name + "\"?")) return;
        var body = new URLSearchParams();
        body.append("id", current.id);
        body.append("__RequestVerificationToken", antiForgeryToken());
        fetch("/Reports/DeletePerspective", {
            method:  "POST",
            headers: formHeaders(),
            body:    body
        })
        .then(parseResponse)
        .then(function () {
            current = null; $select.value = ""; updateButtonState();
            return listPerspectives();
        })
        .catch(function (e) { alert("Delete failed: " + e.message); });
    });

    // -- export pivot to Excel (XLSX via server) ------------------------
    // V34.3: replaces the client-side CSV blob with a server-side ClosedXML
    // build that produces a polished workbook (title row, scope/drill summary,
    // merged headers for multi-measure, bold + frozen header, per-measure
    // number formats, auto-width, totals row).
    $btnExport.addEventListener("click", function () {
        if (!lastReqBody) return;
        $btnExport.disabled = true;
        var oldHtml = $btnExport.innerHTML;
        $btnExport.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span>Building…';
        fetch("/Reports/PivotExcel", {
            method:  "POST",
            headers: jsonHeaders(),
            body:    JSON.stringify(lastReqBody)
        })
        .then(function (r) {
            if (!r.ok) {
                return r.text().then(function (txt) {
                    var msg = txt; try { msg = JSON.parse(txt).error || txt; } catch (e) {}
                    throw new Error(msg || ("HTTP " + r.status));
                });
            }
            return r.blob();
        })
        .then(function (blob) {
            var url = URL.createObjectURL(blob);
            var a   = document.createElement("a");
            var stamp = new Date().toISOString().replace(/[-:T]/g, "").substring(0, 13);
            a.href = url; a.download = "pivot-" + REPORT + "-" + stamp + ".xlsx";
            document.body.appendChild(a); a.click();
            setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 0);
        })
        .catch(function (e) { alert("Excel export failed: " + e.message); })
        .finally(function () {
            $btnExport.disabled = false;
            $btnExport.innerHTML = oldHtml;
        });
    });

    // -- helpers ---------------------------------------------------------
    function escapeHtml(s) {
        if (s == null) return "";
        return String(s).replace(/[&<>"']/g, function (c) {
            return { "&":"&amp;", "<":"&lt;", ">":"&gt;", '"':"&quot;", "'":"&#39;" }[c];
        });
    }
    function showMessage(text, kind) {
        $msg.className = "alert alert-" + (kind || "info") + " py-2 small mb-2";
        $msg.textContent = text;
        $msg.classList.remove("d-none");
    }
    function hideMessage() { $msg.classList.add("d-none"); }

    // -- drill chips + picker --------------------------------------------
    // -- values chip zone + measure picker (V34.3) -----------------------
    function renderValuesZone() {
        $valuesZone.innerHTML = "";
        if (measuresSel.length === 0) {
            var empty = document.createElement("div");
            empty.className = "text-muted small p-2";
            empty.textContent = "No measure yet. Click + Add measure to pick what to aggregate.";
            $valuesZone.appendChild(empty);
            return;
        }
        measuresSel.forEach(function (m, idx) {
            var chip = document.createElement("span");
            chip.className = "value-chip";
            chip.setAttribute("draggable", "true");
            chip.setAttribute("data-idx", String(idx));
            chip.setAttribute("title",
                m.label
                    ? (m.label + "  (" + aggLabel(m.agg) + " of " + m.display + ")")
                    : (aggLabel(m.agg) + " of " + m.display)
            );
            chip.innerHTML =
                '<span class="badge text-bg-success me-1" style="font-size: .7rem;">' + escapeHtml(m.agg) + '</span>' +
                '<strong>' + escapeHtml(m.label || m.display) + '</strong>' +
                (m.label ? ' <small class="text-muted">' + escapeHtml(m.display) + '</small>' : '') +
                (m.format ? ' <small class="text-muted">[' + escapeHtml(m.format) + ']</small>' : '') +
                ' <span class="chip-x" title="Remove this measure">&times;</span>';
            chip.addEventListener("click", function (ev) {
                if (ev.target.classList.contains("chip-x")) {
                    measuresSel.splice(idx, 1);
                    renderValuesZone();
                    return;
                }
                openMeasurePicker(idx);
            });
            chip.addEventListener("dragstart", onValueChipDragStart);
            chip.addEventListener("dragend",   onValueChipDragEnd);
            $valuesZone.appendChild(chip);
        });
    }
    var valueDragState = null;
    function onValueChipDragStart(ev) {
        valueDragState = parseInt(ev.currentTarget.getAttribute("data-idx"), 10);
        ev.currentTarget.classList.add("dragging");
        try { ev.dataTransfer.setData("text/plain", "m"); } catch (e) {}
        ev.dataTransfer.effectAllowed = "move";
    }
    function onValueChipDragEnd(ev) {
        ev.currentTarget.classList.remove("dragging");
        valueDragState = null;
        $valuesZone.classList.remove("drag-over");
    }
    $valuesZone.addEventListener("dragover", function (ev) {
        if (valueDragState == null) return;
        ev.preventDefault();
        $valuesZone.classList.add("drag-over");
        ev.dataTransfer.dropEffect = "move";
    });
    $valuesZone.addEventListener("drop", function (ev) {
        if (valueDragState == null) return;
        ev.preventDefault();
        $valuesZone.classList.remove("drag-over");
        var fromIdx = valueDragState;
        // Find insertion index from horizontal cursor relative to chips.
        var insertIdx = measuresSel.length;
        var chips = Array.prototype.slice.call($valuesZone.querySelectorAll(".value-chip"));
        for (var i = 0; i < chips.length; i++) {
            var r = chips[i].getBoundingClientRect();
            if (ev.clientX < (r.left + r.width / 2)) { insertIdx = i; break; }
        }
        if (insertIdx > fromIdx) insertIdx -= 1;
        var moved = measuresSel.splice(fromIdx, 1)[0];
        measuresSel.splice(insertIdx, 0, moved);
        renderValuesZone();
    });

    function openMeasurePicker(editIdx) {
        $measureSelect.innerHTML = "";
        (schema && schema.measures || []).forEach(function (m) {
            $measureSelect.appendChild(new Option(m.display, m.key));
        });
        if (editIdx != null && measuresSel[editIdx]) {
            var existing = measuresSel[editIdx];
            $measureSelect.value = existing.key;
            populateMeasureAgg(existing.key, existing.agg);
            $measureFormat.value = existing.format || "";
            $measureLabelInput.value = existing.label || "";
            $measureModalTitle.textContent = "Edit measure";
            $measureEditingIdx.value = String(editIdx);
        } else {
            populateMeasureAgg($measureSelect.value, null);
            $measureFormat.value = "";
            $measureLabelInput.value = "";
            $measureModalTitle.textContent = "Add measure";
            $measureEditingIdx.value = "";
        }
        bootstrap.Modal.getOrCreateInstance($measureModal).show();
    }
    function populateMeasureAgg(measureKey, preferred) {
        $measureAgg.innerHTML = "";
        (measureAggs[measureKey] || []).forEach(function (a) {
            $measureAgg.appendChild(new Option(aggLabel(a), a));
        });
        if (preferred) $measureAgg.value = preferred;
    }
    $measureSelect.addEventListener("change", function () {
        populateMeasureAgg($measureSelect.value, null);
    });
    $btnAddMeasure.addEventListener("click", function () { openMeasurePicker(null); });
    $btnApplyMeasure.addEventListener("click", function () {
        var key = $measureSelect.value;
        var def = measuresByKey[key];
        if (!def) { alert("Pick a measure."); return; }
        var agg = $measureAgg.value;
        if (!agg) { alert("Pick an aggregation."); return; }
        var entry = {
            key: def.key, display: def.display,
            agg: agg,
            format: $measureFormat.value || null,
            label:  $measureLabelInput.value.trim() || null
        };
        var idxStr = $measureEditingIdx.value;
        if (idxStr !== "" && measuresSel[parseInt(idxStr, 10)]) {
            measuresSel[parseInt(idxStr, 10)] = entry;
        } else {
            measuresSel.push(entry);
        }
        renderValuesZone();
        bootstrap.Modal.getOrCreateInstance($measureModal).hide();
    });

    function renderDrillZone() {
        $drillZone.innerHTML = "";
        if (drills.length === 0) {
            var empty = document.createElement("div");
            empty.className = "text-muted small p-2";
            empty.textContent = "No drill filter yet. Click + Add filter, or click a cell in the result table.";
            $drillZone.appendChild(empty);
            return;
        }
        drills.forEach(function (d) {
            var label = (dimByKey[d.key] && dimByKey[d.key].display) || d.key;
            // Render a clear count badge + a short preview. The full picked list
            // goes into the native browser tooltip so the user can verify what's
            // actually constraining the pivot without re-opening the modal.
            var n = d.values.length;
            var previewItems = d.values.slice(0, 2).map(function (v) {
                return v === "(null)" ? "(empty)" : v;
            });
            var preview = previewItems.map(escapeHtml).join(", ");
            var moreText = n > 2 ? ' <span class="text-muted">+ ' + (n - 2) + ' more</span>' : '';
            var fullTip = "Drill filter — pivot limited to these " + n + " "
                + label + " value" + (n === 1 ? "" : "s") + ":\n"
                + d.values.map(function (v) { return "  • " + (v === "(null)" ? "(empty)" : v); }).join("\n")
                + "\n\nClick to edit.";
            var chip = document.createElement("span");
            chip.className = "drill-chip";
            chip.setAttribute("data-key", d.key);
            chip.setAttribute("title", fullTip);
            chip.innerHTML =
                '<span class="badge text-bg-primary me-1" style="font-size: .75rem;">' + n + '</span>' +
                '<strong>' + escapeHtml(label) + '</strong>: ' +
                preview + moreText +
                ' <span class="chip-x" title="Remove this filter">&times;</span>';
            chip.addEventListener("click", function (ev) {
                if (ev.target.classList.contains("chip-x")) {
                    drills = drills.filter(function (x) { return x.key !== d.key; });
                    renderDrillZone();
                    runPivot();      // re-run after removing so the user sees the change immediately
                    return;
                }
                openDrillPicker(d.key, d.values);
            });
            $drillZone.appendChild(chip);
        });
    }

    var drillFetchCounter = 0; // race guard
    var drillSearchDebounce = null;
    function openDrillPicker(presetDimKey, presetValues) {
        // Populate dim dropdown with every registered dim.
        $drillDimSelect.innerHTML = "";
        (schema && schema.dimensions || []).forEach(function (d) {
            $drillDimSelect.appendChild(new Option(d.display, d.key));
        });
        var dimKey = presetDimKey || $drillDimSelect.value;
        $drillDimSelect.value = dimKey;
        $drillEditingKey.value = presetDimKey || "";
        $drillModalTitle.textContent = presetDimKey ? "Edit drill filter" : "Add drill filter";
        $drillValueSearch.value = "";
        $drillValuesTrunc.classList.add("d-none");
        var preSelected = {};
        (presetValues || []).forEach(function (v) { preSelected[v] = true; });
        loadValuesIntoModal(dimKey, "", preSelected);
        var modal = bootstrap.Modal.getOrCreateInstance($drillModal);
        modal.show();
    }
    function loadValuesIntoModal(dimKey, search, preSelected) {
        $drillValuesLoading.classList.remove("d-none");
        $drillValuesList.innerHTML = "";
        var token = ++drillFetchCounter;
        var qs = "report=" + encodeURIComponent(REPORT)
            + "&dim=" + encodeURIComponent(dimKey)
            + "&ignorePageFilter=" + (ignorePage ? "true" : "false");
        if (search) qs += "&q=" + encodeURIComponent(search);
        // Pipe the page filter into the request so the dropdown matches the
        // current pivot scope.
        var f = activeScopeFilterPayload();
        Object.keys(f).forEach(function (k) {
            qs += "&" + encodeURIComponent(k) + "=" + encodeURIComponent(f[k]);
        });
        fetch("/Reports/PivotValues?" + qs, {
            headers: { "X-Requested-With": "XMLHttpRequest" }
        })
        .then(parseResponse)
        .then(function (data) {
            if (token !== drillFetchCounter) return; // stale
            $drillValuesLoading.classList.add("d-none");
            if (data.truncated) $drillValuesTrunc.classList.remove("d-none");
            else $drillValuesTrunc.classList.add("d-none");
            var values = data.values || [];
            if (values.length === 0) {
                $drillValuesList.innerHTML = '<div class="text-muted small py-2">No values found for this dimension in the current scope.</div>';
                return;
            }
            var html = values.map(function (v) {
                var label = v === "(null)" ? "<em>(empty)</em>" : escapeHtml(v);
                var checked = preSelected[v] ? "checked" : "";
                return '<div class="form-check">' +
                    '<input class="form-check-input drill-val" type="checkbox" value="' + escapeHtml(v) + '" ' + checked + '>' +
                    '<label class="form-check-label">' + label + '</label></div>';
            });
            $drillValuesList.innerHTML = html.join("");
        })
        .catch(function (e) {
            if (token !== drillFetchCounter) return;
            $drillValuesLoading.classList.add("d-none");
            $drillValuesList.innerHTML = '<div class="text-danger small py-2">' + escapeHtml(e.message) + '</div>';
        });
    }
    $drillDimSelect.addEventListener("change", function () {
        $drillValueSearch.value = "";
        loadValuesIntoModal($drillDimSelect.value, "", {});
    });
    $drillValueSearch.addEventListener("input", function () {
        clearTimeout(drillSearchDebounce);
        var v = $drillValueSearch.value;
        // Preserve current selection across reloads so typing doesn't drop ticks.
        var existing = {};
        $drillValuesList.querySelectorAll("input.drill-val:checked").forEach(function (i) { existing[i.value] = true; });
        drillSearchDebounce = setTimeout(function () {
            loadValuesIntoModal($drillDimSelect.value, v, existing);
        }, 220);
    });
    $drillSelectAll.addEventListener("click", function () {
        $drillValuesList.querySelectorAll("input.drill-val").forEach(function (i) { i.checked = true; });
    });
    $drillClearAll.addEventListener("click", function () {
        $drillValuesList.querySelectorAll("input.drill-val").forEach(function (i) { i.checked = false; });
    });
    $btnApplyDrill.addEventListener("click", function () {
        var dimKey = $drillDimSelect.value;
        var values = [];
        $drillValuesList.querySelectorAll("input.drill-val:checked").forEach(function (i) { values.push(i.value); });
        if (values.length === 0) {
            // Empty selection = remove the drill (if editing) or no-op (if adding).
            drills = drills.filter(function (x) { return x.key !== dimKey; });
        } else {
            // Replace any existing drill for this dim.
            drills = drills.filter(function (x) { return x.key !== dimKey; });
            drills.push({ key: dimKey, values: values });
        }
        renderDrillZone();
        bootstrap.Modal.getOrCreateInstance($drillModal).hide();
        runPivot();
    });
    $btnAddDrill.addEventListener("click", function () { openDrillPicker(null, []); });

    // -- click-to-drill on table cells -----------------------------------
    // We add a single delegated listener on the output container; the table
    // renderer tags each value cell with data-row-key + data-col-key JSON.
    $output.addEventListener("click", function (ev) {
        var td = ev.target.closest("td[data-row-key]");
        if (!td || !lastResult) return;
        var rowKey, colKey;
        try {
            rowKey = JSON.parse(td.getAttribute("data-row-key") || "[]");
            colKey = JSON.parse(td.getAttribute("data-col-key") || "[]");
        } catch (e) { return; }
        var rowDims = (lastResult.rowDimensions || []).map(function (d) { return d; });
        var colDims = (lastResult.colDimensions || []).map(function (d) { return d; });
        // Map display names back to keys via the rowsSel / colsSel arrays
        // (same order the SELECT emitted).
        var added = 0;
        function applyOne(dimKeyList, idx, val) {
            if (!dimKeyList[idx]) return;
            var key = dimKeyList[idx].key;
            // Replace existing drill for this dim with the single picked value.
            drills = drills.filter(function (x) { return x.key !== key; });
            drills.push({ key: key, values: [val] });
            added++;
        }
        rowKey.forEach(function (v, i) { applyOne(rowsSel, i, v); });
        colKey.forEach(function (v, i) { applyOne(colsSel, i, v); });
        if (added > 0) {
            renderDrillZone();
            runPivot();
        }
    });

    // -- bootstrap -------------------------------------------------------
    setScope("page");
    renderDrillZone();
    loadSchema()
        .then(listPerspectives)
        .then(function () {
            var dflt = perspectives.find(function (p) { return p.isDefault && p.isOwn; });
            if (dflt) {
                $select.value = String(dflt.id);
                current = dflt;
                applyConfig(dflt.configJson);
                updateButtonState();
                runPivot();
            }
        })
        .catch(function (e) {
            showMessage("Could not initialise analyzer: " + e.message, "danger");
        });
})();
