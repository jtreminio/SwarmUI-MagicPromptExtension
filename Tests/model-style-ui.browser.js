async page => {
 const check=(value,message)=>{if(!value)throw Error(message)};
 await page.reload();
 await page.setViewportSize({width:1440,height:1000});
 await page.waitForFunction(()=>MP.settingsLoaded && document.querySelector('#mp_model_dropdown_trigger'));
 const appearance=async(menu,search,row)=>({
   menu:await page.locator(menu).evaluate(el=>{const s=getComputedStyle(el);return [s.backgroundColor,s.borderColor,s.borderRadius,s.padding,s.boxShadow]}),
   search:await page.locator(search).evaluate(el=>{const s=getComputedStyle(el);return [s.backgroundColor,s.color,s.height,s.padding,s.borderRadius,s.fontSize]}),
   row:await page.locator(row).first().evaluate(el=>{const s=getComputedStyle(el);return [s.fontSize,s.lineHeight,s.padding,el.getBoundingClientRect().height]})
 });
 await page.locator('#mp_model_dropdown_trigger').click();
 const main=await appearance('#mp_model_dropdown_popover','#mp_model_dropdown_popover input','#mp_model_dropdown_popover .mp-model-dropdown-row');
 await page.keyboard.press('Escape');
 await page.locator('#magicprompt_sidebar_settings_link button').click();
 await page.getByRole('tab',{name:'Models',exact:true}).click();
 await page.locator('#modelSelect').click();
 const native=page.locator('#popover_uiimprover_modelSelect');
 await native.waitFor();
 check(Math.abs((await native.boundingBox()).width-(await page.locator('#modelSelect').boundingBox()).width)<2,'Default menu does not match its field width');
 const favorites=native.locator('.mp-model-dropdown-row-favorited');
 check(await favorites.count()===1,'Default favorite marker missing');
 check(await favorites.locator('.mp-model-dropdown-favorite-marker').textContent()==='★','Default favorite star missing');
 check(await favorites.locator('.mp-model-dropdown-row-label').textContent()==='Google: Fast model · $1.5 / 1M out','Wrong default favorite');
 check(await native.locator('.mp-model-dropdown-row').first().textContent()==='Google: Fast model · $1.5 / 1M out★','Favorites not first');
 const defaultStyle=await appearance('#popover_uiimprover_modelSelect','#popover_uiimprover_modelSelect input','#popover_uiimprover_modelSelect .mp-model-dropdown-row');
 check(JSON.stringify(defaultStyle)===JSON.stringify(main),`Default differs from sidebar: ${JSON.stringify({main,defaultStyle})}`);
 await native.locator('input').fill('local/model-b');
 await native.locator('.sui_popover_model_button').filter({hasText:'Local model'}).click();
 check(await page.locator('#modelSelect').inputValue()==='local/model-b','Styled row selection failed');
 for(const id of ['mpFavoriteModels','mpBlockedModels']){
   await page.locator(`#${id}Trigger`).click();
   const style=await appearance(`#${id}Panel`,`#${id}Panel .mp-model-filter`,`#${id}Panel .mp-model-option`);
   check(JSON.stringify(style)===JSON.stringify(main),`${id} differs from sidebar: ${JSON.stringify({main,style})}`);
   await page.keyboard.press('Escape');
 }
 await page.getByRole('button',{name:'Cancel',exact:true}).click();
 await page.waitForFunction(()=>document.querySelectorAll('.modal-backdrop').length===0);
 return {passed:true,checks:'favorite stars and pinned order; identical menu/search/row styles across sidebar, Default, Favorites and Blocked; native selection'};
}
