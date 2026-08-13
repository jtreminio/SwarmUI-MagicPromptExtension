/**
 * prompthistory.js
 * Tracks the last 100 unique raw prompts sent to SwarmUI and shows them in a modal.
 *
 * "Raw" means the exact text the user had in the prompt box when they hit Generate - wildcards, variables
 * and mpprompt tags are all recorded unexpanded, so an entry can be dropped straight back into the prompt
 * box and re-run. Uniqueness is decided on a trimmed + lowercased copy; the stored text keeps the original.
 *
 * Storage is localStorage (per browser, like the extension's other UI state). 100 prompts is far below the
 * ~5MB per-origin budget, but a write that gets refused still degrades gracefully by dropping oldest entries.
 */

'use strict';

(function () {
    const STORAGE_KEY = 'magicprompt_prompt_history';
    const MAX_ENTRIES = 100;
    const MODAL_ID = 'promptHistoryModal';
    const LIST_ID = 'promptHistoryList';
    const FILTER_ID = 'promptHistoryFilter';
    const EMPTY_ID = 'promptHistoryEmpty';
    const COUNT_ID = 'promptHistoryCount';
    const CLEAR_BTN_ID = 'promptHistoryClearBtn';
    /** A prompt already sitting at the top of the list only gets its timestamp rewritten this often. */
    const TOUCH_INTERVAL_MS = 60_000;

    const PromptHistory = {
        maxEntries: MAX_ENTRIES,
        /** Current filter text (lowercased). */
        filter: '',
        /** The filtered rows currently rendered, so click handlers can map a row index back to an entry. */
        visible: [],
        /** True once the Clear All button is armed and waiting for a second click. */
        clearArmed: false,

        /** The uniqueness key for a prompt: trimmed and lowercased. */
        normalize(prompt) {
            return (prompt || '').trim().toLowerCase();
        },

        /** Reads the stored history, dropping malformed or duplicate rows. Newest use first: `[{prompt, at}]`. */
        load() {
            let parsed;
            try {
                parsed = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
            } catch (error) {
                console.error('MagicPrompt: prompt history was unreadable, starting fresh:', error);
                return [];
            }
            if (!Array.isArray(parsed)) {
                return [];
            }
            const seen = new Set();
            const cleaned = [];
            for (const entry of parsed) {
                const prompt = entry?.prompt;
                if (typeof prompt !== 'string' || !prompt.trim()) {
                    continue;
                }
                const key = this.normalize(prompt);
                if (seen.has(key)) {
                    continue;
                }
                seen.add(key);
                cleaned.push({ prompt: prompt, at: typeof entry.at === 'number' ? entry.at : 0 });
                if (cleaned.length >= MAX_ENTRIES) {
                    break;
                }
            }
            return cleaned;
        },

        /**
         * Writes the history back. If the browser refuses the write (quota), the oldest entries are dropped
         * and it retries rather than losing the whole list.
         * @param {Array} entries Entries to store, newest first.
         * @returns {boolean} True if the write landed.
         */
        save(entries) {
            let toSave = entries.slice(0, MAX_ENTRIES);
            while (toSave.length > 0) {
                try {
                    localStorage.setItem(STORAGE_KEY, JSON.stringify(toSave));
                    return true;
                } catch (error) {
                    console.warn(`MagicPrompt: could not store ${toSave.length} history entries, trimming:`, error);
                    toSave = toSave.slice(0, Math.floor(toSave.length / 2));
                }
            }
            try {
                localStorage.removeItem(STORAGE_KEY);
            } catch (error) {
                console.error('MagicPrompt: could not clear prompt history:', error);
            }
            return false;
        },

        /**
         * Records a prompt as most recently used, moving an existing match back to the top instead of
         * storing it twice.
         * @param {string} prompt Raw, unexpanded prompt text.
         */
        record(prompt) {
            const key = this.normalize(prompt);
            if (!key) {
                return;
            }
            const entries = this.load();
            const existingIndex = entries.findIndex(entry => this.normalize(entry.prompt) === key);
            // Generate Forever re-runs the same prompt many times a second - don't rewrite storage for each one.
            if (existingIndex === 0 && Date.now() - entries[0].at < TOUCH_INTERVAL_MS) {
                return;
            }
            if (existingIndex >= 0) {
                entries.splice(existingIndex, 1);
            }
            entries.unshift({ prompt: prompt.trim(), at: Date.now() });
            this.save(entries);
            if (this.isModalOpen()) {
                this.render();
            }
        },

        remove(prompt) {
            const key = this.normalize(prompt);
            this.save(this.load().filter(entry => this.normalize(entry.prompt) !== key));
            this.render();
        },

        clear() {
            this.save([]);
            this.render();
        },

        /** Loads an entry into the Generate tab's prompt box, replacing or appending to what's there. */
        applyToPromptBox(prompt, append) {
            const promptBox = document.getElementById('alt_prompt_textbox');
            if (!promptBox) {
                showError('Could not find the prompt box.');
                return;
            }
            if (append && promptBox.value.trim()) {
                promptBox.value = `${promptBox.value.replace(/\s+$/, '')} ${prompt}`;
            }
            else {
                promptBox.value = prompt;
            }
            triggerChangeFor(promptBox);
            // The prompt box lives on the Generate tab, so only focus it when that tab is actually showing.
            if (promptBox.offsetParent !== null) {
                promptBox.focus();
                promptBox.setSelectionRange(promptBox.value.length, promptBox.value.length);
            }
        },

        isModalOpen() {
            return document.getElementById(MODAL_ID)?.classList.contains('show') === true;
        },

        /** "3 minutes ago" style stamp for an entry's last use. */
        describeAge(timestampMs) {
            if (!timestampMs) {
                return '';
            }
            const seconds = Math.max(0, Math.floor((Date.now() - timestampMs) / 1000));
            if (seconds < 10) {
                return 'just now';
            }
            const steps = [
                [60, 'second'],
                [60, 'minute'],
                [24, 'hour'],
                [7, 'day'],
                [4, 'week'],
                [12, 'month'],
                [Infinity, 'year']
            ];
            let value = seconds;
            for (const [size, label] of steps) {
                if (value < size) {
                    const rounded = Math.floor(value);
                    return `${rounded} ${label}${rounded === 1 ? '' : 's'} ago`;
                }
                value /= size;
            }
            return '';
        },

        render() {
            const list = document.getElementById(LIST_ID);
            if (!list) {
                return;
            }
            const entries = this.load();
            const filtered = this.filter
                ? entries.filter(entry => entry.prompt.toLowerCase().includes(this.filter))
                : entries;
            this.visible = filtered;
            const count = document.getElementById(COUNT_ID);
            if (count) {
                count.textContent = entries.length
                    ? `${filtered.length} of ${entries.length} (max ${MAX_ENTRIES})`
                    : '';
            }
            list.innerHTML = filtered.map((entry, index) => `
                <div class="mp-history-item" data-index="${index}" tabindex="0" role="button" title="${escapeHtmlNoBr(entry.prompt)}">
                    <div class="mp-history-item-body">
                        <div class="mp-history-text">${escapeHtmlNoBr(entry.prompt)}</div>
                        <div class="mp-history-meta">${escapeHtmlNoBr(this.describeAge(entry.at))}</div>
                    </div>
                    <div class="mp-history-actions">
                        <button type="button" class="mp-history-action" data-action="use" title="Replace the prompt box with this">📝</button>
                        <button type="button" class="mp-history-action" data-action="append" title="Append this to the prompt box">➕</button>
                        <button type="button" class="mp-history-action" data-action="copy" title="Copy to clipboard">📋</button>
                        <button type="button" class="mp-history-action danger" data-action="delete" title="Remove from history">🗑️</button>
                    </div>
                </div>`).join('');
            const empty = document.getElementById(EMPTY_ID);
            if (empty) {
                empty.hidden = filtered.length > 0;
                empty.textContent = entries.length
                    ? 'No prompts match your filter.'
                    : 'No prompts yet. Generate an image and it will show up here.';
            }
        },

        handleListClick(event) {
            const item = event.target.closest('.mp-history-item');
            if (!item) {
                return;
            }
            const entry = this.visible[parseInt(item.dataset.index, 10)];
            if (!entry) {
                return;
            }
            const action = event.target.closest('.mp-history-action')?.dataset.action || 'use';
            switch (action) {
                case 'append':
                    this.applyToPromptBox(entry.prompt, true);
                    this.close();
                    break;
                case 'copy':
                    navigator.clipboard?.writeText(entry.prompt).catch(error => showError(`Failed to copy: ${error}`));
                    break;
                case 'delete':
                    this.remove(entry.prompt);
                    break;
                default:
                    this.applyToPromptBox(entry.prompt, false);
                    this.close();
                    break;
            }
        },

        /** Clearing wipes everything, so the button arms on the first click and only clears on the second. */
        handleClearClick() {
            if (!this.clearArmed) {
                const button = document.getElementById(CLEAR_BTN_ID);
                this.clearArmed = true;
                if (button) {
                    button.classList.add('armed');
                    button.textContent = 'Click again to confirm';
                }
                setTimeout(() => this.disarmClear(), 5000);
                return;
            }
            this.disarmClear();
            this.clear();
        },

        disarmClear() {
            const button = document.getElementById(CLEAR_BTN_ID);
            this.clearArmed = false;
            if (button) {
                button.classList.remove('armed');
                button.textContent = 'Clear All';
            }
        },

        show() {
            const modal = document.getElementById(MODAL_ID);
            if (!modal || modal.classList.contains('show')) {
                return;
            }
            this.filter = '';
            const filterInput = document.getElementById(FILTER_ID);
            if (filterInput) {
                filterInput.value = '';
            }
            this.disarmClear();
            this.render();
            (bootstrap.Modal.getInstance(modal) || new bootstrap.Modal(modal)).show();
            filterInput?.focus();
        },

        close() {
            const modal = document.getElementById(MODAL_ID);
            if (!modal) {
                return;
            }
            try {
                bootstrap.Modal.getInstance(modal)?.hide();
            } catch (error) {
                console.error('Error closing prompt history modal:', error);
            }
        },

        bindModal() {
            const list = document.getElementById(LIST_ID);
            if (!list || list.dataset.mpBound === 'true') {
                return;
            }
            list.dataset.mpBound = 'true';
            list.addEventListener('click', event => this.handleListClick(event));
            list.addEventListener('keydown', event => {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    this.handleListClick(event);
                }
            });
            document.getElementById(FILTER_ID)?.addEventListener('input', event => {
                this.filter = event.target.value.trim().toLowerCase();
                this.render();
            });
            document.getElementById(CLEAR_BTN_ID)?.addEventListener('click', () => this.handleClearClick());
        },

        /**
         * Wraps the Generate handler so every real generation records its prompt. The prompt is read from the
         * collected request payload, which is the raw text as sent to the server. Previews are skipped - they
         * fire on every keystroke and are not prompts the user chose to run.
         */
        hookGenerateHandler() {
            if (typeof mainGenHandler === 'undefined' || !mainGenHandler || mainGenHandler.mpHistoryHooked) {
                return;
            }
            mainGenHandler.mpHistoryHooked = true;
            const original = mainGenHandler.doGenerate.bind(mainGenHandler);
            const self = this;
            mainGenHandler.doGenerate = function (input_overrides = {}, input_preoverrides = {}, postCollectRun = null) {
                const isPreview = '_preview' in input_overrides;
                return original(input_overrides, input_preoverrides, actualInput => {
                    try {
                        if (!isPreview) {
                            self.record(actualInput?.prompt);
                        }
                    } catch (error) {
                        console.error('Failed to record prompt history:', error);
                    }
                    if (postCollectRun) {
                        postCollectRun(actualInput);
                    }
                });
            };
        }
    };

    /**
     * Adds a "Prompt History" button into the Magic Prompt group in the Generate tab's left sidebar,
     * next to the Settings link.
     * @returns {void}
     */
    function addSidebarHistoryLink() {
        const linkId = 'magicprompt_sidebar_history_link';
        const build = () => {
            const group = document.getElementById('input_group_content_magicpromptautoenable');
            if (!group || document.getElementById(linkId)) {
                return;
            }
            group.append(createDiv(linkId, 'keep_group_visible',
                `<button type="button" class="basic-button" onclick="showPromptHistoryModal()">📜 Prompt History</button>`));
        };
        if (typeof postParamBuildSteps !== 'undefined') {
            postParamBuildSteps.push(build);
        }
        build();
    }

    document.addEventListener('DOMContentLoaded', () => {
        // The modal markup lives in the MagicPrompt tab-pane, which is display:none while the Generate tab is
        // active. Move it to <body> (as magicprompt.js does for the other modals) so it renders from anywhere.
        const modal = document.getElementById(MODAL_ID);
        if (modal && modal.parentElement !== document.body) {
            document.body.appendChild(modal);
        }
        PromptHistory.bindModal();
        PromptHistory.hookGenerateHandler();
        addSidebarHistoryLink();
    });

    window.MP = window.MP || {};
    window.MP.PromptHistory = PromptHistory;
    window.showPromptHistoryModal = () => PromptHistory.show();
    window.closePromptHistoryModal = () => PromptHistory.close();
})();
