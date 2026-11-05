// Reports -> Defect reports. Pivots the dataset from /DefectReports/Data in the
// browser: six tabs, shared multi-select filters, charts, week-on-week trend
// arrows and a drill panel listing the orders behind any number. Each tab is
// built as a TABLE MODEL first; the on-screen table, the chart and the Excel /
// PDF exports are all drawn from that one model, so they cannot disagree.
// The drill-down workbook is rebuilt on the server by DefectReportService.
// DrillAsync with the same rules -- keep the two in step.
(function () {
  "use strict";
  const root = document.getElementById("dr");
  if (!root) return;
  const URL_DATA = root.dataset.urlData, URL_DRILL = root.dataset.urlDrill, URL_PENDING = root.dataset.urlPending, URL_EXPORT = root.dataset.urlExport;
  const URL_QO = root.dataset.urlQo.replace(/\/0$/, "/"), URL_ARR = root.dataset.urlArrival.replace(/\/0$/, "/");
  const MAX_DAYS = parseInt(root.dataset.maxDays || "366", 10);
  const TOKEN = (document.querySelector("#drToken input[name=__RequestVerificationToken]") || {}).value || "";
  const plantName = c => { const n = D && D.plantNames && D.plantNames[c]; return n && n !== c ? `${n} (${c})` : (c || "(none)"); };
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? "").replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]);
  const store = { get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} } };
  const SEP_CLASS = "\u0002", SEP_SUBWEEK = "\u0003", CELL = "\u0001";

  // ---------- model (rebuilt on every load) --------------------------------
  let D, cats = [], catColor = {}, defs = [], units = [], factsBy = [], groupInfo = {}, nameOrder = {}, pending = [];
  const catInk = hex => { const n = parseInt((hex || "#6c757d").slice(1), 16), r = n >> 16, g = (n >> 8) & 255, b = n & 255; return (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? "#212529" : "#ffffff"; };
  function build(data) {
    D = data;
    cats = D.categories.map(c => c.nm);
    catColor = Object.fromEntries(D.categories.map(c => [c.nm, c.col || "#6c757d"]));
    defs = D.defects.map(([id, nm, ct, g, o]) => ({ id, nm, ct, g, o }));
    units = D.units.map(([q, n, p, v, mj, sm, c, r, k, g, ns, sz, pw, ctn, uom, vr], i) => ({
      i, q, n, p: D.plants[p], v: D.suppliers[v], mj: D.majors[mj], sm: D.subs[sm], c: D.classes[c], vr: D.varieties[vr], r, k, g: D.groups[g],
      ns, sz: +sz, pw: D.packWeights[pw], ctn: +ctn, uom: D.uoms[uom], wk: weekStart(r)
    }));
    factsBy = units.map(() => []);
    for (const [u, d, v, pc] of D.facts) factsBy[u].push([d, +v, +pc]);
    groupInfo = {};
    for (const g of D.groups) {
      const byName = {}, catCount = {};
      for (const i of (D.groupDefects[g] || [])) { const d = defs[i]; byName[d.nm] = d.ct; catCount[d.ct] = (catCount[d.ct] || 0) + 1; }
      groupInfo[g] = { byName, catCount };
    }
    nameOrder = {};
    for (const d of defs) { const k = d.ct + "|" + d.nm; if (!(k in nameOrder) || d.o < nameOrder[k]) nameOrder[k] = d.o; }
  }

  // ---------- weeks: ISO 8601, Monday to Sunday ------------------------------
  function weekStart(iso) { const d = new Date(iso + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() - (d.getUTCDay() + 6) % 7); return d.toISOString().slice(0, 10); }
  function isoWeek(iso) {
    const d = new Date(iso + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + 3 - (d.getUTCDay() + 6) % 7);
    const jan4 = new Date(Date.UTC(d.getUTCFullYear(), 0, 4));
    return 1 + Math.round(((d - jan4) / 86400000 - 3 + (jan4.getUTCDay() + 6) % 7) / 7);
  }
  const MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const dm = iso => { const d = new Date(iso + "T00:00:00Z"); return `${d.getUTCDate()} ${MON[d.getUTCMonth()]}`; };
  const addDays = (iso, n) => { const d = new Date(iso + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
  const weekLabel = w => `W${isoWeek(w)}`;
  const weekRange = w => `Week ${isoWeek(w)}: Monday ${dm(w)} to Sunday ${dm(addDays(w, 6))}`;

  // ---------- state --------------------------------------------------------
  // Every filter holds a list of chosen values; empty means no filter.
  const S = { plant: [], supplier: [], major: [], sub: [], cls: [], pw: [], cat: [], hide: true, tab: "week", bySub: true };
  const expanded = new Set();
  const TABS = [
    { id: "week",   label: "Per week",            title: "Defects per week", hint: "Defects down the side, ISO weeks (Monday to Sunday) across. Arrows compare each week with the week before." },
    { id: "class",  label: "Per defect & class",  title: "Defects per sub-major category and class", hint: "Each sub-major category with its classes underneath." },
    { id: "branch", label: "Per branch & defect", title: "Defects per sub-major category and branch", hint: "One row per sub-major category; open it to compare the receiving branches underneath." },
    { id: "supplier", label: "Per supplier comparison", title: "Supplier comparison per sub-major category and defect", hint: "Suppliers side by side, read down each column. Open a sub-major category to see its defects." },
    { id: "sub",    label: "Per sub-major",       title: "Defect categories per sub-major category and variety", hint: "Category totals for every sub-major category; open it to see its varieties." },
    { id: "major",  label: "Per major",           title: "Defect categories per major category", hint: "Category totals for every major category." }
  ];
  const needsFruit = t => t === "week" || t === "class" || t === "branch" || t === "supplier";
  const SUPPLIER_COLS = 15, TREND_CHARTS = 12;

  // ---------- filters ------------------------------------------------------
  const FILTERS = [
    { id: "fPlant", key: "p", s: "plant", all: "All branches", label: plantName, name: "Branch" },
    { id: "fSupplier", key: "v", s: "supplier", all: "All suppliers", name: "Supplier" },
    { id: "fMajor", key: "mj", s: "major", all: "All major categories", name: "Major category" },
    { id: "fSub", key: "sm", s: "sub", all: "All sub-major categories", name: "Sub-major category" },
    { id: "fClass", key: "c", s: "cls", all: "All classes", name: "Class" },
    { id: "fWeight", key: "pw", s: "pw", all: "All pack weights", name: "Pack weight", order: () => D.packWeights }
  ];
  function passes(u, skip) {
    for (const f of FILTERS) if (S[f.s].length && skip !== f.s && !S[f.s].includes(u[f.key])) return false;
    return true;
  }
  const CAT_FILTER = { id: "fCat", s: "cat", all: "All defect categories", name: "Defect category" };
  const MS = [...FILTERS, CAT_FILTER];
  const msText = f => v => f.label ? f.label(v) : v;
  function msSummary(f) {
    const btn = $(f.id + "-btn"), sel = S[f.s], text = msText(f);
    btn.textContent = !sel.length ? f.all : sel.length === 1 ? text(sel[0]) : `${sel.length} selected`;
    btn.title = sel.map(text).join(", ") || f.all;
  }
  function msMenu(f, values) {
    const box = $(f.id), menu = box.querySelector(".dropdown-menu"), text = msText(f);
    menu.innerHTML = `<div class="d-flex justify-content-between align-items-center mb-1"><span class="small text-muted">${esc(f.name)}</span><button type="button" class="btn btn-sm btn-link p-0" data-ms-clear>Clear</button></div>`
      + (values.length > 8 ? `<input type="search" class="form-control form-control-sm my-1" data-ms-search placeholder="Search…" aria-label="Search ${esc(f.name)}" autocomplete="off">` : "")
      + values.map((v, i) => `<div class="form-check" data-ms-row><input class="form-check-input" type="checkbox" id="${f.id}-${i}" value="${esc(v)}"${S[f.s].includes(v) ? " checked" : ""}><label class="form-check-label small" for="${f.id}-${i}">${esc(text(v))}</label></div>`).join("")
      + (values.length ? "" : `<div class="small text-muted fst-italic">Nothing to choose</div>`);
  }
  function refreshOptions() {
    // Each dropdown lists only what the OTHER filters leave, so it never offers an empty page.
    // A menu that is open is left alone: rebuilding it under the pointer would
    // lose the search text and the scroll position while someone is ticking.
    for (const f of MS) {
      const box = $(f.id);
      if (!box.firstChild) box.innerHTML = `<button type="button" id="${f.id}-btn" class="btn btn-sm btn-outline-secondary dropdown-toggle w-100 text-start text-truncate" data-bs-toggle="dropdown" data-bs-auto-close="outside" aria-expanded="false"></button><div class="dropdown-menu p-2 dr-ms-menu"></div>`;
      let values;
      if (f === CAT_FILTER) values = cats;
      else {
        const have = new Set(units.filter(u => passes(u, f.s)).map(u => u[f.key]).filter(Boolean));
        values = f.order ? f.order().filter(v => have.has(v)) : [...have].sort((a, b) => a.localeCompare(b));
      }
      S[f.s] = S[f.s].filter(v => values.includes(v));
      if (!box.querySelector(".dropdown-menu.show")) msMenu(f, values);
      msSummary(f);
    }
  }
  function filterText() {
    const bits = MS.filter(f => S[f.s].length).map(f => `${f.name}: ${S[f.s].map(msText(f)).join(", ")}`);
    return bits.length ? bits : ["No filters (everything in the period)"];
  }
  const catOk = c => !S.cat.length || S.cat.includes(c);

  // ---------- engine -------------------------------------------------------
  // A cell is (row key, defect key). Defect keys: "N:<name>", "C:<category>", "T".
  // [defective units, inspected units]; the figure is 100 x units / inspected.
  function compute(unitKeys) {
    const cells = new Map(), used = new Set(), rowsSeen = new Set(), cartons = new Map();
    const add = (rk, dk, a, b) => { const key = rk + CELL + dk; let x = cells.get(key); if (!x) { x = [0, 0]; cells.set(key, x); } x[0] += a; x[1] += b; };
    let n = 0, samples = 0, ctn = 0; const orders = new Set(), uom = {};
    for (const u of units) {
      if (!passes(u)) continue;
      const gi = groupInfo[u.g]; if (!gi) continue;
      n++; orders.add(u.q); samples += u.ns; ctn += u.ctn; uom[u.uom || "?"] = (uom[u.uom || "?"] || 0) + u.ctn;
      for (const rk of unitKeys(u)) {
        rowsSeen.add(rk); cartons.set(rk, (cartons.get(rk) || 0) + u.ctn);
        let any = false;
        for (const [nm, ct] of Object.entries(gi.byName)) if (catOk(ct)) add(rk, "N:" + nm, 0, u.sz);
        for (const ct of Object.keys(gi.catCount)) if (catOk(ct)) { add(rk, "C:" + ct, 0, u.sz); any = true; }
        if (any) add(rk, "T", 0, u.sz);
        for (const [d, v] of factsBy[u.i]) {
          const df = defs[d]; if (!catOk(df.ct)) continue;
          add(rk, "N:" + df.nm, v, 0); add(rk, "C:" + df.ct, v, 0); add(rk, "T", v, 0);
          if (v > 0) used.add(df.ct + "|" + df.nm);
        }
      }
    }
    return { cells, used, cartons, rows: [...rowsSeen], n, orders: orders.size, samples, ctn, uom };
  }
  const val = (res, rk, dk) => { const x = res.cells.get(rk + CELL + dk); return !x || x[1] <= 0 ? null : 100 * x[0] / x[1]; };

  function defectAxis(res) {
    const present = new Map();
    for (const key of res.cells.keys()) {
      const dk = key.split(CELL)[1]; if (!dk.startsWith("N:")) continue;
      const nm = dk.slice(2);
      for (const ct of cats) if (nameOrder[ct + "|" + nm] !== undefined) {
        if (!catOk(ct)) continue;
        if (S.hide && !res.used.has(ct + "|" + nm)) continue;
        if (!present.has(ct)) present.set(ct, new Set());
        present.get(ct).add(nm);
      }
    }
    return cats.filter(c => present.has(c)).map(c => ({ cat: c, names: [...present.get(c)].sort((a, b) => (nameOrder[c + "|" + a] - nameOrder[c + "|" + b]) || a.localeCompare(b)) }));
  }
  function catAxis(res) {
    const have = new Set();
    for (const key of res.cells.keys()) { const dk = key.split(CELL)[1]; if (dk.startsWith("C:")) have.add(dk.slice(2)); }
    return cats.filter(c => have.has(c) && catOk(c));
  }
  // The latest week in the filtered data and the one before it, for the trend.
  function lastTwoWeeks() {
    const ws = [...new Set(units.filter(u => passes(u)).map(u => u.wk))].sort();
    return ws.length >= 2 ? [ws[ws.length - 2], ws[ws.length - 1]] : null;
  }
  const trendOf = (now, before) => (now === null || before === null) ? null : now - before;

  // ---------- table models -------------------------------------------------
  // columns: [{ group, color, label, kind }]
  // rows:    [{ label, html?, level: ""|"grp"|"child"|"gt", gid?, parent?, cells: [{ v, rk, dk, trend, scale, kind }] }]
  function modelUnitRows(res, rows, rowLabel, withDefects, rowInfo, trendWeeks) {
    const axis = withDefects ? defectAxis(res) : catAxis(res).map(c => ({ cat: c, names: [] }));
    if (!axis.length) return null;
    const columns = [];
    for (const g of axis) {
      for (const nm of g.names) columns.push({ group: g.cat, color: catColor[g.cat], label: nm, kind: "defect", dk: "N:" + nm });
      columns.push({ group: g.cat, color: catColor[g.cat], label: withDefects ? `${g.cat} total` : g.cat, kind: withDefects ? "cattot" : "cat", dk: "C:" + g.cat });
    }
    columns.push({ group: "", label: "All defects", kind: "all", dk: "T" });
    columns.push({ group: "", label: "Cartons (PO qty)", kind: "cartons" });
    if (trendWeeks) columns.push({ group: "", label: `${weekLabel(trendWeeks[1])} vs ${weekLabel(trendWeeks[0])}`, kind: "trend" });
    const out = [];
    for (const rk of rows) {
      const info = rowInfo(rk);
      const cells = columns.map(c => {
        if (c.kind === "cartons") return { v: res.cartons.get(rk) ?? null, kind: "cartons" };
        if (c.kind === "trend") {
          const now = val(res, rk + SEP_SUBWEEK + trendWeeks[1], "T"), before = val(res, rk + SEP_SUBWEEK + trendWeeks[0], "T");
          return { v: trendOf(now, before), kind: "trend", now, before };
        }
        return { v: val(res, rk, c.dk), rk, dk: c.dk, kind: c.kind };
      });
      out.push({ ...info, rk, cells });
    }
    return { columns, rows: out, rowLabel, heat: true };
  }
  function modelWeek(res, weeks) {
    const axis = defectAxis(res);
    if (!axis.length) return null;
    const columns = weeks.map(w => ({ group: "", label: weekLabel(w), title: weekRange(w), kind: "week", wk: w }))
      .concat([{ group: "", label: "Whole period", kind: "all", wk: "__all" }]);
    const { line, block, cartonsRow } = acrossHelpers(res, columns, axis);
    const rows = [];
    rows.push(cartonsRow(""));
    rows.push(...block("", null, null));
    rows.push(line("All defects", null, "", "T", "gt"));
    if (S.bySub && S.sub.length !== 1) {
      const subs = [...new Set(res.rows.filter(r => r.includes(SEP_SUBWEEK)).map(r => r.split(SEP_SUBWEEK)[0]))].sort((a, b) => a.localeCompare(b));
      for (const sm of subs) {
        const gid = "s:" + sm, prefix = sm + SEP_SUBWEEK;
        rows.push(line(sm || "(no sub-major)", null, prefix, "T", "sub", { gid }));
        rows.push(...block(prefix, gid, gid));
      }
    }
    return { columns, rows, rowLabel: "Defect", heat: true, week: true };
  }
  // Tables with defects down the side and one thing per column: weeks, suppliers.
  function acrossHelpers(res, columns, axis) {
    const cartonsRow = prefix => ({ label: "Cartons (PO qty)", level: "info", cells: columns.map(c => ({ v: res.cartons.get(prefix + c.wk) ?? null, kind: "cartons" })) });
    const line = (label, html, prefix, dk, level, extra) => {
      const cells = columns.map((c, i) => {
        const v = val(res, prefix + c.wk, dk);
        const prev = i > 0 && c.kind === "week" ? val(res, prefix + columns[i - 1].wk, dk) : null;
        return { v, rk: prefix + c.wk, dk, kind: c.kind === "all" ? "all" : "week", trend: c.kind === "week" && i > 0 ? trendOf(v, prev) : null };
      });
      return { label, html, level, cells, ...extra };
    };
    const block = (prefix, gid, parent) => {
      const out = [];
      for (const g of axis) {
        out.push(line(`${g.cat} total`, `<span class="dr-cat" style="${catStyle(g.cat)}">${esc(g.cat)}</span> total`, prefix, "C:" + g.cat, parent ? "child" : "grp", { parent }));
        for (const nm of g.names) out.push(line(nm, null, prefix, "N:" + nm, "child", { parent, indent: parent ? 2 : 1 }));
      }
      return out;
    };
    return { line, block, cartonsRow };
  }
  // Supplier comparison: sub-major categories down the side with their defects
  // underneath, one column per supplier. Keys: "<sub-major><SEP><supplier or __all>".
  function modelSupplier(res, suppliers) {
    const axis = defectAxis(res);
    if (!axis.length) return null;
    const columns = suppliers.map(v => ({ group: "", label: v, title: v, kind: "supplier", wk: v }))
      .concat([{ group: "", label: "All suppliers", kind: "all", wk: "__all" }]);
    const { line, block, cartonsRow } = acrossHelpers(res, columns, axis);
    const rows = [cartonsRow("")];
    const subs = [...new Set(res.rows.filter(r => r.includes(SEP_SUBWEEK)).map(r => r.split(SEP_SUBWEEK)[0]))].sort((a, b) => a.localeCompare(b));
    for (const sm of subs) {
      const gid = "v:" + sm, prefix = sm + SEP_SUBWEEK;
      rows.push(line(sm || "(no sub-major)", null, prefix, "T", "sub", { gid }));
      rows.push(...block(prefix, gid, gid));
    }
    rows.push(line("All defects", null, "", "T", "gt"));
    return { columns, rows, rowLabel: "Sub-major / defect", heat: true, supplier: true };
  }

  // ---------- rendering ----------------------------------------------------
  const fmt = v => v === null || v === undefined ? "" : v.toFixed(2);
  const fmtInt = v => v === null || v === undefined ? "" : Math.round(v).toLocaleString();
  const catStyle = c => `--cat:${catColor[c]};--cat-ink:${catInk(catColor[c])}`;
  let drillCtx = {}, current = null, classOpened = false;
  function arrow(t, title) {
    if (t === null || t === undefined || Math.abs(t) < 0.005) return t === null || t === undefined ? "" : `<span class="tr flat" title="${esc(title || "No change")}">▬</span>`;
    return t > 0 ? `<span class="tr up" title="${esc(title || "")}">▲</span>` : `<span class="tr down" title="${esc(title || "")}">▼</span>`;
  }
  function renderTable(m) {
    const leafCols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "defect" || x.c.kind === "cat" || x.c.kind === "week" || x.c.kind === "supplier");
    let max = 0;
    for (const r of m.rows) if (r.level !== "gt" && r.level !== "info") for (const x of leafCols) { const v = r.cells[x.i].v; if (v !== null && v > max) max = v; }
    const grouped = m.columns.some(c => c.kind === "defect");
    let h1 = `<tr><th class="rowh" rowspan="${grouped ? 2 : 1}">${esc(m.rowLabel)}</th>`, h2 = "<tr>";
    for (let i = 0; i < m.columns.length; i++) {
      const c = m.columns[i];
      if (grouped) {
        if (c.group) {
          if (i === 0 || m.columns[i - 1].group !== c.group) {
            const span = m.columns.filter(x => x.group === c.group).length;
            h1 += `<th class="cat" colspan="${span}" style="${catStyle(c.group)}">${esc(c.group)}</th>`;
          }
          h2 += c.kind === "cattot" ? `<th class="dn tot" style="${catStyle(c.group)};background:var(--cat);color:var(--cat-ink)">${esc(c.label)}</th>` : `<th class="dn" title="${esc(c.label)}">${esc(c.label)}</th>`;
        } else h1 += `<th class="tot" rowspan="2">${esc(c.label)}</th>`;
      } else {
        const style = c.kind === "cat" ? ` class="cat" style="${catStyle(c.label)}"` : ` class="${c.kind === "all" || c.kind === "cartons" || c.kind === "trend" ? "tot" : c.kind === "supplier" ? "sup" : ""}"`;
        h1 += `<th${style}${c.title ? ` title="${esc(c.title)}"` : ""}>${esc(c.label)}</th>`;
      }
    }
    h1 += "</tr>"; h2 += "</tr>";
    let body = "";
    for (const r of m.rows) {
      const hidden = r.parent && !expanded.has(r.parent);
      const toggle = r.gid ? `<button type="button" class="tg" data-toggle="${esc(r.gid)}" aria-expanded="${expanded.has(r.gid)}" aria-label="Show or hide the rows underneath">${expanded.has(r.gid) ? "▾" : "▸"}</button>` : "";
      const lvl = r.level === "child" && r.indent === 2 ? "child deep" : (r.level || "");
      body += `<tr class="${lvl}"${hidden ? " hidden" : ""}${r.parent ? ` data-parent="${esc(r.parent)}"` : ""}><th title="${esc(r.label)}">${toggle}${r.html || esc(r.label)}</th>`;
      for (const cell of r.cells) body += cellHtml(cell, max, r);
      body += "</tr>";
    }
    return `<div class="tablewrap"><table class="pv"><thead>${h1}${grouped ? h2 : ""}</thead><tbody>${body}</tbody></table></div>`;
  }
  function cellHtml(cell, max, row) {
    if (cell.kind === "cartons") return `<td class="na tot ctn">${fmtInt(cell.v)}</td>`;
    if (cell.kind === "trend") {
      if (cell.v === null) return `<td class="na tot"></td>`;
      const title = `This week ${fmt(cell.now)}% vs last week ${fmt(cell.before)}%`;
      return `<td class="na tot trend">${arrow(cell.v, title)} ${cell.v > 0 ? "+" : ""}${fmt(cell.v)}</td>`;
    }
    const v = cell.v, cls = (cell.kind === "cattot" || cell.kind === "all" ? "tot " : "") + (v === 0 ? "zero " : "");
    if (v === null) return `<td class="na ${cls}"></td>`;
    const scale = row.level === "gt" || row.level === "sub" || cell.kind === "cattot" || cell.kind === "all" || row.level === "grp" ? max * 1.6 : max;
    const h = scale > 0 ? Math.round(Math.sqrt(Math.min(v / scale, 1)) * 42) : 0;
    const id = "c" + Object.keys(drillCtx).length;
    drillCtx[id] = { rk: cell.rk, dk: cell.dk };
    const tr = cell.trend !== undefined && cell.trend !== null ? arrow(cell.trend, `${cell.trend > 0 ? "+" : ""}${fmt(cell.trend)} points on the week before`) : "";
    return `<td class="${cls}" style="--h:${h}%" data-c="${id}" tabindex="0">${tr}${fmt(v)}</td>`;
  }

  // ---------- charts (Plotly) ----------------------------------------------
  const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
  const chartInk = () => css("--theme-text") || "#212529";
  function baseLayout() {
    const ink = chartInk(), grid = css("--theme-border") || "#dee2e6";
    return { margin: { l: 60, r: 20, t: 10, b: 60 }, height: 300, paper_bgcolor: "rgba(0,0,0,0)", plot_bgcolor: "rgba(0,0,0,0)",
      font: { color: ink, size: 11 }, legend: { orientation: "h", y: -0.25 }, yaxis: { title: "% of inspected units", gridcolor: grid, rangemode: "tozero" }, xaxis: { gridcolor: grid } };
  }
  function chartSpec(m) {
    const ink = chartInk(), layout = baseLayout();
    if (m.supplier) {
      // Grouped bars: each sub-major category, one bar per supplier.
      const cols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "supplier");
      const allIdx = m.columns.findIndex(c => c.kind === "all");
      const subs = m.rows.filter(r => r.level === "sub").sort((a, b) => (b.cells[allIdx].v ?? 0) - (a.cells[allIdx].v ?? 0)).slice(0, 12);
      layout.barmode = "group"; layout.height = 360; layout.xaxis.automargin = true; layout.legend.y = -0.35;
      return { data: cols.map(x => ({ x: subs.map(r => r.label), y: subs.map(r => r.cells[x.i].v), type: "bar", name: x.c.label })), layout };
    }
    if (m.week) {
      const wcols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "week");
      const series = m.rows.filter(r => !r.parent && (r.level === "grp" || r.level === "gt"));
      const data = series.map(r => ({
        x: wcols.map(x => `${x.c.label} (${dm(x.c.wk)})`), y: wcols.map(x => r.cells[x.i].v), type: "scatter", mode: "lines+markers", name: r.label,
        line: { color: r.level === "gt" ? ink : catColor[r.label.replace(/ total$/, "")], width: r.level === "gt" ? 3 : 2, dash: r.level === "gt" ? "dot" : "solid" }
      }));
      return { data, layout };
    }
    // Stacked category totals per row (top 20 rows by All defects).
    const catCols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "cattot" || x.c.kind === "cat");
    const allIdx = m.columns.findIndex(c => c.kind === "all");
    const rows = m.rows.filter(r => r.level !== "gt" && r.level !== "child").sort((a, b) => (b.cells[allIdx].v ?? 0) - (a.cells[allIdx].v ?? 0)).slice(0, 20);
    const data = catCols.map(x => ({ x: rows.map(r => r.label), y: rows.map(r => r.cells[x.i].v), type: "bar", name: x.c.group || x.c.label, marker: { color: catColor[x.c.group || x.c.label] } }));
    layout.barmode = "stack"; layout.xaxis.automargin = true;
    return { data, layout };
  }
  function drawChart(m) {
    const el = $("chart");
    if (!window.Plotly || !m) { el.hidden = true; return; }
    el.hidden = false;
    const spec = chartSpec(m);
    Plotly.react(el, spec.data, spec.layout, { displayModeBar: false, responsive: true });
  }
  // Per week: one chart comparing the sub-major categories, then one trend chart
  // per sub-major category (its defect categories and its total), biggest first.
  function drawTrends(m, res) {
    const box = $("charts");
    if (window.Plotly) box.querySelectorAll(".dr-mini").forEach(el => Plotly.purge(el));
    box.innerHTML = "";
    const subs = m && m.week && window.Plotly ? m.rows.filter(r => r.level === "sub") : [];
    box.hidden = !subs.length;
    if (!subs.length) return;
    const inspected = r => (res.cells.get(r.gid.slice(2) + SEP_SUBWEEK + "__all" + CELL + "T") || [0, 0])[1];
    const ranked = subs.slice().sort((a, b) => inspected(b) - inspected(a));
    const shown = ranked.slice(0, TREND_CHARTS);
    const wcols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "week");
    // Week numbers alone on the small charts: with the dates the labels tilt and clip.
    const xWide = wcols.map(w => `${w.c.label} (${dm(w.c.wk)})`), x = wcols.map(w => w.c.label);
    const draw = (title, data, wide) => {
      const el = document.createElement("div");
      el.className = "dr-mini" + (wide ? " wide" : "");
      box.appendChild(el);
      const layout = baseLayout();
      layout.title = { text: title, font: { size: 13 }, x: 0.01, xanchor: "left" };
      layout.margin = { l: 45, r: 15, t: 34, b: 50 }; layout.height = wide ? 340 : 280; layout.yaxis.title = ""; layout.legend.y = -0.28;
      Plotly.react(el, data, layout, { displayModeBar: false, responsive: true });
    };
    if (shown.length > 1)
      draw("All defects % by sub-major category", shown.map(r => ({ x: xWide, y: wcols.map(w => r.cells[w.i].v), type: "scatter", mode: "lines+markers", connectgaps: true, name: r.label })), true);
    for (const r of shown) {
      const catLines = m.rows.filter(c => c.parent === r.gid && c.indent !== 2).map(c => ({
        x, y: wcols.map(w => c.cells[w.i].v), type: "scatter", mode: "lines+markers", connectgaps: true, name: c.label.replace(/ total$/, ""),
        line: { color: catColor[c.label.replace(/ total$/, "")], width: 2 }
      }));
      catLines.push({ x, y: wcols.map(w => r.cells[w.i].v), type: "scatter", mode: "lines+markers", connectgaps: true, name: "All defects", line: { color: chartInk(), width: 3, dash: "dot" } });
      draw(r.label, catLines, false);
    }
    if (ranked.length > shown.length) {
      const note = document.createElement("p");
      note.className = "hint wide";
      note.textContent = `Showing the ${shown.length} sub-major categories with the most inspected units, out of ${ranked.length}. Use the Sub-major filter to choose others.`;
      box.appendChild(note);
    }
  }

  // ---------- main render --------------------------------------------------
  const emptyHtml = (msg, extra) => `<div class="empty"><div>${esc(msg)}</div>${extra || ""}</div>`;
  let rowLabelOf = k => k;
  function render() {
    drillCtx = {}; current = null;
    const tab = TABS.find(t => t.id === S.tab);
    let head = `<div class="ptitle"><div><h2>${tab.title}</h2><span class="hint">${tab.hint}</span></div><div class="acts" id="acts"></div></div>`;
    if (!D) { $("panel").innerHTML = head; $("chart").hidden = true; drawTrends(null); return; }
    if (needsFruit(S.tab) && !S.major.length) {
      const majors = [...new Set(units.filter(u => passes(u, "major")).map(u => u.mj).filter(Boolean))].sort();
      $("panel").innerHTML = head + emptyHtml("This report lists every defect of one fruit, so pick a major category to start.",
        `<div class="chips">${majors.map(m => `<button type="button" data-major="${esc(m)}">${esc(m)}</button>`).join("")}</div>`);
      $("chart").hidden = true; drawTrends(null); updateSummary(null); return;
    }
    const tw = lastTwoWeeks();
    const withTrend = (keysFn) => u => { const ks = keysFn(u); if (tw && (u.wk === tw[0] || u.wk === tw[1])) for (const k of ks.slice()) ks.push(k + SEP_SUBWEEK + u.wk); return ks; };
    let res, model;
    if (S.tab === "week") {
      res = compute(u => [u.wk, "__all", u.sm + SEP_SUBWEEK + u.wk, u.sm + SEP_SUBWEEK + "__all"]);
      model = modelWeek(res, [...new Set(res.rows.filter(r => !r.includes(SEP_SUBWEEK) && r !== "__all"))].sort());
    } else if (S.tab === "class") {
      res = compute(withTrend(u => [u.sm, u.sm + SEP_CLASS + (u.c || "(no class)"), "__all"]));
      const base = res.rows.filter(r => !r.includes(SEP_SUBWEEK));
      const subs = base.filter(r => r !== "__all" && !r.includes(SEP_CLASS)).sort((a, b) => a.localeCompare(b));
      const rows = []; for (const s of subs) { rows.push(s); rows.push(...base.filter(r => r.startsWith(s + SEP_CLASS)).sort()); } rows.push("__all");
      model = modelUnitRows(res, rows, "Sub-major / class", true,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : k.includes(SEP_CLASS) ? { label: k.split(SEP_CLASS)[1], level: "child", parent: "c:" + k.split(SEP_CLASS)[0] } : { label: k || "(no sub-major)", level: "grp", gid: "c:" + k }, tw);
      if (!classOpened) { for (const s of subs) expanded.add("c:" + s); classOpened = true; }   // opens expanded, like the Excel sheet
    } else if (S.tab === "branch") {
      res = compute(withTrend(u => [u.sm, u.sm + SEP_CLASS + u.p, "__all"]));
      const base = res.rows.filter(r => !r.includes(SEP_SUBWEEK));
      const subs = base.filter(r => r !== "__all" && !r.includes(SEP_CLASS)).sort((a, b) => a.localeCompare(b));
      const rows = []; for (const s of subs) { rows.push(s); rows.push(...base.filter(r => r.startsWith(s + SEP_CLASS)).sort((a, b) => plantName(a.split(SEP_CLASS)[1]).localeCompare(plantName(b.split(SEP_CLASS)[1])))); } rows.push("__all");
      model = modelUnitRows(res, rows, "Sub-major / branch", true,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : k.includes(SEP_CLASS) ? { label: plantName(k.split(SEP_CLASS)[1]), level: "child", parent: "b:" + k.split(SEP_CLASS)[0] } : { label: k || "(no sub-major)", level: "grp", gid: "b:" + k }, tw);
    } else if (S.tab === "supplier") {
      res = compute(u => [u.v, "__all", u.sm + SEP_SUBWEEK + u.v, u.sm + SEP_SUBWEEK + "__all"]);
      const inspected = k => (res.cells.get(k + CELL + "T") || [0, 0])[1];
      const ranked = res.rows.filter(r => r !== "__all" && !r.includes(SEP_SUBWEEK)).sort((a, b) => inspected(b) - inspected(a));
      const shown = S.supplier.length ? ranked : ranked.slice(0, SUPPLIER_COLS);
      model = modelSupplier(res, shown);
      if (model && ranked.length > shown.length) model.note = `Showing the ${shown.length} suppliers with the most inspected units, out of ${ranked.length}. Use the Supplier filter to choose others.`;
    } else if (S.tab === "sub") {
      res = compute(withTrend(u => [u.sm || "(none)", (u.sm || "(none)") + SEP_CLASS + (u.vr || "(no variety)"), "__all"]));
      const base = res.rows.filter(r => !r.includes(SEP_SUBWEEK));
      const subs = base.filter(r => r !== "__all" && !r.includes(SEP_CLASS)).sort((a, b) => a.localeCompare(b));
      const rows = []; for (const s of subs) { rows.push(s); rows.push(...base.filter(r => r.startsWith(s + SEP_CLASS)).sort((a, b) => a.localeCompare(b))); } rows.push("__all");
      model = modelUnitRows(res, rows, "Sub-major / variety", false,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : k.includes(SEP_CLASS) ? { label: k.split(SEP_CLASS)[1], level: "child", parent: "m:" + k.split(SEP_CLASS)[0] } : { label: k, level: "grp", gid: "m:" + k }, tw);
    } else {
      const key = "mj";
      res = compute(withTrend(u => [u[key] || "(none)", "__all"]));
      const rows = [...res.rows.filter(r => r !== "__all" && !r.includes(SEP_SUBWEEK)).sort((a, b) => a.localeCompare(b)), "__all"];
      model = modelUnitRows(res, rows, "Major category", false,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : { label: k, level: "" }, tw);
    }
    current = model ? { model, res, tab } : null;
    let body;
    if (!res.n) body = emptyHtml("Nothing matches these filters. Widen the dates or clear a filter.");
    else if (!model) body = emptyHtml("No defects were recorded for this selection.");
    else body = (model.note ? `<p class="hint mb-0">${esc(model.note)}</p>` : "") + renderTable(model);
    $("panel").innerHTML = head + body;
    $("acts").innerHTML = (S.tab === "week" ? `<div class="form-check form-switch mb-0 me-2"><input class="form-check-input" type="checkbox" id="fBySub" ${S.bySub ? "checked" : ""} ${S.sub.length === 1 ? "disabled" : ""}><label class="form-check-label small" for="fBySub">By sub-major</label></div>` : "")
      + (model && model.rows.some(r => r.gid) ? `<button type="button" class="btn btn-sm btn-outline-secondary" id="expAll">Expand all</button><button type="button" class="btn btn-sm btn-outline-secondary" id="colAll">Collapse all</button>` : "")
      + (model ? `<button type="button" class="btn btn-sm btn-outline-success" data-export="xlsx"><i class="bi bi-file-earmark-excel me-1"></i>Excel</button><button type="button" class="btn btn-sm btn-outline-danger" data-export="pdf"><i class="bi bi-file-earmark-pdf me-1"></i>PDF</button>` : "");
    const t = $("panel").querySelector("thead tr"), tbl = $("panel").querySelector("table");
    if (t && tbl) tbl.style.setProperty("--h1", t.getBoundingClientRect().height + "px");
    drawChart(model);
    drawTrends(model, res);
    updateSummary(res);
  }

  // ---------- summary strip ------------------------------------------------
  function pendingFiltered() { return pending.filter(p => (!S.plant.length || S.plant.includes(p.plant)) && (!S.supplier.length || S.supplier.includes(p.supplier))); }
  function updateSummary(res) {
    const range = D ? `${dm(D.from)} – ${dm(D.to)} ${D.to.slice(0, 4)}` : "";
    const pend = pendingFiltered(), notYet = pend.filter(p => !p.openedByEnd).length;
    const uomTitle = res ? Object.entries(res.uom).sort((a, b) => b[1] - a[1]).map(([u, q]) => `${u}: ${Math.round(q).toLocaleString()}`).join(" · ") : "";
    const tiles = [
      ["Period", range, ""],
      ["Orders", res ? res.orders.toLocaleString() : "—", ""],
      ["Samples", res ? res.samples.toLocaleString() : "—", ""],
      ["Cartons (PO qty)", res ? Math.round(res.ctn).toLocaleString() : "—", uomTitle],
      ["Pending containers", pend.length.toLocaleString(), `${notYet} not yet inspected · ${pend.length - notYet} pending, as of ${D ? dm(D.to) : ""}. Click to list them.`, "pending"]
    ];
    $("summary").innerHTML = tiles.map(([l, v, t, act]) => `<${act ? "button type=\"button\" data-open=\"" + act + "\"" : "div"} class="kp${act ? " link" : ""}" title="${esc(t)}"><div class="l">${l}</div><div class="v">${v}</div></${act ? "button" : "div"}>`).join("");
  }
  function renderTabs() {
    $("tabs").innerHTML = TABS.map(t => `<button type="button" role="tab" id="tab-${t.id}" aria-selected="${t.id === S.tab}" aria-controls="panel" tabindex="${t.id === S.tab ? 0 : -1}" data-tab="${t.id}">${t.label}</button>`).join("");
  }

  // ---------- drill-down (mirrored by DefectReportService.DrillAsync) --------
  function unitInRow(u, rk) {
    if (rk === "__all") return true;
    if (S.tab === "week") {
      if (rk.includes(SEP_SUBWEEK)) { const [sm, w] = rk.split(SEP_SUBWEEK); return u.sm === sm && (w === "__all" || u.wk === w); }
      return u.wk === rk;
    }
    if (S.tab === "class") return rk.includes(SEP_CLASS) ? (u.sm + SEP_CLASS + (u.c || "(no class)")) === rk : u.sm === rk;
    if (S.tab === "branch") return rk.includes(SEP_CLASS) ? (u.sm + SEP_CLASS + u.p) === rk : u.sm === rk;
    if (S.tab === "supplier") {
      if (rk.includes(SEP_SUBWEEK)) { const [sm, v] = rk.split(SEP_SUBWEEK); return u.sm === sm && (v === "__all" || u.v === v); }
      return u.v === rk;
    }
    if (S.tab === "sub") return rk.includes(SEP_CLASS) ? ((u.sm || "(none)") + SEP_CLASS + (u.vr || "(no variety)")) === rk : (u.sm || "(none)") === rk;
    return (u.mj || "(none)") === rk;
  }
  function describe(rk, dk) {
    const what = dk === "T" ? "All defects" : dk.startsWith("C:") ? `${dk.slice(2)} defects` : dk.slice(2);
    let where;
    if (rk === "__all") where = S.tab === "week" ? "Whole period" : "All rows";
    else if (S.tab === "week") { const [a, b] = rk.includes(SEP_SUBWEEK) ? rk.split(SEP_SUBWEEK) : ["", rk]; where = [a, b === "__all" ? "Whole period" : weekRange(b)].filter(Boolean).join(" · "); }
    else if (S.tab === "branch") { const [s, p] = rk.split(SEP_CLASS); where = [s || "(no sub-major)", p && plantName(p)].filter(Boolean).join(" · "); }
    else if (S.tab === "supplier") { const [a, b] = rk.includes(SEP_SUBWEEK) ? rk.split(SEP_SUBWEEK) : ["", rk]; where = [a, b === "__all" ? "All suppliers" : b].filter(Boolean).join(" · "); }
    else where = rk.replace(SEP_CLASS, " · ");
    return { what, where };
  }
  function openDrawer(title, sub, kpis, tableHtml, exportHref) {
    $("dTitle").textContent = title; $("dSub").textContent = sub;
    $("dKpis").innerHTML = kpis.map(([l, v]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");
    $("dTable").innerHTML = tableHtml;
    $("dExport").hidden = !exportHref; if (exportHref) $("dExport").href = exportHref;
    $("scrim").hidden = false; $("drawer").hidden = false; $("dClose").focus();
  }
  function openDrill(id) {
    const ctx = drillCtx[id]; if (!ctx) return;
    const { rk, dk } = ctx;
    const matches = df => dk === "T" || (dk.startsWith("C:") && df.ct === dk.slice(2)) || (dk.startsWith("N:") && df.nm === dk.slice(2));
    const perQo = new Map();
    for (const u of units) {
      if (!passes(u) || !unitInRow(u, rk)) continue;
      if (!(D.groupDefects[u.g] || []).some(i => catOk(defs[i].ct) && matches(defs[i]))) continue;
      let v = 0;
      for (const [d, dv] of factsBy[u.i]) { const df = defs[d]; if (catOk(df.ct) && matches(df)) v += dv; }
      let o = perQo.get(u.q);
      if (!o) { o = { q: u.q, n: u.n, r: u.r, p: u.p, v: u.v, k: u.k, items: new Set(), ns: 0, sz: 0, val: 0, ctn: 0 }; perQo.set(u.q, o); }
      o.items.add(u.sm + (u.c ? " · " + u.c : "")); o.ns += u.ns; o.sz += u.sz; o.val += v; o.ctn += u.ctn;
    }
    const list = [...perQo.values()].map(o => ({ ...o, pct: o.sz > 0 ? 100 * o.val / o.sz : null })).sort((a, b) => (b.pct ?? -1) - (a.pct ?? -1) || b.r.localeCompare(a.r));
    const sum = k => list.reduce((s, o) => s + o[k], 0);
    const d = describe(rk, dk);
    const context = [d.where, ...filterText().filter(t => !t.startsWith("No filters"))].join(" · ");
    const q = new URLSearchParams({ From: D.from, To: D.to, Tab: S.tab, RowKey: rk, DefectKey: dk, Metric: "qc", title: `${d.what} · ${context}` });
    // One pair per value: the server binds each to a list.
    for (const [name, values] of [["Plant", S.plant], ["Supplier", S.supplier], ["Major", S.major], ["Sub", S.sub], ["Class", S.cls], ["PackWeight", S.pw], ["Cat", S.cat]])
      for (const v of values) q.append(name, v);
    openDrawer(d.what, context, [
      ["Defect %", sum("sz") ? (100 * sum("val") / sum("sz")).toFixed(2) + "%" : "—"],
      ["Orders", list.length.toLocaleString()],
      ["Inspected units", fmtInt(sum("sz"))],
      ["Defective units", fmtInt(sum("val"))],
      ["Cartons (PO qty)", fmtInt(sum("ctn"))]
    ], `<thead><tr><th>QC order</th><th>Received</th><th>Branch</th><th>Supplier</th><th>Container</th><th>Material</th><th class="r">Cartons</th><th class="r">Samples</th><th class="r">Inspected</th><th class="r">Defective</th><th class="r">%</th></tr></thead><tbody>` +
      list.map(o => `<tr><td><a href="${URL_QO}${o.q}" target="_blank" rel="noopener">${esc(o.n)}</a></td><td>${o.r}</td><td>${esc(plantName(o.p))}</td><td>${esc(o.v)}</td><td>${esc(o.k)}</td><td>${esc([...o.items].join(", "))}</td><td class="r">${fmtInt(o.ctn)}</td><td class="r">${o.ns}</td><td class="r">${fmtInt(o.sz)}</td><td class="r">${fmtInt(o.val)}</td><td class="r"><b>${o.pct === null ? "—" : o.pct.toFixed(2)}</b></td></tr>`).join("") + `</tbody>`,
      URL_DRILL + "?" + q.toString());
  }
  function openPending() {
    const list = pendingFiltered().slice().sort((a, b) => a.received.localeCompare(b.received));
    const notYet = list.filter(p => !p.openedByEnd).length;
    openDrawer("Pending containers", `Received ${dm(D.from)} – ${dm(D.to)}, inspection not finished by ${dm(D.to)}${S.plant.length ? " · " + S.plant.map(plantName).join(", ") : ""}${S.supplier.length ? " · " + S.supplier.join(", ") : ""}`, [
      ["Pending", list.length.toLocaleString()], ["Not yet inspected", notYet.toLocaleString()], ["Inspection pending", (list.length - notYet).toLocaleString()]
    ], `<thead><tr><th>Status</th><th>Received</th><th>Branch</th><th>Supplier</th><th>Container</th><th>Arrival</th><th>QC order</th></tr></thead><tbody>` +
      list.map(p => `<tr><td><span class="badge ${p.openedByEnd ? "bg-info-subtle text-info-emphasis" : "bg-warning-subtle text-warning-emphasis"}">${p.openedByEnd ? "Pending" : "Not yet inspected"}</span></td><td>${p.received}</td><td>${esc(plantName(p.plant))}</td><td>${esc(p.supplier)}</td><td>${esc(p.container)}</td><td>${p.arrivalId ? `<a href="${URL_ARR}${p.arrivalId}" target="_blank" rel="noopener">${esc(p.arrivalNo)}</a>` : `<span class="text-muted">No arrival yet</span>`}</td><td>${p.qoId ? `<a href="${URL_QO}${p.qoId}" target="_blank" rel="noopener">${esc(p.qoNo)}</a>` : ""}</td></tr>`).join("") + `</tbody>`, null);
  }
  const closeDrill = () => { $("scrim").hidden = true; $("drawer").hidden = true; };

  // ---------- export (Excel / PDF) -----------------------------------------
  async function exportReport(format, btn) {
    if (!current) return;
    const { model, res, tab } = current;
    btn.disabled = true;
    try {
      let chart = null;
      if (window.Plotly && !$("chart").hidden) {
        try { chart = await Plotly.toImage($("chart"), { format: "png", width: 1400, height: 420, scale: 1 }); } catch (e) { chart = null; }
      }
      const charts = [];
      if (window.Plotly && !$("charts").hidden)
        for (const el of $("charts").querySelectorAll(".dr-mini")) {
          try { charts.push(await Plotly.toImage(el, { format: "png", width: 900, height: 480, scale: 1 })); } catch (e) { /* the table still exports */ }
        }
      const pend = pendingFiltered();
      const payload = {
        format, title: tab.title, tab: tab.label, hint: [tab.hint, model.note].filter(Boolean).join(" "),
        period: `Received ${D.from} to ${D.to}`, filters: filterText(),
        kpis: [["Orders", res.orders.toLocaleString()], ["Samples", res.samples.toLocaleString()], ["Cartons (PO qty)", Math.round(res.ctn).toLocaleString()], ["Pending containers", pend.length.toLocaleString()]],
        rowLabel: model.rowLabel,
        columns: model.columns.map(c => ({ group: c.group || "", label: c.label, color: c.group ? catColor[c.group] : (c.kind === "cat" ? catColor[c.label] : null), kind: c.kind })),
        rows: model.rows.filter(r => !r.parent || expanded.has(r.parent) || format === "xlsx").map(r => ({
          label: r.label, level: r.level || "", indent: r.parent ? (r.indent === 2 ? 2 : 1) : (r.level === "child" ? 1 : 0),
          values: r.cells.map(c => c.v === undefined ? null : c.v), trends: r.cells.map(c => c.trend ?? null)
        })),
        chart, charts, generated: new Date().toLocaleString()
      };
      // X-CSRF-TOKEN: the antiforgery header name set in Program.cs (AddAntiforgery).
      const r = await fetch(URL_EXPORT, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": TOKEN }, body: JSON.stringify(payload) });
      if (!r.ok) throw new Error(r.status);
      const blob = await r.blob();
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = `defect-report-${tab.id}-${D.from}-${D.to}.${format}`;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    } catch (e) {
      alert("The report could not be exported. Try again, or narrow the filters.");
    } finally { btn.disabled = false; }
  }

  // ---------- loading ------------------------------------------------------
  let loadSeq = 0;
  async function load() {
    let from = $("fFrom").value, to = $("fTo").value;
    if (!from || !to) return;
    if (from > to) { [from, to] = [to, from]; $("fFrom").value = from; $("fTo").value = to; }
    const seq = ++loadSeq;
    $("loading").hidden = false;
    try {
      const [res, pres] = await Promise.all([
        fetch(`${URL_DATA}?from=${from}&to=${to}`, { headers: { Accept: "application/json" } }),
        fetch(`${URL_PENDING}?from=${from}&to=${to}`, { headers: { Accept: "application/json" } })
      ]);
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      const pend = pres.ok ? await pres.json() : [];
      if (seq !== loadSeq) return;
      build(data); pending = pend;
      if (data.from !== from) $("fFrom").value = data.from;   // clamped to MAX_DAYS by the server
      refreshOptions(); render();
      $("foot").textContent = `${units.length.toLocaleString()} material lines on finished inspections received ${dm(D.from)} – ${dm(D.to)} (at most ${MAX_DAYS} days). Loaded ${D.generated}.`;
    } catch (e) {
      $("panel").innerHTML = emptyHtml("The report data could not be loaded. Reload the page, or try a shorter date range.");
    } finally { if (seq === loadSeq) $("loading").hidden = true; }
  }

  // ---------- wiring -------------------------------------------------------
  const update = () => { S.hide = $("fHide").checked; refreshOptions(); render(); };
  $("fHide").addEventListener("change", update);
  const filterBox = document.querySelector(".dr-filters");
  const msOf = el => { const box = el.closest(".dr-ms"); return box ? MS.find(f => f.id === box.id) : null; };
  filterBox.addEventListener("change", e => {
    const f = e.target.type === "checkbox" ? msOf(e.target) : null;
    if (!f) return;
    S[f.s] = e.target.checked ? [...S[f.s], e.target.value] : S[f.s].filter(v => v !== e.target.value);
    update();
  });
  filterBox.addEventListener("click", e => {
    if (!e.target.closest("[data-ms-clear]")) return;
    const f = msOf(e.target);
    S[f.s] = [];
    $(f.id).querySelectorAll("input[type=checkbox]").forEach(c => { c.checked = false; });
    update();
  });
  filterBox.addEventListener("input", e => {
    if (!e.target.matches("[data-ms-search]")) return;
    const q = e.target.value.toLowerCase();
    e.target.closest(".dropdown-menu").querySelectorAll("[data-ms-row]").forEach(r => { r.hidden = !r.textContent.toLowerCase().includes(q); });
  });
  // Closed again: bring its list in step with what the other filters now leave.
  filterBox.addEventListener("hidden.bs.dropdown", () => { refreshOptions(); });
  for (const id of ["fFrom", "fTo"]) $(id).addEventListener("change", load);
  $("fReset").addEventListener("click", () => {
    for (const f of MS) S[f.s] = [];
    $("fHide").checked = true; update();
  });
  function selectTab(id) { S.tab = id; store.set("defectReports.tab", id); try { history.replaceState(null, "", "#" + id); } catch (e) {} renderTabs(); render(); }
  $("tabs").addEventListener("click", e => { const b = e.target.closest("[data-tab]"); if (b) selectTab(b.dataset.tab); });
  $("tabs").addEventListener("keydown", e => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const i = TABS.findIndex(t => t.id === S.tab), n = TABS[(i + (e.key === "ArrowRight" ? 1 : TABS.length - 1)) % TABS.length];
    selectTab(n.id); $("tab-" + n.id).focus();
  });
  $("panel").addEventListener("click", e => {
    const m = e.target.closest("[data-major]"); if (m) { S.major = [m.dataset.major]; update(); return; }
    const tg = e.target.closest("[data-toggle]");
    if (tg) {
      const gid = tg.dataset.toggle, open = !expanded.has(gid);
      if (open) expanded.add(gid); else expanded.delete(gid);
      tg.textContent = open ? "▾" : "▸"; tg.setAttribute("aria-expanded", open);
      $("panel").querySelectorAll("tr[data-parent]").forEach(tr => { if (tr.dataset.parent === gid) tr.hidden = !open; });
      return;
    }
    if (e.target.id === "expAll" || e.target.id === "colAll") {
      const open = e.target.id === "expAll";
      if (current) for (const r of current.model.rows) if (r.gid) { if (open) expanded.add(r.gid); else expanded.delete(r.gid); }
      render(); return;
    }
    const x = e.target.closest("[data-export]"); if (x) { exportReport(x.dataset.export, x); return; }
    const c = e.target.closest("td[data-c]"); if (c) openDrill(c.dataset.c);
  });
  $("panel").addEventListener("change", e => { if (e.target.id === "fBySub") { S.bySub = e.target.checked; store.set("defectReports.bySub", S.bySub ? "1" : "0"); render(); } });
  $("panel").addEventListener("keydown", e => { if (e.key === "Enter") { const c = e.target.closest("td[data-c]"); if (c) openDrill(c.dataset.c); } });
  $("summary").addEventListener("click", e => { if (e.target.closest("[data-open=pending]") && D) openPending(); });
  $("dClose").addEventListener("click", closeDrill);
  $("scrim").addEventListener("click", closeDrill);
  document.addEventListener("keydown", e => { if (e.key === "Escape" && !$("drawer").hidden) closeDrill(); });

  const hash = (location.hash || "").slice(1), savedTab = store.get("defectReports.tab");
  S.tab = TABS.some(t => t.id === hash) ? hash : (TABS.some(t => t.id === savedTab) ? savedTab : "major");
  S.bySub = store.get("defectReports.bySub") !== "0";
  renderTabs(); render(); load();
})();
