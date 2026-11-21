# Independent review - PR #68, Phase 3.11 slice 2b (pricing persistence)

* Reviewed: the full diff against `origin/main` at head `cecc4d9`. Reviewer: a separate read-only subagent (general-purpose, Sonnet family, fresh context; it ran the generator `--check`, it did not run the PostgreSQL suite). **No Critical, no High; two Medium, four Low.**
* Verified: policy versions append-only with max+1 and a unique-index serialization of concurrent inserts; immutability and revision triggers; DELETE/TRUNCATE refused and runtime privileges SELECT/INSERT/UPDATE only; partial unique indexes handle the NULL item/group tier columns; ranges match the engine's bounds and the service pre-validates them; error mapping; audit detail contains codes, ids and prices only; quote in one repeatable-read transaction over active data, override needing `company.pricing.override`; permissions company-wide; system numbering contiguous; generator deterministic with collision checks, `--check` in CI and a derivation unit test; registry only raised (1016 -> 1022).

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| M1 | Medium | Two active discount rules with the same scope, priority, minimum quantity and start made the engine refuse (`ambiguous_discount`) every matching quote. | Fixed in the (still unmerged) migration: a partial unique index on active rules (scope with NULLs as the zero uuid, priority, minimum quantity, start) mapped to `ambiguous_rule`; tests cover create, another priority, inactive rules and activation of a colliding rule. |
| M2 | Medium | A price list or discount rule could name a party without a customer role. | Fixed: creation requires an existing customer role of the party (`customer_role_required`); tests use real customer roles and refuse a role-less and a missing party. |
| L1 | Low | No test of concurrent policy saves or direct out-of-range SQL inserts. | Recorded follow-up (the unique index and CHECK constraints are exercised by the service tests). |
| L2 | Low | A null `CustomerId`/`ItemId`/`UnitId` on update is ignored rather than refused. | Accepted: the triggers enforce immutability. |
| L3 | Low | An entry is not checked for unit-belongs-to-item or active item. | Recorded for the pricing screens slice (no accounting rule is invented here). |
| L4 | Low | Quote reads are not READ ONLY and make one entry query per active list. | Accepted; revisit with performance validation. |
