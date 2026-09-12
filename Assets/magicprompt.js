/**
 * magicprompt.js
 * Core functionality and utilities for the MagicPrompt extension.
 * 
 * This file has been updated to move settings-related functions to settings.js
 */

'use strict';

// Initialize MagicPrompt global namespace if it doesn't exist
if (!window.MP) {
    window.MP = {
        initialized: false,
        allInstructionNames: [],
        settings: {
            // Core settings
            backend: 'ollama',
            model: '',
            visionbackend: 'ollama',
            visionmodel: '',
            linkChatAndVisionModels: true, // Default to true
            // Backend configurations
            backends: {
                ollama: {
                    baseurl: 'http://localhost:11434',
                    unloadModel: false,
                    timeout: 120,
                    endpoints: {
                        chat: '/api/chat',
                        models: '/api/tags'
                    }
                },
                openaiapi: {
                    baseurl: 'http://localhost:11434',
                    unloadModel: false,
                    timeout: 120,
                    endpoints: {
                        chat: 'v1/chat/completions',
                        models: '/v1/models'
                    },
                    apikey: ''
                },
                openai: {
                    baseurl: 'https://api.openai.com',
                    timeout: 20,
                    endpoints: {
                        chat: 'v1/chat/completions',
                        models: 'v1/models'
                    },
                    apikey: ''
                },
                openrouter: {
                    baseurl: 'https://openrouter.ai',
                    timeout: 20,
                    endpoints: {
                        chat: '/api/v1/chat/completions',
                        models: '/api/v1/models'
                    },
                    apikey: ''
                }
            },
            blockedModels: {
                ollama: [],
                openrouter: [],
                openaiapi: [],
                openai: [],
            },
            favoritedModels: {
                ollama: [],
                openrouter: [],
                openaiapi: [],
                openai: [],
            },
            // Instructions
            instructions: {
                chat: '',
                vision: '',
                caption: '',
                prompt: ''
            }
        },

        APIClient: {
            /**
             * Makes an API request using SwarmUI's genericRequest
             * @param {Object} payload - Request payload
             * @returns {Promise<Object>} API response
             */
            async makeRequest(payload) {
                if (!payload) {
                    throw new Error('Invalid payload');
                }
                // Allow handling of existing requests for vision mode
                const isVisionRequest = payload.messageType === 'Vision';
                const currentMode = document.getElementById('vision_mode')?.checked;
                if (!isVisionRequest && currentMode) {
                    const currentImage = window.visionHandler?.getCurrentImage();
                    if (currentImage) {
                        payload.messageContent.media = [{
                            type: "base64",
                            data: currentImage
                        }];
                        payload.messageType = 'Vision';
                    }
                }
                console.log('Making API request with instructions:', payload.messageContent.instructions);
                try {
                    return new Promise((resolve, reject) => {
                        genericRequest('MagicPromptPhoneHome', payload,
                            data => {
                                if (data.success) {
                                    console.log('API request successful:', data);
                                    resolve(data);
                                } else {
                                    console.error('API request failed:', data.error);
                                    reject(new Error(data.error || 'API request failed'));
                                }
                            }
                        );
                    });
                } catch (error) {
                    console.error('API request error:', error);
                    throw error;
                }
            }
        },

        RequestBuilder: {
            /**
             * Creates a request payload for API calls
             * @param {string} input - User input text
             * @param {string|null} image - Base64 image data
             * @param {string} action - Action type ('chat', 'vision', 'prompt', 'caption')
             * @returns {Object} Formatted request payload
             */
            createRequestPayload(input, image, action) {
                // only allow input to be empty for 'random-prompt' action
                if (!input?.trim() && action !== 'random-prompt') {
                    throw new Error('Input is required');
                }
                const hasImage = Boolean(image);
                const featureName = action.toLowerCase();
                console.log(`Action: "${action}", featureName: "${featureName}"`);

                // Use the feature mapping system to get the right instruction type
                const instructionType = getInstructionForFeature(featureName);
                console.log(`Instruction type from feature mapping: "${instructionType}"`);

                // If no mapping found, use the featureName as fallback
                const effectiveType = instructionType || featureName;
                console.log(`Effective instruction type: "${effectiveType}"`);

                const instructions = getInstructionContent(effectiveType);
                try {
                    // Get model and backend based on request type
                    const modelId = this.getModelId(hasImage);
                    const backend = hasImage ? MP.settings.visionbackend : MP.settings.backend;
                    // Get appropriate instructions based on action type and feature mapping
                    const featureName = action.toLowerCase();
                    // Use the feature mapping system to get the right instruction type
                    const instructionType = getInstructionForFeature(featureName) || featureName;
                    const instructions = getInstructionContent(instructionType);
                    console.log('CreaterequestPayload: Using instructions:', instructions);
                    // Create the message content
                    const messageContent = {
                        text: input,
                        media: image ? [{ type: "base64", data: image, mediaType: window.visionHandler?.currentMediaType || "image/jpeg" }] : null,
                        instructions: instructions,
                        KeepAlive: (backend.toLowerCase() === 'ollama' && MP.settings.backends[backend]?.unloadModel) ? 0 : null
                    };
                    return {
                        messageContent,
                        modelId,
                        messageType: hasImage ? "Vision" : "Text",
                        action: featureName,
                    };
                } catch (error) {
                    console.error('Error creating request payload:', error);
                    throw error;
                }
            },

            getModelId(isVision) {
                if (MP.settings.linkChatAndVisionModels) {
                    return document.getElementById('modelSelect')?.value;
                }
                const modelId = isVision
                    ? document.getElementById('visionModel')?.value
                    : document.getElementById('modelSelect')?.value;

                if (!modelId) {
                    throw new Error('Please select a model first');
                }
                return modelId;
            },
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
                showError(errorMessage);
                return null;
            }
        },

        ResizeHandler: class {
            constructor(options) {
                this.handle = options.handle;
                this.panel = options.panel;
                this.storageKey = options.storageKey;
                this.defaultWidth = options.defaultWidth || 400;
                this.minWidth = options.minWidth || 300;
                this.maxWidthOffset = options.maxWidthOffset || 300;
                this.isDragging = false;
                this.startX = 0;
                this.startWidth = 0;
                // Bind methods
                this.startDragging = this.startDragging.bind(this);
                this.doDrag = this.doDrag.bind(this);
                this.stopDragging = this.stopDragging.bind(this);
                this.init();
            }

            init() {
                // Set initial width from storage or default
                const storedWidth = localStorage.getItem(this.storageKey);
                const initialWidth = storedWidth ? parseInt(storedWidth) : this.defaultWidth;
                this.panel.style.width = `${initialWidth}px`;
                // Add event listener to handle
                this.handle.addEventListener('mousedown', this.startDragging);
                // Add to layout resets if available
                if (window.layoutResets) {
                    window.layoutResets.push(() => {
                        localStorage.removeItem(this.storageKey);
                        this.panel.style.width = `${this.defaultWidth}px`;
                    });
                }
            }

            startDragging(e) {
                this.isDragging = true;
                this.startX = e.pageX;
                this.startWidth = parseInt(document.defaultView.getComputedStyle(this.panel).width, 10);
                // Add event listeners
                document.addEventListener('mousemove', this.doDrag);
                document.addEventListener('mouseup', this.stopDragging);
                // Add dragging class
                document.body.classList.add('dragging');
                // Prevent text selection
                e.preventDefault();
            }

            doDrag(e) {
                if (!this.isDragging) return;
                e.preventDefault();
                const containerWidth = this.panel.parentElement.getBoundingClientRect().width;
                // Calculate new width
                let newWidth = this.startWidth + (e.pageX - this.startX);
                // Clamp the width between min and max
                newWidth = Math.max(this.minWidth, Math.min(containerWidth - this.maxWidthOffset, newWidth));
                // Apply the new width
                this.panel.style.width = `${newWidth}px`;
                // Save the width
                localStorage.setItem(this.storageKey, newWidth);
            }

            stopDragging() {
                if (!this.isDragging) return;
                this.isDragging = false;
                document.removeEventListener('mousemove', this.doDrag);
                document.removeEventListener('mouseup', this.stopDragging);
                document.body.classList.remove('dragging');
            }
        }
    }
};

/**
 * Adds a "MagicPrompt Settings" link into the Magic Prompt group in the
 * Generate tab's left sidebar (alongside the extension's other parameter
 * fields). Clicking it opens the same settings modal as the gear button in
 * the MagicPrompt tab.
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
            `<button type="button" class="basic-button" onclick="showSettingsModal()">⚙️ MagicPrompt Settings</button>`));
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
        // Initialize settings
        await loadSettings();
        // Populate instruction prefixes for autocomplete
        mpRefreshInstructionPrefixes();
        // Add a Settings link into the Magic Prompt group in the left sidebar
        addSidebarSettingsLink();
        // Setup auto wildcard seed generation on Generate click (capture phase, before onclick)
        wildcardSeedGenerator();
        // Relocate the MagicPrompt modals to <body> so they render regardless of
        // the active tab. They live inside the MagicPrompt tab-pane, which is
        // display:none when another tab (e.g. Generate) is active, which would
        // otherwise leave the modal invisible (only its backdrop showing) when
        // opened from the sidebar Settings link.
        ['settingsModal', 'customInstructionModal', 'confirmDeleteModal'].forEach(id => {
            const modal = document.getElementById(id);
            if (modal && modal.parentElement !== document.body) {
                document.body.appendChild(modal);
            }
        });
        // Initialize modal
        $('#settingsModal').modal({
            backdrop: true, keyboard: true, show: false
        }).on('show.bs.modal', initSettingsModal);
        // Initialize models
        await fetchModels();
        MP.modelsInitialized = true;
        // Initialize handlers
        if (window.visionHandler) {
            await window.visionHandler.initialize();
        }
        if (window.chatHandler) {
            await window.chatHandler.initialize();
        }
        // Initialize resize handler
        const resizeHandle = document.getElementById('resize_handle');
        const visionSection = document.getElementById('vision_section');
        if (resizeHandle && visionSection) {
            new MP.ResizeHandler({
                handle: resizeHandle,
                panel: visionSection,
                storageKey: 'magicprompt_vision_width',
                defaultWidth: 400,
                minWidth: 300,
                maxWidthOffset: 300
            });
        }
        // Update linked models UI
        const isLinked = MP.settings.linkChatAndVisionModels !== false;
        updateLinkedModelsUI(isLinked);
    } catch (error) {
        console.error('Error initializing MagicPrompt:', error);
        MP.ResponseHandler.showError('Failed to initialize MagicPrompt: ' + error.message);
    }
});

function mpRefreshInstructionPrefixes() {
    const instructions = ['prompt', 'chat', 'vision', 'caption'];
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
        '\nThis allows chaining: use one LLM response as input to another mpprompt.',
        '\nNote: You can only reference responses from mpprompt tags that appear BEFORE this reference.',
        '\nExample usage:',
        '\n  <mpprompt[Style A]:a sunset>',
        '\n  <mpprompt[Enhance]:Enhance this scene: <mpresponse:0>>'
    ];
}, false);
