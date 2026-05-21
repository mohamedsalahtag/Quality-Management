(function () {
    'use strict';

    var dataNode = document.getElementById('dash-data');
    if (!dataNode || typeof Chart === 'undefined') return;

    var data;
    try { data = JSON.parse(dataNode.textContent || '{}'); }
    catch (e) { console.warn('dashboard: bad JSON payload', e); return; }

    function cssVar(name, fallback) {
        var v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
        return v.length > 0 ? v : fallback;
    }

    function themeColors() {
        return {
            body:    cssVar('--bs-body-color',   '#212529'),
            muted:   cssVar('--bs-secondary-color', '#6c757d'),
            border:  cssVar('--bs-border-color', '#dee2e6'),
            primary: cssVar('--bs-primary',      '#0d6efd'),
            success: cssVar('--bs-success',      '#198754'),
            warning: cssVar('--bs-warning',      '#ffc107'),
            danger:  cssVar('--bs-danger',       '#dc3545'),
            info:    cssVar('--bs-info',         '#0dcaf0'),
            secondary: cssVar('--bs-secondary',  '#6c757d')
        };
    }

    Chart.defaults.font.family = cssVar('--bs-body-font-family', 'system-ui, sans-serif');
    Chart.defaults.maintainAspectRatio = false;
    Chart.defaults.plugins.legend.labels.boxWidth = 12;
    Chart.defaults.plugins.legend.labels.boxHeight = 12;

    var registry = [];

    // ---- Arrivals trend (line) ----
    var trendEl = document.getElementById('chartArrivalsTrend');
    if (trendEl && data.trend && data.trend.length) {
        var colors = themeColors();
        var labels = data.trend.map(function (p) {
            var d = new Date(p.WeekStart);
            return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
        });
        var chart = new Chart(trendEl, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [
                    { label: 'Created',   data: data.trend.map(function(p){return p.Created;}),
                      borderColor: colors.primary, backgroundColor: colors.primary + '22',
                      tension: 0.3, fill: true },
                    { label: 'Completed', data: data.trend.map(function(p){return p.Completed;}),
                      borderColor: colors.success, backgroundColor: colors.success + '22',
                      tension: 0.3, fill: true }
                ]
            },
            options: {
                plugins: { legend: { position: 'bottom', labels: { color: colors.body } } },
                scales: {
                    x: { ticks: { color: colors.muted }, grid: { color: colors.border } },
                    y: { ticks: { color: colors.muted, precision: 0 }, grid: { color: colors.border }, beginAtZero: true }
                }
            }
        });
        registry.push({ chart: chart, kind: 'trend' });
    } else if (trendEl) {
        renderEmpty(trendEl, 'No arrivals in the last 12 weeks.');
    }

    // ---- QO by status (doughnut) ----
    var qoEl = document.getElementById('chartQoStatus');
    if (qoEl && data.qoByStatus) {
        var entries = Object.keys(data.qoByStatus).map(function (k) {
            return { label: k, value: data.qoByStatus[k] };
        });
        var hasAny = entries.some(function (e) { return e.value > 0; });
        if (hasAny) {
            var colorsQ = themeColors();
            var palette = {
                'Initial':   colorsQ.secondary,
                'Open':      colorsQ.primary,
                'Reopened':  colorsQ.warning,
                'Closed':    colorsQ.success,
                'Cancelled': colorsQ.danger
            };
            var chart = new Chart(qoEl, {
                type: 'doughnut',
                data: {
                    labels: entries.map(function(e){return e.label;}),
                    datasets: [{
                        data: entries.map(function(e){return e.value;}),
                        backgroundColor: entries.map(function(e){return palette[e.label] || colorsQ.info;}),
                        borderColor: cssVar('--bs-body-bg', '#ffffff'),
                        borderWidth: 2
                    }]
                },
                options: {
                    cutout: '60%',
                    plugins: {
                        legend: { position: 'bottom', labels: { color: colorsQ.body } }
                    }
                }
            });
            registry.push({ chart: chart, kind: 'qoStatus' });
        } else {
            renderEmpty(qoEl, 'No quality orders yet.');
        }
    }

    // ---- Defect rate gauge (half-doughnut) ----
    var gaugeEl = document.getElementById('chartDefectGauge');
    if (gaugeEl) {
        var pct = data.avgDefectPct == null ? 0 : Math.min(100, Math.max(0, data.avgDefectPct));
        var hasValue = data.avgDefectPct != null;
        var redAt    = data.thresholds && data.thresholds.DefectPctRed    != null ? data.thresholds.DefectPctRed    : 10;
        var yellowAt = data.thresholds && data.thresholds.DefectPctYellow != null ? data.thresholds.DefectPctYellow : 5;
        var band = pct > redAt ? '#dc3545' : (pct > yellowAt ? '#ffc107' : '#198754');
        var trackColor = cssVar('--bs-border-color', '#e9ecef');
        var centerPlugin = {
            id: 'gaugeCenter',
            afterDraw: function (chart) {
                var ctx  = chart.ctx;
                var area = chart.chartArea;
                var cx   = (area.left + area.right) / 2;
                var cy   = area.bottom - 6;
                ctx.save();
                ctx.textAlign = 'center';
                ctx.fillStyle = cssVar('--bs-body-color', '#212529');
                ctx.font = '700 1.6rem ' + Chart.defaults.font.family;
                ctx.fillText(hasValue ? (pct.toFixed(1) + '%') : '—', cx, cy - 8);
                ctx.font = '400 .75rem ' + Chart.defaults.font.family;
                ctx.fillStyle = cssVar('--bs-secondary-color', '#6c757d');
                ctx.fillText('Avg defect % (30 d)', cx, cy + 8);
                ctx.restore();
            }
        };
        var chart = new Chart(gaugeEl, {
            type: 'doughnut',
            data: {
                labels: ['Defect %', 'Remainder'],
                datasets: [{
                    data: [pct, Math.max(0, 100 - pct)],
                    backgroundColor: [band, trackColor],
                    borderWidth: 0
                }]
            },
            options: {
                rotation: -90,
                circumference: 180,
                cutout: '70%',
                plugins: { legend: { display: false }, tooltip: { enabled: false } }
            },
            plugins: [centerPlugin]
        });
        registry.push({ chart: chart, kind: 'gauge' });
    }

    function renderEmpty(canvas, message) {
        var box = canvas.parentNode;
        if (!box) return;
        canvas.remove();
        var div = document.createElement('div');
        div.className = 'd-flex align-items-center justify-content-center text-muted small h-100';
        div.style.minHeight = '200px';
        div.textContent = message;
        box.appendChild(div);
    }

    // Refresh chart colors on theme change. theme-pack.js toggles
    // data-bs-theme on <html>; observing that handles every theme switch
    // without coupling to theme-pack's internals.
    var mo = new MutationObserver(function () {
        var colors = themeColors();
        registry.forEach(function (entry) {
            var ch = entry.chart;
            if (!ch) return;
            // Common axis colors
            if (ch.options && ch.options.scales) {
                if (ch.options.scales.x) {
                    ch.options.scales.x.ticks.color = colors.muted;
                    ch.options.scales.x.grid.color  = colors.border;
                }
                if (ch.options.scales.y) {
                    ch.options.scales.y.ticks.color = colors.muted;
                    ch.options.scales.y.grid.color  = colors.border;
                }
            }
            if (ch.options && ch.options.plugins && ch.options.plugins.legend && ch.options.plugins.legend.labels) {
                ch.options.plugins.legend.labels.color = colors.body;
            }
            if (entry.kind === 'qoStatus' && ch.data && ch.data.datasets[0]) {
                ch.data.datasets[0].borderColor = cssVar('--bs-body-bg', '#ffffff');
            }
            ch.update('none');
        });
    });
    mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme', 'data-theme'] });
})();
