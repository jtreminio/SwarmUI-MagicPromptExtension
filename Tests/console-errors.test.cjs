const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function loadAsset(context, name) {
    vm.runInContext(fs.readFileSync(path.join(__dirname, '../Assets', name), 'utf8'), context);
}

function setup() {
    const errors = [];
    const popups = [];
    const element = () => ({
        value: '', style: {}, classList: { add() {}, remove() {} }, addEventListener() {}
    });
    const context = vm.createContext({
        console: { log() {}, error: (...args) => errors.push(args) },
        showError: (...args) => popups.push(args),
        document: { addEventListener() {}, getElementById: element, querySelector: () => null },
        registerMediaButton() {},
        promptTabComplete: { getPossibleList() {}, registerPrefix() {} }
    });
    context.window = context;
    loadAsset(context, 'magicprompt.js');
    return { context, errors, popups };
}

test('generation failures log without popups; unrelated Swarm errors still display', () => {
    const { context, errors, popups } = setup();
    const timeout = 'MagicPrompt: LLM request failed for tag #0, skipping this generation. Request Timeout';
    context.showError(timeout);
    assert.deepEqual(errors, [[timeout]]);
    assert.deepEqual(popups, []);
    context.showError('No backends available');
    assert.deepEqual(popups, [['No backends available']]);
});

test('API error callback rejects the request without invoking the default popup handler', async () => {
    const { context, errors, popups } = setup();
    context.genericRequest = (url, payload, callback, depth, errorHandle) => {
        assert.equal(url, 'MagicPromptPhoneHome');
        assert.equal(depth, 0);
        assert.equal(typeof errorHandle, 'function');
        errorHandle('Request Timeout');
    };
    await assert.rejects(context.MP.APIClient.makeRequest({ messageContent: {} }), /Request Timeout/);
    assert.equal(errors.length, 1);
    assert.deepEqual(popups, []);
});

test('successful API responses still resolve', async () => {
    const { context, errors, popups } = setup();
    const response = { success: true, response: 'A sunset' };
    context.genericRequest = (url, payload, callback) => callback(response);
    assert.equal(await context.MP.APIClient.makeRequest({ messageContent: {} }), response);
    assert.deepEqual(errors, []);
    assert.deepEqual(popups, []);
});

test('unusable LLM responses are console-only', () => {
    const { context, errors, popups } = setup();
    for (const response of [null, { error: 'Request Timeout' }, { success: true, response: '' }]) {
        assert.equal(context.MP.ResponseHandler.handleResponse(response), null);
    }
    assert.ok(errors.length >= 3);
    assert.deepEqual(popups, []);
});

test('chat failures clear loading state without adding error messages', async () => {
    const { context, errors, popups } = setup();
    loadAsset(context, 'chat.js');
    const handler = new context.ChatHandler();
    const messages = [];
    handler.elements = {
        chatInput: { value: 'A sunset' }, loadingIndicator: { style: {} },
        visionModeRadio: { checked: false }, chatMessages: { querySelector: () => null }
    };
    handler.appendMessage = (...args) => messages.push(args);
    handler.adjustInputHeight = () => {};
    context.MP.RequestBuilder.createRequestPayload = () => ({});
    context.MP.APIClient.makeRequest = async () => { throw new Error('Request Timeout'); };
    await handler.submitInput();
    assert.deepEqual(messages, [['user', 'A sunset']]);
    assert.equal(handler.isTyping, false);
    assert.equal(handler.elements.loadingIndicator.style.display, 'none');

    handler.findMessage = () => ({ id: 1 });
    handler.findPrecedingUserMessage = () => ({ content: 'A sunset' });
    await handler.regenerateMessage(1);
    assert.deepEqual(messages, [['user', 'A sunset']]);
    assert.equal(handler.isTyping, false);
    assert.equal(handler.elements.loadingIndicator.style.display, 'none');
    assert.equal(errors.length, 2);
    assert.deepEqual(popups, []);
});

test('caption failures clear loading state without popups', async () => {
    const { context, errors, popups } = setup();
    context.document.querySelector = context.document.getElementById;
    loadAsset(context, 'vision.js');
    const handler = context.visionTab;
    let loading = false;
    handler.elements.imagePreview.src = 'data:image/png;base64,test';
    handler.elements.loadingSpinner.classList = {
        add: () => { loading = true; }, remove: () => { loading = false; }
    };
    context.getInstructionContent = () => '';
    context.getInstructionForFeature = () => 'caption';
    context.MP.RequestBuilder.createRequestPayload = () => ({});
    context.MP.APIClient.makeRequest = async () => { throw new Error('Request Timeout'); };
    await handler.generateCaption();
    assert.equal(loading, false);
    assert.equal(errors.length, 1);
    assert.deepEqual(popups, []);
});
