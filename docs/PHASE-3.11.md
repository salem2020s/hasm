# HASM - Phase 3.11: Pricing and discount engine

Sources: SRS 8.1-8.4 (several price lists and deterministic, configurable priority rules by customer and customer group, quantity, unit, currency and effective date; a clear sequence for resolving conflicts between prices and discounts with the chosen rule recorded; authorized manual override of a price or discount with a reason; margin monitoring with minimum limits or an exceptional approval according to policy; price, discount and related cost/tax stored as historical snapshots on issued invoices, a later list change never re-prices a posted document), SRS 4.2. Roadmap 3.11 needs 3.4 (items, merged) and 3.9B (customers, PR #56) for the persistence slice.

## Slice plan

1. **Pure engine and snapshot (this branch).** `Pricing/PricingModel.cs`, `PricingEngine.cs`, `PricingSnapshot.cs`: no database, clock, culture or floating point.
2. Persistence (price lists, entries, discount rules, policy; revision-fenced, never deleted, audited, generated opaque names) and the resolution service that feeds the engine - needs 3.9B for the customer and customer-group references.
3. Management API and the shared Web/Tauri screens with Tabs (price lists, entries, discounts, policy) and the override permission.

## Engine contract (slice 1)

* **Price selection (deterministic).** Eligible lists: active, in date, same currency (case-insensitive), scope customer / customer group / general (a list with both set is invalid data). Order: scope specificity (customer, group, general), then list priority (lower first), then the later start, then code, then id; the input order never matters. The FIRST list holding a matching entry wins: item entry before item-group entry, exact unit, the highest quantity tier not above the quantity (a tier starts at its minimum). A list that cannot price the line is skipped and the skip is traced. No eligible price is a refusal (`no_price`): there is never an implicit zero. Two entries for the same tier are `ambiguous_price`.
* **Discount selection (no stacking).** Applicable rules: active, in date, scope matches, quantity at or above the minimum. Order: customer specificity (customer, group, general), then item specificity (item, item group, general), then priority (lower first), higher minimum quantity, later start, code, id. Exactly ONE discount applies; the losers are traced. Rules identical in every key before the code are `ambiguous_discount`. Stacking is deliberately not supported in this slice (a reversible default; stacking would need an explicit sequence rule from the owner).
* **Arithmetic.** net unit price = round(list price x (100 - discount) / 100, price decimals); line net = round(net unit price x quantity, money decimals); both half away from zero, once each (the printed unit price is what multiplies). Decimals 0-6; price up to 6 decimals, discount up to 4, quantity up to 10.
* **Override.** An authorized manual override (the permission is the caller's) replaces the selected price and/or discount; a reason (1-500 characters) is mandatory; what the rules chose stays in the trace; the override cannot bypass the margin control.
* **Margin control.** With a minimum margin set: below it, Warn reports, RequireApproval flags the line for an exceptional approval, Block refuses (`margin_below_minimum`); a missing cost is refused (`cost_unavailable`) under RequireApproval and Block and reported (not evaluated) under Warn, because a control must never skip itself silently. A free line with a cost is reported as a -100 % margin.
* **Snapshot** `pricing-snapshot-v1`: stores VALUES (policy, request, the selected price and discount with their opaque rule references and the trace, the override, the result) plus a SHA-256 over the canonical, scale-normalized JSON; `Verify` recomputes the arithmetic from the stored values, so a later list or rule change can never alter or invalidate it, and tampering is detected. The hash is tamper evidence, not authentication.

## Tests

12 unit tests: price list ordering matrix, skip tracing and eligibility, quantity tiers and item-before-group, refusals, discount specificity matrix without stacking, ambiguity and data refusals, rounding, override rules, margin actions, snapshot sealing and tamper detection, order independence over randomized input orders. No migration and no API in this slice.

## Decisions and review follow-up (docs/review-records/PR-3-11-pricing-slice1.md)

* **Units and currency.** The quantity is expressed in the unit being priced (an entry exists per unit; conversion between units is the caller's job before pricing); the price currency is the document currency (a list is in one currency; conversion is the document framework's job); `UnitCost` must be in the same unit and currency. Reversible; revisit with the document framework (3.13).
* **Override** results name `manual-override` for the replaced price or discount; the replaced rule and value stay in the trace. **Resolution integrity:** the price rule reference is mandatory and a discount needs its rule reference. **Exactness:** the line amount is computed on integers and rounded once. **Snapshot v1** is defined by an explicit field list and pinned by a golden hash; adding a field requires a new snapshot version. Tests: 17.

## Owner decision P - resolved by the owner directive of 2026-11-20: company-configurable precedence (slice 2a, implemented)

The owner replaced the open question by a requirement: precedence and discount policy are COMPANY-CONFIGURABLE, with the slice-1 behavior as the default and nothing financial activated silently.

* **`PricingPolicy` gains two settings** (defaults reproduce slice 1 exactly): `ListPrecedence` = `ScopeThenPriority` (default: customer list, group list, general list; priority only among lists of one scope) or `PriorityThenScope` (priority first, scope breaks ties); `DiscountPrecedence` = `SpecificityThenPriority` (default: customer, item specificity, then priority) or `PriorityThenSpecificity`. The rest of the key (higher minimum quantity, later start, code, id) and the ambiguity refusal are unchanged, so the order is total and deterministic in every mode.
* **Discounts never stack in any mode.** Stacking is not an approved requirement of SRS 8.1-8.4 and is NOT offered; adding it would be a separate owner decision. A quantity tier stays a floor and an item entry still beats an item-group entry (these are structural selection rules, not company policy).
* **Traced.** A non-default precedence adds a `policy` step to the trace.
* **Historical snapshots are preserved.** A snapshot of a default-precedence policy stays `pricing-snapshot-v1` with its historical hash (the golden-hash test is unchanged and green); a non-default policy is sealed as `pricing-snapshot-v2`, whose content includes both precedences. `Verify` accepts v1 only with default precedences and v2 only with non-default ones, so a mislabelled or edited snapshot is invalid evidence.
* **Tests:** 5 unit tests in `PricingPolicyTests` (list precedence, discount precedence and ambiguity, invalid policy and input-order independence, snapshot versions and tamper cases, seal-under-the-resolving-policy); all 962 unit tests pass. `Create` and `Verify` require the trace's `policy` steps to match the snapshot's policy (`policy_mismatch`), so a snapshot can never state a policy that was not applied.
* **Slice 2b (next):** persistence of price lists, entries, discount rules and the policy (the two precedences are stored per company, default = the values above, every change revision-fenced and audited), then the API and screens.

**Slice-2 requirements from the review (still open for 2b):** margin basis is the rounded unit net price and must be stated in the UI and API; a Warn/RequireApproval below-minimum result needs an approval record before it is sealed; price lists and discount rules are validated on write (customer XOR group, ValidFrom <= ValidTo) so one malformed row cannot block pricing.
