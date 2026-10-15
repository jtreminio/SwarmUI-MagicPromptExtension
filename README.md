# MagicPrompt

Prompt rewriting tools integrated into SwarmUI’s Generate tab. This fork focuses on prompt tags, custom instructions, model preferences, caching, and prompt history.

Supported providers are **OpenRouter** and **OpenAI-compatible local APIs**. There is no dedicated MagicPrompt page, chat, vision mode, image captioning, Enhance Prompt button, or Magic Vision button. Ollama’s native API and direct OpenAI/ChatGPT integration are no longer supported.

## Setup

Install this extension in `SwarmUI/src/Extensions`, restart SwarmUI, and reload the browser. Open **Magic Prompt Auto Enable → MagicPrompt Settings** in the Generate sidebar.

1. **Provider:** choose OpenRouter or a local API. For a local API, enter the server’s base URL. Set the request timeout.
2. **API keys:** use **Open API keys** to open SwarmUI’s User → User Info section. OpenRouter requires a key; local APIs can use an optional key.
3. **Models:** refresh the catalog and choose a default model. OpenRouter models appear newest first in every model selector, including the favorite and blocked lists and their selected chips. Favorites are pinned above other models in the default and generation dropdowns. The default model uses SwarmUI’s native searchable dropdown. Favorites and blocked models use WhatTheDuck-style searchable checkbox pickers with removable chips, a selected count, and Clear. Blocked models are excluded from the default and favorite dropdowns. Blocking the current default clears it; unblock a model before adding it to favorites. Preferences apply to the selected provider.
4. **Instructions:** search the library and edit instructions in place. Create, import, export, or delete custom instructions. **Draft with AI** uses the saved provider and default model; review its output before saving.
5. **Save and close** applies all edits. Cancel, Escape, or closing the dialog discards unsaved edits. Import and deletion also take effect only after saving.

The generation sidebar retains its current model and instruction selections when catalogs or preferences change. Choose the desired model under **MP Model ID** for each batch. Model prices, where available, show USD per million output tokens.

Existing custom instruction IDs and content are preserved. Saved OpenRouter/local configuration and model preferences remain available. A retired provider selection falls back to OpenRouter with an empty default model; select a supported provider and model before generating. Legacy built-in instruction text remains stored for compatibility with existing prompt tags.

## Usage

Select **MP Model ID** and **MP Instructions**, then include `<mpprompt:your prompt>` in the Generate prompt box. The model response replaces the tag during generation. **Refine Img**, response chaining, variables, post-filters, and wildcard seed controls remain available.

**Prompt History** opens the last 100 unique raw prompts. Search entries, use or append a prompt, copy it, or delete it. Buttons support keyboard activation. Clear All requires a second click within five seconds. History is stored in the current browser.

### Generate Tab Integration
MagicPrompt integrates with SwarmUI’s prompt parser for generation and batch processing.

* **Automatic Parameter Group**: A new "Magic Prompt Auto Enable" parameter group appears in the sidebar interface when the extension is installed.
* **Prompt Tag Processing**: Use `<mpprompt:your prompt here>` tags directly in your prompts for automatic LLM processing during generation. Only what is in this tag will be sent to your LLM. The response will replace the tag.
  * **Literal Angle Brackets**: Emotes such as `>:(` and `<8)` work directly: `<mpprompt:angry >:( and wearing a hat <8)>`. For arbitrary literal `>` characters, append `</mpprompt>` after the final closing `>`, for example `<mpprompt:angry >:(, wearing hat <8), 5 > 3></mpprompt>`. The complete `></mpprompt>` delimiter ends the content and is not sent to the LLM. Response chaining uses the same form: `<mpprompt[Tag-Based]:<mpresponse:0>></mpprompt>`. Text after the delimiter stays outside the tag. Nested Swarm tags, including wildcards and variables, still expand before the LLM request. Expanded text can contain either angle bracket without escaping. A backslash before `<` or `>` also makes that bracket literal.
* **Per-Tag Instruction**: Choose which instruction set a tag uses with `<mpprompt[InstructionName]:your prompt here>`. If omitted, the globally selected "MP Instructions" is used.
* **Per-Tag Model**: Choose which LLM a tag uses as the second parameter: `<mpprompt[InstructionName, ModelName]:your prompt here>`. The existing `|ModelName` form is also supported: `<mpprompt[InstructionName|ModelName]:your prompt here>`. Leave the instruction blank to keep the default instruction while overriding only the model: `<mpprompt[|ModelName]:your prompt here>`. If no model is specified, the globally selected "MP Model ID" is used. (The global "MP Model ID" must still be set — it acts as the on/off switch and default; per-tag models override it.) The image metadata's "MP Model ID" is updated to reflect the model(s) actually used (multiple distinct models are joined with `, `), so it matches what ran rather than just the dropdown selection.
  * **Model Autocomplete**: After typing `<mpprompt[InstructionName, `, suggestions show the current "MP Model ID" dropdown's models in the same order. Type part of a model name or ID to filter the list; selecting a suggestion inserts its model ID.
  * **Random Model**: Use `random` as the model name to pick a random model from your non-blocked list for that tag: `<mpprompt[|random]:your prompt here>`. The choice is tied to the wildcard seed, so it stays consistent (and cache-friendly) within a batch unless you enable "MP Generate Wildcard Seed".
* **Suppressing Output (for chaining)**: Set any of the first three parameters in the instruction section to `false` to make the LLM request but NOT insert the response into the final prompt. The response is still available via `<mpresponse:N>`.
  * Examples: `<mpprompt[false]:some tags>`, `<mpprompt[Action, false]:some tags>`, or `<mpprompt[Action, random, false]:some tags>`. Then reference the hidden response later with `<mpresponse:0>`.
* **Attaching the Original Prompt**: Use `<mporiginal>` to add your original, unmodified tags back to your prompt. This can useful if your model works better with the original tag-based prompt.
* **Smart Caching**: LLM responses are always cached. With "MP Use Cache" enabled, matching prompts can reuse a response across image seeds, significantly improving performance and reducing API costs during batch generation. With it disabled, reuse requires the normalized prompt, image seed, and LLM model to match, which avoids broad reuse while still letting an exact image regeneration reuse its prior LLM response. Use alongside the Wildcard Seed Generation feature below for more powerful caching! Only one request per matching cache identity is ever in flight — the rest of the batch waits on it. If that request fails, the waiting images do not all fail with it: one of them takes over and retries (still one request at a time), for a maximum of 2 LLM calls per cache identity. The count resets once the batch is done, so a later generate starts fresh.
* **Error Handling**: The "MP On Error" parameter controls what happens when an LLM request fails (error response, timeout, or empty reply).
  * LLM failures are logged to the console instead of displayed as frontend popups.
  * *Skip Generation* (default): that image is cancelled before any backend is claimed, so no GPU time is wasted and no junk image is produced. **Enable SwarmUI's "Continue After Errors" parameter** if you want the rest of the batch to keep going — without it, SwarmUI cancels the whole queue on the first failure.
  * *Use Original Prompt*: generates anyway, using the raw text inside the `<mpprompt:...>` tag (the behavior prior to this parameter existing).
* **Disable LLM Request**: This checkbox below "MP On Error" skips LLM requests and cached responses while still applying "MP Post-Filter" to the original text inside each `<mpprompt:...>` tag. Response references and output suppression still work using that filtered text. With no tags, filters apply to the whole prompt. No model selection is required. Unchecked by default.
* **Wildcard Seed Generation**: The "MP Generate Wildcard Seed" parameter generates new wildcard seeds for each batch, perfect for creating varied results while reusing cached LLM responses. If your prompts contain `<wildcard>` or `<wc>` tags, and you run a batch of multiple images, the same wildcard selections will be chosen, ensuring your prompts are the same and the LLM is called only once per batch.
* **Model Selection**: Choose different LLM models for each batch using the "MP Model ID" parameter.
* **Instruction Selection**: Select custom instructions for each batch using the "MP Instructions" parameter.
* **Prompt History**: A "Prompt History" button in the Magic Prompt sidebar group opens a modal listing the last 100 unique prompts you generated with. Entries are the *raw* prompt exactly as you typed it — wildcards, variables and `<mpprompt:...>` tags are stored unexpanded, so any entry can be loaded back and re-run as-is. Uniqueness is checked on a trimmed, lowercased copy of the prompt, so re-running the same prompt just moves it back to the top instead of adding a duplicate. From the modal you can filter the list, load an entry into the prompt box (replacing or appending), copy it, delete a single entry, or clear the whole history. History is stored in your browser's localStorage, so it is per-browser and is lost if you clear site data.
* **Thinking Level**: Control how much thinking/reasoning effort the LLM uses with the "MP Thinking" parameter (None/Low/Medium/High, default None). None disables thinking where the backend supports an off switch. Levels map per backend: OpenRouter reasoning effort, local API `reasoning_effort`. Levels only apply to reasoning-capable models; some backends reject unsupported levels with an error.

## Troubleshooting

- If models fail to load, check the provider key or local server URL, then refresh the catalog.
- A failed settings save keeps the dialog open with your edits and an error message. Retry after fixing the connection.
- LLM generation failures are logged to the console. Use **MP On Error** to choose whether to skip generation or use the original prompt.
- After installing this redesign, restart SwarmUI and reload the browser so the removed tab and old scripts are unloaded.

## Development

See [Tests/README.md](Tests/README.md) for frontend and C# regression checks.

## License and attribution

Licensed under the [MIT License](LICENSE). Originally forked from HartsyAI’s SwarmUI-MagicPromptExtension. Original copyright notices remain in LICENSE.
