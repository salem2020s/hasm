# Independent review - Phase 3.11 slice 1 (pure pricing engine and snapshot, PR #58)

* Reviewed: the full diff of `feature/phase-3-11-pricing-engine` against `main`. Reviewer: a separate subagent session (general-purpose agent, fresh context, read-only, static; the tax engine files were not present in its worktree so it compared by the documented contract). Result: **no Critical, no High**; five Medium, seven Low.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | `Verify` threw on a null trace; `Resolve` threw on null lists. | Fixed: a malformed snapshot is invalid evidence (false), null lists are `invalid_request`; tests. |
| M2 | Medium | `Calculate` sealed a resolution without a price rule reference or with a discount and no rule reference ("chosen rule recorded" broken). | Fixed: `invalid_resolution`; tests. |
| M3 | Medium | After an override the result still named the replaced rules. | Fixed: the result names `manual-override` for the replaced part and the trace step records the replaced rule and value; tests. |
| M4 | Medium | The hash serialized the live record types, so a later property would invalidate every stored snapshot. | Fixed: explicit field lists define the v1 hash (a new field needs a new snapshot version); a golden-hash test pins it; culture independence tested (ar-SA, de-DE, fa-IR). |
| M5 | Medium | Unit matching is exact and there is no currency conversion; `UnitCost` had no declared unit or currency. | Recorded as an explicit decision (docs/PHASE-3.11.md and the model comment): the quantity is in the priced unit, the currency is the document currency, conversions belong to the caller/document framework, `UnitCost` is in the same unit and currency. |
| L1 | Low | Lists tied on scope, priority and start are broken by code then id (discounts refuse the tie). | Accepted: list codes will be unique per company (persistence slice); documented in the engine. |
| L2 | Low | Thin entry validation; a bad INACTIVE rule blocked pricing. | Fixed: negative minimum quantity, item AND group, neither, and `ValidTo < ValidFrom` are refused; inactive lists and rules are never evaluated. |
| L3 | Low | An override reason over 500 characters returned `override_reason_required`. | Fixed: `override_reason_invalid`. |
| L4 | Low | Margin is capped at -100 only for a zero net. | Accepted and documented (the control compares against the minimum; the figure is informational). |
| L5 | Low | Rounding the unit price first zeroes out very low prices at 2 decimals. | Accepted: by design (the printed unit price is what multiplies); the policy's price decimals are configurable up to 6. |
| L6 | Low | Net x quantity could overflow or double-round. | Fixed: the line amount is computed exactly on integers and rounded once; an unrepresentable result is `amount_too_large`; the largest permitted product is tested exactly. |
| L7 | Low | Nothing binds the resolution to the request; the result trace array is mutable. | Accepted for this slice (the persistence slice builds the resolution from the request); recorded. |

Tests added: 5 (`PricingReviewTests`: golden hash and culture, snapshot tamper matrix, resolution integrity and override record, limits/margins/exact line amounts, tie and data validation). Total 17.

## Re-review - full diff against main (2026-11-20, head 57e4206)

Reviewer: a separate read-only subagent (general-purpose, Sonnet 5.5 family, fresh context; it ran nothing and could not compare against SRS 8.1-8.4 because the SRS is not in the worktree). **No Critical, no High; two Medium, seven Low.** Confirmed: no floating point, clock or culture; exact decimal and BigInteger arithmetic with a single rounding and overflow refusal; margin control never silently skipped (missing cost under Block/RequireApproval refuses; an override cannot bypass the margin); the snapshot hash covers explicit canonical fields and `Verify` recalculates; zero/negative quantity and empty lists are refused; 17 unit tests.

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | Selection rules (scope specificity before list priority; exactly one discount, no stacking; item entry before item-group entry; quantity tier as a floor) are documented reversible defaults but have no owner-recorded decision; SRS 8.1 says "configurable". | Handled: this slice is a pure engine with no migration, API or persistence, so nothing is schema-bound. Recorded ONCE as owner decision **P** in `docs/PHASE-3.11.md`; slice 2 (persistence) must not start until the owner confirms or changes the defaults. Fail-closed: nothing consumes the engine yet. |
| M2 | Medium | Margin is computed on the rounded unit net price (undocumented basis); a below-minimum result under Warn/RequireApproval is sealed without an approval record (caller's job). | Documented in `docs/PHASE-3.11.md` as a slice-2 requirement. |
| Lows | Low | One malformed active list blocks all pricing (persistence must validate on write); Calculate trusts rule refs (accepted L7); mutable trace array (hash is tamper-evident); Guid.Empty and untrimmed currency accepted; no control-character check on the override reason; margin below -100% possible when cost exceeds price. | Recorded for slice 2; none blocks. |
