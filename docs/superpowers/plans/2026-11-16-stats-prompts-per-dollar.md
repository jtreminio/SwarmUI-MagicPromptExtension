# Stats Prompts per Dollar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Preserve provider usage and display useful prompt and skip counts per dollar without resetting history.

**Architecture:** Extend the existing LiteDB transaction and reset revision mechanism with a usage collection and decimal cost aggregates. Capture usage at the HTTP request boundary; carry cost and usage identity in the existing one-use Skip If receipt. Derive display ratios in the Stats UI.

**Tech Stack:** C# / .NET 8, LiteDB, JavaScript, Bootstrap, existing offline C# and browser fixtures.

**Spec:** `docs/superpowers/specs/2026-11-16-stats-cost-design.md`

## Global Constraints

- Retain raw reported usage, not prompt/response text; exclude evaluator calls.
- Keep latency, old counts, groups and controls; no reset needed.
- Display only Responses and Skipped under Prompts for $1; no coverage or cumulative spend.
- Use decimal charges and independent known-cost denominators; skipped denominator includes eligible passes.
- Preserve reset revisions and exactly-once outcome attribution.

## Review Focus

- Old records missing new fields must remain readable and updatable (Task 1).
- Free, missing, invalid and tiny costs must remain distinct (Tasks 1 and 3).
- An in-flight reset must prevent old usage or decisions repopulating stats (Task 1).
- Response parsing failures must not discard received usage or break generation (Task 2).
- Cache reuse and shared calls must not duplicate usage; narrow screens must remain usable (Tasks 2 and 3).

### Task 1: Persist usage and cost cohorts

**Files:** `ModelStats.cs`, `Tests/ModelStatsChecks.cs`.
**Interfaces:** Extend `CompleteAttempt(Attempt, double?, JObject, string responseJson = null)`; Read adds `costed_completed`, `cost_total`, `costed_skip_completed`, `costed_skipped`, `skip_cost_total`. `usage` collection retains raw usage JSON and request identity/outcome; no API exposes raw records.

- [x] Add offline checks for old rows, raw precision, known/missing/zero/invalid costs, separate skip cohorts, duplicate outcomes, resets and failed calls.
- [x] Run `dotnet run --project Tests/PromptTags.csproj`; confirm the new assertions fail before implementation.
- [x] Implement usage persistence and sums in existing transactions; clear model usage on reset.
- [x] Run the same suite; expect all checks to pass, then commit.

### Task 2: Capture provider responses

**Files:** `WebAPI/LLMAPICalls.cs`, `Tests/ModelStatsChecks.cs`.
**Interfaces:** Pass the received raw response into Task 1's `CompleteAttempt` independently of decision-source capture.

- [x] Add HTTP fixture checks for direct OpenRouter use, billed truncated/failed responses, unsupported provider costs and shared cache calls.
- [x] Run C# suite and observe the missing transport capture failure.
- [x] Capture the response body outside the success-only path and pass it to stats in finally.
- [x] Run the C# suite; expect pass, then commit.

### Task 3: Display and verify the compact metrics

**Files:** `Assets/stats.js`, `Assets/stats.css`, `Tests/stats-cost.test.cjs`, `Tests/stats-ui.browser.js`, `Tests/ui-fixture.html`, `Tests/README.md`.
**Interfaces:** Consume Task 1's independent cost cohorts. Responses/count divided by sum; skips divided by eligible response sum. Unknown = dash, zero-charge cohort = Free, positive fraction below 1 = <1.

- [x] Add real helper tests for ratios, formatting and sorts; extend browser fixture to check displayed numbers, latency and reset behavior.
- [x] Run `node --test Tests/*.test.cjs`; observe missing metric behavior.
- [x] Replace latency column with the two-line metric; move latency below model identity; add cost and latency sorts.
- [x] Run Node suite, C# suite, and Stats browser/preference checks at desktop and narrow widths; inspect screenshots and commit.
- [ ] Request fresh code review, resolve material findings and verify before completion.

## Execution record

- Approved implementation is being executed inline without another approval round. Use a feature branch in the current checkout so the fixture and C# project retain their parent SwarmUI dependencies.
- Existing UI has no latency sort despite the design's wording about retaining it; Task 3 adds it.

- Ruling: Tasks 1 and 2 were implemented and tested together through real HTTP fixtures because provider capture is necessary to exercise storage without an artificial test interface. No product behavior changed from the approved design.
- Backend regression failed first on missing cost accounting (`/tmp/mp-cost-red.log`); all C# checks now pass (`/tmp/mp-cost-green.log`, 272 parser checks plus stats/decision checks). Node regression failed 3/3 before implementation; the full frontend suite passes 37/37 (`/tmp/mp-cost-node.log`). Stats and preference browser checks pass against the isolated fixture.
