/** Prompt settings: one modal, draft edits, native SwarmUI searchable selects. */
'use strict';

const MP_BACKENDS = ['openrouter', 'openaiapi'];
const MP_BUILTINS = { prompt: 'Default prompt', randomprompt: 'Random prompt', instructiongen: 'Instruction generator' };
let mpSettingsDraft = null;
let mpEditingInstruction = 'prompt';
let mpSettingsBusy = false;
let mpSettingsOpening = false;
let mpSettingsCloseQueued = false;
let mpCatalogRequest = 0;
let mpSaveQueue = Promise.resolve();

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
    for (const key of ['blockedModels', 'favoritedModels']) {
        result[key] ||= {};
        for (const backend of MP_BACKENDS) {
            result[key][backend] = [...new Set((result[key][backend] || []).filter(id => typeof id === 'string' && id.trim()))];
        }
    }
    for (const backend of MP_BACKENDS) {
        result.favoritedModels[backend] = result.favoritedModels[backend].filter(id => !result.blockedModels[backend].includes(id));
    }
    return result;
}

async function loadSettings() {
    MP.settings = mpNormalizeSettings((await mpRequest('GetMagicPromptSettings')).settings);
    MP.settingsLoaded = true;
    return MP.settings;
}

/** Serialize writes so rapid preference edits cannot arrive out of order. */
function mpPersistSettings(settings) {
    const snapshot = JSON.parse(JSON.stringify(settings));
    const pending = mpSaveQueue.catch(() => {}).then(() => mpRequest('SaveMagicPromptSettings', { settings: snapshot }));
    mpSaveQueue = pending;
    return pending;
}

function mpSettingsStatus(message, error = false) {
    const status = document.getElementById('mpSettingsStatus');
    if (status) {
        status.textContent = message;
        status.classList.toggle('mp-status-error', error);
    }
}

function mpCurrentSettings() { return mpSettingsDraft || MP.settings; }
function mpModelSelectOptionText(row) { return `${row.name || row.model}${row.costOut ? ` · ${row.costOut} / 1M out` : ''}`; }
function mpCatalogKey(settings) { return `${settings.backend}\n${settings.backends[settings.backend]?.baseurl || ''}`; }
function mpCatalog(settings = mpCurrentSettings()) { return MP.modelCatalogs?.[mpCatalogKey(settings)] || []; }

async function fetchModels(settings = mpCurrentSettings()) {
    const key = mpCatalogKey(settings);
    const request = ++mpCatalogRequest;
    mpSettingsStatus('Loading models…');
    try {
        const response = await mpRequest('GetMagicPromptModels', {
            backend: settings.backend, baseurl: settings.backends[settings.backend]?.baseurl || ''
        });
        MP.modelCatalogs ||= {};
        MP.modelCatalogs[key] = (response.models || []).filter(row => row && row.model);
        if (request === mpCatalogRequest && key === mpCatalogKey(mpCurrentSettings())) {
            renderModelSettings();
            mpSettingsStatus(`${response.models.length} models available.`);
        }
        if (key === mpCatalogKey(MP.settings)) updateModelListOnLeft(true);
        return response;
    }
    catch (error) {
        if (request === mpCatalogRequest) mpSettingsStatus(`Could not load models: ${error.message}`, true);
        throw error;
    }
}

/** Preserve explicit generation selections, even if a model was just blocked. */
function updateModelListOnLeft(preserveSelection = true) {
    const select = document.getElementById('input_mpmodelid');
    const instructions = document.getElementById('input_mpinstructions');
    if (select && mpCatalog(MP.settings).length) {
        mpFillModelSelect(select, MP.settings, preserveSelection && select.value && select.value !== 'loading' ? select.value : MP.settings.model, preserveSelection);
        triggerChangeFor(select);
    }
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
}

/** OpenRouter catalog dates, newest first; stable ties preserve provider order. */
function mpCompareModelDates(a, b, backend) {
    const created = row => Number.isFinite(Number(row.created)) ? Number(row.created) : 0;
    return backend === 'openrouter' ? created(b) - created(a) : 0;
}

function mpFillModelSelect(select, settings, selected, preserveBlockedSelection = false) {
    const backend = settings.backend;
    const blocked = new Set(settings.blockedModels[backend]);
    const favorites = new Set(settings.favoritedModels[backend]);
    if (blocked.has(selected) && !preserveBlockedSelection) selected = '';
    // OpenRouter's creation timestamp is the date the model was added to its catalog.
    // Keep favorites pinned, newest first within each group. Stable sorting retains
    // provider order for equal/missing dates and for local catalogs.
    const rows = mpCatalog(settings).slice().sort((a, b) =>
        Number(favorites.has(b.model)) - Number(favorites.has(a.model)) ||
        mpCompareModelDates(a, b, backend));
    select.replaceChildren(new Option('Select a model', ''));
    for (const row of rows) {
        if (blocked.has(row.model) && row.model !== selected) continue;
        const option = new Option(mpModelSelectOptionText(row), row.model);
        if (blocked.has(row.model)) option.dataset.blocked = '1';
        if (favorites.has(row.model)) option.dataset.favorited = '1';
        if (select.id === 'modelSelect') {
            const label = escapeHtmlNoBr(option.text);
            const favorite = favorites.has(row.model);
            option.dataset.cleanname = `<span class="mp-model-dropdown-row${favorite ? ' mp-model-dropdown-row-favorited' : ''}"><span class="mp-model-dropdown-row-label">${label}</span><span class="mp-model-dropdown-favorite-marker${favorite ? ' mp-model-dropdown-star-active' : ''}" title="${favorite ? 'Favorited model' : 'Not favorited'}" aria-label="${favorite ? 'Favorited model' : 'Not favorited'}">${favorite ? '★' : '☆'}</span></span>`;
        }
        select.add(option);
    }
    // A saved ID may be temporarily absent from a provider's catalog.
    if (selected && selected !== 'loading' && ![...select.options].some(option => option.value === selected)) {
        select.add(new Option(`${selected} (unavailable)`, selected));
    }
    select.value = selected === 'loading' ? '' : selected || '';
}

function isModelFavoritedForBackend(backend, id) { return MP.settings.favoritedModels?.[backend]?.includes(id) || false; }
function mpChangeModelPreference(key, backend, id, add) {
    const list = MP.settings[key][backend];
    MP.settings[key][backend] = list.filter(value => value !== id);
    if (add) {
        MP.settings[key][backend].push(id);
        const other = key === 'blockedModels' ? 'favoritedModels' : 'blockedModels';
        MP.settings[other][backend] = MP.settings[other][backend].filter(value => value !== id);
    }
    updateModelListOnLeft(true);
    mpPersistSettings({ blockedModels: MP.settings.blockedModels, favoritedModels: MP.settings.favoritedModels })
        .catch(error => showError(`Could not save model preferences: ${error.message}`));
}
Object.assign(MP, {
    isModelFavoritedForBackend,
    blockModelId: (backend, id) => mpChangeModelPreference('blockedModels', backend, id, true),
    unblockModelId: (backend, id) => mpChangeModelPreference('blockedModels', backend, id, false),
    favoriteModelId: (backend, id) => mpChangeModelPreference('favoritedModels', backend, id, true),
    unfavoriteModelId: (backend, id) => mpChangeModelPreference('favoritedModels', backend, id, false)
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
        </div>
        <div class="modal-footer"><span id="mpSettingsStatus" role="status" aria-live="polite"></span><button type="button" class="basic-button" data-bs-dismiss="modal">Cancel</button><button type="button" class="basic-button mp-primary" id="mpSaveSettings">Save and close</button></div>
      </div></div>
    </div>`);
    const modal = document.getElementById('settingsModal');
    new ResizeObserver(([entry]) => {
        const width = entry.target.getBoundingClientRect().width;
        if (width) modal.style.setProperty('--mp-model-menu-width', `${width}px`);
    }).observe(document.getElementById('modelSelect'));
    modal.addEventListener('hide.bs.modal', event => { if (mpSettingsBusy) event.preventDefault(); });
    modal.addEventListener('hidden.bs.modal', () => { mpSettingsDraft = null; });
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
            index = event.key === 'Home' ? 0 : event.key === 'End' ? 2 : (index + (event.key === 'ArrowRight' ? 1 : 2)) % 3;
            tabs[index].click(); tabs[index].focus();
        });
    });
    document.getElementById('mpBackend').addEventListener('change', event => {
        mpCaptureProvider(false);
        mpSettingsDraft.backend = event.target.value;
        mpSettingsDraft.model = '';
        mpRenderProvider(); renderModelSettings();
        fetchModels().catch(() => {});
    });
    document.getElementById('mpBaseUrl').addEventListener('change', () => { mpCaptureProvider(); renderModelSettings(); });
    document.getElementById('mpRefreshModels').addEventListener('click', () => { mpCaptureProvider(); fetchModels().catch(() => {}); });
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
    for (const [id, key] of [['mpFavoriteModels', 'favoritedModels'], ['mpBlockedModels', 'blockedModels']]) {
        const select = document.getElementById(id);
        mpModelPickers.push(new MPModelPreferencePicker(select, modal));
        select.addEventListener('change', () => {
            if (!mpSettingsDraft) return;
            const backend = mpSettingsDraft.backend;
            const values = [...select.selectedOptions].map(option => option.value);
            mpSettingsDraft[key][backend] = values;
            const other = key === 'blockedModels' ? 'favoritedModels' : 'blockedModels';
            mpSettingsDraft[other][backend] = mpSettingsDraft[other][backend].filter(id => !values.includes(id));
            renderModelSettings();
        });
    }
    document.getElementById('modelSelect').addEventListener('change', () => {
        if (mpSettingsDraft) mpSettingsDraft.model = document.getElementById('modelSelect').value;
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
    mpFillModelSelect(document.getElementById('modelSelect'), settings, settings.model);
    if (mpSettingsDraft) mpSettingsDraft.model = document.getElementById('modelSelect').value;
    for (const [id, key] of [['mpFavoriteModels', 'favoritedModels'], ['mpBlockedModels', 'blockedModels']]) {
        const select = document.getElementById(id);
        const chosen = settings[key][settings.backend];
        const rows = new Map(mpCatalog(settings).map(row => [row.model, row]));
        for (const model of chosen) if (!rows.has(model)) rows.set(model, { model });
        select.replaceChildren();
        for (const row of [...rows.values()].sort((a, b) => mpCompareModelDates(a, b, settings.backend))) {
            if (key === 'favoritedModels' && settings.blockedModels[settings.backend].includes(row.model)) continue;
            const option = new Option(`${mpModelSelectOptionText(row)}${row.name && row.name !== row.model ? ` — ${row.model}` : ''}`, row.model, false, chosen.includes(row.model));
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
    mpEditingInstruction = 'prompt';
    document.getElementById('mpInstructionSearch').value = '';
    mpRenderProvider(); renderModelSettings(); renderInstructionList(); mpRenderInstruction();
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
        const response = await mpPersistSettings(mpSettingsDraft);
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
