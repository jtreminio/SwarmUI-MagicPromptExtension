async page => {
  const check = (value, message) => { if (!value) throw new Error(message); };
  await page.reload();
  await page.waitForFunction(() => MP.settingsLoaded);
  await page.locator('#magicprompt_sidebar_settings_link button').click();
  await page.getByRole('tab', {name:'Models',exact:true}).click();
  const catalog = [
    {model:'old', name:'A oldest', created:10},
    {model:'tie-z', name:'Z tied', created:20},
    {model:'tie-a', name:'A tied', created:20},
    {model:'new', name:'Z newest', created:30},
    {model:'missing-z', name:'Z no date'},
    {model:'missing-a', name:'A no date'}
  ];
  for (const backend of ['openrouter', 'openaiapi']) {
    const expected = backend === 'openrouter'
      ? ['new','tie-z','tie-a','old','missing-z','missing-a','unavailable']
      : ['old','tie-z','tie-a','new','missing-z','missing-a','unavailable'];
    for (const [id,key] of [['mpFavoriteModels','favoritedModels'],['mpBlockedModels','blockedModels']]) {
      await page.evaluate(({backend,catalog,key}) => {
        mpSettingsDraft.backend = backend;
        mpSettingsDraft.blockedModels[backend] = [];
        mpSettingsDraft.favoritedModels[backend] = [];
        // Deliberately select in a different order from either catalog dates or names.
        mpSettingsDraft[key][backend] = ['unavailable','old','new','tie-a','tie-z','missing-a','missing-z'];
        MP.modelCatalogs[mpCatalogKey(mpSettingsDraft)] = catalog;
        renderModelSettings();
      }, {backend,catalog,key});
      await page.locator(`#${id}Trigger`).click();
      const listOrder = await page.locator(`#${id}Panel input[type=checkbox]`).evaluateAll(boxes => boxes.map(box => box.value));
      const chipOrder = await page.locator(`#${id}Trigger [data-remove-model]`).evaluateAll(chips => chips.map(chip => chip.dataset.removeModel));
      check(JSON.stringify(listOrder) === JSON.stringify(expected), `${backend} ${id} list order: ${listOrder}`);
      check(JSON.stringify(chipOrder) === JSON.stringify(expected), `${backend} ${id} chip order: ${chipOrder}`);
      await page.locator(`#${id}Panel input[type=search]`).fill('tied');
      const filtered = await page.locator(`#${id}Panel .mp-model-option:visible input`).evaluateAll(boxes => boxes.map(box => box.value));
      check(JSON.stringify(filtered) === JSON.stringify(['tie-z','tie-a']), 'Filtering changed stable date order');
      await page.locator(`#${id}Panel input[type=search]`).fill('');
      await page.keyboard.press('Escape');
    }
  }
  await page.getByRole('button', {name:'Cancel',exact:true}).click();
  await page.waitForFunction(() => document.querySelectorAll('.modal-backdrop').length === 0);
  return {passed:true, checks:'favorites and blocked lists AND chips: newest OpenRouter dates, stable ties, missing dates/IDs, local provider order, filtering'};
}
