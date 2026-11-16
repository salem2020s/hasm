# Phase 3.8B — Stocktaking, adjustments and the negative-stock exception (plan)

Sources: SRS §9 (stocktake and adjustments; negative stock forbidden by default; limited authorized exception for eligible moving-average items with a provisional cost and later settlement; never FIFO, lot or serial items), ROADMAP 3.8B (SRS 9.1, 9.4), PHASE-3.6 (costing engine), PHASE-3.7A–D (accounting configuration, posting, reversal, UI), PHASE-3.8A (balances).

Status: **PLAN ONLY — nothing is implemented yet.** Written before any code, as CLAUDE.md requires for substantial changes.

## What exists today
* Document types are `Receipt`, `Issue`, `Transfer` (`StockDocType`); there is no stocktake and no adjustment document.
* The costing engine (PostgreSQL CHECK constraints, migration 0014) forbids negative quantity and value, a value without quantity and a negative FIFO layer, by design.
* Posting (3.7B) creates the cost operation, the idempotent journal (or an explicit accounting-pending state) and the reversal (3.7C); posting needs an amount per receipt line entered by the poster.

## Slicing (each slice is a PR with full backend, PostgreSQL, frontend tests and the independent review)
1. **3.8B-1 Stocktake and variance (no negative stock).** Stocktake sheet per warehouse (snapshot of the book quantity at a stated date, counted quantity per item, variance = counted − book, reason per line), draft → submitted → approved with the existing warehouse-scoped permission pattern and audit. Company migration (new tables, additive only); system migration only for new permissions.
2. **3.8B-2 Adjustments posted through the 3.7 machinery.** An approved stocktake generates **one** adjustment document: shortage lines are issues at the current cost (exact, from the costing engine), surplus lines are receipts whose amount is entered by the poster like any receipt. Posting is exactly-once with an accounting state; reversal reuses 3.7C. Needs the inventory adjustment counter account of the 3.7A configuration.
3. **3.8B-3 Negative-stock exception.** BLOCKED on the owner decision below.

## Ambiguities that need the owner (not decided by the agent)
The SRS states the exception in one sentence. These are accounting rules and must not be invented:
* **N1.** How the provisional cost of a negative position is determined (last known average cost? a manually entered cost? the last receipt cost?) and who may enter it.
* **N2.** The accounting entries at the time of the negative issue (which account carries the provisional cost) and at settlement (the difference between the provisional cost and the cost of the receipt that covers it: which account, which period if it falls in a closed period).
* **N3.** Whether a closed period blocks settlement and how the "material pending differences prevent period close" rule (SRS §9) treats an open provisional position.
* **N4.** The eligibility flag: per item, per item group or per company, and who may grant the permission (a new permission is assumed: `company.stock.negativeexception`).
It also needs relaxing the central CHECK constraints of the costing engine, which is the highest-risk change of Release 1 and should be reviewed after N1–N4 are answered. Until then 3.8B-3 is recorded as blocked and the agent continues with the phases that do not depend on it (3.8C, 3.8D, 3.9A) per the autonomous policy.

## Invariants to test (PostgreSQL, disposable cluster on 5433 only)
Variance arithmetic exact in the base unit; a stocktake approved once yields exactly one adjustment document and one posting; a repeated submit or approve is idempotent; concurrent approvals are serialized; warehouse scope applied inside every query; posting refused in a closed period; shortage cost equals the engine's cost; reversal restores quantity and value exactly; no change to existing constraints in slices 1 and 2.
