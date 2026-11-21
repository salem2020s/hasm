# HASM - Phase 3.8D: inventory revaluation accounting contract (PROPOSAL, design only)

**Status: PROPOSAL for independent accounting review and explicit owner approval. Nothing here is built, no migration is written, and REVALUATION EXECUTION STAYS CLOSED.** The owner's decisions D-192 and D-198 require this contract, its independent review and the required tests before revaluation is activated ("Do not activate revaluation until the contract and required tests are approved", D-198). Until then the engine keeps refusing a method or value change (`valuation_method_changed`), and no revaluation document, account mapping or permission exists.

**Revision 2.** The first independent accounting review (Agent FAC, at `6fc1810`: request changes, 1 High, 8 Medium, 5 Low) is worked in. Where the reviewer recommends another default than revision 1, the recommendation is now the proposal and both are shown. Section 18 lists the reviewer's recommendation per owner question.

How to read it. Every section separates three kinds of statement:

* **[APPROVED: source]** a rule already settled by the approved SRS or by a recorded owner decision; it is cited and not reopened.
* **[PROPOSAL]** a rule this document proposes; it binds nobody until the contract is approved. A rule taken from the unmerged design of PR 48 is a PROPOSAL too, and says where it comes from.
* **[OPEN: RQ-n]** a point the approved sources do not settle. Options are given with their consequences, one is marked as the proposed default, and the point is listed as an owner question in section 17. No accounting policy is decided by this document.

No decision ID, audit code, permission name or migration number is allocated here; names in this document are working names.

## 1. Sources (what was read, and where)

* **SRS** (`docs/SRS.md` on `main`): section 9 (two valuation methods with "controls for changing the method and revaluation"; cost per item and per warehouse; negative stock forbidden by default and always for FIFO, lot and serial items; retroactive movements through separate, approved adjustment documents; posted journals never edited, Final Closed periods never posted into; operation date distinct from recording date), section 5 (posted entries are never changed or deleted, a correction is a reversal or an adjustment entry; periods Open, Soft Closed, Final Closed; base currency), section 4 (approvals and separation of duties; a posted document is corrected by a reversal or adjustment document), section 15 (Trial counting) and section 19 (rules 2, 3, 5 and 8).
* **Decisions on `main`** (`docs/DECISIONS.md`): D-017, D-018, D-019, D-190 and D-191 (the replay design and X2 to X8), D-192 (revaluation stays in Phase 3.8D and in Release 1; the seven topics this contract must cover), D-198 (the approved policy direction), D-203 (returns), D-215 (Trial counting), D-216 (execution and accounting states are separate), D-220 (the database guard against stock and cost postings into Soft Closed, Final Closed or missing periods).
* **Decisions read on the branch of PR 48, not on `main`** (`feature/phase-3-8d-impact-preview` at `c0e15d9`, `docs/DECISIONS.md`): D-044 (compensation by separate, approved, traceable entries; original posted journals and historical cost operations are never modified) and D-045 (prospective method conversion preserves quantity and carrying value; "Any separate value revaluation still needs its accounting/approval contract; this decision does not silently approve one").
* **The plan-execution design on the same branch** (`docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` at `c0e15d9`), sections 1, 2, 3 and 11. It is team design text on an unmerged branch (its final revision is independently approved for the replay, D-190), not an owner decision about revaluation; what this contract takes from it is marked PROPOSAL.
* **The costing engine as built** (`docs/PHASE-3.6.md`; company migration 0014; `InventoryCostingService.cs`), **inventory accounting as built** (`docs/PHASE-3.7A.md`, `3.7B`, `3.7C`; migrations 0015 and 0016; `InventoryValuationProbe.cs`), **negative-stock obligations as built** (`docs/PHASE-3.8B-3.md`; migration 0020; `NegativeStock.cs`), and the stock ledger reader (`StockBalance.cs`).

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

**[APPROVED: SRS 4, SRS 5, SRS 19 rule 3, D-044]** A posted journal entry and a historical cost operation are never edited or deleted; a posted document is corrected by a separate reversal or adjustment document. **[APPROVED: SRS 19 rule 5]** Nothing is posted into a Final Closed period, and no automatic task bypasses an approval. **[APPROVED: SRS 19 rule 2]** One business event creates at most one financial and one stock effect. **[APPROVED: D-220]** Stock and cost postings into Soft Closed, Final Closed or missing periods are to be refused by an additive database guard.

**What the approved sources do NOT say**, and this document therefore does not assume: which accounting framework a company follows; how a lower value is measured and at which unit (layer, position, item, group); which accounts take the difference; whether and how far a write-down may be reversed; how a mistaken revaluation is corrected; what evidence is required. These are section 17.

## 3. Facts of the engine that constrain the design (verified in the sources named in section 1)

* Cost entries, consumptions and operations are append-only. Only a layer's remaining figures and a position's totals change, and a position moves by exactly one entry at a time.
* **A value-only entry is impossible:** an entry needs a quantity greater than zero (constraint `k_0b3e9de8`). **The operation kinds cannot be widened** (receipt, issue, transfer, reversal; constraint `k_f356c89a`), and the migration guard refuses dropping a constraint.
* A position's last effective date never decreases; the service refuses a back-dated entry.
* FIFO consumes strictly oldest first by arrival sequence. A partial issue costs `amount x part / whole`, computed exactly and rounded half away from zero to the currency's minor units; emptying a layer or a position takes the exact remainder. Unit costs are derived for display and never used to compute a cost.
* One costing request cannot carry two lines of the same item (`duplicate_item_line`, `InventoryCostingService.cs`), so several inflows of one item into one position are several operations today.
* Reversal is per operation: an outflow gets back exactly the layers and amounts it consumed; an inflow is taken out again only while its stock is untouched.
* A position stores its last positive quantity and value (the "last average" pair of migration 0020), written by its entries only.
* All amounts are in the company's base currency. Inventory accounts limited to a foreign currency are refused by the configuration (3.7A).
* `ReconcileAsync` proves, among others: position = signed sum of its entries; open FIFO layers = position; company value = signed sum of all entries.
* The close probe `inventory_valuation_differences` blocks a period close while the costing ledger up to the period end differs from the balance of the inventory accounts in the general ledger. A stock posting whose journal entry was cancelled or reversed is "failed" and is repaired by reversing the stock (3.7C).

Consequence. A revaluation cannot be "a value change on a row". It has to be expressed either with ordinary quantity-carrying entries (an outflow and an inflow of the same quantity at different values) or through a new additive structure that the engine's readers take into account. Section 7 compares the two.

## 4. Scope of a revaluation

**[PROPOSAL]** A revaluation is a separate, approved document (working name: inventory revaluation document) that changes the CARRYING VALUE of the stock on hand of one or more positions (item and warehouse, the costing unit of **[APPROVED: D-018]**) without changing any quantity, any posted journal or any historical cost row. It is never created by an automatic task, a recurring run, a replay or a method conversion. A document has ONE direction: it is either a write-down document or a reversal document, never both.

**Not a revaluation, and out of this contract:** purchase-cost adjustments and landed cost (OD-2, D-218, Phase 3.15); quantity differences (stocktakes and adjustments, 3.8B); the correction of a wrong receipt amount (reversal and new operation, or the replay of 3.8D); method conversion (D-045, D-193, D-199), which preserves the carrying value and has its own contract; exchange-rate differences (Phase 3.18; see section 12).

## 5. Topic 1: revaluation accounting and counteraccounts

**[APPROVED: D-198]** Versioned, company-specific mappings; a balanced, traceable journal for every financial effect; refusal when the mapping is unavailable.

**[APPROVED: as built, 3.7A]** The inventory (asset) account of an item is resolved by the versioned inventory accounting configuration: the item group's own account, else the nearest parent's, else the company default.

**[PROPOSAL] Mapping.** A new versioned, append-only revaluation mapping per company, built like the 3.7A configuration (immutable versions, content hash, validated against the live chart when saved AND again when a document executes). It holds a company default and optional overrides per item group, resolved along the same group chain, for two counteraccounts:

1. the **write-down account** (debited by a write-down);
2. the **write-down reversal account** (credited by a permitted reversal). It may be the same account as (1) when the company's policy presents a reversal as a reduction of the expense.

Each must be an active, postable account, must not be an inventory account of the current inventory configuration, must not be bound to a cash or bank account or be a party control account, and must not be limited to a foreign currency. The account TYPE (expense, income, other) is the company's choice and is not constrained by HASM beyond these rules, because the SRS fixes no chart **[OPEN: RQ-1]**.

**[PROPOSAL] Refusals** (stable codes, working names): `revaluation_mapping_not_configured`, `revaluation_account_inactive`, `revaluation_account_not_postable`, `revaluation_account_is_inventory_account`, `inventory_account_not_configured` (existing). A refusal rolls everything back; no default account is ever seeded or guessed (as in 3.7B).

**[PROPOSAL] The journal.** One journal entry per revaluation document, created through the existing automatic journal path in the transaction that executes the cost effect, with a unique source key (working form `inventoryrevaluation:<id>`), dated on the revaluation date, with one pair of lines per position:

| Direction | Debit | Credit | Amount |
|---|---|---|---|
| Write-down | write-down account | inventory account of the item | the decrease of the position's carrying value |
| Reversal of an earlier write-down | inventory account of the item | write-down reversal account | the increase of the position's carrying value |

Each line carries the branch of the warehouse, as stock postings do. Lines of the same account and branch may be aggregated in the entry; the per-position figures stay in the revaluation document and its lineage (section 7). A position whose value does not change gets no line; a document whose total effect is zero is refused (`nothing_to_revalue`).

**[OPEN: RQ-2] Where the write-down is credited.** Three options:

* (a) **Proposed default:** the INVENTORY account itself, so the costing ledger and the inventory accounts stay equal and the existing close probe keeps working unchanged. Gross cost is then visible in HASM's lineage and write-down register, not in the general ledger.
* (b) A separate allowance (contra-asset) account outside the inventory account set, with the inventory account kept at cost. Consequence: either the costing ledger stays at cost (then the layers are not revalued and cost of goods sold stays at the old cost, a different model from the one D-192 describes), or the close probe and every valuation report must net two accounts.
* (c) A write-down sub-account INSIDE the inventory account set of the configuration (the set the close probe sums). Gross cost and accumulated write-down are then both visible in the general ledger and the probe equality is kept, at the price of extending the 3.7A configuration and its validation.

Note for every option: an allowance booked by hand in the general ledger outside HASM changes nothing in the costing ledger; the close probe notices it only if it is booked on an inventory account.

**[OPEN: RQ-3] When the journal is posted.** This is the point the review rated High, because of what the options do in combination with section 15.

* (a) **Proposed default (changed in revision 2 on the reviewer's recommendation): posted in the SAME TRANSACTION as the cost effect.** The revaluation document already carries its own independent approval (section 8), which is the approval D-198 requires for the financial effect; the journal entry is the automatic entry of an approved document (SRS 7 allows automatic posting). The costing ledger and the general ledger can then never diverge: either both effects commit or neither does. A posting refusal (period, account, journal validation) refuses the whole execution.
* (b) Revision 1's default: a pending draft entry posted later through the journal workflow with its own approval, as stock documents do today (3.7B). Its consequences must be stated plainly: (1) while the draft is pending, later issues already post cost of goods sold at the written-down amounts although the write-down expense is not yet in the general ledger, so interim figures are understated for expense and misstated between accounts; (2) the period cannot be closed (the existing probe); (3) if the journal approver rejects or cancels the draft, the costing ledger is written down and the general ledger never follows. For a stock document that state is repaired by reversing the stock; for a revaluation it needs a defined repair. Option (b) is therefore acceptable ONLY with this repair path, which the contract specifies: the draft of an executed revaluation can be cancelled only through the revaluation's own cancellation (section 15.1); if the draft is nevertheless cancelled or rejected, the document enters the failed accounting state, and the single permitted repair is a deterministic RE-ISSUE of the identical journal entry (same lines, amounts, date and source key generation, bound to the executed document's fingerprint), which again needs approval and posting; no manual entry on an inventory account is a repair.

## 6. Topic 2: write-down, and permitted write-up or reversal

**[APPROVED: D-198]** Write-downs are supported when separately approved. Reversals or write-ups are permitted "only according to an explicitly configured and reviewed accounting policy". An unsupported upward valuation is never permitted, and a previous impairment is never concealed.

**[PROPOSAL] A versioned revaluation policy per company**, approved independently (author and approver distinct, as the attachment retention policy of `docs/PHASE-R-attachment-retention.md`, plan item D-108), immutable per version, bound by hash to every document executed under it. It states:

1. whether write-downs are enabled at all (default of a new company: NO policy, so every revaluation is refused with `revaluation_policy_missing`);
2. the measurement basis the company applies, as a named choice with a free-text reference to its framework **[OPEN: RQ-4]**, and the unit at which the lower value is tested **[OPEN: RQ-5]**;
3. whether a reversal of an earlier write-down is permitted, and its limit **[OPEN: RQ-6]**;
4. the evidence a document must carry **[OPEN: RQ-7]**.

**Write-down.** **[PROPOSAL]** The document states, per position, the new lower value on a declared basis (section 9 gives the exact arithmetic). The system never computes a market or realisable value by itself and never derives one from price lists: the figure is the responsibility of the creator and of the independent approver, supported by the evidence the policy requires. A write-down never takes a layer or a position below zero; zero is permitted (the engine already carries quantity at zero value for free goods).

**[OPEN: RQ-4] Measurement basis.** The SRS names no accounting framework. Option (a), proposed default: the policy only RECORDS the basis the company declares (for example "lower of cost and net realisable value under the company's framework"), and each document carries the approved figures and their evidence; HASM computes nothing. Option (b): HASM computes a candidate value from configured selling prices and costs to sell. (b) would be an invented valuation rule and is not proposed.

**[OPEN: RQ-5] Unit of comparison for the lower-of test (new in revision 2).** "Lower than what, measured where" is an accounting policy that neither the SRS nor an owner decision settles; revision 1 presented one answer as arithmetic. The options, with the figures of example 16.1 (position value 199.00, target unit value 9.00):

* (a) **Per FIFO layer** (and per position for the average method, which has no layers): each layer is compared with its own target. Write-down 36.00. No allocation is needed. It gives the lowest carrying value, because a layer above the target is written down even when other layers of the same position are below it. **Proposed default**, as the most prudent result and the only option that needs no allocation rule; the owner may well prefer (b).
* (b) **Per position** (item and warehouse, the costing unit of D-018): the position's total is compared with `Q x u`. Write-down 28.00 (199.00 against 171.00). Under FIFO the position's write-down must then be ALLOCATED to layers; the proposed allocation rule is in section 9.2.
* (c) **Per item across warehouses**, or (d) **per item group**: a larger unit nets gains in one place against losses in another. Both contradict the per-warehouse costing unit of D-018 unless an allocation across positions is defined; neither is proposed, and either would need its own rule.

**Upward movement.** **[PROPOSAL]** HASM distinguishes three things:

* a **reversal of an earlier write-down**: the carrying value of stock that was written down, and is still on hand, moves back up, at most to the cost it would carry had it never been written down (RQ-6);
* the **cancellation of a mistaken revaluation document** (section 15.1, RQ-8): the document is taken back as a whole and the documented cost is restored; this is error correction, not a valuation;
* an **upward revaluation above cost**: never supported. No document, policy version or permission can produce it.

**[OPEN: RQ-6] Is a reversal permitted, and how far?** Option (a), proposed default for the FIRST implementation: write-down only; a reversal document is refused (`revaluation_reversal_not_permitted`). It is the conservative choice (values only ever go down). It is acceptable only together with the cancellation path of RQ-8, otherwise an error could never be corrected upward. Option (b): reversal permitted when the company's approved policy version enables it, limited per layer (FIFO) or per position (average) to the write-down still attributable to the stock on hand (section 9.3). Option (c): a different limit set by the owner. Under every option the limit "never above original cost" holds.

**[PROPOSAL] No concealment.** A reversal or a cancellation never deletes, edits or nets away the earlier write-down: both stay as separate documents, separate cost rows and separate journal entries, and both appear in the write-down register of section 10. The register is append-only and its cumulative columns never decrease.

## 7. Topic 5 (taken first because the others depend on it): preserving FIFO layer provenance, quantities, ordering and historical dates

**[APPROVED: D-192, D-198]** No layer is collapsed, discarded or overwritten; original quantities, receipt dates, layer ordering and immutable lineage are preserved.

**[PROPOSAL, from section 11 item 5 of the plan-execution design on PR 48]** Two technical options are on record there, and that design says the contract must choose explicitly.

**Option (i): one outflow and one inflow per layer, with lineage.** The revaluation takes the remaining quantity of every open layer of the position out at its present amount and puts the same quantities back as new layers at the new amounts, in the same order, in one transaction under the position lock. The operations have natural kinds (issue and receipt); their meaning comes from a new append-only lineage table (the pattern of migration 0020 and of the replay epochs). Because one request cannot hold two lines of one item (section 3), the inflows are one receipt operation per layer, or a dedicated internal entry point of the engine; the implementing slice chooses and has it reviewed.

* For it: no existing constraint, trigger or function changes; every engine invariant and `ReconcileAsync` hold unchanged; every reader of BALANCES (position value, open layers, valuation as of a date, the close probe) stays correct without change; the original layers stay as immutable rows with their original quantity, amount, sequence and date.
* Against it, stated without softening (review M1):
  * The successor layers carry the revaluation date and new arrival sequences. The original receipt date and rank are preserved only through the lineage, so every reader of DATES must read the lineage.
  * **Readers of FLOWS and readers keyed on the entry kind are wrong until adapted, and wrong in money, not only in dates.** The pair adds an outflow and an inflow of the whole on-hand quantity and value. Any reader that totals issue or receipt entries (movement cards, turnover, cost of goods sold taken from entries, receipts at cost, last purchase cost, received quantity from layer originals) would misstate quantities and amounts by the full on-hand value. Revision 1's sentence "a missed reader shows a wrong date, not wrong money" was false and is withdrawn.
  * **Every earlier receipt and transfer-in of the position becomes irreversible** through the engine (`reversal_not_possible_goods_consumed`), including those whose layer value did not change, because every open layer is cycled. Corrections of such documents then need new documents (section 15).
* A condition that makes it order-preserving: EVERY open layer of the position is cycled, oldest first, including layers whose value does not change. Cycling only some layers would move them behind the others and silently change the FIFO order, which D-198 forbids.

**Option (ii): an additive per-layer value-adjustment table that the engine reads.** Layers keep their identity, rank and date; a new table holds the value adjustment per layer (or per average position).

* For it: layer identity, order and receipt date are preserved directly, with no lineage to follow; no artificial quantity movement, so flow readers are unaffected; earlier receipts stay reversible in principle.
* Against it: a position's value cannot change without an entry (the position trigger requires the sequence to advance by exactly one, and an entry needs a quantity), so the position and layer rows would stay at cost and the carrying value would be "cost plus adjustments". Every reader of BALANCES and of cost must then change: issue costing, transfers, reversals, valuation, `ReconcileAsync`, the close probe, the replay. A reader that forgets the adjustment misstates the inventory value and the cost of goods sold. It is a change of the engine's readers that needs its own database and accounting review.

**[OPEN: RQ-9] The choice.** **Proposed default: option (i)**, with its real trade stated: under (i) BALANCES stay right by construction and FLOW and KIND-KEYED readers must each be adapted and tested; under (ii) flows stay right and every BALANCE and cost reader must be adapted. A missed balance reader misstates the inventory value and every later cost of goods sold, which is the worse failure; a missed flow reader misstates a report. Option (i) also leaves every existing safeguard untouched, the rule the owner set for X1-A (D-190). The choice is conditional on the reader inventory below being completed and tested, and on a database-safety review.

**[PROPOSAL] Inventory of readers of cost entries and layers (each needs a stated behaviour and a test before activation).** The list comes from a search of the backend sources on `main` for the cost tables; the implementing slice must repeat the search and extend the list, and the replay readers live on the PR 48 branch.

| Reader | What it reads | Required behaviour with revaluation members |
|---|---|---|
| `InventoryCostingService` issue, transfer, reverse | layers and positions | unchanged; draws the written-down amounts (balances) |
| `InventoryCostingService.ReconcileAsync` | entries, layers, consumptions | unchanged; must stay empty |
| `InventoryValuationProbe` (close probe) | sum of cost entries against inventory accounts | unchanged under RQ-2 (a); pinned by test |
| `StockBalance` stock ledger (movement card; reads entries with their kind and the source document) | entries by sequence | must show a revaluation as ONE value-only line with a marker, never as an issue and a receipt |
| `NegativeStock.LatestReceiptAsync` (provisional cost source 2) | the latest receipt entry of a stock-document receipt (it joins the stock posting row of a receipt document) | as read in the code it does not select a revaluation inflow as long as a revaluation is NOT recorded as a stock posting of a receipt document; this must be pinned by a test, and what it SHOULD return after a write-down is RQ-12 |
| `NegativeStock` last-average pair (provisional cost source 1) | the position's last positive quantity and value | follows a write-down by construction (the pair is rewritten by the revaluation's last inflow); intermediate states inside the transaction are never visible; pinned by test |
| `NegativeStock` settlement matching | receipt posting lines and operations | a revaluation inflow is never a settling receipt; pinned by test |
| `StockPosting`, `StockPosting.Reversal` | the costing operation of a document | a revaluation is not a stock posting; reversal rules in section 15 |
| `StocktakeService` | cost entries for stocktake valuation | book value after a revaluation is the written-down value; counted differences are costed by the engine as today |
| Replay readers on PR 48 (`CostReplay`, `CostFlowComparison`, the snapshot readers) | entries and layers in history | refuse a history that contains revaluation members (section 14), as they refuse epoch members |
| Reports of Phase 3.8E and 3.23 (not built): movements, turnover, cost of goods sold, aging, receipts at cost, last purchase price (OD-4) | entries and layers | must exclude revaluation members from flows and read dates through the lineage |
| Imports and exports that read cost data (`InventoryImportAdapters` appears in the search) | to be confirmed | to be confirmed by the implementing slice |

**[PROPOSAL] The lineage record (option (i)).** Append-only, written in the same transaction, one row per cycled layer:

* the revaluation document and its line; the position;
* the predecessor layer and the successor layer; the outflow consumption that emptied the predecessor;
* the **root layer**: the first layer of the chain WITHIN THE POSITION, that is the layer created there by the original receipt, transfer-in or conversion. It is carried forward unchanged through any number of revaluations, with the root's original date, original arrival sequence, original quantity and original amount copied in for direct reading. A root created by a transfer-in also has the existing source-layer link to the layer it came from, so the first receipt in the company is reachable by following that link (section 10.1);
* the quantity cycled; the amount before; the amount after; the difference;
* the **cost basis**: the amount this quantity would carry had no write-down ever happened in the chain (section 9.3);
* the rank of the layer within the position at the time (so that "same order" is provable by a query).

For a moving-average position, which has no layers, one row per revaluation holds the position, quantity, value before, value after, difference and the reversible pool of section 9.3; the pool's later movements are rows of an append-only pool ledger.

**[PROPOSAL] What "preserved" means, as checkable statements.** (1) The count of open layers of a position is the same after a revaluation as before. (2) Their order by arrival sequence maps one to one onto the order before. (3) The quantity of each successor equals the remaining quantity of its predecessor. (4) Every open layer of a revalued position reaches exactly one root through the lineage, and the root's row is unchanged since its creation. (5) No original layer, entry, consumption or operation row is updated except the predecessor's remaining figures going to zero through the ordinary consumption, as any issue does. (6) No predecessor layer is ever reopened: section 15.2 refuses the reversals that would do it.

## 8. Topic 3: independent approval and audit

**[APPROVED: D-198]** Every financial effect has an independent approval.

**[PROPOSAL, modelled on D-191 X3 and X4, which the owner approved for the replay only]** The approver of a revaluation document is never its creator, last editor or submitter, in every company and without a switch (SRS 4 requires a different approver "when the policy is enabled"; this contract proposes it as unconditional for revaluation, because D-198 requires independence for every financial effect). Dedicated, scoped permissions; no existing role is widened automatically.

**[PROPOSAL] Life cycle** (the pattern of stock documents and of the replay plan): draft, submitted, approved, executed; plus rejected (back to draft), cancelled before execution, and cancelled after execution only by the process of section 15.1. A document is immutable once submitted; an edit after a rejection starts a new revision and needs a new approval.

**[PROPOSAL] Separation of duties.** The approver is not the creator, not the last editor and not the submitter. The person who executes may be any holder of the execute permission, including the creator, but execution is possible only for the exact approved revision. Under RQ-3 (a) the journal posts with the execution; under RQ-3 (b) it additionally passes the journal workflow.

**[PROPOSAL] Permissions** (working names; privileged and classified with the access model of D-096; never added to an existing preset automatically): view, create, submit, approve, execute and cancel-after-execution for revaluation documents, scoped by warehouse like stock posting; configure and approve for the revaluation policy and for the account mapping, company-wide. Whether document approval belongs to the Inventory Cost Approver role of X4 or to a separate role is **[OPEN: RQ-10]**; proposed default: the same role, because both are approvals of a change to inventory cost, and no separate role is invented.

**[PROPOSAL] Binding of the approval to what is executed.** The approval is bound to a fingerprint of: the document revision and direction; the policy version and mapping version (hashes); per position the ordered list of open layers with their identifiers, remaining quantities and amounts at the preview; the exact new amounts; the revaluation date. Execution recomputes the fingerprint under the position locks and refuses on any difference (`revaluation_state_changed`): stock that moved between approval and execution invalidates the approval. This reuses the same-transaction execution binding that section 3 of the plan-execution design defines for the replay (a PROPOSAL from that design, not a new mechanism). Every executed document stores the three hashes.

**[PROPOSAL] Audit.** Business-audit events for: policy version saved and approved; mapping version saved; document created, submitted, approved, rejected, cancelled, executed, cancelled after execution; every refusal of an execution. Each carries identifiers, the fingerprint and totals, and no free text beyond the reason code. The reason text, the basis statement and the evidence references live in the document. Every step fails closed when its audit event cannot be written. The audit codes are allocated by the lead when the slice is planned.

**[OPEN: RQ-7] Evidence.** D-198 refuses execution when "original cost evidence" is unavailable. The original cost itself is always available in HASM (the root layer). What a document must carry to justify the NEW value is not defined by the sources. Option (a), proposed default: a mandatory reason code, a mandatory basis statement and at least one attachment per document (through the existing attachment module), all immutable after submission. Option (b): reason and basis statement only. (a) is the conservative choice.

## 9. Topic 4: FIFO and moving-weighted-average behaviour (exact arithmetic)

All amounts are in the base currency and are rounded only as section 3 describes. **[APPROVED: as built, 3.6]** The engine's rounding rule is not changed. Everything else in this section is **[PROPOSAL]**.

### 9.1 What the document states

Per position the document states either (A) explicit new amounts, or (B) a target unit value from which the preview computes the amounts deterministically. In both cases the APPROVED and BOUND figures are exact amounts in minor units per layer (FIFO) or per position (average); no unit value is stored as the basis of a later computation (the engine's rule).

For (B), with target unit value `u` (at most 10 decimals, as derived unit costs) and quantity `q`: target amount `T = round_half_away(q x u)` to the currency's minor units, computed exactly.

### 9.2 Write-down

A write-down document never increases any amount.

* **FIFO under RQ-5 (a), per layer** (proposed default). For an open layer with remaining quantity `q` and remaining amount `a`: `a' = min(a, T)`. A layer already at or below its target is cycled at an unchanged amount. The layer's write-down is `a - a'`; the position's is the sum.
* **FIFO under RQ-5 (b), per position.** With position quantity `Q`, value `V` and `T = round_half_away(Q x u)`: the position's write-down is `W = max(0, V - T)`. If `W = 0`, nothing is written down even if single layers are above the target. Otherwise `W` is allocated only to the layers whose amount exceeds their own target, in proportion to that excess `e_i = a_i - T_i` (with `E` the sum of the positive excesses; `W` is limited to `E`, which matters only when the rounding of the targets makes `V - T` exceed `E` by minor units): `w_i = round_half_away(W x e_i / E)` for every such layer except the NEWEST one, which takes the exact remainder `W - sum(w_i)`; `a_i' = a_i - w_i`. No layer is written below its own target, the layer amounts add up to exactly `V - W`, and the rule is deterministic. (A plain pro-rata allocation by amount is the alternative; it would write down layers that are already below the target.)
* **Moving weighted average, per position** with quantity `Q` and value `V`: `V' = min(V, T)`. The write-down is `V - V'`.

Engine operations (option (i)): one issue of the whole on-hand quantity of the position (FIFO: consuming every open layer in order, each at its exact remaining amount; average: at the whole value, the exact-remainder rule), then, FIFO: the inflows per former layer, in the former order, with quantity `q` and amount `a'`; average: one inflow with `Q` and `V'`. All in one transaction under the position lock; the position passes through zero quantity inside the transaction and is never visible in that state.

### 9.3 Reversal of an earlier write-down (only if RQ-6 permits it)

A reversal document never decreases any amount.

* **Cost basis.** A lineage row carries the cost basis `B` of the cycled quantity `q0`: at the first cycle of a chain `B` is the amount before; when a layer with remaining quantity `q` and present ceiling `C` is cycled again, the new row carries `B' = max(C, a_before)` and `q0' = q`, so the basis shrinks with the stock and never grows beyond the cost of what is left. (The `max` matters only where the engine's exact remainder is above the computed ceiling by rounding, see the rounding case below.)
* **Ceiling.** For a successor layer with remaining quantity `q <= q0`: `C = floor(B x q / q0)` to minor units, rounded DOWN so that rounding can never lift a value above cost.
* **Reversal of a layer.** `a' = max(a, min(C, T))`. A layer is SKIPPED (cycled at an unchanged amount) when its chain has no write-down, when `C <= a`, or when its target is not above its present amount. A single target that is below some layers and above others therefore raises only the layers it is above and never lowers any. The document is REFUSED (`nothing_to_revalue`) only if no layer of any of its positions would increase.
* **Reversible amount** of a layer, as the register shows it: `max(0, C - a)`.
* **The rounding case that motivated these rules (review M3).** A layer of 3 units for 100.00 is cycled unchanged (`B = 100.00`, `q0 = 3`). One unit is issued for 33.33 and 66.67 remains, while `C = floor(100.00 x 2 / 3) = 66.66`. The engine's own untouched remainder is one minor unit above the ceiling. This is not a violation: the ceiling limits what a REVERSAL may write, it is not a statement about every amount. Reversible is `max(0, 66.66 - 66.67) = 0`, the layer is skipped, and no rule lowers it.
* **Moving weighted average.** There are no layers, so the reversible amount is a position-level pool `P`: a write-down of `D` adds `D`; every later outflow of quantity `x` from on-hand `Q` reduces it to `floor(P x (Q - x) / Q)`, and the reduction is recorded as a pool-ledger row bound to that outflow; receipts and inbound transfers add nothing; a position that reaches zero quantity has `P = 0`. A reversal of `R` requires `0 < R <= P`, sets `V' = V + R` and reduces the pool by `R`; with a target, `R = min(P, max(0, T - V))`. This proportional rule is a PROPOSAL: the average method blends costs, and the sources do not say how much of an earlier write-down still "sits" in a blended position (part of RQ-6).
* **Across a transfer.** **[APPROVED: D-018]** The actual cost moves with the transfer, so a transferred layer arrives at the destination with the written-down amount as its cost. **[PROPOSAL]** In the first implementation the cost basis and the pool do NOT travel with a transfer: at the destination the transferred amount is the cost, and a reversal there is refused **[OPEN: RQ-11]**.

### 9.4 Refusals common to both methods (working names)

`revaluation_policy_missing`, `revaluation_reversal_not_permitted`, `revaluation_above_cost`, `nothing_to_revalue` (zero quantity, or nothing changes), `revaluation_state_changed`, `revaluation_date_invalid`, `position_in_pending_plan`, `tracked_item_not_supported_yet` (existing), `valuation_method_unresolved` (existing), `amount_precision_exceeded` (existing).

## 10. Topics 6 and 7: reporting of dates, and reconciliation

### 10.1 Historical receipt dates versus revaluation dates

**[APPROVED: D-192, D-198; SRS 9 "operation date distinct from the recording date"]** The two dates are reported clearly and separately; receipt dates are preserved.

**[PROPOSAL]** Every report and screen that shows a FIFO layer shows, from the lineage: the date and source document of the root layer; the original quantity and amount; each revaluation date with its document, direction and difference; the present quantity and carrying amount. Specifically:

* **Two historical dates, not one (review L4).** The root of a layer is the layer created in THIS position. If it arrived by transfer, its date is the transfer date, the "date entered this warehouse". The "first receipt in the company" is found by following the existing source-layer link from root to root across warehouses (and stops at a conversion layer, whose date is the conversion date). Reports show both, labelled; **aging** uses the first receipt in the company unless the owner prefers the warehouse date, and never a revaluation date.
* **Movement and turnover reports** show a revaluation as ONE value-only line per position (quantity change zero, value change the difference), with a marker; the internal outflow and inflows are never counted as an issue and a receipt, as a sale, as consumption or as turnover (see the reader inventory in section 7).
* **As-of valuation.** A revaluation is dated on its revaluation date and is never back-dated (section 11), so the valuation as of any earlier date is unchanged and stays reproducible. This differs from a replay epoch.
* **Cost of goods sold** after a write-down is drawn from the written-down amounts; the trace of an issued unit leads through the lineage to its root receipt (**[APPROVED: SRS 19 rule 8]**).
* **Write-down register** (new report): per item, warehouse and root layer (or average position): cumulative write-downs; cumulative reversals; cumulative cancellations (section 15.1); still reversible (`max(0, C - a)` per open layer, the pool for an average position); and **"left with issued or transferred stock", which is DEFINED as the residual** (written down, minus reversed, minus cancelled, minus still reversible), so that the columns always add up under rounding (review L5). Every figure links to its documents. The cumulative columns never decrease. This is how "never conceal a previous impairment" is made visible.

### 10.2 Reconciliation with journals and inventory valuation

**[APPROVED: as built]** `ReconcileAsync` and the close probe exist and are not weakened.

**[PROPOSAL]** After every revaluation, and as invariants in tests:

1. `ReconcileAsync` is empty (it needs no change under option (i)).
2. For each document: the sum of the lineage differences per position = the net value change of its cost operations = the amount of its journal lines on the inventory account for that position; and the sum over the document = the amount on the counteraccounts. Debits equal credits. Aggregated journal lines (several positions, one account and branch) equal the sum of the per-position figures of the document.
3. Per period: the sum of all revaluation differences in the costing ledger = the movement of the inventory accounts that originates from revaluation journals = the opposite movement on the revaluation counteraccounts.
4. **Under RQ-3 (a): the costing ledger equals the inventory accounts at every commit** as far as revaluations are concerned; there is no pending state. **Under RQ-3 (b):** the difference is bounded to documents whose draft is pending or failed, each is listed, the close is blocked, and the only repairs are the ones of RQ-3 (b).
5. The write-down register reconciles to the lineage: every column equals the figure recomputed from the lineage rows, the pool ledger and later consumptions.
6. A journal entry of a revaluation that is reversed or cancelled OUTSIDE the revaluation's own process puts the document into the failed accounting state and blocks the close; the repair is the re-issue of RQ-3 (b) or the cancellation of section 15.1, never a manual inventory entry.

## 11. Period rules

**[APPROVED: SRS 5 and SRS 19 rule 5]** Nothing is posted into a Final Closed period. **[APPROVED: D-220]** Stock and cost postings into Soft Closed, Final Closed or missing periods are to be refused by a database guard, to which a revaluation's cost operations are subject like any other once it exists.

**[PROPOSAL, following D-191 X2 (approved for the replay only) and section 11 item 8 of the plan-execution design]** The revaluation date must lie in an **Open** period; Soft Closed is refused (the stock side has no soft-close override); Final Closed and a missing period are refused. The date is not earlier than the last effective date of any position in the document (the engine refuses back-dating, and a revaluation is never a replay); it may equal that date; and it is not later than the current date. The period is re-checked at execution and when the journal posts.

**[OPEN: RQ-14] Period-end cut-off (new in revision 2; review M4).** The two rules above have a consequence that must be decided, not discovered. A write-down that is determined during the closing work of a period, after movements of the NEXT period have already been posted on the position, cannot be dated at the period end: the date cannot precede the position's last effective date, and a replay across a revaluation is refused (section 14). The write-down then lands in the next period, which is the wrong period for the expense. Options:

* (a) **Proposed default:** accept the rule and make it an operating requirement: revaluations for a period end are executed BEFORE any later movement is posted on the affected positions (for example by posting the write-down on the period-end date before the first movement of the new period); the preview states plainly when the wanted date is no longer possible; the close checklist names the step.
* (b) HASM blocks later-dated movements on a position while a submitted revaluation for an earlier date is waiting. This protects the cut-off for known documents only and holds up operations.
* (c) A later, separately designed and reviewed back-dated path (a replay that can cross a revaluation). It is the complete answer and the largest work; it is not part of the first implementation (RQ-13).

## 12. Multi-currency

**[APPROVED: SRS 5; as built 3.6 and 3.7A]** The SRS has multiple currencies with dated rates, and the ledger and the costing engine work in the company's base currency; inventory accounts limited to a foreign currency are refused.

**[PROPOSAL]** A revaluation is stated, approved, costed and journalised in the base currency only. Inventory is a non-monetary item carried in base currency, so no exchange-rate revaluation of stock exists in this contract; exchange differences on monetary items belong to Phase 3.18. A counteraccount limited to a foreign currency is refused.

## 13. Zero and negative-quantity positions, obligations, tracked items, Trial

* **Zero quantity.** **[PROPOSAL]** A position with no stock on hand has nothing to revalue and is refused (`nothing_to_revalue`). A FIFO layer with no remaining quantity is not cycled and keeps its history. A reversal can never be applied to stock that has left.
* **Zero value.** A layer or position with quantity and zero value (free goods) can be neither written down further nor, having cost zero, written up.
* **Negative quantity and open obligations.** **[APPROVED: SRS 9, D-019]** Negative stock is forbidden by default and always for FIFO, lot and serial items; the engine never holds a negative position. The limited moving-average exception is implemented as obligations with a provisional cost (3.8B-3). As built, an open obligation exists only while the position holds zero quantity, so such a position is already refused by `nothing_to_revalue`; revision 1's separate refusal and question were moot (review L1) and are withdrawn.
* **[OPEN: RQ-12] Provisional cost after a write-down (the real question behind it).** When stock later goes negative under the approved exception, the provisional cost is taken from (1) the position's last average, (2) the latest stock-document receipt, or (3) a manual figure. As read in the code, source 1 follows a write-down by construction (the last positive state of the position is the written-down one). Source 2 returns the cost of the original receipt, which a write-down does not touch. Option (a), proposed default: a receipt earlier than the latest revaluation of that position is not eligible as source 2, so the provisional cost never ignores a write-down (source 3 or `provisional_cost_unavailable` then applies). Option (b): keep source 2 as built; the provisional cost may then exceed the written-down level until the settlement corrects it.
* **Lot, serial and expiry items.** **[APPROVED: as built]** The engine refuses them today (`tracked_item_not_supported_yet`); their revaluation is designed with Phase 3.8C (OD-11, D-219), not here.
* **[OPEN: RQ-16] Trial counting.** **[APPROVED: D-215]** Each unique, user-originated posted business transaction counts once; internal and system-generated entries do not count separately; "A separate, user-originated posted reversal or adjustment counts if it is itself an independent business transaction". Applied to this contract as a PROPOSAL: an executed revaluation document is a user-originated posted transaction and counts ONCE; its cost operations and its journal entry never count separately; a cancellation after execution (section 15.1) is itself a user-originated posted reversal and counts once. Whether revaluation is available in the Trial at all (SRS 15 limits the Trial to basic modules) belongs to the Phase 4.2 plan. The alternative, not counting revaluations, would need the owner's word.

## 14. Interaction with replay epochs (X5, X6) and with pending replay plans

**[APPROVED: D-191]** X5: a second replay plan on a position already affected by an executed epoch is refused in the initial implementation. X6: unsupported reversals affecting an executed epoch are refused. X7: a document bound to a pending replay plan cannot be posted through the normal path.

**[PROPOSAL]** The same caution in both directions, each a refusal in the first implementation:

1. **Revaluation while a replay plan is pending.** A position that belongs to the closure of a replay plan that is submitted or approved and not yet executed, cancelled or expired is refused for revaluation (`position_in_pending_plan`). The plan's fingerprint binds the position's history; a revaluation would invalidate it.
2. **Replay over a revaluation.** A replay plan whose closure would have to storno or rebook the cost operations of a revaluation (a back-dated event earlier than the revaluation on that position) is refused (working name `plan_on_revalued_position`), as X5 refuses a plan on an epoch. Replaying across a revaluation would need rules for recomputing every write-down, which this contract does not define. Together with section 11 this freezes back-dated corrections before a revaluation date (RQ-14).
3. **Revaluation after an executed epoch.** A revaluation is a forward-dated operation on the present state, not a second replay; it is permitted on a position that has epoch members. Its lineage roots are then the epoch's rebooked layers, whose own epoch lineage leads to the original receipts; reports follow both.
4. **Method conversion (D-045).** Conversion preserves the carrying value it finds, which after a write-down is the written-down value. What happens to the cost basis, the pool and the register across a conversion (FIFO layers becoming one average position, or an average position becoming one conversion layer) is defined by the conversion contract; until it is, a conversion of a position with a reversible amount carries the register forward as history and ends reversibility (basis and pool do not cross the conversion). A test pins that the register still reconciles.

**[OPEN: RQ-13]** Whether items 1 and 2 stay refusals for Release 1 or must later be supported.

## 15. Cancellation, reversals and returns after a revaluation

### 15.1 Correcting a mistaken revaluation

**[APPROVED: SRS 4, SRS 5, SRS 19 rule 3]** A posted document is never edited; it is corrected by a separate reversal or adjustment document.

**[OPEN: RQ-8]** Revision 1 proposed "only by another approved revaluation; no reversal at all" and called it conservative. The review (M5) is right that, together with write-down only (RQ-6 (a)), this leaves a keying error uncorrectable (a target of 0.90 instead of 9.00 would misstate inventory permanently and shift profit between periods), and that this is in tension with the SRS rule just cited. Restoring the documented cost of a mistaken document, with both documents kept, is error correction and not an "unsupported upward valuation" in the sense of D-198.

* (a) Revision 1: no cancellation after execution; correction only by a further revaluation. Not proposed any more.
* (b) **Proposed default (the reviewer's recommendation): a narrow cancellation of the WHOLE executed document**, itself a separate document with a reason, permitted only when ALL hold: nothing has moved on any of its positions since the revaluation (the engine's own "untouched" rule for every inflow of the document); the revaluation date is still in an Open period; an independent approval by someone who is not the creator, last editor or submitter of the original document or of the cancellation; the cancellation is audited. Effect, in one transaction: the document's inflows are reversed and then its outflow is reversed through the engine's ordinary reversal, which restores exactly the predecessor layers with their identity, order, dates and amounts (FIFO) or the former quantity and value (average); the lineage rows stay and are marked cancelled by an appended row; the journal entry is reversed by a reversing entry (or, under RQ-3 (b), the pending draft is cancelled); the register shows the write-down AND its cancellation. Outside these conditions a cancellation is refused (`revaluation_cancellation_not_possible`), and the correction is a further revaluation under the policy.
* (c) A wider correction path (after later movements): not proposed; it would need the recomputation rules of a replay.

The engine's generic reversal is refused for every cost operation of a revaluation outside this process (`revaluation_not_reversible`), by the service and by an additive database rule in the style of X6.

### 15.2 Reversals and returns of stock after a revaluation (new in revision 2; review M7)

**[OPEN: RQ-15]** Each rule below is a PROPOSAL with its alternative; together they are one owner question.

1. **Reversal of an issue or transfer-out made BEFORE the revaluation (FIFO).** The engine's reversal would give the quantity back to the consumed PREDECESSOR layer at its pre-write-down cost: a closed predecessor would reopen ahead of its successors, with no lineage row, at a value the write-down was meant to remove. **Proposed: refused** (`operation_precedes_revaluation`), by the service and an additive database rule. The goods come back through a new receipt or return document, valued under rule 4. Alternative: allow the reversal and write the restored quantity down in the same transaction; this needs its own arithmetic and lineage and is not proposed for the first implementation.
2. **The same for the average method.** A reversal of a pre-revaluation outflow would add the quantity back at its old, higher value into a written-down position. **Proposed: refused** with the same code. (A reversal of a pre-revaluation INFLOW is already impossible in the engine: later movements exist.)
3. **Reversal of an issue or transfer made AFTER the revaluation.** Permitted as today. FIFO: the successor layer gets its quantity and amount back, and the ceiling formula follows the restored quantity by itself. Average: the pool-ledger row of that outflow is reversed by an appended row, so the pool is restored exactly to what the outflow removed.
4. **Customer return of units sold after a write-down.** **[APPROVED: D-203]** A return requires its original document and prefers the original inventory-cost provenance; where exact cost reconstruction cannot be proven, the posting is refused. **[PROPOSAL]** The provenance of such units is their consumption trace, so they return at the written-down cost at which they left, as a new layer (FIFO) or added value (average). The returned stock carries NO reversible amount: its cost is what it left at. Alternative: restore a share of the basis with the return; not proposed.
5. **Return to supplier of written-down stock.** The receipt cannot be reversed (its layer was cycled), so the return is a new outflow document at the carrying, written-down cost. The difference between that cost and the supplier's credit has NO account in this contract. **[PROPOSAL]** It is not invented here: it belongs to the purchase-return accounting of Phase 3.13 S4 and 3.15 (the versioned mappings of D-202 and D-218); until that mapping exists a purchase return whose credit differs from the carrying cost stays accounting-pending with a reason, as D-202 prescribes for missing configuration.
6. **Reversal of a receipt or transfer-in made before the revaluation.** Already refused by the engine (`reversal_not_possible_goods_consumed`), also for layers whose value did not change (section 7). The user is told that the position was revalued; the correction is a new document.

## 16. Worked examples (base currency with two decimals; RQ-5 (a), per layer, unless stated)

### 16.1 FIFO write-down

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

Cost operations: issue 19 for 199.00 (consuming L1 60.00, L2 75.00, L3 64.00); inflows L1' 6 for 54.00, L2' 5 for 45.00, L3' 8 for 64.00, in that order. Position after: quantity 19, value 163.00. Lineage: L1 to L1' (root L1, 2026-01-10, basis 60.00 for 6), L2 to L2' (root L2, 2026-02-05, basis 75.00 for 5), L3 to L3' (root L3, 2026-03-01, basis 64.00 for 8, no write-down).

Journal entry, dated 2026-03-31:

| Account | Debit | Credit |
|---|---|---|
| Write-down account | 36.00 | |
| Inventory account of the item | | 36.00 |

A later issue of 8 units costs L1' whole (54.00) plus 2 of the 5 units of L2' (45.00 x 2 / 5 = 18.00): 72.00. Without the write-down it would have cost 60.00 + 30.00 = 90.00.

**The same case under RQ-5 (b), per position:** `T = 19 x 9.00 = 171.00`, `W = 199.00 - 171.00 = 28.00`. Excess over the own target: L1 6.00, L2 30.00, L3 none; `E = 36.00`. `w1 = 28.00 x 6.00 / 36.00 = 4.67` (rounded), L2 as the newest layer with an excess takes the remainder 23.33. New amounts: L1' 55.33, L2' 51.67, L3' 64.00; position 171.00. Journal: 28.00 instead of 36.00.

### 16.2 FIFO reversal (only if RQ-6 permits it)

After the issue of 8: L2' has 3 units for 27.00; L3' has 8 units for 64.00. Reversal document dated 2026-05-31 with target unit value 12.00.

| Layer | Ceiling `C = floor(B x q / q0)` | Target `T` | `max(a, min(C, T))` | Reversal |
|---|---|---|---|---|
| L2' | 75.00 x 3 / 5 = 45.00 | 3 x 12.00 = 36.00 | 36.00 | 9.00 |
| L3' | no write-down in its chain: skipped | 8 x 12.00 = 96.00 | 64.00 (unchanged; never above cost) | 0.00 |

Journal entry: debit the inventory account 9.00, credit the write-down reversal account 9.00. The register for root L2: written down 30.00; reversed 9.00; still reversible `max(0, 45.00 - 36.00) = 9.00`; left with issued stock, the residual, 30.00 - 9.00 - 9.00 = 12.00. For root L1: written down 6.00, still reversible 0, left with issued stock 6.00.

Rounding cases. (1) A root of 3 units for 100.00, of which 2 units for 66.67 remain when it is first written down, has basis 66.67 for 2; after one more unit is issued the ceiling of the last unit is `floor(66.67 x 1 / 2) = 33.33`, never 33.34. (2) The case of section 9.3: a never-written-down layer whose untouched remainder (66.67) is above its computed ceiling (66.66) is skipped; nothing lowers it.

### 16.3 Moving weighted average write-down and reversal

Position: quantity 20, value 400.00 (derived unit 20.00). Write-down with target unit value 17.50: `T = 350.00`, new value 350.00, write-down 50.00. Cost operations: issue 20 for 400.00, inflow 20 for 350.00. Journal: debit the write-down account 50.00, credit the inventory account 50.00. Pool `P = 50.00`.

Then a receipt of 10 for 200.00: quantity 30, value 550.00, pool unchanged. Then an issue of 12: cost 550.00 x 12 / 30 = 220.00; quantity 18, value 330.00; pool `floor(50.00 x 18 / 30) = 30.00` (pool-ledger row: minus 20.00, bound to that issue).

A reversal with target unit value 25.00: `T = 450.00`, `R = min(30.00, 450.00 - 330.00) = 30.00`; new value 360.00. Journal: debit the inventory account 30.00, credit the write-down reversal account 30.00. Pool 0.00. (360.00 is what 18 units would carry had there been no write-down: 600.00 x 18 / 30.)

If instead the issue of 12 is reversed (section 15.2 rule 3) before any reversal document: quantity 30, value 550.00, and the appended pool row restores the pool to 50.00.

## 17. Owner questions (renumbered in revision 2; each with the proposed default; none is decided here)

| # | Question | Proposed default | Main alternative and its consequence |
|---|---|---|---|
| RQ-1 | Which accounts take revaluation differences, and may HASM constrain their type? | A versioned mapping with a write-down account and a write-down reversal account, company default with item-group overrides; type not constrained; refusal when missing | A fixed account type would be a chart rule the SRS does not contain |
| RQ-2 | Where is a write-down credited? | (a) the inventory account, so the costing ledger and the general ledger stay equal | (b) an allowance account outside the inventory set changes the close probe and every valuation report, or leaves the layers unrevalued; (c) a write-down sub-account inside the inventory account set shows gross cost in the ledger and keeps the probe, by extending the 3.7A configuration |
| RQ-3 | When is the revaluation journal posted? | (a) in the same transaction as the cost effect, on the document's own independent approval; the two ledgers can never diverge | (b) a pending draft through the journal workflow: a second approval, but interim cost of goods sold at written-down cost before the expense is in the ledger, a blocked close, and a rejected draft repairable only by the defined re-issue or by cancellation |
| RQ-4 | On which measurement basis is a lower value determined? | The policy records the basis the company declares; each document carries approved figures and evidence; HASM computes no market value | A computed value from price lists would be an invented valuation rule |
| RQ-5 | At which unit is the lower value tested? | (a) per FIFO layer (per position for the average method): the lowest carrying value, no allocation | (b) per position (the costing unit of D-018), with the excess-proportional allocation of section 9.2: a smaller write-down (28.00 against 36.00 in the example); (c) per item across warehouses or (d) per group: not proposed, they net across costing units |
| RQ-6 | Is a reversal of an earlier write-down permitted, and to what limit? | First implementation: write-down only, acceptable only together with RQ-8 (b). If enabled later: limited to the write-down still attributable to the stock on hand, never above original cost (per layer for FIFO; a proportional pool for the average method) | Without reversals a company whose framework requires them cannot follow it in HASM |
| RQ-7 | What evidence must a revaluation document carry? | Reason code, basis statement and at least one attachment, immutable after submission | Reason and statement only |
| RQ-8 | How is a mistaken revaluation corrected? | (b) a narrow, independently approved cancellation of the whole document while nothing has moved on its positions and the period is Open; otherwise a further revaluation | (a) no cancellation at all leaves a keying error uncorrectable under write-down only, in tension with SRS 4, 5 and 19 rule 3 |
| RQ-9 | Technical representation: per-layer outflow and inflow with lineage, or an additive adjustment table read by the engine? | The outflow and inflow pair with lineage, cycling every open layer in order: balances stay right by construction, no existing safeguard changes; every flow and kind-keyed reader must be adapted and tested, and earlier receipts of the position become irreversible | The adjustment table keeps layer identity, dates and flows, but every reader of balances and cost must change, and a missed one misstates inventory and cost of goods sold |
| RQ-10 | Who approves a revaluation document? | The Inventory Cost Approver role of X4, never the creator, last editor or submitter; no existing role widened | A separate revaluation approver role |
| RQ-11 | Does the right to reverse a write-down travel with a transfer to another warehouse? | No: at the destination the transferred amount is the cost; a reversal there is refused | Carrying the basis through the source-layer link, a larger design |
| RQ-12 | After a write-down, may the latest stock-document receipt still serve as the provisional cost of a negative-stock exception? | No: a receipt earlier than the latest revaluation of the position is not eligible | Keep it as built; the provisional cost may exceed the written-down level until settlement |
| RQ-13 | Do the refusals between revaluations and replay plans stay for Release 1? | Yes: no revaluation on a position in a pending plan, and no replay across a revaluation | Supporting either needs recomputation rules this contract does not define; read together with RQ-14 |
| RQ-14 | Period-end cut-off: a write-down found during the close cannot be dated before later movements already posted | (a) accept, as an operating requirement: period-end revaluations are executed before later movements are posted; the preview says when the wanted date is no longer possible | (b) block later movements while a submitted revaluation waits; (c) a later back-dated path across revaluations |
| RQ-15 | Reversals and returns of stock after a revaluation (section 15.2) | Reversal of a pre-revaluation issue refused (both methods); post-revaluation reversals permitted with basis and pool restored; customer returns come back at the written-down cost they left at, with no reversible amount; a supplier-return difference is not given an account here and stays accounting-pending until the purchase-return mapping exists | Allowing pre-revaluation reversals with an automatic write-down of the restored quantity; restoring basis with returns |
| RQ-16 | Does an executed revaluation document count as a Trial transaction? | Yes, once (D-215 applied); its cost operations and journal never separately; a cancellation after execution counts once; availability in the Trial is decided in Phase 4.2 | Not counting revaluations needs the owner's word |

## 18. The reviewer's recommendation per question (Agent FAC, review of revision 1; question numbers of this revision)

| # | Reviewer's recommendation | This revision |
|---|---|---|
| RQ-1 | Accept the default | unchanged |
| RQ-2 | Accept the direct credit; consider a sub-account inside the inventory account set if gross cost must show in the ledger | default unchanged; option (c) added |
| RQ-3 | Do not accept the pending draft as written; prefer posting in the same transaction, or define a repair path first | default changed to same-transaction posting; the pending draft is kept as option (b) with its consequences and repair path |
| RQ-4 | Accept, but decide the unit of comparison first | unchanged; RQ-5 added |
| RQ-5 | New question required (no default recommended) | added; per layer proposed, per position shown with an allocation rule and figures |
| RQ-6 | Accept write-down only, but only together with the cancellation of RQ-8 (b) | default unchanged, with that condition stated |
| RQ-7 | Accept | unchanged |
| RQ-8 | Choose the narrow, independently approved cancellation | default changed to it |
| RQ-9 | The outflow and inflow pair is acceptable as the default, conditional on the corrected claim, the reader inventory and a database-safety review | default unchanged; the claim corrected, the reader inventory and the irreversibility consequence added |
| RQ-10 | Accept | unchanged |
| RQ-11 | Accept | unchanged |
| RQ-12 | The old question was moot as built; ask about the provisional-cost sources instead | restated |
| RQ-13 | Accept the refusals for Release 1, read together with the cut-off | unchanged; cross-reference added |
| RQ-14 | New question required | added |
| RQ-15 | New question required | added |
| RQ-16 | Apply D-215 or ask | added |

## 19. Invariants an implementation must keep (each pinned by at least one test)

* I1. No original layer, entry, consumption, operation or journal row is updated or deleted by a revaluation, beyond the remaining figures of a consumed layer; no predecessor layer is ever reopened except by the cancellation of section 15.1.
* I2. Quantities never change: per position, quantity after = quantity before; per layer, successor quantity = predecessor remaining quantity.
* I3. The number and the order of the open layers of a position are the same before and after, and every open layer has exactly one root with an unchanged original date, quantity and amount.
* I4. Direction and ceiling, as rules on what a revaluation WRITES: a write-down document never increases an amount; a reversal document never decreases one and never writes an amount above the ceiling `C`; without an enabled reversal policy no reversal document executes. (It is not claimed that every engine remainder is at or below `C`; see section 9.3.)
* I5. Every changed position has exactly one journal effect, balanced, equal to its cost effect, on accounts resolved from the bound mapping version.
* I6. No execution, and no cancellation after execution, without an approved, unexpired, exactly matching approval by a person who is not the creator, last editor or submitter; one execution per approval.
* I7. No execution outside an Open period, before a position's last effective date, or in the future.
* I8. `ReconcileAsync` is empty. Under RQ-3 (a) the costing ledger equals the inventory accounts at every commit that contains a revaluation; under RQ-3 (b) every difference belongs to a listed pending or failed document, blocks the close, and is repairable only by the defined re-issue or the cancellation.
* I9. Idempotent: a repeated or concurrent execution of the same document changes nothing and returns the first result.
* I10. No existing constraint, trigger, function or guard is dropped, replaced, disabled or widened (before and after catalog comparison).
* I11. Refusals are complete rollbacks: no layer, entry, lineage row, journal or audit event of a refused execution remains, except the audited refusal itself.
* I12. Nothing is computed from a stored unit value; all bound figures are exact amounts in minor units.
* I13. No cost operation of a revaluation is ever reversed outside the revaluation's own cancellation process, by the service or by raw SQL as the runtime role.
* I14. The write-down register is append-only and monotone: cumulative write-downs, reversals and cancellations never decrease, and its columns always add up (the residual definition).
* I15. Every executed document stores the hashes of its policy version, its mapping version and its fingerprint, and they verify.
* I16. No reader keyed on the entry kind or totalling flows treats a revaluation member as a business receipt or issue (one test per reader of the inventory in section 7).

## 20. Test matrix an implementation must pass before activation

Every test is a PostgreSQL test on the isolated cluster unless marked unit. The numbering is local to this document.

| # | Test | Proves |
|---|---|---|
| RT1 | FIFO write-down of three layers, one below target (example 16.1) | amounts, journal, layer count and order, lineage roots |
| RT2 | FIFO issue after a write-down draws the written-down amounts; the trace of the issued units reaches the root receipts | cost of goods sold and traceability |
| RT3 | FIFO partial layer and thirds rounding; ceiling rounded down (rounding case 1 of 16.2) | I4, I12 |
| RT4 | Average write-down (example 16.3) and the exact-remainder rule through zero quantity inside the transaction | I2, I8 |
| RT5 | Reversal refused when the policy does not permit it; never above the ceiling; a layer with no write-down skipped | I4 |
| RT6 | Reversal permitted by policy: FIFO per layer and average pool, after partial issues, receipts and a transfer out | section 9.3 |
| RT7 | Transfer of written-down stock; reversal at the destination refused | RQ-11 default |
| RT8 | Cycling only some layers is impossible: every open layer is cycled and the order mapping is one to one | I3 |
| RT9 | Approver equal to creator, last editor or submitter refused; revoked approver refused at decision; approval bound to the revision | I6 |
| RT10 | Stock moves between approval and execution: `revaluation_state_changed`, nothing written | binding |
| RT11 | Twelve concurrent executions of one document: exactly one effect; replay returns the first result | I9 |
| RT12 | Concurrent issue and revaluation on one position serialise under the position lock; both orders give reconciled results | concurrency |
| RT13 | Soft Closed, Final Closed, missing period, date before the last effective date, future date: each refused; a date EQUAL to the last effective date accepted | I7 |
| RT14 | Mapping missing, account inactive, not postable, an inventory account, foreign-currency limited: each refused with nothing left behind | I5, I11 |
| RT15 | Policy missing or not approved: refused; policy, mapping and fingerprint hashes stored and verified | I15 |
| RT16 | RQ-3 (a): a journal refusal refuses the whole execution; cost ledger = inventory accounts at the commit. RQ-3 (b), if chosen: journal pending blocks the close; draft rejected or cancelled gives the failed state; the deterministic re-issue repairs it; a manual inventory entry does not | I8 |
| RT17 | Per-document and per-period reconciliation identities of section 10.2, including after a 150-step random scenario of receipts, issues, transfers, reversals, revaluations and negative-stock obligations with settlements | reconciliation |
| RT18 | Before and after catalog comparison of every existing guard function, trigger and constraint | I10 |
| RT19 | Raw SQL as the runtime role cannot insert a lineage row for a foreign document, cannot write an amount above the ceiling, cannot update or delete lineage or pool rows, cannot reverse a revaluation operation | I13 and the database rules of the slice |
| RT20 | Zero-quantity position refused (also with an open obligation); zero-value layer neither written down nor up | section 13 |
| RT21 | Position in the closure of a pending replay plan refused; a replay plan across a revaluation refused; revaluation after an executed epoch permitted with both lineages readable | section 14 |
| RT22 | Generic reversal of a revaluation operation refused; reversal of an original receipt whose layer was cycled refused with a clear code, also for a layer whose value did not change | sections 7 and 15 |
| RT23 | Aging by the first receipt in the company across a transfer, with the warehouse date shown separately; as-of valuation before the revaluation date unchanged | section 10.1 |
| RT24 | Write-down register equals the figures recomputed from the lineage and the pool ledger after issues, reversals and a cancellation; columns add up; cumulative columns never decrease | I14 |
| RT25 | Audit event missing or failing makes the step fail closed | section 8 |
| RT26 | Permissions: each action refused without its permission and outside the warehouse scope; no existing preset gained a permission | section 8 |
| RT27 | Unit: the arithmetic of sections 9.1 to 9.3 on boundary values (huge quantities, three-decimal currency, zero-decimal currency) | I12 |
| RT28 | Shared screen tests (Web and Tauri identical): preview, the dates shown separately, refusal messages | shared frontend rule |
| RT29 | An issue posted while a revaluation journal is pending (RQ-3 (b) only): the interim difference is listed and the close blocked | I8 |
| RT30 | Reversal of a pre-revaluation issue refused, FIFO and average (`operation_precedes_revaluation`); raw SQL cannot reopen a predecessor layer | section 15.2, I1 |
| RT31 | Reversal of a post-revaluation issue: FIFO basis follows; average pool restored exactly (example 16.3, last paragraph) | section 15.2 |
| RT32 | Two successive revaluations of one layer: the basis carries, the root is unchanged, the register adds up | section 9.3 |
| RT33 | The rounding case of section 9.3 (untouched remainder above the computed ceiling): layer skipped, nothing lowered | I4 |
| RT34 | One reversal target that is above some layers and below others: only the former rise, none falls | I4 |
| RT35 | Per-position unit of comparison (if RQ-5 (b) is chosen): the allocation of example 16.1, remainder on the newest layer with an excess, sums exact | section 9.2 |
| RT36 | Latest-receipt provisional cost after a revaluation: never the revaluation inflow; eligibility as RQ-12 decides. Last-average provisional cost equals the written-down state | reader inventory |
| RT37 | Stock card and movement readers show one value-only line; flow totals exclude revaluation members; one test per reader of the inventory in section 7 | I16 |
| RT38 | Method conversion after a write-down: carrying value preserved, reversibility ended, register reconciles | section 14 item 4 |
| RT39 | Journal lines aggregated across positions and branches equal the per-position figures | section 10.2 item 2 |
| RT40 | Cancellation after execution: permitted only while nothing has moved, in an Open period, by an independent approver; restores the predecessor layers exactly; journal reversed; register shows both; refused otherwise | section 15.1 |
| RT41 | Customer return of units sold after a write-down comes back at the cost it left at, with no reversible amount; supplier return with a differing credit stays accounting-pending | section 15.2 rules 4 and 5 |
| RT42 | Trial counting: one count per executed revaluation document and per cancellation; none for their internal operations (if RQ-16 is confirmed) | D-215 |

## 21. What happens next, and what stays closed

1. This revision goes back to the **independent accounting reviewer** for confirmation, and section 7 to a **database-safety review**.
2. The owner answers RQ-1 to RQ-16 and approves the contract explicitly, or changes it.
3. Only then is an implementation slice planned: an additive company migration (lineage, pool ledger, documents, policy and mapping versions; no existing safeguard changed), services, the adapted readers, the shared screen, and the tests of section 20.
4. **Revaluation execution stays CLOSED until the approved contract and the required tests have passed their review.** Recording this proposal activates nothing.
