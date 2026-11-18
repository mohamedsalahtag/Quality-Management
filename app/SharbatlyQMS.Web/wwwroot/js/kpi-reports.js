// Reports -> KPI reports. One table per area (volume, speed, quality, claims),
// one row per user, compared with the team: a Team median and Team average
// row, and each figure tinted when it is clearly better or worse than the
// team median. Figures come from /KpiReports/Data; the drill panel lists the
// orders behind one user's figure from /KpiReports/Drill, which is built from
// the same events (KpiReportService), so the two always agree.
(function () {
  "use strict";
  const root = document.getElementById("dr");
  if (!root) return;
  const URL_DATA = root.dataset.urlData, URL_DRILL = root.dataset.urlDrill, URL_EXPORT = root.dataset.urlExport;
  const URL_QO = root.dataset.urlQo.replace(/\/0$/, "/"), URL_ARR = root.dataset.urlArrival.replace(/\/0$/, "/");
  const MAX_DAYS = parseInt(root.dataset.maxDays || "366", 10);
  const TOKEN = (document.querySelector("#drToken input[name=__RequestVerificationToken]") || {}).value || "";
  const $ = id => document.getElementById(id);
  const esc = s => String(s ?? "").replace(/[&<>"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[ch]);
  const store = { get(k) { try { return localStorage.getItem(k); } catch (e) { return null; } }, set(k, v) { try { localStorage.setItem(k, v); } catch (e) {} } };
  const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
  const MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  const dm = iso => { const d = new Date(iso + "T00:00:00Z"); return `${d.getUTCDate()} ${MON[d.getUTCMonth()]}`; };
  const fmtInt = v => v === null || v === undefined ? "" : Math.round(v).toLocaleString();
  const fmt1 = v => v === null || v === undefined ? "" : (+v).toFixed(1);

  const TABS = [
    { id: "volume",  label: "Volume",             title: "How much each user did" },
    { id: "speed",   label: "Speed",              title: "How fast each step was done (median)" },
    { id: "quality", label: "Quality of work",    title: "How often work had to be redone (lower is better)" },
    { id: "claims",  label: "Claims & reporting", title: "Claim decisions, notes and reports e-mailed" }
  ];
  const TINT_MIN = 5, TINT_GAP = 0.25;

  // ---------- state ---------------------------------------------------------
  let D = null, KPI = {}, loadSeq = 0, current = null;
  const S = { tab: "volume", plant: [], role: [], user: [], active: true, sort: {}, chart: {} };

  const plantName = c => { const n = D && D.plantNames && D.plantNames[c]; return n && n !== c ? `${n} (${c})` : (c || "(none)"); };
  const val = (k, u) => { const by = D.values[k]; return by && by[u] ? by[u] : null; };
  const n = (k, u) => { const v = val(k, u); return v ? +v[0] : 0; };

  /** The figure shown for a KPI: count, median time, or rate %. Null = nothing to show. */
  function figure(k, u) {
    if (k.kind === "rate") { const den = n(k.den, u); return den > 0 ? 100 * n(k.num, u) / den : null; }
    const v = val(k.key, u); if (!v) return null;
    return k.kind === "dur" ? +v[1] : +v[0];
  }
  /** How many cases a figure rests on, for the "too few to colour" rule. */
  function basis(k, u) {
    if (k.kind === "rate") return n(k.den, u);
    const v = val(k.key, u); return v ? +v[0] : 0;
  }
  const show = (k, v) => v === null || v === undefined ? "" : k.kind === "rate" ? fmt1(v) + "%" : k.kind === "dur" ? fmt1(v) : fmtInt(v);

  // ---------- filters (multi-select, as on Defect reports) -------------------
  const FILTERS = [
    { id: "fPlant", s: "plant", all: "All branches", name: "Branch", values: () => [...new Set(D.users.flatMap(u => u.plants))].sort(), label: plantName },
    { id: "fRole",  s: "role",  all: "All roles", name: "Role", values: () => [...new Set(D.users.map(u => u.role))].sort() },
    { id: "fUser",  s: "user",  all: "All users", name: "User", values: () => D.users.map(u => u.user), label: u => (D.users.find(x => x.user === u) || {}).name || u }
  ];
  const text = f => v => f.label ? f.label(v) : v;
  function drawFilters() {
    for (const f of FILTERS) {
      const box = $(f.id);
      if (!box.firstChild) box.innerHTML = `<button type="button" id="${f.id}-btn" class="btn btn-sm btn-outline-secondary dropdown-toggle w-100 text-start text-truncate" data-bs-toggle="dropdown" data-bs-auto-close="outside" aria-expanded="false"></button><div class="dropdown-menu p-2 dr-ms-menu"></div>`;
      const values = D ? f.values() : [], t = text(f);
      S[f.s] = S[f.s].filter(v => values.includes(v));
      const menu = box.querySelector(".dropdown-menu");
      if (!menu.classList.contains("show"))
        menu.innerHTML = `<div class="d-flex justify-content-between align-items-center mb-1"><span class="small text-muted">${esc(f.name)}</span><button type="button" class="btn btn-sm btn-link p-0" data-ms-clear>Clear</button></div>`
          + (values.length > 8 ? `<input type="search" class="form-control form-control-sm my-1" data-ms-search placeholder="Search…" aria-label="Search ${esc(f.name)}" autocomplete="off">` : "")
          + values.map((v, i) => `<div class="form-check" data-ms-row><input class="form-check-input" type="checkbox" id="${f.id}-${i}" value="${esc(v)}"${S[f.s].includes(v) ? " checked" : ""}><label class="form-check-label small" for="${f.id}-${i}">${esc(t(v))}</label></div>`).join("");
      const btn = $(f.id + "-btn"), sel = S[f.s];
      btn.textContent = !sel.length ? f.all : sel.length === 1 ? t(sel[0]) : `${sel.length} selected`;
      btn.title = sel.map(t).join(", ") || f.all;
    }
  }
  function filterText() {
    const bits = FILTERS.filter(f => S[f.s].length).map(f => `${f.name}: ${S[f.s].map(text(f)).join(", ")}`);
    return bits.length ? bits : ["All users in your branches"];
  }
  function usersShown(kpis) {
    return D.users.filter(u =>
      (!S.plant.length || u.plants.some(p => S.plant.includes(p))) && (!S.role.length || S.role.includes(u.role))
      && (!S.user.length || S.user.includes(u.user))
      && (!S.active || kpis.some(k => figure(k, u.user) !== null && (k.kind !== "count" || figure(k, u.user) > 0))));
  }

  // ---------- team figures ---------------------------------------------------
  const median = a => { if (!a.length) return null; const s = [...a].sort((x, y) => x - y), m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2; };
  const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
  function team(k, users) {
    const vs = users.map(u => figure(k, u.user)).filter(v => v !== null);
    return { med: median(vs), avg: mean(vs) };
  }
  function tint(k, v, b, med) {
    if (v === null || med === null || k.better === "none" || b < TINT_MIN || med === 0 && v === 0) return "";
    const base = Math.max(Math.abs(med), k.kind === "rate" ? 1 : 0.5), d = (v - med) / base;
    if (Math.abs(d) < TINT_GAP) return "";
    const better = k.better === "high" ? d > 0 : d < 0;
    return better ? " kpi-good" : " kpi-bad";
  }

  // ---------- render -----------------------------------------------------------
  function render() {
    const tab = TABS.find(t => t.id === S.tab);
    const head = `<div class="ptitle"><div><h2>${tab.title}</h2><span class="hint">Click a column heading to sort; click a figure to see the orders behind it.</span></div><div class="acts" id="acts"></div></div>`;
    if (!D) { $("panel").innerHTML = head; $("chart").hidden = true; return; }
    const kpis = D.kpis.filter(k => k.tab === S.tab);
    const users = usersShown(kpis);
    const sort = S.sort[S.tab] || { key: kpis[0].key, dir: -1 };
    const sk = kpis.find(k => k.key === sort.key) || kpis[0];
    users.sort((a, b) => {
      const x = figure(sk, a.user), y = figure(sk, b.user);
      if (x === null && y === null) return a.name.localeCompare(b.name);
      if (x === null) return 1; if (y === null) return -1;
      return sort.dir * (x - y) || a.name.localeCompare(b.name);
    });
    const teams = kpis.map(k => team(k, users));
    current = { tab, kpis, users, teams };

    let body;
    if (!users.length) body = `<div class="empty"><div>No user has figures on this tab for the period and filters chosen.</div></div>`;
    else {
      const arrow = k => k.key === sk.key ? (sort.dir < 0 ? " ▾" : " ▴") : "";
      const th = `<tr><th class="rowh">User</th><th>Role</th><th>Branches</th>${kpis.map(k =>
        `<th class="sortable" data-sort="${k.key}" title="${esc(k.hint || "")}${k.better === "low" ? " Lower is better." : k.better === "high" ? " Higher is better." : ""}">${esc(k.label)}${arrow(k)}</th>`).join("")}</tr>`;
      const rows = users.map(u => `<tr><th title="${esc(u.user)}">${esc(u.name)}</th><td class="na txt">${esc(u.role)}</td><td class="na txt">${esc(u.plants.join(", "))}</td>${kpis.map((k, i) => {
        const v = figure(k, u.user);
        if (v === null) return `<td class="na"></td>`;
        const raw = val(k.kind === "rate" ? k.num : k.key, u.user);
        const tip = k.kind === "rate" ? `${fmtInt(n(k.num, u.user))} of ${fmtInt(n(k.den, u.user))}`
          : k.kind === "dur" ? `Median ${fmt1(v)} · average ${fmt1(raw && raw[2])} · ${fmtInt(raw && raw[0])} cases` : "";
        const clickable = k.key !== "active_days" && (k.kind !== "rate" || n(k.num, u.user) > 0);
        return `<td class="${clickable ? "" : "na"}${tint(k, v, basis(k, u.user), teams[i].med)}"${clickable ? ` tabindex="0" data-k="${k.key}" data-u="${esc(u.user)}"` : ""} title="${esc(tip)}">${show(k, v)}</td>`;
      }).join("")}</tr>`).join("");
      const teamRow = (label, f) => `<tr class="gt"><th>${label}</th><td class="na"></td><td class="na"></td>${kpis.map((k, i) => `<td class="na tot">${show(k, f(teams[i]))}</td>`).join("")}</tr>`;
      body = `<div class="tablewrap"><table class="pv kpi-table"><thead>${th}</thead><tbody>${rows}${teamRow("Team median", t => t.med)}${teamRow("Team average", t => t.avg)}</tbody></table></div>`;
    }
    $("panel").innerHTML = head + body;
    $("acts").innerHTML = users.length ? `<button type="button" class="btn btn-sm btn-outline-success" data-export="xlsx"><i class="bi bi-file-earmark-excel me-1"></i>Excel</button><button type="button" class="btn btn-sm btn-outline-danger" data-export="pdf"><i class="bi bi-file-earmark-pdf me-1"></i>PDF</button>` : "";
    drawChart(sk, users, teams[kpis.indexOf(sk)]);
    drawSummary(users);
  }

  function drawChart(k, users, t) {
    const el = $("chart");
    const list = users.filter(u => figure(k, u.user) !== null).slice(0, 30);
    if (!window.Plotly || !list.length) { el.hidden = true; return; }
    el.hidden = false;
    const ink = css("--theme-text") || "#212529", grid = css("--theme-border") || "#dee2e6";
    const x = list.map(u => u.name), y = list.map(u => +(+figure(k, u.user)).toFixed(2));
    const color = list.map(u => { const c = tint(k, figure(k, u.user), basis(k, u.user), t.med); return c.includes("good") ? "#54a24b" : c.includes("bad") ? "#e45756" : "#4c78a8"; });
    const traces = [{ x, y, type: "bar", name: k.label, marker: { color } }];
    if (t.med !== null) traces.push({ x: [x[0], x[x.length - 1]], y: [t.med, t.med], type: "scatter", mode: "lines", name: "Team median", line: { color: ink, dash: "dash", width: 1.5 } });
    Plotly.react(el, traces, {
      margin: { l: 60, r: 20, t: 30, b: 110 }, height: 340, paper_bgcolor: "rgba(0,0,0,0)", plot_bgcolor: "rgba(0,0,0,0)",
      font: { color: ink, size: 11 }, legend: { orientation: "h", y: 1.12 }, title: { text: k.label, font: { size: 13 } },
      yaxis: { gridcolor: grid, rangemode: "tozero", ticksuffix: k.kind === "rate" ? "%" : "" }, xaxis: { gridcolor: grid, tickangle: -35, automargin: true }
    }, { displayModeBar: false, responsive: true });
  }

  function drawSummary(users) {
    const sum = k => users.reduce((s, u) => s + n(k, u.user), 0);
    const med = k => { const def = D.kpis.find(x => x.key === k); const t = team(def, users); return t.med === null ? "—" : fmt1(t.med); };
    const range = `${dm(D.from)} – ${dm(D.to)} ${D.to.slice(0, 4)}`;
    $("summary").innerHTML = [["Period", range], ["Users shown", users.length.toLocaleString()],
      ["Orders finished", fmtInt(sum("qo_finished"))], ["Samples entered", fmtInt(sum("samples"))],
      ["Median review time (h)", med("submit_finish")], ["Median received → opened (days)", med("recv_open")]]
      .map(([l, v]) => `<div class="kp"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");
  }

  function renderTabs() {
    $("tabs").innerHTML = TABS.map(t => `<button type="button" role="tab" id="tab-${t.id}" aria-selected="${t.id === S.tab}" aria-controls="panel" tabindex="${t.id === S.tab ? 0 : -1}" data-tab="${t.id}">${t.label}</button>`).join("");
  }

  // ---------- drill ----------------------------------------------------------------
  function openDrawer(title, sub, kpis, tableHtml) {
    $("dTitle").textContent = title; $("dSub").textContent = sub;
    $("dKpis").innerHTML = kpis.map(([l, v]) => `<div class="kpi"><div class="l">${l}</div><div class="v">${v}</div></div>`).join("");
    $("dTable").innerHTML = tableHtml;
    $("scrim").hidden = false; $("drawer").hidden = false; $("dClose").focus();
  }
  const closeDrill = () => { $("scrim").hidden = true; $("drawer").hidden = true; };
  function setDrawerMax(on) {
    $("drawer").classList.toggle("max", on);
    const b = $("dSize"), label = on ? "Restore" : "Maximize";
    b.setAttribute("aria-pressed", on); b.title = label; b.setAttribute("aria-label", label);
    b.innerHTML = `<i class="bi ${on ? "bi-arrows-angle-contract" : "bi-arrows-angle-expand"}"></i><span class="ms-1 d-none d-sm-inline">${label}</span>`;
  }
  async function openDrill(key, user) {
    const k = KPI[key], u = D.users.find(x => x.user === user);
    const what = k.kind === "rate" ? `${k.label.replace(/ %$/, "")} — the ${fmtInt(n(k.num, user))} cases counted` : k.label;
    openDrawer(`${u ? u.name : user}: ${what}`, `${dm(D.from)} – ${dm(D.to)} ${D.to.slice(0, 4)}`, [], `<tbody><tr><td><span class="spinner-border spinner-border-sm me-1"></span>Loading…</td></tr></tbody>`);
    try {
      const q = new URLSearchParams({ kpi: key, user, from: D.from, to: D.to });
      const res = await fetch(`${URL_DRILL}?${q}`, { headers: { Accept: "application/json" } });
      if (!res.ok) throw new Error(res.status);
      const rows = await res.json();
      const timed = k.kind === "dur", qty = rows.reduce((s, r) => s + (+r.qty || 0), 0);
      const kp = [["Rows", rows.length.toLocaleString()]];
      if (!timed && qty !== rows.length) kp.push(["Total", fmtInt(qty)]);
      if (timed) { const d = rows.filter(r => r.dur !== null).map(r => +r.dur); kp.push(["Median", fmt1(median(d))], ["Average", fmt1(mean(d))]); }
      const unit = key === "recv_open" ? "Days" : "Hours";
      const html = `<thead><tr><th>When</th><th>QC order</th><th>Arrival</th><th>Container</th><th>Branch</th><th class="num">${timed ? unit : "Count"}</th></tr></thead><tbody>` +
        (rows.length ? rows.map(r => `<tr><td>${esc(new Date(r.at + (r.at.endsWith("Z") ? "" : "Z")).toLocaleString())}</td>` +
          `<td>${r.qoId ? `<a href="${URL_QO}${r.qoId}" target="_blank" rel="noopener">${esc(r.qoNo)}</a>` : ""}</td>` +
          `<td>${r.arrivalId ? `<a href="${URL_ARR}${r.arrivalId}" target="_blank" rel="noopener">${esc(r.arrivalNo)}</a>` : ""}</td>` +
          `<td>${esc(r.containerNo)}</td><td>${esc(plantName(r.plant))}</td><td class="num">${timed ? fmt1(r.dur) : fmtInt(r.qty)}</td></tr>`).join("")
          : `<tr><td colspan="6" class="text-muted">Nothing to list.</td></tr>`) + "</tbody>";
      openDrawer($("dTitle").textContent, $("dSub").textContent, kp, html);
    } catch (e) {
      $("dTable").innerHTML = `<tbody><tr><td class="text-danger">The list could not be loaded.</td></tr></tbody>`;
    }
  }

  // ---------- export ----------------------------------------------------------------
  async function exportReport(format, btn) {
    if (!current) return;
    const { tab, kpis, users, teams } = current;
    btn.disabled = true;
    try {
      let chart = null;
      if (window.Plotly && !$("chart").hidden) { try { chart = await Plotly.toImage($("chart"), { format: "png", width: 1400, height: 420, scale: 1 }); } catch (e) { chart = null; } }
      const cols = [["Role", "text"], ["Branches", "text"]].map(([label, kind]) => ({ group: "", label, color: null, kind }))
        .concat(kpis.map(k => ({ group: "", label: k.label, color: null, kind: k.kind === "count" ? "count" : "pct" })));
      const rows = users.map(u => ({ label: u.name, level: "", indent: 0, trends: [],
        values: [null, null].concat(kpis.map(k => figure(k, u.user))), texts: [u.role, u.plants.join(", ")].concat(kpis.map(() => null)) }));
      for (const [label, f] of [["Team median", t => t.med], ["Team average", t => t.avg]])
        rows.push({ label, level: "gt", indent: 0, trends: [], values: [null, null].concat(teams.map(f)), texts: [] });
      const payload = {
        format, title: tab.title, tab: tab.label, hint: "", period: `Actions ${D.from} to ${D.to}`, filters: filterText(),
        kpis: [["Users", users.length.toLocaleString()]], rowLabel: "User", columns: cols, rows, chart, charts: [],
        generated: new Date().toLocaleString(),
        footnote: "Times are medians in hours (received → opened in days); rates are % of the user's own cases. A step's time belongs to whoever completed it; a send-back counts against the submitter, a reopen or reinspection against the finisher."
      };
      // X-CSRF-TOKEN: the antiforgery header name set in Program.cs (AddAntiforgery).
      const r = await fetch(URL_EXPORT, { method: "POST", headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": TOKEN }, body: JSON.stringify(payload) });
      if (!r.ok) throw new Error(r.status);
      const blob = await r.blob(), a = document.createElement("a");
      a.href = URL.createObjectURL(blob); a.download = `kpi-report-${tab.id}-${D.from}-${D.to}.${format}`;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    } catch (e) {
      alert("The report could not be exported. Try again, or narrow the filters.");
    } finally { btn.disabled = false; }
  }

  // ---------- loading ---------------------------------------------------------------
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
      D = data; KPI = Object.fromEntries(D.kpis.map(k => [k.key, k]));
      if (D.from !== from) $("fFrom").value = D.from;   // clamped to MAX_DAYS by the server
      drawFilters(); render();
      $("foot").textContent = `${D.users.length.toLocaleString()} users with actions ${dm(D.from)} – ${dm(D.to)} (at most ${MAX_DAYS} days). Loaded ${D.generated}.`;
    } catch (e) {
      $("panel").innerHTML = `<div class="empty"><div>The figures could not be loaded. Reload the page, or try a shorter date range.</div></div>`;
    } finally { if (seq === loadSeq) $("loading").hidden = true; }
  }

  // ---------- wiring ----------------------------------------------------------------
  const filterBox = document.querySelector(".dr-filters");
  const msOf = el => { const box = el.closest(".dr-ms"); return box ? FILTERS.find(f => f.id === box.id) : null; };
  const update = () => { drawFilters(); render(); };
  filterBox.addEventListener("change", e => {
    if (e.target.id === "fActive") { S.active = e.target.checked; render(); return; }
    const f = e.target.type === "checkbox" ? msOf(e.target) : null; if (!f) return;
    S[f.s] = e.target.checked ? [...S[f.s], e.target.value] : S[f.s].filter(v => v !== e.target.value);
    render(); drawFilters();
  });
  filterBox.addEventListener("click", e => {
    if (!e.target.closest("[data-ms-clear]")) return;
    const f = msOf(e.target); S[f.s] = [];
    $(f.id).querySelectorAll("input[type=checkbox]").forEach(c => { c.checked = false; });
    update();
  });
  filterBox.addEventListener("input", e => {
    if (!e.target.matches("[data-ms-search]")) return;
    const q = e.target.value.toLowerCase();
    e.target.closest(".dropdown-menu").querySelectorAll("[data-ms-row]").forEach(r => { r.hidden = !r.textContent.toLowerCase().includes(q); });
  });
  filterBox.addEventListener("hidden.bs.dropdown", () => drawFilters());
  for (const id of ["fFrom", "fTo"]) $(id).addEventListener("change", load);
  $("fReset").addEventListener("click", () => { for (const f of FILTERS) S[f.s] = []; S.active = true; $("fActive").checked = true; update(); });

  function selectTab(id) { S.tab = id; store.set("kpiReports.tab", id); try { history.replaceState(null, "", "#" + id); } catch (e) {} renderTabs(); render(); }
  $("tabs").addEventListener("click", e => { const b = e.target.closest("[data-tab]"); if (b) selectTab(b.dataset.tab); });
  $("tabs").addEventListener("keydown", e => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const i = TABS.findIndex(t => t.id === S.tab), nx = TABS[(i + (e.key === "ArrowRight" ? 1 : TABS.length - 1)) % TABS.length];
    selectTab(nx.id); $("tab-" + nx.id).focus();
  });
  $("panel").addEventListener("click", e => {
    const x = e.target.closest("[data-export]"); if (x) { exportReport(x.dataset.export, x); return; }
    const h = e.target.closest("th[data-sort]");
    if (h) {
      const cur = S.sort[S.tab], k = KPI[h.dataset.sort];
      const first = k.better === "low" ? 1 : -1;
      S.sort[S.tab] = { key: k.key, dir: cur && cur.key === k.key ? -cur.dir : first };
      render(); return;
    }
    const c = e.target.closest("td[data-k]"); if (c) openDrill(c.dataset.k, c.dataset.u);
  });
  $("panel").addEventListener("keydown", e => { if (e.key === "Enter") { const c = e.target.closest("td[data-k]"); if (c) openDrill(c.dataset.k, c.dataset.u); } });
  $("dClose").addEventListener("click", closeDrill);
  $("dSize").addEventListener("click", () => setDrawerMax(!$("drawer").classList.contains("max")));
  $("drawer").querySelector(".dh").addEventListener("dblclick", e => { if (!e.target.closest("button")) setDrawerMax(!$("drawer").classList.contains("max")); });
  $("scrim").addEventListener("click", closeDrill);
  document.addEventListener("keydown", e => { if (e.key === "Escape" && !$("drawer").hidden) closeDrill(); });

  const hash = (location.hash || "").slice(1), saved = store.get("kpiReports.tab");
  S.tab = TABS.some(t => t.id === hash) ? hash : (TABS.some(t => t.id === saved) ? saved : "volume");
  renderTabs(); render(); drawFilters(); load();
})();
