const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function setup() {
    const context = vm.createContext({ MP: { settings: {} }, console });
    context.window = context;
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets/settings.js'), 'utf8'), context);
    return context;
}

test('retired backend fallback preserves custom instruction IDs and contents', () => {
    const context = setup();
    const settings = { backend: 'ollama', model: 'retired', instructions: { custom: { 'custom-old': { title: 'Keep', content: 'Exact\ncontent', categories: ['vision'] } } } };
    const normalized = context.mpNormalizeSettings(settings);
    assert.equal(normalized.backend, 'openrouter');
    assert.equal(normalized.model, '');
    assert.deepEqual(JSON.parse(JSON.stringify(normalized.instructions)), settings.instructions);
    assert.equal(settings.backend, 'ollama');
});

test('saved missing catalog IDs remain in model preferences and blocked wins conflicts', () => {
    const context = setup();
    const result = context.mpNormalizeSettings({ backend: 'openrouter', blockedModels: { openrouter: ['gone', 'gone'] }, favoritedModels: { openrouter: ['gone', 'favorite'] } });
    assert.deepEqual(Array.from(result.blockedModels.openrouter), ['gone']);
    assert.deepEqual(Array.from(result.favoritedModels.openrouter), ['favorite']);
});

test('settings writes snapshot edits and run in order even after a failed save', async () => {
    const context = setup();
    const calls = [];
    context.genericRequest = (route, payload, done, depth, fail) => calls.push({ payload, done, fail });
    const settings = { model: 'first' };
    const first = context.mpPersistSettings(settings);
    settings.model = 'second';
    const second = context.mpPersistSettings(settings);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls.length, 1);
    assert.equal(calls[0].payload.settings.model, 'first');
    const failed = assert.rejects(first, /offline/);
    calls[0].fail('offline');
    await failed;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls.length, 2);
    assert.equal(calls[1].payload.settings.model, 'second');
    calls[1].done({ success: true, settings });
    await second;
});

function modelSelect(context, backend, catalog, { favorites = [], blocked = [], selected = '' } = {}) {
    context.Option = function(text, value) { this.text = text; this.value = value; this.dataset = {}; };
    const settings = context.mpNormalizeSettings({ backend, favoritedModels: { [backend]: favorites }, blockedModels: { [backend]: blocked } });
    context.MP.modelCatalogs = { [context.mpCatalogKey(settings)]: catalog };
    const select = { options: [], replaceChildren(...options) { this.options = options; }, add(option) { this.options.push(option); } };
    context.mpFillModelSelect(select, settings, selected);
    return select;
}

test('OpenRouter models sort newest first within favorites and other models, retaining the selection', () => {
    const context = setup();
    const select = modelSelect(context, 'openrouter', [
        { model: 'old', name: 'Alphabetically first', created: 10 },
        { model: 'favorite-old', created: 5 },
        { model: 'missing-date' },
        { model: 'new', name: 'Z last alphabetically', created: 30 },
        { model: 'favorite-new', created: 40 },
        { model: 'blocked-newest', created: 50 }
    ], { favorites: ['favorite-old', 'favorite-new'], blocked: ['blocked-newest'], selected: 'old' });
    assert.deepEqual(select.options.map(option => option.value), ['', 'favorite-new', 'favorite-old', 'new', 'old', 'missing-date']);
    assert.equal(select.value, 'old');
});

test('equal or missing dates and local catalogs retain provider order', () => {
    const context = setup();
    const catalog = [{ model: 'z', created: 10 }, { model: 'a', created: 10 }, { model: 'y' }, { model: 'b', created: 'invalid' }];
    assert.deepEqual(modelSelect(context, 'openrouter', catalog).options.map(option => option.value), ['', 'z', 'a', 'y', 'b']);
    catalog[1].created = 20;
    assert.deepEqual(modelSelect(context, 'openaiapi', catalog).options.map(option => option.value), ['', 'z', 'a', 'y', 'b']);
});

test('default model excludes blocked selections, including IDs absent from the catalog', () => {
    const context = setup();
    for (const catalog of [[{ model: 'blocked', created: 30 }, { model: 'allowed', created: 10 }], [{ model: 'allowed', created: 10 }]]) {
        const select = modelSelect(context, 'openrouter', catalog, { blocked: ['blocked'], selected: 'blocked' });
        assert.deepEqual(select.options.map(option => option.value), ['', 'allowed']);
        assert.equal(select.value, '');
    }
});
