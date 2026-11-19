# Phase 3.8D transition matrix and consolidated owner decisions (proposal)

Status: **PROPOSAL for owner decision. Nothing in this document is approved policy unless it is in section 1.** No accounting behavior that depends on a section 5 or 6 decision is implemented. Existing fail-closed behavior stays active until the owner decides.

Legend: **[A]** approved (owner decision or approved SRS), **[E]** existing implemented behavior (cited), **[P]** proposed here, **[D]** open owner decision.

Letters used below: **I** inventory account of the item group, **K** issue counter account (cost of goods), **G** receipt counter account, **O** negative-stock obligation account, **V** settlement variance account. Dr/Cr in base currency; every example assumes accounts unchanged, all journals posted and all relevant periods Open unless stated.

## 1. Approved rules (sources)

| # | Rule | Source |
|---|---|---|
| A1 | Back-dated movements are allowed within period policy and permissions; require impact analysis, preview, replay of later FIFO/average movements including chained transfers, and **separate, approved inventory and accounting adjustment documents** before posting. | SRS section 9 |
| A2 | A posted journal is never changed or deleted; correction is by reversal/adjustment documents with approval and audit. Final Closed periods accept no back-posting. | SRS 9, 19.3, 19.5, 21.2 |
| A3 | Compensation restores the ORIGINAL accounts and dimensions through separately approved, traceable entries; original classifications are preserved wherever valid and legally permitted; original journals and cost operations are never modified; Final Closed, approvals, separation of duties and audit are respected; standard/jurisdiction restrictions are explicit configuration or release-validation requirements. | **D-044** |
| A4 | Method conversion is prospective, preserves quantity and carrying value (FIFO totals become the average basis; an average position becomes one auditable opening FIFO layer); cross-conversion return/reversal/transfer contracts must be defined, reviewed and regression-tested BEFORE activation; revaluation needs its own contract. | **D-045** |
| A5 | Negative stock is moving-average only, with a provisional cost and later settlement; material pending differences block period close. | SRS 9 |
| A6 | One business event creates at most one financial/stock effect (idempotency); full traceability of cost and approval source. | SRS 19.2, 19.8 |
| E1 | While an obligation of a position is open every other inflow into it is refused (`negative_obligation_open`), a receipt dated before it is refused (`negative_obligation_older`), and back-dated movements with obligations are refused until 3.8D. | `PHASE-3.8B-3.md` lines 24, 32-40, 113 |
| E2 | A stock reversal restores exactly the original consumed layers/amounts; its journal is a draft reversal of a posted original or a cancellation of an unposted one (JournalAction 2 / 1 / 0). | `StockPosting.Reversal.cs` |
| E3 | Accounting state is derived: pending, not required, posted, **failed** (journal cancelled/reversed outside the stock reversal; blocks period close), reversed. | `StockPosting.cs` DeriveState |
| E4 | Transfers book no journal (item inventory account is by item group, identical on both legs). | `StockPosting.cs` |
| E5 | Stock journals are base-currency, account + branch only, one aggregated line per account, no cost-centre allocation, no rounding line. | `StockPosting.cs` PlanJournal |

## 2. Method (proposed) - how the matrix is evaluated  [P]

For the affected closure (postings, obligations, settlements, reversals, read by the classification-evidence service):

1. **E_orig** = net effect per bucket (account, branch, currency, period) of the journals that ACTUALLY exist and are posted, including reversing and settlement journals (stored lines are the classification, contract rev. 4).
2. **E_hyp** = net effect the SAME posting functions (`PlanJournal`, settlement lines) would have produced from the replayed history, with every account **pinned to the original accounts** found in E_orig/evidence, never re-resolved from the current mapping.
3. The new back-dated event's own journal is its normal posting and is excluded from both sides.
4. **Compensation = E_hyp - E_orig** per bucket. Mandatory checks: the compensation balances (sum = 0); every account is valid (section 5, TM-D4); every date is permitted (TM-D8); the plan's evidence fingerprint is unchanged at execution; original journals/operations are untouched; the compensation is created together with the stock re-cost atomically as draft journals following the ordinary approval workflow (SRS "approved before posting").
5. Any cell of the matrix that is not **Accepted** below stops the plan with a stable code; nothing is partially applied.

## 3. Transition matrix

Rows: original effect. Columns: how replay changes it. "Compensation" is the bucket difference of section 2.

| # | Original effect | Replay change | Compensation | Status |
|---|---|---|---|---|
| M1 | Ordinary issue (K Dr / I Cr at engine cost), FIFO or average | Cost changes by delta | delta > 0: Dr K, Cr I; delta < 0 mirrored. Receipts keep their entered amounts. | Accepted in principle [P]; depends on TM-D6, TM-D8 |
| M2 | Transfer (no journal) | Carried cost changes | No line for the transfer itself (same account both legs); downstream issues follow M1. | **TM-D7** (currently refused) |
| M3 | Issue with obligation, later fully settled | Back-dated stock covers the issue | Restore K/V/I/O/G per bucket diff (example E3). | Accepted in principle [P]; TM-D1..D5, D8 apply |
| M4 | Same, partial coverage | Part of the quantity becomes covered | Bucket diff, includes new provisional and settlement amounts (example E4). | Accepted in principle [P] |
| M5 | Several settlements of one obligation (oldest-first matching) | Matching re-derived | Per-settlement hypothetical journals, then bucket diff. | Accepted in principle [P] |
| M6 | Ordinary issue later fully reversed | Cost changes | Both sides net to zero when the reversal restores the replayed amounts; no lines (E5). | Accepted in principle [P]; requires both journals posted (TM-D3) |
| M7 | Reversed issue with reversed obligation | Any | Net zero on both sides (E2/E6). | Accepted in principle [P] |
| M8 | Back-dated movement with an **open** obligation | Any | Not in scope: refused (E1). | [E] keep refusal (TM-D10) |
| M9 | Journal cancelled/reversed outside the stock reversal while the effect lives | Any | None defined | **TM-D1** (refused) |
| M10 | Stock reversed, draft journal cancelled | Any | None defined | **TM-D2** (refused) |
| M11 | Stock reversed, posted original, reversing journal not yet posted | Any | None defined | **TM-D3** (refused) |
| M12 | Original account now missing/inactive/group/foreign-currency-limited | Any | None defined | **TM-D4** (refused) |
| M13 | Stock reversed, reversing journal later cancelled by hand | Any | None defined | **TM-D5** (refused) |
| M14 | Postings whose item group at posting time cannot be proven | Any | None defined | **TM-D6** (refused only when ambiguous) |
| M15 | Stocktake/adjustment origins | Any | Adjustment counter account bucket | Out of scope here (refused `classification_unavailable`); no decision requested now |
| M16 | Method change across history | Any | Part B | C-D1..C-D4 |

## 4. Worked examples (exact figures)

**E1 - average, ordinary issue (M1).** Receipt A 10 units for 100 (03-10), issue 4 units on 03-12 costs 40: Dr K 40 / Cr I 40. A back-dated receipt B of 5 units for 80 dated 03-11 makes the average on 03-12 (100+80)/15 = 12, the issue costs 48. B posts normally (Dr I 80 / Cr G 80). Compensation: Dr K 8 / Cr I 8. Inventory ledger 100 - 40 + 80 - 8 = 132 = 11 units x 12.

**E2 - FIFO (M1).** Receipt A 10 for 100 (03-10), issue 4 on 03-12 consumes A: 40. Back-dated receipt B 5 for 100 (20 each) on 03-09 is older, so the issue consumes B: 80. B posts Dr I 100 / Cr G 100. Compensation: Dr K 40 / Cr I 40. Inventory 100 - 40 + 100 - 40 = 120 = layers A 10@100 + B 1@20.

**E3 - settled obligation, full coverage (M3; the PHASE-3.8D-ACCOUNTING-PROPOSAL example).** Original: issue 7 uncovered at provisional 35: Dr K 35 / Cr O 35; receipt 7 for 28 settles: Dr O 35 / Cr G 28, Cr V 7. Back-dated receipt of 7 for 42 (own journal Dr I 42 / Cr G 42) now covers the issue: hypothetical issue costs 42 (Dr K 42 / Cr I 42), no obligation, the later receipt enters inventory (Dr I 28 / Cr G 28). Bucket diff (hypothetical - original): K +7, V +7 (undo the credit 7), I -14, O 0, G 0. Compensation: **Dr K 7, Dr V 7 / Cr I 14**. Final: inventory 28, K 42, V 0, O 0.

**E4 - settled obligation, partial coverage (M4).** Start: 3 units on hand for 15 (average 5). Original: issue 10 covers 3 (15) and leaves 7 at provisional 35: Dr K 50 / Cr I 15, Cr O 35; receipt 7 for 28 settles all: Dr O 35 / Cr G 28, Cr V 7. Back-dated receipt of 4 for 20 before the issue (own journal Dr I 20 / Cr G 20): on hand 7 for 35 (average 5); the issue covers 7 (35) and leaves 3 at provisional 15: Dr K 50 / Cr I 35, Cr O 15. The later receipt settles 3 units (actual 12): Dr O 15 / Cr G 12, Cr V 3; the other 4 units enter at 16: Dr I 16 / Cr G 16. Bucket diff: K 0, I -19-(-15) = -4, V +4, O 0, G 0. Compensation: **Dr V 4 / Cr I 4**. Final inventory 20 - 4 = 16 = 4 units x 4.

**E5 - ordinary issue later reversed (M6).** Issue costs 40 (Dr K 40 / Cr I 40) and is reversed (Dr I 40 / Cr K 40, reversing journal posted): net 0. After a back-dated receipt the replay costs the issue at 48 and its reversal restores exactly 48: net 0. Compensation: none. The cost ledger is re-costed; the general ledger needs no entry.

**E6 - transfer (M2, subject to TM-D7).** W1 average 10 units for 100; transfer 4 to W2 carries 40 (no journal); a later issue of 2 from W2 costs 20 (Dr K 20 / Cr I 20). Back-dated receipt into W1 of 5 for 80: W1 average 12, the transfer carries 48, W2 average 12, the issue costs 24. Compensation: Dr K 4 / Cr I 4 (the transfer itself needs no line because the inventory account is the same on both legs and both warehouses are in one branch).

**E7 - why original accounts matter (D-044).** If E3's difference were booked only as "Dr K 14 / Cr I 14" the ledger would end with K 49 and V credit 7: net expense is still 42 but the classification differs from restoration. The matrix therefore uses bucket difference, not a single net difference.

## 5. Decisions required from the owner

Each decision lists: scenario, treatments, impact, recommendation and why, and the existing fail-closed behavior. **Unless the owner decides otherwise, every decision stays on the fail-closed behavior and its matrix cells stay refused.**

### TM-D1 - live effect, journal cancelled/reversed outside the stock reversal (M9)
* **Scenario:** an issue is posted and moves stock; an accountant cancels its draft journal (or reverses it) without reversing the stock document. State E3 "failed"; the ledger has no cost for goods that physically left.
* **Treatments:** (a) refuse replay of that chain until the stock document is reversed through the ordinary reversal or the accounting side is repaired; (b) treat the original ledger effect as zero and let the compensation also book the whole hypothetical effect (a silent catch-up); (c) ignore the posting.
* **Impact:** (b) books costs the accountant deliberately removed, bypassing their decision; (c) leaves cost ledger and ledger permanently different.
* **Recommended: (a).** The state is already declared as needing attention and blocking close (E3); replay should not be a way around it. Technical: no new rule, the evidence already refuses (`open_decision`).
* **Fail-closed now:** `open_decision`.

### TM-D2 - stock reversed, draft journal cancelled (M10)
* **Scenario:** a posting whose journal was never posted is reversed; the reversal cancels the draft (JournalAction 1). Ledger effect: nothing on both sides.
* **Treatments:** (a) refuse always; (b) accept as ledger-neutral when the replay proves that the original operation and its reversal net to zero stock and zero value in the hypothetical history; (c) accept without proof.
* **Impact:** (a) blocks every back-dated entry before any reversed-draft document, forever; (b) allows it with no ledger lines (as E5); (c) risks hiding a mismatch.
* **Recommended: (b).** It is correct by construction (original effect 0, hypothetical net 0), keeps old history from blocking operations, and the proof is mechanical.
* **Fail-closed now:** `open_decision`.

### TM-D3 - stock reversed, original posted, reversing journal not yet posted (M11)
* **Scenario:** the reversing journal is a draft/pending/approved entry waiting for approval.
* **Treatments:** (a) refuse until it is posted; (b) treat it as booked; (c) compensate against the current ledger.
* **Impact:** (b)/(c) depend on a mutable draft that may be edited or cancelled, making the plan fingerprint volatile and possibly wrong; the proposal rule already says pending is not a posted effect.
* **Recommended: (a).** The cost is only an operational wait: post the reversing journal, then re-run.
* **Fail-closed now:** `open_decision`.

### TM-D4 - original account no longer valid (M12)
* **Scenario:** an original account is now inactive, a group account, or limited to a foreign currency; the compensation must book to it.
* **Treatments:** (a) refuse; (b) book to the invalid account anyway; (c) explicit per-account remap inside the proposal: only to an active postable account of the same type and nature and base-currency compatible, approved by someone other than the author, audited, bound into the fingerprint and visible in the approval content.
* **Impact:** (b) violates normal chart rules; (a) can block legitimate corrections after a chart cleanup; (c) restores classification as nearly as possible with a visible exception (D-044 allows "where valid and legally permitted").
* **Recommended: (c) with (a) as default when no remap is approved.** Needs your approval of the "same type and nature" restriction.
* **Fail-closed now:** `open_decision`.

### TM-D5 - stock reversed, reversing journal later cancelled by hand (M13)
* **Scenario:** a posted original was reversed with a draft reversing journal that an accountant then cancelled: stock is reversed, the ledger still carries the original effect.
* **Treatments:** (a) refuse until repaired (new reversal journal through a separate repair action); (b) treat the ledger as authoritative; (c) ignore.
* **Impact:** same family as TM-D1: a stock/ledger discrepancy the system already treats as an exception.
* **Recommended: (a)** and, as a separate small slice after approval, a repair action that re-creates the reversing journal. Do not decide accounting silently inside replay.
* **Fail-closed now:** `open_decision` (this case was found by the second design review).

### TM-D6 - item-group provenance (M14)
* **Scenario:** journals aggregate one line per account; posting lines store no item group; the group at posting time comes from live data and the item may have moved groups since. For a posting with items in different groups the per-item share of each account cannot be reconstructed.
* **Treatments:** (a) refuse only when ambiguity exists (single resolvable account per posting is accepted) and always for postings where the item's current group account differs from the evidence; (b) additionally persist, from now on, the resolved inventory account per posting line (company schema migration); (c) infer.
* **Impact:** (b) needs a schema migration (**needs your approval**) and makes new postings fully attributable; (a) keeps legacy data safe but refuses ambiguous legacy chains; (c) can silently misclassify.
* **Recommended: (a) now, plus (b) in a separate approved migration slice.**
* **Fail-closed now:** evidence cross-check returns `classification_ambiguous` for accounts the version could not produce; attribution of mixed-group postings is not yet implemented and refuses.

### TM-D7 - transfers (M2)
* **Scenario:** replay changes the cost a transfer carries; transfers book no journal (E4).
* **Treatments:** (a) keep refusing; (b) accept when source and target warehouses are in the same branch and the item's inventory account is identical on both legs (downstream issues follow M1); (c) accept always.
* **Impact:** (c) ignores branch dimension: a cross-branch transfer moves value between branches with no entry (an existing gap of 3.7, not introduced here).
* **Recommended: (b).** Transfers are common; (b) is the maximal safe subset.
* **Fail-closed now:** `classification_unavailable` for every transfer.

### TM-D8 - date and period of compensation entries
* **Scenario:** compensation corrects effects of operations dated in earlier periods, possibly Soft Closed.
* **Treatments:** (a) each entry dated at the affected original operation (restates the correct period; allowed in Open, and in Soft Closed only with the existing soft-close permission/warning; Final Closed refuses the whole plan); (b) all entries on one explicit date chosen at approval (usually today): earlier periods stay untouched, reclassification appears in the current period; (c) automatic choice.
* **Impact:** (a) gives accurate period reporting and may change figures of a Soft Closed period; (b) never touches closed periods but period results stay as originally reported and the correction is visible only later. D-044 and SRS say "respect Final Closed" but leave the date open.
* **Recommended: (a) when the period is Open; in Soft Closed use (a) only with the existing permission, otherwise refuse the plan (never silently fall back to (b)).** Explicit date selection (b) as an optional approved mode only if you want it.
* **Fail-closed now:** the plan cannot be created.

### TM-D9 - approval and separation of duties for compensation
* **Scenario:** who prepares, approves and posts.
* **Proposal [P]:** the plan (stock re-cost plus compensation journals) is created by one user, approved by a different user holding the new approval permission, then materialized atomically; the compensation journals are ordinary Draft journals following the existing journal approval policy and posting by a third permission as today. The approver sees the exact entries and the fingerprint.
* **Decision requested:** confirm that no new roles beyond the existing approval policy are required (recommended), or name the extra separation you want.
* **Fail-closed now:** no execution exists.

### TM-D10 - open obligations
* **Scenario:** a back-dated event for a position with an open obligation (M8).
* **Treatments:** (a) keep refusing (current rule E1); (b) allow with a defined effect on provisional cost and open quantity.
* **Recommended: (a).** The provisional model is defined only for forward flow; allowing back-dating would need a new accounting contract for re-provisioning (O and K amounts change while no settlement exists) and is outside this phase.
* **Fail-closed now:** `negative_obligation_open`.

## 6. Part B - method conversion dependencies (D-045 prerequisites)

D-045 requires these contracts BEFORE execution. All are **proposals**; conversion stays disabled (`valuation_method_changed` guard).

* **C-D1 - return/reversal after conversion of an operation made before it.** Treatments: (a) the reversal restores the ORIGINAL amounts and re-enters as new basis (average: added to quantity/value; FIFO: one new layer at the original amount), never resurrecting old layers; (b) re-cost at the new method's cost. **Recommended (a):** the reversing journal mirrors the original journal exactly, carrying value is preserved.
* **C-D2 - transfers in flight across a conversion.** Legs on either side of the conversion date: **Recommended:** refuse conversion while any posted-but-unreconciled transfer chain spans the date; conversion is allowed only at a date where every transfer chain touching the position is complete.
* **C-D3 - back-dated movements before a conversion date.** Changing quantity/value carried into the conversion changes its basis. **Recommended:** a conversion barrier: back-dated movements before the effective date of a conversion are refused.
* **C-D4 - revaluation.** Not approved; any value change on conversion requires its own counter-account policy. **Recommended:** exclude from this phase.

## 7. Technical dependencies that need no owner decision

* The negative-stock replay (obligations, settlements, reversal chains) and the E_hyp journal builder: internal, read-only, after the decisions above are known.
* Compensation proposal storage as non-postable proposals (never independently postable drafts) and atomic execution: after TM-D1..D9.
* Schema work: only TM-D6(b) and the proposal tables (company migration) need approval before any migration is written.
* Tests required before activation: every matrix cell above, per-bucket balance, period barrier, pending/volatile fingerprints, double-execution/idempotency, concurrency (close during execution), authorization masking.

## 8. Decision request summary

| ID | Question | Recommended |
|---|---|---|
| TM-D1 | Journal cancelled/reversed outside stock reversal, effect live | Keep refusing; repair first |
| TM-D2 | Reversed with cancelled draft | Accept as ledger-neutral with proof |
| TM-D3 | Reversing journal not yet posted | Keep refusing until posted |
| TM-D4 | Original account no longer valid | Explicit approved remap (same type/nature), default refuse |
| TM-D5 | Reversing journal cancelled by hand | Keep refusing; separate repair action |
| TM-D6 | Item-group provenance | Refuse ambiguity now; approve a migration to store the account per line |
| TM-D7 | Transfers | Accept same-branch same-account transfers |
| TM-D8 | Compensation date/period | Date at the affected operation; Final Closed refuses; Soft Closed only with existing permission |
| TM-D9 | Approval/separation of duties | Existing approval policy, different approver |
| TM-D10 | Open obligations | Keep refusing |
| C-D1..C-D4 | Conversion contracts | As listed in section 6 |
