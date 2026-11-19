# Phase 3.8D original-classification evidence contract (design proposal)

Status: **proposal, not implemented, not independently reviewed.** It implements nothing of D-044 by itself; it defines the read-only evidence that a later compensation plan must bind. No API/DI/caller, posting-barrier removal, journal rewrite or conversion execution is introduced. PR #48 stays Draft.

## Purpose
D-044 (owner-approved) restores the ORIGINAL account/dimension classification of a posted effect when history is replayed. Before any compensation content can be planned, the plan needs an immutable, fingerprinted record of what each original posting actually booked. Current mappings must never be substituted silently.

## Evidence per original stock posting (one read-only RepeatableRead snapshot, same boundary and budget as `LoadSourceEvidenceAsync`)
For each posting whose cost operation is in the affected history (and each settlement/obligation posting linked to it):
1. Posting identity: posting id, document id/revision at posting, cost operation id, effective date, total value.
2. Accounting version: `AccountingVersionId` and the immutable inventory-accounting version row (version no, content hash, resolved inventory/receipt-counter/issue-counter/adjustment/obligation/variance accounts). The group-mapping rows used are read from THAT version, not the latest.
3. Journal: `JournalEntryId`, entry status, period, branch, base currency; lines (account, base debit/credit, branch, currency, rounding flag). Cost-center/dimension data is included only where the journal schema stores it; if a dimension is not stored for a line it is recorded as "none", never inferred.
4. Classification state per posting, derived from facts only: `Posted` (journal posted), `NotRequired` (no journal), `Pending` (draft/approval: NOT a posted effect, D-044 / proposal section 1), `Failed`/`Reversed` (excluded from restoration, refused for compensation until explicitly handled).

## Fail-closed rules
* Missing version row, journal, or account that is inactive/non-postable/currency-restricted now: evidence is recorded as `RemapRequired`; no plan is complete until explicit remapping is approved. Never auto-redirect.
* Posting in a Final Closed period: barrier (existing fiscal evidence); compensation date must be separately permitted.
* Evidence is bounded by the existing global row/command/deadline budget; no per-posting query loops (batch by id arrays, sentinel row budget); cancellation honored.
* Authorization: counted bootstrap permission is not authority. Final live AnyView + journal/cost permission rechecks stay mandatory, as in source evidence.
* Fingerprint: SHA-256 over canonical, culture-independent used evidence (contract id `stock-classification-evidence-v1`), independent of SQL row order. Changed version/journal status/line content changes the fingerprint.

## Out of scope (still mandatory, tracked separately)
Compensation content and dates, transition matrix for partial coverage/multiple settlements/returns/reversals, basis/entitlement/legal policy, approval binding, atomic execution, revaluation, shared UI.

## Required evidence before merge of the implementation
Independent full-diff DESIGN review of this document first; then PostgreSQL tests for: unchanged accounts, later account-map change (original version still read), inactive/currency-restricted account (`RemapRequired`), pending journal not treated as posted, no-journal posting, cancelled/reversed journal, settlement posting, Final Closed period, read-only footprint (no locks/writes), budget/cancellation, fingerprint stability and sensitivity; existing posting/reversal/negative-stock regression suites.
