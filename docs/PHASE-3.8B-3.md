# HASM — Phase 3.8B-3: Negative-stock exception (as built)

Third slice of Phase 3.8B (plan: `docs/PHASE-3.8B.md`; previous slices `docs/PHASE-3.8B-1.md`, `docs/PHASE-3.8B-2.md`). Normative design: `docs/PHASE-3.8B-3-DESIGN.md` — Revision 4, then Revision 3, then Revision 2, then D1–D7
(sections 3–9 of the original text are superseded where they differ). Sources: SRS §9 (negative stock forbidden by default; a limited, authorized exception for eligible moving-average items with a provisional cost and a later
settlement; never FIFO, lot or serial; material pending differences block the period close; retroactive entries never edit posted history), PHASE-3.6, PHASE-3.7A–D, PHASE-3.8B-2.

**The cost position never goes negative.** No existing constraint was changed or relaxed: the 0014 position, entry and layer CHECKs (never a negative quantity or value, no value without quantity, no negative layer) are untouched. An
issue above the stock is split into the COVERED part (the unchanged engine, real moving average) and the UNCOVERED part, a provisional OBLIGATION kept beside the engine.

## What exists
* **Company migration 0020** (`CompanySchema.NegativeStock.cs`; added columns in `CompanySchema.Costing.cs` and `CompanySchema.InventoryAccounting.cs`), purely additive:
  * `NegativeStockSetting` — versioned, append-only company enablement (off until a version enables it) and the optional second-approver rule;
  * `NegativeStockItemFlag` — append-only eligibility history per item (the latest row is current). PostgreSQL refuses the flag for anything but active goods without lot/serial/expiry tracking whose valuation resolves to moving
    average and that hold no FIFO position, and refuses removing it while an obligation of the item is open;
  * `NegativeExceptionApproval` — one append-only row per approver and snapshot (approver, time, reason, snapshot JSON and its SHA-256), only for an APPROVED, not executed ISSUE document, never by its creator;
  * `NegativeObligation` — immutable: item, warehouse, position, issue posting and line, issue operation, issue journal, accounting version, O, V, K, date, original quantity (base unit, > 0), provisional TOTAL (> 0; no unit
    cost is stored), source 1/2/3 with its (quantity, value) pair or receipt entry, justification (source 3 only, mandatory), approval;
  * `NegativeSettlement` — append-only matches (obligation, receipt posting line, receipt operation, line quantity and amount, matched quantity, provisional and actual amounts, exact difference, date, journal, sequence); a
    reversal is its own row negating exactly one match (`reverses_settlement_id`, unique), with the stock reversal it belongs to;
  * `NegativeObligationReversal` — append-only "the originating issue was reversed" (only with no net settlement);
  * cost position: `last_average_quantity` / `last_average_value` (the figures after the latest entry that left the position holding stock; retained when it falls to zero; written by entries only; **no backfill**);
  * inventory accounting version: `obligation_account` (O) and `variance_account` (V), nullable (versions saved before have none).
* **System migration 0022**: the warehouse-scoped permission `company.stockdocs.negativeexception` (Company Administrator preset).
* **Engine** (`InventoryCostingService`): writes the last average pair; refuses any inflow into a position with an open obligation (`negative_obligation_open`) unless the caller names the position as obligation-aware (a settling
  receipt, the reversal of the issue that created the obligation); accepts an operation without lines only when explicitly provisional; `LockPositionsInWorkAsync` creates/locks positions for the exception; reconciliation
  check `last_average_equals_last_stocked_entry`. `CostingMath.Scale` prices an uncovered quantity from a pair (one rounding, the part may exceed the whole).
* **Posting of an issue** (`StockPosting.Negative.cs`): the plan (`NegativeStockPlanner.PlanAsync`) is computed under the posting locks from the live data; lines of one position are covered in document order from the quantity on
  hand before the document. With no uncovered quantity nothing changes. Otherwise, in order: disabled company or ineligible item → `insufficient_stock` (the default, R18); no approval → `negative_exception_approval_required`;
  no approved snapshot equal to the live plan → `negative_exception_stale`; not enough distinct approvers other than the poster and the creator → `negative_exception_self_approval` / `negative_exception_approval_required`;
  any line problem → its code. Then one transaction: the engine issues the covered quantities (an issue operation WITHOUT entries when nothing is covered), one balanced draft journal (5.1), the posting record, one obligation per
  uncovered line, audit.
* **Posting of a stock-document receipt**: the positions are locked, then the open obligations of these positions in (date, sequence) order. Refused when an open obligation is dated after the receipt
  (`negative_obligation_older`, R13), when its issue journal was cancelled or reversed (`issue_accounting_failed`), or when the receipt is dated before the last movement (`backdated_not_supported`). Matching is oldest first,
  per line in document order; the rest of each line enters the position through the engine (a receipt operation WITHOUT entries when everything matched). Each match gets its own balanced draft journal (5.2/5.3) with the key
  `stock_negative_settlement:<obligation>:<posting>:<line>`; the receipt's own journal books only what entered the position. The posting keeps the entered amount of every line.
* **Reversal** (`StockPostingService.ReverseAsync`): an issue with obligations is reversible only while none has a net settlement (`negative_obligation_settled`); the covered part comes back and each obligation gets its
  reversal row. A settling receipt is reversible only newest first (`negative_settlement_newer_exists`) and never re-opens an obligation of a Final Closed period (`negative_obligation_period_closed`); each settlement gets its
  negating row, its journal follows 3.7C (cancelled while unposted, the reversal staying in the posting's period; a draft reversal entry once posted), and a re-opened obligation needs an empty position
  (`reversal_not_possible_later_movements`).
* **Other inflows (R3, D4, R15)**: transfer-in, the reversal of a covered issue, an adjustment surplus and the reversal of an adjustment shortage are refused while an obligation of the position is open
  (`negative_obligation_open`; the adjustment refusal names the line's item with a field error and writes nothing). A transfer never uses the exception (`negative_exception_transfer_not_allowed`; posting stays
  `insufficient_stock`). An adjustment shortage never uses it.
* **Close** (`InventoryValuationPeriodCloseProbe`): once obligations exist, both closes are blocked by an obligation-account difference at the period end, a variance difference or a failed settlement journal; the Final Close
  (transition from Soft Closed) is also blocked by any obligation open now with a date up to the period end. The soft-close warning (D7) is the close report: `GET /api/v1/company/negative-stock/close-report?periodEnd=`
  lists the open obligations (item, warehouse, document, open quantity and provisional value, age, how it is resolved) and the point-in-time figures (expected O balance, posted O balance, settlement differences, posted V lines,
  failed settlement journals).
* **Guarded changes (R12)** — PostgreSQL refuses with `negative_obligation_open` (services map it to the same code) while an obligation is open: removing the eligibility flag (`NegativeStockItemFlag` insert trigger);
  changing the item's group, lot/serial/expiry tracking, type, or deactivating it (`t_2f2a7184`, trigger `NegativeStockGuard.ItemGuardTrigger`); changing the valuation or the parent of any group in the item's chain
  (`t_2640a66e`, `GroupGuardTrigger`); changing the company default valuation when an open obligation's item would no longer resolve to moving average (`t_1cf46c0f`, `SettingsGuardTrigger`); deactivating the warehouse
  (`t_d2006f04`, `WarehouseGuardTrigger`). Each has a test.
* **Manual entries on O** are refused by PostgreSQL (`JournalGuardTrigger` on journal lines; reversal entries mirror an automatic entry and are allowed), reported as `account_automatic_only`.
* **API** (`NegativeStockEndpoints.cs`): `GET/PUT /api/v1/company/negative-stock/settings` (If-Match = version number), `GET /items`, `PUT /items/{itemId}`, `GET /obligations?status=open|all`, `GET /close-report?periodEnd=`;
  `GET /api/v1/company/stock-documents/{id}/negative-exception`, `POST .../negative-exception/approvals` (If-Match = document revision). Posting stays the 3.7B endpoint. A 409 may now carry `fields` naming the lines
  concerned (additive).
* **Authorization**: enablement and flags — `company.inventorysettings.view` / `.configure`; approval — `company.stockdocs.negativeexception` on the document's warehouse plus the right to see the document; obligations —
  `company.stockdocs.view`, filtered to the visible warehouses; close report — `company.inventoryaccounting.view`.
* **Shared UI (Web and Tauri, Arabic RTL / English LTR)**: a new screen *Negative-stock exception* (`/inventory/negative-stock`) with tabs Enablement, Eligible items, Obligations, Close report (tabs shown only to users
  who may use them, switching never resets input); the exception panel on the posting tab of an approved, not executed issue document (the server's split, provisional cost and source, approvals n of m, reason and the
  justified manual value of a line without a cost source); the obligation and variance accounts on the inventory accounting screen; the adjustment panel names the refused item.

## Decisions (standing authority, conservative; the design's D1–D7 and R1–R22 apply)
* **DB-1 (R16) — schema technique without DROP.** The migration guard refuses every non-comment `DROP`, including `ALTER TABLE … DROP CONSTRAINT`, and documents no allow path, so the operation-kind CHECK `k_f356c89a` cannot
  be widened (D1 and R10 (a)/(b) are therefore replaced). A provisional operation keeps its natural kind (2, 1, 4) and may have zero entries only when an obligation, a settlement, or the reversal of such an operation
  references it; a deferred constraint trigger enforces this at commit. `StockPosting.cost_operation` and `StockPostingReversal.compensating_operation` stay NOT NULL and unique; no column is added to the operation table.
* **DB-2 — no new foreign key to the posting LINE or stock REVERSAL tables.** The obligation and the settlement reference the posting (FK); the line, and the stock reversal of a settlement or obligation reversal, are
  verified by the insert triggers. A foreign key would have changed how the existing append-only guards of those tables answer a `TRUNCATE` (an FK error instead of the guard), which the 3.7B/3.7C tests pin.
* **DB-3 — R7 locking without write privilege on append-only tables.** `SELECT … FOR UPDATE` needs UPDATE privilege, which the runtime role must not have on immutable tables. Every writer of obligations, settlements and
  obligation reversals holds the company warehouse-structure advisory lock and the cost position row (`FOR UPDATE`, the engine's order); the insert triggers lock the position row too, and the obligation trigger takes the
  item row `FOR SHARE` (the eligibility flag trigger takes it `FOR UPDATE`), so a concurrent item change and an obligation serialise.
* **DB-4 — settlement sequence.** Settlements carry an identity sequence; "newest first" (R4/R21) is decided by it, never by timestamps.
* **Approval snapshot.** The plan (document id and revision, date, warehouse, accounting version, O/V/K, and per line: item, quantity, quantity on hand before the line, covered, uncovered, source, entry, pair, provisional total,
  justification) is hashed; posting recomputes it with each approved snapshot's own manual inputs and needs one whose hash matches. A second approver confirms the current snapshot (or enters the manual values).
* **Manual source.** A provisional TOTAL for the uncovered quantity of the line (never a unit cost), > 0 at the currency's scale, with a mandatory justification, allowed only when sources 1 and 2 are unavailable
  (`manual_cost_not_allowed`). A source-1/2 total that rounds to zero is refused (`provisional_cost_unavailable`).
* **Variance account.** V when configured, else the issue counter account K; refused (`negative_variance_account_invalid`) when the resulting account equals G or O, so the V lines of a settlement journal are exactly the
  difference (R17).
* **Dating (R22).** An exception issue dated before the position's last movement or before the latest obligation/settlement of the position is `backdated_not_supported`.
* **Adjustments (R15).** A shortage can never exceed the book quantity (it is counted − book) and a movement changing that book is refused (`book_changed` / `backdated_not_supported`), so an adjustment never reaches the exception.

## Double-entry examples (exact figures of the tests; I inventory, K issue counter, G receipt counter, O obligation, V variance)
* **5.1 mixed issue** — 3 on hand at 15.00, issue 10, provisional from the last average pair (3, 15.00): covered 3 = 15.00, uncovered 7 = 35.00. One entry: Dr K 50.00 / Cr I 15.00, Cr O 35.00. Position 0 / 0.00; last pair (3, 15.00).
* **5.2 full settlement** — obligation 7 / 35.00, receipt 7 at 42.00: Dr O 35.00, Dr V 7.00 / Cr G 42.00 (receipt with no entry, no own journal). At 28.00: Dr O 35.00 / Cr G 28.00, Cr V 7.00.
* **5.3 partial, then more** — receipt 4 at 24.00: Dr O 20.00, Dr V 4.00 / Cr G 24.00 (open 3 / 15.00); receipt 10 at 45.00: match 3 → Dr O 15.00 / Cr G 13.50, Cr V 1.50; the remaining 7 enter at 31.50: Dr I 31.50 / Cr G 31.50
  (average 4.50, never touched by the provisional cost).
* **5.4 several obligations, rounding** — pair (3, 10.00): obligation A 3 = 10.00, B 2 = 6.67 (same date, A first by sequence). Three receipts of 1 at 5.00 settle A for 3.33, 3.34, 3.33 (the exact remainder on the final
  match); a receipt of 3 at 9.00 settles B for provisional 6.67, actual 6.00 (difference −0.67), 1 unit enters at 3.00.
* **R4 reversal of a settling receipt whose settlement journal was posted** — draft reversal entry Cr O 35.00, Cr V 7.00 / Dr G 42.00; the obligation is open again 7 / 35.00.
* **Across periods** — issue 12 Jan (Dr K 35.00 / Cr O 35.00, posted), January can be soft closed (warning) but not Final Closed while the obligation is open; a receipt of 2 March settles it (variance in March); at 31 Jan
  the point-in-time O balance is still 35.00 and January can then be Final Closed; reversing the March receipt is refused (`negative_obligation_period_closed`).

## Tests
* PostgreSQL (`NegativeStockTests` 15, `NegativeStockApiTests` 1; disposable cluster): 5.1, fully uncovered issue and fully matched receipt with both signs (5.2), 5.3, oldest-first with rounding across three partial matches,
  provisional-cost priority (last average, last stock-document receipt, justified manual, never zero), refusals (FIFO, lot, unflagged, disabled company, missing/foreign permission, creator, poster, transfer, receipt,
  not needed, stale snapshot, second approver, switches turned off), reversals in order with exact open quantities and the R4 posted-journal case, the receipt dated before the obligation and every other inflow,
  close probe and point-in-time reconciliation across periods with the Final-Close reversal refusal, concurrency (posting once, two receipts never over-settle), every database invariant attacked directly, guarded changes
  R12, account validation and a change of O between versions with manual entries refused, the adjustment conditions R15 (1), (2) and (3), and the HTTP endpoints.
* Unit: `CostingMath.Scale`; schema map, migration list and permission fixtures raised.
* Frontend (vitest): `negativeStock.test.tsx` (screen tabs and actions, eligibility, obligations, close report, English, the exception panel's approval with manual value and refusals).

## Review of PR #45 (record: `docs/review-records/PR-45-phase-3.8B-3.md`)
* **M1** — the obligation insert trigger also requires the item's LIVE resolved method to be moving average and locks the item's group chain and the inventory settings row `FOR SHARE` (a policy change under the
  inventory master lock and an exception posting serialise; whichever wins, no open obligation coexists with another method).
* **M2 / M4** — approval and preview plan read-only: no position is created or locked (a missing position reads as empty with the method its policy resolves to, so the snapshot equals the posting's); the preview takes no
  company-wide lock and is rolled back. Only posting creates the position.
* **M3 / M5** — the receipt counter account may not be the O or V of an open obligation: refused at settlement (`negative_settlement_account_conflict`) and at configuration save
  (`receipt_counter_equals_open_obligation_account`).
* Lows: the manual amount travels as decimal text; the group guard checks the open quantity first. Follow-ups: re-check the approver's permission at posting; scope the close report per warehouse if needed.
* Tests: `NegativeStockReviewTests` (3 PostgreSQL tests; registry 943 → 946).
## Not in this slice
* The owner policy's "approved current-period correction workflow" (C3) does not exist; a receipt in a Final Closed period is refused (`fiscal_period_final_closed`) and reported.
* Back-dated movements with obligations (C4) belong to 3.8D; until then they are refused.
* No closing checklist exists; the soft-close warning is the close report (R20).
