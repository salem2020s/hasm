# HASM - Phase 3.8C: lots, serial numbers and expiry (DESIGN PROPOSAL, nothing built)

**Scope, exactly as the roadmap and the owner define it.** `docs/ROADMAP.md` gives Phase 3.8C as "Lots, serial numbers, expiry and reservations", approved scope SRS 9.1 and 9.4, prerequisite 3.8A, with the deliverables "Tracking by lot, serial and expiry through documents, costing and balances; reservations" and the acceptance criteria "Quantities per lot/serial reconcile to item x warehouse positions; expired stock identifiable; reservations never exceed available stock". The owner's decision D-219 (OD-11) says: "Proceed with the approved Release 1 inventory scope using strict defaults: serial uniqueness, traceable lot movements, no unsupported negative stock for serial/lot items, refusal of expired-stock dispatch by default, and full source-document lineage. Do not invent reservation behavior or loosen FIFO safeguards. Use independently reviewed implementation slices." This document designs lot, serial and expiry tracking within that scope and does not widen it.

**Reservations are in the roadmap scope of this phase and are NOT designed here.** The owner forbids inventing reservation behaviour (D-219), calls reservations "a separate future feature requiring an approved stock-allocation contract" (D-200), and the question whether "future" means after Release 1 is open with the owner (register item RESERVATIONS-SCOPE in `docs/INTEGRATION-STATUS.md`). Until that is answered and a stock-allocation contract is approved, the reservation deliverable and its acceptance criterion stay OPEN for this phase; nothing in this design reserves stock, and nothing here removes the requirement.

**Status: PROPOSAL** for independent review (D-219: "Use independently reviewed implementation slices") and, where it raises policy, for the owner. Nothing is built, no migration is written, and no decision ID, audit code, permission name or migration number is allocated; names are working names.

**Revision 2.** The independent review of revision 1 (Agent FAC, at `6754ad6`: request changes, 5 Medium and 7 Low; scope, sources and arithmetic confirmed) is worked in. Section 15 lists the reviewer's recommendation per owner question.

How to read it. **[APPROVED: source]** is a rule settled by the approved SRS or a recorded owner decision. **[AS BUILT: file, function]** is a statement about `main` that was verified in the named code for this document. **[PROPOSAL]** binds nobody until reviewed and, where needed, approved. **[OPEN: CQ-n]** is a point the approved sources do not settle: options, consequences and a proposed conservative default are given, and it is listed in section 14.

## 1. Sources

* **SRS** (`docs/SRS.md`), section 9: the first item (items, units, barcodes, lots, serial numbers, expiry, stocktakes, adjustments, transfers, reservations); the two approved valuation methods; cost per item and per warehouse; negative stock forbidden by default, with a limited authorised exception for eligible moving-average items and never for FIFO or lot- or serial-tracked items; operation date distinct from recording date. Section 19 rules 2, 3, 5 and 8 (one effect per event, no edit of posted documents, no posting into Final Closed periods, full traceability of the cost of goods).
* **Decisions** (`docs/DECISIONS.md` on `main`): D-017 (FIFO and moving weighted average), D-018 (cost per item and warehouse; actual cost moves with a transfer), D-019 (negative stock forbidden by default; never for FIFO, serial or lot), D-219 (above), D-200 (quotes and orders reserve nothing; reservations need an approved stock-allocation contract), D-203 (returns), D-190 and D-191 (the replay design), D-198 (revaluation direction), D-220 (the database guard against stock and cost postings into Soft Closed, Final Closed or missing periods).
* **Roadmap** (`docs/ROADMAP.md`, generated from `tools/roadmap/phases.js` and `decisions.js`): the 3.8C entry quoted above and its note "Needs a design proposal first: the approved costing scope is item x warehouse (D-018); lot/serial dimensions must not break it"; the OD-11 row, decided by D-219.
* **What is built**: `docs/PHASE-3.4.md` (item master), `3.5` (stock documents), `3.6` (costing engine), `3.7A` to `3.7D` (posting), `3.8A`, `3.8B`, `3.8B-1`, `3.8B-2`, `3.8B-3` (balances, stocktakes, adjustments, negative-stock exception), and the code named in section 3.
* **Other designs this one must fit**: `docs/PHASE-3.8D-REVALUATION-CONTRACT.md` (a proposal on `main`), the replay design on the branch of PR 48, and the migration-guard hardening of PR 188 (open, not merged; read from its description only).

## 2. What the approved sources settle

* **[APPROVED: SRS 9; ROADMAP 3.8C]** Lots, serial numbers and expiry are Release 1 requirements; quantities per lot or serial must reconcile to the item and warehouse position; expired stock must be identifiable.
* **[APPROVED: D-017, D-018]** Two valuation methods, FIFO and the moving weighted average, with cost per item and warehouse. No approved source lists specific identification. **[APPROVED: D-219]** "Do not ... loosen FIFO safeguards".
* **[PROPOSAL: the team's derivation from D-017 and D-018, to be confirmed by the owner through CQ-10]** A lot or serial is therefore a QUANTITY-tracking dimension: choosing a lot never changes the cost drawn. The roadmap note and the reconciliation note of D-219 say the same, but both are team wording, not owner text.
* **[APPROVED: SRS 9, D-019, D-219]** No negative stock for lot- or serial-tracked items, in any form. The words "no unsupported negative stock" open no case: SRS 9 and D-019 forbid it outright for these items.
* **[APPROVED: D-219]** Strict defaults: serial uniqueness; traceable lot movements; refusal of expired-stock dispatch by default; full source-document lineage; no invented reservation behaviour; independently reviewed slices.
* **[APPROVED: SRS 19 rules 2, 3, 5; D-220]** One stock effect per business event; posted documents are corrected by reversal, never edited; nothing is posted into closed or missing periods.

**Not settled by any approved source**, and therefore section 14: the scope of serial and lot uniqueness; what exactly "expired" and "dispatch" mean and how expired stock may leave; whether any override exists; how lots are picked; how an expiry-only item is carried; how tracking is switched on for an item that already has stock; what a lot-specific loss of value does.

## 3. What exists on `main` (verified in the code named)

* **Tracking flags are attributes only.** **[AS BUILT: `CompanySchema.Inventory.cs`, class `Item`; migration `0012_company_inventory_master_data.sql`]** An item has three independent boolean flags: lot, serial, expiry. The only database rule on them is that a service cannot carry any (constraint `k_de22e5a1`). **[AS BUILT: `InventoryModel.cs`, `TryNormalizeItem`]** The item validation refuses a tracked service (`service_cannot_be_tracked`) and, in the part read for this document, does not look at stock or movements when the flags of an existing item change. There is no lot table, no serial table and no tracking quantity anywhere. **[AS BUILT: `CompanyInventoryService.cs`, `CreateItemAsync` and `UpdateItemAsync`]** The flags are written by item creation and by the item update, whose UPDATE statement sets all three from the input with no check of stock, movements or documents; **[AS BUILT: `InventoryImportAdapters.cs`, the items adapter]** the items import carries the three flags as columns and creates items through the same service (`CreateItemInWorkAsync`); its header comment says it imports no stock quantities, values or opening balances. So today a flag can be switched on for an item that HOLDS stock, and that stock is then frozen by the refusals below.
* **A tracked item cannot be moved, counted or costed.** The refusal `tracked_item_not_supported_yet` is raised in three service functions: **[AS BUILT: `InventoryCostingService.cs`, `ItemAsync`]** the costing engine; **[AS BUILT: `StockDocumentService.cs`, `ResolveLines`]** stock document lines, with the comment that such an item "cannot be moved instead of being moved without its tracking"; **[AS BUILT: `StocktakeService.cs`, `ResolveLines`]** stocktake lines. **[AS BUILT: `StockDocumentService.cs`, `SearchItemsAsync`]** The item search for stock documents leaves tracked items out.
* **These refusals live in services, not in PostgreSQL.** **[AS BUILT: search of the company migrations for the three flag columns]** The flags are referenced only by migration 0012 (the columns and the service constraint) and by migration 0020. No constraint or trigger of the stock document, costing or posting tables refuses a tracked item, so lifting the service refusals needs no change to an existing database object.
* **The negative-stock exception excludes tracked items in the database.** **[AS BUILT: migration `0020_company_negative_stock.sql`, functions `f_89cdb0aa` and the item trigger `r_b3836cf7`]** The eligibility flag can be set only for active goods "without lot, serial or expiry tracking", and a change of an item's tracking flags fires a trigger that refuses while a negative-stock obligation is open. **[AS BUILT: `StockPosting.Negative.cs`, the eligibility query in `RefuseIneligibleUnderLocksAsync`; `NegativeStock.cs`]** The services apply the same exclusion. This design changes none of it.
* **Stocktake variances post gross, per line.** **[AS BUILT: `docs/PHASE-3.8B-2.md`, decision D2 there; not re-read in `StockAdjustment.cs`]** An adjustment has one line per item with a direction; shortages post as ONE costing issue at engine cost and surpluses as ONE costing receipt at amounts the poster enters, in one transaction and one journal. A surplus therefore creates a new FIFO layer.
* **Units carry decimals.** **[AS BUILT: `CompanySchema.Inventory.cs`, `Unit.Decimals`]** A base unit may allow fractional quantities.
* **Costing, posting, reversal, stocktake, adjustment** as described in `docs/PHASE-3.6.md`, `3.7B`, `3.7C`, `3.8B-1` and `3.8B-2`: one cost position per item and warehouse, locked per position; append-only cost entries; one stock posting per executed document with one posting line per document line; reversal per operation; back-dated entries refused. Permissions in use today (read from the permission strings in the backend): `company.stockdocs.view|create|update|submit|approve|cancel|post|reverse|negativeexception`, `company.stocktake.*`, `company.stockbalance.view`.

## 4. Design principles (PROPOSAL; each becomes an invariant in section 11)

1. **Tracking is a second, quantity-only ledger beside the cost ledger.** Every movement of a tracked item writes, in the same transaction as its cost entry, tracking rows that say WHICH lots or serials moved. The cost ledger is not keyed by lot and is not read differently (D-018). The one place where tracking could CAUSE a cost effect that would not otherwise exist is a stocktake that moves quantity between lots; section 7.5 says how that is kept out (CQ-14).
2. **The two ledgers always agree in quantity.** For every tracked item and warehouse, the sum of its lot quantities (or the count of its serials in stock) equals the quantity of its cost position, at every commit.
3. **No tracked movement without complete tracking.** A line of a tracked item that does not name lots or serials for exactly its base quantity is refused. Nothing is ever moved "without its tracking".
4. **Never negative.** No lot balance below zero; a serial is in at most one place; the position rule of the engine is untouched.
5. **Everything is traceable to its source document** in both directions: from a lot or serial to every document that moved it, and from a document line to its lots and serials (D-219, SRS 19 rule 8).
6. **Additive only.** New tables, new columns that are nullable or defaulted, new functions, and new triggers. Nearly all new triggers sit on the new tables; **two NEW triggers sit on EXISTING tables and are named here so that no reviewer has to find them**: the deferred reconciliation trigger on the cost side (section 5.2) and the tracking-flag rule on the item table (section 5.1). Both are added with CREATE TRIGGER under new names, calling new functions. No existing constraint, trigger or function is dropped, replaced, disabled or widened.
7. **Strict by default; nothing invented.** Where the sources are silent the stricter behaviour is the default and the point is an owner question.

## 5. Slices (PROPOSAL; each is implemented, tested, independently reviewed and merged on its own, D-219)

| Slice | Content | Changes behaviour for |
|---|---|---|
| S1 | Lot and serial registers; rules for the tracking flags; no movement yet | item master only |
| S2 | Tracking lines on stock documents; the tracking ledger; posting, transfer and reversal of tracked items; reconciliation | receipts, issues, transfers of tracked items (today refused) |
| S3 | Expiry: dates on lots and serials, the refusal of expired dispatch, identification of expired and soon-expiring stock | issues and transfers of items with expiry |
| S4 | Stocktakes and adjustments per lot and serial | counting of tracked items (today refused) |
| S5 | Shared screens and the trace and balance reads | Web and Tauri |
| not designed | Reservations | blocked on RESERVATIONS-SCOPE and a stock-allocation contract |

Until S2 is merged, `tracked_item_not_supported_yet` stays exactly as it is. S2 lifts it only on the paths that write complete tracking; every other path (the negative-stock exception, replay, revaluation, commercial documents until their own slices) keeps refusing tracked items.

### 5.1 S1: registers and flag rules

* **Lot** (per item): a lot code, optional manufacturing date, expiry date (S3), the document that first received it. One row per item and lot code; the same lot keeps its identity in every warehouse of the company, so a transfer moves quantity of the SAME lot **[OPEN: CQ-2]**.
* **Serial unit** (per item): a serial number and its present state (in stock in one warehouse, or out). **[APPROVED: D-219]** serial uniqueness; its scope is **[OPEN: CQ-1]**.
* **Combinations of the three flags.** Lot + expiry: the lot carries the expiry date. Serial + expiry: the serial carries it. Serial + lot: a serial may also belong to a lot (same item); quantities reconcile through the serials. Expiry only: the stock still needs a carrier for its date **[OPEN: CQ-8]**.
* **Whole units for serials.** A serial is one unit. **[PROPOSAL]** A line of a serial item must have a whole base quantity, also after conversion from an alternate unit (`serial_quantity_not_whole`), and the serial flag is refused for an item whose base unit allows decimals (`serial_item_needs_whole_unit`).
* **Changing the flags of an item.** As built nothing stops it (section 3), through any of the three writers: item creation, the item update, and the items import. **[PROPOSAL]**
  * Switching a tracking flag ON is refused while the item has a non-zero cost position in any warehouse, an open stock document, stocktake or adjustment (`tracking_change_item_has_stock`, `tracking_change_item_in_use`). How stock that already exists becomes tracked is **[OPEN: CQ-9]**.
  * Switching a flag OFF is allowed while the item has NO tracking balance and no open document, even if it holds stock. This is the way out for the state that exists today (next point).
  * **Items that are already tracked AND hold stock (review M3).** Such an item is frozen today, and without a rule it would stay frozen in both directions: its flag could not change because it has stock, and its stock could not move because it has no lots. **[PROPOSAL]** Slice S1 ships a read-only pre-check that lists every such item per company. For each, the company chooses: switch the flags off (allowed by the rule above, the stock becomes ordinary untracked stock again), or, if CQ-9 approves it, assign tracking with the dedicated document. S2 is not enabled for a company while the list is non-empty **[OPEN: CQ-15]**.
  * **Tracking and the negative-stock exception (review M4).** As built, the item trigger of migration 0020 refuses a tracking-flag change only while an obligation is OPEN, so an item whose eligibility flag is set and that has no open obligation can be made tracked; the posting service then refuses the exception for it at posting time. **[PROPOSAL]** Setting any tracking flag is refused while the item's eligibility flag for the exception is set (`tracking_change_item_negative_eligible`); the flag must be removed first, by its own audited action. The existing database rule already refuses setting eligibility on a tracked item.
  * **Enforced in the database, serialised with postings.** The rules above are enforced by a NEW trigger on the item table (a new function; the existing trigger of migration 0020 stays and keeps its own refusal), not only by the services, so that all three writers and raw SQL meet the same rule. The trigger takes the item row lock; postings of the item take the same row in share mode before they write (the pattern `RefuseIneligibleUnderLocksAsync` uses today for flagged items), so a flag change and a posting of the same item cannot interleave.

### 5.2 S2: tracking lines, ledger, posting

* **Tracking details on a document line.** For a tracked item each line of a receipt, issue or transfer carries detail rows: a lot with a base quantity, or individual serials (quantity one each). A receipt may name a new lot or new serials, creating them at posting; an issue or transfer-out must name existing ones. The details are part of the document's revision (an edit after approval follows the existing re-approval rules), and they must add up to the line's base quantity exactly, else `tracking_incomplete`.
* **Picking.** Which lots or serials an issue takes is stated on the line by the user. HASM does not choose silently **[OPEN: CQ-7]**.
* **The tracking ledger.** Append-only entries (item, warehouse, lot or serial, direction, base quantity, effective date, the stock posting line and through it the cost entry and the source document) and a balance per item, warehouse and lot; a serial's state row. Written by the posting service in the transaction of the stock posting, under the cost position lock the engine already takes.
* **Locks beyond the position lock (review L1).** The position lock is per item AND warehouse, so it does not serialise two receipts of the same new serial, or of the same new lot code, in different warehouses. Uniqueness is therefore carried by unique indexes (item and serial; item and lot code), and the posting takes the lot and serial rows it touches in a deterministic order (item id, then lot code or serial, ascending), after the position locks, so that concurrent postings can neither create a duplicate nor deadlock. A receipt that names an EXISTING lot with a different expiry or manufacturing date than the lot carries is refused (`lot_attribute_mismatch`) **[OPEN: CQ-17]**.
* **Lifting the engine's refusal only where tracking is complete (review M2).** The engine's single refusal for tracked items in `ItemAsync` is kept as the default. A caller that writes complete tracking in the same transaction passes an explicit opt-in on its costing request (working name: tracked items accepted); only the stock posting, reversal and adjustment paths of this phase set it. Every other caller keeps refusing, and a test enumerates the engine's callers and asserts it.
* **Transfers.** One outflow from the source warehouse and one inflow into the destination for the same lots or serials; cost moves as today (actual cost, D-018).
* **Reversal.** Reversing a posting writes negating tracking entries for exactly its lots and serials. An inflow is reversible only while exactly those lots still hold the quantity in that warehouse and those serials are still in stock there; the engine's own cost rule (untouched stock) applies as today, and both must hold. Refusals: `tracking_lot_consumed`, `tracking_serial_moved`.
* **Reconciliation, enforced from BOTH sides (review M2).** A deferred constraint trigger on the tracking tables (the pattern of migration 0020) compares, at commit, the tracked quantity of each touched item and warehouse with its cost position. A second deferred constraint trigger on the COST side fires for every cost entry of an item that is tracked at that moment and makes the same comparison, so that a cost entry written with no tracking row at all (another caller, a future path that forgets, raw SQL) also aborts at commit. That second trigger is a NEW trigger on an EXISTING table (principle 6); it changes no existing object, and the hardened migration guard of PR 188, as described there, concerns disabling triggers and replacing guard functions, neither of which this needs; the database-safety reviewer is asked to confirm that against the merged guard. Both checks lock the position row before they read, so two concurrent transactions cannot each pass on a stale figure. A `ReconcileAsync` extension reports the same per item, warehouse and company.
* **Refusals** (working names): `tracking_required`, `tracking_incomplete`, `tracking_not_allowed` (details on an untracked item), `lot_not_found`, `lot_quantity_insufficient`, `serial_not_found`, `serial_not_in_warehouse`, `serial_duplicate`, `serial_quantity_must_be_one`, `serial_quantity_not_whole`, `lot_attribute_mismatch`.

### 5.3 S3: expiry

* **[APPROVED: D-219]** Expired-stock dispatch is refused by default; **[APPROVED: ROADMAP]** expired stock is identifiable.
* **[PROPOSAL]** The expiry date is required when a lot or serial of an item with the expiry flag is first received, is immutable afterwards except through an audited, privileged correction (which can make expired stock valid again and is therefore a privileged, reasoned action with its own test), and is compared with the LATER of the document's effective date and the date of posting, so that a document dated in the past cannot dispatch stock that is expired today. What "expired" means at the boundary is **[OPEN: CQ-3]**; which movements are a "dispatch" and how expired stock may leave is **[OPEN: CQ-4]**; whether any override exists is **[OPEN: CQ-5]**; whether expired or nearly expired goods may be received is **[OPEN: CQ-6]**.
* **Identification.** Balances by lot and serial show the expiry date and a state (valid, expiring within a configurable number of days, expired); the state is derived from the date, never stored.

### 5.4 S4: stocktakes and adjustments

Counts are entered per lot or serial and the variance is computed per lot or serial in base units. What then goes through the COST path is defined in section 7.5: under the proposed rule only the NET variance per item and warehouse is posted as a shortage or surplus (with its tracking details), and a redistribution between lots that nets to zero is a tracking-only reassignment. A surplus of a serial item needs the serial numbers; a surplus of a lot item names the lot. The existing book-changed and concurrency rules of 3.8B apply unchanged.

### 5.5 S5: screens and reads

See section 13.

## 6. Data model changes (PROPOSAL; logical; additive only; no migration number allocated)

All physical names are generated by the schema tooling as for earlier company migrations. Planned objects, each NEW:

| Object | Purpose | Notable rules |
|---|---|---|
| lot table | one row per item and lot code | unique (item, lot code); expiry and manufacturing dates; append-only except an audited expiry correction |
| serial table | one row per item and serial | uniqueness per CQ-1; state and current warehouse changed only by tracking entries |
| document-line tracking table | lots and serials named on a stock document line | belongs to the line and its revision; quantities positive; serial quantity one |
| tracking entry table | the append-only tracking ledger | foreign key to the stock posting line; no update, delete or truncate |
| tracking balance table | quantity per item, warehouse and lot | quantity never negative; changed only together with an entry |
| stocktake and adjustment tracking tables | counted and adjusted lots and serials | as the document-line table |
| functions and triggers on these tables only | append-only guards, balance maintenance, the deferred reconciliation with the cost position | new names; none replaces an existing function |

**Compatibility with the migration guard, present and hardened.** The existing guard refuses DROP, TRUNCATE and DELETE; the hardening of PR 188 (open; read from its description) additionally concerns paired trigger disabling and the replacement of existing guard functions. This design needs none of these: it creates new objects and adds no column to a guarded table that is not nullable or defaulted, disables no trigger, and replaces no existing function. The before-and-after catalog comparison of the existing guard functions, triggers and constraints is a mandatory test (section 12).

**Runtime privileges.** SELECT and INSERT on the new tables; UPDATE only where a balance or a serial state changes; never DELETE; granted by the reviewed grant code, as for earlier tables.

## 7. Accounting effects, with worked examples

**[PROPOSAL: a design goal, not an approved rule]** Tracking should have no accounting effect of its own: every journal entry of a tracked movement is the one its cost entry already produces under Phases 3.6 and 3.7 (inventory account against the receipt or issue counteraccount; none for a transfer), and no new account or mapping is proposed except where a question asks for one (CQ-16). Revision 1 stated this as approved and without exception; the review showed that it is false for stocktakes as built unless a rule is added (section 7.5). The consequences of keeping cost per item and warehouse must also be stated completely, because they surprise users:

### 7.1 FIFO: the lot issued and the cost layer consumed can differ

Item I, FIFO, warehouse W, base currency with two decimals.

| Receipt | Lot | Quantity | Amount | Cost layer |
|---|---|---|---|---|
| 2026-03-01 | A (expires 2026-09-30) | 10 | 100.00 | L1: 10 for 100.00 |
| 2026-04-01 | B (expires 2027-03-31) | 10 | 120.00 | L2: 10 for 120.00 |

An issue of 4 units on 2026-05-01, picked from lot B:

* Tracking ledger: lot B minus 4. Lot balances after: A 10, B 6. Sum 16.
* Cost ledger: FIFO consumes the oldest layer, L1: cost 100.00 x 4 / 10 = 40.00. Layers after: L1 6 for 60.00, L2 10 for 120.00. Position 16 for 180.00.
* Journal (as 3.7B): debit the issue counteraccount 40.00, credit the inventory account 40.00.

The units physically taken came from the receipt at 12.00, the cost charged is 10.00 per unit. Quantities reconcile: lots 10 + 6 = position 16. **[OPEN: CQ-10]** asks the owner to confirm this explicitly, because the alternative (cost by lot or serial) is specific identification, which no approved source lists. The full consequences the owner should have in front of them (review M5):

* **Serial items.** The cost charged for the unit sold is not that unit's purchase cost. For goods that are not interchangeable (vehicles, machines, phones) this is the case in which accounting frameworks commonly expect specific identification. The SRS names no framework, so this is put to the owner as a consequence and not passed over.
* **Reports.** Margin by lot or serial, stock value by lot, and the value of expired or expiring stock cannot be DERIVED; they can only be allocated from the position's cost.
* **Return of a specific lot or serial to the supplier.** It leaves at the position's cost, not at what that lot cost; a difference to the supplier's credit has no account in this design (it belongs to purchase-return accounting).
* **Ageing.** The dates of the open FIFO layers stop corresponding to the lots physically on hand (after the issue above the oldest layer is partly consumed while lot A is untouched), so ageing by cost layer and ageing by lot disagree; reports must say which one they show.
* **Reversals.** After the issue above NEITHER receipt can be reversed: lot B is partly consumed in tracking, and layer L1 is partly consumed in cost. Each ledger blocks the reversal of a different receipt (section 5.2).

### 7.2 Moving weighted average

Same receipts under the average method: position 20 for 220.00 (derived unit 11.00). The issue of 4 from lot B costs 220.00 x 4 / 20 = 44.00 whichever lot is picked. Journal: debit the issue counteraccount 44.00, credit the inventory account 44.00. Lots after: A 10, B 6; position 16 for 176.00.

### 7.3 Transfer of a lot

Transfer of 5 units of lot A from W to W2 (FIFO): the tracking ledger moves 5 of lot A from W to W2 (same lot identity). The cost ledger consumes 5 from the oldest layer of W (L1 after the issue above: 60.00 x 5 / 6 = 50.00) and creates a mirrored layer of 5 for 50.00 in W2. No journal (it nets to zero inside the inventory accounts). Both warehouses reconcile: W lots A 5 + B 6 = 11 = position; W2 lot A 5 = position.

### 7.4 Expired stock

Removing expired stock is an ordinary outflow of quantity at the position's cost through the existing issue or adjustment accounting (3.7B, 3.8B-2): no new account is invented. Because cost is not kept per lot, the value that leaves with an expired lot is the FIFO or average cost of the position, not the purchase cost of that lot; and a loss of VALUE of one particular lot (damage, approaching expiry) cannot be expressed at lot level by the revaluation contract either, which works per cost layer or position **[OPEN: CQ-11]**. A disposal issue posts, as built, to the single issue counteraccount of the inventory configuration (3.7A), so a loss through expiry is not distinguishable in the general ledger from any other issue **[OPEN: CQ-16]**.

### 7.5 Stocktakes: when a count moves quantity between lots (review M1)

Counting per lot can show lot A two short and lot B two over for the same item and warehouse: the item's total is right, two units were mislabelled or mis-keyed. As built, an adjustment posts every shortage as an issue and every surplus as a receipt (section 3). Applied per lot, this count would post an issue of 2 at engine cost and a receipt of 2 at an entered amount: a cost effect, a journal and a new FIFO layer, although no stock was lost or found. Without tracking the same count posts nothing. **[OPEN: CQ-14]** The options:

* (a) **Proposed default: net per item and warehouse through the cost path; the rest is a tracking-only reassignment.** The net variance of the item and warehouse (here zero) is what posts as shortage or surplus, with tracking details for exactly that net quantity. The redistribution between lots that nets to zero is recorded by a **tracking reassignment document**: quantity-only entries (lot A minus 2, lot B plus 2), no cost entry, no journal, with its own number, independent approval (not the creator, last editor or submitter), audit events, and the period rule of the stock side (its date in an Open period; Soft Closed, Final Closed and missing periods refused). It is the one place where tracking entries are NOT children of a stock posting line, and section 9 is amended for it. The same document corrects a mis-keyed lot or serial after partial consumption, where a reversal is no longer possible.
* (b) Gross posting as built: every lot shortage is an issue and every lot surplus a receipt. Simple, and it creates cost effects from relabelling: in the example an issue of 2 at FIFO cost (say 20.00) and a receipt of 2 at an entered amount, which changes layers, possibly the average, and the result whenever the two amounts differ.

Under (a), with example 7.1's stock (lots A 10 and B 6, position 16): a count of A 8 and B 8 posts nothing to cost and reassigns 2 from A to B; a count of A 8 and B 7 posts a net shortage of 1 (with tracking: lot A minus 1) and reassigns 1 from A to B.

## 8. Negative stock

**[APPROVED: SRS 9, D-019, D-219]** Never for lot- or serial-tracked items. For an item with ONLY the expiry flag the approved sources do not speak; its exclusion from the exception is **[AS BUILT: migration 0020]** and is kept. **[PROPOSAL]** Three independent barriers: the engine's position rule (as built); the non-negative lot balance and the single location of a serial (new database rules); the unchanged exclusion of tracked items from the negative-stock exception (as built, section 3). An item cannot become eligible for the exception while tracked (existing database rule), and cannot become tracked while its eligibility flag is set (new rule, section 5.1), while an obligation is open (existing trigger) or while it has stock (section 5.1).

## 9. Interaction with Phase 3.8D and the period guard

* **Replay (X1-A; D-190, D-191; design on the branch of PR 48).** A replay epoch stornos and rebooks cost entries of positions; it changes costs, not quantities. The engine refuses tracked items today (section 3), so no replay can contain one at present. **[PROPOSAL]** For the first implementation tracked items stay OUTSIDE the replay: a replay plan whose closure contains a tracked item is refused, and a back-dated movement of a tracked item is refused as every back-dated movement is today. Reason: epoch storno and rebook rows are ordinary cost entries; if they carried tracking rows the lot history would be rewritten, and if they did not the reconciliation of section 5.2 would have to learn about epochs. Supporting it needs its own reviewed design **[OPEN: CQ-12]**.
* **Revaluation contract (`docs/PHASE-3.8D-REVALUATION-CONTRACT.md`, a proposal).** Its section 13 leaves the revaluation of tracked items to this phase. **[PROPOSAL]** A revaluation changes the carrying value of cost layers or of an average position and no quantity, so it has NO tracking effect and writes no tracking row; under the contract's option of an outflow and inflow pair, the pair nets to zero quantity inside one transaction and the reconciliation at commit still holds. The reconciliation trigger must therefore compare at COMMIT, not per statement (it does, being deferred). Revaluation of tracked items is enabled only after both this phase and the contract are approved; until then the engine's refusal stands. The lot-level question is CQ-11.
* **STOCK-PERIOD-DB (D-220).** The owner approved an additive guard that refuses stock and cost postings into Soft Closed, Final Closed or missing periods; it is not built, and the recorded proposal places it on the header tables of cost operations, stock postings and reversals, adjustments and negative-stock settlements. **[PROPOSAL]** Tracking entries exist only as children of a stock posting line (foreign key, same transaction), where they inherit that guard, or, under CQ-14 (a), as children of a tracking reassignment document, whose header gets the same kind of insert guard against Soft Closed, Final Closed and missing periods as the other header tables. There is no third parent; a test inserts a tracking entry by raw SQL for each kind of parent in a closed period and expects the refusal.
* **Commercial documents (3.13 S3) and returns (D-203).** A delivery, receipt or return of a tracked item posts through the stock posting path and therefore needs tracking details like any stock document; a customer return of a serial item names the serial that left on the original document, and a return of a lot item names the lot of the original document **[OPEN: CQ-13]**; under the CQ-6 default a customer return of goods that are already expired is refused at receipt, which the owner should decide knowingly. The commercial slices state this dependency; nothing is built here.
* **Negative-stock exception, landed cost, method conversion:** unchanged; each keeps refusing tracked items until its own design says otherwise.

## 10. Security, permissions and audit (PROPOSAL)

* **Authorization** stays on the server and inside every query, scoped by warehouse as stock documents are. Entering tracking details is part of creating and editing a document (the existing `company.stockdocs.create` and `.update`); posting and reversal use the existing `.post` and `.reverse`. New, separate permissions (working names, privileged where marked, never added to an existing preset automatically): view of lot and serial traces and balances; correction of an expiry date (privileged, audited, reason required); the exit of expired stock (privileged), if CQ-4 and CQ-5 allow one.
* **Separation of duties** is unchanged: the approver of a document is not its creator, last editor or submitter, and tracking details are part of what is approved.
* **Audit.** Business-audit events for: lot created; serial registered; expiry date corrected (old and new date); tracking flag of an item changed; a refused dispatch of expired stock; a tracking reconciliation failure. Events carry identifiers and counts, never free text. Every step fails closed when its event cannot be written. Codes are allocated by the lead when a slice is planned.
* **No cross-company exposure:** lots and serials live in the company database; serial numbers appear in audit detail only as identifiers of that company.
* **Imports.** **[AS BUILT: `InventoryImportAdapters.cs`]** The items import writes the tracking flags and imports no stock. It must obey the flag rules of section 5.1 (the database trigger guarantees it; a test pins it). There is no opening-stock import today, so the opening stock of a tracked item enters through receipt documents with tracking details; an opening-stock import with lot and serial columns would be new work and is not part of this phase.

## 11. Invariants (each pinned by at least one test)

* T1. For every tracked item and warehouse, at every commit: sum of lot balances (or count of serials in stock there) = quantity of the cost position; enforced from the tracking side AND from the cost side.
* T2. No lot balance is negative; a serial is in stock in at most one warehouse; a serial number is unique within the scope CQ-1 decides.
* T3. A posted movement of a tracked item has tracking entries for exactly its base quantity; a movement of an untracked item has none.
* T4. Tracking entries are append-only and each belongs to exactly one stock posting line; a reversal negates exactly the entries of the posting it reverses.
* T5. For receipts, issues, transfers and reversals the cost ledger is unchanged by tracking: for the same sequence of quantities and amounts, costs, layers, positions and journals are identical with and without tracking details. For stocktakes the same holds under CQ-14 (a), where only the net variance per item and warehouse reaches the cost path; under CQ-14 (b) it does not hold and the difference is the stated cost effect.
* T6. No item has a tracking flag and the stored eligibility flag of the negative-stock exception at the same time (enforced in both directions by database rules, the existing one and the new one); no tracked position is ever negative.
* T7. No dispatch of expired stock is posted except through the exits CQ-4 and CQ-5 allow.
* T8. From any lot or serial every source document that moved it is reachable, and from any document line its lots and serials.
* T9. No existing constraint, trigger, function or guard is dropped, replaced, disabled or widened.
* T10. A tracking flag is never switched ON while the item has stock or open documents, and never switched OFF while it has a tracking balance or open documents; the rule holds for every writer (item creation, item update, items import, raw SQL).
* T12. Every tracking entry has exactly one parent: a stock posting line, or (CQ-14 (a)) a tracking reassignment document; a reassignment nets to zero per item and warehouse and writes no cost entry and no journal.
* T13. The engine accepts a tracked item only from a caller that opted in and writes complete tracking in the same transaction.
* T11. Nothing reserves stock; no balance, document or screen of this phase shows a reserved quantity.

## 12. Test matrix (PostgreSQL on the isolated cluster unless marked; numbering local to this document)

| # | Test | Proves |
|---|---|---|
| CT1 | Receipt, issue and transfer of a lot item with complete details; balances per lot; examples 7.1 to 7.3 with their journals | T1, T3, T5 |
| CT2 | The same quantities without tracking on an untracked twin item give identical costs, layers and journals | T5 |
| CT3 | Missing, short or excess tracking details; details on an untracked item; unknown lot; lot quantity insufficient | T3 and the refusals of 5.2 |
| CT4 | Serial items: quantity one per serial; duplicate serial at receipt refused; issue of a serial not in that warehouse refused; a serial transferred is in exactly one warehouse | T2 |
| CT5 | Serial and lot uniqueness under concurrency: 16 parallel receipts of the same serial, and of the same new lot code, in the SAME and in DIFFERENT warehouses create exactly one and never deadlock; a receipt naming an existing lot with another expiry or manufacturing date is refused | T2, section 5.2 |
| CT6 | Lot balance under concurrency: parallel issues against the last quantity of a lot never go negative; the position lock serialises them | T2 |
| CT7 | Reversal: negating entries; inflow reversal refused once the lot was consumed or the serial moved; reversal of an issue restores the same lots and serials | T4 |
| CT8 | Deferred reconciliation from both sides: a raw SQL tracking insert that would make lots differ from the position fails at commit; a raw SQL COST entry for a tracked item with no tracking row fails at commit; two concurrent transactions cannot both pass on a stale position | T1 |
| CT9 | Raw SQL as the runtime role cannot update or delete tracking entries, cannot make a balance negative, cannot place a serial in two warehouses | T2, T4 |
| CT10 | Expiry: dispatch refused at the boundary CQ-3 decides; permitted exits only; expiry date required at first receipt; correction audited | T7 |
| CT11 | Expired and soon-expiring stock listed correctly as of a date; state derived, not stored | roadmap acceptance |
| CT12 | Tracking flag ON refused with stock or open documents, OFF refused with a tracking balance, allowed and audited otherwise; the same through the item update, the items import and raw SQL; a flag change and a concurrent posting of the item serialise | T10 |
| CT13 | Negative-stock exception: a tracked item cannot be made eligible; an item whose eligibility flag is set cannot be made tracked, with and without an open obligation; the existing rules still hold | T6 |
| CT14 | Stocktake and adjustment per lot and serial: variance per lot; surplus needs lot or serials; one adjustment and one posting | S4 |
| CT15 | Before and after catalog comparison of every existing guard function, trigger and constraint | T9 |
| CT16 | Period guard: a tracking entry for a posting in a Soft Closed, Final Closed or missing period cannot be inserted (with the D-220 guard present) | section 9 |
| CT17 | Replay plan containing a tracked item refused; back-dated movement of a tracked item refused | section 9 |
| CT18 | Revaluation of a tracked position (once enabled): no tracking row, reconciliation holds at commit | section 9 |
| CT19 | Trace: from a lot and from a serial to every document, and from a line to its lots and serials, across a transfer and a reversal | T8 |
| CT20 | Permissions: each new read and action refused without its permission and outside the warehouse scope; no existing preset gained a permission | section 10 |
| CT21 | A 150-step deterministic random scenario of receipts, issues, transfers, reversals and stocktakes on tracked and untracked items: T1 and `ReconcileAsync` hold after every step | T1 |
| CT22 | Idempotency: a retried or concurrent post of a tracked document creates one stock effect and one set of tracking entries | SRS 19 rule 2 |
| CT23 | Web and Tauri component tests of the shared screens (section 13); typecheck, production build and the Tauri build check | shared frontend rule |
| CT24 | No screen, API field or table of this phase carries a reserved quantity | T11 |
| CT25 | Stocktake of lots under CQ-14 (a): a zero-net count between two lots posts no cost entry and no journal and creates one reassignment; a count with a net shortage posts exactly the net; the gross alternative's cost effect is shown by a comparison test if (b) is chosen | T5, T12 |
| CT26 | Tracking reassignment: independent approval required; refused in a Soft Closed, Final Closed or missing period; cannot change the item and warehouse total; cannot be inserted by raw SQL without its document | T12 |
| CT27 | Engine opt-in: every engine caller other than the paths of this phase still refuses a tracked item; the opted-in paths refuse when tracking is incomplete | T13 |
| CT28 | S1 pre-check lists exactly the items that are tracked and hold stock; switching their flags off works and unfreezes the stock; S2 cannot be enabled while the list is non-empty | section 5.1 |
| CT29 | Serial items: fractional base quantity refused, also after alternate-unit conversion; serial flag refused for a base unit with decimals | section 5.1 |
| CT30 | Expiry: a document dated before the expiry date but posted after it is refused; a privileged expiry correction that makes stock valid again is audited and then permits dispatch | section 5.3 |
| CT31 | The two-lot case of section 7.1: after the issue neither receipt can be reversed, each with its own refusal code | sections 5.2 and 7.1 |
| CT32 | Returns: a customer return of a serial names the original serial, of a lot item the original lot; an expired return is refused under the CQ-6 default | section 9 |

## 13. Shared frontend (Web and Tauri; PROPOSAL)

One implementation in the shared React and TypeScript frontend on the UI-1 design system, delivered by both clients with identical behaviour; no platform-specific feature; all rules enforced by the server.

* **Item editor:** the existing "Tracking settings" tab shows the three flags and, when a change is refused, the reason from the server (stock exists, open documents). No new tab.
* **Stock document lines:** a tracked line opens a tracking dialog (lots with quantities, or a list of serials; paste and barcode entry as plain text input, no device integration). The line shows a tracking indicator (complete, incomplete) and the document cannot be submitted while any line is incomplete; the server refuses regardless. Switching tabs or closing the dialog never discards entered details silently (the tabs standard).
* **Stocktake:** counting per lot and serial in the same dialog pattern.
* **Balances:** the stock balance screen gains a per-lot and per-serial breakdown with expiry date and state; a trace view lists the documents of a lot or serial.
* **Arabic and English, RTL and LTR, keyboard accessible;** dates shown in the company's format; error and unsaved indicators at tab level; record identity and Save or Cancel visible from every tab.
* **No reservation column, badge or action anywhere.**

## 14. Owner questions (each with a proposed conservative default; none is decided here)

| # | Question | Proposed default | Alternatives and consequences |
|---|---|---|---|
| CQ-1 | Scope of serial uniqueness (D-219: "serial uniqueness") | Unique per item within the company, for all time: a serial that has left is never accepted as a NEW unit again (it can come back only through a return, CQ-13) | Company-wide across all items: stricter, refuses two different items that share a manufacturer's numbering. Unique only among units in stock: looser, loses the history of a unit |
| CQ-2 | Identity of a lot | One lot per item and lot code for the whole company; the same lot keeps its identity across warehouses | Lot per item and warehouse: the trace across a transfer needs a link between two lots. Lot codes unique across items: refuses suppliers' codes reused on different items |
| CQ-3 | When is stock "expired", and against which date? | From the expiry date itself: a movement is refused when the LATER of its effective date and its posting date is ON or after the expiry date (the strictest reading) | Only AFTER the expiry date (usable through that day): one day looser. Comparing with the effective date alone would let a back-dated document dispatch stock that is expired today |
| CQ-4 | What is a "dispatch", and how may expired stock leave? | Every outflow of expired stock is refused, including transfers between warehouses, EXCEPT a disposal issue marked as such, under a privileged permission and audited. (Changed in revision 2 on the reviewer's recommendation: a stocktake shortage is no longer an exit, because it would record a disposal as a count loss.) | A transfer to a designated quarantine warehouse, which the reviewer asks the owner to consider because expired goods must be physically segregated; a return to the supplier; a stocktake shortage (revision 1). Each is an additional exit to define |
| CQ-5 | Is there an override that lets expired stock be sold or delivered ("by default" in D-219)? | None in the first implementation | A privileged, independently approved, audited override per document line; needs the owner's explicit wish, since it moves expired goods to customers |
| CQ-6 | May goods be received that are already expired, or that expire soon? | A receipt of a lot or serial whose expiry date is not after the receipt date is refused; no minimum remaining shelf life is enforced | A configurable minimum remaining shelf life at receipt; or accepting expired receipts into stock (for returns), which then can leave only through CQ-4 |
| CQ-7 | How are lots and serials picked for an issue? | Explicitly by the user on every line; without complete details the line is refused; the screen may SUGGEST the earliest expiry first without choosing | Automatic earliest-expiry-first picking by the server: convenient, but a rule the SRS does not contain and one that decides which goods leave |
| CQ-8 | How is the expiry date of an item with ONLY the expiry flag carried? | Such an item is tracked by expiry batches: quantities grouped by expiry date, handled as lots whose code is the date | Require that an item with the expiry flag also has the lot or the serial flag (stricter master data; existing items with expiry only would have to be changed) |
| CQ-9 | How does stock of an UNTRACKED item become tracked? | It does not: a tracking flag can be switched on only at zero stock with nothing open; existing stock is issued and received again, or counted to zero, by ordinary documents (the separate case of items already tracked with stock is CQ-15) | A dedicated, approved "assign tracking" document that distributes the existing quantity over lots or serials without moving cost; new work with its own rules |
| CQ-10 | Confirm the costing consequence for lots AND for serials: the lot or unit issued and the cost charged can differ (example 7.1 and the five consequences listed there: serial units not costed at their own purchase cost, reports by lot only allocable, supplier returns at position cost, ageing by layer versus by lot, blocked reversals) | Confirmed as the team's derivation from D-017 and D-018: a lot or serial is a quantity-tracking dimension; no cost by lot or serial | Cost by lot or serial is specific identification, a third valuation method the SRS and D-017 do not list; for serial items of non-interchangeable goods it is what frameworks commonly expect; it would need a new owner decision and a different engine |
| CQ-11 | A loss of value of one particular lot or serial (damage, near expiry) | Not expressible at lot level: removal is a quantity outflow at the position's cost, and a value write-down follows the revaluation contract per layer or position, whichever lots are affected | Lot-level valuation, which again is specific identification |
| CQ-12 | Back-dated movements and replay for tracked items | Refused in the first implementation, as back-dated movements are today; a replay plan containing a tracked item is refused | A reviewed extension of the replay design that carries tracking through epochs |
| CQ-13 | Returns of tracked goods | A serial that has left re-enters only through a return linked to the original document that issued it (D-203), with its history kept; a return of a lot item names the lot of the original document; a return of goods already expired is refused at receipt (the CQ-6 default) | Free re-receipt of a known serial or into any lot: loses the link the owner asked for ("full source-document lineage"). Accepting expired returns into stock, from where they can leave only through CQ-4 |
| CQ-14 | A stocktake that shows quantity in the wrong lot (lot A short, lot B over, the item's total right) | Only the NET variance per item and warehouse goes through the cost path; the zero-net redistribution is a tracking-only reassignment document with its own independent approval, audit and period rule (section 7.5) | Gross posting as built for untracked lines: an issue and a receipt per lot, which creates a cost effect, a journal and a new FIFO layer from a relabelling |
| CQ-15 | Items that are already tracked AND hold stock today (frozen by the present refusals) | An S1 pre-check lists them; their flags may be switched OFF while they have no tracking balance, which makes the stock ordinary again; S2 is not enabled for the company while any remain | The "assign tracking" document of CQ-9 for exactly these items; or leaving them frozen, which is not proposed |
| CQ-16 | Which account takes the disposal of expired stock? | The issue counteraccount of the inventory configuration as built (3.7A): no new account, and expiry losses are then NOT distinguishable in the general ledger from other issues | A dedicated expiry-disposal counteraccount: an extension of the versioned inventory accounting configuration and its validation, with its own review |
| CQ-17 | A receipt names an existing lot with a different expiry or manufacturing date | Refused (`lot_attribute_mismatch`): one lot has one set of dates; a different date means a different lot code | Accept and keep the earlier expiry date for the whole lot (strict but silently changes what the document said); or accept and overwrite (loses the earlier date) |

Not a new question, but a dependency: **reservations** (register item RESERVATIONS-SCOPE). This phase cannot meet its roadmap acceptance criterion "reservations never exceed available stock" until the owner answers that item and a stock-allocation contract is approved.

## 15. The reviewer's recommendation per question (Agent FAC, review of revision 1)

| # | Reviewer's recommendation | This revision |
|---|---|---|
| CQ-1 | Accept (unique per item in the company, for all time) | unchanged |
| CQ-2 | Accept (one lot per item and code across warehouses) | unchanged |
| CQ-3 | Accept the strict boundary, with the date basis "later of effective and posting date" added | added to the default |
| CQ-4 | Accept the disposal issue as the exit; drop the stocktake shortage as an exit; consider allowing the quarantine transfer | default changed (shortage exit dropped); the quarantine transfer is shown as the alternative the reviewer asks the owner to consider |
| CQ-5 | Accept (no override) | unchanged |
| CQ-6 | Accept, but decide returns of expired goods knowingly | unchanged; the consequence is stated in CQ-13 |
| CQ-7 | Accept (explicit picking, suggestion only) | unchanged |
| CQ-8 | Accept (expiry batches as lots) | unchanged |
| CQ-9 | Accept zero-stock only, once a transition rule for existing tracked stock exists | unchanged; the transition rule is section 5.1 and CQ-15 |
| CQ-10 | Confirm for lot items; for serial items confirm only with the full consequences in front of the owner, since specific identification would be a new decision | the proposal is unchanged; the five consequences are stated in section 7.1 and in the question |
| CQ-11 | Accept | unchanged |
| CQ-12 | Accept the refusal for the first implementation | unchanged |
| CQ-13 | Accept, extended to lots | extended to lots and to expired returns |
| CQ-14 | Question required (stocktake netting and lot reassignment) | added; net posting with a tracking-only reassignment proposed, the gross alternative shown |
| CQ-15 | Question required (existing tracked stock) | added |
| CQ-16 | Question required (the account for expiry disposal) | added |
| CQ-17 | Question required (lot attribute mismatch) | added |

## 16. What happens next

1. Independent review of this proposal (inventory and database safety).
2. The owner answers CQ-1 to CQ-17, or accepts the defaults; RESERVATIONS-SCOPE is answered separately.
3. Slices S1 to S5 are planned one by one, each with its migration number allocated at merge order, its own independent full-diff review and its tests from section 12.
4. Until S2 merges, tracked items remain unmovable exactly as today; nothing in this document changes behaviour.
