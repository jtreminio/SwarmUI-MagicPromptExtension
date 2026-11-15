const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

test('Swarm image metadata displays decisions, errors and skipped checks', () => {
    const escape = text => String(text).replaceAll('<', '&lt;').replaceAll('>', '&gt;');
    const context = vm.createContext({
        console, document: { addEventListener() {} }, registerMediaButton() {},
        promptTabComplete: { getPossibleList() {}, registerPrefix() {} },
        getParamById() {}, hashCode() { return 0; }, escapeHtml: escape, escapeHtmlNoBr: escape
    });
    context.window = context;
    for (const source of ['../../../wwwroot/js/genpage/helpers/metadatahelpers.js', '../Assets/magicprompt.js', '../Assets/decisionhistory.js']) {
        vm.runInContext(fs.readFileSync(path.join(__dirname, source), 'utf8'), context);
    }

    const { entries } = context.getFormattedMetadataEntries(JSON.stringify({
        sui_image_params: {}, sui_extra_data: { mp_decisions: [
            { tag_index: 0, status: 'ok', answer: false, probabilities: [0.9, 0.1], decision: { title: 'Refusal' }, source: { model: 'model-a' }, generation_skipped: true },
            { tag_index: 1, status: 'error', error: 'Evaluator unavailable', decision: { title: 'Refusal' } },
            { tag_index: 2, status: 'skipped', message: 'Cached response' }
        ] }
    }));
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'MP Decisions');
    assert.match(entries[0].compareValue, /Refusal — No \(10% probability of Yes\) · Generation skipped · model-a/);
    assert.match(entries[0].compareValue, /Refusal — Error: Evaluator unavailable/);
    assert.match(entries[0].compareValue, /Tag 3 — Cached response/);
});
