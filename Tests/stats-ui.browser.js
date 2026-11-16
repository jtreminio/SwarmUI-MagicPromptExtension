async page => {
    const check = (value, message) => { if (!value) throw new Error(message); };
    await page.goto(await page.evaluate(() => location.origin + '/'));
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.waitForFunction(() => MP.settingsLoaded && document.querySelector('#input_mpmodelid').options.length > 2);
    await page.evaluate(() => {
        localStorage.removeItem('magicprompt_stats_collapsed_groups');
        testSettings.favoritedModels.openrouter = ['GOOGLE/MODEL-A'];
        MP.settings = mpNormalizeSettings(testSettings);
        testStats = [
            { provider: 'openrouter', model: 'old/model', completed: 0, errors: 0 },
            { provider: 'openrouter', model: 'newest', costed_completed: 100, cost_total: 0.2, costed_skip_completed: 50, costed_skipped: 10, skip_cost_total: 0.1, completed: 530, errors: 10, latency_ms: 1823, skipped: 36, skip_completed: 400, recent_skipped: 36, recent_skip_completed: 100 },
            { provider: 'openrouter', model: 'older', costed_completed: 1, cost_total: 4, costed_skip_completed: 1, costed_skipped: 0, skip_cost_total: 4, completed: 405, errors: 0, latency_ms: 923, skipped: 103, skip_completed: 200, recent_skipped: 0, recent_skip_completed: 100 },
            { provider: 'openrouter', model: 'tied', costed_completed: 2, cost_total: 0, costed_skip_completed: 2, costed_skipped: 1, skip_cost_total: 0, completed: 14, errors: 0, latency_ms: 0, skipped: 0, skip_completed: 14, recent_skipped: 0, recent_skip_completed: 14 },
            { provider: 'openrouter', model: 'google/model-a', completed: 2, errors: 1, latency_ms: 225, skipped: 1, skip_completed: 2, recent_skipped: 1, recent_skip_completed: 2 },
            { provider: 'openrouter', model: 'GOOGLE/MODEL-A', completed: 7, errors: 0, latency_ms: 400 },
            { provider: 'openaiapi', model: 'local/z', completed: 0, errors: 2 },
            { provider: 'openaiapi', model: 'local/a', completed: 1, errors: 0, latency_ms: 3400 },
            { provider: 'openrouter', model: 'unknown/a-very-long-model-identifier-without-a-word-break', completed: 0, errors: 0 },
            { provider: 'openrouter', model: 'unknown/z', completed: 10001, errors: 1, latency_ms: 1250, skipped: 10000, skip_completed: 10001 }
        ].map(row => ({ latency_ms: null, skipped: 0, skip_completed: 0, recent_skipped: 0, recent_skip_completed: 0, ...row }));
        testStatsCatalogs = {
            openrouter: [
                { model: 'tied', name: 'Zenith 14B', created: 300 },
                { model: 'newest', name: 'Aurora 70B', created: 300 },
                { model: 'older', name: 'Atlas 32B', created: 200 },
                { model: 'google/model-a', name: 'Gemini Flash', created: 100 },
                { model: 'old/model', name: 'Older Model', created: 10 }
            ],
            openaiapi: [{ model: 'local/z', name: 'Zeta Local' }, { model: 'local/a', name: 'Alpha Local' }]
        };
        testStatsGates.openrouter = new Promise(resolve => { window.releaseOpenRouterCatalog = resolve; });
        testStatsGates.read = new Promise(resolve => { window.releaseStatsRead = resolve; });
    });

    const opener = page.getByRole('button', { name: 'Stats', exact: true });
    await opener.click({ timeout: 2000 });
    const modal = page.getByRole('dialog', { name: 'Stats', exact: true });
    const status = modal.getByRole('status');
    const table = modal.getByRole('table', { name: 'Model stats' });
    const rows = table.locator('tbody tr[data-key]');
    const groupHeadings = table.getByRole('rowheader').and(table.locator('th[scope="rowgroup"]'));
    const groupToggle = name => table.getByRole('button', { name, exact: true });
    const model = id => rows.filter({ has: page.locator('th').getByText(id, { exact: true }) });
    const sort = modal.getByRole('combobox', { name: 'Sort' });
    const refresh = modal.getByRole('button', { name: 'Refresh', exact: true });
    const action = (row, verb) => row.getByRole('button', { name: new RegExp('^' + verb + ' ') });
    const waitForFocus = async locator => check(await locator.evaluate(el => el === document.activeElement), 'Expected focus to stay on ' + locator);

    await status.filter({ hasText: 'Loading stats…' }).waitFor();
    await page.evaluate(() => releaseStatsRead());
    await model('newest').waitFor();
    const zeta = rows.filter({ hasText: 'Zeta Local' });
    await zeta.waitFor();
    check(await modal.getByText('Aurora 70B').count() === 0, 'A slow OpenRouter catalog delayed local enrichment or the stats table');
    await action(model('newest'), 'Star').focus();
    await page.evaluate(() => releaseOpenRouterCatalog());
    await modal.getByText('Aurora 70B').waitFor();
    const aurora = rows.filter({ hasText: 'Aurora 70B' });
    const atlas = rows.filter({ hasText: 'Atlas 32B' });
    const zenith = rows.filter({ hasText: 'Zenith 14B' });
    const gemini = rows.filter({ hasText: 'Gemini Flash' });
    const geminiAlias = model('GOOGLE/MODEL-A');
    const older = rows.filter({ hasText: 'Older Model' });
    await waitForFocus(action(aurora, 'Star'));
    const matchOrder = async (expected, message) => {
        const actual = await rows.locator('th > :first-child').allTextContents();
        check(expected.every((value, index) => actual[index] === value), message + ': ' + actual.join(', '));
    };
    await matchOrder(['Gemini Flash', 'GOOGLE/MODEL-A', 'Zenith 14B', 'Aurora 70B', 'Atlas 32B', 'Zeta Local', 'Alpha Local'], 'Newest, catalog ties, local rank, or preference groups are wrong');
    check((await groupHeadings.allTextContents()).join(', ') === 'Starred, Unstarred, Blocked', 'Preference groups have no visible headings');
    const groupHeading = row => row.locator('xpath=..').locator('th[scope="rowgroup"]');
    check(await groupHeading(gemini).textContent() === 'Starred'
        && await groupHeading(aurora).textContent() === 'Unstarred'
        && await groupHeading(older).textContent() === 'Blocked', 'A model is under the wrong heading');
    const starredToggle = groupToggle('Starred');
    const blockedToggle = groupToggle('Blocked');
    check(await starredToggle.getAttribute('aria-expanded', { timeout: 2000 }) === 'true', 'Sections should start expanded');
    await starredToggle.click();
    await blockedToggle.press('Space');
    check(await starredToggle.getAttribute('aria-expanded') === 'false' && !await gemini.isVisible()
        && await blockedToggle.getAttribute('aria-expanded') === 'false' && !await older.isVisible()
        && await aurora.isVisible(), 'Section buttons should collapse their own rows independently');
    await waitForFocus(blockedToggle);
    await page.keyboard.press('Escape');
    await modal.waitFor({ state: 'hidden' });
    await opener.click();
    check(await starredToggle.getAttribute('aria-expanded') === 'false'
        && await blockedToggle.getAttribute('aria-expanded') === 'false' && !await gemini.isVisible()
        && !await older.isVisible(), 'Reopening Stats should retain collapsed sections');
    await starredToggle.press('Enter');
    await blockedToggle.click();
    check(await gemini.isVisible() && await older.isVisible(), 'Expanding a section should restore its rows');
    check(await table.getByRole('columnheader', { name: 'Completed requests' }).textContent() === '#', 'Completed count header is not #');
    check((await rows.last().textContent()).includes('Older Model'), 'Blocked row was not last');
    check(await action(geminiAlias, 'Unstar').isVisible(), 'Exact stats rows failed to share a case-insensitive preference');
    check(await action(gemini, 'Unstar').getByText('★', { exact: true }).evaluate(star =>
        getComputedStyle(star).color === 'rgb(245, 179, 1)'), 'Starred models do not use the main menu’s yellow star');
    check(await aurora.getByText('Avg response: 2 s', { exact: true }).isVisible(), 'Latency is not rounded to whole seconds');
    check(await aurora.getByText('1.9%', { exact: true }).isVisible(), 'Wrong error denominator');
    check(await table.getByRole('columnheader', { name: 'Prompts for $1', exact: true }).isVisible(), 'Missing dollar metric header');
    const costs = aurora.locator('.mp-stats-cost');
    check(await costs.getByText('500', { exact: true }).isVisible() && await costs.getByText('100', { exact: true }).isVisible(),
        'Dollar estimates used historical counts or omitted spend on passing responses');
    check(await zenith.locator('.mp-stats-cost').getByText('Free', { exact: true }).count() === 2
        && await atlas.locator('.mp-stats-cost').getByText('<1', { exact: true }).isVisible()
        && await atlas.locator('.mp-stats-cost').getByText('0', { exact: true }).isVisible(),
        'Free, sub-dollar yield and zero observed skips are indistinguishable');
    check(!/Recorded spend|priced responses|Evaluation spend/.test(await modal.textContent()), 'Unrequested cost details are visible');
    await modal.locator('.modal-body').evaluate(el => el.scrollTop = 0);
    await page.waitForFunction(() => getComputedStyle(document.getElementById('mpStats')).opacity === '1');
    await page.screenshot({ path: '/tmp/mp-cost-desktop.png' });

    check(await aurora.getByText('9%', { exact: true }).isVisible() && await aurora.getByText('36%', { exact: true }).isVisible(), 'All-time and recent Skip If percentages use different denominators');
    check(await aurora.getByText('All time', { exact: true }).isVisible() && await aurora.getByText('Last 100', { exact: true }).isVisible(), 'Missing Skip If period labels');
    check(await zenith.getByText('Last 14', { exact: true }).isVisible() && await zenith.getByText('Avg response: 0 s', { exact: true }).isVisible(), 'Missing partial recent window or sampled zero latency');
    check(await zenith.getByText('0%', { exact: true }).count() === 3 && await older.getByText('—', { exact: true }).count() === 5, 'Missing rates and actual zero rates are indistinguishable');
    check(await older.getByText('Recent', { exact: true }).isVisible(), 'Empty recent period label is misleading');
    check(await zeta.getByText('100%', { exact: true }).isVisible(), 'Error-only model should have a 100% error rate');
    check(await model('unknown/z').getByText('<0.1%', { exact: true }).isVisible() && await model('unknown/z').getByText('>99.9%', { exact: true }).isVisible(), 'Non-endpoint rates look like exact endpoints');
    check(await aurora.locator('[title*="36 of 400"]').count() === 1 && await aurora.locator('[title*="36 of 100"]').count() === 1, 'Exact rate ratios are unavailable');
    check(await aurora.evaluate(row => {
        const tracks = [...row.querySelectorAll('.mp-stats-track')].map(track => track.getBoundingClientRect().width);
        return tracks.length === 3 && tracks.every(width => width > 0 && width === tracks[0]);
    }), 'Rate tracks are uneven');
    const errorRateCell = aurora.getByRole('cell').nth(1);
    const errorLabelFollowsTrack = () => errorRateCell.evaluate(cell => {
        const track = cell.querySelector('.mp-stats-track').getBoundingClientRect();
        const value = cell.querySelector('.mp-stats-value');
        const range = document.createRange();
        range.selectNodeContents(value);
        const gap = range.getBoundingClientRect().left - track.right;
        return gap >= 0 && gap < 10;
    });
    check(await errorLabelFollowsTrack(), 'Error percentage is visually separated from its track');
    for (const row of [aurora, gemini]) {
        const order = await row.locator('.mp-stats-actions button').evaluateAll(buttons => buttons.map(button => button.dataset.action));
        check(order.slice(0, 2).join(',') === 'star,use', 'Star or Unstar should come before Use');
    }
    check(await action(older, 'Unblock').isVisible() && await older.locator('button[data-action="star"]').count() === 0,
        'A blocked model should offer Unblock and hide the star action');
    check(await action(older, 'Use').count() === 0 && await action(zeta, 'Use').count() === 0
        && await action(model('unknown/z'), 'Use').count() === 0, 'Use was offered for a blocked, other-provider, or unavailable model');
    await page.evaluate(() => {
        const select = document.getElementById('input_mpmodelid');
        const preset = select.cloneNode(true);
        preset.id = 'preset_input_mpmodelid';
        select.after(preset);
        preset.value = 'google/model-a';
        select.addEventListener('change', () => { window.lastSelectedModel = select.value; });
    });
    await modal.getByRole('button', { name: 'Close', exact: true }).click();
    await modal.waitFor({ state: 'hidden' });
    const openingTransition = await page.addStyleTag({ content: '#mpStats.show .modal-dialog { transition-duration: 1s; }' });
    await page.locator('#mpStats').evaluate(el => {
        el.addEventListener('shown.bs.modal', () => { el.dataset.fullyShown = 'true'; }, { once: true });
    });
    await opener.click();
    const use = action(atlas, 'Use');
    await use.waitFor({ timeout: 2000 });
    check(await use.evaluate(button => {
        if (button.closest('#mpStats').dataset.fullyShown) return false;
        button.click();
        return true;
    }), 'Expected to activate Use during the opening transition');
    await modal.waitFor({ state: 'hidden', timeout: 2000 });
    await openingTransition.evaluate(style => style.remove());
    const trigger = page.locator('#mp_model_dropdown_trigger');
    await page.waitForFunction(() => document.activeElement.id === 'mp_model_dropdown_trigger');
    check(await trigger.textContent() === 'Atlas 32B' && await page.evaluate(() =>
        lastSelectedModel === 'older' && document.getElementById('input_mpmodelid').value === 'older'
        && document.getElementById('preset_input_mpmodelid').value === 'google/model-a' && testWrites.length === 0),
        'Use must update the live Generate selection through its change event without saving settings or changing the preset');
    await opener.click();
    await aurora.waitFor();

    for (const [label, expected] of [
        ['All-time Skip If, low to high', ['Zenith 14B', 'Aurora 70B', 'Atlas 32B', 'unknown/z']],
        ['All-time Skip If, high to low', ['unknown/z', 'Atlas 32B', 'Aurora 70B', 'Zenith 14B']],
        ['Recent Skip If, low to high', ['Zenith 14B', 'Atlas 32B', 'Aurora 70B']],
        ['Recent Skip If, high to low', ['Aurora 70B', 'Zenith 14B', 'Atlas 32B']],
        ['Responses/$1, high to low', ['Zenith 14B', 'Aurora 70B', 'Atlas 32B']],
        ['Responses/$1, low to high', ['Atlas 32B', 'Aurora 70B', 'Zenith 14B']],
        ['Skipped/$1, high to low', ['Zenith 14B', 'Aurora 70B', 'Atlas 32B']],
        ['Skipped/$1, low to high', ['Atlas 32B', 'Aurora 70B', 'Zenith 14B']]
    ]) {
        await sort.selectOption({ label });
        await matchOrder(['Gemini Flash', 'GOOGLE/MODEL-A', ...expected], 'Sort failed for ' + label);
    }
    await sort.selectOption({ label: 'Newest model added' });
    await page.setViewportSize({ width: 720, height: 844 });
    check(await modal.evaluate(el => el.querySelector('.modal-body').scrollWidth <= el.querySelector('.modal-body').clientWidth), 'Stats overflows a 720 px viewport');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.waitForFunction(() => getComputedStyle(document.getElementById('mpStats')).opacity === '1');
    await page.screenshot({ path: '/tmp/mp-cost-mobile.png' });
    check(await modal.evaluate(el => document.documentElement.scrollWidth <= innerWidth && el.scrollWidth <= innerWidth
        && el.querySelector('.modal-body').scrollWidth <= el.querySelector('.modal-body').clientWidth), 'Stats overflows mobile');
    check(await groupHeading(older).isVisible() && await errorLabelFollowsTrack(), 'Mobile loses group headings or separates the error rate');
    await blockedToggle.click();
    check(!await older.isVisible(), 'A collapsed section still displays rows on mobile');
    await blockedToggle.click();
    await action(atlas, 'Block').scrollIntoViewIfNeeded();
    const actionBounds = await action(atlas, 'Block').boundingBox();
    check(actionBounds.x >= 0 && actionBounds.x + actionBounds.width <= 390
        && actionBounds.y >= 0 && actionBounds.y + actionBounds.height <= 844, 'Mobile row action is unreachable');
    await modal.locator('.modal-body').evaluate(el => el.scrollTop = 0);
    await page.setViewportSize({ width: 320, height: 640 });
    check(await modal.evaluate(el => el.querySelector('.modal-body').scrollWidth <= el.querySelector('.modal-body').clientWidth), 'Stats overflows a 320 px viewport');
    await sort.selectOption({ label: 'All-time Skip If, high to low' });
    check(await sort.evaluate(select => {
        const styles = getComputedStyle(select);
        const context = document.createElement('canvas').getContext('2d');
        context.font = `${styles.fontSize} ${styles.fontFamily}`;
        const width = select.clientWidth - parseFloat(styles.paddingLeft) - parseFloat(styles.paddingRight);
        return context.measureText(select.selectedOptions[0].text).width <= width;
    }), 'The closed sort control truncates its selected label');
    await sort.selectOption({ label: 'Newest model added' });
    await page.setViewportSize({ width: 1440, height: 900 });

    await page.evaluate(() => { testSettings.favoritedModels.openrouter.push('another-tabs-favorite'); });
    await action(aurora, 'Star').click();
    await action(aurora, 'Unstar').waitFor();
    await waitForFocus(action(aurora, 'Unstar'));
    await matchOrder(['Aurora 70B', 'Gemini Flash', 'GOOGLE/MODEL-A'], 'New favorite did not move up');
    check(await page.evaluate(() => testSettings.favoritedModels.openrouter.includes('newest')
        && document.querySelector('#input_mpmodelid option[value="newest"]').dataset.favorited === '1'
        && testSettings.favoritedModels.openrouter.includes('another-tabs-favorite')
        && testPreferenceWrites.at(-1).model === 'newest'), 'Star did not preserve another tab’s preference and update the Generate selector');
    await action(aurora, 'Block').click();
    await action(aurora, 'Unblock').waitFor();
    await waitForFocus(action(aurora, 'Unblock'));
    check(await groupHeading(aurora).textContent() === 'Blocked' && await action(aurora, 'Use').count() === 0
        && await aurora.locator('button[data-action="star"]').count() === 0, 'Blocking must move the row and remove Star and Use');
    check(await page.evaluate(() => testSettings.blockedModels.openrouter.includes('newest')
        && !testSettings.favoritedModels.openrouter.includes('newest')
        && !document.querySelector('#input_mpmodelid option[value="newest"]')), 'Block did not remove the star and hide the Generate option');
    await action(aurora, 'Unblock').click();
    await action(aurora, 'Star').waitFor();
    await waitForFocus(action(aurora, 'Block'));
    check(await action(aurora, 'Use').isVisible()
        && await page.evaluate(() => !testSettings.blockedModels.openrouter.includes('newest')), 'Unblock did not restore Star and Use');
    await action(aurora, 'Star').click();
    await action(aurora, 'Unstar').waitFor();
    await action(geminiAlias, 'Unstar').click();
    await action(gemini, 'Star').waitFor();
    check(await page.evaluate(() => !testSettings.favoritedModels.openrouter.some(id => id.toLowerCase() === 'google/model-a')), 'Mixed-case unstar did not remove the persisted preference');

    await page.evaluate(() => { failSave = true; });
    await action(aurora, 'Unstar').click();
    await status.filter({ hasText: /Could not save.*Simulated save failure/ }).waitFor();
    check(await action(aurora, 'Unstar').isVisible(), 'Failed save was shown as successful');
    await waitForFocus(action(aurora, 'Unstar'));
    await page.evaluate(() => { failSave = false; });

    const reset = action(aurora, 'Reset');
    const confirm = aurora.getByRole('button', { name: 'Reset', exact: true });
    const cancel = aurora.getByRole('button', { name: 'Cancel', exact: true });
    await reset.click();
    await waitForFocus(cancel);
    await aurora.getByText('Reset stats for Aurora 70B?', { exact: true }).waitFor();
    for (const button of [confirm, cancel]) {
        check(await button.evaluate(el => document.getElementById(el.getAttribute('aria-describedby'))?.textContent === 'Reset stats for Aurora 70B?'),
            'Confirmation controls do not identify the affected model to assistive technology');
    }
    await cancel.click();
    await waitForFocus(reset);
    check(await aurora.getByText('530', { exact: true }).isVisible(), 'Cancel changed metrics');

    await page.evaluate(() => { testStatsFailures.reset = 'Not authorized to reset'; });
    await action(older, 'Reset').click();
    await older.getByRole('button', { name: 'Reset', exact: true }).click();
    await status.filter({ hasText: /Not authorized to reset/ }).waitFor();
    check(await status.evaluate(el => {
        const { top, bottom } = el.getBoundingClientRect();
        const clippingRect = el.closest('.modal-body, .modal-content').getBoundingClientRect();
        return top >= clippingRect.top && bottom <= clippingRect.bottom && top >= 0 && bottom <= innerHeight;
    }), 'The reset error is out of view when a lower row action fails');
    await older.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.evaluate(() => {
        testStatsFailures.reset = '';
        testStatsGates.read = new Promise(resolve => { window.releaseStatsRead = resolve; });
    });
    await refresh.click();
    await reset.click();
    await confirm.click();
    await aurora.getByText('Recent', { exact: true }).waitFor();
    await waitForFocus(reset);
    await page.evaluate(() => releaseStatsRead());
    await page.waitForFunction(() => testStatsReads === 0);
    check(await aurora.getByText('—', { exact: true }).count() === 5, 'Reset failed to clear both skip windows or late refresh restored old metrics');
    check(await action(aurora, 'Unstar').isVisible(), 'Reset changed preferences');
    await page.evaluate(() => { testStatsFailures.read = 'Stats unavailable'; });
    await refresh.click();
    await status.filter({ hasText: /Stats unavailable/ }).waitFor();
    check(await table.isVisible(), 'A refresh failure hid known stats');
    const unstarredToggle = groupToggle('Unstarred');
    await unstarredToggle.click();
    await page.evaluate(() => { testStatsFailures.read = ''; testStats = []; });
    await refresh.click();
    await status.filter({ hasText: 'No model stats yet.' }).waitFor();
    check(!await table.isVisible(), 'Empty stats displayed a blank table');
    await page.keyboard.press('Escape');
    await modal.waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('#magicprompt_stats_link button') === document.activeElement);

    await page.reload();
    await page.waitForFunction(() => MP.settingsLoaded
        && document.querySelector('#input_mpmodelid option[value="local/model-b"]'));
    await page.evaluate(() => {
        testSettings.backend = 'openaiapi';
        testSettings.model = 'local/a';
        testStats = ['local/a', 'local/model-b'].map(id => ({
            provider: 'openaiapi', model: id, completed: 1, errors: 0, latency_ms: 1200, skipped: 0, skip_completed: 0,
            recent_skipped: 0, recent_skip_completed: 0
        }));
        testStatsGates.openaiapi = new Promise(resolve => { window.releaseLocal = resolve; });
        testStatsFailures.catalogs.openaiapi = 'Catalog unavailable';
        window.permissions = { hasPermission: id => id !== 'magicprompt_reset_stats' };
    });
    await opener.click();
    check(await unstarredToggle.getAttribute('aria-expanded') === 'false' && !await model('local/a').isVisible(),
        'Reloading the browser should retain collapsed sections');
    await unstarredToggle.click();
    await model('local/a').waitFor();
    await page.waitForFunction(() => MP.settings.backend === 'openaiapi');
    check(await action(model('local/model-b'), 'Use').count() === 0, 'Use offered an option left over from the previous provider');
    await page.evaluate(() => releaseLocal());
    check(await action(model('local/a'), 'Star').isVisible(), 'Catalog failure hid rows or actions');
    check((await groupHeadings.allTextContents()).join(', ') === 'Unstarred', 'Empty preference groups display headings');
    check(await action(model('local/a'), 'Reset').count() === 0, 'Reset was offered without permission');
    await page.evaluate(() => {
        testStatsFailures.catalogs.openaiapi = '';
        testStatsCatalogs.openaiapi = [{ model: 'local/a', name: 'local/a' }];
        document.getElementById('mp_model_dropdown_trigger').style.display = 'none';
    });
    await refresh.click();
    await action(model('local/a'), 'Use').click();
    await modal.waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('#magicprompt_stats_link button') === document.activeElement, null, { timeout: 2000 });
    return 'Stats groups, metrics, model selection, preferences, focus, reset, stale responses and unavailable data passed';
}
