# Phase 3.8D original-classification evidence contract (design proposal, revision 4)

Status: **proposal, not implemented.** Revision 2 answered the first independent design review (4 High, 6 Medium, 3 Low); revision 3 answered the re-review (2 High, 4 Medium, 3 Low); revision 4 answers the third review (0 Critical/High, 3 Medium, 3 Low), see `review-records/PR-48-classification-design.md`. It implements nothing of D-044 by itself; it defines the read-only evidence a later compensation plan must bind. No API/DI/caller, posting-barrier removal, journal rewrite or conversion execution. PR #48 stays Draft.

## Principle
The ORIGINAL classification of a posted effect is **the lines of the linked journal entries as stored** (account, base debit/credit, branch, currency, allocations). The accounting-version row and item-group mapping are never used to *derive* the original account: stock posting lines store no item group and the live group at posting time may have changed since. Version/group rows are a cross-check only; a mismatch is recorded `Ambiguous` (never silently resolved).

## Evidence (one read-only RepeatableRead snapshot, same boundary/budget pattern as `LoadSourceEvidenceAsync`)
1. **Postings** in the affected history: id, document id, cost operation, effective date, `TotalValue`. `TotalValue` is NOT the journal amount (issue postings include provisional amounts).
2. **Journal entries** linked by `JournalEntryId` (posting, reversal row `RV.JournalEntryId`, obligation, settlement, obligation reversal): id, status, `Revision`, `PostedAt`, period, branch, `Adjusts`/`Reverses` links; **lines** (account, base debit/credit, branch, currency, rate, rounding flag); **allocations** from `JournalAllocation` (cost centre, amount, base) - a table separate from `JournalLine`.
3. **Accounting version** only where the posting has one (conditional: zero-value receipts without settlement and transfers have none). Used as cross-check only.
4. **Obligation chain** (negative stock), precisely as stored:
   * `NegativeObligation` stores its own snapshot of O, V and counter accounts and its own `JournalEntryId`.
   * `NegativeSettlement` stores NO accounts. O and V come from its obligation; the receipt counter account G exists ONLY in the settlement journal lines (live configuration at settlement time). A settlement without a journal records no G; nothing is inferred.
   * Settlement reversals are NOT a separate table: they are `NegativeSettlement` rows with `ReversesSettlementId`, `ReversalId` and their own `JournalEntryId`. Netting (as `ForwardSettlementsAsync`): the reversed settlement AND the reversal row are both excluded from the NET chain, but the journals of both remain evidence.
   * `NegativeObligationReversal` has NO journal column (obligation, stock reversal, reversal operation, date, actor). The journal of an obligation reversal is reached through `StockPostingReversal.JournalEntryId` + `JournalAction`; the implementation must trace this path in code and fail closed (`classification_unavailable`) where it cannot.
   * Additional journal links to read: `StockPostingEvent.JournalEntryId`.
5. **Stock adjustments** (stocktake shortage/surplus, `StockAdjustmentReversal`, adjustment counter account of the accounting version) are posted effects with their own classification. They are **refused as `classification_unavailable`** in this slice, explicitly, not silently omitted.

## Facts about stock journals that the evidence must state, not infer
* Stock posting books **no cost-centre allocations** and no foreign currency, rate or rounding lines: its stored classification is account + branch. Allocations exist only if a user edited the draft before posting; the **posted** journal is the only truth. A non-posted draft is mutable, so its evidence is volatile and any later edit/post/cancel changes the fingerprint. A rounding line, a foreign-currency line or an unexpected allocation is flagged `Unexpected`, never attributed to the original classification.
* **Transfers have no journal and no version by design**: classification "none stored". This is NOT "no change needed". A replay that changes a transfer's cost effect between warehouses whose inventory accounts differ is refused (`classification_unavailable`) until a separate contract exists.

## Journal-id semantics (verified in code by the third review)
* `NegativeObligation.JournalEntryId` is the SAME journal as its issue posting (no separate obligation journal). The same journal id appears in several rows (posting, `StockPostingReversal`, obligation, settlement, negating settlement, `StockPostingEvent`): evidence deduplicates by journal id.
* `StockPostingReversal.JournalEntryId` is the REVERSING journal only when `JournalAction = 2`; with action 1 it is the ORIGINAL journal id again; with action 0 it is null (a reversed posting without journal is a normal, journal-less case). `StockPostingEvent` kind 3 repeats the same reference and is a cross-check only. The same rule applies to the negating `NegativeSettlement` row. `JournalEntry.Reverses` is the primary proof of netting; `JournalAction` is a hint.
* Reverse lookups are mandatory: journals with `Adjusts = ANY(ids)` and `Reverses = ANY(ids)` are read and bound in the fingerprint, so a later adjusting entry that reclassifies an account invalidates the plan. A posted stock journal that has an adjusting entry is refused (`open_decision`) in this slice: the effective classification after an adjusting entry is not defined.
* The existing posting precondition `issue_accounting_failed` (journal status 5/6 on an open obligation) already refuses that case in code; the OPEN case below concerns history after the fact and does not contradict it.

## State mapping (stock effect and journal are separate facts)
Defined separately; none is decided by the agent - the starred cases are **OPEN owner decisions** and until decided the evidence reports them and the plan REFUSES:
* effect live + journal Posted / NotRequired: normal.
* effect live + journal Draft/PendingApproval/Approved: `NotPosted` (not a posted effect; D-044); a later post changes the fingerprint.
* effect live + journal Cancelled/Reversed outside the stock reversal*: stock layers exist, ledger has none; replay still changes costs. OPEN.
* stock reversed + journal reversed: original and reversing journal both recorded (`Reverses` link) and netted.
* stock reversed + draft cancelled*: OPEN.
* stock reversed + posted journal whose reversing journal is still Draft/PendingApproval/Approved* (3.7C: a posted journal receives only a draft reversal; same for settlement journals): OPEN.
* stock reversed + posted journal whose draft reversal was later cancelled by hand*: not defined by any rule; REFUSED (`open_decision`) like the other open cases (a fifth case added by the third review).
* `RemapRequired` (account now missing/inactive/non-postable/currency-restricted) is evidence, and a plan using it is refused until an owner-approved remap rule exists*. OPEN.

## Authorization and bounds
* Stock visibility (`StockDocsView`, warehouse scope) does not grant ledger visibility. Journal/lines/accounts/allocations additionally require the journal/ledger view permission at company and branch scope; EVERY member of the closure (each posting document warehouse, each journal branch, each settling receipt / other-branch issue / allocation cost centre) must be visible; any invisible or missing member masks the WHOLE evidence as one indistinguishable `forbidden` (no partial result, no existence leak); failure is masked as not-found (no existence leak). Final live rechecks of stock AND ledger permissions run on success and caught failures. Required: `company.journal.view` (branch scope), `company.accounts.view` (company scope, current-validity evidence), `company.costcenters.view` when allocations exist, plus the stock view; ALL are required, otherwise the whole evidence is masked as not-found (no partial disclosure). Status codes 5/6 mean reversed/cancelled (a test pins Draft/Pending/Approved/Posted/Reversed/Cancelled codes against the enum, since NotPosted depends on them). Tables/columns are cited by `CompanySchema` class names, `StockPosting.AccountingVersionId` is nullable.
* Transitive closure (settling receipt -> obligations -> issue postings -> journals -> settlements -> reversals) is bounded by per-table sentinel budgets (postings, journals, lines, allocations, obligations, settlements, accounts, version groups) charged to the shared budget, with deduplication of shared journals/accounts; batch by id arrays, never per-row queries; cancellation honored.
* Original evidence and **current-validity** evidence are separate sections. Current account state (active, postable, currency, revision) is bound into the fingerprint so an account change after the snapshot invalidates the plan.
* Fingerprint `stock-classification-evidence-v1`: SHA-256 over canonical, culture-independent fields: all of the above plus journal `Revision`, `PostedAt`, status, `Adjusts`/`Reverses`, allocations, obligation/settlement/reversal rows; independent of SQL row order.

## First slice
Read-only service mirroring `LoadSourceEvidenceAsync` for issue and receipt postings in one RepeatableRead transaction; batch id loads (`ANY(@ids)`); refusals `classification_unavailable` (transfers, adjustments, untraceable reversal path) and `open_decision` (OPEN cases above); fingerprint v1. No API/DI/caller, no posting change.

## Out of scope (still mandatory)
Compensation content/dates, the transition matrix for partial coverage/multiple settlements/returns/reversals, basis/entitlement/legal policy, approval binding, atomic execution, revaluation, shared UI.

## Required tests before the implementation merges
Unchanged accounts; later account-map change; item moved to another group after posting (original journal account kept, version cross-check `Ambiguous`); inactive/currency-restricted account; draft vs posted journal and fingerprint change on post; no-journal posting; cancelled/reversed journal (each case above); transfer (`classification_unavailable`); settlement with and without journal; reversed settlement netted; user-added allocation; stock-only user denied ledger evidence without existence leak; closure and budget bounds, cancellation; read-only footprint; fingerprint stability/sensitivity; existing posting/reversal/negative-stock regression suites.
