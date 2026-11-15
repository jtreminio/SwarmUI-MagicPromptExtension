async page => {
    const check = (value, message) => { if (!value) throw new Error(message); };
    await page.reload();
    await page.waitForFunction(() => MP.settingsLoaded);
    await page.setViewportSize({ width: 1440, height: 1000 });
    const open = async () => {
        await page.locator('#magicprompt_sidebar_settings_link button').click();
        await page.locator('#settingsModal.show').waitFor();
        await page.getByRole('tab', { name: 'Decision Models', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('#mpDecisionModelSelect option[value="typesafe/new"]'));
    };
    await open();
    check(await page.locator('#mpDecisionEvaluator').count() === 0, 'Local evaluator still exists');
    check(await page.locator('#mpDecisionModelSelect').inputValue() === '', 'Default was silently chosen');
    check((await page.locator('#mpDecisionModelSelect').textContent()).includes('$0.001 / request'), 'Request pricing missing');
    // Use the same searchable SwarmUI menu as regular default models.
    await page.locator('#mpDecisionModelSelect').click();
    const menu = page.locator('#popover_uiimprover_mpDecisionModelSelect');
    await menu.waitFor();
    await menu.locator('.sui_popover_text_input').fill('typesafe/old');
    await menu.locator('.sui_popover_model_button').filter({ hasText: 'Older decision' }).click();
    check(await page.locator('#mpDecisionModelSelect').inputValue() === 'typesafe/old', 'Native default selection failed');
    await page.locator('#mpDecisionFavoriteModelsTrigger').click();
    await page.locator('#mpDecisionFavoriteModelsPanel .mp-model-filter').fill('typesafe/old');
    await page.locator('#mpDecisionFavoriteModelsPanel').getByRole('checkbox', { name: /Older decision/ }).check();
    await page.keyboard.press('Escape');
    check(await page.locator('#mpDecisionModelSelect option').nth(1).getAttribute('value') === 'typesafe/old', 'Favorite not pinned');
    await page.locator('#mpDecisionBlockedModelsTrigger').click();
    await page.locator('#mpDecisionBlockedModelsPanel').getByRole('checkbox', { name: /Older decision/ }).check();
    await page.keyboard.press('Escape');
    check(await page.locator('#mpDecisionModelSelect').inputValue() === '', 'Blocked default did not clear');
    check(await page.locator('#mpDecisionFavoriteModels option:checked').count() === 0, 'Blocked favorite was retained');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => MP.settings.decisions.blockedModels.length) === 0, 'Cancel persisted block');

    await open();
    await page.locator('#mpDecisionModelSelect').selectOption('typesafe/new');
    await page.locator('#mpDecisionFavoriteModelsTrigger').click();
    await page.locator('#mpDecisionFavoriteModelsPanel .mp-model-filter').fill('typesafe/new');
    await page.locator('#mpDecisionFavoriteModelsPanel').getByRole('checkbox', { name: /New decision/ }).check();
    await page.keyboard.press('Escape');
    await page.locator('#mpDecisionBlockedModelsTrigger').click();
    await page.locator('#mpDecisionBlockedModelsPanel').getByRole('checkbox', { name: /Parallel/ }).check();
    await page.keyboard.press('Escape');
    await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
    await page.locator('.mp-decision-skip-if').first().fill('yes');
    await page.getByRole('button', { name: 'Save and close', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => testSettings.decisions.model === 'typesafe/new'
        && testSettings.decisions.favoritedModels.includes('typesafe/new')
        && testSettings.decisions.blockedModels.includes('parallel/decision')
        && testSettings.decisions.questions[0].skipIf === 'yes'
        && testSettings.favoritedModels.openrouter.join() === 'google/model-a'
        && testSettings.blockedModels.openrouter.join() === 'old/model'
        && testSettings.model === 'google/model-a'), 'Saved decision preferences damaged other settings');
    await open();
    check(await page.locator('#mpDecisionModelSelect').inputValue() === 'typesafe/new', 'Saved decision default not restored');
    await page.getByRole('tab', { name: 'Provider', exact: true }).click();
    await page.locator('#mpBackend').selectOption('openaiapi');
    await page.getByRole('tab', { name: 'Decision Models', exact: true }).click();
    await page.locator('#mpRefreshDecisionModels').click();
    check(await page.locator('#mpDecisionModelSelect').inputValue() === 'typesafe/new', 'Prompt provider changed decision default');
    check(await page.locator('#mpDecisionModelSelect option[value="google/model-a"]').count() === 0, 'Prompt catalog leaked into decisions');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('#mpDecisionFavoriteModelsTrigger').click();
    const box = await page.locator('#mpDecisionFavoriteModelsPanel').boundingBox();
    check(box.x >= 0 && box.x + box.width <= 391, 'Decision picker overflows mobile viewport');
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => testErrors.length) === 0, 'UI reported errors');
    return 'Decision Models: native selection, pricing, independent favorites/blocks, cancel/save/reopen, provider independence, mobile passed';
}
