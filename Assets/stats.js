'use strict';

const MP_STATS_PROVIDERS = { openrouter: 'OpenRouter', openaiapi: 'Local OpenAI API' };
const MP_STATS_GROUPS = ['Starred', 'Unstarred', 'Blocked'];
const MP_STATS_SORTS = {
    newest: { label: 'Newest model added' },
    skipHigh: { label: 'All-time Skip If, high to low', metric: 'allTime', direction: -1 },
    skipLow: { label: 'All-time Skip If, low to high', metric: 'allTime', direction: 1 },
    recentHigh: { label: 'Recent Skip If, high to low', metric: 'recent', direction: -1 },
    recentLow: { label: 'Recent Skip If, low to high', metric: 'recent', direction: 1 }
};

function mpStatsRate(matches, total) { return total > 0 ? matches / total : null; }

function mpStatsPercentage(rate) {
    if (rate === null) return '—';
    const rounded = Math.round(rate * 1000) / 10;
    if (rounded === 0 && rate > 0) return '<0.1%';
    if (rounded === 100 && rate < 1) return '>99.9%';
    return `${rounded}%`;
}

function mpStatsElement(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
}

function mpStatsRateDisplay(label, matches, total, metric, period = '') {
    const rate = mpStatsRate(matches, total);
    const percentage = mpStatsPercentage(rate);
    const summary = rate === null
        ? metric === 'Error' ? 'Error rate unavailable. No completed or failed requests.' : `${label} Skip If rate unavailable. No eligible outcomes recorded.`
        : `${metric === 'Error' ? 'Failed' : 'Skip If matched'} ${matches} of ${total} ${metric === 'Error' ? 'requests' : 'eligible responses'} (${percentage})${period}.`;
    const line = mpStatsElement('div', 'mp-stats-rate');
    if (label) line.append(mpStatsElement('span', 'mp-stats-period', label));
    const track = mpStatsElement('span', 'mp-stats-track');
    track.setAttribute('aria-hidden', 'true');
    track.hidden = rate === null;
    if (rate !== null) {
        const fill = mpStatsElement('span', 'mp-stats-fill');
        fill.style.width = `${rate * 100}%`;
        track.append(fill);
    }
    const value = mpStatsElement('span', 'mp-stats-value', percentage);
    value.setAttribute('aria-hidden', 'true');
    line.title = summary;
    line.append(track, value, mpStatsElement('span', 'visually-hidden', summary));
    return line;
}

function mpStatsSortRows(models, mode) {
    const catalogs = new Map();
    const groups = new Map();
    for (const provider of MP_BACKENDS) {
        if (!models.some(model => model.provider === provider)) continue;
        const rows = mpCatalog({ ...MP.settings, backend: provider }).slice().sort((a, b) => mpCompareModelDates(a, b, provider));
        catalogs.set(provider, new Map(rows.map((row, index) => [row.model, { row, index }])));
        for (const [preference, group] of [['favoritedModels', 0], ['blockedModels', 2]]) {
            for (const id of MP.settings[preference]?.[provider] || []) groups.set(`${provider}\n${mpModelIdKey(id)}`, group);
        }
    }
    const rows = models.map(model => {
        const catalog = catalogs.get(model.provider)?.get(model.model);
        return {
            ...model, name: catalog?.row.name || model.model,
            key: JSON.stringify([model.provider, model.model]),
            group: groups.get(`${model.provider}\n${mpModelIdKey(model.model)}`) ?? 1,
            rank: catalog?.index ?? 0,
            catalogGroup: !catalog ? 3 : model.provider === 'openaiapi' ? 1 : Number(catalog.row.created) > 0 ? 0 : 2,
            allTime: mpStatsRate(model.skipped, model.skip_completed),
            recent: mpStatsRate(model.recent_skipped, model.recent_skip_completed)
        };
    });
    const { metric, direction } = MP_STATS_SORTS[mode];
    return rows.sort((a, b) => {
        if (a.group !== b.group) return a.group - b.group;
        if (metric && a[metric] !== b[metric]) {
            if (a[metric] === null) return 1;
            if (b[metric] === null) return -1;
            return direction * (a[metric] - b[metric]);
        }
        return a.catalogGroup - b.catalogGroup || a.rank - b.rank
            || a.name.localeCompare(b.name) || a.provider.localeCompare(b.provider)
            || (a.model < b.model ? -1 : Number(a.model > b.model));
    });
}

function mpStatsButton(action, text, label = text) {
    const button = mpStatsElement('button', 'basic-button', text);
    button.type = 'button';
    button.dataset.action = action;
    if (label !== text) button.setAttribute('aria-label', label);
    return button;
}

function mpStatsSelectableModels(select) {
    const selectable = new Set();
    if (!MP.settingsLoaded || !select) return selectable;
    // Native options can still belong to the previous provider while the catalog loads.
    const catalogIds = new Set(mpCatalog(MP.settings).map(row => row.model));
    for (const option of select.options) {
        if (option.value && option.value !== 'loading' && !option.disabled && !option.hidden
            && option.dataset.blocked !== '1' && option.dataset.unavailable !== '1' && catalogIds.has(option.value)) {
            selectable.add(option.value);
        }
    }
    return selectable;
}

function mpStatsTableRow(model, confirm, busy, canUse) {
    const row = document.createElement('tr');
    row.dataset.key = model.key;
    row.setAttribute('role', 'row');
    const heading = document.createElement('th');
    heading.scope = 'row';
    heading.setAttribute('role', 'rowheader');
    const provider = Object.hasOwn(MP_STATS_PROVIDERS, model.provider) ? MP_STATS_PROVIDERS[model.provider] : model.provider;
    const identity = model.name === model.model ? provider : `${provider} · ${model.model}`;
    heading.append(mpStatsElement('div', 'mp-stats-model', model.name), mpStatsElement('div', 'mp-stats-provider', identity));

    const controls = mpStatsElement('div', 'mp-stats-actions');
    const name = `${model.name} (${model.provider}, ${model.model})`;
    const canEdit = MP.settingsLoaded && MP_BACKENDS.includes(model.provider);
    const blocked = model.group === 2;
    if (canEdit && !blocked) {
        const starred = model.group === 0;
        const star = starred ? 'Unstar' : 'Star';
        const starButton = mpStatsButton('star', star, `${star} ${name}`);
        const icon = mpStatsElement('span', starred ? 'mp-model-dropdown-star-active' : '', starred ? '★' : '☆');
        icon.setAttribute('aria-hidden', 'true');
        starButton.prepend(icon, ' ');
        controls.append(starButton);
    }
    if (canUse) controls.append(mpStatsButton('use', 'Use', `Use ${name}`));
    if (canEdit) {
        const block = blocked ? 'Unblock' : 'Block';
        controls.append(mpStatsButton('block', block, `${block} ${name}`));
    }
    if (MP_BACKENDS.includes(model.provider) && (typeof permissions === 'undefined' || permissions.hasPermission('magicprompt_reset_stats'))) {
        controls.append(mpStatsButton('reset', 'Reset', `Reset stats for ${name}`));
    }
    if (controls.childElementCount) heading.append(controls);
    if (confirm) {
        const inline = mpStatsElement('div', 'mp-stats-confirm');
        const message = mpStatsElement('span', '', `Reset stats for ${model.name}?`);
        message.id = 'mpStatsConfirmMessage';
        const buttons = mpStatsElement('div', 'mp-stats-actions');
        const reset = mpStatsButton('confirm', 'Reset');
        const cancel = mpStatsButton('cancel', 'Cancel');
        reset.classList.add('mp-danger');
        reset.setAttribute('aria-describedby', message.id);
        cancel.setAttribute('aria-describedby', message.id);
        buttons.append(reset, cancel);
        inline.append(message, buttons);
        heading.append(inline);
    }
    if (busy) for (const control of heading.querySelectorAll('button')) control.setAttribute('aria-disabled', 'true');
    row.append(heading);

    const latency = model.latency_ms === null ? '—' : `${Math.round(model.latency_ms / 1000)} s`;
    const skipRates = mpStatsElement('div', 'mp-stats-skip');
    skipRates.append(
        mpStatsRateDisplay('All time', model.skipped, model.skip_completed, 'Skip If', ' all time'),
        mpStatsRateDisplay(model.recent_skip_completed ? `Last ${model.recent_skip_completed}` : 'Recent',
            model.recent_skipped, model.recent_skip_completed, 'Skip If', ` in the last ${model.recent_skip_completed}`)
    );
    for (const [label, content] of [
        ['Latency', mpStatsElement('span', 'mp-stats-value', latency)],
        ['Error Rate', mpStatsRateDisplay('', model.errors, model.completed + model.errors, 'Error')],
        ['Skip If Rate', skipRates],
        ['Completed', mpStatsElement('span', 'mp-stats-value', model.completed.toLocaleString())]
    ]) {
        const cell = row.insertCell();
        cell.setAttribute('role', 'cell');
        const caption = mpStatsElement('span', 'mp-stats-caption', label);
        caption.setAttribute('aria-hidden', 'true');
        cell.append(caption, content);
    }
    return row;
}

function mpShowStats(opener = document.activeElement) {
    let modal = document.getElementById('mpStats');
    if (!modal) modal = mpCreateStats();
    modal.mpOpener = opener;
    (bootstrap.Modal.getInstance(modal) || new bootstrap.Modal(modal)).show();
}

function mpCreateStats() {
    document.body.insertAdjacentHTML('beforeend', `
    <div class="modal fade mp-settings mp-stats" id="mpStats" tabindex="-1" aria-labelledby="mpStatsTitle" aria-hidden="true">
      <div class="modal-dialog modal-dialog-centered"><div class="modal-content">
        <div class="modal-header"><h5 class="modal-title" id="mpStatsTitle">Stats</h5><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close stats"></button></div>
        <div class="modal-body">
          <div class="mp-stats-tools"><label for="mpStatsSort">Sort</label><select id="mpStatsSort" class="form-select"></select></div>
          <table aria-label="Model stats" role="table" hidden>
            <thead role="rowgroup"><tr role="row"><th scope="col" role="columnheader">Model</th><th scope="col" role="columnheader">Latency</th><th scope="col" role="columnheader">Error Rate</th><th scope="col" role="columnheader">Skip If Rate</th><th scope="col" role="columnheader" aria-label="Completed requests" title="Completed requests">#</th></tr></thead>
          </table>
        </div>
        <div class="modal-footer"><p class="mp-stats-status" role="status"></p><button type="button" class="basic-button" id="mpStatsRefresh">Refresh</button><button type="button" class="basic-button" data-bs-dismiss="modal">Close</button></div>
      </div></div>
    </div>`);
    const modal = document.getElementById('mpStats');
    const sort = modal.querySelector('#mpStatsSort');
    sort.replaceChildren(...Object.entries(MP_STATS_SORTS).map(([value, { label }]) => new Option(label, value)));
    const table = modal.querySelector('table');
    const status = modal.querySelector('[role="status"]');
    const refreshButton = modal.querySelector('#mpStatsRefresh');
    let models = [];
    let rows = new Map();
    let requestId = 0;
    let loading = false;
    let loaded = false;
    let busy = '';
    let confirmation = null;
    let readError = '';
    let actionError = '';
    let shown = false;
    let usedModel = false;

    function renderStatus() {
        status.classList.toggle('mp-status-error', !!(actionError || readError));
        status.textContent = actionError || readError || (busy === 'reset' ? 'Resetting stats…'
            : busy === 'preference' ? 'Saving model preferences…'
                : loading ? 'Loading stats…' : loaded && !models.length ? 'No model stats yet.' : '');
        refreshButton.setAttribute('aria-disabled', String(busy === 'reset'));
    }

    function render(focusAction) {
        const active = document.activeElement;
        const focusedRow = table.contains(active) ? active.closest('tr[data-key]') : null;
        const sorted = mpStatsSortRows(models, sort.value);
        rows = new Map(sorted.map(model => [model.key, model]));
        if (!rows.has(confirmation)) confirmation = null;
        const selectable = mpStatsSelectableModels(document.getElementById('input_mpmodelid'));
        const grouped = MP_STATS_GROUPS.map(() => []);
        for (const model of sorted) grouped[model.group].push(model);
        const sections = [];
        const elements = new Map();
        for (const [group, members] of grouped.entries()) {
            if (!members.length) continue;
            const section = mpStatsElement('tbody');
            section.setAttribute('role', 'rowgroup');
            const divider = mpStatsElement('tr', 'mp-stats-group-heading');
            divider.setAttribute('role', 'row');
            const heading = mpStatsElement('th', '', MP_STATS_GROUPS[group]);
            heading.scope = 'rowgroup';
            heading.colSpan = table.tHead.rows[0].cells.length;
            heading.setAttribute('role', 'rowheader');
            divider.append(heading);
            section.append(divider);
            for (const model of members) {
                const canUse = group !== 2 && model.provider === MP.settings.backend && selectable.has(model.model);
                const row = mpStatsTableRow(model, model.key === confirmation, !!busy, canUse);
                section.append(row);
                elements.set(model.key, row);
            }
            sections.push(section);
        }
        table.replaceChildren(table.tHead, ...sections);
        table.hidden = !sorted.length;
        if (focusedRow) {
            const target = elements.get(focusedRow.dataset.key)
                ?.querySelector(`button[data-action="${focusAction || active.dataset.action}"]`);
            (target || refreshButton).focus();
        }
        renderStatus();
    }

    async function refresh() {
        if (busy === 'reset') return;
        const id = ++requestId;
        const requestedCatalogs = new Set();
        function enrich() {
            for (const provider of new Set(models.map(model => model.provider))) {
                if (!MP_BACKENDS.includes(provider)) continue;
                const settings = { ...MP.settings, backend: provider };
                const key = mpCatalogKey(settings);
                if (!requestedCatalogs.has(key)) {
                    requestedCatalogs.add(key);
                    fetchModels(settings, false).then(() => render(), () => {});
                }
            }
        }
        loadSettings().then(() => {
            // Repaint preference controls even if stats or catalogs loaded first.
            mpRefreshPromptModelControls();
            render();
            if (id === requestId) enrich();
        }, () => {});
        loading = true;
        readError = actionError = '';
        renderStatus();
        try {
            const data = await mpRequest('GetMagicPromptStats');
            if (id !== requestId) return;
            models = data.models;
            loaded = true;
            enrich();
        }
        catch (error) {
            if (id === requestId) readError = `Could not load stats: ${error.message}`;
        }
        finally {
            if (id === requestId) {
                loading = false;
                render();
            }
        }
    }

    table.addEventListener('click', async event => {
        const button = event.target.closest('button[data-action]');
        if (!button || busy) return;
        const model = rows.get(button.closest('tr').dataset.key);
        const action = button.dataset.action;
        actionError = '';
        if (action === 'use') {
            const select = document.getElementById('input_mpmodelid');
            if (model.group === 2 || model.provider !== MP.settings.backend
                || !mpStatsSelectableModels(select).has(model.model)) {
                render();
                return;
            }
            usedModel = true;
            select.value = model.model;
            triggerChangeFor(select);
            if (shown) bootstrap.Modal.getInstance(modal).hide();
            return;
        }
        if (action === 'reset' || action === 'cancel') {
            confirmation = action === 'reset' ? model.key : null;
            render(action === 'reset' ? 'cancel' : 'reset');
            return;
        }

        const reset = action === 'confirm';
        busy = reset ? 'reset' : 'preference';
        if (reset) {
            // Old reads may have captured counters before reset.
            ++requestId;
            loading = false;
            readError = '';
        }
        render();
        let focusAction;
        try {
            if (reset) {
                const data = await mpRequest('ResetMagicPromptStats', { provider: model.provider, model: model.model });
                models = data.models;
                confirmation = null;
                if (document.activeElement?.closest('tr')?.dataset.key === model.key
                    && ['confirm', 'cancel'].includes(document.activeElement.dataset.action)) focusAction = 'reset';
            }
            else {
                const star = action === 'star';
                await mpSavePromptModelPreference(star ? 'favoritedModels' : 'blockedModels',
                    model.provider, model.model, model.group !== (star ? 0 : 2));
            }
        }
        catch (error) {
            actionError = `Could not ${reset ? 'reset stats' : 'save model preferences'}: ${error.message}`;
        }
        finally {
            busy = '';
            render(focusAction);
        }
    });
    sort.addEventListener('change', () => render());
    refreshButton.addEventListener('click', refresh);
    modal.addEventListener('show.bs.modal', refresh);
    modal.addEventListener('shown.bs.modal', () => {
        shown = true;
        // Bootstrap ignores hide() during its opening transition.
        if (usedModel) bootstrap.Modal.getInstance(modal).hide();
        else sort.focus();
    });
    modal.addEventListener('hidden.bs.modal', () => {
        shown = false;
        if (confirmation && busy !== 'reset') {
            confirmation = null;
            render();
        }
        if (usedModel) {
            usedModel = false;
            const trigger = document.getElementById('mp_model_dropdown_trigger');
            trigger?.focus();
            if (trigger === document.activeElement) return;
        }
        modal.mpOpener?.focus();
    });
    return modal;
}
