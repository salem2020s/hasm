# Phase 3.8B-3 — Negative-stock exception: technical and accounting design

Status: **DESIGN FOR INDEPENDENT REVIEW — nothing is implemented.** Basis: the owner policy for N1–N4 (recorded in the owner message of 2026-11-16), SRS §9 (negative stock forbidden by default; limited authorized exception for eligible moving-average items with a provisional cost and later settlement; never FIFO, lot or serial; the period-close rule for material pending differences; retroactive entries never edit posted history), PHASE-3.6 (costing engine), PHASE-3.7A–D (accounting configuration, posting, reversal, close probe), ARCHITECTURE §7–§8.

## 1. Core decision: do not weaken the engine, model the obligation beside it
The costing position of an item and warehouse keeps every existing CHECK (never a negative quantity or value, no value without quantity, no negative FIFO layer). **No existing constraint is relaxed.** A negative issue is split:
* the **covered part** (up to the quantity on hand) goes through the engine exactly as today at the real moving-average cost;
* the **uncovered part** Qn never touches the position. It becomes a **provisional obligation**: a separate, narrowly constrained record that says "Qn units left the warehouse before they were received, valued provisionally at p per unit".
So the position stays a true physical stock that can never be negative, the weighted average and FIFO layers cannot be corrupted by the provisional figure, and the exception cannot leak into ordinary stock. (The physical stock is then shown by the balances screen as quantity on hand plus the open obligation quantity, a separate column.)

## 2. Eligibility and authorization (N4)
* Default stays prohibited. Three independent switches must all be on: **company enablement** (a versioned, audited company setting, off by default), **item eligibility** (an explicit per-item flag, settable only for items whose resolved method is moving weighted average and that are neither lot nor serial tracked; the database refuses the flag on any other item, and the method of a position is fixed at its first entry, so a later method change cannot make a flagged item FIFO silently: changing the method of an item with an open obligation or the flag is refused), and a **dedicated warehouse-scoped permission** `company.stockdocs.negativeexception` held by the user who approves the exception.
* **Approval control and segregation of duties:** an issue that would exceed stock is refused by default. When the exception applies, the document needs an explicit *exception approval* (a separate, audited event recorded on the document with the approver, time and reason) by a user holding the permission who is **not** the poster and not the document creator (configurable: the company may require a second distinct approver). Posting without the recorded exception approval is refused; no background or automatic process creates or consumes an exception approval.
* Transfers may **not** use the exception (a transfer carries the actual cost to the receiving warehouse; a provisional cost must never travel): refused.

## 3. Provisional cost (N1)
Resolved at posting, in this priority, and stored immutably on the obligation with source, user, time and references:
1. the **last valid moving weighted-average unit cost** of that item and warehouse. The engine keeps `last_average_unit_cost` per position, written whenever the position holds quantity and **retained when it falls to zero** (today the average is only derivable while quantity exists); source code `last_average`;
2. else the **latest reliable receipt unit cost** (the most recent non-reversed receipt entry of that item and warehouse with quantity and value; source `last_receipt`, reference = the entry);
3. else an **explicitly authorized manual provisional unit cost**: entered by the exception approver with a mandatory justification (source `manual`, justification stored, audited). A zero or missing cost is **never** assumed: with no source and no manual cost the issue is refused (`provisional_cost_unavailable`).
The chosen cost is part of the approval snapshot, so what the approver saw is what posts (a stale snapshot is refused and must be re-approved).

## 4. Data model (company migration, additive)
* `StockNegativeObligation` (append-only except the single open-quantity counter, which only decreases, enforced by trigger): id, item, warehouse, originating stock document and posting and cost-operation references, issuing journal entry, original quantity (base unit, > 0), open quantity (0 to original), provisional unit cost (> 0), provisional total, source code, justification, created by, time, approval reference, status (open, settled, reversed).
* `StockNegativeSettlement` (append-only): obligation, the receipt posting it was matched to, matched quantity (> 0), provisional unit cost, actual unit cost, difference amount (signed, exact), settlement journal entry, effective date, time and user; `unique(obligation, receipt entry)`; the matched quantity of an obligation never exceeds its original; the matched quantity of a receipt line never exceeds the receipt quantity (database triggers).
* `InventoryAccountingVersion` gains two **new configured accounts** (a new immutable version, nothing hard-coded or seeded): the **provisional obligation account** (a liability-type, postable account) and the **settlement variance account** (default: the original issue's counter account, i.e. cost of sales; an explicit variance account may be set). The existing inventory and counter mappings are untouched. Unconfigured company: the exception is refused.
* Position: `last_average_unit_cost`, written by the engine only.
Concurrency: every operation takes the same per-item-and-warehouse advisory lock and document lock as posting (3.7B); matching is serialized per item and warehouse.

## 5. Accounting (double entry, base currency, exact decimal arithmetic)
Let I = inventory account (resolved for the item group), K = issue counter account (cost of sales), G = receipt counter account (goods received), O = provisional obligation account, V = variance account (default K).

**5.1 Issue of 10 units when 3 are on hand at average 5.00 (provisional cost p = 5.00 from last average).**
* Covered part: 3 units × 5.00 = 15.00 through the engine: Dr K 15.00 / Cr I 15.00.
* Uncovered part: 7 units × 5.00 = 35.00 as an obligation: Dr K 35.00 / Cr O 35.00.
* One balanced journal entry (accounting state as today: draft pending, or no journal when zero value). Inventory is 0 quantity / 0 value; the open obligation is 7 at 5.00.

**5.2 Full settlement by a receipt of 7 at 6.00.**
* Matched 7: provisional 35.00, actual 42.00, difference 7.00 (actual above provisional).
* Dr O 35.00, Dr V 7.00 / Cr G 42.00. Nothing enters inventory (the goods were already issued). The position is unchanged.
* If the actual cost were 4.00 (actual 28.00, difference -7.00): Dr O 35.00 / Cr V 7.00, Cr G 28.00.

**5.3 Partial receipt, then another.** Obligation 7 at 5.00. Receipt 1: 4 at 6.00 → matched 4: Dr O 20.00, Dr V 4.00 / Cr G 24.00; open 3. Receipt 2: 10 at 4.50 → matched 3 (provisional 15.00, actual 13.50, difference -1.50): Dr O 15.00 / Cr V 1.50, Cr G 13.50; the remaining 7 units enter inventory normally at 4.50: Dr I 31.50 / Cr G 31.50 (the engine receives 7 at an exact amount of 31.50). The weighted average therefore never sees the provisional cost.

**5.4 Several obligations.** Matching is deterministic: obligations of the item and warehouse are consumed **oldest first** by (effective date, creation sequence); within one receipt the matched split is stored per obligation. A receipt line is matched before any quantity enters the position.

**5.5 Reversal.** Reversing a receipt that settled obligations re-opens the settled quantities exactly (compensating settlement entries: the settlement journal is reversed through the existing reversal machinery, the obligation's open quantity is restored by a new append-only row, never an update of history). Reversing the negative issue is refused while any settlement exists; settlements must be reversed first (newest first). Reversal after partial settlement restores exactly the unsettled and settled parts in order.

**5.6 Settlement spanning fiscal periods.** A settlement is dated at its receipt's effective date and posted in that period. The original issue stays in its own period, never edited. If the receipt's period is soft-closed the existing posting authorization rules apply; if its period is **Final Closed** the receipt (and therefore the settlement) cannot be posted at all: the receipt is blocked with the unresolved obligation reported (`period_closed`), never backdated. A receipt dated in an open period settles obligations from earlier (including closed) periods; the variance lands in the receipt's open period. The "approved current-period correction workflow" named in the owner policy **does not exist in the system today**; this design therefore blocks and reports rather than inventing one (see C3).

## 6. Period close and reconciliation (N3)
* The existing `inventory_valuation_differences` close probe is extended: **any open obligation with a document date on or before the period end blocks Final Close** (and is reported at soft close as a warning on the closing checklist, as the SRS says only material pending differences block). The blocking report lists, per obligation: item, warehouse, originating document, open quantity, provisional value, age, and what resolves it (a receipt to match, or the reversal of the issue).
* Reconciliation: for each period end, `costing position value = inventory GL balance` stays as 3.7C verifies; a new line checks **open obligation provisional total = GL balance of O**, and **sum of settlement differences = the postings to V attributable to settlements**. Any mismatch blocks the close with the failing figure.
* A settlement journal in a pending or cancelled state counts as a posting problem exactly like 3.7C states (derived from facts).

## 7. Database-enforced invariants (narrow)
Obligation: quantity > 0, open between 0 and original, unit cost > 0, source in {1,2,3}, manual source requires a justification, the item must be flagged eligible and its position method moving average (trigger reads the position's immutable method), no update except decreasing open quantity or the status change, no delete or truncate. Settlement: append-only, positive matched quantity, cumulative matched ≤ original, difference = exact `matched × (actual − provisional)` recomputed by a trigger. Item flag: refused unless moving-average and untracked; refused to unset or change method while an open obligation exists. None of the existing position or layer constraints changes.

## 8. Interactions and conflicts to be decided in review
* **C1 (SRS silent):** two new accounts (obligation, variance) extend the inventory accounting configuration; the SRS names neither. Proposed as configuration, no default.
* **C2:** `last_average_unit_cost` is a new engine-maintained column; the engine's existing invariants and the 3.6 reconciliation must include it (it must equal the last average the position had while quantity existed).
* **C3 (owner policy item without a mechanism):** "approved current-period correction workflow" does not exist; blocking and reporting is proposed. If the owner wants such a workflow it is a separate phase.
* **C4 (3.8D):** back-dated movements and recalculation must treat obligations (a back-dated receipt may settle an older obligation, a back-dated issue may create one). 3.8D must be designed against this document; until then back-dated movements that touch an item with an open obligation are refused.
* **C5 (exception and approval):** a second distinct approver is configurable; the default requires the approver to differ from the poster.
* **C6 (transfers):** excluded by design (provisional cost must not move).

## 9. Test plan (PostgreSQL, disposable cluster only)
Provisional-cost priority and manual cost with justification and refusal when none; refusal for FIFO, lot, serial, unflagged items, a company without enablement, a user without the permission, the poster approving their own exception; split of a mixed issue (covered plus uncovered) with exact figures; full, partial and multiple settlements with positive and negative differences and journals balancing to the cent; oldest-first determinism; reversal restoring exact open quantities (with settlement reversal order); settlement across fiscal periods and Final Close blocking; probe and reconciliation figures; exactly-once under concurrent postings and concurrent receipts; every database invariant attacked directly; append-only trigger tests; weighted average unchanged by obligations; no existing test or constraint changed.
