/**
 * modeldropdown.js
 * Replaces SwarmUI's default rendering of the "MP Model ID" T2I parameter
 * (#input_mpmodelid) with a custom searchable dropdown whose rows expose a
 * "Remove" button on the far right. Clicking Remove blocks the model for the
 * active backend (using the shared MP.blockModelId helper) and drops it from
 * the dropdown immediately, leaving the popover open so multiple models can
 * be blocked in one pass.
 *
 * Robustness note: SwarmUI re-builds main_inputs_area whenever params change
 * (params.js genInputs(), settings_editor.js reload, etc.), which wipes any
 * trigger we previously inserted. We watch the inputs area with a
 * MutationObserver and re-attach on every rebuild.
 */

'use strict';

function mpInstallModelDropdown(SELECT_ID, TRIGGER_ID, POPOVER_ID, decisions = false) {
  const INPUTS_AREA_IDS = ['main_inputs_area', 'main_inputs_area_hidden', 'simple_inputs_area'];

  /** @type {HTMLDivElement|null} */
  let openPopover = null;
  let originatingPopover = null;
  let refreshPopoverRows = null;
  let attachedSelect = null;
  let labelObserver = null;
  let changeListener = null;
  document.addEventListener('mp-model-dropdown-open', () => closePopover());

  function ensureAttached() {
    let select = document.getElementById(SELECT_ID);
    if (!select) {
      return;
    }
    let existingTrigger = document.getElementById(TRIGGER_ID);
    if (existingTrigger && existingTrigger.previousElementSibling === select && attachedSelect === select) {
      return;
    }
    if (existingTrigger) {
      existingTrigger.remove();
    }
    attachToSelect(select);
  }

  function attachToSelect(select) {
    if (changeListener && attachedSelect) {
      attachedSelect.removeEventListener('change', changeListener);
    }
    if (labelObserver) {
      labelObserver.disconnect();
    }
    attachedSelect = select;

    let trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.id = TRIGGER_ID;
    trigger.className = 'mp-model-dropdown-trigger';
    trigger.setAttribute('aria-haspopup', 'dialog');
    if (decisions) trigger.setAttribute('aria-label', 'MP Decision Model ID');
    trigger.setAttribute('aria-expanded', 'false');

    select.parentNode.insertBefore(trigger, select.nextSibling);
    syncTriggerLabel(select, trigger);

    changeListener = () => syncTriggerLabel(select, trigger);
    select.addEventListener('change', changeListener);

    labelObserver = new MutationObserver(() => {
      if (!decisions && openPopover) {
        if (openPopover === originatingPopover) refreshPopoverRows();
        else closePopover(openPopover.contains(document.activeElement));
      }
      originatingPopover = null;
      syncTriggerLabel(select, trigger);
    });
    labelObserver.observe(select, { childList: true });

    trigger.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      if (openPopover) {
        closePopover();
        return;
      }
      openDropdown(select, trigger);
    });
  }

  function syncTriggerLabel(select, trigger) {
    let opt = select.options[select.selectedIndex];
    const blocked = decisions && opt?.dataset.blocked === '1';
    const label = opt?.value && opt.text ? opt.text : select.options[0]?.text;
    trigger.setAttribute('aria-invalid', String(blocked));
    trigger.title = blocked ? 'This decision model is blocked. Choose another model or Settings default.' : '';
    trigger.textContent = blocked ? `${label} (blocked — choose another model)` : label || '— No models —';
    trigger.classList.toggle('mp-model-dropdown-empty', !label);
    if (decisions) trigger.setAttribute('aria-label', `MP Decision Model ID: ${trigger.textContent}`);
  }

  function closePopover(focus = false) {
    if (!openPopover) {
      return;
    }
    openPopover.remove();
    openPopover = null;
    refreshPopoverRows = null;
    document.removeEventListener('mousedown', onDocMouseDown, true);
    document.removeEventListener('keydown', onDocKeyDown, true);
    let trigger = document.getElementById(TRIGGER_ID);
    if (trigger) {
      trigger.setAttribute('aria-expanded', 'false');
      if (focus) trigger.focus();
    }
  }

  function markPreferenceOrigin() {
    const source = openPopover;
    return () => { originatingPopover = source; };
  }

  function onDocMouseDown(e) {
    if (openPopover && !openPopover.contains(e.target) && e.target.id !== TRIGGER_ID) {
      closePopover();
    }
  }

  function onDocKeyDown(e) {
    if (e.key === 'Escape') {
      closePopover(true);
    }
  }

  function openDropdown(select, trigger) {
    closePopover();

    document.dispatchEvent(new Event('mp-model-dropdown-open'));
    let backendKey = decisions ? 'decisions' : (window.MP && MP.settings && MP.settings.backend) || 'openrouter';

    let popover = document.createElement('div');
    popover.id = POPOVER_ID;
    popover.className = 'sui-popover sui_popover_model sui-popover-visible mp-model-dropdown-popover';
    popover.setAttribute('role', 'dialog');
    popover.setAttribute('aria-label', decisions ? 'Choose a decision model' : 'Choose a model');
    popover.style.position = 'absolute';
    popover.style.zIndex = '2000';

    let search = document.createElement('input');
    search.type = 'text';
    search.placeholder = 'Search...';
    search.className = 'sui_popover_text_input mp-model-dropdown-search';
    search.setAttribute('aria-label', 'Search models');
    popover.appendChild(search);

    let listArea = document.createElement('div');
    listArea.className = 'sui_popover_scrollable_tall mp-model-dropdown-list';
    popover.appendChild(listArea);

    let rows = [];
    let lastFavoritedRow = null;
    let blocked = new Set(((decisions ? window.MP?.settings?.decisions?.blockedModels : window.MP?.settings?.blockedModels?.[backendKey]) || []).map(mpModelIdKey));
    for (let i = 0; i < select.options.length; i++) {
      let opt = select.options[i];
      if ((!opt.value && !decisions) || blocked.has(mpModelIdKey(opt.value)) || opt.dataset.blocked === '1' || (decisions && opt.dataset.unavailable === '1')) {
        continue;
      }
      let row = buildRow(opt, select, trigger, backendKey);
      listArea.appendChild(row.el);
      rows.push(row);
      if (row.favorited) {
        lastFavoritedRow = row.el;
      }
    }
    if (lastFavoritedRow) {
      let divider = document.createElement('div');
      divider.className = 'mp-model-dropdown-divider';
      lastFavoritedRow.after(divider);
    }

    function applyFilter() {
      let q = search.value.trim().toLowerCase();
      let anyVisible = false;
      const available = rows.filter(row => row.el.isConnected);
      for (let r of available) {
        let visible = !q || r.text.includes(q) || r.value.includes(q);
        r.el.style.display = visible ? '' : 'none';
        if (visible) {
          anyVisible = true;
        }
      }
      let noMatches = listArea.querySelector('.mp-model-dropdown-no-matches');
      if (!anyVisible) {
        if (!noMatches) {
          noMatches = document.createElement('div');
          noMatches.className = 'mp-model-dropdown-no-matches';
          listArea.appendChild(noMatches);
        }
        noMatches.classList.toggle('mp-model-dropdown-empty-msg', available.length === 0);
        noMatches.textContent = available.length ? 'No matches' : 'No models available. Configure a backend in Settings.';
      } else if (noMatches) {
        noMatches.remove();
      }
    }
    search.addEventListener('input', applyFilter);
    popover.addEventListener('keydown', (e) => {
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) return;
      // Keep text editing and star/block buttons' keyboard behavior intact.
      if (e.target !== search && !e.target.matches('button.mp-model-dropdown-row-label')) return;
      if (e.target === search && ['Home', 'End'].includes(e.key)) return;
      const choices = rows.filter(row => row.el.isConnected && row.el.style.display !== 'none')
        .map(row => row.el.querySelector('button.mp-model-dropdown-row-label'));
      if (!choices.length) return;
      e.preventDefault();
      const current = choices.indexOf(e.target);
      const next = e.key === 'Home' ? 0 : e.key === 'End' ? choices.length - 1
        : current < 0 ? (e.key === 'ArrowDown' ? 0 : choices.length - 1)
        : (current + (e.key === 'ArrowDown' ? 1 : -1) + choices.length) % choices.length;
      choices[next].focus();
    });

    document.body.appendChild(popover);
    openPopover = popover;
    refreshPopoverRows = () => {
      const options = new Map([...select.options].map(option => [option.value, option]));
      for (const row of rows) {
        if (!row.el.isConnected) continue;
        const option = options.get(row.model);
        if (!option || option.dataset.blocked === '1') {
          if (row.el.contains(document.activeElement)) search.focus();
          row.el.remove();
        } else {
          row.setFavorited(option.dataset.favorited === '1');
        }
      }
      applyFilter();
    };
    trigger.setAttribute('aria-expanded', 'true');

    applyFilter();
    positionPopover(popover, trigger);
    scrollSelectedIntoView(listArea);

    setTimeout(() => search.focus(), 0);
    setTimeout(() => {
      document.addEventListener('mousedown', onDocMouseDown, true);
      document.addEventListener('keydown', onDocKeyDown, true);
    }, 0);
  }

  function scrollSelectedIntoView(listArea) {
    let selectedRow = listArea.querySelector('.sui_popover_model_button_selected');
    if (!selectedRow) {
      return;
    }
    // selectedRow.offsetTop is relative to the nearest positioned ancestor
    // (the popover), not the scroll container. Subtract listArea.offsetTop to
    // get the row's offset within the scroll container, then center it.
    let rowTopInList = selectedRow.offsetTop - listArea.offsetTop;
    let center = rowTopInList - (listArea.clientHeight / 2) + (selectedRow.offsetHeight / 2);
    listArea.scrollTop = Math.max(0, center);
  }

  function positionPopover(popover, trigger) {
    let rect = trigger.getBoundingClientRect();
    let scrollX = window.scrollX || window.pageXOffset;
    let scrollY = window.scrollY || window.pageYOffset;
    let viewportH = window.innerHeight;
    let viewportW = window.innerWidth;
    let margin = 8;

    let minWidth = Math.max(rect.width, 240);
    popover.style.minWidth = minWidth + 'px';

    // Horizontal: keep fully on-screen.
    let preferredLeft = rect.left + scrollX;
    let maxLeft = scrollX + viewportW - minWidth - margin;
    let minLeft = scrollX + margin;
    popover.style.left = Math.max(minLeft, Math.min(preferredLeft, maxLeft)) + 'px';

    // Reset the list area's max-height so we can measure the popover's
    // natural size before deciding how much to shrink it.
    let listArea = popover.querySelector('.mp-model-dropdown-list');
    if (listArea) {
      listArea.style.maxHeight = '';
    }

    let spaceBelow = viewportH - rect.bottom - margin;
    let spaceAbove = rect.top - margin;
    let placeBelow = spaceBelow >= spaceAbove;
    let availSpace = placeBelow ? spaceBelow : spaceAbove;

    // If the popover at its natural height won't fit in the chosen direction,
    // clamp the scrollable list so the popover stays within the viewport.
    if (listArea && popover.offsetHeight > availSpace) {
      let nonListChrome = popover.offsetHeight - listArea.offsetHeight;
      let listMaxHeight = Math.max(80, availSpace - nonListChrome);
      listArea.style.maxHeight = listMaxHeight + 'px';
    }

    if (placeBelow) {
      popover.style.top = (rect.bottom + scrollY + 2) + 'px';
    } else {
      // Re-read offsetHeight after the list cap so the top is correct.
      popover.style.top = (rect.top + scrollY - popover.offsetHeight - 2) + 'px';
    }
  }

  function buildRow(opt, select, trigger, backendKey) {
    let row = document.createElement('div');
    row.className = 'sui_popover_model_button mp-model-dropdown-row';
    if (opt.value === select.value) {
      row.classList.add('sui_popover_model_button_selected');
    }
    let isFavorited = (window.MP && typeof window.MP.isModelFavoritedForBackend === 'function')
      ? window.MP.isModelFavoritedForBackend(backendKey, opt.value)
      : !!(opt.dataset && opt.dataset.favorited === '1');
    let label = document.createElement('button');
    label.type = 'button';
    label.className = 'mp-model-dropdown-row-label';
    label.textContent = opt.text;
    row.appendChild(label);

    let starBtn = document.createElement('button');
    starBtn.type = 'button';
    starBtn.className = 'mp-model-dropdown-star';
    function setFavorited(favorited) {
      starBtn.textContent = favorited ? '★' : '☆';
      starBtn.title = favorited ? 'Remove from favorites' : 'Add to favorites';
      starBtn.setAttribute('aria-label', starBtn.title + ': ' + opt.text);
      starBtn.classList.toggle('mp-model-dropdown-star-active', favorited);
      row.classList.toggle('mp-model-dropdown-row-favorited', favorited);
    }
    setFavorited(isFavorited);
    row.appendChild(starBtn);

    let removeBtn = document.createElement('button');
    removeBtn.type = 'button';
    removeBtn.className = 'mp-model-dropdown-remove';
    removeBtn.title = 'Block this model (adds to block list)';
    removeBtn.setAttribute('aria-label', 'Block ' + opt.text);
    removeBtn.textContent = '×';
    row.appendChild(removeBtn);
    if (decisions && !opt.value) { starBtn.hidden = true; removeBtn.hidden = true; }

    // mousedown on the row buttons must not trigger a row-select reaction.
    starBtn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
    });
    removeBtn.addEventListener('mousedown', (e) => {
      e.preventDefault();
      e.stopPropagation();
    });

    starBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      let id = opt.value;
      let MP = window.MP;
      if (!MP) {
        return;
      }
      const favorite = !starBtn.classList.contains('mp-model-dropdown-star-active');
      const action = favorite ? MP.favoriteModelId : MP.unfavoriteModelId;
      action?.(backendKey, id, decisions ? undefined : markPreferenceOrigin());
      if (decisions) {
        setFavorited(favorite);
        if (favorite) opt.dataset.favorited = '1';
        else delete opt.dataset.favorited;
      }
      // Reordering happens on the next dropdown open (after persist + select
      // rebuild). Keeping the row in place avoids surprising layout jumps
      // while the user toggles several stars in a row.
    });

    removeBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      let id = opt.value;

      window.MP?.blockModelId?.(backendKey, id, decisions ? undefined : markPreferenceOrigin());
      if (!decisions) return;

      // Removing the selected native option silently selects another model.
      // Retain it for SwarmUI's parameter value, but hide it from this menu.
      if (opt.value === select.value) {
        opt.dataset.blocked = '1';
      } else if (opt.parentNode) {
        opt.parentNode.removeChild(opt);
      }
      row.remove();
      syncTriggerLabel(select, trigger);
    });

    row.addEventListener('click', (e) => {
      if (
        e.target === removeBtn || removeBtn.contains(e.target) ||
        e.target === starBtn || starBtn.contains(e.target)
      ) {
        return;
      }
      const live = [...select.options].find(option => option.value === opt.value);
      if (!live || live.dataset.blocked === '1') {
        closePopover(true);
        return;
      }
      select.value = opt.value;
      if (typeof triggerChangeFor === 'function') {
        triggerChangeFor(select);
      }
      syncTriggerLabel(select, trigger);
      closePopover(true);
    });

    return {
      el: row,
      model: opt.value,
      setFavorited,
      text: (opt.text || '').toLowerCase(),
      value: (opt.value || '').toLowerCase(),
      favorited: isFavorited,
    };
  }

  function observeInputsAreas() {
    let observed = 0;
    const observationKey = decisions ? 'mpDecisionDropdownObserved' : 'mpDropdownObserved';
    for (let id of INPUTS_AREA_IDS) {
      let area = document.getElementById(id);
      if (!area || area.dataset[observationKey] === '1') {
        continue;
      }
      area.dataset[observationKey] = '1';
      new MutationObserver(() => ensureAttached())
        .observe(area, { childList: true, subtree: true });
      observed++;
    }
    return observed;
  }

  function init() {
    ensureAttached();
    observeInputsAreas();
    // Retry until the inputs areas exist and the select is present. After the
    // observers are wired, mutations will keep us in sync automatically.
    if (!document.getElementById(SELECT_ID) || !document.getElementById(TRIGGER_ID)) {
      setTimeout(init, 500);
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
}
mpInstallModelDropdown('input_mpmodelid', 'mp_model_dropdown_trigger', 'mp_model_dropdown_popover');
mpInstallModelDropdown('input_mpdecisionmodelid', 'mp_decision_model_dropdown_trigger', 'mp_decision_model_dropdown_popover', true);
