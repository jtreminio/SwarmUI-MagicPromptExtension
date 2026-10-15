const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function setup(elements = {}) {
    const storage = new Map();
    const events = [];
    const context = vm.createContext({
        document: { addEventListener() {}, getElementById: id => elements[id] || null },
        localStorage: { getItem: key => storage.get(key), setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
        console, navigator: {}, Event, setTimeout,
        triggerChangeFor: element => events.push(element),
        showError: message => events.push(message)
    });
    context.window = context;
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets/prompthistory.js'), 'utf8'), context);
    return { history: context.MP.PromptHistory, events, context };
}

test('history preserves raw prompt whitespace and tags while deduplicating', () => {
    const { history } = setup();
    const raw = '  <mpprompt[Style]:<wc:subject>>\n  ';
    history.record(raw);
    history.record(raw.trim().toUpperCase());
    assert.equal(history.load().length, 1);
    assert.equal(history.load()[0].prompt, raw);
    assert.equal(history.save([]), true);
    assert.equal(history.load().length, 0);
});

test('Use and Append synchronize both prompt fields and notify input listeners', () => {
    const events = [];
    const prompt = { value: 'Current  ', offsetParent: null, dispatchEvent: event => events.push(event.type) };
    const hidden = { value: '' };
    const { history } = setup({ alt_prompt_textbox: prompt, input_prompt: hidden });
    assert.equal(history.applyToPromptBox('<wc:subject>', true), true);
    assert.equal(prompt.value, 'Current <wc:subject>');
    assert.equal(hidden.value, prompt.value);
    assert.deepEqual(events, ['input']);
    history.applyToPromptBox('  Exact\ntext  ', false);
    assert.equal(prompt.value, '  Exact\ntext  ');
    assert.equal(hidden.value, prompt.value);
});

test('missing prompt box leaves the dialog open', () => {
    const { history } = setup();
    let closed = false;
    history.close = () => { closed = true; };
    history.visible = [{ prompt: 'test' }];
    history.handleListClick({ target: { closest: selector => selector === '.mp-history-item' ? { dataset: { index: '0' } } : null } });
    assert.equal(closed, false);
});

test('copy fallback stays inside the modal focus trap and cleans up after failure', async () => {
    const { history, context, events } = setup();
    let removed = false;
    let appended = false;
    let refocused = false;
    const textarea = { style: {}, focus() {}, select() {}, remove() { removed = true; } };
    context.document.createElement = () => textarea;
    context.document.getElementById = () => ({ append: element => { appended = element === textarea; } });
    context.document.execCommand = () => false;
    await history.copyPrompt('raw text', { focus() { refocused = true; } });
    assert.equal(textarea.value, 'raw text');
    assert.ok(appended && removed && refocused);
    assert.match(events[0], /Clipboard access was denied/);
});

test('clipboard rejection reports an error instead of claiming success', async () => {
    const { history, context, events } = setup();
    context.navigator.clipboard = { writeText: async () => { throw new Error('denied'); } };
    const button = { textContent: 'Copy', isConnected: true };
    await history.copyPrompt('test', button);
    assert.equal(button.textContent, 'Copy');
    assert.match(events[0], /denied/);
});
