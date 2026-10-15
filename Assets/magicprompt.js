/**
 * magicprompt.js
 * Core functionality and utilities for the MagicPrompt extension.
 * 
 * This file has been updated to move settings-related functions to settings.js
 */

'use strict';

// Swarm routes generation failures through its shared toast handler. Keep the
// server's skip/queue behavior, but report MagicPrompt failures only in the console.
if (typeof showError === 'function') {
    const originalShowError = showError;
    showError = function (message, ...args) {
        if (typeof message === 'string' && message.startsWith('MagicPrompt:')) {
            console.error(message);
            return;
        }
        return originalShowError.call(this, message, ...args);
    };
}

// Shared state for prompt tools. Provider settings are loaded from the server.
window.MP = {
    initialized: false,
    allInstructionNames: [],
    settings: { backend: 'openrouter', model: '', backends: {}, instructions: { custom: {} }, blockedModels: {}, favoritedModels: {} },
    APIClient: {
        makeRequest(payload) {
            return new Promise((resolve, reject) => {
                genericRequest('MagicPromptPhoneHome', payload, data => {
                    if (data.success) resolve(data);
                    else reject(new Error(data.error || 'API request failed'));
                }, 0, error => {
                    console.error('MagicPrompt API request failed:', error);
                    reject(new Error(error));
                });
            });
        }
    },
        ResponseHandler: {
            handleResponse(response, action) {
                if (!response) {
                    console.error('No response received');
                    return this.showError('No response received from LLM');
                }
                if (response.error) {
                    console.error('Response error:', response.error);
                    return this.showError(response.error);
                }
                if (!response.response) {
                    console.error('Response missing content');
                    return this.showError('Empty response received from LLM');
                }
            },

            handleMagicResponse(response) {
                const promptBox = document.getElementById('alt_prompt_textbox');
                if (promptBox) {
                    promptBox.value = response;
                    triggerChangeFor(promptBox);
                    promptBox.focus();
                    promptBox.setSelectionRange(0, promptBox.value.length);
                }
            },
            showError(error) {
                let errorMessage = error;
                if (typeof error === 'string' && error.includes('Provider returned error')) {
                    const match = error.match(/Provider returned error.*?:(.*)/);
                    if (match?.[1]) {
                        errorMessage = match[1].trim();
                    }
                }
                console.error('MagicPrompt:', errorMessage);
                return null;
            }
        }
};

/**
 * Adds a "MagicPrompt Settings" link into the Magic Prompt group in the
 * Generate tab's left sidebar (alongside the extension's other parameter
 * fields). Clicking it opens the prompt tools settings.
 * @returns {void}
 */
function addSidebarSettingsLink() {
    const groupId = 'magicpromptautoenable';
    const linkId = 'magicprompt_sidebar_settings_link';
    // The group's content is regenerated whenever the parameter list rebuilds,
    // so (re)append the link as part of postParamBuildSteps and also run it now
    // in case the parameters were already built before this ran.
    const build = () => {
        const group = document.getElementById(`input_group_content_${groupId}`);
        if (!group || document.getElementById(linkId)) {
            return;
        }
        group.append(createDiv(linkId, 'keep_group_visible',
            `<button type="button" class="basic-button" onclick="showSettingsModal()">MagicPrompt Settings</button>`));
    };
    if (typeof postParamBuildSteps !== 'undefined') {
        postParamBuildSteps.push(build);
    }
    build();
}

function wildcardSeedGenerator() {
    document.addEventListener('click', function (e) {
        const MAX_WC_SEED = 4294967295;
        const extensionEnabled = document.getElementById('input_group_content_magicpromptautoenable_toggle');
        const generateEnabled = document.getElementById('input_mpgeneratewildcardseed');

        if (
            !e.target
            || e.target.id !== 'alt_generate_button'
            || !extensionEnabled
            || !extensionEnabled.checked
            || !generateEnabled
            || !generateEnabled.checked
        ) {
            return;
        }

        let wildcardSeedElem = getRequiredElementById('input_wildcardseed');
        wildcardSeedElem.value = Math.floor(Math.random() * MAX_WC_SEED);
        triggerChangeFor(wildcardSeedElem);
    }, true);
}

function magicPromptRefineImage(src) {
    let metadataFull;
    try {
        let readable = interpretMetadata(currentMetadataVal);
        metadataFull = readable ? JSON.parse(readable) : {};
        if (typeof metadataFull.sui_image_params?.prompt !== 'string') {
            showError('Refine Img requires a finalized prompt in the selected image metadata.');
            return;
        }
    } catch (error) {
        showError(`Refine Img could not read the selected image metadata: ${error.message}`);
        return;
    }
    toDataURL(src, url => {
        let inputOverrides = {
            initimage: url,
            initimagecreativity: 0,
            images: 1,
            prompt: metadataFull.sui_image_params.prompt
        };
        let metadata = metadataFull.sui_image_params;
        if ('seed' in metadata && !('refinercontrolpercentage' in metadata)) {
            inputOverrides.seed = metadata.seed;
        }
        let togglerInit = getRequiredElementById('input_group_content_initimage_toggle');
        let togglerRefine = getRequiredElementById('input_group_content_refineupscale_toggle');
        let togglerInitOriginal = togglerInit.checked;
        let togglerRefineOriginal = togglerRefine.checked;
        togglerInit.checked = false;
        togglerRefine.checked = true;
        triggerChangeFor(togglerInit);
        triggerChangeFor(togglerRefine);
        mainGenHandler.doGenerate(inputOverrides, {}, actualInput => {
            actualInput.extra_metadata ||= {};
            actualInput.extra_metadata.mp_is_refining = true;
            actualInput.extra_metadata.mp_refined_prompt = metadata.prompt;
            if (typeof metadataFull.sui_extra_data?.original_prompt === 'string') {
                actualInput.extra_metadata.original_prompt = metadataFull.sui_extra_data.original_prompt;
            }
            let variables = metadataFull.sui_extra_data?.mp_variables;
            if (variables && typeof variables === 'object' && !Array.isArray(variables)) {
                actualInput.extra_metadata.mp_refined_variables = JSON.stringify(variables);
            }
            togglerInit.checked = togglerInitOriginal;
            togglerRefine.checked = togglerRefineOriginal;
            triggerChangeFor(togglerInit);
            triggerChangeFor(togglerRefine);
        });
    });
}

registerMediaButton('Refine Img', magicPromptRefineImage, 'Refines this image using its finalized prompt', ['image'], true, true);

/** Display the captured MP Prompt before Negative Prompt, keeping other variables in place. */
if (typeof getFormattedMetadataEntries === 'function') {
    const originalGetFormattedMetadataEntries = getFormattedMetadataEntries;
    getFormattedMetadataEntries = function (metadata) {
        const formatted = originalGetFormattedMetadataEntries(metadata);
        const entries = formatted.entries;
        const variablesIndex = entries.findIndex(entry => entry.id === 'mp_variables');
        if (variablesIndex < 0) {
            return formatted;
        }
        let variables;
        try {
            variables = JSON.parse(entries[variablesIndex].compareValue);
        }
        catch {
            return formatted;
        }
        // Swarm displays the stored lowercase `prompt` key as "Prompt".
        const promptKey = variables && Object.keys(variables).find(key => key.toLowerCase() === 'prompt');
        if (!promptKey || typeof variables[promptKey] !== 'string' || !variables[promptKey]) {
            return formatted;
        }
        const prompt = variables[promptKey];
        delete variables[promptKey];
        // Reuse Swarm's formatting for escaping, copy buttons, and nested variables.
        const replacement = originalGetFormattedMetadataEntries(JSON.stringify({
            sui_image_params: {},
            sui_extra_data: {
                'MP Prompt': prompt,
                ...(Object.keys(variables).length > 0 ? { mp_variables: variables } : {})
            }
        })).entries;
        const promptEntry = replacement.shift();
        promptEntry.id = 'mp_variable_prompt';
        promptEntry.breakAfter = true;
        entries.splice(variablesIndex, 1, ...replacement);
        let insertIndex = entries.findIndex(entry => entry.id === 'negativeprompt');
        if (insertIndex < 0) {
            insertIndex = entries.findLastIndex(entry => ['prompt', 'Original Prompt', 'Interpreted Prompt'].includes(entry.id)) + 1;
        }
        entries.splice(insertIndex, 0, promptEntry);
        return formatted;
    };
}

/**
 * Initializes on DOM load
 */
document.addEventListener("DOMContentLoaded", async function () {
    try {
        if (MP.initialized) return;
        MP.initialized = true;
        initSettingsModal();
        addSidebarSettingsLink();
        // Initialize settings
        await loadSettings();
        if (typeof postParamBuildSteps !== 'undefined') postParamBuildSteps.push(() => updateModelListOnLeft(true));
        updateModelListOnLeft(true);
        // Populate instruction prefixes for autocomplete
        mpRefreshInstructionPrefixes();
        // Setup auto wildcard seed generation on Generate click (capture phase, before onclick)
        wildcardSeedGenerator();
        await fetchModels();
        updateModelListOnLeft(true);
    } catch (error) {
        console.error('Error initializing MagicPrompt:', error);
        MP.ResponseHandler.showError('Failed to initialize MagicPrompt: ' + error.message);
    }
});

function mpRefreshInstructionPrefixes() {
    for (const name of MP.allInstructionNames) delete promptTabComplete.prefixes[`mpprompt[${name}]`];
    const instructions = ['prompt'];
    if (MP.settings?.instructions?.custom) {
        for (const [, instruction] of Object.entries(MP.settings.instructions.custom)) {
            if (instruction && !instruction.deleted && instruction.title) {
                instructions.push(instruction.title);
            }
        }
    }
    MP.allInstructionNames = instructions;

    for (const name of instructions) {
        const prefixName = `mpprompt[${name}]`;
        promptTabComplete.prefixes[prefixName] = {
            name: prefixName,
            description: `Send prompt to LLM using "${name}" instruction`,
            completer: () => [
                `\nUsing instruction: ${name}`,
                '\nEnter your prompt text after the colon.',
                `\nExample: "<mpprompt[${name}]:your prompt here>"`
            ],
            selfStanding: false,
            isAlt: false
        };
    }
}

window.mpRefreshInstructionPrefixes = mpRefreshInstructionPrefixes;

// Swarm's prefix completers run after the colon. Model selection happens inside
// the brackets, so handle that slot before delegating to the standard completion.
(function () {
    const originalGetPossibleList = promptTabComplete.getPossibleList;
    promptTabComplete.getPossibleList = function (box) {
        const prompt = this.getPromptBeforeCursor(box);
        const match = prompt.match(/<mpprompt\[([^\[\]<>,|]*),\s*([^\[\]<>,]*)$/i);
        if (!match) {
            return originalGetPossibleList.call(this, box);
        }

        const select = document.getElementById('input_mpmodelid');
        const query = match[2].trim().toLowerCase();
        const tagStart = match[0].slice(0, match[0].length - match[2].length);
        const afterCursor = getTextContent(box).substring(getTextSelRange(box)[0]);
        // Keep an existing bracket or output flag when completing inside a tag.
        const ending = /^\s*[,\]]/.test(afterCursor) ? '' : ']:';

        return Array.from(select?.options || [])
            .filter(option => option.value && option.value !== 'loading' && !option.disabled)
            .filter(option => option.value.toLowerCase().includes(query) || option.text.toLowerCase().includes(query))
            .map(option => ({
                raw: true,
                name: `${tagStart}${option.value}${ending}`,
                clean: option.text,
                desc: option.value
            }));
    };
})();

promptTabComplete.registerPrefix('mpprompt', 'Prompt to be sent to LLM', (prefix) => {
    return [];
}, false);

promptTabComplete.registerPrefix('mporiginal', 'Placeholder for the original prompt', (prefix) => {
    return [];
}, true);

promptTabComplete.registerPrefix('mpresponse', 'Reference the LLM response from a previous mpprompt tag (0-indexed)', (prefix) => {
    return [
        '\nUse <mpresponse:N> to reference the LLM response from the Nth mpprompt tag (0-indexed).',
        '\nExample: <mpresponse:0> returns the response from the first mpprompt tag.',
        '\nApply filters only when inserting it: <mpresponse|"cat=dog","red":0>.',
        '\nUse "match=replacement" to replace, or "match" to delete.',
        '\nThis allows chaining: use one LLM response as input to another mpprompt.',
        '\nNote: You can only reference responses from mpprompt tags that appear BEFORE this reference.',
        '\nExample usage:',
        '\n  <mpprompt[Style A]:a sunset>',
        '\n  <mpprompt[Enhance]:Enhance this scene: <mpresponse:0>>'
    ];
}, false);
