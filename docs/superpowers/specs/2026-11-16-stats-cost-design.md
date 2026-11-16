# Stats prompt cost design

Status: approved; subsequent model-name/copy refinement requested by the user.

## Intent and scope

Compare prompt models by observed request cost alongside the existing Skip If and error rates. Preserve existing statistics and start collecting costs on new requests without requiring a reset.

## Selected approach

The user selected a compact column and subsequently requested `Prompts for $1` instead of dollar averages. Stack `Responses` and `Skipped` in that column. Keep the underlying provider-reported cost and token measurements rather than storing only the displayed ratios.

Alternatives considered: separate cost columns improve wide-screen scanning but crowd model names and actions; a separate Costs view hides the relationship between cost and Skip If rates.

## Presentation

- Retain model groups, actions, error rates, Skip If rates, and completed counts.
- Show the human-readable model name with a copy icon immediately after it. Hide the provider/codename line. Copy writes the exact codename, offers keyboard access and visible success/failure feedback, and supports HTTP clipboard fallback. If the catalog has no human-readable name, retain the model ID as the identifying fallback.
- Keep tracking latency. Placement: `Avg response: 16 s` below the model identity, freeing the existing Latency column for costs. Preserve latency sorting.
- Do not show cost coverage counts, cumulative spend, or evaluator costs.
- Display unknown estimates as a dash. For a sample whose reported charges are all zero, display `Free` rather than dividing by zero or pretending to know a finite budget limit. Round display counts appropriately, retaining precision internally; use `<1` for positive estimates below one.
- Preserve responsive labeled rows and accessible labels. Add sorting for the two prompts-per-dollar metrics, with unknown values last. Explain in an accessible tooltip that these are estimates based on observed request charges.

## Metric definitions

`Responses` is the number of completed non-skipped prompt responses with known costs divided by those non-skipped responses' summed charges in USD. Requests without Skip If checks belong here. A response initially counts here; if it later triggers Skip If, its count and charge move to the Skipped group atomically.

`Skipped` is the number of skipped prompt responses with known costs divided by those skipped responses' summed charges in USD. Neither metric includes counts or costs from the opposite group. Example: two non-skipped responses costing $0.008 together give 250 Responses/$1; one skipped response costing $0.002 gives 500 Skipped/$1. An empty group shows unavailable, not free or zero estimated yield.

Both metrics exclude cache reuse, failed requests, and image generation costs. Do not estimate charges from advertised model prices.

## Storage and compatibility

Capture OpenRouter's reported request charge independently of decision evaluation. Persist a per-request usage record containing the original usage object, including reported cost and input/output/total token counts, with optional cache/reasoning details retained when supplied. Associate it with the requested provider/model, available provider request ID, timestamp, completion status, and eventual Skip If outcome. Do not persist prompt or response text as part of this feature, or collect evaluator usage.

Capture usage at the actual provider-call boundary, once per fresh request, without duplicating records for cache reuse or shared in-flight results. Preserve reported precision and distinguish absent fields from zero. Keep internal sums and sample counts for efficient Stats reads; the user's request to omit coverage counts applies to presentation. Failed prompt calls with returned usage may retain their raw measurements but do not enter the completed-response estimates.

Old records remain valid with no known costs. Old completed counts must not enter prompts-per-dollar calculations. Preserve existing exact-once Skip If receipts and reset revision protection, carrying the original response cost into its Skip If outcome. Reset clears the corresponding usage records and new aggregates alongside existing statistics.

Other providers without a supported reported charge show unavailable costs. On first read or update of an older model row, rebuild the separated cost groups from stored usage for its current reset revision, preserving all historical counters. This is a one-time cached migration; pre-cost history cannot be priced.

## Verification

Check raw usage persistence and precision, old-record compatibility, reported zero versus missing cost, mixed known/unknown responses, both ratio formulas, exact skipped-response attribution, cache reuse, and reset behavior. Check the modal at desktop and narrow widths, including sorting and existing model actions.

## Approved implementation choices

Retain latency beneath the model name and use the independent non-skipped and skipped cost groups above (superseding the earlier budget-yield calculation at the user’s request). No changes to how latency is measured. Very large finite estimates use compact/scientific notation. Unrepresentable cost totals display as unavailable while preserving raw usage and request/outcome counters.
