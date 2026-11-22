# HASM - Phase 3.15: landed-cost allocation contract (PROPOSAL, design only)

**Scope, exactly as the roadmap and the owner define it.** `docs/ROADMAP.md` gives Phase 3.15 as "Purchasing" (SRS 7.1, 7.2, 7.5, 6.3, 8.4): purchase orders, goods receipts posting through 3.7, supplier invoices in direct and separate modes, purchase returns, GRNI, tax and price snapshots; with the note that purchase-cost composition is decided by OD-2 (D-218) as "a versioned landed-cost allocation contract, designed and independently reviewed before it is built". The owner's decision D-218 says: "Support documented directly attributable purchase costs, such as inbound freight and approved purchase discounts/rebates, through a versioned landed-cost allocation contract. Allocation must be deterministic and reconcilable. Do not capitalize unrelated operating expenses or invent missing amounts." **This document is that contract and nothing more.** It does not design purchase orders, supplier invoices, GRNI clearing, payables or purchase returns; where it needs them it names the dependency.

**Status: PROPOSAL** for independent accounting review and explicit owner approval. Nothing is built, no migration is written, and no decision ID, audit code, permission name or migration number is allocated; names are working names. **No landed cost is capitalised by HASM beyond what an authorised poster enters as the exact receipt amount today, until this contract is approved and its tests have passed review.**

How to read it. **[APPROVED: source]** is settled by the approved SRS or a recorded owner decision. **[AS BUILT: file, function]** is a statement about `main` verified in the named code for this document. **[PROPOSAL]** binds nobody until approved. **[OPEN: LQ-n]** is not settled by the approved sources: options, consequences and a proposed conservative default are given, and it is listed in section 17.

## 1. Sources

* **SRS** (`docs/SRS.md`): section 7 (purchase cycle, direct and separate modes, no duplicate stock or ledger effect, intermediate accounts such as GRNI), section 6 (tax engine; an immutable snapshot of base, rates and results on the final document), section 8 (cost and related taxes kept as historical snapshots), section 9 (two valuation methods; cost per item and warehouse; posted journals never edited; Final Closed periods never posted into; retroactive movements through separate approved adjustment documents), section 5 (base currency; multiple currencies with dated rates; posted entries corrected only by reversal or adjustment), sections 4 and 19 (approvals; rules 2, 3, 5 and 8).
* **Decisions on `main`** (`docs/DECISIONS.md`): D-017, D-018, D-019; D-218 (above); D-205 (3.13 Q16: "In separate mode, require an explicit, approved per-line receipt valuation supported by the linked purchase-order snapshot or verified receipt information. Refuse missing or ambiguous values. GRNI postings require approved company-specific accounting mappings and subsequent invoice reconciliation."); D-202 (versioned company-specific mappings; no seeded accounts; an explicit accounting-pending state until configured); D-190 and D-191 (replay, X2 to X8); D-192 and D-198 (revaluation); D-215 (Trial counting); D-216 (execution and accounting states); D-220 (period guard).
* **Roadmap** (`docs/ROADMAP.md`, `tools/roadmap/phases.js`, `decisions.js`): the 3.15 entry and its acceptance criteria ("receipts cost from the invoice or receipt input; GRNI clears exactly; foreign-currency settlement refused (fail closed) until 3.18"); the OD-2 row, whose note reads "the allocation contract (rules, accounts, what happens to charges after the goods were sold) is designed and independently reviewed in Phase 3.15".
* **Other proposals this one must fit**: `docs/PHASE-3.8D-REVALUATION-CONTRACT.md` (on `main`, questions RQ-1 to RQ-16 open), the replay design on the branch of PR 48, `docs/PHASE-3.8C-DESIGN.md` (open as a pull request).
* **What is built**: section 3.

## 2. What the approved sources settle, and what they do not

* **[APPROVED: D-218]** Documented, directly attributable purchase costs are supported; the owner's examples are inbound freight and approved purchase discounts and rebates. The contract is versioned. Allocation is deterministic and reconcilable. Unrelated operating expenses are never capitalised. Missing amounts are never invented.
* **[APPROVED: D-205]** A receipt line carries an explicit, approved valuation; missing or ambiguous values are refused. The explicit per-line amount is kept; this contract does not replace it.
* **[APPROVED: D-017, D-018]** Two methods; cost per item and warehouse; the actual cost moves with a transfer.
* **[APPROVED: SRS 5, 9, 19 rule 3; D-044 as cited by the replay design]** Posted journals and historical cost rows are never edited; a correction is a separate document.
* **[APPROVED: SRS 19 rule 5; D-220]** Nothing is posted into a Final Closed period; stock and cost postings into Soft Closed or missing periods are to be refused by a database guard.
* **[APPROVED: D-202]** Accounts come only from versioned, company-specific mappings; none is seeded; without a mapping the effect stays accounting-pending or is refused.

**Not settled by any approved source**, and therefore owner questions: WHICH charge types are "directly attributable" beyond the two examples; the ALLOCATION BASE (the owner approved the properties "deterministic and reconcilable" and named no base: every base in section 6 is an option, none is approved); what happens to a charge that arrives after part of the stock has left (the roadmap note names the question and leaves it to this contract); the accounts; tax on charges; foreign-currency charges.

## 3. What exists on `main` (verified), and the engine functions this contract relies on

There are no purchase documents: no purchase order, supplier invoice or purchase return exists, and no commercial document type is registered for production. A receipt of goods is a stock document of type receipt, posted through the stock posting service with one exact amount per line.

| Function (file) | What it does, as read | How this contract relies on it |
|---|---|---|
| `StockPostingService.PostInWorkAsync` with `StockPostInput` / `StockPostLineInput(LineNo, Amount)` (`Inventory/StockPosting.cs`) | the poster supplies the amount of each receipt line; the record types were read, the service body is described in `docs/PHASE-3.7B.md` | the allocated charge share becomes part of this per-line amount BEFORE posting (section 7) |
| `InventoryCostingService.ReceiveAsync` (`Costing/InventoryCostingService.cs`) | read in full: validates each line's amount against the currency's minor units, appends ONE receipt entry per line and, for FIFO, ONE layer per line with exactly that quantity and amount; never rounds a receipt | the engine needs no change for charges known at receipt |
| `InventoryCostingService.IssueAsync`, `TransferAsync`, `ReverseAsync` (same file) | signatures read; `TransferAsync` read in part (mirrored layers at the destination with a source-layer link, the value carried exactly); behaviour otherwise as `docs/PHASE-3.6.md` describes | issues after a receipt draw the landed amounts; late-charge attribution reads what left |
| `CostingMath.Proportion` (`Costing/CostingMath.cs`) | read in full: `amount x part / whole` in exact integer arithmetic, rounded half away from zero to the minor units, the exact remainder when the part is the whole | the cost of a partial issue; the model for the proportional rules of section 8 |
| `CostingMath.PlanFifo`, `PlanAverage` (same file) | signatures read | FIFO and average effects in the examples |
| `InventoryCostingService.ReconcileAsync` (same file) | signature read; checks as listed in `docs/PHASE-3.6.md` | must stay empty after every landed-cost operation |
| `InventoryAccountingService.ResolveAsync`, `ValidateForPostingAsync` (`Inventory/InventoryAccounting.cs`) | signatures read; the versioned configuration holds an inventory account per group chain and ONE receipt counteraccount and ONE issue counteraccount (`docs/PHASE-3.7A.md`) | the inventory side of every journal line; the landed-cost accounts are a NEW mapping (section 9) |
| `InventoryValuationProbe.HasBlockersAsync` (`Inventory/InventoryValuationProbe.cs`) | signature read; the close probe compares the costing ledger with the inventory accounts (`docs/PHASE-3.7C.md`) | must keep holding; pinned by a test |
| `StockPostingService.ReverseAsync`, `PeriodOfAsync` (`Inventory/StockPosting.Reversal.cs`) | signatures read | reversal of a receipt that carries landed cost; period checks |

Other verified facts: a value-only cost entry is impossible and operation kinds cannot be widened (migration 0014, as established for the revaluation contract); the item master has no weight or volume field (searched in `CompanySchema.Inventory.cs`); the tax engine as built has no notion of recoverable versus non-recoverable tax (searched in the backend sources and in `docs/PHASE-3.10.md`). The implementing slice re-reads every function it changes or calls; "signature read" above means the behaviour was taken from the phase documents, not re-verified line by line.

## 4. Definitions (PROPOSAL)

* **Charge**: an amount, positive (a cost: freight, and whatever else LQ-1 admits) or negative (a purchase discount or rebate), stated in a document of a third party or of the supplier, that belongs to identified received goods.
* **Landed-cost document**: a separate, approved HASM document that states one or more charges, the receipt lines they belong to, the allocation base and the resulting shares. It has its own number, life cycle, approval and audit.
* **At-receipt charge**: known and approved before the receipt is posted. **Late charge**: stated after the receipt was posted.
* **Share**: the part of a charge allocated to one receipt line, in minor units.

## 5. Which charges may be capitalised

**[APPROVED: D-218]** "documented directly attributable purchase costs, such as inbound freight and approved purchase discounts/rebates"; never "unrelated operating expenses"; never invented amounts.

**[PROPOSAL]** A versioned, append-only **landed-cost contract version** per company (the "versioned landed-cost allocation contract" of D-218), approved independently (author and approver distinct), bound by hash to every landed-cost document executed under it. It lists the charge TYPES the company capitalises, each with: a code and names; its sign (cost or reduction); its permitted allocation bases; its accounts (section 9). A charge whose type is not in the approved version cannot be capitalised (`charge_type_not_capitalisable`). HASM seeds NO type: a new company has no contract version, and every landed-cost document is refused (`landed_cost_contract_missing`) until one is approved **[OPEN: LQ-1: which types may a contract version contain at all]**.

**[PROPOSAL] Documentation.** Every charge names its source document (supplier or carrier document number and date) and carries at least one attachment; the amount is entered, never derived or estimated by HASM; an estimate or accrual without a document is not a charge of this contract **[OPEN: LQ-2]**.

## 6. Allocation: bases, determinism, rounding

**No allocation base is approved by the owner.** D-218 requires the allocation to be "deterministic and reconcilable"; it names no base. Each base below is therefore an OPTION **[OPEN: LQ-3]**:

| Base | Share of line i | Notes |
|---|---|---|
| (a) By value, **proposed default** | proportional to the line's goods amount | always defined; needs no new master data; a line with zero amount (free goods) receives nothing |
| (b) By quantity | proportional to the base-unit quantity | meaningful only when every line of the charge has the SAME base unit; refused otherwise (`allocation_base_mixed_units`) |
| (c) Explicit amounts | the shares are entered per line and must sum to the charge | deterministic as entered; the approver approves the split; nothing is computed |
| (d) By weight or volume | proportional to weight or volume | NOT offered: the item master has no weight or volume as built; it would need new master data and its own review |
| (e) Equal per line | charge divided by the number of lines | not proposed; it ignores what was bought |

**[PROPOSAL] The allocation rule (bases a and b), deterministic and exact.** For a charge `C` (in minor units, positive or negative; the rule runs on `|C|` and restores the sign) over lines with base figures `b_i > 0` and `B = sum(b_i)`:

1. the exact share is `s_i = |C| x b_i / B`;
2. each line gets `floor(s_i)`;
3. the minor units still missing, `|C| - sum(floor(s_i))`, are given one each to the lines with the largest fractional part of `s_i`, the lower line number first on a tie;
4. a reduction (discount, rebate) is additionally BOUNDED: no share may reduce a line below zero; if a floor-plus-remainder share would, the charge is refused (`reduction_exceeds_line_amount`) rather than re-spread.

Properties, each a test: the shares sum to exactly `C`; every share lies between `floor(s_i)` and `floor(s_i) + 1`; the result depends only on the lines' figures and numbers, never on time, order of entry or the user; the same inputs give the same shares on every run (determinism), and the stored shares can be recomputed from the stored inputs (reconcilability).

**Scope of one charge.** A charge is allocated over the receipt lines it names: all lines of one receipt, selected lines, or lines of several receipts of the same company. A line of a service or of a document that is not a posted (or, for at-receipt charges, about to be posted) receipt is refused.

## 7. Timing 1: charges known at receipt

**[APPROVED: D-205]** The receipt line valuation is explicit and approved. **[AS BUILT: `ReceiveAsync`]** The engine takes the exact amount per line.

**[PROPOSAL]** A landed-cost document approved BEFORE the receipt is posted contributes its shares to the per-line amounts: line amount = goods amount + cost shares - reduction shares. The receipt is then posted through the existing path with these amounts; the engine, the layers and the cost entries need no change. The breakdown (goods amount and each share with its charge) is stored with the posting line, so that the amount the engine holds is explained to the last minor unit. The posting refuses when a named landed-cost document is not approved, belongs to another receipt revision, or its shares no longer match the lines (`landed_cost_state_changed`).

Today's practice stays valid: **[AS BUILT: `docs/PHASE-3.7B.md`]** a poster may enter a line amount that already includes allocated freight. Under this contract that is still accepted; it is simply not explained by a landed-cost document, and D-205 requires it to be approved and supported by purchase-order or receipt information all the same.

## 8. Timing 2: charges that arrive later

This is the question the roadmap note leaves to this contract ("what happens to charges after the goods were sold").

**[APPROVED]** Posted history is never rewritten (SRS 5, 9, 19 rule 3); nothing is invented (D-218).

Three things can be true of the received quantity when a late charge arrives: it is still on hand in the position it was received into; it has left by an issue (sold, consumed, adjusted away); it has been transferred to another warehouse. **[OPEN: LQ-4]** The options:

* (a) **Proposed default: capitalise the share that belongs to quantity still on hand in the receiving position, and EXPENSE the rest**, in one landed-cost adjustment dated on its own date in an Open period. Nothing earlier is touched; the cost of goods already issued is not corrected, the part of the charge that belongs to them goes to a configured account in the current period.
* (b) Expense every late charge in full. Simplest and lowest inventory value; it never capitalises a late charge even when all the goods are still there, which understates stock the owner allows to be capitalised.
* (c) Correct the receipt through a replay plan of Phase 3.8D (a back-dated correction with compensation entries). Exact for open periods, but it carries every refusal of the replay (X5, X6, Soft Closed and Final Closed periods), cannot cross a revaluation, and is not built. Not proposed as the standard path; it may be the company's choice for a material late charge once the replay exists.

### 8.1 How much is "still on hand" (option a)

* **FIFO.** **[AS BUILT: `ReceiveAsync`]** each receipt line created one layer, with an original and a remaining quantity. The on-hand part of line i is `r_i / q_i` (remaining over original quantity of its layer). Capitalised part of its share `S_i`: `K_i = floor(|S_i| x r_i / q_i)` with the sign of `S_i`; the expensed part is the exact remainder `S_i - K_i`. Rounding DOWN means rounding never capitalises more than the on-hand part.
* **Moving weighted average.** There are no layers, so "which units are left" has no answer; the method itself treats every outflow as taking proportionally from all stock. **[PROPOSAL]** The on-hand fraction of a receipt line starts at 1 and is multiplied by `(Q - x) / Q` for every later outflow of `x` from on-hand `Q` of that position (the same rule as the reversible pool of the revaluation contract); it is 0 once the position has been empty. `K_i = floor(|S_i| x fraction)`. This is a PROPOSAL on a point the sources do not settle **[OPEN: LQ-5]**.
* **Transferred quantity.** **[APPROVED: D-018]** The actual cost moved with the transfer. **[PROPOSAL]** In the first implementation a transferred quantity counts as having LEFT the receiving position: its part of a late charge is expensed. Following the goods to the destination (FIFO: through the source-layer link of the mirrored layers) and capitalising there is the alternative **[OPEN: LQ-6]**.
* **A layer or position that was written down** under the revaluation contract and whose write-down is not reversed: capitalising a late cost would raise a carrying amount that was deliberately lowered. **[PROPOSAL]** Its part of a positive late charge is expensed; a late reduction is applied as below **[OPEN: LQ-7]**.
* **Late reductions (discounts, rebates).** Symmetric: the on-hand part reduces the carrying amount of the layer or position, bounded so that it never goes below zero (the excess goes with the expensed part); the rest is credited to the configured account.

### 8.2 How the on-hand value is changed

A value change without a quantity is impossible in the engine (section 3). This is the same constraint the revaluation contract meets, and **the mechanism must be the same one**, whichever the owner approves there (its question RQ-9: an outflow and inflow pair per layer with lineage, or an additive adjustment table). **[PROPOSAL]** The late path of this contract is therefore built only AFTER that choice is approved, on the same lineage, so that one layer has ONE history of value changes (landed-cost adjustments, write-downs, reversals), and so that the cost basis used by the revaluation contract's ceiling INCLUDES capitalised landed cost and excludes expensed parts. Until then HASM offers no capitalisation of late charges: slice 1 of this contract is the at-receipt path only, and a late charge is handled outside this contract (section 15) **[OPEN: LQ-8]**.

### 8.3 Interaction with the replay, the revaluation contract and the period guard

* **Replay (X1-A; D-190, D-191).** A landed-cost adjustment is a forward-dated operation on the present state, like a revaluation, not a replay. **[PROPOSAL]** A position in the closure of a pending replay plan is refused for a landed-cost adjustment; a replay plan that would have to storno or rebook the cost operations of a landed-cost adjustment is refused, as the revaluation contract proposes for revaluations; an adjustment is permitted on a position with an executed epoch. At-receipt charges need none of this: they are part of an ordinary receipt.
* **Revaluation contract.** One lineage and one cost basis (8.2). The preconditions of that contract apply here too: no adjustment on a position with an unreversed posting whose accounting state is pending or failed; reversals of documents made before an adjustment follow its section 15.2.
* **STOCK-PERIOD-DB (D-220).** A landed-cost adjustment is dated on its own date and is subject to the guard like any cost operation; it is never dated back into the receipt's period. Consequence, stated plainly: when a charge arrives after the receipt's period, the capitalised part enters stock and the expensed part enters the result in the LATER period.

## 9. Accounts and journal lines

**[APPROVED: D-202, D-205, D-218]** Accounts come from versioned, company-specific mappings; nothing is seeded; GRNI postings need approved mappings and later invoice reconciliation.

**[PROPOSAL]** The landed-cost contract version holds, per charge type (company default, optional item-group override for the expensed account):

1. the **charge clearing account**: credited when a cost charge is capitalised or expensed by this contract, debited for a reduction. It is the intermediate account that the supplier's or carrier's invoice later clears in purchasing (an accrual or GRNI-type account). Clearing it against the invoice is the work of the purchasing slices of 3.15, not of this contract.
2. the **late-charge result account**: debited with the expensed part of a late cost charge, credited with the non-capitalised part of a late reduction.

Each must be active and postable, not an inventory account of the inventory configuration, not bound to a cash or bank account, not a party control account, not limited to a foreign currency. The account TYPE is the company's choice **[OPEN: LQ-9]**. Refusals as in the revaluation contract (`landed_cost_mapping_not_configured` and the account-state codes).

| Event | Debit | Credit |
|---|---|---|
| Receipt with at-receipt charges | inventory account: goods amount plus cost shares minus reduction shares | receipt counteraccount (as built, 3.7A): goods amount; charge clearing account: cost shares (and debited with reduction shares) |
| Late cost charge | inventory account: capitalised part; late-charge result account: expensed part | charge clearing account: the charge |
| Late reduction | charge clearing account: the reduction | inventory account: capitalised part; late-charge result account: the rest |

Each line carries the branch of the warehouse. The journal is created through the existing automatic journal path with a unique source key. **[PROPOSAL]** Posting follows whatever the owner decides for the revaluation contract's RQ-3 (same transaction, proposed there, or pending draft), so that the two contracts never differ in how inventory value and the ledger are kept equal **[OPEN: LQ-10]**. For an at-receipt charge the journal is the receipt's own journal (one event, one entry: SRS 19 rule 2).

## 10. Multi-currency and tax

* **Currency.** **[APPROVED: SRS 5; roadmap 3.15 "foreign-currency settlement refused (fail closed) until 3.18"; as built, the engine and the inventory accounts work in the base currency]** **[PROPOSAL]** A charge is stated in the base currency. A charge document in a foreign currency is refused by this contract until Phase 3.18 defines rates and exchange differences; HASM does not convert it **[OPEN: LQ-11]**.
* **Tax.** **[APPROVED: SRS 6]** Tax results are an immutable snapshot on the document. **[AS BUILT]** The tax engine has no notion of recoverable or non-recoverable tax. **[PROPOSAL]** A charge amount is the NET amount of the charge; tax on a charge is never capitalised by this contract, and it is handled by the purchasing documents with their tax snapshot. Whether a non-recoverable purchase tax is part of inventory cost is a policy the sources do not settle **[OPEN: LQ-12]**.

## 11. Approval, permissions and audit (PROPOSAL)

* **Independent approval** of the contract version and of every landed-cost document: the approver is never the creator, the last editor or the submitter (the rule the owner approved for the replay in D-191 X3, proposed here; SRS 4). Life cycle: draft, submitted, approved, executed; rejected back to draft; immutable once submitted.
* **Binding.** The approval is bound to a fingerprint of: the document revision; the contract version hash; the charges with their source documents; the named receipt lines with their amounts; the base and the shares; for a late charge also the layers or position figures at the preview. Execution recomputes it under the position locks and refuses on any difference (`landed_cost_state_changed`).
* **Permissions** (working names; privileged; never added to an existing preset automatically): view, create, submit, approve and execute for landed-cost documents, scoped by warehouse; configure and approve for the contract version, company-wide. Whether the approval belongs to the Inventory Cost Approver role of X4 is **[OPEN: LQ-13]**; proposed: yes, no new role.
* **Audit.** Business-audit events for: contract version saved and approved; document created, submitted, approved, rejected, cancelled, executed; every refused execution. Identifiers, fingerprint and totals only. Fail closed when an event cannot be written. Codes are allocated by the lead when a slice is planned.
* **Trial counting.** **[APPROVED: D-215]** applied as a PROPOSAL: an at-receipt charge is part of its receipt and adds no count; an executed late landed-cost document is a user-originated posted adjustment and counts once; its internal operations never count separately **[OPEN: LQ-14]**.

## 12. Reversals and corrections (PROPOSAL)

* **Before execution** a landed-cost document is cancelled like any document.
* **An at-receipt charge** lives and dies with its receipt: reversing the receipt (existing rules: only while its stock is untouched) takes the landed amounts out with it and reverses the one journal; the landed-cost document returns to approved-and-unused or is cancelled. The charge cannot be removed from a posted receipt on its own.
* **An executed late adjustment** is corrected as the revaluation contract's section 15.1 proposes for a revaluation: a narrow, independently approved cancellation of the whole document while nothing has moved on its positions and the date is in an Open period; otherwise a further landed-cost document (for example a reduction) corrects it. The generic engine reversal is refused for its operations.
* **A wrong charge amount found later** is a new charge of the opposite sign with its own source document, never an edit.
* **Purchase returns.** Goods returned to the supplier leave at their carrying cost, which includes capitalised landed cost. What the supplier credits, and where a difference goes, is purchase-return accounting of 3.15 and 3.13 S4, not of this contract; this contract only guarantees that the landed part of the cost is visible on the layer.

## 13. Effects under FIFO and under the moving weighted average (worked examples; two decimals)

### 13.1 Allocation at receipt, by value

A receipt with three lines and one freight charge of 37.00:

| Line | Item | Quantity | Goods amount | Exact share `37.00 x b / 450.00` | Floor | Fraction | Share |
|---|---|---|---|---|---|---|---|
| 1 | X | 10 | 100.00 | 8.2222 | 8.22 | .22 | 8.22 |
| 2 | Y | 20 | 300.00 | 24.6667 | 24.66 | .67 | 24.67 |
| 3 | Z | 5 | 50.00 | 4.1111 | 4.11 | .11 | 4.11 |
| | | | 450.00 | | 36.99 | | 37.00 |

One minor unit is missing after the floors and goes to the largest fraction (line 2). Line amounts handed to the engine: 108.22, 324.67, 54.11; total 487.00. Journal of the receipt: debit inventory 487.00; credit the receipt counteraccount 450.00 and the charge clearing account 37.00. FIFO: three layers, 10 for 108.22, 20 for 324.67, 5 for 54.11. Average: each position gains its quantity and its landed amount.

A purchase discount of 9.00 by value on the same receipt gives exact shares 2.00, 6.00 and 1.00 and line amounts 106.22, 318.67, 53.11.

### 13.2 A late charge, FIFO

Item X: the layer of 10 for 108.22. Four units are issued: cost 108.22 x 4 / 10 = 43.288, rounded 43.29; the layer holds 6 for 64.93. A late customs document of 20.00 belongs to this line only.

* On-hand part: 6 / 10. Capitalised `floor(20.00 x 6 / 10) = 12.00`; expensed 8.00.
* Layer after: 6 for 76.93.
* Journal, dated on the adjustment date: debit inventory 12.00 and the late-charge result account 8.00; credit the charge clearing account 20.00.
* A later issue of 3 units costs 76.93 x 3 / 6 = 38.465, rounded 38.47; the layer holds 3 for 38.46.

Nothing earlier changed: the issue of 4 keeps its cost of 43.29, and the 8.00 that belongs to those units is in the result of the period of the adjustment.

### 13.3 A late charge, moving weighted average

Position before the receipt: 10 units for 200.00. Receipt of 10 for 108.22: 20 for 308.22. Issue of 8: cost 308.22 x 8 / 20 = 123.288, rounded 123.29; position 12 for 184.93. The on-hand fraction of the receipt line is (20 - 8) / 20 = 0.6.

Late charge 20.00: capitalised `floor(20.00 x 0.6) = 12.00`, expensed 8.00. Position after: 12 for 196.93. Journal as in 13.2.

### 13.4 A late rebate, FIFO

Continuing 13.2 before the last issue (layer 6 for 76.93): a supplier rebate of 15.00 on this line. On-hand part 6 / 10: inventory reduced by `floor(15.00 x 6 / 10) = 9.00`; the remaining 6.00 goes to the late-charge result account. Layer after: 6 for 67.93. Journal: debit the charge clearing account 15.00; credit inventory 9.00 and the late-charge result account 6.00.

## 14. Slices (PROPOSAL)

| Slice | Content | Depends on |
|---|---|---|
| L1 | Contract versions (charge types, bases, accounts) with independent approval; landed-cost documents; allocation; the at-receipt path through the existing receipt posting; breakdown stored with the posting line | this contract approved; the 3.13 S3 and 3.15 receipt path where the receipt comes from a purchase document |
| L2 | The late path: on-hand attribution, the value change on layers and positions, the expensed part | L1; the approved mechanism of the revaluation contract (RQ-9) and its posting rule (RQ-3) |
| L3 | Shared screens (Web and Tauri identical): contract versions, the document with its preview of shares, the breakdown on a receipt line and on a layer | L1 |
| later | Following transferred quantities; replay-based correction; foreign-currency charges (3.18); non-recoverable tax | their own decisions |

## 15. What stays as it is until this contract is approved and built

A poster may include allocated charges in the exact per-line receipt amount, with the approval and support D-205 requires. HASM capitalises nothing else. A charge that arrives after the receipt is not capitalised by HASM: it is entered through the ordinary purchasing or journal documents, as the company's accountant decides. This is a limitation of the software, not a policy of this contract.

## 16. Invariants (each pinned by at least one test)

* K1. The shares of a charge sum to exactly the charge; each share is within one minor unit above its exact proportional value and never below its floor.
* K2. Allocation is a pure function of the stored inputs: recomputing it from the stored document gives the stored shares.
* K3. A receipt line's amount equals its goods amount plus its cost shares minus its reduction shares, and the cost layer or average receipt entry carries exactly that amount.
* K4. No charge is capitalised whose type is not in the approved contract version bound to the document, or which has no source document.
* K5. For a late charge: capitalised part plus expensed part equals the share; the capitalised part never exceeds the on-hand part; no carrying amount goes below zero; no quantity changes.
* K6. No posted journal, cost entry, layer original or posting line of an earlier document is updated or deleted.
* K7. Every landed-cost effect has one balanced journal effect equal to its cost effect; the costing ledger equals the inventory accounts as the revaluation contract requires for the chosen posting rule.
* K8. No execution without an approved, exactly matching approval by a person who is not the creator, last editor or submitter; one execution per approval; idempotent under retry and concurrency.
* K9. No execution outside an Open period; a late adjustment is never dated before the last effective date of its positions.
* K10. No existing constraint, trigger, function or guard is dropped, replaced, disabled or widened.
* K11. One layer has one lineage of value changes shared with revaluations; the cost basis used for a write-down reversal includes capitalised landed cost and excludes expensed parts.
* K12. `ReconcileAsync` is empty after every operation of this contract.

## 17. Owner questions (each with a proposed conservative default; none is decided here)

| # | Question | Proposed default | Alternatives and consequences |
|---|---|---|---|
| LQ-1 | Which charge types may a company capitalise ("directly attributable")? | Only types listed in the company's independently approved contract version; HASM seeds none; the contract admits inbound freight, purchase discounts and rebates (the owner's examples) and other types only when the owner names them | A fixed wider list (insurance in transit, customs duties, handling, brokerage): convenient, but each is a statement about attributability the owner has not made |
| LQ-2 | What documentation must a charge carry? | A source document number and date and at least one attachment; no estimates or accruals without a document | Allow an approved estimate to be capitalised and trued up later: earlier cost, but an amount that is not yet documented |
| LQ-3 | Allocation base | By value; by quantity only when all lines share one base unit; explicit approved amounts as an alternative; weight and volume not offered (no master data) | Quantity or weight as the default: needs comparable units or new master data. Equal per line: ignores what was bought |
| LQ-4 | A charge that arrives after the receipt was posted | Capitalise the part belonging to quantity still on hand in the receiving position, expense the rest, in the current Open period; never rewrite history | Expense every late charge (lowest stock value, understates stock still on hand); or a back-dated correction through the replay (exact in open periods, with all its refusals; not built) |
| LQ-5 | Under the average method, how much of a receipt is "still on hand"? | The receipt's on-hand fraction shrinks proportionally with every later outflow of the position (the method's own blending) | Treat the receipt as fully on hand while the position holds at least its quantity (capitalises more); or expense every late charge of average items |
| LQ-6 | Quantity transferred to another warehouse before the charge arrived | Counts as having left: its part is expensed (first implementation) | Follow the goods and capitalise at the destination through the source-layer link: more exact, larger design |
| LQ-7 | A late cost charge on stock that was written down and not reversed | Its part is expensed, so a written-down value is never raised by a late cost | Capitalise and raise the cost basis; the write-down would then need to be reassessed |
| LQ-8 | When is the late path built, and on which mechanism? | After the owner's answer to the revaluation contract's RQ-9, on the same lineage; until then at-receipt charges only | A separate mechanism for landed cost: two histories of value changes per layer, which the reviewers of the revaluation contract would have to reconcile |
| LQ-9 | Which accounts take the clearing and the expensed part, and may HASM constrain their type? | A charge clearing account and a late-charge result account per charge type in the contract version; type not constrained; refusal when missing | Posting the expensed part to the issue counteraccount of 3.7A (cost of goods sold): one account less, but it mixes late charges into cost of sales without a trace |
| LQ-10 | When is the journal of a late adjustment posted? | As the owner decides for the revaluation contract (RQ-3; proposed there: in the same transaction) | A different rule for landed cost would let the two ledgers diverge for one kind of adjustment and not the other |
| LQ-11 | Charges in a foreign currency | Refused by this contract until Phase 3.18 | Conversion at an entered rate now: an exchange-rate rule ahead of 3.18 |
| LQ-12 | Is non-recoverable purchase tax part of inventory cost? | Not capitalised by this contract; charges are net of tax; the tax engine has no recoverability notion as built | Capitalise non-recoverable tax: needs a recoverability attribute in the tax configuration and its own review |
| LQ-13 | Who approves landed-cost documents and contract versions? | The Inventory Cost Approver role of X4, never the creator, last editor or submitter; no existing role widened | A separate role |
| LQ-14 | Trial counting | An at-receipt charge adds no count; an executed late landed-cost document counts once (D-215 applied) | Not counting late documents needs the owner's word |
| LQ-15 | Is there a time limit after which a late charge may no longer be capitalised? | No fixed limit: the on-hand rule and the Open-period rule already bound it | A limit in days or periods after the receipt; anything later is expensed in full |

## 18. Test matrix an implementation must pass before activation

PostgreSQL on the isolated cluster unless marked; numbering local to this document.

| # | Test | Proves |
|---|---|---|
| LT1 | Example 13.1: shares, line amounts, layers, journal; the discount variant | K1, K3, K7 |
| LT2 | Unit: allocation properties on boundary cases (many lines with tiny bases, one line, zero-amount line, huge amounts, three-decimal and zero-decimal currencies); ties go to the lower line number | K1, K2 |
| LT3 | By quantity with mixed base units refused; explicit amounts that do not sum refused; a reduction that would take a line below zero refused | section 6 |
| LT4 | Recomputing every stored allocation from its stored inputs reproduces the stored shares | K2 |
| LT5 | Receipt posting with a landed-cost document that is not approved, belongs to another revision, or whose lines changed: refused, nothing written | section 7 |
| LT6 | Charge type not in the bound contract version, missing contract, missing source document or attachment: refused | K4 |
| LT7 | Examples 13.2 and 13.4 (FIFO late cost and late rebate): capitalised and expensed parts, layer, journal, later issue | K5, K7 |
| LT8 | Example 13.3 (average): the on-hand fraction after several outflows and a receipt; a position that was empty in between gives zero | K5 |
| LT9 | Transferred quantity counts as left; written-down layer: cost part expensed | LQ-6 and LQ-7 defaults |
| LT10 | Late reduction never takes a layer or position below zero; the excess goes with the non-capitalised part | K5 |
| LT11 | Approver equal to creator, last editor or submitter refused; approval bound to the revision; stock moving between approval and execution refuses (`landed_cost_state_changed`) | K8 |
| LT12 | Twelve concurrent executions: one effect; replay returns the first result | K8 |
| LT13 | Soft Closed, Final Closed, missing period, date before the last effective date: refused; with the D-220 guard present, raw SQL cannot insert the operation into a closed period | K9 |
| LT14 | Mapping missing, account inactive, not postable, an inventory account, foreign-currency limited: refused, nothing left behind | section 9 |
| LT15 | Costing ledger equals the inventory accounts after at-receipt and late operations; the close probe behaves as the chosen posting rule says | K7 |
| LT16 | `ReconcileAsync` empty after a 150-step random scenario with receipts carrying charges, issues, transfers, reversals, late charges and reductions, write-downs | K12 |
| LT17 | Before and after catalog comparison of every existing guard function, trigger and constraint | K10 |
| LT18 | Reversal of a receipt with at-receipt charges takes the landed amounts out and reverses one journal; refused once stock moved | section 12 |
| LT19 | Cancellation of a late adjustment only while nothing moved, in an Open period, by an independent approver; generic reversal of its operations refused | section 12 |
| LT20 | Position in a pending replay plan refused; a replay across a landed-cost adjustment refused; adjustment after an executed epoch permitted | section 8.3 |
| LT21 | One lineage with revaluations: a write-down after a capitalised late charge uses the raised basis; an expensed part never enters the basis | K11 |
| LT22 | Foreign-currency charge refused; a charge amount with more decimals than the base currency refused | section 10 |
| LT23 | Permissions: each action refused without its permission and outside the warehouse scope; no existing preset gained a permission | section 11 |
| LT24 | Audit event failing makes the step fail closed | section 11 |
| LT25 | Shared screen tests (Web and Tauri identical): preview of shares, breakdown on the receipt line, refusal messages; typecheck, production build and Tauri build check | shared frontend rule |
| LT26 | Trial counting as LQ-14 decides | D-215 |

## 19. What happens next

1. Independent accounting review of this proposal.
2. The owner answers LQ-1 to LQ-15 and approves the contract explicitly, or changes it. LQ-8 and LQ-10 depend on the answers to the revaluation contract's RQ-9 and RQ-3.
3. Slices are then planned one by one, each with its own independent full-diff review; L1 needs no change to the costing engine.
4. Until then nothing in this document changes behaviour, and HASM capitalises no landed cost beyond the explicit receipt amount an authorised poster enters today.
