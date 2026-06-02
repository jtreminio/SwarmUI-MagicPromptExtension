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

(function () {
  const SELECT_ID = 'input_mpmodelid';
  const TRIGGER_ID = 'mp_model_dropdown_trigger';
  const POPOVER_ID = 'mp_model_dropdown_popover';
  const INPUTS_AREA_IDS = ['main_inputs_area', 'main_inputs_area_hidden', 'simple_inputs_area'];

  /** @type {HTMLDivElement|null} */
  let openPopover = null;
  let attachedSelect = null;
  let labelObserver = null;
  let changeListener = null;

  function ensureAttached() {
    let select = document.getElementById(SELECT_ID);
    if (!select) {
      return;
    }
    let existingTrigger = document.getElementById(TRIGGER_ID);
    if (existingTrigger && existingTrigger.previousElementSibling === select) {
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
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');

    select.parentNode.insertBefore(trigger, select.nextSibling);
    syncTriggerLabel(select, trigger);

    changeListener = () => syncTriggerLabel(select, trigger);
    select.addEventListener('change', changeListener);

    labelObserver = new MutationObserver(() => syncTriggerLabel(select, trigger));
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
    if (opt && opt.value && opt.text) {
      trigger.textContent = opt.text;
      trigger.classList.remove('mp-model-dropdown-empty');
      return;
    }
    if (select.options.length > 0 && select.options[0].text) {
      trigger.textContent = select.options[0].text;
      trigger.classList.remove('mp-model-dropdown-empty');
      return;
    }
    trigger.textContent = '— No models —';
    trigger.classList.add('mp-model-dropdown-empty');
  }

  function closePopover() {
    if (!openPopover) {
      return;
    }
    openPopover.remove();
    openPopover = null;
    document.removeEventListener('mousedown', onDocMouseDown, true);
    document.removeEventListener('keydown', onDocKeyDown, true);
    let trigger = document.getElementById(TRIGGER_ID);
    if (trigger) {
      trigger.setAttribute('aria-expanded', 'false');
    }
  }

  function onDocMouseDown(e) {
    if (openPopover && !openPopover.contains(e.target) && e.target.id !== TRIGGER_ID) {
      closePopover();
    }
  }

  function onDocKeyDown(e) {
    if (e.key === 'Escape') {
      closePopover();
    }
  }

  function openDropdown(select, trigger) {
    closePopover();

    let backendKey = (window.MP && MP.settings && MP.settings.backend) || 'ollama';

    let popover = document.createElement('div');
    popover.id = POPOVER_ID;
    popover.className = 'sui-popover sui_popover_model sui-popover-visible mp-model-dropdown-popover';
    popover.style.position = 'absolute';
    popover.style.zIndex = '2000';

    let search = document.createElement('input');
    search.type = 'text';
    search.placeholder = 'Search...';
    search.className = 'sui_popover_text_input mp-model-dropdown-search';
    popover.appendChild(search);

    let listArea = document.createElement('div');
    listArea.className = 'sui_popover_scrollable_tall mp-model-dropdown-list';
    popover.appendChild(listArea);

    let rows = [];
    let lastFavoritedRow = null;
    for (let i = 0; i < select.options.length; i++) {
      let opt = select.options[i];
      if (!opt.value) {
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

    if (rows.length === 0) {
      let empty = document.createElement('div');
      empty.className = 'mp-model-dropdown-empty-msg';
      empty.textContent = 'No models available. Configure a backend in Settings.';
      listArea.appendChild(empty);
    }

    function applyFilter() {
      let q = search.value.trim().toLowerCase();
      let anyVisible = false;
      for (let r of rows) {
        let visible = !q || r.text.includes(q) || r.value.includes(q);
        r.el.style.display = visible ? '' : 'none';
        if (visible) {
          anyVisible = true;
        }
      }
      let noMatches = listArea.querySelector('.mp-model-dropdown-no-matches');
      if (rows.length > 0 && !anyVisible) {
        if (!noMatches) {
          noMatches = document.createElement('div');
          noMatches.className = 'mp-model-dropdown-no-matches';
          noMatches.textContent = 'No matches';
          listArea.appendChild(noMatches);
        }
      } else if (noMatches) {
        noMatches.remove();
      }
    }
    search.addEventListener('input', applyFilter);
    search.addEventListener('keydown', (e) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        closePopover();
      }
    });

    document.body.appendChild(popover);
    positionPopover(popover, trigger);
    openPopover = popover;
    trigger.setAttribute('aria-expanded', 'true');

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
    if (isFavorited) {
      row.classList.add('mp-model-dropdown-row-favorited');
    }

    let label = document.createElement('span');
    label.className = 'mp-model-dropdown-row-label';
    label.textContent = opt.text;
    row.appendChild(label);

    let starBtn = document.createElement('button');
    starBtn.type = 'button';
    starBtn.className = 'mp-model-dropdown-star';
    starBtn.textContent = isFavorited ? '★' : '☆';
    starBtn.title = isFavorited ? 'Remove from favorites' : 'Add to favorites';
    starBtn.setAttribute('aria-label', starBtn.title + ': ' + opt.text);
    if (isFavorited) {
      starBtn.classList.add('mp-model-dropdown-star-active');
    }
    row.appendChild(starBtn);

    let removeBtn = document.createElement('button');
    removeBtn.type = 'button';
    removeBtn.className = 'mp-model-dropdown-remove';
    removeBtn.title = 'Block this model (adds to block list)';
    removeBtn.setAttribute('aria-label', 'Block ' + opt.text);
    removeBtn.textContent = '×';
    row.appendChild(removeBtn);

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
      if (starBtn.classList.contains('mp-model-dropdown-star-active')) {
        if (typeof MP.unfavoriteModelId === 'function') {
          MP.unfavoriteModelId(backendKey, id);
        }
        starBtn.classList.remove('mp-model-dropdown-star-active');
        starBtn.textContent = '☆';
        starBtn.title = 'Add to favorites';
        row.classList.remove('mp-model-dropdown-row-favorited');
        if (opt.dataset) {
          delete opt.dataset.favorited;
        }
      } else {
        if (typeof MP.favoriteModelId === 'function') {
          MP.favoriteModelId(backendKey, id);
        }
        starBtn.classList.add('mp-model-dropdown-star-active');
        starBtn.textContent = '★';
        starBtn.title = 'Remove from favorites';
        row.classList.add('mp-model-dropdown-row-favorited');
        if (opt.dataset) {
          opt.dataset.favorited = '1';
        }
      }
      // Reordering happens on the next dropdown open (after persist + select
      // rebuild). Keeping the row in place avoids surprising layout jumps
      // while the user toggles several stars in a row.
    });

    removeBtn.addEventListener('click', (e) => {
      e.preventDefault();
      e.stopPropagation();
      let id = opt.value;
      let wasSelected = select.value === id;

      if (window.MP && typeof window.MP.blockModelId === 'function') {
        window.MP.blockModelId(backendKey, id);
      }

      if (opt.parentNode) {
        opt.parentNode.removeChild(opt);
      }
      row.remove();

      if (wasSelected) {
        let firstOption = null;
        for (let i = 0; i < select.options.length; i++) {
          if (select.options[i].value) {
            firstOption = select.options[i];
            break;
          }
        }
        if (firstOption) {
          select.value = firstOption.value;
          if (typeof triggerChangeFor === 'function') {
            triggerChangeFor(select);
          }
        }
        syncTriggerLabel(select, trigger);
      }
    });

    row.addEventListener('click', (e) => {
      if (
        e.target === removeBtn || removeBtn.contains(e.target) ||
        e.target === starBtn || starBtn.contains(e.target)
      ) {
        return;
      }
      select.value = opt.value;
      if (typeof triggerChangeFor === 'function') {
        triggerChangeFor(select);
      }
      syncTriggerLabel(select, trigger);
      closePopover();
    });

    return {
      el: row,
      text: (opt.text || '').toLowerCase(),
      value: (opt.value || '').toLowerCase(),
      favorited: isFavorited,
    };
  }

  function observeInputsAreas() {
    let observed = 0;
    for (let id of INPUTS_AREA_IDS) {
      let area = document.getElementById(id);
      if (!area || area.dataset.mpDropdownObserved === '1') {
        continue;
      }
      area.dataset.mpDropdownObserved = '1';
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
})();
