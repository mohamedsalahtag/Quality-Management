// Reports -> Defect reports. Pivots the dataset from /DefectReports/Data in the
// browser: five tabs, shared filters, two figures (QC report % / Excel
// average) and a drill panel listing the orders behind any number. The
// drill-down workbook is rebuilt on the server by DefectReportService.DrillAsync
// with the same rules, so keep the two in step.
(function () {
  "use strict";
  const root = document.getElementById("dr");
  if (!root) return;
  const URL_DATA = root.dataset.urlData, URL_DRILL = root.dataset.urlDrill;
  const URL_QO = root.dataset.urlQo.replace(/\/0$/, "/");
  const MAX_DAYS = parseInt(root.dataset.maxDays || "366", 10);
  const PLANT = { AH01: "Al Hasa", BH01: "Bahrain", BU01: "Buraidah", DB01: "UAE", DM01: "Dammam", JD01: "Jeddah", KH01: "Khamis Mushayt", MD01: "Medinah", MK01: "Makkah", RD01: "Riyadh", TB01: "Tabuk" };
  const plantName = c => PLANT[c] ? `${PLANT[c]} (${c})` : (c || "(none)");
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? "").replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]);
  const store = { get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} } };

  // ---------- model (rebuilt on every load) --------------------------------
  let D, cats = [], catColor = {}, defs = [], units = [], factsBy = [], groupInfo = {}, nameOrder = {};
  const catInk = hex => { const n = parseInt((hex || "#6c757d").slice(1), 16), r = n >> 16, g = (n >> 8) & 255, b = n & 255; return (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? "#212529" : "#ffffff"; };
  function build(data) {
    D = data;
    cats = D.categories.map(c => c.nm);
    catColor = Object.fromEntries(D.categories.map(c => [c.nm, c.col || "#6c757d"]));
    defs = D.defects.map(([id, nm, ct, g, o]) => ({ id, nm, ct, g, o }));
    units = D.units.map(([q, n, p, v, mj, sm, c, r, k, g, ns, sz], i) => ({
      i, q, n, p: D.plants[p], v: D.suppliers[v], mj: D.majors[mj], sm: D.subs[sm], c: D.classes[c], r, k, g: D.groups[g], ns, sz: +sz
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

  // ---------- state --------------------------------------------------------
  const S = { from: $("fFrom").value, to: $("fTo").value, plant: "", supplier: "", major: "", sub: "", cls: "", cat: "", metric: "qc", hide: true, tab: "week" };
  const TABS = [
    { id: "week",   label: "Per week",            title: "Defects per week", hint: "Defects down the side, receive weeks (Saturday to Friday) across." },
    { id: "class",  label: "Per defect & class",  title: "Defects per sub-major category and class", hint: "Each sub-major category with its classes underneath." },
    { id: "branch", label: "Per branch & defect", title: "Defects per branch", hint: "One row per receiving branch." },
    { id: "sub",    label: "Per sub-major",       title: "Defect categories per sub-major category", hint: "Category totals for every sub-major category." },
    { id: "major",  label: "Per major",           title: "Defect categories per major category", hint: "Category totals for every major category." }
  ];
  const needsFruit = t => t === "week" || t === "class" || t === "branch";

  // ---------- filters ------------------------------------------------------
  function fill(sel, values, allLabel, label) {
    sel.innerHTML = `<option value="">${esc(allLabel)}</option>` + values.map(v => `<option value="${esc(v)}">${esc(label ? label(v) : v)}</option>`).join("");
  }
  function passes(u, skip) {
    if (S.plant && skip !== "plant" && u.p !== S.plant) return false;
    if (S.supplier && skip !== "supplier" && u.v !== S.supplier) return false;
    if (S.major && skip !== "major" && u.mj !== S.major) return false;
    if (S.sub && skip !== "sub" && u.sm !== S.sub) return false;
    if (S.cls && skip !== "cls" && u.c !== S.cls) return false;
    return true;
  }
  function refreshOptions() {
    // Each dropdown lists only what the OTHER filters leave, so it never offers an empty page.
    const opts = (key, skip) => [...new Set(units.filter(u => passes(u, skip)).map(u => u[key]).filter(Boolean))].sort((a, b) => a.localeCompare(b));
    const set = (id, key, skip, all, lab) => {
      const values = opts(key, skip); fill($(id), values, all, lab);
      const want = S[skip]; $(id).value = values.includes(want) ? want : ""; S[skip] = $(id).value;
    };
    set("fPlant", "p", "plant", "All branches", plantName);
    set("fSupplier", "v", "supplier", "All suppliers");
    set("fMajor", "mj", "major", "All major categories");
    set("fSub", "sm", "sub", "All sub-major categories");
    set("fClass", "c", "cls", "All classes");
    fill($("fCat"), cats, "All defect categories"); $("fCat").value = cats.includes(S.cat) ? S.cat : ""; S.cat = $("fCat").value;
  }

  // ---------- engine -------------------------------------------------------
  // A cell is (row key, defect key). Defect keys: "N:<name>", "C:<category>", "T".
  // Each keeps [QC numerator, QC denominator, Excel numerator, Excel denominator].
  function compute(unitKeys) {
    const cells = new Map(), used = new Set(), rowsSeen = new Set();
    const catOk = c => !S.cat || c === S.cat;
    const add = (rk, dk, a, b, c, d) => {
      const key = rk + "\u0001" + dk; let x = cells.get(key);
      if (!x) { x = [0, 0, 0, 0]; cells.set(key, x); }
      x[0] += a; x[1] += b; x[2] += c; x[3] += d;
    };
    let n = 0, samples = 0; const orders = new Set();
    for (const u of units) {
      if (!passes(u)) continue;
      const gi = groupInfo[u.g]; if (!gi) continue;
      n++; orders.add(u.q); samples += u.ns;
      for (const rk of unitKeys(u)) {
        rowsSeen.add(rk);
        let totRows = 0;
        for (const [nm, ct] of Object.entries(gi.byName)) if (catOk(ct)) add(rk, "N:" + nm, 0, u.sz, 0, u.ns);
        for (const [ct, cnt] of Object.entries(gi.catCount)) if (catOk(ct)) { add(rk, "C:" + ct, 0, u.sz, 0, u.ns * cnt); totRows += cnt; }
        if (totRows) add(rk, "T", 0, u.sz, 0, u.ns * totRows);
        for (const [d, v, pc] of factsBy[u.i]) {
          const df = defs[d]; if (!catOk(df.ct)) continue;
          add(rk, "N:" + df.nm, v, 0, pc, 0); add(rk, "C:" + df.ct, v, 0, pc, 0); add(rk, "T", v, 0, pc, 0);
          if (v > 0) used.add(df.ct + "|" + df.nm);
        }
      }
    }
    return { cells, used, rows: [...rowsSeen], n, orders: orders.size, samples };
  }
  const value = x => !x || (S.metric === "qc" ? x[1] <= 0 : x[3] <= 0) ? null : (S.metric === "qc" ? 100 * x[0] / x[1] : x[2] / x[3]);

  function defectAxis(res) {
    const present = new Map();
    for (const key of res.cells.keys()) {
      const dk = key.split("\u0001")[1]; if (!dk.startsWith("N:")) continue;
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
    for (const key of res.cells.keys()) { const dk = key.split("\u0001")[1]; if (dk.startsWith("C:")) have.add(dk.slice(2)); }
    return cats.filter(c => have.has(c) && (!S.cat || c === S.cat));
  }

  // ---------- rendering ----------------------------------------------------
  const fmt = v => v === null ? "" : v.toFixed(2);
  let drillCtx = {}, rowLabelOf = k => k;
  function cellHtml(res, rk, dk, max, cls) {
    const v = value(res.cells.get(rk + "\u0001" + dk));
    if (v === null) return `<td class="na ${cls || ""}"></td>`;
    const h = max > 0 ? Math.round(Math.sqrt(Math.min(v / max, 1)) * 42) : 0;
    const id = "c" + Object.keys(drillCtx).length;
    drillCtx[id] = { rk, dk };
    return `<td class="${v === 0 ? "zero " : ""}${cls || ""}" style="--h:${h}%" data-c="${id}" tabindex="0">${fmt(v)}</td>`;
  }
  function maxOf(res, rowKeys, dks) {
    let m = 0;
    for (const rk of rowKeys) for (const dk of dks) { const v = value(res.cells.get(rk + "\u0001" + dk)); if (v !== null && v > m) m = v; }
    return m;
  }
  const catStyle = c => `--cat:${catColor[c]};--cat-ink:${catInk(catColor[c])}`;
  function renderUnitRows(res, rows, rowLabel, withDefects, rowClass) {
    const axis = withDefects ? defectAxis(res) : catAxis(res).map(c => ({ cat: c, names: [] }));
    if (!axis.length) return emptyHtml("No defects were recorded for this selection.");
    const leaf = axis.flatMap(g => g.names.map(nm => "N:" + nm));
    const max = maxOf(res, rows.filter(r => r !== "__all"), withDefects ? leaf : axis.map(g => "C:" + g.cat));
    let h1 = `<tr><th class="rowh" rowspan="${withDefects ? 2 : 1}">${esc(rowLabel)}</th>`, h2 = "<tr>";
    for (const g of axis) {
      if (withDefects) {
        h1 += `<th class="cat" colspan="${g.names.length + 1}" style="${catStyle(g.cat)}">${esc(g.cat)}</th>`;
        for (const nm of g.names) h2 += `<th class="dn" title="${esc(nm)}">${esc(nm)}</th>`;
        h2 += `<th class="dn tot" style="${catStyle(g.cat)};background:var(--cat);color:var(--cat-ink)">${esc(g.cat)} total</th>`;
      } else h1 += `<th class="cat" style="${catStyle(g.cat)}">${esc(g.cat)}</th>`;
    }
    h1 += `<th class="tot" rowspan="${withDefects ? 2 : 1}">All defects</th></tr>`; h2 += "</tr>";
    let body = "";
    for (const rk of rows) {
      const isAll = rk === "__all", cls = isAll ? "gt" : (rowClass ? rowClass(rk) : "");
      const label = isAll ? "Grand total" : rowLabelOf(rk);
      body += `<tr class="${cls}"><th title="${esc(label)}">${esc(label)}</th>`;
      for (const g of axis) {
        for (const nm of g.names) body += cellHtml(res, rk, "N:" + nm, max);
        body += cellHtml(res, rk, "C:" + g.cat, withDefects ? max * 1.5 : max, withDefects ? "tot" : "");
      }
      body += cellHtml(res, rk, "T", max * 1.5, "tot") + "</tr>";
    }
    return `<div class="tablewrap"><table class="pv"><thead>${h1}${withDefects ? h2 : ""}</thead><tbody>${body}</tbody></table></div>`;
  }
  function renderWeek(res, weeks) {
    const axis = defectAxis(res);
    if (!axis.length) return emptyHtml("No defects were recorded for this selection.");
    const cols = [...weeks, "__all"], leafRows = axis.flatMap(g => g.names.map(n => "N:" + n));
    const m = maxOf(res, weeks, leafRows);
    const head = `<tr><th class="rowh">Defect</th>` + weeks.map(w => `<th title="${esc(weekRange(w))}">${weekLabel(w)}</th>`).join("") + `<th class="tot">Whole period</th></tr>`;
    const row = (label, plain, dk, cls, scale) => {
      let s = `<tr class="${cls}"><th title="${esc(plain)}">${label}</th>`;
      for (const w of cols) s += cellHtml(res, w, dk, m * scale, w === "__all" ? "tot" : "");
      return s + "</tr>";
    };
    let body = "";
    for (const g of axis) {
      body += row(`<span class="dr-cat" style="${catStyle(g.cat)}">${esc(g.cat)}</span> total`, `${g.cat} total`, "C:" + g.cat, "grp", 1.5);
      for (const nm of g.names) body += row(esc(nm), nm, "N:" + nm, "child", 1);
    }
    body += row("All defects", "All defects", "T", "gt", 1.5);
    return `<div class="tablewrap"><table class="pv"><thead>${head}</thead><tbody>${body}</tbody></table></div>`;
  }

  // Weeks run Saturday to Friday (DefectReportService.WeekStart).
  function weekStart(iso) { const d = new Date(iso + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() - (d.getUTCDay() + 1) % 7); return d.toISOString().slice(0, 10); }
  const MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const dm = iso => { const d = new Date(iso + "T00:00:00Z"); return `${d.getUTCDate()} ${MON[d.getUTCMonth()]}`; };
  const addDays = (iso, n) => { const d = new Date(iso + "T00:00:00Z"); d.setUTCDate(d.getUTCDate() + n); return d.toISOString().slice(0, 10); };
  const weekLabel = w => `Sat ${dm(w)}`;
  const weekRange = w => `Saturday ${dm(w)} to Friday ${dm(addDays(w, 6))}`;
  const emptyHtml = (msg, extra) => `<div class="empty"><div>${esc(msg)}</div>${extra || ""}</div>`;

  function render() {
    drillCtx = {};
    const tab = TABS.find(t => t.id === S.tab);
    let html = `<div class="ptitle"><h2>${tab.title}</h2><span class="hint">${tab.hint} ${S.metric === "qc" ? "Showing QC report %." : "Showing the Excel average."}</span></div>`;
    if (!D) { $("panel").innerHTML = html; return; }
    if (needsFruit(S.tab) && !S.major) {
      const majors = [...new Set(units.filter(u => passes(u, "major")).map(u => u.mj).filter(Boolean))].sort();
      html += emptyHtml("This report lists every defect of one fruit, so pick a major category to start.",
        `<div class="chips">${majors.map(m => `<button type="button" data-major="${esc(m)}">${esc(m)}</button>`).join("")}</div>`);
      $("panel").innerHTML = html; updateSummary(null); return;
    }
    let res, body;
    if (S.tab === "week") {
      res = compute(u => [weekStart(u.r), "__all"]);
      body = renderWeek(res, res.rows.filter(r => r !== "__all").sort());
    } else if (S.tab === "class") {
      res = compute(u => [u.sm, u.sm + "\u0002" + (u.c || "(no class)"), "__all"]);
      const subs = res.rows.filter(r => r !== "__all" && !r.includes("\u0002")).sort((a, b) => a.localeCompare(b));
      const rows = [];
      for (const s of subs) { rows.push(s); rows.push(...res.rows.filter(r => r.startsWith(s + "\u0002")).sort()); }
      rows.push("__all");
      rowLabelOf = k => k.includes("\u0002") ? k.split("\u0002")[1] : (k || "(no sub-major)");
      body = renderUnitRows(res, rows, "Sub-major / class", true, k => k.includes("\u0002") ? "child" : "grp");
    } else if (S.tab === "branch") {
      res = compute(u => [u.p, "__all"]);
      rowLabelOf = plantName;
      body = renderUnitRows(res, [...res.rows.filter(r => r !== "__all").sort(), "__all"], "Branch", true);
    } else {
      const key = S.tab === "sub" ? "sm" : "mj";
      res = compute(u => [u[key] || "(none)", "__all"]);
      rowLabelOf = k => k;
      body = renderUnitRows(res, [...res.rows.filter(r => r !== "__all").sort((a, b) => a.localeCompare(b)), "__all"],
        S.tab === "sub" ? "Sub-major category" : "Major category", false);
    }
    $("panel").innerHTML = html + (res.n ? body : emptyHtml("Nothing matches these filters. Widen the dates or clear a filter."));
    const t = $("panel").querySelector("thead tr"), tbl = $("panel").querySelector("table");
    if (t && tbl) tbl.style.setProperty("--h1", t.getBoundingClientRect().height + "px");
    updateSummary(res);
  }
  function updateSummary(res) {
    const range = D ? `${dm(D.from)} – ${dm(D.to)}` : "";
    $("summary").innerHTML = res ? `<b>${res.orders.toLocaleString()}</b> orders · <b>${res.samples.toLocaleString()}</b> samples · ${range}` : range;
  }
  function renderTabs() {
    $("tabs").innerHTML = TABS.map(t => `<button type="button" role="tab" id="tab-${t.id}" aria-selected="${t.id === S.tab}" aria-controls="panel" tabindex="${t.id === S.tab ? 0 : -1}" data-tab="${t.id}">${t.label}</button>`).join("");
  }

  // ---------- drill-down (mirrored by DefectReportService.DrillAsync) --------
  function unitInRow(u, rk) {
    if (rk === "__all") return true;
    if (S.tab === "week") return weekStart(u.r) === rk;
    if (S.tab === "class") return rk.includes("\u0002") ? (u.sm + "\u0002" + (u.c || "(no class)")) === rk : u.sm === rk;
    if (S.tab === "branch") return u.p === rk;
    return (u[S.tab === "sub" ? "sm" : "mj"] || "(none)") === rk;
  }
  function describe(rk, dk) {
    const what = dk === "T" ? "All defects" : dk.startsWith("C:") ? `${dk.slice(2)} defects` : dk.slice(2);
    let where;
    if (rk === "__all") where = S.tab === "week" ? "Whole period" : "All rows";
    else if (S.tab === "week") where = weekRange(rk);
    else if (S.tab === "class") where = rk.replace("\u0002", " · ");
    else if (S.tab === "branch") where = plantName(rk);
    else where = rk;
    return { what, where };
  }
  function openDrill(id) {
    const ctx = drillCtx[id]; if (!ctx) return;
    const { rk, dk } = ctx, catOk = c => !S.cat || c === S.cat;
    const matches = df => dk === "T" || (dk.startsWith("C:") && df.ct === dk.slice(2)) || (dk.startsWith("N:") && df.nm === dk.slice(2));
    const perQo = new Map();
    for (const u of units) {
      if (!passes(u) || !unitInRow(u, rk)) continue;
      const set = (D.groupDefects[u.g] || []).map(i => defs[i]).filter(df => catOk(df.ct) && matches(df));
      if (!set.length) continue;
      let v = 0, pc = 0;
      for (const [d, dv, dpc] of factsBy[u.i]) { const df = defs[d]; if (catOk(df.ct) && matches(df)) { v += dv; pc += dpc; } }
      let o = perQo.get(u.q);
      if (!o) { o = { q: u.q, n: u.n, r: u.r, p: u.p, v: u.v, k: u.k, items: new Set(), ns: 0, sz: 0, val: 0, pc: 0, rows: 0 }; perQo.set(u.q, o); }
      o.items.add(u.sm + (u.c ? " · " + u.c : "")); o.ns += u.ns; o.sz += u.sz; o.val += v; o.pc += pc; o.rows += u.ns * set.length;
    }
    const list = [...perQo.values()].map(o => ({ ...o, pct: S.metric === "qc" ? (o.sz > 0 ? 100 * o.val / o.sz : null) : (o.rows > 0 ? o.pc / o.rows : null) }))
      .sort((a, b) => (b.pct ?? -1) - (a.pct ?? -1) || b.r.localeCompare(a.r));
    const sum = k => list.reduce((s, o) => s + o[k], 0);
    const fig = S.metric === "qc" ? (sum("sz") ? 100 * sum("val") / sum("sz") : null) : (sum("rows") ? sum("pc") / sum("rows") : null);
    const d = describe(rk, dk);
    const context = [d.where, S.major, S.sub, S.cls, S.plant && plantName(S.plant), S.supplier].filter(Boolean).join(" · ");
    $("dTitle").textContent = d.what;
    $("dSub").textContent = context;
    $("dKpis").innerHTML = [
      [S.metric === "qc" ? "QC report %" : "Excel average", fig === null ? "—" : fig.toFixed(2) + "%"],
      ["Orders", list.length.toLocaleString()],
      ["Inspected units", Math.round(sum("sz")).toLocaleString()],
      ["Defective units", Math.round(sum("val")).toLocaleString()]
    ].map(([l, v]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");
    $("dTable").innerHTML = `<thead><tr><th>QC order</th><th>Received</th><th>Branch</th><th>Supplier</th><th>Container</th><th>Material</th><th class="r">Samples</th><th class="r">Inspected</th><th class="r">Defective</th><th class="r">%</th></tr></thead><tbody>` +
      list.map(o => `<tr><td><a href="${URL_QO}${o.q}" target="_blank" rel="noopener">${esc(o.n)}</a></td><td>${o.r}</td><td>${esc(o.p)}</td><td>${esc(o.v)}</td><td>${esc(o.k)}</td><td>${esc([...o.items].join(", "))}</td><td class="r">${o.ns}</td><td class="r">${Math.round(o.sz).toLocaleString()}</td><td class="r">${Math.round(o.val).toLocaleString()}</td><td class="r"><b>${o.pct === null ? "—" : o.pct.toFixed(2)}</b></td></tr>`).join("") + `</tbody>`;
    const q = new URLSearchParams({ From: D.from, To: D.to, Plant: S.plant, Supplier: S.supplier, Major: S.major, Sub: S.sub, Class: S.cls, Cat: S.cat, Tab: S.tab, RowKey: rk, DefectKey: dk, Metric: S.metric, title: `${d.what} · ${context}` });
    $("dExport").href = URL_DRILL + "?" + q.toString();
    $("scrim").hidden = false; $("drawer").hidden = false; $("dClose").focus();
  }
  const closeDrill = () => { $("scrim").hidden = true; $("drawer").hidden = true; };

  function example() {
    const sub = units.some(u => u.sm === "Apples Royal Gala") ? "Apples Royal Gala" : (units[0] && units[0].sm);
    const us = units.filter(u => u.sm === sub);
    let v = 0, sz = 0, pc = 0, rows = 0;
    for (const u of us) { sz += u.sz; rows += u.ns * ((groupInfo[u.g] || { catCount: {} }).catCount.Major || 0); for (const [d, dv, dpc] of factsBy[u.i]) if (defs[d].ct === "Major") { v += dv; pc += dpc; } }
    $("exQc").textContent = sz && rows ? `${sub}, Major: ${Math.round(v).toLocaleString()} defective ÷ ${Math.round(sz).toLocaleString()} inspected = ${(100 * v / sz).toFixed(2)}%` : "";
    $("exXls").textContent = sz && rows ? `${sub}, Major: average of ${rows.toLocaleString()} sample-defect rows = ${(pc / rows).toFixed(2)}%` : "";
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
      const res = await fetch(`${URL_DATA}?from=${from}&to=${to}`, { headers: { Accept: "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const data = await res.json();
      if (seq !== loadSeq) return;
      build(data);
      if (data.from !== from) $("fFrom").value = data.from;   // clamped to MAX_DAYS by the server
      refreshOptions(); render(); example();
      $("foot").textContent = `${units.length.toLocaleString()} material lines on finished inspections received ${dm(D.from)} – ${dm(D.to)} (at most ${MAX_DAYS} days). Loaded ${D.generated}.`;
    } catch (e) {
      $("panel").innerHTML = emptyHtml("The report data could not be loaded. Reload the page, or try a shorter date range.");
    } finally { if (seq === loadSeq) $("loading").hidden = true; }
  }

  // ---------- wiring -------------------------------------------------------
  function readFilters() {
    S.plant = $("fPlant").value; S.supplier = $("fSupplier").value; S.major = $("fMajor").value;
    S.sub = $("fSub").value; S.cls = $("fClass").value; S.cat = $("fCat").value; S.hide = $("fHide").checked;
  }
  const update = () => { readFilters(); refreshOptions(); render(); };
  for (const id of ["fPlant", "fSupplier", "fMajor", "fSub", "fClass", "fCat", "fHide"]) $(id).addEventListener("change", update);
  for (const id of ["fFrom", "fTo"]) $(id).addEventListener("change", load);
  $("fReset").addEventListener("click", () => {
    for (const id of ["fPlant", "fSupplier", "fMajor", "fSub", "fClass", "fCat"]) $(id).value = "";
    $("fHide").checked = true; update();
  });
  function setMetric(m) {
    S.metric = m;
    for (const [id, on] of [["mQc", m === "qc"], ["mXls", m === "xls"]]) {
      $(id).setAttribute("aria-pressed", on); $(id).classList.toggle("btn-primary", on); $(id).classList.toggle("btn-outline-primary", !on);
    }
    store.set("defectReports.metric", m); render();
  }
  $("mQc").addEventListener("click", () => setMetric("qc"));
  $("mXls").addEventListener("click", () => setMetric("xls"));
  function selectTab(id) { S.tab = id; store.set("defectReports.tab", id); try { history.replaceState(null, "", "#" + id); } catch (e) {} renderTabs(); render(); }
  $("tabs").addEventListener("click", e => { const b = e.target.closest("[data-tab]"); if (b) selectTab(b.dataset.tab); });
  $("tabs").addEventListener("keydown", e => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const i = TABS.findIndex(t => t.id === S.tab), n = TABS[(i + (e.key === "ArrowRight" ? 1 : TABS.length - 1)) % TABS.length];
    selectTab(n.id); $("tab-" + n.id).focus();
  });
  $("panel").addEventListener("click", e => {
    const m = e.target.closest("[data-major]"); if (m) { $("fMajor").value = m.dataset.major; update(); return; }
    const c = e.target.closest("td[data-c]"); if (c) openDrill(c.dataset.c);
  });
  $("panel").addEventListener("keydown", e => { if (e.key === "Enter") { const c = e.target.closest("td[data-c]"); if (c) openDrill(c.dataset.c); } });
  $("dClose").addEventListener("click", closeDrill);
  $("scrim").addEventListener("click", closeDrill);
  document.addEventListener("keydown", e => { if (e.key === "Escape" && !$("drawer").hidden) closeDrill(); });

  const hash = (location.hash || "").slice(1), savedTab = store.get("defectReports.tab");
  S.tab = TABS.some(t => t.id === hash) ? hash : (TABS.some(t => t.id === savedTab) ? savedTab : "major");
  if (store.get("defectReports.metric") === "xls") setMetric("xls");
  renderTabs(); render(); load();
})();
