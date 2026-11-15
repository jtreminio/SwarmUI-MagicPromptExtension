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

test('decision settings migrate local evaluators and keep preferences independent', () => {
    const context = setup();
    const original = { backend: 'openaiapi', model: 'local-model', favoritedModels: { openrouter: ['shared'] }, decisions: {
        evaluator: 'jev', model: 'shared', blockedModels: [' shared ', 'SHARED'], favoritedModels: ['shared', 'keep', 'keep'],
        questions: [{ id: 'keep-rule', question: 'Refused?', skipIf: 'yes', selected: true }]
    } };
    const result = context.mpNormalizeSettings(original);
    assert.equal(result.decisions.evaluator, undefined);
    assert.equal(result.decisions.model, '');
    assert.deepEqual(Array.from(result.decisions.blockedModels), ['shared']);
    assert.deepEqual(Array.from(result.decisions.favoritedModels), ['keep']);
    assert.deepEqual(Array.from(result.favoritedModels.openrouter), ['shared']);
    assert.equal(result.model, 'local-model');
    assert.deepEqual(JSON.parse(JSON.stringify(result.decisions.questions)), original.decisions.questions);
    assert.equal(original.decisions.evaluator, 'jev');
});

test('decision model catalog and ordering are independent of the prompt provider', () => {
    const context = setup();
    context.Option = function(text, value) { this.text = text; this.value = value; this.dataset = {}; };
    const saved = context.mpNormalizeSettings({ backend: 'openaiapi', model: 'local', decisions: { model: 'old', favoritedModels: ['old'], blockedModels: ['blocked'], questions: [] } });
    const settings = context.mpDecisionModelSettings(saved);
    assert.notEqual(context.mpCatalogKey(settings), context.mpCatalogKey(saved));
    context.MP.modelCatalogs = {
        [context.mpCatalogKey(settings)]: [{ model: 'new', created: 20 }, { model: 'old', created: 1 }, { model: 'blocked', created: 30 }],
        [context.mpCatalogKey(saved)]: [{ model: 'local' }]
    };
    const select = { options: [], replaceChildren(...options) { this.options = options; }, add(option) { this.options.push(option); } };
    context.mpFillModelSelect(select, settings, saved.decisions.model);
    assert.deepEqual(select.options.map(option => option.value), ['', 'old', 'new']);
    assert.equal(select.value, 'old');
    assert.match(context.mpModelSelectOptionText({ model: 'native', decisionModel: true, costIn: '$0', costOut: '$0', costRequest: '$0.001' }), /\$0.001 \/ request/);
});

test('out-of-order refresh responses cannot replace a newer catalog', async () => {
    const context = setup();
    context.document = { getElementById: () => null };
    context.renderModelSettings = () => {};
    context.updateModelListOnLeft = () => {};
    context.MP.settings = context.mpNormalizeSettings({ backend: 'openrouter' });
    const calls = [];
    context.genericRequest = (route, payload, done) => calls.push({ route, done });
    const first = context.fetchModels();
    const second = context.fetchModels();
    calls[1].done({ success: true, models: [{ model: 'new' }] });
    await second;
    calls[0].done({ success: true, models: [{ model: 'stale' }] });
    await first;
    assert.equal(context.mpCatalog()[0].model, 'new');
});

test('Generate decision stars and blocks persist separately and clear a blocked default', async () => {
    const context = setup();
    context.MP.settings = context.mpNormalizeSettings({ backend: 'openrouter', model: 'chat',
        favoritedModels: { openrouter: ['chat'] }, decisions: { model: 'judge', questions: [{ id: 'keep' }] } });
    context.updateModelListOnLeft = () => {};
    let saved;
    context.mpPersistSettings = async value => { saved = JSON.parse(JSON.stringify(value)); };
    context.MP.favoriteModelId('decisions', 'judge');
    assert.equal(context.MP.isModelFavoritedForBackend('decisions', 'judge'), true);
    context.MP.blockModelId('decisions', 'judge');
    assert.equal(context.MP.settings.decisions.model, '');
    assert.deepEqual(Array.from(context.MP.settings.decisions.favoritedModels), []);
    assert.deepEqual(saved.decisions.blockedModels, ['judge']);
    assert.equal(saved.decisions.questions, undefined, 'Preference writes must not overwrite a newer question library in another tab');
    assert.equal(saved.decisions.model, undefined, 'Preference writes must not overwrite a newer shared default');
    assert.equal(saved.blockedModels, undefined);
    assert.deepEqual(Array.from(context.MP.settings.favoritedModels.openrouter), ['chat']);
});

test('a pending decision import cannot write into a reopened draft', async () => {
    const context = setup();
    context.document = { getElementById: () => ({ value: '' }) };
    context.mpCaptureDecisionEditor = () => {};
    context.mpRenderDecisionList = () => {};
    context.mpRenderDecisionEditor = () => {};
    context.mpSettingsStatus = () => {};
    vm.runInContext('mpSettingsDraft = { decisions: { questions: [] } }', context);
    let complete;
    const pending = context.mpImportDecisions({ text: () => new Promise(resolve => { complete = resolve; }) });
    vm.runInContext('mpSettingsDraft = { decisions: { questions: [] } }', context);
    complete(JSON.stringify({ title: 'Cancelled import', question: 'Keep?', type: 'noul', options: [] }));
    await pending;
    assert.equal(vm.runInContext('mpSettingsDraft.decisions.questions.length', context), 0);
});

test('decision preference mutations match saved IDs regardless of case', () => {
    const context = setup();
    context.MP.settings = context.mpNormalizeSettings({ decisions: { model: 'JUDGE', questions: [], favoritedModels: ['JUDGE'] } });
    context.updateModelListOnLeft = () => {};
    context.mpPersistSettings = async () => ({});
    assert.equal(context.MP.isModelFavoritedForBackend('decisions', 'judge'), true);
    context.MP.blockModelId('decisions', 'judge');
    assert.deepEqual(Array.from(context.MP.settings.decisions.favoritedModels), []);
    assert.equal(context.MP.settings.decisions.model, '');
    context.MP.unblockModelId('decisions', 'JUDGE');
    assert.deepEqual(Array.from(context.MP.settings.decisions.blockedModels), []);
    context.MP.favoriteModelId('decisions', 'JUDGE');
    context.MP.favoriteModelId('decisions', 'judge');
    assert.deepEqual(Array.from(context.MP.settings.decisions.favoritedModels), ['judge']);
    context.MP.unfavoriteModelId('decisions', 'JUDGE');
    assert.deepEqual(Array.from(context.MP.settings.decisions.favoritedModels), []);
});
