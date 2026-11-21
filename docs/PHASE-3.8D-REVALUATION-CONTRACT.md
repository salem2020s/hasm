# HASM - Phase 3.8D: inventory revaluation accounting contract (PROPOSAL, design only)

**Status: PROPOSAL for independent accounting review and explicit owner approval. Nothing here is built, no migration is written, and REVALUATION EXECUTION STAYS CLOSED.** The owner's decisions D-192 and D-198 require this contract, its independent review and the required tests before revaluation is activated ("Do not activate revaluation until the contract and required tests are approved", D-198). Until then the engine keeps refusing a method or value change (`valuation_method_changed`), and no revaluation document, account mapping or permission exists.

How to read it. Every section separates three kinds of statement:

* **[APPROVED: source]** a rule already settled by the approved SRS or by a recorded decision; it is cited and not reopened.
* **[PROPOSAL]** a rule this document proposes; it binds nobody until the contract is approved.
* **[OPEN: RQ-n]** a point the approved sources do not settle. Options are given with their consequences, the conservative one is marked as the proposed default, and the point is listed as an owner question in section 16. No accounting policy is decided by this document.

No decision ID, audit code, permission name or migration number is allocated here; names in this document are working names.

## 1. Sources (what was read, and where)

* **SRS** (`docs/SRS.md` on `main`): section 9 (two valuation methods with "controls for changing the method and revaluation"; cost per item and per warehouse; negative stock forbidden by default and always for FIFO, lot and serial items; retroactive movements through separate, approved adjustment documents; posted journals never edited, Final Closed periods never posted into; operation date distinct from recording date), section 5 (posted entries are never changed or deleted; periods Open, Soft Closed, Final Closed; base currency), section 4 (approvals and separation of duties), section 15 (Trial counting) and section 19 (rules 2, 3, 5 and 8).
* **Decisions on `main`** (`docs/DECISIONS.md`): D-017 (FIFO and moving weighted average), D-018 (cost per item and warehouse; actual cost moves with a transfer), D-019 (negative stock forbidden by default, never for FIFO, serial or lot), D-190 and D-191 (the replay design and X2 to X8), D-192 (revaluation stays in Phase 3.8D and in Release 1; the seven topics this contract must cover), D-198 (the approved policy direction), D-215 (Trial counting), D-216 (execution and accounting states are separate).
* **Decisions read on the branch of PR 48, not on `main`** (`feature/phase-3-8d-impact-preview` at `c0e15d9`, `docs/DECISIONS.md`): D-044 (compensation by separate, approved, traceable entries; original posted journals and historical cost operations are never modified) and D-045 (prospective method conversion preserves quantity and carrying value; "Any separate value revaluation still needs its accounting/approval contract; this decision does not silently approve one").
* **The plan-execution design on the same branch** (`docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` at `c0e15d9`): section 1 (verified facts of the engine), section 2 (replay epochs), section 3 (persistence, lineage and binding) and section 11 (the required contents of this contract, including the two technical options of its item 5).
* **The costing engine as built** (`docs/PHASE-3.6.md`; company migration 0014): positions, entries, FIFO layers, consumptions and operations; the single rounding rule; reconciliation.
* **Inventory accounting as built** (`docs/PHASE-3.7A.md`, `3.7B`, `3.7C`; company migrations 0015 and 0016): the versioned inventory account configuration and its resolver, the stock posting with its draft journal and the accounting state derived from facts, and the `inventory_valuation_differences` close probe.
* **Negative-stock obligations as built** (`docs/PHASE-3.8B-3.md`; company migration 0020).

## 2. What the owner has already decided (not proposals)

**[APPROVED: D-192]** Revaluation stays within Phase 3.8D and within Release 1; it is not removed or deferred. FIFO layers are never silently collapsed and their historical information is never discarded. The contract needs independent accounting review and explicit owner approval before its financial execution is enabled.

**[APPROVED: D-198]** The policy direction, in the owner's words:

* "No historical FIFO layer may be silently collapsed, discarded, or overwritten."
* "Preserve original quantities, receipt dates, cost-layer ordering, and immutable accounting lineage."
* "Support separately approved inventory write-downs and permitted reversals/write-ups only according to an explicitly configured and reviewed accounting policy."
* "Never permit an unsupported upward valuation or conceal a previous impairment."
* "Use versioned company-specific accounting mappings for the required valuation and counteraccounts."
* "Every financial effect must have a balanced, traceable journal and an independent approval."
* "If the applicable policy, original cost evidence, or accounting mapping is unavailable, refuse the financial execution rather than invent values."

**[APPROVED: SRS 5, SRS 19 rule 3, D-044]** A posted journal entry and a historical cost operation are never edited or deleted. **[APPROVED: SRS 19 rule 5]** Nothing is posted into a Final Closed period, and no automatic task bypasses an approval. **[APPROVED: SRS 19 rule 2]** One business event creates at most one financial and one stock effect.

**What the approved sources do NOT say**, and this document therefore does not assume: which accounting framework a company follows; how a lower value is measured (for example net realisable value); which accounts take the difference; whether and how far a write-down may be reversed; whether evidence must be attached. These are section 16.

## 3. Facts of the engine that constrain the design (verified, section 1 of the plan-execution design and `docs/PHASE-3.6.md`)

* Cost entries, consumptions and operations are append-only. Only a layer's remaining figures and a position's totals change, and a position moves by exactly one entry at a time.
* **A value-only entry is impossible:** an entry needs a quantity greater than zero (constraint `k_0b3e9de8`). **The operation kinds cannot be widened** (receipt, issue, transfer, reversal; constraint `k_f356c89a`), and the migration guard refuses dropping a constraint.
* A position's last effective date never decreases; the service refuses a back-dated entry.
* FIFO consumes strictly oldest first by arrival sequence. A partial issue costs `amount x part / whole`, computed exactly and rounded half away from zero to the currency's minor units; emptying a layer or a position takes the exact remainder. Unit costs are derived for display and never used to compute a cost.
* All amounts are in the company's base currency. Inventory accounts limited to a foreign currency are refused by the configuration (3.7A).
* `ReconcileAsync` proves, among others: position = signed sum of its entries; open FIFO layers = position; company value = signed sum of all entries.
* The close probe `inventory_valuation_differences` blocks a period close while the costing ledger up to the period end differs from the balance of the inventory accounts in the general ledger.

Consequence. A revaluation cannot be "a value change on a row". It has to be expressed either with ordinary quantity-carrying entries (an outflow and an inflow of the same quantity at different values) or through a new additive structure that the engine's readers take into account. Section 7 compares the two.

## 4. Scope of a revaluation

**[PROPOSAL]** A revaluation is a separate, approved document (working name: inventory revaluation document) that changes the CARRYING VALUE of the stock on hand of one or more positions (item and warehouse, **[APPROVED: D-018]**) without changing any quantity, any posted journal or any historical cost row. It is never created by an automatic task, a recurring run, a replay or a method conversion.

**Not a revaluation, and out of this contract:** purchase-cost adjustments and landed cost (OD-2, D-218, Phase 3.15); quantity differences (stocktakes and adjustments, 3.8B); the correction of a wrong receipt amount (reversal and new operation, or the replay of 3.8D); method conversion (D-045, D-193, D-199), which preserves the carrying value and has its own contract; exchange-rate differences (Phase 3.18; see section 12).

## 5. Topic 1: revaluation accounting and counteraccounts

**[APPROVED: D-198]** Versioned, company-specific mappings; a balanced, traceable journal for every financial effect; refusal when the mapping is unavailable.

**[APPROVED: as built, 3.7A]** The inventory (asset) account of an item is resolved by the versioned inventory accounting configuration: the item group's own account, else the nearest parent's, else the company default.

**[PROPOSAL] Mapping.** A new versioned, append-only revaluation mapping per company, built like the 3.7A configuration (immutable versions, content hash, validated against the live chart when saved AND again when a document executes). It holds a company default and optional overrides per item group, resolved along the same group chain, for two counteraccounts:

1. the **write-down account** (debited by a write-down);
2. the **write-down reversal account** (credited by a permitted reversal). It may be the same account as (1) when the company's policy presents a reversal as a reduction of the expense.

Each must be an active, postable account, must not be an inventory account of the current inventory configuration, must not be bound to a cash or bank account or be a party control account, and must not be limited to a foreign currency. The account TYPE (expense, income, other) is the company's choice and is not constrained by HASM beyond these rules, because the SRS fixes no chart **[OPEN: RQ-1]**.

**[PROPOSAL] Refusals** (stable codes, working names): `revaluation_mapping_not_configured`, `revaluation_account_inactive`, `revaluation_account_not_postable`, `revaluation_account_is_inventory_account`, `inventory_account_not_configured` (existing). A refusal rolls everything back; no default account is ever seeded or guessed (as in 3.7B).

**[PROPOSAL] The journal.** One journal entry per revaluation document, created through the existing automatic journal path in the transaction that executes the cost effect, with a unique source key (working form `inventoryrevaluation:<id>`), dated on the revaluation date, with one pair of lines per position and direction:

| Direction | Debit | Credit | Amount |
|---|---|---|---|
| Write-down | write-down account | inventory account of the item | the decrease of the position's carrying value |
| Reversal of an earlier write-down | inventory account of the item | write-down reversal account | the increase of the position's carrying value |

Each line carries the branch of the warehouse, as stock postings do. Lines of the same account and branch may be aggregated in the entry; the per-position figures stay in the revaluation document and its lineage (section 8). A document whose net effect on a position is zero creates no line for it; a document whose total effect is zero is refused (`nothing_to_revalue`).

**[OPEN: RQ-2] Direct credit or allowance account.** Option (a), proposed default: the write-down credits the INVENTORY account itself, so the costing ledger and the inventory accounts stay equal and the existing close probe keeps working unchanged. Option (b): credit a separate allowance (contra-asset) account and keep the inventory account at cost. Consequence of (b): either the costing ledger stays at cost (then the layers are not revalued and cost of goods sold stays at the old cost, which is a different accounting model from the one D-192 describes), or the close probe and every valuation report must net two accounts. (a) is the conservative choice for the system's integrity; (b) would be a presentation requirement of a company's framework and needs the owner.

**[PROPOSAL] Posting state.** As for stock documents (**[APPROVED: D-216]**, 3.7B/3.7C): the journal entry is created as a pending draft, the revaluation's accounting state is derived from the journal's facts (pending, posted, failed, reversed) and is never a stored flag, and the document is never shown as financially posted while its journal is pending. The existing close probe already blocks the period while the cost ledger and the general ledger differ **[OPEN: RQ-3: pending draft, as proposed, or posting in the same step as execution]**.

## 6. Topic 2: write-down, and permitted write-up or reversal

**[APPROVED: D-198]** Write-downs are supported when separately approved. Reversals or write-ups are permitted "only according to an explicitly configured and reviewed accounting policy". An unsupported upward valuation is never permitted, and a previous impairment is never concealed.

**[PROPOSAL] A versioned revaluation policy per company**, approved independently (author and approver distinct, as the attachment retention policy of `docs/PHASE-R-attachment-retention.md`, plan item D-108), immutable per version, bound by hash to every document executed under it. It states:

1. whether write-downs are enabled at all (default of a new company: NO policy, so every revaluation is refused with `revaluation_policy_missing`);
2. the measurement basis the company applies, as a named choice with a free-text reference to its framework **[OPEN: RQ-4]**;
3. whether a reversal of an earlier write-down is permitted, and its limit **[OPEN: RQ-5]**;
4. the evidence a document must carry **[OPEN: RQ-6]**.

**Write-down.** **[PROPOSAL]** The document states, per position, the new lower value on a declared basis (section 9 gives the exact arithmetic). The system never computes a market or realisable value by itself and never derives one from price lists: the figure is the responsibility of the creator and of the independent approver, supported by the evidence the policy requires. A write-down never takes a layer or a position below zero; zero is permitted (a total write-off of value while the quantity stays, which the engine already supports for free goods).

**[OPEN: RQ-4] Measurement basis.** The SRS names no accounting framework. Option (a), proposed default: the policy only RECORDS the basis the company declares (for example "lower of cost and net realisable value under the company's framework"), and each document carries the approved figures and their evidence; HASM computes nothing. Option (b): HASM computes a candidate value from configured selling prices and costs to sell. (b) would be an invented valuation rule and is not proposed.

**Upward movement.** **[PROPOSAL]** HASM distinguishes two things and supports only the first:

* a **reversal of an earlier write-down**: the carrying value of stock that was written down, and is still on hand, moves back up, at most to the cost it would carry had it never been written down;
* an **upward revaluation above cost**: never supported. No document, policy version or permission can produce it; the limit below is enforced by the service and by the database rule of the implementing slice.

**[OPEN: RQ-5] Is a reversal permitted, and how far?** Option (a), proposed default for the FIRST implementation: write-down only; a reversal is refused (`revaluation_reversal_not_permitted`). It is the conservative choice (values only ever go down), at the price that a company whose framework requires reversing a write-down cannot do it in HASM until the option is enabled. Option (b): reversal permitted when the company's approved policy version enables it, limited per layer (FIFO) or per position (average) to the remaining write-down that is still attributable to the stock on hand (section 9.3). Option (c): a different limit set by the owner. Under every option the limit "never above original cost" holds.

**[PROPOSAL] No concealment.** A reversal never deletes, edits or nets away the earlier write-down: both stay as separate documents, separate cost rows, separate journal lines on (possibly) separate accounts, and both appear in the write-down register of section 10. A write-down is never "undone" by editing.

**[OPEN: RQ-7] Correction of a mistaken revaluation.** Option (a), proposed default: a wrong revaluation is corrected only by another approved revaluation document (a further write-down, or a reversal where the policy permits one); the engine's generic reversal is refused for revaluation operations (`revaluation_not_reversible`). Consequence: under a write-down-only policy an excessive write-down cannot be corrected upward at all. Option (b): additionally allow an authorised, independently approved cancellation of the whole document while NOTHING has moved on its positions since and its date is still in an Open period (the engine's existing "untouched inflow" rule makes this exact), with a reversing journal entry. (b) is a narrow error-correction path, not a valuation policy, but it is an upward movement and so it is left to the owner.

## 7. Topic 5 (taken first because the others depend on it): preserving FIFO layer provenance, quantities, ordering and historical dates

**[APPROVED: D-192, D-198]** No layer is collapsed, discarded or overwritten; original quantities, receipt dates, layer ordering and immutable lineage are preserved.

**[APPROVED: plan-execution design, section 11 item 5]** Two technical options are on record and the contract must choose explicitly.

**Option (i): one outflow and one inflow per layer, with lineage.** The revaluation takes the remaining quantity of every open layer of the position out at its present amount and puts the same quantities back as new layers at the new amounts, in the same order, in one transaction under the position lock. The operations have natural kinds (issue and receipt); their meaning comes from a new append-only lineage table (the pattern of migration 0020 and of the replay epochs).

* For it: no existing constraint, trigger or function changes; every engine invariant and `ReconcileAsync` hold unchanged; no reader of VALUES changes, so a reader that does not know about revaluations still values stock correctly; the original layers stay as immutable rows with their original quantity, amount, sequence and date.
* Against it: the successor layers carry the revaluation date and new arrival sequences. The original receipt date and rank are preserved only through the lineage, so every reader of DATES (aging, layer reports, as-of analysis by receipt date) must read the lineage. The pair shows as an outflow and an inflow of the full quantity, so movement and turnover reports must recognise it as value-only. An original receipt whose layer was cycled can no longer be reversed by the engine (`reversal_not_possible_goods_consumed`), which is correct but must be explained to the user.
* A condition that makes it order-preserving: EVERY open layer of the position is cycled, oldest first, including layers whose value does not change. Cycling only some layers would move them behind the others and silently change the FIFO order, which D-198 forbids.

**Option (ii): an additive per-layer value-adjustment table that the engine reads.** Layers keep their identity, rank and date; a new table holds the value adjustment per layer (or per average position).

* For it: layer identity, order and receipt date are preserved directly, with no lineage to follow; no artificial quantity movement.
* Against it: a position's value cannot change without an entry (the position trigger requires the sequence to advance by exactly one, and an entry needs a quantity), so the position and layer rows would stay at cost and the carrying value would be "cost plus adjustments". Every reader of VALUES must then change: issue costing, transfers, reversals, valuation, `ReconcileAsync`, the close probe, the replay. A reader that forgets the adjustment misstates the inventory value and the cost of goods sold. It is a change of the engine's readers that needs its own database and accounting review.

**[OPEN: RQ-8] The choice.** **Proposed default: option (i)**, because its failure mode is the safer one: a reader that has not been adapted shows a revaluation date where a receipt date was meant (a reporting defect that a test can pin), while under option (ii) such a reader misstates money. Option (i) also leaves every existing safeguard untouched, which is the rule the owner set for X1-A (D-190). The independent accounting reviewer and the database-safety reviewer are asked to confirm or reverse this choice; it is the single most consequential technical decision of the contract.

**[PROPOSAL] The lineage record (option (i)).** Append-only, written in the same transaction, one row per cycled layer:

* the revaluation document and its line; the position;
* the predecessor layer and the successor layer; the outflow consumption that emptied the predecessor;
* the **root layer**: the first layer of the chain, that is the layer created by the original receipt, transfer or conversion. It is carried forward unchanged through any number of revaluations, with the root's original receipt date, original arrival sequence, original quantity and original amount copied in for direct reading;
* the quantity cycled; the amount before; the amount after; the difference;
* the **cost basis**: the amount this quantity would carry had no write-down ever happened in the chain (equal to the amount before, at the first write-down; carried and scaled afterwards, section 9.3);
* the rank of the layer within the position at the time (so that "same order" is provable by a query).

For a moving-average position, which has no layers, one row per revaluation holds the position, quantity, value before, value after, difference and the reversible pool of section 9.3.

**[PROPOSAL] What "preserved" means, as checkable statements.** (1) The count of open layers of a position is the same after a revaluation as before. (2) Their order by arrival sequence maps one to one onto the order before. (3) The quantity of each successor equals the remaining quantity of its predecessor. (4) Every open layer reaches exactly one root through the lineage, and the root's row is unchanged since its creation. (5) No original layer, entry, consumption or operation row is updated except the predecessor's remaining figures going to zero through the ordinary consumption, as any issue does.

## 8. Topic 3: independent approval and audit

**[APPROVED: D-198]** Every financial effect has an independent approval. **[APPROVED: SRS 4, D-191 X3 and X4 for the replay]** The approver is never the creator, the last editor or the submitter; dedicated, scoped permissions and an appropriately privileged Inventory Cost Approver role; existing roles are not widened automatically.

**[PROPOSAL] Life cycle** (the pattern of stock documents and of the replay plan): draft, submitted, approved, executed; plus rejected (back to draft) and cancelled (before execution only). A document is immutable once submitted; an edit after a rejection starts a new revision and needs a new approval.

**[PROPOSAL] Separation of duties.** The approver is not the creator, not the last editor and not the submitter of the document. The person who executes may be any holder of the execute permission, including the creator, but execution is possible only for the exact approved revision. The journal entry then follows the journal workflow and its own approval policies (so a revaluation normally meets two independent approvals: the document's and the journal's) **[OPEN: RQ-3]**.

**[PROPOSAL] Permissions** (working names; privileged and classified with the access model of D-096; never added to an existing preset automatically): view, create, submit, approve, execute for revaluation documents, scoped by warehouse like stock posting; configure and approve for the revaluation policy and for the account mapping, company-wide. Whether document approval belongs to the Inventory Cost Approver role of X4 or to a separate role is **[OPEN: RQ-9]**; proposed default: the same role, because both are approvals of a change to inventory cost, and no separate role is invented.

**[PROPOSAL] Binding of the approval to what is executed.** The approval is bound to a fingerprint of: the document revision; the policy version and mapping version (hashes); per position the ordered list of open layers with their identifiers, remaining quantities and amounts at the preview; the exact new amounts; the revaluation date. Execution recomputes the fingerprint under the position locks and refuses on any difference (`revaluation_state_changed`): stock that moved between approval and execution invalidates the approval. This is the same-transaction execution binding of section 3 of the plan-execution design, reused, not a new mechanism.

**[PROPOSAL] Audit.** Business-audit events for: policy version saved and approved; mapping version saved; document created, submitted, approved, rejected, cancelled, executed; every refusal of an execution. Each carries identifiers, the fingerprint and totals, and no free text beyond the reason code. The reason text, the basis statement and the evidence references live in the document. Every step fails closed when its audit event cannot be written. The audit codes are allocated by the lead when the slice is planned.

**[OPEN: RQ-6] Evidence.** D-198 refuses execution when "original cost evidence" is unavailable. The original cost itself is always available in HASM (the root layer). What a document must carry to justify the NEW value is not defined by the sources. Option (a), proposed default: a mandatory reason code, a mandatory basis statement and at least one attachment per document (through the existing attachment module), all immutable after submission. Option (b): reason and basis statement only. (a) is the conservative choice.

## 9. Topic 4: FIFO and moving-weighted-average behaviour (exact arithmetic)

All amounts are in the base currency and are rounded only as section 3 describes. **[APPROVED: as built, 3.6]** The engine's rounding rule is not changed.

### 9.1 What the document states

**[PROPOSAL]** Per position the document states either (A) explicit new amounts, or (B) a target unit value from which the preview computes the amounts deterministically. In both cases the APPROVED and BOUND figures are exact amounts in minor units per layer (FIFO) or per position (average); no unit value is stored as the basis of a later computation (the engine's rule).

For (B), with target unit value `u` (at most 10 decimals, as derived unit costs) and remaining quantity `q`: target amount `T = round_half_away(q x u)` to the currency's minor units, computed exactly.

### 9.2 Write-down

* **FIFO, per open layer** with remaining quantity `q` and remaining amount `a`: new amount `a' = min(a, T)`. A layer whose amount is already at or below its target is cycled at an unchanged amount (`a' = a`). The write-down of the layer is `a - a'`; the position's write-down is the sum over its layers. There is no allocation of a position total across layers and therefore no allocation remainder: each layer has its own exact figure.
* **Moving weighted average, per position** with quantity `Q` and value `V`: new value `V' = min(V, T)` with `T` computed from `Q`. The write-down is `V - V'`.

Engine operations (option (i)): one issue of the whole on-hand quantity of the position (FIFO: consuming every open layer in order, each at its exact remaining amount; average: at the whole value, the exact-remainder rule), then, FIFO: one receipt per former layer, in the former order, with quantity `q` and amount `a'`; average: one receipt with `Q` and `V'`. All in one transaction under the position lock; the position passes through zero quantity inside the transaction and is never visible in that state.

### 9.3 Reversal of an earlier write-down (only if RQ-5 permits it)

The limit must hold "never above original cost" even after part of the written-down stock has been issued.

* **FIFO.** Each lineage row carries the cost basis `B` of the cycled quantity `q0`. For a successor layer with remaining quantity `q` (`q <= q0` after later issues) the ceiling is `C = floor(B x q / q0)` to minor units (rounded DOWN, so that rounding can never lift a value above cost). A reversal sets `a' = min(C, T)` and is refused for a layer with no write-down in its chain, or when `a' <= a` for every layer. When a layer is cycled again, its new row carries `B' = C` and `q0' = q`, so the basis shrinks with the stock and never grows.
* **Moving weighted average.** There are no layers, so the reversible amount is a position-level pool `P`: a write-down of `D` adds `D` to it; every later outflow of quantity `x` from on-hand `Q` reduces it to `floor(P x (Q - x) / Q)`; receipts and inbound transfers add nothing; a position that reaches zero quantity has `P = 0`. A reversal of `R` requires `R <= P` and reduces the pool by `R`. This proportional rule is a PROPOSAL: the average method blends costs, and the sources do not say how much of an earlier write-down still "sits" in a blended position **[OPEN: RQ-5]**.
* **Across a transfer.** **[APPROVED: D-018]** The actual cost moves with the transfer, so a transferred layer arrives at the destination with the written-down amount as its cost. **[PROPOSAL]** In the first implementation the cost basis and the pool do NOT travel with a transfer: at the destination the transferred amount is the cost, and a reversal there is refused. Carrying the basis across warehouses is possible through the existing source-layer link but is a larger design; it is left as **[OPEN: RQ-10]**.

### 9.4 Refusals common to both methods (working names)

`revaluation_policy_missing`, `revaluation_direction_not_permitted`, `revaluation_above_cost`, `nothing_to_revalue` (zero quantity, or no layer changes), `revaluation_state_changed`, `revaluation_date_invalid`, `position_has_open_obligation`, `position_in_pending_plan`, `tracked_item_not_supported_yet` (existing), `valuation_method_unresolved` (existing), `amount_precision_exceeded` (existing).

## 10. Topics 6 and 7: reporting of dates, and reconciliation

### 10.1 Historical receipt dates versus revaluation dates

**[APPROVED: D-192, D-198; SRS 9 "operation date distinct from the recording date"]** The two dates are reported clearly and separately; receipt dates are preserved.

**[PROPOSAL]** Every report and screen that shows a FIFO layer shows, from the lineage: the ORIGINAL receipt date and source document of the root layer; the original quantity and amount; each revaluation date with its document, direction and difference; the present quantity and carrying amount. Specifically:

* **Aging** of stock is computed from the root's original receipt date, never from a revaluation date.
* **Movement and turnover reports** show a revaluation as ONE value-only line per position (quantity change zero, value change the difference), with a marker; the internal outflow and inflow are never counted as an issue and a receipt, as a sale, as consumption or as turnover.
* **As-of valuation.** A revaluation is dated on its revaluation date and is never back-dated (section 11), so the valuation as of any earlier date is unchanged and stays reproducible. This differs from a replay epoch, which changes as-of values from the event date onward.
* **Cost of goods sold** after a write-down is drawn from the written-down amounts; the trace of an issued unit leads through the lineage to its root receipt (**[APPROVED: SRS 19 rule 8]**, traceability of the cost of goods).
* **Write-down register** (new report): per item, warehouse and root layer (or average position), the cumulative write-downs, the cumulative reversals, the part that left with issued stock, and the remaining reversible amount; with the documents behind every figure. This is how "never conceal a previous impairment" is made visible.

### 10.2 Reconciliation with journals and inventory valuation

**[APPROVED: as built]** `ReconcileAsync` and the close probe exist and are not weakened.

**[PROPOSAL]** After every revaluation, and as invariants in tests:

1. `ReconcileAsync` is empty (it needs no change under option (i)).
2. For each document: the sum of the lineage differences per position = the net value change of its two cost operations = the amount of its journal lines on the inventory account for that position; and the sum over the document = the amount on the counteraccounts. Debits equal credits.
3. Per period: the sum of all revaluation differences in the costing ledger = the movement of the inventory accounts that originates from revaluation journals = the opposite movement on the revaluation counteraccounts.
4. The close probe: the costing ledger up to the period end equals the balance of the inventory accounts once the revaluation's journal is posted; while it is pending, failed or cancelled the period cannot be closed (existing behaviour, 3.7C).
5. The write-down register reconciles to the lineage: remaining reversible amounts equal the figures recomputed from the lineage rows and later consumptions.
6. A journal entry of a revaluation that is cancelled or reversed OUTSIDE the revaluation's own process puts the document into the failed accounting state and blocks the close, as for stock postings.

## 11. Period rules

**[APPROVED: SRS 5 and 19 rule 5; D-191 X2 for the replay; plan-execution design section 11 item 8]** Open periods only; Final Closed is never touched.

**[PROPOSAL]** The revaluation date must lie in an **Open** period. Soft Closed is refused (the stock side has no soft-close override, as for the replay); Final Closed is refused. The date is not earlier than the last effective date of any position in the document (the engine refuses back-dating, and a revaluation is never a replay), and not later than the current date. The period is re-checked at execution and when the journal posts. If the database guard of STOCK-PERIOD-DB (D-220) exists by then, the revaluation's cost operations are subject to it like any other.

## 12. Multi-currency

**[APPROVED: SRS 5; as built 3.6 and 3.7A]** The SRS has multiple currencies with dated rates, and the ledger and the costing engine work in the company's base currency; inventory accounts limited to a foreign currency are refused.

**[PROPOSAL]** A revaluation is stated, approved, costed and journalised in the base currency only. Inventory is a non-monetary item carried in base currency, so no exchange-rate revaluation of stock exists in this contract; exchange differences on monetary items belong to Phase 3.18. A counteraccount limited to a foreign currency is refused.

## 13. Zero and negative-quantity positions, obligations, tracked items

* **Zero quantity.** **[PROPOSAL]** A position with no stock on hand has nothing to revalue and is refused (`nothing_to_revalue`). A FIFO layer with no remaining quantity is not cycled and keeps its history. A reversal can never be applied to stock that has left: the basis and the pool shrink with every outflow (section 9.3).
* **Zero value.** A layer or position with quantity and zero value (free goods) can be neither written down further nor, having cost zero, written up.
* **Negative quantity.** **[APPROVED: SRS 9, D-019]** Negative stock is forbidden by default and always for FIFO; the engine never holds a negative position. The limited moving-average exception is implemented as obligations with a provisional cost and later settlement (3.8B, migration 0020). **[PROPOSAL]** A position with an OPEN negative-stock obligation is refused for revaluation (`position_has_open_obligation`) until the obligation is settled, because its cost is provisional **[OPEN: RQ-11]**.
* **Lot, serial and expiry items.** **[APPROVED: as built]** The engine refuses them today (`tracked_item_not_supported_yet`); their revaluation is designed with Phase 3.8C (OD-11, D-219), not here.

## 14. Interaction with replay epochs (X5, X6) and with pending replay plans

**[APPROVED: D-191]** X5: a second replay plan on a position already affected by an executed epoch is refused in the initial implementation. X6: unsupported reversals affecting an executed epoch are refused. X7: a document bound to a pending replay plan cannot be posted through the normal path.

**[PROPOSAL]** The same caution in both directions, each a refusal in the first implementation:

1. **Revaluation while a replay plan is pending.** A position that belongs to the closure of a replay plan that is submitted or approved and not yet executed, cancelled or expired is refused for revaluation (`position_in_pending_plan`). The plan's fingerprint binds the position's history; a revaluation would invalidate it.
2. **Replay over a revaluation.** A replay plan whose closure would have to storno or rebook the cost operations of a revaluation (a back-dated event earlier than the revaluation on that position) is refused (working name `plan_on_revalued_position`), exactly as X5 refuses a plan on an epoch. Replaying across a revaluation would need rules for recomputing every write-down, which this contract does not define.
3. **Revaluation after an executed epoch.** A revaluation is a forward-dated operation on the present state, not a second replay; it is permitted on a position that has epoch members. Its lineage roots are then the epoch's rebooked layers, whose own epoch lineage leads to the original receipts; reports follow both.
4. **Reversals.** Revaluation operations are not reversible through the generic reversal (section 6, RQ-7). The X6 database rule protects epoch members; an equivalent additive rule protects revaluation members. An original stock document whose receipt layer was cycled can no longer be reversed (the engine already refuses); the user is told why.
5. **Method conversion (D-045).** Conversion preserves the carrying value it finds, which after a write-down is the written-down value; the lineage and the write-down register survive a conversion unchanged. The conversion contract, not this one, defines the rest.

**[OPEN: RQ-12]** Whether items 1 and 2 stay refusals for Release 1 or must later be supported.

## 15. Worked examples (base currency with two decimals)

### 15.1 FIFO write-down

Position before, item I in warehouse W, three open layers in arrival order:

| Layer | Original receipt | Original qty and amount | Remaining qty | Remaining amount | Unit (derived) |
|---|---|---|---|---|---|
| L1 | 2026-01-10 | 10 for 100.00 | 6 | 60.00 | 10.00 |
| L2 | 2026-02-05 | 5 for 75.00 | 5 | 75.00 | 15.00 |
| L3 | 2026-03-01 | 8 for 64.00 | 8 | 64.00 | 8.00 |

Position: quantity 19, value 199.00. Revaluation dated 2026-03-31 with target unit value 9.00.

| Layer | Target `T` | New amount `min(a, T)` | Write-down |
|---|---|---|---|
| L1 | 6 x 9.00 = 54.00 | 54.00 | 6.00 |
| L2 | 5 x 9.00 = 45.00 | 45.00 | 30.00 |
| L3 | 8 x 9.00 = 72.00 | 64.00 (already below target, unchanged) | 0.00 |

Cost operations: issue 19 for 199.00 (consuming L1 60.00, L2 75.00, L3 64.00); receipts L1' 6 for 54.00, L2' 5 for 45.00, L3' 8 for 64.00, in that order. Position after: quantity 19, value 163.00. Lineage: L1 to L1' (root L1, original date 2026-01-10, basis 60.00 for 6), L2 to L2' (root L2, 2026-02-05, basis 75.00 for 5), L3 to L3' (root L3, 2026-03-01, basis 64.00 for 8, no write-down).

Journal entry, dated 2026-03-31:

| Account | Debit | Credit |
|---|---|---|
| Write-down account | 36.00 | |
| Inventory account of the item | | 36.00 |

A later issue of 8 units costs L1' whole (54.00) plus 2 of the 5 units of L2' (45.00 x 2 / 5 = 18.00): 72.00. Without the write-down it would have cost 60.00 + 30.00 = 90.00. The 18.00 of write-down that left with these units is no longer reversible.

### 15.2 FIFO reversal (only if RQ-5 permits it)

After that issue: L2' has 3 units for 27.00; L3' has 8 units for 64.00. Revaluation dated 2026-05-31 with target unit value 12.00.

| Layer | Ceiling `C = floor(B x q / q0)` | Target `T` | New amount `min(C, T)` | Reversal |
|---|---|---|---|---|
| L2' | 75.00 x 3 / 5 = 45.00 | 3 x 12.00 = 36.00 | 36.00 | 9.00 |
| L3' | no write-down in its chain | 8 x 12.00 = 96.00 | 64.00 (unchanged; never above cost) | 0.00 |

Journal entry: debit the inventory account 9.00, credit the write-down reversal account 9.00. The register shows for root L2: written down 30.00, left with issued stock 12.00, reversed 9.00, still reversible 9.00.

A rounding case for the ceiling: a root of 3 units for 100.00, of which 2 units for 66.67 remain when it is first written down, has basis 66.67 for 2. If one more unit is issued, the ceiling of the last unit is `floor(66.67 x 1 / 2) = 33.33`, never 33.34.

### 15.3 Moving weighted average write-down and reversal

Position: quantity 20, value 400.00 (derived unit 20.00). Revaluation with target unit value 17.50: `T = 350.00`, new value 350.00, write-down 50.00. Cost operations: issue 20 for 400.00, receipt 20 for 350.00. Journal: debit the write-down account 50.00, credit the inventory account 50.00. Pool `P = 50.00`.

Then a receipt of 10 for 200.00: quantity 30, value 550.00, pool unchanged. Then an issue of 12: cost 550.00 x 12 / 30 = 220.00; quantity 18, value 330.00; pool `floor(50.00 x 18 / 30) = 30.00`.

A reversal with target unit value 25.00: `T = 450.00`, wanted increase 120.00, limited by the pool to 30.00; new value 360.00. Journal: debit the inventory account 30.00, credit the write-down reversal account 30.00. Pool 0.00.

## 16. Owner questions (each with the proposed conservative default; none is decided here)

| # | Question | Proposed default | Main alternative and its consequence |
|---|---|---|---|
| RQ-1 | Which accounts take revaluation differences, and may HASM constrain their type? | A versioned mapping with a write-down account and a write-down reversal account, company default with item-group overrides; type not constrained; refusal when missing | A fixed account type per line would be a chart rule the SRS does not contain |
| RQ-2 | Does a write-down credit the inventory account or a separate allowance account? | The inventory account, so the costing ledger and the general ledger stay equal | An allowance account changes the close probe and every valuation report, or leaves the layers unrevalued |
| RQ-3 | Is the revaluation journal a pending draft posted through the journal workflow, or posted at execution? | Pending draft with the derived accounting state; the close stays blocked until it posts | One-step posting removes the second approval and the pending state |
| RQ-4 | On which measurement basis is a lower value determined? | The policy records the basis the company declares; each document carries approved figures and evidence; HASM computes no market value | A computed value from price lists would be an invented valuation rule |
| RQ-5 | Is a reversal of an earlier write-down permitted, and to what limit? | First implementation: write-down only. If enabled later: limited to the write-down still attributable to the stock on hand, never above original cost (per layer for FIFO; a proportional pool for the average method) | Without reversals a company whose framework requires them cannot follow it in HASM |
| RQ-6 | What evidence must a revaluation document carry? | Reason code, basis statement and at least one attachment, immutable after submission | Reason and statement only |
| RQ-7 | How is a mistaken revaluation corrected? | Only by another approved revaluation; no generic reversal | A narrow, independently approved cancellation while nothing has moved and the period is Open |
| RQ-8 | Technical representation: per-layer outflow and inflow with lineage, or an additive adjustment table read by the engine? | The outflow and inflow pair with lineage, cycling every open layer in order (no existing safeguard or value reader changes) | The adjustment table keeps layer identity and dates directly but changes every reader of values; needs its own review |
| RQ-9 | Who approves a revaluation document? | The Inventory Cost Approver role of X4, never the creator, last editor or submitter; no existing role widened | A separate revaluation approver role |
| RQ-10 | Does the right to reverse a write-down travel with a transfer to another warehouse? | No: at the destination the transferred amount is the cost; a reversal there is refused | Carrying the basis through the source-layer link, a larger design |
| RQ-11 | May a position with an open negative-stock obligation be revalued? | No, until the obligation is settled | Revaluing a provisional cost |
| RQ-12 | Do the refusals between revaluations and replay plans stay for Release 1? | Yes: no revaluation on a position in a pending plan, and no replay across a revaluation | Supporting either needs recomputation rules this contract does not define |

## 17. Invariants an implementation must keep (each pinned by at least one test)

* I1. No original layer, entry, consumption, operation or journal row is updated or deleted by a revaluation, beyond the remaining figures of a consumed layer.
* I2. Quantities never change: per position, quantity after = quantity before; per layer, successor quantity = predecessor remaining quantity.
* I3. The number and the order of the open layers of a position are the same before and after, and every open layer has exactly one root with an unchanged original receipt date, quantity and amount.
* I4. No amount is ever above its ceiling: a carrying amount never exceeds the cost the same quantity would carry without any write-down; without an enabled reversal policy no amount ever increases.
* I5. Every changed position has exactly one journal effect, balanced, equal to its cost effect, on accounts resolved from the bound mapping version.
* I6. No execution without an approved, unexpired, exactly matching approval by a person who is not the creator, last editor or submitter; one execution per approval.
* I7. No execution outside an Open period, before a position's last effective date, or in the future.
* I8. `ReconcileAsync` is empty and the cost ledger equals the inventory accounts once the journal is posted; until then the close is blocked.
* I9. Idempotent: a repeated or concurrent execution of the same document changes nothing and returns the first result.
* I10. No existing constraint, trigger, function or guard is dropped, replaced, disabled or widened (before and after catalog comparison).
* I11. Refusals are complete rollbacks: no layer, entry, lineage row, journal or audit event of a refused execution remains, except the audited refusal itself.
* I12. Nothing is computed from a stored unit value; all bound figures are exact amounts in minor units.

## 18. Test matrix an implementation must pass before activation

Every test is a PostgreSQL test on the isolated cluster unless marked unit. The numbering is local to this document.

| # | Test | Proves |
|---|---|---|
| RT1 | FIFO write-down of three layers, one below target (example 15.1) | amounts, journal, layer count and order, lineage roots |
| RT2 | FIFO issue after a write-down draws the written-down amounts; trace of the issued units reaches the root receipts | cost of goods sold and traceability |
| RT3 | FIFO partial layer and thirds rounding; ceiling rounded down (example 15.2 rounding case) | I4, I12 |
| RT4 | Average write-down (example 15.3) and the exact-remainder rule through zero quantity inside the transaction | I2, I8 |
| RT5 | Reversal refused when the policy does not permit it; refused above the ceiling; refused for a layer with no write-down | I4 |
| RT6 | Reversal permitted by policy: FIFO per layer and average pool, after partial issues, receipts and a transfer out | section 9.3 |
| RT7 | Transfer of written-down stock; reversal at the destination refused | RQ-10 default |
| RT8 | Cycling only some layers is impossible: every open layer is cycled and the order mapping is one to one | I3 |
| RT9 | Approver equal to creator, last editor or submitter refused; revoked approver refused at decision; approval bound to the revision | I6 |
| RT10 | Stock moves between approval and execution: `revaluation_state_changed`, nothing written | binding |
| RT11 | Twelve concurrent executions of one document: exactly one effect; replay returns the first result | I9 |
| RT12 | Concurrent issue and revaluation on one position serialise under the position lock; both orders give reconciled results | concurrency |
| RT13 | Soft Closed, Final Closed, missing period, date before the last effective date, future date: each refused | I7 |
| RT14 | Mapping missing, account inactive, not postable, an inventory account, foreign-currency limited: each refused with nothing left behind | I5, I11 |
| RT15 | Policy missing or not approved: refused; policy version and mapping version hashes bound in the document | section 6 |
| RT16 | Journal pending blocks the period close; posted clears it; journal cancelled outside puts the document into the failed state and blocks the close | I8, section 10.2 |
| RT17 | Per-document and per-period reconciliation identities of section 10.2, including after a 150-step random scenario of receipts, issues, transfers, reversals and revaluations | reconciliation |
| RT18 | Before and after catalog comparison of every existing guard function, trigger and constraint | I10 |
| RT19 | Raw SQL as the runtime role cannot insert a lineage row for a foreign document, cannot write an amount above the ceiling, cannot update or delete lineage | database rules of the slice |
| RT20 | Position with an open negative-stock obligation refused; zero-quantity position refused; zero-value layer neither written down nor up | section 13 |
| RT21 | Position in the closure of a pending replay plan refused; a replay plan across a revaluation refused; revaluation after an executed epoch permitted with both lineages readable | section 14 |
| RT22 | Generic reversal of a revaluation operation refused; reversal of an original receipt whose layer was cycled refused with a clear code | section 14 item 4 |
| RT23 | Aging by original receipt date; movement report shows one value-only line; as-of valuation before the revaluation date unchanged | section 10.1 |
| RT24 | Write-down register equals the figures recomputed from the lineage after issues and reversals | no concealment |
| RT25 | Audit event missing or failing makes the step fail closed | section 8 |
| RT26 | Permissions: each action refused without its permission and outside the warehouse scope; no existing preset gained a permission | section 8 |
| RT27 | Unit: the arithmetic of sections 9.1 to 9.3 on boundary values (huge quantities, three-decimal currency, zero-decimal currency) | I12 |
| RT28 | Shared screen tests (Web and Tauri identical): preview, the two dates shown separately, refusal messages | shared frontend rule |

## 19. What happens next, and what stays closed

1. This proposal goes to an **independent accounting review** (and, for section 7, a database-safety review).
2. The owner answers RQ-1 to RQ-12 and approves the contract explicitly, or changes it.
3. Only then is an implementation slice planned: an additive company migration (lineage, documents, policy and mapping versions; no existing safeguard changed), services, the shared screen, and the tests of section 18.
4. **Revaluation execution stays CLOSED until the approved contract and the required tests have passed their review.** Recording this proposal activates nothing.
