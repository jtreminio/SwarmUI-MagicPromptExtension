const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function setup() {
    const elements = {
        input_mpinstructions: { value: 'custom-description' },
        input_mpmodelid: { options: [
            { value: 'model-first', text: 'First model' },
            { value: 'model-second', text: 'Second model' }
        ] }
    };
    const context = vm.createContext({
        console,
        document: { addEventListener() {}, getElementById: id => elements[id] },
        registerMediaButton() {},
        getTextContent: box => box.text,
        getTextSelRange: box => [box.cursor],
        promptTabComplete: {
            getPossibleList: () => ['standard completion'],
            getPromptBeforeCursor: box => box.text.slice(0, box.cursor),
            registerPrefix() {}
        }
    });
    context.window = context;
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets/magicprompt.js'), 'utf8'), context);
    context.MP.settings.instructions = {
        prompt: 'Write <var:base_length> words.',
        custom: {
            'custom-description': { title: 'Describe', content: '<var:mplength> words, <var:tone> tone, <var:mplength> again.' },
            'custom-other': { title: 'Other', content: '<var:subject>' },
            'custom-deleted': { title: 'Deleted', content: '<var:retired>', deleted: true }
        }
    };
    return {
        context,
        elements,
        complete(before, after = '') {
            const result = context.promptTabComplete.getPossibleList({ text: before + after, cursor: before.length });
            return JSON.parse(JSON.stringify(result));
        }
    };
}

test('semicolon suggests unique variables from the selected instruction by title or ID', () => {
    const { complete } = setup();
    for (const instruction of ['Describe', 'describe', 'custom-description']) {
        const rows = complete(`<mpprompt[${instruction}; `);
        assert.deepEqual(rows.map(row => row.clean), ['mplength=', 'tone=']);
        assert.equal(rows[0].name, `<mpprompt[${instruction}; mplength=`);
        assert.equal(rows[0].raw, true);
    }
    assert.deepEqual(complete('<mpprompt[Other; ').map(row => row.clean), ['subject=']);
});

test('blank instruction and suppression-only forms use the global instruction', () => {
    const { complete, elements } = setup();
    for (const header of ['', 'false', ', model-first', '|model-first']) {
        assert.deepEqual(complete(`<mpprompt[${header}; `).map(row => row.clean), ['mplength=', 'tone=']);
    }
    elements.input_mpinstructions.value = 'prompt';
    assert.deepEqual(complete('<mpprompt[; ').map(row => row.clean), ['base_length=']);
});

test('variable completion filters names, omits assigned variables, and preserves following values', () => {
    const { complete } = setup();
    const before = 'prefix <mpprompt[Describe, model-first, false; tone="dark; cinematic"; MP';
    assert.deepEqual(complete(before).map(row => row.name), [
        '<mpprompt[Describe, model-first, false; tone="dark; cinematic"; mplength='
    ]);
    assert.equal(complete('<mpprompt[Describe; mp', '=100]:forest>')[0].name, '<mpprompt[Describe; mplength');
    assert.deepEqual(complete('<mpprompt[Describe; mplength=100; tone="dark"; '), []);
});

test('quoted values handle delimiters and escapes without suggesting names inside values', () => {
    const { complete } = setup();
    for (const before of ['<mpprompt[Describe; tone="dark;', '<mpprompt[Describe; mplength=10', '<mpprompt[Describe; tone="dark"']) {
        assert.deepEqual(complete(before), []);
    }
    assert.equal(complete('<mpprompt[Describe; tone="say \\"hi\\"; [forest] >"; mp')[0].clean, 'mplength=');
});

test('model completion retains dropdown order and an existing semicolon', () => {
    const { complete } = setup();
    assert.deepEqual(complete('<mpprompt[Describe, model').map(row => row.name), [
        '<mpprompt[Describe, model-first]:', '<mpprompt[Describe, model-second]:'
    ]);
    assert.equal(complete('<mpprompt[Describe, model-f', '; mplength=100]:forest>')[0].name, '<mpprompt[Describe, model-first');
});

test('assignment completion resumes after nested Swarm tags and comma separators', () => {
    const { complete } = setup();
    for (const value of ['<wc:prompts/uncensor>', '<var:foo>', '<repeat[2]:<random:dark,dark>>', '<repeat[2]:dark; cinematic>']) {
        const before = `<mpprompt[Describe; tone=${value},mp`;
        assert.deepEqual(complete(before).map(row => row.clean), ['mplength=']);
        const row = complete(before, '=300]:forest>')[0];
        assert.equal(before.slice(0, before.lastIndexOf('<')) + row.name + '=300]:forest>',
            `<mpprompt[Describe; tone=${value},mplength=300]:forest>`);
    }
    assert.deepEqual(complete('<mpprompt[Describe; tone=<wc:prompts/uncensor>,mplength=300; '), []);
    assert.deepEqual(complete('<mpprompt[Describe; tone=<repeat[2]:dark; cinematic>; ').map(row => row.clean), ['mplength=']);
});

test('Beach trailing comma suggests remaining instruction variables without hard-coded names', () => {
    const { context, complete } = setup();
    const beach = { title: 'Beach', content: '<var:uncensor> Write <var:mplength> words. <var:uncensor>' };
    context.MP.settings.instructions.custom['custom-beach'] = beach;
    assert.deepEqual(complete('<mpprompt[Beach;unc').map(row => row.clean), ['uncensor=']);
    const before = '<mpprompt[Beach;uncensor=<wc:prompts/uncensor>,';
    for (const whitespace of ['', ' ', '\n']) {
        assert.deepEqual(complete(before + whitespace).map(row => row.clean), ['mplength=']);
    }
    assert.deepEqual(complete(before + 'mplength=300,').map(row => row.clean), []);
    beach.content = '<var:uncensor> <var:camera_angle> <var:lighting>';
    assert.deepEqual(complete(before).map(row => row.clean), ['camera_angle=', 'lighting=']);
});

test('unfinished inline Swarm tags delegate to native tag completion', () => {
    const { complete } = setup();
    for (const value of ['<wc:prompts/', '<var:fo', '<repeat[2]:<random:dark,']) {
        assert.deepEqual(complete(`<mpprompt[Describe; tone=${value}`), ['standard completion']);
    }
});

test('closed tags and ordinary text keep standard completion', () => {
    const { complete } = setup();
    for (const before of ['forest', '<mpprompt[Describe; mplength=100]:forest', '<mpprompt[Describe; mplength=100]:forest> <var:']) {
        assert.deepEqual(complete(before), ['standard completion']);
    }
});

test('Swarm completion insertion preserves surrounding text, quoted brackets, and the cursor', () => {
    const { context } = setup();
    const source = fs.readFileSync(path.join(__dirname, '../../../wwwroot/js/genpage/gentab/prompttools.js'), 'utf8');
    vm.runInContext(source.slice(0, source.indexOf('let promptTabComplete =')), context);
    vm.runInContext('promptTabComplete.onInput = PromptTabCompleteClass.prototype.onInput; promptTabComplete.findLastWordIndex = PromptTabCompleteClass.prototype.findLastWordIndex;', context);
    context.escapeHtml = text => text;
    context.setTextContent = (box, text) => { box.text = text; };
    context.setTextSelRange = (box, cursor) => { box.cursor = cursor; };
    context.Event = class { };
    let buttons;
    context.AdvancedPopover = class {
        constructor(id, rows) { buttons = rows; }
        remove() {}
    };
    for (const value of ['dark', 'dark <8) > [forest]; cinematic']) {
        const before = `prefix <mpprompt[Describe; tone="${value}"; mp`;
        const after = '=100]:forest> suffix';
        const box = {
            text: before + after, cursor: before.length,
            getBoundingClientRect: () => ({ x: 0, y: 0 }),
            focus() {}, dispatchEvent() {}
        };
        context.promptTabComplete.onInput(box);
        buttons[0].action();
        assert.equal(box.text, before.slice(0, -2) + 'mplength' + after);
        assert.equal(box.cursor, before.length - 2 + 'mplength'.length);
    }
});
