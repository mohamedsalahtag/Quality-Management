// Reports -> Defect reports. Pivots the dataset from /DefectReports/Data in the
// browser: five tabs, shared filters, a chart per tab, week-on-week trend
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
  const PLANT = { AH01: "Al Hasa", BH01: "Bahrain", BU01: "Buraidah", DB01: "UAE", DM01: "Dammam", JD01: "Jeddah", KH01: "Khamis Mushayt", MD01: "Medinah", MK01: "Makkah", RD01: "Riyadh", TB01: "Tabuk" };
  const plantName = c => PLANT[c] ? `${PLANT[c]} (${c})` : (c || "(none)");
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
    units = D.units.map(([q, n, p, v, mj, sm, c, r, k, g, ns, sz, pw, ctn, uom], i) => ({
      i, q, n, p: D.plants[p], v: D.suppliers[v], mj: D.majors[mj], sm: D.subs[sm], c: D.classes[c], r, k, g: D.groups[g],
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
  const S = { plant: "", supplier: "", major: "", sub: "", cls: "", pw: "", cat: "", hide: true, tab: "week", bySub: true };
  const expanded = new Set();
  const TABS = [
    { id: "week",   label: "Per week",            title: "Defects per week", hint: "Defects down the side, ISO weeks (Monday to Sunday) across. Arrows compare each week with the week before." },
    { id: "class",  label: "Per defect & class",  title: "Defects per sub-major category and class", hint: "Each sub-major category with its classes underneath." },
    { id: "branch", label: "Per branch & defect", title: "Defects per branch and sub-major category", hint: "One row per receiving branch; open a branch to see its sub-major categories." },
    { id: "sub",    label: "Per sub-major",       title: "Defect categories per sub-major category", hint: "Category totals for every sub-major category." },
    { id: "major",  label: "Per major",           title: "Defect categories per major category", hint: "Category totals for every major category." }
  ];
  const needsFruit = t => t === "week" || t === "class" || t === "branch";

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
    for (const f of FILTERS) if (S[f.s] && skip !== f.s && u[f.key] !== S[f.s]) return false;
    return true;
  }
  function refreshOptions() {
    // Each dropdown lists only what the OTHER filters leave, so it never offers an empty page.
    for (const f of FILTERS) {
      const have = new Set(units.filter(u => passes(u, f.s)).map(u => u[f.key]).filter(Boolean));
      const values = f.order ? f.order().filter(v => have.has(v)) : [...have].sort((a, b) => a.localeCompare(b));
      $(f.id).innerHTML = `<option value="">${esc(f.all)}</option>` + values.map(v => `<option value="${esc(v)}">${esc(f.label ? f.label(v) : v)}</option>`).join("");
      $(f.id).value = values.includes(S[f.s]) ? S[f.s] : ""; S[f.s] = $(f.id).value;
    }
    $("fCat").innerHTML = `<option value="">All defect categories</option>` + cats.map(c => `<option value="${esc(c)}">${esc(c)}</option>`).join("");
    $("fCat").value = cats.includes(S.cat) ? S.cat : ""; S.cat = $("fCat").value;
  }
  function filterText() {
    const bits = FILTERS.filter(f => S[f.s]).map(f => `${f.name}: ${f.label ? f.label(S[f.s]) : S[f.s]}`);
    if (S.cat) bits.push(`Defect category: ${S.cat}`);
    return bits.length ? bits : ["No filters (everything in the period)"];
  }

  // ---------- engine -------------------------------------------------------
  // A cell is (row key, defect key). Defect keys: "N:<name>", "C:<category>", "T".
  // [defective units, inspected units]; the figure is 100 x units / inspected.
  function compute(unitKeys) {
    const cells = new Map(), used = new Set(), rowsSeen = new Set(), cartons = new Map();
    const catOk = c => !S.cat || c === S.cat;
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
        if (S.cat && ct !== S.cat) continue;
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
    return cats.filter(c => have.has(c) && (!S.cat || c === S.cat));
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
    const rows = [];
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
    rows.push(cartonsRow(""));
    rows.push(...block("", null, null));
    rows.push(line("All defects", null, "", "T", "gt"));
    if (S.bySub && !S.sub) {
      const subs = [...new Set(res.rows.filter(r => r.includes(SEP_SUBWEEK)).map(r => r.split(SEP_SUBWEEK)[0]))].sort((a, b) => a.localeCompare(b));
      for (const sm of subs) {
        const gid = "s:" + sm, prefix = sm + SEP_SUBWEEK;
        rows.push(line(sm || "(no sub-major)", null, prefix, "T", "sub", { gid }));
        rows.push(...block(prefix, gid, gid));
      }
    }
    return { columns, rows, rowLabel: "Defect", heat: true, week: true };
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
    const leafCols = m.columns.map((c, i) => ({ c, i })).filter(x => x.c.kind === "defect" || x.c.kind === "cat" || x.c.kind === "week");
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
        const style = c.kind === "cat" ? ` class="cat" style="${catStyle(c.label)}"` : ` class="${c.kind === "all" || c.kind === "cartons" || c.kind === "trend" ? "tot" : ""}"`;
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
  function chartSpec(m) {
    const ink = css("--theme-text") || "#212529", grid = css("--theme-border") || "#dee2e6";
    const layout = { margin: { l: 60, r: 20, t: 10, b: 60 }, height: 300, paper_bgcolor: "rgba(0,0,0,0)", plot_bgcolor: "rgba(0,0,0,0)",
      font: { color: ink, size: 11 }, legend: { orientation: "h", y: -0.25 }, yaxis: { title: "% of inspected units", gridcolor: grid, rangemode: "tozero" }, xaxis: { gridcolor: grid } };
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

  // ---------- main render --------------------------------------------------
  const emptyHtml = (msg, extra) => `<div class="empty"><div>${esc(msg)}</div>${extra || ""}</div>`;
  let rowLabelOf = k => k;
  function render() {
    drillCtx = {}; current = null;
    const tab = TABS.find(t => t.id === S.tab);
    let head = `<div class="ptitle"><div><h2>${tab.title}</h2><span class="hint">${tab.hint}</span></div><div class="acts" id="acts"></div></div>`;
    if (!D) { $("panel").innerHTML = head; $("chart").hidden = true; return; }
    if (needsFruit(S.tab) && !S.major) {
      const majors = [...new Set(units.filter(u => passes(u, "major")).map(u => u.mj).filter(Boolean))].sort();
      $("panel").innerHTML = head + emptyHtml("This report lists every defect of one fruit, so pick a major category to start.",
        `<div class="chips">${majors.map(m => `<button type="button" data-major="${esc(m)}">${esc(m)}</button>`).join("")}</div>`);
      $("chart").hidden = true; updateSummary(null); return;
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
      res = compute(withTrend(u => [u.p, u.p + SEP_CLASS + u.sm, "__all"]));
      const base = res.rows.filter(r => !r.includes(SEP_SUBWEEK));
      const plants = base.filter(r => r !== "__all" && !r.includes(SEP_CLASS)).sort();
      const rows = []; for (const p of plants) { rows.push(p); rows.push(...base.filter(r => r.startsWith(p + SEP_CLASS)).sort((a, b) => a.localeCompare(b))); } rows.push("__all");
      model = modelUnitRows(res, rows, "Branch / sub-major", true,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : k.includes(SEP_CLASS) ? { label: k.split(SEP_CLASS)[1] || "(no sub-major)", level: "child", parent: "b:" + k.split(SEP_CLASS)[0] } : { label: plantName(k), level: "grp", gid: "b:" + k }, tw);
    } else {
      const key = S.tab === "sub" ? "sm" : "mj";
      res = compute(withTrend(u => [u[key] || "(none)", "__all"]));
      const rows = [...res.rows.filter(r => r !== "__all" && !r.includes(SEP_SUBWEEK)).sort((a, b) => a.localeCompare(b)), "__all"];
      model = modelUnitRows(res, rows, S.tab === "sub" ? "Sub-major category" : "Major category", false,
        k => k === "__all" ? { label: "Grand total", level: "gt" } : { label: k, level: "" }, tw);
    }
    current = model ? { model, res, tab } : null;
    let body;
    if (!res.n) body = emptyHtml("Nothing matches these filters. Widen the dates or clear a filter.");
    else if (!model) body = emptyHtml("No defects were recorded for this selection.");
    else body = renderTable(model);
    $("panel").innerHTML = head + body;
    $("acts").innerHTML = (S.tab === "week" ? `<div class="form-check form-switch mb-0 me-2"><input class="form-check-input" type="checkbox" id="fBySub" ${S.bySub ? "checked" : ""} ${S.sub ? "disabled" : ""}><label class="form-check-label small" for="fBySub">By sub-major</label></div>` : "")
      + (model && model.rows.some(r => r.gid) ? `<button type="button" class="btn btn-sm btn-outline-secondary" id="expAll">Expand all</button><button type="button" class="btn btn-sm btn-outline-secondary" id="colAll">Collapse all</button>` : "")
      + (model ? `<button type="button" class="btn btn-sm btn-outline-success" data-export="xlsx"><i class="bi bi-file-earmark-excel me-1"></i>Excel</button><button type="button" class="btn btn-sm btn-outline-danger" data-export="pdf"><i class="bi bi-file-earmark-pdf me-1"></i>PDF</button>` : "");
    const t = $("panel").querySelector("thead tr"), tbl = $("panel").querySelector("table");
    if (t && tbl) tbl.style.setProperty("--h1", t.getBoundingClientRect().height + "px");
    drawChart(model);
    updateSummary(res);
  }

  // ---------- summary strip ------------------------------------------------
  function pendingFiltered() { return pending.filter(p => (!S.plant || p.plant === S.plant) && (!S.supplier || p.supplier === S.supplier)); }
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
    if (S.tab === "branch") return rk.includes(SEP_CLASS) ? (u.p + SEP_CLASS + u.sm) === rk : u.p === rk;
    return (u[S.tab === "sub" ? "sm" : "mj"] || "(none)") === rk;
  }
  function describe(rk, dk) {
    const what = dk === "T" ? "All defects" : dk.startsWith("C:") ? `${dk.slice(2)} defects` : dk.slice(2);
    let where;
    if (rk === "__all") where = S.tab === "week" ? "Whole period" : "All rows";
    else if (S.tab === "week") { const [a, b] = rk.includes(SEP_SUBWEEK) ? rk.split(SEP_SUBWEEK) : ["", rk]; where = [a, b === "__all" ? "Whole period" : weekRange(b)].filter(Boolean).join(" · "); }
    else if (S.tab === "branch") { const [p, s] = rk.split(SEP_CLASS); where = [plantName(p), s].filter(Boolean).join(" · "); }
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
    const { rk, dk } = ctx, catOk = c => !S.cat || c === S.cat;
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
    const q = new URLSearchParams({ From: D.from, To: D.to, Plant: S.plant, Supplier: S.supplier, Major: S.major, Sub: S.sub, Class: S.cls, PackWeight: S.pw, Cat: S.cat, Tab: S.tab, RowKey: rk, DefectKey: dk, Metric: "qc", title: `${d.what} · ${context}` });
    openDrawer(d.what, context, [
      ["Defect %", sum("sz") ? (100 * sum("val") / sum("sz")).toFixed(2) + "%" : "—"],
      ["Orders", list.length.toLocaleString()],
      ["Inspected units", fmtInt(sum("sz"))],
      ["Defective units", fmtInt(sum("val"))],
      ["Cartons (PO qty)", fmtInt(sum("ctn"))]
    ], `<thead><tr><th>QC order</th><th>Received</th><th>Branch</th><th>Supplier</th><th>Container</th><th>Material</th><th class="r">Cartons</th><th class="r">Samples</th><th class="r">Inspected</th><th class="r">Defective</th><th class="r">%</th></tr></thead><tbody>` +
      list.map(o => `<tr><td><a href="${URL_QO}${o.q}" target="_blank" rel="noopener">${esc(o.n)}</a></td><td>${o.r}</td><td>${esc(o.p)}</td><td>${esc(o.v)}</td><td>${esc(o.k)}</td><td>${esc([...o.items].join(", "))}</td><td class="r">${fmtInt(o.ctn)}</td><td class="r">${o.ns}</td><td class="r">${fmtInt(o.sz)}</td><td class="r">${fmtInt(o.val)}</td><td class="r"><b>${o.pct === null ? "—" : o.pct.toFixed(2)}</b></td></tr>`).join("") + `</tbody>`,
      URL_DRILL + "?" + q.toString());
  }
  function openPending() {
    const list = pendingFiltered().slice().sort((a, b) => a.received.localeCompare(b.received));
    const notYet = list.filter(p => !p.openedByEnd).length;
    openDrawer("Pending containers", `Received ${dm(D.from)} – ${dm(D.to)}, inspection not finished by ${dm(D.to)}${S.plant ? " · " + plantName(S.plant) : ""}${S.supplier ? " · " + S.supplier : ""}`, [
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
      const pend = pendingFiltered();
      const payload = {
        format, title: tab.title, tab: tab.label, hint: tab.hint,
        period: `Received ${D.from} to ${D.to}`, filters: filterText(),
        kpis: [["Orders", res.orders.toLocaleString()], ["Samples", res.samples.toLocaleString()], ["Cartons (PO qty)", Math.round(res.ctn).toLocaleString()], ["Pending containers", pend.length.toLocaleString()]],
        rowLabel: model.rowLabel,
        columns: model.columns.map(c => ({ group: c.group || "", label: c.label, color: c.group ? catColor[c.group] : (c.kind === "cat" ? catColor[c.label] : null), kind: c.kind })),
        rows: model.rows.filter(r => !r.parent || expanded.has(r.parent) || format === "xlsx").map(r => ({
          label: r.label, level: r.level || "", indent: r.parent ? (r.indent === 2 ? 2 : 1) : (r.level === "child" ? 1 : 0),
          values: r.cells.map(c => c.v === undefined ? null : c.v), trends: r.cells.map(c => c.trend ?? null)
        })),
        chart, generated: new Date().toLocaleString()
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
  function readFilters() {
    for (const f of FILTERS) S[f.s] = $(f.id).value;
    S.cat = $("fCat").value; S.hide = $("fHide").checked;
  }
  const update = () => { readFilters(); refreshOptions(); render(); };
  for (const id of [...FILTERS.map(f => f.id), "fCat", "fHide"]) $(id).addEventListener("change", update);
  for (const id of ["fFrom", "fTo"]) $(id).addEventListener("change", load);
  $("fReset").addEventListener("click", () => {
    for (const id of [...FILTERS.map(f => f.id), "fCat"]) $(id).value = "";
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
    const m = e.target.closest("[data-major]"); if (m) { $("fMajor").value = m.dataset.major; update(); return; }
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
