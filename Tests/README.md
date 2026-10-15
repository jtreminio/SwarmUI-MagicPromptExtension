Run the C# prompt parsing regression checks against an existing SwarmUI build:

```sh
dotnet run --project Tests/PromptTags.csproj
```

The default binary directory is SwarmUI's `src/bin/live_release`. Override it with `-p:SwarmBinaryDirectory=/absolute/path/to/swarm/bin` when needed. The checks compile the extension sources and use SwarmUI's actual prompt parser without calling an LLM or image backend.

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
```

The fixture mocks all API calls and uses a separate browser origin. It does not load or change real user settings. Checks cover instruction creation/editing, cancel, failed saves and retries, model searches and preference exclusivity, Escape handling, repeated modal cycles, prompt history actions and keyboard activation, and mobile overflow. The C# checks also cover provider migration and supported request schemas.

The fixture includes SwarmUI’s real dropdown event handlers to verify the default model uses the native menu without duplicate dropdowns. Preference picker checks cover search, checkboxes, chips, Clear, matching theme colors, keyboard control, and mobile placement. Model-order tests check newest-first OpenRouter dates, pinned favorites, stable ties, and unchanged local catalog order.

Model-order browser checks exercise both preference lists and their selected chips with deliberately conflicting names and dates, stable ties, missing catalog IDs, search filtering, and local provider order.

Model-style checks load SwarmUI’s generation-page stylesheet and compare actual menu, search, and row styles across the sidebar, default model, favorites, and blocked models. They also verify favorite stars and native selection.
