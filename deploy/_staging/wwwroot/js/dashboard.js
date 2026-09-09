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

    // One place that knows how wide a trend bucket is. A single-day period is
    // bucketed by HOUR (a day bucketed by day is one dot, which is what made
    // the trend charts look broken on the default view); up to a month by day;
    // beyond that by week.
    function bucketLabel(value) {
        var d = new Date(value);
        if (data.bucketByHour) {
            return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
        }
        return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    }

    // ---- Arrivals trend (line) ----
    var trendEl = document.getElementById('chartArrivalsTrend');
    if (trendEl && data.trend && data.trend.length) {
        var colors = themeColors();
        var labels = data.trend.map(function (p) { return bucketLabel(p.WeekStart); });
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
            // The legend carried the status names but not their counts, so the
            // one number a reader wants from a doughnut was only available by
            // hovering each slice in turn.
            var qoTotal = entries.reduce(function (t, e) { return t + e.value; }, 0);
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
                        legend: {
                            position: 'bottom',
                            labels: {
                                color: colorsQ.body,
                                // The count is the thing a reader wants from a
                                // doughnut, and it used to take a hover per
                                // slice to get it. Put it in the legend.
                                generateLabels: function (ch) {
                                    var ds = ch.data.datasets[0];
                                    return ch.data.labels.map(function (lab, i) {
                                        return {
                                            text: lab + ' (' + ds.data[i] + ')',
                                            fillStyle: ds.backgroundColor[i],
                                            strokeStyle: ds.borderColor,
                                            lineWidth: 1,
                                            index: i
                                        };
                                    });
                                }
                            }
                        },
                        tooltip: {
                            callbacks: {
                                label: function (ctx) {
                                    var pct = qoTotal > 0
                                        ? Math.round(ctx.parsed * 1000 / qoTotal) / 10 : 0;
                                    return ctx.label + ': ' + ctx.parsed + ' (' + pct + '%)';
                                }
                            }
                        }
                    }
                }
            });
            registry.push({ chart: chart, kind: 'qoStatus' });
        } else {
            renderEmpty(qoEl, 'No quality orders yet.');
        }
    }

    // ---- Throughput vs QC pace (line, 12 weeks) ----
    var throughputEl = document.getElementById('chartThroughput');
    if (throughputEl && data.throughput && data.throughput.length) {
        var tColors = themeColors();
        var tLabels = data.throughput.map(function (p) { return bucketLabel(p.WeekStart); });
        var tChart = new Chart(throughputEl, {
            type: 'line',
            data: {
                labels: tLabels,
                datasets: [
                    { label: 'Arrivals received', data: data.throughput.map(function(p){return p.ArrivalsRecv;}),
                      borderColor: tColors.primary, backgroundColor: tColors.primary + '22',
                      tension: 0.3, fill: false, pointRadius: 3 },
                    { label: 'QOs closed',        data: data.throughput.map(function(p){return p.QosClosed;}),
                      borderColor: tColors.success, backgroundColor: tColors.success + '22',
                      tension: 0.3, fill: false, pointRadius: 3, borderDash: [4,4] }
                ]
            },
            options: {
                plugins: { legend: { position: 'bottom', labels: { color: tColors.body } } },
                scales: {
                    x: { ticks: { color: tColors.muted }, grid: { color: tColors.border } },
                    y: { ticks: { color: tColors.muted, precision: 0 }, grid: { color: tColors.border }, beginAtZero: true }
                }
            }
        });
        registry.push({ chart: tChart, kind: 'throughput' });
    } else if (throughputEl) {
        renderEmpty(throughputEl, 'No throughput data yet.');
    }

    // ---- Open QO age distribution (bar histogram) ----
    var ageEl = document.getElementById('chartOpenQoAge');
    if (ageEl && data.openQoAge && data.openQoAge.length === 4) {
        var aColors = themeColors();
        var ageData = data.openQoAge.map(function (n) { return n; });
        var hasAge = ageData.some(function (n) { return n > 0; });
        if (hasAge) {
            // One bar per DAY (0..13) with a 14+ tail. The old four bands
            // (0-3 / 3-7 / 7-14 / 14+) hid the thing the chart is for: whether
            // one particular day's work is piling up. Colour still carries the
            // urgency, so the shape and the severity read at the same glance.
            var ageLabels = ageData.map(function (_n, i) {
                if (i === 0)  return 'Today';
                if (i === 1)  return '1 day';
                if (i === 14) return '14+ d';
                return i + ' d';
            });
            var bandColors = ageData.map(function (_n, i) {
                if (i < 3)  return aColors.success;
                if (i < 7)  return aColors.warning;
                return aColors.danger;
            });
            var ageChart = new Chart(ageEl, {
                type: 'bar',
                data: {
                    labels: ageLabels,
                    datasets: [{
                        data: ageData,
                        backgroundColor: bandColors,
                        borderWidth: 0
                    }]
                },
                options: {
                    plugins: {
                        legend: { display: false },
                        tooltip: {
                            callbacks: {
                                title: function (items) {
                                    var i = items[0].dataIndex;
                                    return i === 14 ? 'Open 14 days or more'
                                         : (i === 0 ? 'Opened today' : 'Open ' + i + ' day' + (i === 1 ? '' : 's'));
                                },
                                label: function (ctx) { return ctx.parsed.y + ' order' + (ctx.parsed.y === 1 ? '' : 's'); }
                            }
                        }
                    },
                    scales: {
                        // autoSkip off: fifteen narrow bars, and skipping ticks
                        // would leave the reader guessing which day is which.
                        x: { ticks: { color: aColors.muted, autoSkip: false, maxRotation: 0, font: { size: 10 } },
                             grid: { display: false } },
                        y: { ticks: { color: aColors.muted, precision: 0 }, grid: { color: aColors.border }, beginAtZero: true }
                    }
                }
            });
            registry.push({ chart: ageChart, kind: 'qoAge' });
        } else {
            renderEmpty(ageEl, 'No open quality orders.');
        }
    }

    // ---- Received vs committed to QC ----
    // Two shapes for one question. Over a single day there is no time axis
    // worth drawing, so the comparison is per PLANT; over a longer period the
    // per-plant totals are already in the table beside this and what the chart
    // adds is the shape over TIME -- the day the gap opened.
    var cmEl = document.getElementById('chartCommitment');
    if (cmEl) {
        var cmColors = themeColors();
        var trend = data.commitmentTrend || [];
        var perPlant = data.commitment || [];
        var useTrend = trend.length > 1;
        var cmLabels, received, committed, qosCreated, emptyMsg;

        if (useTrend) {
            cmLabels   = trend.map(function (pt) { return bucketLabel(pt.Bucket); });
            received   = trend.map(function (pt) { return pt.Received; });
            committed  = trend.map(function (pt) { return pt.Committed; });
            qosCreated = trend.map(function (pt) { return pt.QosCreated; });
        } else {
            cmLabels   = perPlant.map(function (r) { return r.plant; });
            received   = perPlant.map(function (r) { return r.received; });
            committed  = perPlant.map(function (r) { return r.committed; });
            qosCreated = perPlant.map(function (r) { return r.qosCreated; });
        }
        emptyMsg = 'Nothing was received or committed in this period.';

        if (cmLabels.length && (received.some(Boolean) || committed.some(Boolean))) {
            var cmChart = new Chart(cmEl, {
                type: 'bar',
                data: {
                    labels: cmLabels,
                    datasets: [
                        { label: 'Received', data: received,
                          backgroundColor: cmColors.primary, borderWidth: 0 },
                        // The SAME containers that have an order -- so the two
                        // bars are comparable and the gap between them is real.
                        { label: 'With a QC order', data: committed,
                          backgroundColor: cmColors.success, borderWidth: 0 },
                        // Orders RAISED in the bucket, from any arrival date.
                        // A line, not a third bar: it answers a different
                        // question (team throughput) and drawing it as a peer
                        // of the other two is what made "3 received, 13
                        // committed" look like a contradiction.
                        { label: 'QC orders raised', data: qosCreated,
                          type: 'line', borderColor: cmColors.warning,
                          backgroundColor: cmColors.warning,
                          borderWidth: 2, pointRadius: 3, tension: 0.3, fill: false }
                    ]
                },
                options: {
                    plugins: {
                        legend: { position: 'bottom', labels: { color: cmColors.body } },
                        tooltip: {
                            callbacks: {
                                // The gap is the whole point of the chart, so
                                // say it outright rather than making the reader
                                // subtract two bars by eye.
                                afterBody: function (items) {
                                    if (!items.length) return '';
                                    var i = items[0].dataIndex;
                                    var gap = received[i] - committed[i];
                                    return gap > 0
                                        ? 'Still without a QC order: ' + gap
                                        : (received[i] > 0 ? 'Every container covered' : '');
                                }
                            }
                        }
                    },
                    scales: {
                        x: { ticks: { color: cmColors.muted }, grid: { display: false } },
                        y: { ticks: { color: cmColors.muted, precision: 0 },
                             grid: { color: cmColors.border }, beginAtZero: true }
                    }
                }
            });
            registry.push({ chart: cmChart, kind: 'commitment' });
        } else {
            renderEmpty(cmEl, emptyMsg);
        }
    }

    // ---- Defect % by Material Group (horizontal bar, top 8) ----
    var dbgEl = document.getElementById('chartDefectByGroup');
    if (dbgEl && data.defectByGroup && data.defectByGroup.length) {
        var dColors = themeColors();
        var redAtD    = data.thresholds && data.thresholds.DefectPctRed    != null ? data.thresholds.DefectPctRed    : 10;
        var yellowAtD = data.thresholds && data.thresholds.DefectPctYellow != null ? data.thresholds.DefectPctYellow : 5;
        var labelsD = data.defectByGroup.map(function (g) {
            return g.MaterialGroupDesc && g.MaterialGroupDesc.length ? g.MaterialGroupDesc : g.MaterialGroup;
        });
        var valuesD = data.defectByGroup.map(function (g) { return Number(g.AvgDefectPct || 0); });
        var bgD = valuesD.map(function (v) {
            return v > redAtD ? dColors.danger : (v > yellowAtD ? dColors.warning : dColors.success);
        });
        var dgChart = new Chart(dbgEl, {
            type: 'bar',
            data: {
                labels: labelsD,
                datasets: [{
                    data: valuesD,
                    backgroundColor: bgD,
                    borderWidth: 0
                }]
            },
            options: {
                indexAxis: 'y',
                plugins: {
                    legend: { display: false },
                    // A percentage on its own cannot be acted on: 12% of three
                    // samples and 12% of three hundred are different facts, and
                    // "which defect" is the next question either way. Both now
                    // travel with the bar.
                    tooltip: {
                        callbacks: {
                            label: function (ctx) {
                                var g = data.defectByGroup[ctx.dataIndex] || {};
                                var out = [ctx.parsed.x.toFixed(2) + '% defective'];
                                if (g.InspectedUnits) {
                                    out.push(Number(g.DefectiveUnits).toFixed(2) + ' of ' +
                                             Number(g.InspectedUnits).toFixed(2) + ' units');
                                }
                                if (g.Samples) {
                                    out.push(g.Samples + ' sample' + (g.Samples === 1 ? '' : 's') +
                                             ' · ' + g.Orders + ' order' + (g.Orders === 1 ? '' : 's'));
                                }
                                if (g.TopDefect) out.push('Worst: ' + g.TopDefect);
                                return out;
                            }
                        }
                    }
                },
                scales: {
                    x: { ticks: { color: dColors.muted, callback: function (v) { return v + '%'; } },
                         grid: { color: dColors.border }, beginAtZero: true },
                    y: { ticks: { color: dColors.muted }, grid: { display: false } }
                }
            }
        });
        registry.push({ chart: dgChart, kind: 'defectByGroup' });
    } else if (dbgEl) {
        renderEmpty(dbgEl, 'No closed QOs with defect data in this period.');
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
                // Reserve space below the half-doughnut so the "Avg defect %"
                // caption drawn under the arc isn't clipped at the canvas edge.
                layout: { padding: { bottom: 26 } },
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
