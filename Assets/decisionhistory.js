'use strict';

function mpDecisionAnswer(record) {
    if (record.status !== 'ok') return record.message || `Error: ${record.error || 'Decision unavailable'}`;
    const skipped = record.generation_skipped === true ? ' · Generation skipped' : '';
    if (typeof record.answer === 'boolean') {
        const yes = Array.isArray(record.probabilities) ? record.probabilities[1] : record.probabilities?.true;
        return `${record.answer ? 'Yes' : 'No'}${Number.isFinite(yes) ? ` (${Math.round(yes * 100)}% probability of Yes)` : ''}${skipped}`;
    }
    if (record.decision?.type === 'score' && typeof record.answer === 'number') {
        return `${record.answer.toFixed(2)} / ${(record.decision.options?.length || 1) - 1}`;
    }
    return String(record.answer ?? 'Unavailable') + skipped;
}

function mpShowDecisionHistory(opener = document.activeElement) {
    let modal = document.getElementById('mpDecisionHistory');
    if (!modal) {
        document.body.insertAdjacentHTML('beforeend', `
        <div class="modal fade mp-settings" id="mpDecisionHistory" tabindex="-1" aria-labelledby="mpDecisionHistoryTitle" aria-hidden="true">
          <div class="modal-dialog modal-dialog-centered"><div class="modal-content">
            <div class="modal-header"><h5 class="modal-title" id="mpDecisionHistoryTitle">Decision History</h5><button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close history"></button></div>
            <div class="modal-body">
              <div class="mp-decision-filters">
                <label class="mp-field">Period<select id="mpDecisionDays" class="form-select"><option value="7">7 days</option><option value="30" selected>30 days</option><option value="90">90 days</option></select></label>
                <label class="mp-field">Ending on (UTC)<input type="date" id="mpDecisionEnd" class="form-control"></label>
                <label class="mp-field">Question and evaluator<select id="mpDecisionSeries" class="form-select" disabled></select></label>
              </div>
              <p id="mpDecisionHistoryStatus" role="status" aria-live="polite" tabindex="-1"></p>
              <div id="mpDecisionHistoryResults" hidden>
                <p id="mpDecisionDefinition" class="mp-muted"></p>
                <h6>Compare models</h6>
                <p class="mp-muted">Rates use completed checks. For a fair comparison, run the same prompts across models.</p>
                <div class="mp-decision-table"><table aria-label="Model comparison"><thead><tr><th scope="col">Model</th><th scope="col" id="mpDecisionMetric">Result</th><th scope="col">Completed</th><th scope="col">Errors</th></tr></thead><tbody id="mpDecisionModels"></tbody></table></div>
                <h6>Individual checks</h6>
                <div class="mp-decision-table"><table aria-label="Individual decision checks"><thead><tr><th scope="col">Recorded</th><th scope="col">Model</th><th scope="col">Answer</th></tr></thead><tbody id="mpDecisionRows"></tbody></table></div>
                <div class="mp-toolbar mp-decision-pages"><button type="button" class="basic-button" id="mpDecisionPrev">Previous</button><span id="mpDecisionCount" aria-live="polite"></span><button type="button" class="basic-button" id="mpDecisionNext">Next</button></div>
              </div>
            </div>
            <div class="modal-footer"><button type="button" class="basic-button" id="mpDecisionRefresh">Refresh</button><button type="button" class="basic-button" data-bs-dismiss="modal">Close</button></div>
          </div></div>
        </div>`);
        modal = document.getElementById('mpDecisionHistory');
        const get = id => modal.querySelector(`#${id}`);
        const days = get('mpDecisionDays');
        const date = get('mpDecisionEnd');
        const series = get('mpDecisionSeries');
        const status = get('mpDecisionHistoryStatus');
        const results = get('mpDecisionHistoryResults');
        const previous = get('mpDecisionPrev');
        const next = get('mpDecisionNext');
        date.value = new Date().toISOString().slice(0, 10);
        let requestId = 0;
        let offset = 0;
        let pageSize = 25;
        let total = 0;
        let loading = false;

        async function refresh(seriesId = series.value || '', requestedOffset = 0) {
            const id = ++requestId;
            status.textContent = 'Loading decision history…';
            status.classList.remove('mp-status-error');
            loading = true;
            try {
                const data = await mpRequest('GetMagicPromptDecisionHistory', {
                    endDate: date.value, days: Number(days.value), seriesId, offset: requestedOffset
                });
                if (id !== requestId) return;
                series.replaceChildren(...data.series.map(row => new Option(`${row.title} · ${row.evaluator.split('/').pop()} · ${row.version.slice(0, 8)}`, row.id)));
                series.value = data.selected || '';
                series.disabled = !data.series.length;
                const definition = data.series.find(row => row.id === data.selected);
                results.hidden = !definition;
                status.textContent = definition ? '' : 'No checks in this period. Enable MP Decision Checks and generate a fresh remote response.';
                if (!definition) return;

                get('mpDecisionDefinition').textContent = definition.question + (definition.type === 'score' ? ` · Rating scale: ${definition.options.map((label, index) => `${index}: ${label}`).join(' · ')}` : '');
                get('mpDecisionMetric').textContent = { noul: 'Yes rate', choice: 'Answers', score: 'Mean score' }[definition.type];
                const yesRate = model => model.completed ? model.yes / model.completed : -1;
                const modelRows = definition.type === 'noul' ? data.models.slice().sort((a, b) => yesRate(b) - yesRate(a)) : data.models;
                mpDecisionTableRows(get('mpDecisionModels'), modelRows.map(model => {
                    let metric = '—';
                    if (model.completed > 0) {
                        if (definition.type === 'noul') metric = `${(yesRate(model) * 100).toFixed(1)}% (${model.yes}/${model.completed})`;
                        if (definition.type === 'score') metric = `${model.mean_score.toFixed(2)} / ${definition.options.length - 1}`;
                        if (definition.type === 'choice') metric = definition.options.map(option => `${option}: ${Object.hasOwn(model.choices, option) ? model.choices[option] : 0}`).join(' · ');
                    }
                    return [`${model.model} · ${model.provider}`, metric, model.completed, model.errors];
                }));
                mpDecisionTableRows(get('mpDecisionRows'), data.rows.map(row => [
                    new Date(row.time_utc).toLocaleString(), `${row.source.model} · ${row.source.provider}`, mpDecisionAnswer(row)
                ]));
                offset = requestedOffset;
                pageSize = data.page_size;
                total = data.total;
                previous.setAttribute('aria-disabled', String(offset === 0));
                next.setAttribute('aria-disabled', String(offset + pageSize >= total));
                get('mpDecisionCount').textContent = `${offset + 1}–${offset + data.rows.length} of ${data.total}`;
            }
            catch (error) {
                if (id !== requestId) return;
                status.classList.add('mp-status-error');
                status.textContent = `Could not load decision history: ${error.message}`;
                if (results.contains(document.activeElement)) status.focus();
                results.hidden = true;
            }
            finally { if (id === requestId) loading = false; }
        }

        days.addEventListener('change', () => refresh(''));
        date.addEventListener('change', () => refresh(''));
        series.addEventListener('change', () => refresh());
        previous.addEventListener('click', () => { if (!loading && offset > 0) refresh(series.value, Math.max(0, offset - pageSize)); });
        next.addEventListener('click', () => { if (!loading && offset + pageSize < total) refresh(series.value, offset + pageSize); });
        get('mpDecisionRefresh').addEventListener('click', () => refresh());
        modal.addEventListener('show.bs.modal', () => refresh());
        modal.addEventListener('shown.bs.modal', () => modal.focus());
        modal.addEventListener('hidden.bs.modal', () => modal.mpOpener?.focus());
    }
    modal.mpOpener = opener;
    (bootstrap.Modal.getInstance(modal) || new bootstrap.Modal(modal)).show();
}

function mpDecisionTableRows(body, rows) {
    body.replaceChildren();
    for (const values of rows) {
        const row = body.insertRow();
        for (const value of values) row.insertCell().textContent = value;
    }
}
