const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function refineImage(metadata) {
    let request;
    const toggles = {};
    const context = vm.createContext({
        document: { addEventListener() {} },
        registerMediaButton() {},
        promptTabComplete: { getPossibleList() {}, registerPrefix() {} },
        showError(message) { throw new Error(message); },
        currentMetadataVal: JSON.stringify(metadata),
        interpretMetadata: raw => raw,
        toDataURL: (src, callback) => callback('data:image/png;base64,test'),
        getRequiredElementById: id => toggles[id] ||= { checked: false },
        triggerChangeFor() {},
        mainGenHandler: {
            doGenerate(overrides, preOverrides, callback) {
                request = { model: 'current-model', steps: 20, ...overrides, extra_metadata: { custom: 'keep' } };
                callback(request);
            }
        }
    });
    context.window = context;
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets/magicprompt.js'), 'utf8'), context);
    context.magicPromptRefineImage('image.png');
    return JSON.parse(JSON.stringify(request));
}

test('Refine Img carries every stored variable without changing generation parameters', () => {
    const metadata = {
        sui_image_params: { prompt: 'A woman in a red coat', seed: 123 },
        sui_extra_data: { original_prompt: '<mpprompt:<var:character>>' }
    };
    const withoutVariables = refineImage(metadata);
    const variables = {
        prompt: 'Source MP Prompt',
        character: 'A woman in a red coat',
        literal: '<random:red|blue> >:( <8)',
        unicode: '雪\n"quoted" \\ text',
        empty: ''
    };
    metadata.sui_extra_data.mp_variables = variables;
    const withVariables = refineImage(metadata);
    assert.equal(typeof withVariables.extra_metadata.mp_refined_variables, 'string');
    assert.deepEqual(JSON.parse(withVariables.extra_metadata.mp_refined_variables), variables);
    delete withVariables.extra_metadata.mp_refined_variables;
    assert.deepEqual(withVariables, withoutVariables);
    assert.equal(withVariables.prompt, metadata.sui_image_params.prompt);
    assert.equal(withVariables.extra_metadata.original_prompt, metadata.sui_extra_data.original_prompt);
    assert.equal(withVariables.extra_metadata.custom, 'keep');
});

test('Refine Img supports images without variables and preserves an empty variable map', () => {
    const metadata = { sui_image_params: { prompt: 'A sunset' } };
    assert.equal('mp_refined_variables' in refineImage(metadata).extra_metadata, false);
    metadata.sui_extra_data = { mp_variables: {} };
    assert.deepEqual(JSON.parse(refineImage(metadata).extra_metadata.mp_refined_variables), {});
});
