# Phase 3.8D original-classification evidence contract (design proposal, revision 2)

Status: **proposal, not implemented.** Revision 2 answers the independent design review (4 High, 6 Medium, 3 Low; see `review-records/PR-48-classification-design.md`). It implements nothing of D-044 by itself; it defines the read-only evidence a later compensation plan must bind. No API/DI/caller, posting-barrier removal, journal rewrite or conversion execution. PR #48 stays Draft.

## Principle
The ORIGINAL classification of a posted effect is **the lines of the linked journal entries as stored** (account, base debit/credit, branch, currency, allocations). The accounting-version row and item-group mapping are never used to *derive* the original account: stock posting lines store no item group and the live group at posting time may have changed since. Version/group rows are a cross-check only; a mismatch is recorded `Ambiguous` (never silently resolved).

## Evidence (one read-only RepeatableRead snapshot, same boundary/budget pattern as `LoadSourceEvidenceAsync`)
1. **Postings** in the affected history: id, document id, cost operation, effective date, `TotalValue`. `TotalValue` is NOT the journal amount (issue postings include provisional amounts).
2. **Journal entries** linked by `JournalEntryId` (posting, reversal row `RV.JournalEntryId`, obligation, settlement, obligation reversal): id, status, `Revision`, `PostedAt`, period, branch, `Adjusts`/`Reverses` links; **lines** (account, base debit/credit, branch, currency, rate, rounding flag); **allocations** from `JournalAllocation` (cost centre, amount, base) - a table separate from `JournalLine`.
3. **Accounting version** only where the posting has one (conditional: zero-value receipts without settlement and transfers have none). Used as cross-check only.
4. **Obligation chain** (negative stock): `NegativeObligation`, `NegativeSettlement`, `NegativeObligationReversal` rows with their own `JournalEntryId` and their own stored snapshot of obligation (O), variance (V) and counter accounts; a settlement journal may be absent (nothing booked). Append-only reversals are netted: a reversed settlement is excluded exactly as `ForwardSettlementsAsync` does. The receipt journal, obligation journal and settlement journal are tied together by those rows.

## Facts about stock journals that the evidence must state, not infer
* Stock posting books **no cost-centre allocations** and no foreign currency, rate or rounding lines: its stored classification is account + branch. Allocations exist only if a user edited the draft before posting; the **posted** journal is the only truth. A non-posted draft is mutable, so its evidence is volatile and any later edit/post/cancel changes the fingerprint. A rounding line, a foreign-currency line or an unexpected allocation is flagged `Unexpected`, never attributed to the original classification.
* **Transfers have no journal and no version by design**: classification "none stored". This is NOT "no change needed". A replay that changes a transfer's cost effect between warehouses whose inventory accounts differ is refused (`classification_unavailable`) until a separate contract exists.

## State mapping (stock effect and journal are separate facts)
Defined separately; none is decided by the agent - the starred cases are **OPEN owner decisions** and until decided the evidence reports them and the plan REFUSES:
* effect live + journal Posted / NotRequired: normal.
* effect live + journal Draft/PendingApproval/Approved: `NotPosted` (not a posted effect; D-044); a later post changes the fingerprint.
* effect live + journal Cancelled/Reversed outside the stock reversal*: stock layers exist, ledger has none; replay still changes costs. OPEN.
* stock reversed + journal reversed: original and reversing journal both recorded (`Reverses` link) and netted.
* stock reversed + draft cancelled*: OPEN.
* `RemapRequired` (account now missing/inactive/non-postable/currency-restricted) is evidence, and a plan using it is refused until an owner-approved remap rule exists*. OPEN.

## Authorization and bounds
* Stock visibility (`StockDocsView`, warehouse scope) does not grant ledger visibility. Journal/lines/accounts/allocations additionally require the journal/ledger view permission at company and branch scope; failure is masked as not-found (no existence leak). Final live rechecks of stock AND ledger permissions run on success and caught failures. Exact permission codes are taken from `PermissionCatalog` during implementation and fixed by test.
* Transitive closure (settling receipt -> obligations -> issue postings -> journals -> settlements -> reversals) is bounded by per-table sentinel budgets (postings, journals, lines, allocations, obligations, settlements, accounts, version groups) charged to the shared budget, with deduplication of shared journals/accounts; batch by id arrays, never per-row queries; cancellation honored.
* Original evidence and **current-validity** evidence are separate sections. Current account state (active, postable, currency, revision) is bound into the fingerprint so an account change after the snapshot invalidates the plan.
* Fingerprint `stock-classification-evidence-v1`: SHA-256 over canonical, culture-independent fields: all of the above plus journal `Revision`, `PostedAt`, status, `Adjusts`/`Reverses`, allocations, obligation/settlement/reversal rows; independent of SQL row order.

## Out of scope (still mandatory)
Compensation content/dates, the transition matrix for partial coverage/multiple settlements/returns/reversals, basis/entitlement/legal policy, approval binding, atomic execution, revaluation, shared UI.

## Required tests before the implementation merges
Unchanged accounts; later account-map change; item moved to another group after posting (original journal account kept, version cross-check `Ambiguous`); inactive/currency-restricted account; draft vs posted journal and fingerprint change on post; no-journal posting; cancelled/reversed journal (each case above); transfer (`classification_unavailable`); settlement with and without journal; reversed settlement netted; user-added allocation; stock-only user denied ledger evidence without existence leak; closure and budget bounds, cancellation; read-only footprint; fingerprint stability/sensitivity; existing posting/reversal/negative-stock regression suites.
