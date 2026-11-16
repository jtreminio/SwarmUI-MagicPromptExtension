Run the C# prompt parsing regression checks against an existing SwarmUI build:

```sh
dotnet run --project Tests/PromptTags.csproj
```

The default binary directory is SwarmUI's `src/bin/live_release`. Override it with `-p:SwarmBinaryDirectory=/absolute/path/to/swarm/bin` when needed. The checks compile the extension sources and use SwarmUI's actual prompt parser without calling an LLM or image backend.

To verify per-image Skip If handling against the current SwarmUI engine source before rebuilding the host:

```sh
MP_TEST_DECISION_BATCH=1 dotnet run --project Tests/PromptTags.csproj -p:UseSwarmEngineSource=true
```

This source-linked check intentionally replaces the engine type from the existing binary (compiler warning CS0436). Running the batch check against an older binary without the core `SwarmUI.SkipGeneration` handler fails; rebuild SwarmUI to apply that existing core fix. `MP_TEST_DECISION_CATALOG=1` optionally verifies the public OpenRouter model-list endpoint; it does not call inference or require an API key.

Run the frontend regression checks:

```sh
node --test Tests/*.test.cjs
```

For browser regression checks, serve the isolated fixture (requires the parent SwarmUI checkout for Bootstrap, dropdown handlers, and theme assets):

```sh
python3 Tests/ui-fixture.py
playwright-cli -s=magicprompt-tests open http://127.0.0.1:8767 --browser=chromium
playwright-cli -s=magicprompt-tests run-code --filename=Tests/dropdowns-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/model-order-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/model-style-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/settings-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/provider-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/decision-models-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/decision-library-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/decision-generate-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/decision-picker-review-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/decision-question-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/stats-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/stats-preferences-ui.browser.js
playwright-cli -s=magicprompt-tests run-code --filename=Tests/stats-copy-ui.browser.js
```

The fixture mocks all API calls and uses a separate browser origin. It does not load or change real user settings. Checks cover instruction creation/editing, cancel, failed saves and retries, model searches and preference exclusivity, Escape handling, repeated modal cycles, prompt history actions and keyboard activation, and mobile overflow. The C# checks also cover provider migration and supported request schemas.

The fixture includes SwarmUI’s real dropdown event handlers to verify the default model uses the native menu without duplicate dropdowns. Preference picker checks cover search, checkboxes, chips, Clear, matching theme colors, keyboard control, and mobile placement. Model-order tests check newest-first OpenRouter dates, pinned favorites, stable ties, and unchanged local catalog order.

Model-order browser checks exercise both preference lists and their selected chips with deliberately conflicting names and dates, stable ties, missing catalog IDs, search filtering, and local provider order.

Model-style checks load SwarmUI’s generation-page stylesheet and compare actual menu, search, and row styles across the sidebar, default model, favorites, and blocked models. They also verify favorite stars and native selection.

OpenRouter decision checks use offline fixtures for the native typed payload/response, malformed answers and evaluator refusals, input/request pricing, model catalog filtering, legacy settings migration, and independent model preferences. They never call paid inference. Browser checks cover independent decision preferences, the native searchable model menu, cancel/save/reopen, provider switching, and mobile placement.

Decision parity checks cover the Generate model override and independent preset refresh, shared model-menu stars/blocks, parameter rebuilding, and the Decisions library layout/search/editor/save/cancel/import/export behavior.

Picker review checks cover keyboard model selection and default recovery, visible and accessible blocked-selection warnings, stale preset isolation, and case-insensitive decision preferences. The C# catalog check verifies preference saves retain the public catalog while applying the updated favorites and blocks.

Stats browser checks cover catalog ordering and independent loading, both Skip If windows and sort directions, unavailable versus zero metrics, prompt preference persistence and failures, reset permission and stale refreshes, keyboard focus, and narrow-screen layout.

Cost checks also cover raw usage precision, missing/invalid/free charges, historical compatibility, known-cost response and Skip If cohorts, billed parser failures, concurrent usage, cache reuse, and reset races. Frontend checks verify prompts per dollar, unknown/free/zero displays, cost and response-time sorts, latency placement, and responsive Stats layout. All provider responses are offline fixtures; no paid inference is used.

Stats copy checks exercise the real clipboard, keyboard activation, the HTTP fallback inside the modal focus trap, clipboard denial, and very large finite estimates at mobile and desktop widths. Accounting regressions cover invalid optional UTF-16 metadata and decimal aggregate overflow without losing successful requests or raw usage.
