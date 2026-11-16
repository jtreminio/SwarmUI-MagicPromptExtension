const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function setup() {
    const context = vm.createContext({
        MP_BACKENDS: ['openrouter'], MP: { settings: {} },
        mpCatalog: () => [], mpModelIdKey: id => id.toLowerCase(),
        mpCompareModelDates: () => 0
    });
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets/stats.js'), 'utf8'), context);
    return context;
}

test('dollar estimates use known-cost counts and total spend on eligible passes and skips', () => {
    const ctx = setup();
    const model = { provider: 'openrouter', model: 'paid', completed: 1000, skipped: 90, skip_completed: 900,
        costed_completed: 100, cost_total: 0.2, costed_skip_completed: 50, costed_skipped: 10, skip_cost_total: 0.1 };
    const [row] = ctx.mpStatsSortRows([model], 'newest');
    assert.equal(row.responsesPerDollar, 500);
    assert.equal(row.skippedPerDollar, 100);
    // Historical completion and skip counts must not enter either calculation.
    assert.equal(ctx.mpStatsSortRows([{ ...model, completed: 9000, skipped: 900 }], 'newest')[0].responsesPerDollar, 500);
});

test('unknown, free, no observed skips and tiny positive estimates stay distinct', () => {
    const ctx = setup();
    for (const [count, cost, samples, expected] of [
        [undefined, undefined, undefined, null], [0, 0, 0, null], [1, null, 1, null],
        [1, -1, 1, null], [1, Infinity, 1, null], [1, 0, 1, Infinity],
        [0, 0.2, 10, 0], [0, 0, 10, 0], [1, 4, 2, 0.25], [3, 0.01, 3, 300]
    ]) assert.equal(ctx.mpStatsPerDollar(count, cost, samples), expected);
    assert.equal(ctx.mpStatsDollarDisplay(null), '—');
    assert.equal(ctx.mpStatsDollarDisplay(Infinity), 'Free');
    assert.equal(ctx.mpStatsDollarDisplay(0), '0');
    assert.equal(ctx.mpStatsDollarDisplay(0.25), '<1');
    assert.equal(ctx.mpStatsDollarDisplay(500), '500');
});

test('cost and latency sorts keep unknowns last in both directions', () => {
    const ctx = setup();
    const rows = [
        { model: 'unknown', latency_ms: null },
        { model: 'paid', costed_completed: 100, cost_total: 0.2, latency_ms: 2500 },
        { model: 'free', costed_completed: 2, cost_total: 0, latency_ms: 0 },
        { model: 'expensive', costed_completed: 1, cost_total: 4, latency_ms: 7000 }
    ].map(row => ({ provider: 'openrouter', ...row }));
    const ids = mode => Array.from(ctx.mpStatsSortRows(rows, mode), row => row.model);
    assert.deepEqual(ids('responsesHigh'), ['free', 'paid', 'expensive', 'unknown']);
    assert.deepEqual(ids('responsesLow'), ['expensive', 'paid', 'free', 'unknown']);
    assert.deepEqual(ids('latencyLow'), ['free', 'paid', 'expensive', 'unknown']);
    assert.deepEqual(ids('latencyHigh'), ['expensive', 'paid', 'free', 'unknown']);
    const skipped = rows.map(row => ({ ...row, costed_skip_completed: row.costed_completed,
        costed_skipped: row.costed_completed, skip_cost_total: row.cost_total }));
    assert.deepEqual(Array.from(ctx.mpStatsSortRows(skipped, 'skippedDollarHigh'), row => row.model), ['free', 'paid', 'expensive', 'unknown']);
    assert.deepEqual(Array.from(ctx.mpStatsSortRows(skipped, 'skippedDollarLow'), row => row.model), ['expensive', 'paid', 'free', 'unknown']);
});
