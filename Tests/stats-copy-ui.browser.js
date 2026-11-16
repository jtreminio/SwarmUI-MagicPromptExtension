async page => {
    const check = (value, message) => { if (!value) throw new Error(message); };
    await page.goto(await page.evaluate(() => location.origin + '/'));
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    await page.waitForFunction(() => MP.settingsLoaded);
    await page.evaluate(() => {
        localStorage.removeItem('magicprompt_stats_collapsed_groups');
        testStatsCatalogs.openrouter = [{ model: 'deepseek/deepseek-v4.1-flash-1017', name: 'DeepSeek: DeepSeek V4.1 Flash 1017', created: 1 }];
        testStats = [{ provider: 'openrouter', model: 'deepseek/deepseek-v4.1-flash-1017', completed: 10, errors: 0,
            latency_ms: 16000, skipped: 2, skip_completed: 10, recent_skipped: 2, recent_skip_completed: 10,
            response_cost_count: 1, response_cost_total: 1e-28, skipped_cost_count: 1, skipped_cost_total: 1e-28 }];
    });
    await page.getByRole('button', { name: 'Stats', exact: true }).click();
    const modal = page.getByRole('dialog', { name: 'Stats', exact: true });
    const row = modal.locator('tr[data-key]');
    await row.getByText('DeepSeek: DeepSeek V4.1 Flash 1017', { exact: true }).waitFor();
    check(!/OpenRouter|deepseek\//.test(await row.innerText()), 'Provider/codename should be hidden by default');
    const copy = row.getByRole('button', { name: 'Copy codename for DeepSeek: DeepSeek V4.1 Flash 1017', exact: true });
    await copy.click();
    check(await page.evaluate(() => navigator.clipboard.readText()) === 'deepseek/deepseek-v4.1-flash-1017',
        'Copy must write the exact codename, not the human name or provider prefix');
    check(await row.getByText('Avg response: 16 s', { exact: true }).isVisible(), 'Latency disappeared with provider metadata');
    await modal.getByRole('status').filter({ hasText: 'Copied model codename.' }).waitFor();
    await copy.focus();
    await page.keyboard.press('Enter');
    check(await copy.evaluate(el => el === document.activeElement), 'Keyboard copy lost focus');

    await page.evaluate(async () => {
        await navigator.clipboard.writeText('Before fallback copy');
        window.statsClipboard = navigator.clipboard;
        Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
    });
    await copy.click();
    check(await page.evaluate(() => statsClipboard.readText()) === 'deepseek/deepseek-v4.1-flash-1017'
        && await copy.evaluate(el => el === document.activeElement), 'Fallback clipboard copy must work inside the modal focus trap');
    check(await modal.locator('textarea').count() === 0, 'Fallback left a hidden textarea behind');
    await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true,
        value: { writeText: () => Promise.reject(new Error('Clipboard denied')) } }));
    await copy.click();
    await modal.getByRole('status').filter({ hasText: 'Could not copy model codename: Clipboard denied' }).waitFor();
    check(await row.getByText('DeepSeek: DeepSeek V4.1 Flash 1017', { exact: true }).isVisible(), 'Clipboard failure hid the model');
    await page.evaluate(() => Object.defineProperty(navigator, 'clipboard', { configurable: true, value: statsClipboard }));
    await page.evaluate(() => {
        const select = document.getElementById('input_mpmodelid');
        select.value = 'deepseek/deepseek-v4.1-flash-1017';
        triggerChangeFor(select);
    });
    check(await row.getByText('Selected in Generate', { exact: true }).isVisible(), 'Long model name must retain its selection badge');

    for (const width of [320, 390, 801, 1440]) {
        await page.setViewportSize({ width, height: 900 });
        check(await modal.evaluate(el => {
            const body = el.querySelector('.modal-body');
            const costs = el.querySelector('.mp-stats-cost');
            return body.scrollWidth <= body.clientWidth && costs.scrollWidth <= costs.clientWidth;
        }), 'Very large prompts/$1 estimates overflow at ' + width);
        check(await row.locator('.mp-stats-cost').getByText('1E28', { exact: true }).count() === 2,
            'Very large estimates must stay finite and compact');
    }
    await page.evaluate(() => { testStatsFailures.read = 'Fixture refresh failed'; });
    await modal.getByRole('button', { name: 'Refresh', exact: true }).click();
    await modal.getByRole('status').filter({ hasText: 'Could not load stats: Fixture refresh failed' }).waitFor();
    await copy.click();
    await modal.getByRole('status').filter({ hasText: 'Copied model codename.' }).waitFor();
    check(!await modal.getByRole('status').evaluate(el => el.classList.contains('mp-status-error')),
        'A successful copy must show confirmation even when the last Stats refresh failed');
    await modal.getByRole('button', { name: 'Close', exact: true }).click();
    return 'Hidden codenames, exact clipboard copy, keyboard/fallback/denied clipboard and extreme-value layout passed';
}
