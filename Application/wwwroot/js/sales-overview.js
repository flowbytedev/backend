// Chart.js helpers for the Sales Performance Overview dashboard (Chart.js is loaded globally in App.razor).
// One chart instance per canvas id; each render() replaces the existing chart on that canvas.

const charts = {};

// A calm, retail-report palette shared by the categorical charts.
const PALETTE = ['#1F3A5F', '#2E7D46', '#B5760A', '#C8372D', '#5B7FB5', '#7A8450',
    '#9A5B9A', '#3E9AA3', '#B58A00', '#8C6D46', '#6B6A66', '#A0433B'];

function applyDefaults() {
    if (!window.Chart) return false;
    window.Chart.defaults.font.family = "'Inter',sans-serif";
    window.Chart.defaults.font.size = 12;
    window.Chart.defaults.color = "#6B6A66";
    return true;
}

const money = v => '$' + Math.round(v).toLocaleString('en-US');

// Revenue trend (line) with an optional secondary units line on a hidden axis.
export function renderTrendChart(canvasId, labels, revenue) {
    if (!applyDefaults()) return false;
    destroy(canvasId);
    const el = document.getElementById(canvasId);
    if (!el) return false;
    charts[canvasId] = new window.Chart(el.getContext('2d'), {
        type: 'line',
        data: {
            labels,
            datasets: [{
                label: 'Revenue', data: revenue, borderColor: '#1F3A5F',
                backgroundColor: 'rgba(31,58,95,.10)', borderWidth: 2, fill: true, tension: .32,
                pointRadius: 0, pointHoverRadius: 4, pointBackgroundColor: '#1F3A5F'
            }]
        },
        options: {
            responsive: true, maintainAspectRatio: false,
            plugins: {
                legend: { display: false },
                tooltip: { callbacks: { label: c => 'Revenue: ' + money(c.parsed.y) } }
            },
            scales: {
                x: { grid: { display: false }, ticks: { maxRotation: 0, autoSkip: true, maxTicksLimit: 8 } },
                y: { beginAtZero: true, grid: { color: '#EDEBE4' }, ticks: { callback: v => v >= 1000 ? '$' + (v / 1000) + 'k' : '$' + v } }
            }
        }
    });
    return true;
}

// Revenue by hour of day (bar).
export function renderHourChart(canvasId, labels, revenue) {
    if (!applyDefaults()) return false;
    destroy(canvasId);
    const el = document.getElementById(canvasId);
    if (!el) return false;
    charts[canvasId] = new window.Chart(el.getContext('2d'), {
        type: 'bar',
        data: { labels, datasets: [{ data: revenue, backgroundColor: '#2E7D46', borderRadius: 5, maxBarThickness: 26 }] },
        options: {
            responsive: true, maintainAspectRatio: false,
            plugins: { legend: { display: false }, tooltip: { callbacks: { label: c => money(c.parsed.y) } } },
            scales: {
                x: { grid: { display: false } },
                y: { beginAtZero: true, grid: { color: '#EDEBE4' }, ticks: { callback: v => v >= 1000 ? '$' + (v / 1000) + 'k' : '$' + v } }
            }
        }
    });
    return true;
}

// Categorical share (doughnut) — used for category revenue and payment mix.
export function renderDoughnut(canvasId, labels, data, moneyMode) {
    if (!applyDefaults()) return false;
    destroy(canvasId);
    const el = document.getElementById(canvasId);
    if (!el) return false;
    charts[canvasId] = new window.Chart(el.getContext('2d'), {
        type: 'doughnut',
        data: { labels, datasets: [{ data, backgroundColor: PALETTE, borderColor: '#fff', borderWidth: 2 }] },
        options: {
            responsive: true, maintainAspectRatio: false, cutout: '58%',
            plugins: {
                legend: { position: 'right', labels: { boxWidth: 12, boxHeight: 12, font: { size: 11.5 }, padding: 9 } },
                tooltip: {
                    callbacks: {
                        label: c => {
                            const total = c.dataset.data.reduce((a, b) => a + b, 0) || 1;
                            const pct = (c.parsed / total * 100).toFixed(1) + '%';
                            return ' ' + c.label + ': ' + (moneyMode ? money(c.parsed) : c.parsed.toLocaleString('en-US')) + ' (' + pct + ')';
                        }
                    }
                }
            }
        }
    });
    return true;
}

export function destroy(canvasId) {
    const c = charts[canvasId];
    if (c) { c.destroy(); delete charts[canvasId]; }
}
