async page => {
    const check = (value, message) => { if (!value) throw new Error(message); };
    const opener = page.getByRole('button', { name: 'Stats', exact: true });
    const modal = page.getByRole('dialog', { name: 'Stats', exact: true });
    const refresh = modal.getByRole('button', { name: 'Refresh', exact: true });
    const close = modal.getByRole('button', { name: 'Close', exact: true });
    const model = id => modal.getByRole('row', { name: new RegExp(id) });
    const action = (row, verb) => row.getByRole('button', { name: new RegExp('^' + verb + ' ') });
    const base = await page.evaluate(() => location.origin + '/');
    await page.goto(base + 'Tests/ui-fixture.html#stats-delayed-settings');
    await page.evaluate(() => {
        testStats = [{
            provider: 'openrouter', model: 'GOOGLE/MODEL-A', completed: 1, errors: 0, latency_ms: 200,
            skipped: 0, skip_completed: 0, recent_skipped: 0, recent_skip_completed: 0
        }];
        testStatsFailures.catalogs.openrouter = 'Catalog unavailable';
        const select = document.querySelector('#input_mpmodelid');
        select.add(new Option('Another model', 'another'));
        const preset = select.cloneNode(true);
        preset.id = 'preset_input_mpmodelid';
        select.after(preset);
        preset.value = 'another';
    });
    await opener.click();
    const alias = model('GOOGLE/MODEL-A');
    await alias.waitFor();
    check(await action(alias, 'Star').count() === 0 && await action(alias, 'Unstar').count() === 0,
        'Unknown preferences were shown while the settings request was pending');
    await page.evaluate(() => releaseSettings());
    await action(alias, 'Unstar').waitFor({ timeout: 3000 });

    await page.evaluate(() => { testSettings.favoritedModels.openrouter = []; });
    await refresh.click();
    await action(alias, 'Star').waitFor({ timeout: 3000 });

    await page.evaluate(() => {
        testStatsGates.settings = new Promise(resolve => { window.releaseSettings = resolve; });
    });
    await refresh.click();
    await page.waitForFunction(() => testSettingsReads > 0, { timeout: 3000 });
    await action(alias, 'Block').click();
    await action(alias, 'Unblock').waitFor({ timeout: 3000 });
    await page.evaluate(() => releaseSettings());
    await page.waitForFunction(() => testSettingsReads === 0);
    check(await action(alias, 'Unblock').isVisible(), 'A stale settings read reverted a successful preference change');

    for (const selector of ['#input_mpmodelid', '#preset_input_mpmodelid']) {
        const select = page.locator(selector);
        const available = await select.evaluate(el => [...el.options]
            .filter(option => option.style.display !== 'none' && option.dataset.blocked !== '1')
            .map(option => option.value));
        check(available.includes('another') && !available.includes('google/model-a'),
            'Case-insensitive block did not remove an available option from ' + selector);
    }
    check(await page.locator('#input_mpmodelid').inputValue() === 'google/model-a'
        && await page.locator('#preset_input_mpmodelid').inputValue() === 'another',
    'Updating preferences changed an explicit Generate or preset selection');
    await close.click();
    await modal.waitFor({ state: 'hidden' });
    const trigger = page.locator('#mp_model_dropdown_trigger');
    const popover = page.locator('#mp_model_dropdown_popover');
    await trigger.click();
    check(await popover.getByRole('button', { name: 'Google: Fast model', exact: true }).count() === 0,
        'Generate dropdown offered a blocked alias without a catalog');
    await page.keyboard.press('Escape');

    await opener.click();
    await action(alias, 'Unblock').click();
    await action(alias, 'Block').waitFor();
    check(await page.locator('#preset_input_mpmodelid').evaluate(el => [...el.options]
        .some(option => option.value === 'google/model-a' && option.style.display !== 'none')),
    'Unblock could not restore the preset option without a catalog');
    await close.click();
    await modal.waitFor({ state: 'hidden' });
    await page.getByRole('button', { name: 'MagicPrompt Settings' }).click();
    await page.evaluate(() => {
        testSettings.favoritedModels.openrouter.push('another-tabs-favorite');
        testSettings.blockedModels.openaiapi.push('another-tabs-block');
    });
    await page.locator('#mpTimeout').fill('45');
    await page.getByRole('button', { name: 'Save and close' }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => testSettings.backends.openrouter.timeout === 45
        && testSettings.favoritedModels.openrouter.includes('another-tabs-favorite')
        && testSettings.blockedModels.openaiapi.includes('another-tabs-block')), 'An unrelated Settings save overwrote newer prompt preferences');

    await page.goto(base);
    await page.waitForFunction(() => MP.settingsLoaded && document.querySelector('#input_mpmodelid option[value="provider/model-0"]'));
    await page.evaluate(() => {
        testStatsGates.preference = new Promise(resolve => { window.releasePreference = resolve; });
    });
    await trigger.click();
    await popover.getByRole('button', { name: /^Block Provider: Model 0 ·/ }).click();
    await trigger.click();
    await trigger.click();
    const staleRow = popover.getByRole('button', { name: /^Provider: Model 0 ·/ });
    await staleRow.waitFor();
    await page.evaluate(() => releasePreference());
    await staleRow.waitFor({ state: 'hidden', timeout: 3000 });
    check(await page.locator('#input_mpmodelid').inputValue() === 'google/model-a',
        'Saving a block in a reopened dropdown changed the Generate selection');
    if (!await popover.isVisible()) await trigger.click();
    check(!await staleRow.isVisible(), 'Reopening still offered the blocked option');
    await popover.getByRole('button', { name: /^Add to favorites: Provider: Model 1 ·/ }).click();
    await popover.getByRole('button', { name: /^Remove from favorites: Provider: Model 1 ·/ }).waitFor({ timeout: 3000 });
    await popover.getByRole('button', { name: /^Block Provider: Model 2 ·/ }).click();
    await popover.getByRole('button', { name: /^Block Provider: Model 2 ·/ }).waitFor({ state: 'hidden', timeout: 3000 });
    check(await popover.isVisible(), 'Saving preferences closed the original picker before a second edit');
    return 'Preference readiness, shared refreshes, stale reads, catalog fallback and reopened dropdown passed';
}
