async page => {
    const check = (condition, message) => { if (!condition) throw new Error(message); };
    await page.reload();
    await page.waitForFunction(() => MP.settingsLoaded);
    await page.evaluate(() => {
        window.gen_param_types = [{ id: 'mpdecisionquestion', values: [''], value_names: ['Settings defaults'] }];
        document.body.insertAdjacentHTML('beforeend', '<input type="checkbox" id="input_mpdecisionchecks"><input type="checkbox" id="preset_input_mpdecisionchecks"><select id="preset_input_mpdecisionquestion"><option value="">Settings defaults</option></select>');
        postParamBuildSteps.forEach(step => step());
    });
    check(await page.getByRole('checkbox', { name: 'MP Decision Checks' }).count() === 2, 'Decision checkboxes lack accessible names');
    check(await page.getByRole('combobox', { name: 'MP Decision Question' }).count() === 2, 'Decision pickers lack accessible names');
    await page.getByRole('button', { name: 'MagicPrompt Settings' }).click();
    await page.getByRole('tab', { name: 'Decisions' }).click();
    const firstQuestion = page.locator('.mp-decision-question').first();
    await firstQuestion.getByRole('textbox', { name: 'Skip If', exact: true }).fill('yes');
    await page.getByRole('button', { name: 'Add question' }).click();
    const question = page.locator('.mp-decision-question').last();
    await question.getByRole('textbox', { name: 'Title' }).fill('prompt REFUSAL');
    await question.getByRole('textbox', { name: 'Question', exact: true }).fill('Did the model give an answer?');
    await page.getByRole('button', { name: 'Save and close' }).click();
    await page.getByRole('status').filter({ hasText: 'unique title' }).waitFor();
    await question.getByRole('textbox', { name: 'Title' }).fill('Answered prompt');
    await question.getByRole('combobox', { name: 'Answer type' }).selectOption('score');
    const secondRule = question.getByRole('textbox', { name: 'Skip If', exact: true });
    check(await secondRule.isDisabled(), 'A rating question offered an active skip rule');
    await question.getByRole('combobox', { name: 'Answer type' }).selectOption('noul');
    await secondRule.fill('no');
    await page.getByRole('button', { name: 'Save and close' }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    const picker = page.locator('#input_mpdecisionquestion');
    await picker.selectOption({ label: 'Answered prompt' });
    const id = await picker.inputValue();
    await page.locator('#preset_input_mpdecisionquestion').selectOption(id);
    check(await page.evaluate(id => gen_param_types[0].values.includes(id), id), 'Parameter definition was not refreshed for later preset builds');
    check(await page.evaluate(id => testSettings.decisions.questions.some(row => row.id === id && row.title === 'Answered prompt'), id), 'Saved title did not reach Generate selector');
    await page.getByRole('button', { name: 'MagicPrompt Settings' }).click();
    await page.getByRole('tab', { name: 'Decisions' }).click();
    check(await firstQuestion.getByRole('textbox', { name: 'Skip If', exact: true }).inputValue() === 'yes', 'First question lost its own rule');
    check(await secondRule.inputValue() === 'no', 'Second question lost its own rule');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });

    await page.evaluate(id => {
        MP.settings.decisions.questions = MP.settings.decisions.questions.filter(row => row.id !== id);
        MP.settings.decisions.questions.forEach(row => row.selected = false);
        mpRefreshDecisionControls();
    }, id);
    for (const selector of ['#input_mpdecisionquestion', '#preset_input_mpdecisionquestion']) {
        check(await page.locator(selector).inputValue() === id, 'Removed question silently changed selection');
        check((await page.locator(`${selector} option:checked`).textContent()).includes('unavailable'), 'Removed question is not identified');
        check((await page.locator(`${selector} option[value=""]`).textContent()).includes('none selected'), 'Empty settings defaults are not identified');
    }
    await page.evaluate(() => {
        const preset = document.querySelector('#preset_input_mpdecisionquestion');
        preset.replaceChildren(...gen_param_types[0].values.map((value, index) => new Option(gen_param_types[0].value_names[index], value)));
        setDirectParamValue(gen_param_types[0], 'old-preset-id', preset);
    });
    check(await page.locator('#preset_input_mpdecisionquestion').inputValue() === 'old-preset-id', 'Loading a stale preset lost its explicit value');
    check((await page.locator('#preset_input_mpdecisionquestion option:checked').textContent()).includes('unavailable'), 'Loaded stale preset was not identified');
    await page.evaluate(() => new Promise(resolve => refreshParameterValues(true, null, resolve)));
    check(await picker.inputValue() === id, 'Native refresh discarded the stale Generate question');
    check(await page.locator('#preset_input_mpdecisionquestion').inputValue() === 'old-preset-id', 'Native refresh overwrote the independent preset selection');

    await page.evaluate(() => {
        const decision = { title: 'Prompt refusal', question: 'Did it refuse?', type: 'noul' };
        window.testDecisionHistory = {
            series: [{ ...decision, id: 'refusal-v1', evaluator: 'test/StartLux', version: 'abcdef12' }],
            selected: 'refusal-v1', total: 3,
            models: [
                { model: 'model-a', provider: 'openrouter', completed: 2, errors: 1, yes: 1 },
                { model: 'model-b', provider: 'openrouter', completed: 0, errors: 2, yes: 0 }
            ],
            rows: [
                { time_utc: '2026-11-14T21:00:00Z', source: { model: 'model-a', provider: 'openrouter' }, decision, status: 'ok', answer: false, probabilities: [0.9, 0.1] },
                { time_utc: '2026-11-14T20:00:00Z', source: { model: 'model-b', provider: 'openrouter' }, decision, status: 'error', error: 'Evaluator unavailable' }
            ]
        };
    });
    await page.getByRole('button', { name: 'Decision History' }).click();
    const comparisons = page.getByRole('table', { name: 'Model comparison' });
    await comparisons.getByText('50.0% (1/2)').waitFor();
    check(await comparisons.getByRole('row', { name: /model-b/ }).getByText('—', { exact: true }).isVisible(), 'Error-only model showed a yes rate');
    const checks = page.getByRole('table', { name: 'Individual decision checks' });
    await checks.getByText('No (10% probability of Yes)').waitFor();
    await checks.getByText('Error: Evaluator unavailable').waitFor();
    await page.waitForFunction(() => document.activeElement?.id === 'mpDecisionHistory');
    await page.keyboard.press('Escape');
    await page.locator('#mpDecisionHistory').waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.activeElement?.textContent === 'Decision History');
    await page.getByRole('button', { name: 'Decision History' }).click();
    await comparisons.getByText('50.0% (1/2)').waitFor();
    await page.evaluate(() => {
        const decision = { title: 'Rating', question: 'How good?', type: 'score', options: ['Poor', 'Good', 'Great'] };
        testDecisionHistory = { series: [{ ...decision, id: 'rating', evaluator: 'test/StartLux', version: 'abcdef12' }], selected: 'rating', total: 50, page_size: 25, models: [{ model: 'model-a', provider: 'openrouter', completed: 1, errors: 0, mean_score: 1.5 }], rows: [{ time_utc: '2026-11-14T21:00:00Z', source: { model: 'model-a', provider: 'openrouter' }, decision, status: 'ok', answer: 1.5 }] };
    });
    await page.locator('#mpDecisionRefresh').click();
    await page.getByText('How good? · Rating scale: 0: Poor · 1: Good · 2: Great').waitFor();
    await page.getByRole('button', { name: 'Next', exact: true }).click();
    await page.getByText('26–26 of 50').waitFor();
    check(await page.locator('#mpDecisionCount').getAttribute('aria-live') === 'polite', 'Pagination range is not announced');
    check(await page.locator('#mpDecisionNext').evaluate(el => document.activeElement === el), 'Pagination lost focus');
    await page.setViewportSize({ width: 390, height: 844 });
    check(await page.locator('#mpDecisionHistory').evaluate(modal => modal.scrollWidth <= innerWidth), 'Decision History overflows mobile');
    return 'Decision titles, Generate selection, history comparison, answer/error labels, and mobile layout passed';
}
