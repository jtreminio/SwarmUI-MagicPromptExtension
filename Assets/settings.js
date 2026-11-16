/** Prompt settings: one modal, draft edits, native SwarmUI searchable selects. */
'use strict';

const MP_BACKENDS = ['openrouter', 'openaiapi'];
const MP_BUILTINS = { prompt: 'Default prompt', randomprompt: 'Random prompt', instructiongen: 'Instruction generator' };
let mpSettingsDraft = null;
let mpPromptPreferencesAtOpen = null;
let mpEditingInstruction = 'prompt';
let mpEditingDecision = null;
let mpSettingsBusy = false;
let mpSettingsOpening = false;
let mpSettingsCloseQueued = false;
const mpCatalogRequests = new Map();
let mpSaveQueue = Promise.resolve();
let mpSettingsLoad;

function mpRequest(route, payload = {}) {
    return new Promise((resolve, reject) => genericRequest(route, payload, data => {
        if (data.success) resolve(data);
        else reject(new Error(data.error || 'Request failed'));
    }, 0, error => reject(new Error(error?.message || String(error)))));
}

function mpNormalizeSettings(settings) {
    const result = JSON.parse(JSON.stringify(settings));
    if (!MP_BACKENDS.includes(result.backend)) {
        result.backend = 'openrouter';
        result.model = '';
    }
    result.backends ||= {};
    result.backends.openrouter ||= { baseurl: 'https://openrouter.ai', timeout: 60 };
    result.backends.openaiapi ||= { baseurl: 'http://localhost:11434', timeout: 120 };
    result.instructions ||= {};
    result.instructions.custom ||= {};
    result.decisions ||= {
        questions: [{ id: 'refusal', title: 'Prompt refusal', question: 'Did this response refuse to generate the requested prompt?', type: 'noul', options: [], selected: true }]
    };
    delete result.decisions.evaluator;
    result.decisions.model = typeof result.decisions.model === 'string' ? result.decisions.model.trim() : '';
    for (const key of ['blockedModels', 'favoritedModels']) {
        const ids = new Map();
        for (const id of Array.isArray(result.decisions[key]) ? result.decisions[key] : []) {
            if (typeof id === 'string' && id.trim() && !ids.has(id.trim().toLowerCase())) ids.set(id.trim().toLowerCase(), id.trim());
        }
        result.decisions[key] = [...ids.values()];
    }
    const decisionBlocked = new Set(result.decisions.blockedModels.map(id => id.toLowerCase()));
    result.decisions.favoritedModels = result.decisions.favoritedModels.filter(id => !decisionBlocked.has(id.toLowerCase()));
    if (decisionBlocked.has(result.decisions.model.toLowerCase())) result.decisions.model = '';
    for (const key of ['blockedModels', 'favoritedModels']) {
        result[key] ||= {};
        for (const backend of MP_BACKENDS) {
            const ids = new Map();
            for (const id of result[key][backend] || []) {
                if (typeof id === 'string' && id.trim() && !ids.has(mpModelIdKey(id))) ids.set(mpModelIdKey(id), id.trim());
            }
            result[key][backend] = [...ids.values()];
        }
    }
    for (const backend of MP_BACKENDS) {
        const blocked = new Set(result.blockedModels[backend].map(mpModelIdKey));
        result.favoritedModels[backend] = result.favoritedModels[backend].filter(id => !blocked.has(mpModelIdKey(id)));
    }
    return result;
}

function loadSettings() {
    const writes = mpSaveQueue;
    if (mpSettingsLoad?.writes === writes) return mpSettingsLoad.request;
    const request = writes.catch(() => {}).then(async () => {
        const { settings } = await mpRequest('GetMagicPromptSettings');
        if (writes === mpSaveQueue) {
            MP.settings = mpNormalizeSettings(settings);
            MP.settingsLoaded = true;
        }
        return MP.settings;
    }).finally(() => {
        if (mpSettingsLoad?.request === request) mpSettingsLoad = null;
    });
    mpSettingsLoad = { writes, request };
    return request;
}

/** Serialize writes so rapid preference edits cannot arrive out of order. */
function mpQueueSettingsWrite(write) {
    const pending = mpSaveQueue.catch(() => {}).then(write);
    mpSaveQueue = pending;
    return pending;
}

function mpPersistSettings(settings) {
    const snapshot = JSON.parse(JSON.stringify(settings));
    return mpQueueSettingsWrite(() => mpRequest('SaveMagicPromptSettings', { settings: snapshot }));
}

function mpSettingsStatus(message, error = false) {
    const status = document.getElementById('mpSettingsStatus');
    if (status) {
        status.textContent = message;
        status.classList.toggle('mp-status-error', error);
    }
}

function mpCurrentSettings() { return mpSettingsDraft || MP.settings; }
function mpModelSelectOptionText(row, showUnits = true) {
    if (row.decisionModel && showUnits) {
        const prices = [`${row.costIn || '—'} / 1M in`];
        if (row.costOut && row.costOut !== '$0') prices.push(`${row.costOut} / 1M out`);
        if (row.costRequest && row.costRequest !== '$0') prices.push(`${row.costRequest} / request`);
        return `${row.name || row.model} · ${prices.join(' · ')}`;
    }
    const price = row.decisionModel ? row.costIn?.trim() || '—' : row.costOut;
    return `${row.name || row.model}${price ? ` · ${price}${showUnits ? ' / 1M out' : ''}` : ''}`;
}
function mpCatalogKey(settings) { return settings.catalogKey || `${settings.backend}\n${settings.backends[settings.backend]?.baseurl || ''}`; }
function mpCatalog(settings = mpCurrentSettings()) { return MP.modelCatalogs?.[mpCatalogKey(settings)] || []; }
function mpModelIdKey(id) { return (id || '').trim().toLowerCase(); }

/** Adapt decision preferences to the shared model controls without touching prompt settings. */
function mpDecisionModelSettings(settings = mpCurrentSettings()) {
    return { backend: 'openrouter', backends: settings.backends, catalogKey: 'openrouter-decisions', model: settings.decisions.model,
        blockedModels: { openrouter: settings.decisions.blockedModels }, favoritedModels: { openrouter: settings.decisions.favoritedModels } };
}

async function fetchModels(settings = mpCurrentSettings(), reportStatus = true) {
    const key = mpCatalogKey(settings);
    const request = (mpCatalogRequests.get(key) || 0) + 1;
    mpCatalogRequests.set(key, request);
    const decisions = key === 'openrouter-decisions';
    if (reportStatus) mpSettingsStatus('Loading models…');
    try {
        const response = await mpRequest(decisions ? 'GetMagicPromptDecisionModels' : 'GetMagicPromptModels', decisions ? {} : {
            backend: settings.backend, baseurl: settings.backends[settings.backend]?.baseurl || ''
        });
        if (mpCatalogRequests.get(key) !== request) return response;
        MP.modelCatalogs ||= {};
        MP.modelCatalogs[key] = (response.models || []).filter(row => row && row.model);
        if (reportStatus && (decisions || key === mpCatalogKey(mpCurrentSettings()))) {
            if (decisions) { renderDecisionModelSettings(); mpRefreshDecisionModelControls(); }
            else renderModelSettings();
            mpSettingsStatus(`${response.models.length} ${decisions ? 'decision ' : ''}models available.`);
        }
        if (key === mpCatalogKey(MP.settings)) {
            if (reportStatus) updateModelListOnLeft(true);
            else mpRefreshPromptModelControls();
        }
        return response;
    }
    catch (error) {
        if (reportStatus && mpCatalogRequests.get(key) === request) mpSettingsStatus(`Could not load ${decisions ? 'decision ' : ''}models: ${error.message}`, true);
        throw error;
    }
}

const mpNativePromptOptions = new WeakMap();

/** Preserve explicit Generate and preset selections, even if a model was just blocked. */
function mpRefreshPromptModelControls(preserveSelection = true) {
    const settings = MP.settings;
    for (const id of ['input_mpmodelid', 'preset_input_mpmodelid']) {
        const select = document.getElementById(id);
        if (!select) continue;
        const selected = preserveSelection && select.value && select.value !== 'loading' ? select.value : settings.model;
        if (mpCatalog(settings).length) {
            mpNativePromptOptions.delete(select);
            mpFillModelSelect(select, settings, selected, preserveSelection);
        }
        else {
            const options = [...select.options];
            const previous = select.value;
            let original = mpNativePromptOptions.get(select);
            if (!original || original.length !== options.length || options.some(option => !original.includes(option))) {
                original = options;
                mpNativePromptOptions.set(select, original);
            }
            const blocked = new Set(settings.blockedModels[settings.backend]?.map(mpModelIdKey));
            const favorites = new Set(settings.favoritedModels[settings.backend]?.map(mpModelIdKey));
            for (const option of options) {
                const key = mpModelIdKey(option.value);
                if (blocked.has(key)) {
                    option.dataset.blocked = '1';
                    option.style.display = 'none';
                }
                else if (option.dataset.blocked === '1') {
                    delete option.dataset.blocked;
                    option.style.removeProperty('display');
                }
                if (favorites.has(key)) option.dataset.favorited = '1';
                else delete option.dataset.favorited;
            }
            const favorite = option => !!option.value && favorites.has(mpModelIdKey(option.value));
            const ordered = original.slice().sort((a, b) => Number(!b.value) - Number(!a.value) || Number(favorite(b)) - Number(favorite(a)));
            select.replaceChildren(...ordered);
            select.value = options.some(option => option.value === selected) ? selected : previous;
        }
        triggerChangeFor(select);
    }
}

function updateModelListOnLeft(preserveSelection = true) {
    mpRefreshPromptModelControls(preserveSelection);
    const instructions = document.getElementById('input_mpinstructions');
    if (instructions) {
        const previous = instructions.value;
        instructions.replaceChildren();
        for (const [id, instruction] of mpInstructionEntries(MP.settings)) {
            if (id === 'randomprompt' || id === 'instructiongen') continue;
            instructions.add(new Option(instruction.title, id));
        }
        if ([...instructions.options].some(option => option.value === previous)) instructions.value = previous;
        triggerChangeFor(instructions);
    }
    mpRefreshDecisionControls();
    mpRefreshDecisionModelControls();
}

const mpDecisionSelections = new WeakMap();

function mpRememberDecisionSelection(select) {
    if (!mpDecisionSelections.has(select)) {
        select.addEventListener('change', () => mpRememberDecisionSelection(select));
    }
    mpDecisionSelections.set(select, select.dataset.wantsValue || select.value);
}

function mpAddUnavailableDecisionOption(select, value) {
    if (value && !Array.from(select.options).some(option => option.value === value)) {
        const option = new Option(`Saved ${select.id.includes('modelid') ? 'model' : 'question'} unavailable (${value}) — reselect`, value);
        option.dataset.unavailable = '1';
        select.add(option);
    }
}

/** Keep Swarm's parameter definition and both rendered pickers in sync. */
function mpRefreshDecisionControls() {
    const questions = MP.settings.decisions?.questions || [];
    const defaults = questions.some(question => question.selected)
        ? 'Settings defaults' : 'Settings defaults (none selected)';
    const param = typeof gen_param_types !== 'undefined' && gen_param_types?.find(row => row.id === 'mpdecisionquestion');
    if (param) {
        param.values = ['', ...questions.map(question => question.id)];
        param.value_names = [defaults, ...questions.map(question => question.title || question.question)];
    }
    for (const prefix of ['input_', 'preset_input_']) {
        const checkbox = document.getElementById(`${prefix}mpdecisionchecks`);
        checkbox?.setAttribute('aria-label', 'MP Decision Checks');
        const select = document.getElementById(`${prefix}mpdecisionquestion`);
        if (!select) continue;
        select.setAttribute('aria-label', 'MP Decision Question');
        const previous = select.dataset.wantsValue || select.value;
        select.replaceChildren(new Option(defaults, ''));
        for (const question of questions) select.add(new Option(question.title || question.question, question.id));
        mpAddUnavailableDecisionOption(select, previous);
        select.value = previous;
        delete select.dataset.wantsValue;
        mpRememberDecisionSelection(select);
        triggerChangeFor(select);
    }
}

/** Refresh Generate and preset model choices while preserving explicit workflow overrides. */
function mpRefreshDecisionModelControls(rebuildCatalog = true) {
    if (!MP.settings.decisions) return;
    const settings = mpDecisionModelSettings(MP.settings);
    const blocked = new Set(settings.blockedModels.openrouter.map(mpModelIdKey));
    const favorites = new Set(settings.favoritedModels.openrouter.map(mpModelIdKey));
    const label = settings.model ? `Settings default · ${settings.model}` : 'Settings default (not selected)';
    const param = typeof gen_param_types !== 'undefined' && gen_param_types?.find(row => row.id === 'mpdecisionmodelid');
    for (const prefix of ['input_', 'preset_input_']) {
        const select = document.getElementById(`${prefix}mpdecisionmodelid`);
        if (!select) continue;
        const previous = select.dataset.wantsValue || select.value;
        // Retain the server catalog until the browser's independent decision catalog arrives.
        if (rebuildCatalog && MP.modelCatalogs?.[mpCatalogKey(settings)]) mpFillModelSelect(select, settings, previous, true);
        if (!Array.from(select.options).some(option => option.value === '')) select.add(new Option(label, ''), 0);
        const fallback = Array.from(select.options).find(option => option.value === '');
        fallback.text = label;
        for (const option of select.options) {
            delete option.dataset.blocked;
            delete option.dataset.favorited;
            if (blocked.has(mpModelIdKey(option.value))) option.dataset.blocked = '1';
            if (favorites.has(mpModelIdKey(option.value))) option.dataset.favorited = '1';
        }
        mpAddUnavailableDecisionOption(select, previous);
        select.value = previous;
        select.setAttribute('aria-label', 'MP Decision Model ID');
        delete select.dataset.wantsValue;
        mpRememberDecisionSelection(select);
        if (param) {
            // Unavailable explicit overrides belong only to their own picker.
            const options = Array.from(select.options).filter(option => !option.dataset.blocked && !option.dataset.unavailable);
            param.values = options.map(option => option.value);
            param.value_names = options.map(option => option.text);
        }
        triggerChangeFor(select);
    }
}

function mpInstallDecisionControlHandling() {
    // Swarm otherwise defers a preset question ID that has no option.
    if (typeof setDirectParamValue === 'function') {
        const setParam = setDirectParamValue;
        setDirectParamValue = function (param, value, element, ...rest) {
            const select = ['mpdecisionquestion', 'mpdecisionmodelid'].includes(param.id) && (element || document.getElementById(`input_${param.id}`));
            if (select?.tagName !== 'SELECT') return setParam.call(this, param, value, element, ...rest);
            mpAddUnavailableDecisionOption(select, value);
            delete select.dataset.wantsValue;
            const result = setParam.call(this, param, value, element, ...rest);
            mpRememberDecisionSelection(select);
            return result;
        };
    }
    // Native value refresh rewrites both pickers using the Generate value.
    if (typeof refreshParameterValues === 'function') {
        const refresh = refreshParameterValues;
        refreshParameterValues = function (strong, type, callback) {
            const selects = document.querySelectorAll('#input_mpdecisionquestion, #preset_input_mpdecisionquestion, #input_mpdecisionmodelid, #preset_input_mpdecisionmodelid');
            selects.forEach(mpRememberDecisionSelection);
            return refresh.call(this, strong, type, () => {
                for (const select of selects) {
                    if (!select.isConnected) continue;
                    const value = mpDecisionSelections.get(select);
                    mpAddUnavailableDecisionOption(select, value);
                    if (select.value === value) continue;
                    select.value = value;
                    triggerChangeFor(select);
                }
                mpRefreshDecisionModelControls(false);
                fetchModels(mpDecisionModelSettings()).catch(() => {});
                callback?.();
            });
        };
    }
}

/** OpenRouter catalog dates, newest first; stable ties preserve provider order. */
function mpCompareModelDates(a, b, backend) {
    const created = row => Number.isFinite(Number(row.created)) ? Number(row.created) : 0;
    return backend === 'openrouter' ? created(b) - created(a) : 0;
}

function mpFillModelSelect(select, settings, selected, preserveBlockedSelection = false) {
    const backend = settings.backend;
    const key = mpModelIdKey;
    const blocked = new Set(settings.blockedModels[backend].map(key));
    const favorites = new Set(settings.favoritedModels[backend].map(key));
    if (blocked.has(key(selected)) && !preserveBlockedSelection) selected = '';
    // OpenRouter's creation timestamp is the date the model was added to its catalog.
    // Keep favorites pinned, newest first within each group. Stable sorting retains
    // provider order for equal/missing dates and for local catalogs.
    const rows = mpCatalog(settings).slice().sort((a, b) =>
        Number(favorites.has(key(b.model))) - Number(favorites.has(key(a.model))) ||
        mpCompareModelDates(a, b, backend));
    select.replaceChildren(new Option('Select a model', ''));
    const showUnits = !['input_mpmodelid', 'input_mpdecisionmodelid', 'preset_input_mpdecisionmodelid'].includes(select.id);
    for (const row of rows) {
        if (blocked.has(key(row.model)) && row.model !== selected) continue;
        const option = new Option(mpModelSelectOptionText(row, showUnits), row.model);
        if (blocked.has(key(row.model))) option.dataset.blocked = '1';
        if (favorites.has(key(row.model))) option.dataset.favorited = '1';
        if (select.id === 'modelSelect' || select.id === 'mpDecisionModelSelect') {
            const label = escapeHtmlNoBr(option.text);
            const favorite = favorites.has(key(row.model));
            option.dataset.cleanname = `<span class="mp-model-dropdown-row${favorite ? ' mp-model-dropdown-row-favorited' : ''}"><span class="mp-model-dropdown-row-label">${label}</span><span class="mp-model-dropdown-favorite-marker${favorite ? ' mp-model-dropdown-star-active' : ''}" title="${favorite ? 'Favorited model' : 'Not favorited'}" aria-label="${favorite ? 'Favorited model' : 'Not favorited'}">${favorite ? '★' : '☆'}</span></span>`;
        }
        select.add(option);
    }
    // A saved ID may be temporarily absent from a provider's catalog.
    if (selected && selected !== 'loading' && ![...select.options].some(option => option.value === selected)) {
        const option = new Option(`${selected} (unavailable)`, selected);
        option.dataset.unavailable = '1';
        select.add(option);
    }
    select.value = selected === 'loading' ? '' : selected || '';
}

function isModelFavoritedForBackend(backend, id) {
    const decisions = backend === 'decisions';
    return (decisions ? MP.settings.decisions.favoritedModels : MP.settings.favoritedModels?.[backend])
        ?.some(value => mpModelIdKey(value) === mpModelIdKey(id)) || false;
}
function mpEditedModelPreferences(scope, key, id, add) {
    const keep = value => mpModelIdKey(value) !== mpModelIdKey(id);
    const preferences = { ...scope, [key]: scope[key].filter(keep) };
    if (add) {
        preferences[key].push(id);
        const other = key === 'blockedModels' ? 'favoritedModels' : 'blockedModels';
        preferences[other] = scope[other].filter(keep);
    }
    return preferences;
}
function mpSavePromptModelPreference(key, backend, id, add, beforeRefresh) {
    return mpQueueSettingsWrite(async () => {
        const { settings } = await mpRequest('ChangeMagicPromptModelPreference', {
            provider: backend, model: id, preference: key, enabled: add
        });
        const saved = mpNormalizeSettings(settings);
        MP.settings.blockedModels = saved.blockedModels;
        MP.settings.favoritedModels = saved.favoritedModels;
        beforeRefresh?.();
        mpRefreshPromptModelControls();
        if (!mpSettingsDraft) renderModelSettings();
    });
}
function mpChangeModelPreference(key, backend, id, add, beforeRefresh) {
    if (backend !== 'decisions') {
        return mpSavePromptModelPreference(key, backend, id, add, beforeRefresh)
            .catch(error => showError(`Could not save model preferences: ${error.message}`));
    }
    const scope = MP.settings.decisions;
    const changes = mpEditedModelPreferences({ blockedModels: scope.blockedModels, favoritedModels: scope.favoritedModels }, key, id, add);
    Object.assign(scope, changes);
    if (scope.blockedModels.some(value => mpModelIdKey(value) === mpModelIdKey(scope.model))) scope.model = '';
    updateModelListOnLeft(true);
    mpPersistSettings({ decisions: changes })
        .catch(error => showError(`Could not save model preferences: ${error.message}`));
}
Object.assign(MP, {
    isModelFavoritedForBackend,
    blockModelId: (backend, id, beforeRefresh) => mpChangeModelPreference('blockedModels', backend, id, true, beforeRefresh),
    unblockModelId: (backend, id) => mpChangeModelPreference('blockedModels', backend, id, false),
    favoriteModelId: (backend, id, beforeRefresh) => mpChangeModelPreference('favoritedModels', backend, id, true, beforeRefresh),
    unfavoriteModelId: (backend, id, beforeRefresh) => mpChangeModelPreference('favoritedModels', backend, id, false, beforeRefresh)
});

function mpInstructionEntries(settings = mpCurrentSettings()) {
    return [
        ...Object.entries(MP_BUILTINS).map(([id, title]) => [id, { title, content: settings.instructions[id] || '', tooltip: '' }]),
        ...Object.entries(settings.instructions.custom).filter(([, instruction]) => instruction && !instruction.deleted)
    ];
}

function getInstructionContent(id) {
    return MP.settings.instructions.custom?.[id]?.content ?? MP.settings.instructions[id] ?? '';
}

/** WhatTheDuck-style preference picker, with its panel inside the modal focus trap. */
class MPModelPreferencePicker {
    constructor(select, modal) {
        this.select = select;
        this.modal = modal;
        this.root = document.createElement('div');
        this.root.className = 'mp-model-picker';
        this.root.innerHTML = `<div class="mp-model-trigger" role="button" tabindex="0" aria-expanded="false"><span class="mp-model-pills"></span><span class="mp-model-caret">▾</span></div>`;
        select.after(this.root);
        this.trigger = this.root.firstElementChild;
        this.trigger.id = `${select.id}Trigger`;
        this.trigger.setAttribute('aria-labelledby', `${select.id}Label`);
        this.panel = document.createElement('div');
        this.panel.id = `${select.id}Panel`;
        this.panel.className = 'mp-model-panel mp-model-dropdown-popover';
        this.panel.hidden = true;
        this.panel.innerHTML = `<input type="search" class="sui_popover_text_input mp-model-dropdown-search mp-model-filter" placeholder="Search..." aria-label="Filter models" autocomplete="off"><div class="mp-model-list"></div><div class="mp-model-empty" hidden>No matching models.</div><div class="mp-model-actions"><span class="mp-model-count" aria-live="polite"></span><button type="button" class="basic-button">Clear</button></div>`;
        this.panel.setAttribute('role', 'group');
        this.panel.setAttribute('aria-labelledby', `${select.id}Label`);
        this.trigger.setAttribute('aria-controls', this.panel.id);
        modal.append(this.panel);
        this.filter = this.panel.querySelector('input');
        this.list = this.panel.querySelector('.mp-model-list');
        this.trigger.addEventListener('click', event => {
            const remove = event.target.closest('[data-remove-model]');
            if (remove) {
                this.change([...select.selectedOptions].map(option => option.value).filter(value => value !== remove.dataset.removeModel));
                this.trigger.focus();
                return;
            }
            const open = this.panel.hidden;
            mpCloseModelPickers();
            if (open) {
                mpCloseSwarmSettingsDropdown();
                this.panel.hidden = false;
                this.trigger.setAttribute('aria-expanded', 'true');
                this.position();
                this.filter.focus();
            }
        });
        this.trigger.addEventListener('keydown', event => {
            if (event.target === this.trigger && ['Enter', ' '].includes(event.key)) {
                event.preventDefault(); this.trigger.click();
            }
        });
        this.filter.addEventListener('input', () => this.applyFilter());
        this.list.addEventListener('change', () => {
            this.change([...this.list.querySelectorAll('input:checked')].map(box => box.value));
        });
        this.panel.querySelector('button').addEventListener('click', () => this.change([]));
        document.addEventListener('click', event => {
            if (!this.root.contains(event.target) && !this.panel.contains(event.target)) this.close();
        });
        modal.addEventListener('scroll', () => this.position(), true);
        window.addEventListener('resize', () => this.position());
    }
    close(focus = false) {
        this.panel.hidden = true;
        this.trigger.setAttribute('aria-expanded', 'false');
        if (focus) this.trigger.focus();
    }
    change(values) {
        for (const option of this.select.options) option.selected = values.includes(option.value);
        triggerChangeFor(this.select);
    }
    refresh() {
        const options = [...this.select.options];
        const signature = JSON.stringify(options.map(option => [option.value, option.text]));
        if (signature !== this.signature) {
            this.signature = signature;
            this.list.replaceChildren();
            for (const option of options) {
                const label = document.createElement('label');
                label.className = 'sui_popover_model_button mp-model-dropdown-row mp-model-option';
                const box = document.createElement('input');
                box.type = 'checkbox'; box.value = option.value;
                const text = document.createElement('span');
                text.className = 'mp-model-dropdown-row-label';
                text.textContent = option.dataset.label || option.text;
                label.title = option.value;
                label.append(box, text); this.list.append(label);
            }
        }
        const selected = new Set(options.filter(option => option.selected).map(option => option.value));
        for (const box of this.list.querySelectorAll('input')) {
            box.checked = selected.has(box.value);
            box.closest('label').classList.toggle('sui_popover_model_button_selected', box.checked);
        }
        const pills = this.root.querySelector('.mp-model-pills');
        pills.replaceChildren();
        for (const option of options.filter(option => option.selected)) {
            const pill = document.createElement('span');
            pill.className = 'mp-model-pill'; pill.title = option.text;
            const text = document.createElement('span'); text.textContent = option.text;
            const remove = document.createElement('button');
            remove.type = 'button'; remove.textContent = '×'; remove.dataset.removeModel = option.value;
            remove.setAttribute('aria-label', `Remove ${option.text}`);
            pill.append(text, remove); pills.append(pill);
        }
        if (!selected.size) {
            const placeholder = document.createElement('span');
            placeholder.className = 'mp-model-placeholder'; placeholder.textContent = 'Select models…';
            pills.append(placeholder);
        }
        this.panel.querySelector('.mp-model-count').textContent = `${selected.size} selected`;
        this.applyFilter(); this.position();
    }
    applyFilter() {
        const query = this.filter.value.trim().toLowerCase();
        let visible = 0;
        for (const label of this.list.children) {
            label.hidden = !`${label.textContent} ${label.querySelector('input').value}`.toLowerCase().includes(query);
            if (!label.hidden) visible++;
        }
        this.panel.querySelector('.mp-model-empty').hidden = visible > 0;
    }
    position() {
        if (this.panel.hidden) return;
        const rect = this.trigger.getBoundingClientRect();
        // Portal outside the scrolling body so long preference lists are not clipped.
        const margin = 8;
        const below = window.innerHeight - rect.bottom - margin;
        const above = rect.top - margin;
        const upward = below < 330 && above > below;
        const room = Math.max(0, upward ? above : below);
        Object.assign(this.panel.style, {
            left: `${rect.left}px`, width: `${rect.width}px`, maxHeight: `${room}px`,
            top: upward ? 'auto' : `${rect.bottom + 4}px`,
            bottom: upward ? `${window.innerHeight - rect.top + 4}px` : 'auto'
        });
    }
}
const mpModelPickers = [];
function mpCloseModelPickers() { mpModelPickers.forEach(picker => picker.close()); }
function mpCloseSwarmSettingsDropdown(focus = false) {
    const popover = typeof uiImprover !== 'undefined' && uiImprover.lastPopover;
    if (!popover?.popover?.isConnected || !document.getElementById('settingsModal')?.contains(popover.popover)) return false;
    const select = document.getElementById(popover.popover.id.replace('popover_uiimprover_', ''));
    popover.remove();
    if (focus) select?.focus();
    return true;
}

function initSettingsModal() {
    if (document.getElementById('settingsModal')) return;
    document.body.insertAdjacentHTML('beforeend', `
    <div class="modal fade mp-settings" id="settingsModal" tabindex="-1" aria-labelledby="mpSettingsTitle" aria-hidden="true">
      <div class="modal-dialog modal-dialog-centered"><div class="modal-content">
        <div class="modal-header"><div><h5 class="modal-title" id="mpSettingsTitle">MagicPrompt</h5><p class="mp-muted">Prompt tools for Generate</p></div>
          <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close settings"></button></div>
        <div class="mp-settings-tabs" role="tablist" aria-label="Settings sections">
          <button type="button" role="tab" id="mpTabProvider" aria-controls="mpPanelProvider" aria-selected="true" data-mp-tab="Provider">Provider</button>
          <button type="button" role="tab" id="mpTabModels" aria-controls="mpPanelModels" aria-selected="false" tabindex="-1" data-mp-tab="Models">Models</button>
          <button type="button" role="tab" id="mpTabInstructions" aria-controls="mpPanelInstructions" aria-selected="false" tabindex="-1" data-mp-tab="Instructions">Instructions</button>
          <button type="button" role="tab" id="mpTabDecisionModels" aria-controls="mpPanelDecisionModels" aria-selected="false" tabindex="-1" data-mp-tab="DecisionModels">Decision Models</button>
          <button type="button" role="tab" id="mpTabDecisions" aria-controls="mpPanelDecisions" aria-selected="false" tabindex="-1" data-mp-tab="Decisions">Decisions</button>
        </div>
        <div class="modal-body">
          <section id="mpPanelProvider" role="tabpanel" aria-labelledby="mpTabProvider">
            <h6>Connection</h6><p class="mp-muted">Choose the provider used to rewrite prompts during generation.</p>
            <label class="mp-field" for="mpBackend">Provider<select id="mpBackend" class="form-select"><option value="openrouter">OpenRouter</option><option value="openaiapi">OpenAI-compatible API (local)</option></select></label>
            <label class="mp-field" for="mpBaseUrl" id="mpBaseUrlField">Server URL<input class="form-control" id="mpBaseUrl" type="url" placeholder="http://localhost:1234"></label>
            <label class="mp-field mp-timeout" for="mpTimeout">Timeout (seconds)<input class="form-control" id="mpTimeout" type="number" min="1" max="3600" step="1"></label>
            <div class="mp-key-note"><strong>API keys</strong><p class="mp-muted">Manage provider keys in SwarmUI’s User settings.</p><button type="button" class="basic-button" id="mpApiKeys">Open API keys</button></div>
          </section>
          <section id="mpPanelModels" role="tabpanel" aria-labelledby="mpTabModels" hidden>
            <div class="mp-section-heading"><div><h6>Model preferences</h6><p class="mp-muted">Favorites appear first. Blocked models stay out of the model list.</p></div><button type="button" class="basic-button" id="mpRefreshModels">Refresh</button></div>
            <div class="mp-field"><label for="modelSelect">Default model</label><select class="form-select" id="modelSelect"></select></div>
            <div class="mp-field"><label id="mpFavoriteModelsLabel">Favorited models</label><select id="mpFavoriteModels" multiple hidden></select></div>
            <div class="mp-field"><label id="mpBlockedModelsLabel">Blocked models</label><select id="mpBlockedModels" multiple hidden></select></div>
            <p class="mp-muted">Search by model name or ID. Remove a selection to undo a preference. Prices are USD per million output tokens.</p>
          </section>
          <section id="mpPanelInstructions" role="tabpanel" aria-labelledby="mpTabInstructions" hidden>
            <div class="mp-section-heading"><div><h6>Instruction library</h6><p class="mp-muted">Select an instruction to edit its name and system prompt.</p></div><div class="mp-toolbar">
              <button type="button" class="basic-button" id="mpImport">Import</button><button type="button" class="basic-button" id="mpExportAll">Export all</button><button type="button" class="basic-button mp-primary" id="mpNewInstruction">New instruction</button>
              <input type="file" id="mpImportFile" accept="application/json,.json" hidden></div></div>
            <div class="mp-library"><div class="mp-library-sidebar"><label class="visually-hidden" for="mpInstructionSearch">Search instructions</label><input type="search" class="form-control" id="mpInstructionSearch" placeholder="Search instructions"><div id="mpInstructionList" class="mp-instruction-list" aria-label="Instructions"></div></div>
              <div class="mp-editor"><label class="mp-field" for="mpInstructionTitle">Name<input class="form-control" id="mpInstructionTitle"></label>
                <label class="mp-field" for="mpInstructionTooltip">Description<input class="form-control" id="mpInstructionTooltip" placeholder="Optional description"></label>
                <label class="mp-field mp-content-field" for="mpInstructionContent">System prompt<textarea class="form-control" id="mpInstructionContent" rows="12" spellcheck="false"></textarea></label>
                <div class="mp-toolbar"><button type="button" class="basic-button" id="mpExportInstruction">Export</button><button type="button" class="basic-button mp-danger" id="mpDeleteInstruction">Delete</button></div>
                <details class="mp-ai-generator"><summary>Draft with AI</summary><label class="mp-field" for="mpInstructionBrief">Describe the instruction<textarea class="form-control" id="mpInstructionBrief" rows="3"></textarea></label><button type="button" class="basic-button" id="mpGenerateInstruction">Generate draft</button><p class="mp-muted">Uses your saved provider and default model. Review the generated text before saving.</p></details>
              </div></div>
          </section>
          <section id="mpPanelDecisionModels" role="tabpanel" aria-labelledby="mpTabDecisionModels" hidden>
            <div class="mp-section-heading"><div><h6>Decision model preferences</h6><p class="mp-muted">OpenRouter models for decision checks. Favorites and blocks apply only to decisions.</p></div><button type="button" class="basic-button" id="mpRefreshDecisionModels">Refresh</button></div>
            <div class="mp-field"><label for="mpDecisionModelSelect">Default decision model</label><select class="form-select" id="mpDecisionModelSelect"></select></div>
            <div class="mp-field"><label id="mpDecisionFavoriteModelsLabel">Favorited decision models</label><select id="mpDecisionFavoriteModels" multiple hidden></select></div>
            <div class="mp-field"><label id="mpDecisionBlockedModelsLabel">Blocked decision models</label><select id="mpDecisionBlockedModels" multiple hidden></select></div>
            <p class="mp-muted">Uses your OpenRouter API key from User settings. Select a default before enabling decision checks. Prices are USD; input tokens and request fees may be charged even when output tokens are free.</p>
          </section>
          <section id="mpPanelDecisions" role="tabpanel" aria-labelledby="mpTabDecisions" hidden>
            <div class="mp-section-heading"><div><h6>Decision library</h6><p class="mp-muted">Select a decision to edit its question, answer options, and skip rule.</p></div><div class="mp-toolbar">
              <button type="button" class="basic-button" id="mpImportDecisions">Import</button><button type="button" class="basic-button" id="mpExportDecisions">Export all</button><button type="button" class="basic-button mp-primary" id="mpNewDecision">New decision</button>
              <input type="file" id="mpImportDecisionsFile" accept="application/json,.json" hidden></div></div>
            <div class="mp-library"><div class="mp-library-sidebar"><label class="visually-hidden" for="mpDecisionSearch">Search decisions</label><input class="form-control" type="search" id="mpDecisionSearch" placeholder="Search decisions"><div id="mpDecisionList" class="mp-instruction-list"></div></div>
              <div id="mpDecisionQuestions" class="mp-editor"></div></div>
            <template id="mpDecisionQuestionTemplate"><div class="mp-decision-question">
              <label class="mp-field">Title<input class="form-control mp-decision-title" maxlength="100" placeholder="Prompt refusal"></label>
              <label><input class="mp-decision-selected" type="checkbox"> Include in Settings defaults</label>
              <label class="mp-field mp-content-field">Question<textarea class="form-control mp-decision-text" rows="8" maxlength="2000"></textarea></label>
              <label class="mp-field">Answer type<select class="form-select mp-decision-type"><option value="noul">Yes or no</option><option value="choice">Multiple choice</option><option value="score">Rating</option></select></label>
              <label class="mp-field mp-decision-options-field">Answer options<textarea class="form-control mp-decision-options" rows="3"></textarea><span class="mp-muted"></span></label>
              <div class="mp-field"><label class="mp-decision-skip-label">Skip If</label><input class="form-control mp-decision-skip-if" maxlength="500"><span class="mp-muted mp-decision-skip-help"></span></div>
              <div class="mp-toolbar"><button type="button" class="basic-button mp-export-decision">Export</button><button type="button" class="basic-button mp-danger mp-remove-decision">Delete decision</button></div>
            </div></template>
          </section>
        </div>
        <div class="modal-footer"><span id="mpSettingsStatus" role="status" aria-live="polite"></span><button type="button" class="basic-button" data-bs-dismiss="modal">Cancel</button><button type="button" class="basic-button mp-primary" id="mpSaveSettings">Save and close</button></div>
      </div></div>
    </div>`);
    const modal = document.getElementById('settingsModal');
    for (const id of ['modelSelect', 'mpDecisionModelSelect']) {
        new ResizeObserver(entries => {
            const width = entries[0]?.target.getBoundingClientRect().width;
            if (width) modal.style.setProperty('--mp-model-menu-width', `${width}px`);
        }).observe(document.getElementById(id));
    }
    modal.addEventListener('hide.bs.modal', event => { if (mpSettingsBusy) event.preventDefault(); });
    modal.addEventListener('hidden.bs.modal', () => { mpSettingsDraft = mpPromptPreferencesAtOpen = null; });
    modal.addEventListener('hide.bs.modal', () => { mpCloseModelPickers(); mpCloseSwarmSettingsDropdown(); });
    modal.addEventListener('shown.bs.modal', () => {
        mpSettingsOpening = false;
        if (mpSettingsCloseQueued) closeSettingsModal();
    });
    modal.addEventListener('click', event => {
        if (event.target.closest('[data-bs-dismiss="modal"]')) {
            event.preventDefault(); event.stopPropagation(); closeSettingsModal();
        }
    });
    modal.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        const picker = mpModelPickers.find(picker => !picker.panel.hidden);
        if (picker || mpCloseSwarmSettingsDropdown(true)) {
            event.preventDefault(); event.stopPropagation();
            picker?.close(true);
        }
        else if (mpSettingsOpening) {
            event.preventDefault(); event.stopPropagation(); closeSettingsModal();
        }
    }, true);
    modal.querySelectorAll('[data-mp-tab]').forEach(button => {
        button.addEventListener('click', () => mpSelectSettingsTab(button.dataset.mpTab));
        button.addEventListener('keydown', event => {
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
            event.preventDefault();
            const tabs = [...modal.querySelectorAll('[data-mp-tab]')];
            let index = tabs.indexOf(button);
            index = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
                : (index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
            tabs[index].click(); tabs[index].focus();
        });
    });
    document.getElementById('mpDecisionSearch').addEventListener('input', () => { mpCaptureDecisionEditor(); mpRenderDecisionList(); });
    document.getElementById('mpNewDecision').addEventListener('click', () => {
        mpCaptureDecisionEditor();
        if (mpSettingsDraft.decisions.questions.length >= 32) { mpSettingsStatus('You can save up to 32 decisions.', true); return; }
        const id = `decision-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
        mpSettingsDraft.decisions.questions.push({ id, title: mpUniqueDecisionTitle('New decision'), question: '', type: 'noul', options: [], selected: true, skipIf: '' });
        mpEditingDecision = id;
        document.getElementById('mpDecisionSearch').value = '';
        mpRenderDecisionList(); mpRenderDecisionEditor();
        document.querySelector('.mp-decision-title').select();
    });
    document.getElementById('mpImportDecisions').addEventListener('click', () => document.getElementById('mpImportDecisionsFile').click());
    document.getElementById('mpImportDecisionsFile').addEventListener('change', async event => {
        try { await mpImportDecisions(event.target.files[0]); }
        catch (error) { mpSettingsStatus(`Import failed: ${error.message}`, true); }
        event.target.value = '';
    });
    document.getElementById('mpExportDecisions').addEventListener('click', () => mpExportDecisions(false));
    document.getElementById('mpBackend').addEventListener('change', event => {
        mpCaptureProvider(false);
        mpSettingsDraft.backend = event.target.value;
        mpSettingsDraft.model = '';
        mpRenderProvider(); renderModelSettings();
        fetchModels().catch(() => {});
    });
    document.getElementById('mpBaseUrl').addEventListener('change', () => { mpCaptureProvider(); renderModelSettings(); });
    document.getElementById('mpRefreshModels').addEventListener('click', () => { mpCaptureProvider(); fetchModels().catch(() => {}); });
    document.getElementById('mpRefreshDecisionModels').addEventListener('click', () => fetchModels(mpDecisionModelSettings()).catch(() => {}));
    document.getElementById('mpSaveSettings').addEventListener('click', saveSettings);
    document.getElementById('mpApiKeys').addEventListener('click', () => {
        modal.addEventListener('hidden.bs.modal', () => {
            document.getElementById('usersettingstabbutton')?.click();
            document.getElementById('userinfotabbutton')?.click();
            document.getElementById('api_keys_table')?.scrollIntoView({ block: 'center' });
        }, { once: true });
        closeSettingsModal();
    });
    document.getElementById('mpInstructionSearch').addEventListener('input', renderInstructionList);
    document.getElementById('mpNewInstruction').addEventListener('click', () => {
        mpCaptureInstruction();
        const id = `custom-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
        mpSettingsDraft.instructions.custom[id] = { id, title: 'New instruction', content: '', tooltip: '', categories: ['prompt'], created: new Date().toISOString() };
        document.getElementById('mpInstructionSearch').value = '';
        mpEditingInstruction = id; renderInstructionList(); mpRenderInstruction();
        document.getElementById('mpInstructionTitle').select();
    });
    document.getElementById('mpDeleteInstruction').addEventListener('click', () => {
        const instruction = mpSettingsDraft.instructions.custom[mpEditingInstruction];
        if (!instruction || !confirm(`Delete “${instruction.title}”? This takes effect when you save settings.`)) return;
        mpSettingsDraft.instructions.custom[mpEditingInstruction] = { deleted: true };
        mpEditingInstruction = 'prompt'; renderInstructionList(); mpRenderInstruction();
    });
    document.getElementById('mpImport').addEventListener('click', () => document.getElementById('mpImportFile').click());
    document.getElementById('mpImportFile').addEventListener('change', async event => {
        try { await importCustomInstructions(event.target.files[0]); }
        catch (error) { mpSettingsStatus(`Import failed: ${error.message}`, true); }
        event.target.value = '';
    });
    document.getElementById('mpExportAll').addEventListener('click', () => exportInstructions(false));
    document.getElementById('mpExportInstruction').addEventListener('click', () => exportInstructions(true));
    document.getElementById('mpGenerateInstruction').addEventListener('click', generateInstructionWithAI);
    document.getElementById('mpInstructionTitle').addEventListener('input', () => { mpCaptureInstruction(); renderInstructionList(); });
    // Preferences use the same checkbox-and-pill pattern as WhatTheDuck.
    for (const [id, key, decisions] of [
        ['mpFavoriteModels', 'favoritedModels', false], ['mpBlockedModels', 'blockedModels', false],
        ['mpDecisionFavoriteModels', 'favoritedModels', true], ['mpDecisionBlockedModels', 'blockedModels', true]
    ]) {
        const select = document.getElementById(id);
        mpModelPickers.push(new MPModelPreferencePicker(select, modal));
        select.addEventListener('change', () => {
            if (!mpSettingsDraft) return;
            const values = [...select.selectedOptions].map(option => option.value);
            const selected = new Set(values.map(mpModelIdKey));
            const other = key === 'blockedModels' ? 'favoritedModels' : 'blockedModels';
            if (decisions) {
                mpSettingsDraft.decisions[key] = values;
                mpSettingsDraft.decisions[other] = mpSettingsDraft.decisions[other].filter(id => !selected.has(mpModelIdKey(id)));
                renderDecisionModelSettings();
            }
            else {
                const backend = mpSettingsDraft.backend;
                mpSettingsDraft[key][backend] = values;
                mpSettingsDraft[other][backend] = mpSettingsDraft[other][backend].filter(id => !selected.has(mpModelIdKey(id)));
                renderModelSettings();
            }
        });
    }
    document.getElementById('modelSelect').addEventListener('change', () => {
        if (mpSettingsDraft) mpSettingsDraft.model = document.getElementById('modelSelect').value;
    });
    document.getElementById('mpDecisionModelSelect').addEventListener('change', event => {
        if (mpSettingsDraft) mpSettingsDraft.decisions.model = event.target.value;
    });
}

function mpSelectSettingsTab(name) {
    mpCloseModelPickers(); mpCloseSwarmSettingsDropdown();
    const modal = document.getElementById('settingsModal');
    modal.querySelectorAll('[data-mp-tab]').forEach(button => {
        const selected = button.dataset.mpTab === name;
        button.setAttribute('aria-selected', String(selected)); button.tabIndex = selected ? 0 : -1;
        document.getElementById(`mpPanel${button.dataset.mpTab}`).hidden = !selected;
    });
    if (name === 'DecisionModels' && !mpCatalog(mpDecisionModelSettings()).length) fetchModels(mpDecisionModelSettings()).catch(() => {});
}
function mpRenderProvider() {
    const settings = mpSettingsDraft;
    document.getElementById('mpBackend').value = settings.backend;
    document.getElementById('mpBaseUrlField').hidden = settings.backend !== 'openaiapi';
    document.getElementById('mpBaseUrl').value = settings.backends[settings.backend].baseurl;
    document.getElementById('mpTimeout').value = settings.backends[settings.backend].timeout;
}
function mpCaptureProvider(validate = false) {
    const timeout = document.getElementById('mpTimeout');
    if (validate && (!timeout.value || !timeout.reportValidity())) throw new Error('Enter a timeout between 1 and 3600 seconds.');
    const config = mpSettingsDraft.backends[mpSettingsDraft.backend];
    config.timeout = Number(timeout.value);
    if (mpSettingsDraft.backend === 'openaiapi') {
        const url = document.getElementById('mpBaseUrl').value.trim();
        if (validate && !/^https?:\/\/[^\s]+$/i.test(url)) throw new Error('Enter an HTTP or HTTPS server URL.');
        config.baseurl = url;
    }
}

function renderModelSettings() {
    if (!document.getElementById('modelSelect')) return;
    const settings = mpCurrentSettings();
    mpRenderModelPreferences(settings, 'modelSelect', 'mpFavoriteModels', 'mpBlockedModels');
    if (mpSettingsDraft) mpSettingsDraft.model = document.getElementById('modelSelect').value;
}

function renderDecisionModelSettings() {
    if (!document.getElementById('mpDecisionModelSelect')) return;
    mpRenderModelPreferences(mpDecisionModelSettings(), 'mpDecisionModelSelect', 'mpDecisionFavoriteModels', 'mpDecisionBlockedModels');
    if (mpSettingsDraft) mpSettingsDraft.decisions.model = document.getElementById('mpDecisionModelSelect').value;
}

function mpRenderModelPreferences(settings, modelId, favoritesId, blockedId) {
    mpFillModelSelect(document.getElementById(modelId), settings, settings.model);
    const modelKey = mpModelIdKey;
    const blocked = new Set(settings.blockedModels[settings.backend].map(modelKey));
    for (const [id, key] of [[favoritesId, 'favoritedModels'], [blockedId, 'blockedModels']]) {
        const select = document.getElementById(id);
        const chosen = settings[key][settings.backend];
        const selected = new Set(chosen.map(modelKey));
        const rows = new Map(mpCatalog(settings).map(row => [modelKey(row.model), row]));
        for (const model of chosen) if (!rows.has(modelKey(model))) rows.set(modelKey(model), { model });
        select.replaceChildren();
        for (const row of [...rows.values()].sort((a, b) => mpCompareModelDates(a, b, settings.backend))) {
            if (key === 'favoritedModels' && blocked.has(modelKey(row.model))) continue;
            const option = new Option(`${mpModelSelectOptionText(row)}${row.name && row.name !== row.model ? ` — ${row.model}` : ''}`, row.model, false, selected.has(modelKey(row.model)));
            option.dataset.label = mpModelSelectOptionText(row);
            select.add(option);
        }
        mpModelPickers.find(picker => picker.select === select)?.refresh();
    }
}

function mpCaptureInstruction() {
    if (!mpSettingsDraft) return;
    const content = document.getElementById('mpInstructionContent').value;
    if (MP_BUILTINS[mpEditingInstruction]) mpSettingsDraft.instructions[mpEditingInstruction] = content;
    else if (mpSettingsDraft.instructions.custom[mpEditingInstruction]) {
        Object.assign(mpSettingsDraft.instructions.custom[mpEditingInstruction], {
            title: document.getElementById('mpInstructionTitle').value.trim(),
            tooltip: document.getElementById('mpInstructionTooltip').value.trim(), content,
            updated: new Date().toISOString()
        });
    }
}
function renderInstructionList() {
    const list = document.getElementById('mpInstructionList');
    const query = document.getElementById('mpInstructionSearch').value.trim().toLowerCase();
    list.replaceChildren();
    for (const [id, instruction] of mpInstructionEntries()) {
        if (!`${instruction.title} ${instruction.tooltip || ''}`.toLowerCase().includes(query)) continue;
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'mp-instruction-choice';
        button.textContent = instruction.title || 'Untitled instruction';
        button.setAttribute('aria-pressed', String(id === mpEditingInstruction));
        button.addEventListener('click', () => { mpCaptureInstruction(); mpEditingInstruction = id; renderInstructionList(); mpRenderInstruction(); });
        list.append(button);
    }
    if (!list.children.length) list.textContent = 'No matching instructions.';
}
function mpRenderInstruction() {
    const instruction = mpInstructionEntries().find(([id]) => id === mpEditingInstruction)?.[1];
    if (!instruction) return;
    const builtin = !!MP_BUILTINS[mpEditingInstruction];
    document.getElementById('mpInstructionTitle').value = instruction.title;
    document.getElementById('mpInstructionTitle').disabled = builtin;
    document.getElementById('mpInstructionTooltip').value = instruction.tooltip || '';
    document.getElementById('mpInstructionTooltip').disabled = builtin;
    document.getElementById('mpInstructionContent').value = instruction.content || '';
    document.getElementById('mpDeleteInstruction').hidden = builtin;
}
function mpUniqueDecisionTitle(title) {
    const titles = new Set(mpSettingsDraft.decisions.questions.map(row => row.title.toLowerCase()));
    let result = title;
    for (let index = 2; titles.has(result.toLowerCase()); index++) result = `${title.slice(0, 90)} (${index})`;
    return result;
}
function mpCaptureDecisionEditor() {
    const row = document.querySelector('#mpDecisionQuestions .mp-decision-question');
    if (!row || !mpSettingsDraft) return;
    const decision = mpSettingsDraft.decisions.questions.find(item => item.id === row.dataset.decisionId);
    if (!decision) return;
    Object.assign(decision, {
        title: row.querySelector('.mp-decision-title').value.trim(),
        question: row.querySelector('.mp-decision-text').value.trim(),
        type: row.querySelector('.mp-decision-type').value,
        options: row.querySelector('.mp-decision-options').value.split('\n').map(option => option.trim()).filter(Boolean),
        skipIf: row.querySelector('.mp-decision-skip-if:enabled')?.value.trim() || '',
        selected: row.querySelector('.mp-decision-selected').checked
    });
}
function mpRenderDecisionList() {
    const list = document.getElementById('mpDecisionList');
    const query = document.getElementById('mpDecisionSearch').value.trim().toLowerCase();
    list.replaceChildren();
    for (const decision of mpSettingsDraft.decisions.questions) {
        if (!`${decision.title} ${decision.question}`.toLowerCase().includes(query)) continue;
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'mp-instruction-choice';
        button.textContent = decision.title || 'Untitled decision';
        button.setAttribute('aria-pressed', String(decision.id === mpEditingDecision));
        button.addEventListener('click', () => {
            mpCaptureDecisionEditor(); mpEditingDecision = decision.id;
            mpRenderDecisionList(); mpRenderDecisionEditor();
        });
        list.append(button);
    }
    if (!list.children.length) list.textContent = 'No matching decisions.';
}
function mpRenderDecisionEditor() {
    const editor = document.getElementById('mpDecisionQuestions');
    editor.replaceChildren();
    const decision = mpSettingsDraft.decisions.questions.find(row => row.id === mpEditingDecision);
    if (decision) mpAddDecisionQuestion(decision);
    else editor.textContent = 'Create a decision to start your library.';
}

function mpAddDecisionQuestion(decision) {
    const row = document.getElementById('mpDecisionQuestionTemplate').content.firstElementChild.cloneNode(true);
    row.dataset.decisionId = decision.id;
    row.querySelector('.mp-decision-selected').checked = decision.selected;
    const title = row.querySelector('.mp-decision-title');
    title.value = decision.title ?? decision.question.slice(0, 90);
    const question = row.querySelector('.mp-decision-text');
    const type = row.querySelector('.mp-decision-type');
    const options = row.querySelector('.mp-decision-options');
    const skipIf = row.querySelector('.mp-decision-skip-if');
    const skipHelp = row.querySelector('.mp-decision-skip-help');
    skipIf.id = `mp-decision-skip-${decision.id}`;
    skipHelp.id = `${skipIf.id}-help`;
    row.querySelector('.mp-decision-skip-label').htmlFor = skipIf.id;
    skipIf.setAttribute('aria-describedby', skipHelp.id);
    skipIf.value = decision.skipIf || '';
    question.value = decision.question;
    type.value = decision.type;
    options.value = decision.options.join('\n');
    const hints = {
        noul: 'Optional. Enter descriptions for No and Yes on separate lines, in that order.',
        choice: 'Enter 2–26 distinct options, one per line.',
        score: 'Enter 2–10 levels, one per line from lowest to highest.'
    };
    const updateHint = () => {
        row.querySelector('.mp-decision-options-field .mp-muted').textContent = hints[type.value];
        skipIf.disabled = type.value === 'score';
        skipIf.placeholder = type.value === 'noul' ? 'yes or no' : type.value === 'choice' ? 'One exact choice label' : '';
        skipHelp.textContent = skipIf.disabled ? 'Ratings do not support skipping.' : 'Leave blank to keep generating after any answer.';
    };
    type.addEventListener('change', updateHint);
    updateHint();
    title.addEventListener('input', () => { mpCaptureDecisionEditor(); mpRenderDecisionList(); });
    row.querySelector('.mp-export-decision').addEventListener('click', () => mpExportDecisions(true));
    row.querySelector('.mp-remove-decision').addEventListener('click', () => {
        if (!confirm(`Delete “${title.value}”? This takes effect when you save settings.`)) return;
        const questions = mpSettingsDraft.decisions.questions;
        const index = questions.findIndex(item => item.id === decision.id);
        questions.splice(index, 1);
        mpEditingDecision = questions[Math.min(index, questions.length - 1)]?.id || null;
        mpRenderDecisionList(); mpRenderDecisionEditor();
        document.getElementById('mpNewDecision').focus();
    });
    document.getElementById('mpDecisionQuestions').append(row);
    return title;
}

function mpRevealDecision(id) {
    mpEditingDecision = id;
    document.getElementById('mpDecisionSearch').value = '';
    mpRenderDecisionList(); mpRenderDecisionEditor(); mpSelectSettingsTab('Decisions');
}

function mpCaptureDecisions() {
    mpCaptureDecisionEditor();
    const questions = mpSettingsDraft.decisions.questions;
    const titles = questions.map(row => row.title.toLowerCase());
    const badTitle = questions.find((row, index) => !row.title || titles.indexOf(titles[index]) !== index);
    if (badTitle) {
        mpRevealDecision(badTitle.id);
        throw new Error('Give every question a unique title.');
    }
    for (const decision of questions) {
        const { question, type, options } = decision;
        const validCount = type === 'noul' ? [0, 2].includes(options.length)
            : options.length >= 2 && options.length <= (type === 'score' ? 10 : 26);
        if (!question || !validCount || new Set(options).size !== options.length || options.some(option => option.length > 500)) {
            mpRevealDecision(decision.id);
            throw new Error('Enter a question and follow its answer option instructions. Each option must be distinct and at most 500 characters.');
        }
    }
    mpSettingsDraft.decisions.questions = questions;
}

async function mpImportDecisions(file) {
    if (!file || !mpSettingsDraft || mpSettingsBusy) return;
    const draft = mpSettingsDraft;
    const data = JSON.parse(await file.text());
    if (draft !== mpSettingsDraft || mpSettingsBusy) return;
    if (!data || typeof data !== 'object') throw new Error('Expected decision JSON.');
    const rows = typeof data.question === 'string' ? [data] : Object.values(data);
    if (!rows.length || rows.some(row => !row || typeof row.title !== 'string' || !row.title.trim() || row.title.length > 100
        || typeof row.question !== 'string' || !row.question.trim() || row.question.length > 2000
        || !['noul', 'choice', 'score'].includes(row.type) || !Array.isArray(row.options) || row.options.some(option => typeof option !== 'string')))
        throw new Error('Each decision needs a title, question, answer type, and option list.');
    mpCaptureDecisionEditor();
    if (mpSettingsDraft.decisions.questions.length + rows.length > 32) throw new Error('You can save up to 32 decisions.');
    for (const row of rows) {
        const id = `decision-${Date.now()}-${Math.random().toString(36).slice(2, 10)}`;
        mpSettingsDraft.decisions.questions.push({ id, title: mpUniqueDecisionTitle(row.title.trim()), question: row.question.trim(),
            type: row.type, options: row.options.map(option => option.trim()), selected: row.selected === true, skipIf: typeof row.skipIf === 'string' ? row.skipIf : '' });
        mpEditingDecision = id;
    }
    document.getElementById('mpDecisionSearch').value = '';
    mpRenderDecisionList(); mpRenderDecisionEditor();
    mpSettingsStatus(`Imported ${rows.length} decisions. Save to keep them.`);
}
function mpExportDecisions(single) {
    mpCaptureDecisionEditor();
    const rows = mpSettingsDraft.decisions.questions.filter(row => !single || row.id === mpEditingDecision);
    const url = URL.createObjectURL(new Blob([JSON.stringify(Object.fromEntries(rows.map(row => [row.id, row])), null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'magicprompt-decisions.json';
    anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}

async function showSettingsModal() {
    initSettingsModal();
    const modal = document.getElementById('settingsModal');
    if (modal.classList.contains('show')) return;
    try {
        await mpSaveQueue.catch(() => {});
        if (!MP.settingsLoaded) await loadSettings();
    }
    catch (error) { showError(`Could not load MagicPrompt settings: ${error.message}`); return; }
    mpSettingsDraft = mpNormalizeSettings(MP.settings);
    mpPromptPreferencesAtOpen = structuredClone({
        blockedModels: mpSettingsDraft.blockedModels,
        favoritedModels: mpSettingsDraft.favoritedModels
    });
    mpEditingInstruction = 'prompt';
    document.getElementById('mpInstructionSearch').value = '';
    mpRenderProvider(); renderModelSettings(); renderDecisionModelSettings(); renderInstructionList(); mpRenderInstruction();
    mpEditingDecision = mpSettingsDraft.decisions.questions[0]?.id || null;
    document.getElementById('mpDecisionSearch').value = '';
    mpRenderDecisionList(); mpRenderDecisionEditor();
    mpSelectSettingsTab('Provider'); mpSettingsStatus('Changes apply when you save.');
    mpSettingsOpening = true; mpSettingsCloseQueued = false;
    (bootstrap.Modal.getInstance(modal) || new bootstrap.Modal(modal)).show();
    if (!mpCatalog().length) fetchModels().catch(() => {});
}
function closeSettingsModal() {
    if (mpSettingsOpening) { mpSettingsCloseQueued = true; return; }
    bootstrap.Modal.getInstance(document.getElementById('settingsModal'))?.hide();
}

async function saveSettings() {
    if (mpSettingsBusy || !mpSettingsDraft) return;
    try {
        mpCaptureInstruction(); mpCaptureProvider(true);
        mpCaptureDecisions();
        for (const [id, instruction] of Object.entries(mpSettingsDraft.instructions.custom)) {
            if (!instruction.deleted && (!instruction.title?.trim() || !instruction.content?.trim())) {
                mpEditingInstruction = id; mpSelectSettingsTab('Instructions'); renderInstructionList(); mpRenderInstruction();
                throw new Error('Each custom instruction needs a name and system prompt.');
            }
        }
        mpCloseModelPickers(); mpCloseSwarmSettingsDropdown();
        mpSettingsBusy = true;
        document.getElementById('mpSaveSettings').disabled = true;
        document.querySelector('#settingsModal .modal-body').inert = true;
        document.querySelector('#settingsModal .mp-settings-tabs').inert = true;
        mpSettingsStatus('Saving…');
        const payload = { ...mpSettingsDraft };
        for (const key of Object.keys(mpPromptPreferencesAtOpen)) {
            const edited = MP_BACKENDS.filter(provider =>
                JSON.stringify(mpSettingsDraft[key][provider]) !== JSON.stringify(mpPromptPreferencesAtOpen[key][provider]));
            delete payload[key];
            if (edited.length) payload[key] = Object.fromEntries(edited.map(provider => [provider, mpSettingsDraft[key][provider]]));
        }
        const response = await mpPersistSettings(payload);
        const preserveSelection = mpCatalogKey(MP.settings) === mpCatalogKey(mpSettingsDraft);
        MP.settings = mpNormalizeSettings(response.settings);
        window.mpRefreshInstructionPrefixes?.(); updateModelListOnLeft(preserveSelection);
        mpSettingsBusy = false; closeSettingsModal();
    }
    catch (error) { mpSettingsStatus(`Could not save: ${error.message}`, true); }
    finally {
        mpSettingsBusy = false;
        document.getElementById('mpSaveSettings').disabled = false;
        document.querySelector('#settingsModal .modal-body').inert = false;
        document.querySelector('#settingsModal .mp-settings-tabs').inert = false;
    }
}

async function importCustomInstructions(file) {
    if (!file) return;
    const data = JSON.parse(await file.text());
    if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('Expected an instruction JSON object.');
    const entries = typeof data.title === 'string' ? [data] : Object.values(data);
    let count = 0;
    mpCaptureInstruction();
    for (const instruction of entries) {
        if (!instruction || typeof instruction.title !== 'string' || typeof instruction.content !== 'string' || !instruction.title.trim() || !instruction.content.trim()) continue;
        const id = `custom-${Date.now()}-${count++}-${Math.random().toString(36).slice(2, 8)}`;
        mpSettingsDraft.instructions.custom[id] = { id, title: instruction.title, content: instruction.content, tooltip: typeof instruction.tooltip === 'string' ? instruction.tooltip : '', categories: ['prompt'], created: new Date().toISOString() };
    }
    if (!count) throw new Error('No valid instructions found.');
    renderInstructionList(); mpSettingsStatus(`Imported ${count} instructions. Save to keep them.`);
}
function exportInstructions(single) {
    mpCaptureInstruction();
    const entries = mpInstructionEntries().filter(([id]) => single ? id === mpEditingInstruction : !MP_BUILTINS[id]);
    const data = Object.fromEntries(entries);
    const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'magicprompt-instructions.json';
    anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
async function generateInstructionWithAI() {
    const brief = document.getElementById('mpInstructionBrief').value.trim();
    if (!brief) { mpSettingsStatus('Describe the instruction first.', true); return; }
    if (!MP.settings.model) { mpSettingsStatus('Save a default model before generating instructions.', true); return; }
    const button = document.getElementById('mpGenerateInstruction');
    const editing = mpEditingInstruction;
    const draft = mpSettingsDraft;
    button.disabled = true; mpSettingsStatus('Generating instruction…');
    try {
        const response = await MP.APIClient.makeRequest({ modelId: MP.settings.model, action: 'generate-instruction', messageContent: { text: brief, instructions: getInstructionContent('instructiongen') } });
        if (!response.response?.trim()) throw new Error('The model returned an empty instruction.');
        if (draft !== mpSettingsDraft) return;
        if (editing === mpEditingInstruction) document.getElementById('mpInstructionContent').value = response.response;
        else if (MP_BUILTINS[editing]) draft.instructions[editing] = response.response;
        else if (draft.instructions.custom[editing] && !draft.instructions.custom[editing].deleted) draft.instructions.custom[editing].content = response.response;
        mpSettingsStatus('Draft generated. Review the system prompt, then save.');
    }
    catch (error) { mpSettingsStatus(`Generation failed: ${error.message}`, true); }
    finally { button.disabled = false; }
}
