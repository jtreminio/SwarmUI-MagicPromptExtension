async page => {
    const check = (value, message) => { if (!value) throw new Error(message); };
    await page.reload();
    await page.setViewportSize({ width: 1440, height: 1000 });
    await page.waitForFunction(() => MP.settingsLoaded);
    await page.getByRole('button', { name: 'MagicPrompt Settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
    await page.locator('#mpDecisionList').waitFor();
    const sidebar = await page.locator('#mpPanelDecisions .mp-library-sidebar').boundingBox();
    const editor = await page.locator('#mpDecisionQuestions').boundingBox();
    check(sidebar.x + sidebar.width < editor.x, 'Decision library does not have a left sidebar/right editor');
    await page.locator('.mp-decision-text').fill('Original rule edited before changing selection');
    await page.locator('#mpDecisionSearch').fill('Original rule edited');
    check(await page.locator('#mpDecisionList button').count() === 1, 'Search cannot find the current unsaved question text');
    await page.locator('#mpDecisionSearch').fill('');
    await page.getByRole('button', { name: 'New decision', exact: true }).click();
    check(await page.locator('.mp-decision-question').count() === 1, 'Library renders multiple editors at once');
    await page.locator('.mp-decision-title').fill('Second rule');
    await page.locator('.mp-decision-text').fill('Did the response include a landscape?');
    await page.locator('.mp-decision-skip-if').fill('no');
    await page.locator('#mpDecisionList').getByRole('button', { name: 'Prompt refusal', exact: true }).click();
    check(await page.locator('.mp-decision-text').inputValue() === 'Original rule edited before changing selection', 'Switching rules lost edits');
    await page.locator('#mpDecisionSearch').fill('second');
    check(await page.locator('#mpDecisionList button').count() === 1, 'Search did not filter the library');
    await page.locator('#mpDecisionList').getByRole('button', { name: 'Second rule', exact: true }).click();
    check(await page.locator('.mp-decision-skip-if').inputValue() === 'no', 'Rule selection lost Skip If');
    await page.getByRole('button', { name: 'Save and close', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => testSettings.decisions.questions.length === 2), 'Library changes did not persist');
    await page.getByRole('button', { name: 'MagicPrompt Settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
    await page.locator('#mpDecisionList').getByRole('button', { name: 'Second rule', exact: true }).click();
    check(await page.locator('.mp-decision-text').inputValue() === 'Did the response include a landscape?', 'Reopen lost saved rule');
    await page.evaluate(() => { window.confirm = () => true; });
    await page.getByRole('button', { name: 'Delete decision', exact: true }).click();
    check(await page.locator('#mpDecisionList button').count() === 1, 'Delete did not update library');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    check(await page.evaluate(() => testSettings.decisions.questions.length === 2), 'Cancel persisted deletion');
    await page.getByRole('button', { name: 'MagicPrompt Settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Decisions', exact: true }).click();
    await page.locator('#mpImportDecisionsFile').evaluate(input => {
        const transfer = new DataTransfer();
        transfer.items.add(new File([JSON.stringify({ title: 'Imported rating', question: 'How detailed is it?',
            type: 'score', options: ['Sparse', 'Detailed'], selected: false, skipIf: '' })], 'decisions.json', { type: 'application/json' }));
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await page.locator('#mpDecisionList').getByRole('button', { name: 'Imported rating', exact: true }).waitFor();
    check(await page.locator('.mp-decision-type').inputValue() === 'score', 'Import did not select imported editor');
    await page.evaluate(() => {
        const original = URL.createObjectURL;
        URL.createObjectURL = blob => { window.exportedDecision = blob.text(); return original(blob); };
    });
    await page.locator('.mp-export-decision').click();
    const exported = await page.evaluate(async () => JSON.parse(await window.exportedDecision));
    check(Object.values(exported).length === 1 && Object.values(exported)[0].title === 'Imported rating'
        && Object.values(exported)[0].options.join() === 'Sparse,Detailed', 'Selected export lost decision fields');
    await page.setViewportSize({ width: 390, height: 844 });
    check(await page.locator('#settingsModal').evaluate(el => el.scrollWidth <= window.innerWidth), 'Library overflows mobile viewport');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.locator('#settingsModal').waitFor({ state: 'hidden' });
    return 'Decision library: shared layout, search, one editor, switch/save/reopen/delete/cancel passed';
}
