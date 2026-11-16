# Stats prompt cost design

## Intent and scope

Compare prompt models by observed request cost alongside the existing Skip If and error rates. Preserve existing statistics and start collecting costs on new requests without requiring a reset.

## Selected approach

The user selected a compact cost column. Stack `Avg / response` and `Avg / skipped` in one `Prompt cost (USD)` cell. This keeps costs beside Skip If rates without making the modal substantially wider.

Alternatives considered: separate cost columns improve wide-screen scanning but crowd model names and actions; a separate Costs view hides the relationship between cost and Skip If rates.

## Presentation

- Retain model groups, actions, error rates, Skip If rates, and completed counts.
- Keep tracking latency. Proposed placement: `Avg response: 16 s` below the model identity, freeing the existing Latency column for costs. Preserve latency sorting.
- Do not show cost coverage counts, cumulative spend, or evaluator costs.
- Display unknown averages as a dash. Distinguish reported zero from unknown and retain sufficient precision for small nonzero charges.
- Preserve responsive labeled rows and accessible labels. Add sorting for the two cost averages, with unknown values last.

## Metric definitions

`Avg / response` is the sum of provider-reported prompt costs for completed fresh responses divided by the number of those responses with known costs.

`Avg / skipped` uses only completed fresh prompt responses that subsequently trigger a qualifying Skip If rule, and only those with known costs. It measures the original prompt request, excluding evaluator calls.

Both metrics exclude cache reuse, failed requests, and image generation costs. Do not estimate charges from advertised model prices.

## Storage and compatibility

Capture OpenRouter's reported request charge independently of decision evaluation. Keep cost sums and sample counts internally; the user's request to omit coverage counts applies to presentation, since separate denominators are necessary for correct averages.

Old records remain valid with no known costs. Old completed counts must not enter cost-average denominators. Preserve existing exact-once Skip If receipts and reset revision protection, carrying the original response cost into its Skip If outcome. Reset clears new cost aggregates alongside existing statistics.

Other providers without a supported reported charge show unavailable costs. No historical backfill is required.

## Verification

Check old-record compatibility, reported zero versus missing cost, mixed known/unknown responses, exact skipped-response attribution, cache reuse, and reset behavior. Check the modal at desktop and narrow widths, including sorting and existing model actions.

## Remaining design decision

The user requested retaining latency. Its placement below the model identity is the proposed implementation choice for approval; no changes to how latency is measured are proposed.
