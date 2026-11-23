# Phase 3.8D transition matrix and consolidated owner decisions (proposal, revision 2)

Status: **PROPOSAL for owner decision. Nothing here is approved policy unless it is in section 1.** No behavior that depends on a decision in sections 5 or 6 is implemented; the existing fail-closed behavior stays active until the owner decides. Revision 2 answers the independent consolidated review (2 High, 8 Medium, several Low; `review-records/PR-48-transition-matrix-design.md`).

Legend: **[A]** approved (owner decision or approved SRS), **[E]** existing implemented behavior (cited), **[P]** proposed here, **[D]** open owner decision.

Letters: **I** inventory account of the item group, **K** issue counter (cost of goods), **G** receipt counter, **O** negative-stock obligation, **V** settlement variance. Dr/Cr in base currency. Examples assume accounts unchanged, journals posted, periods Open, and **everything within one fiscal period unless an example says otherwise** (section 4 shows the period split).

## 1. Approved rules (sources)

| # | Rule | Source |
|---|---|---|
| A1 | Back-dated movements are allowed within period policy and permissions, with impact analysis, preview, replay of later FIFO/average movements including chained transfers, and **separate approved inventory and accounting adjustment documents** before posting. | SRS section 9 |
| A2 | A posted journal is never changed or deleted; correction is by reversal/adjustment documents with approval and audit. Final Closed periods accept no back-posting. | SRS 9, 19.3, 19.5, 21.2 |
| A3 | Compensation restores the ORIGINAL accounts and dimensions through separately approved, traceable entries; original classifications are preserved wherever valid and legally permitted; original journals and **historical cost operations are never modified**; Final Closed, approvals, separation of duties and audit are respected; standard/jurisdiction restrictions are explicit configuration or release-validation requirements. | **D-044** |
| A4 | Method conversion is prospective, preserves quantity and carrying value; cross-conversion return/reversal/transfer contracts must be defined, reviewed and regression-tested BEFORE activation; revaluation needs its own contract. | **D-045** |
| A5 | Negative stock is moving-average only with provisional cost and later settlement; material pending differences block period close. | SRS 9 |
| A6 | One business event creates at most one financial/stock effect (idempotency); full traceability of cost and approval source. | SRS 19.2, 19.8 |
| E1 | The engine refuses EVERY back-dated entry (`backdated_not_supported`, `InventoryCostingService.cs:248`) until 3.8D. With obligations: a receipt dated before an open obligation is `negative_obligation_older`, one dated after it but before the last movement is `backdated_not_supported`, every other inflow into a position with an open obligation is `negative_obligation_open`. Consequence: back-dating a late receipt before a still-open negative issue is refused; the workaround is an ordinary current-dated receipt. | `PHASE-3.8B-3.md` 24, 32-40, 113; `StockPosting.Negative.cs:155-157` |
| E2 | A stock reversal restores exactly the original consumed layers/amounts. `JournalAction`: 0 no journal existed; 1 the journal was cancelled **or was already Cancelled/Reversed outside the stock reversal**; 2 a draft reversing entry was created for a posted original. | `StockPosting.Reversal.cs:77-106` |
| E3 | Accounting state is derived: pending, not required, posted, **failed** (journal cancelled/reversed outside the stock reversal; blocks close), reversed. | `StockPosting.cs` DeriveState |
| E4 | Transfers book no journal. Cross-branch transfers are refused (`transfer_cross_branch_not_supported`, `StockDocumentService.cs:174`), and a transfer moves one item, whose inventory account is by item group: identical on both legs. | `StockPosting.cs`, `StockDocumentService.cs` |
| E5 | Stock journals are base-currency, account + branch only, one aggregated line per account, no cost-centre allocation, no rounding line. | `StockPosting.cs` PlanJournal |
| E6 | The period-close probe compares the cost ledger with the general ledger cumulatively to each period end, checks the O balance at each period end and failed stock journals. | `InventoryValuationProbe.cs`, `NegativeStockReconciliation` |

## 2. Method (proposed) [P] - decision TM-D11

For the affected closure read by the classification-evidence service:

1. **E_orig** = net effect per bucket **(account, branch, currency, fiscal period)** of the journals that exist and are **Posted**, including reversing and settlement journals.
2. **E_hyp** = net effect the same posting functions (`PlanJournal`, settlement lines) would produce from the replayed history.
3. **Account pinning.** Accounts are taken from the original journal lines. Where a replayed effect needs an account that no original journal contains (E3: the original issue had no inventory line), the account comes from the **accounting version recorded on that posting** (immutable) with the item's group (subject to TM-D6), **never from the live mapping**. If the version cannot determine the account, the plan is refused. The same applies to O and V of an obligation that exists only in the replay. Branch is pinned the same way (the branch of the original journal line).
4. The new back-dated event's own journal is its normal posting, excluded from both sides.
5. **Compensation = E_hyp - E_orig per bucket, per period.** Checks: it balances; every account valid (TM-D4); every date permitted (TM-D8); fingerprint unchanged at execution; original journals and cost operations untouched; the cost-ledger re-cost is **append-only** (new adjustment operations/entries linked to the plan, never an edit of history); journals are Draft journals following the ordinary approval workflow, created atomically with the re-cost.
6. **Pending state.** A live effect or reversal whose journal is not Posted is NOT part of E_orig and the plan is refused (`unposted_effect`) until it is posted (otherwise the drafts would double-book: example: issue 40 with a draft journal, E_orig 0, E_hyp 48, compensation 48, and the later posting of the draft leaves 88). This extends TM-D3.
7. **Repeat plans / idempotency** (TM-D12): every compensation journal carries a source key derived from the plan (plan id + bucket); a plan whose compensation journals are not all posted blocks a second plan on the same closure (`plan_pending`); posted compensation is included in the next E_orig through the plan-to-journal link table. Until that link exists, a closure with any executed plan is refused.
8. The plan takes the same company advisory lock and position locks as posting/reversal, in the same order; the evidence fingerprint is re-verified under those locks.
9. Existing license/Trial limits and approval policies apply to compensation documents unchanged; nothing is bypassed. A stock journal carrying user-added cost-centre allocations is refused (unexpected line).

Any cell not "Candidate" below stops the plan with a stable code; nothing is partially applied.

## 3. Transition matrix

"Candidate [P]" means: proposed to be supported once the listed decisions are approved. It is NOT approved.

| # | Original effect | Replay change | Compensation | Status |
|---|---|---|---|---|
| M1 | Ordinary issue (K Dr / I Cr), FIFO or average | Cost changes | Dr K / Cr I by delta, per period | Candidate [P]; TM-D6, D8, D11 |
| M2 | Transfer (no journal) | Carried cost changes | No line for the transfer; downstream issues follow M1 | **TM-D7** (refused today) |
| M3 | Issue with obligation, later fully settled | Back-dated stock covers the issue | Bucket diff (E3) | Candidate [P] after **TM-D13** (the obligation disappears) |
| M4 | Same, partial coverage | Obligation shrinks | Bucket diff (E4) | Candidate [P] after **TM-D13** |
| M5 | Several settlements (oldest first) | Matching re-derived | Per-settlement hypothetical journals, bucket diff | Candidate [P] after **TM-D13** |
| M6 | Ordinary issue later fully reversed | Cost changes | Net zero ONLY when issue and reversal are in the same period (E5); otherwise one entry per period | Candidate [P]; TM-D3, D8 |
| M7 | Reversed issue with reversed obligation | Any | Same as M6 | Candidate [P]; TM-D13 |
| M8 | Back-dated movement with an **open** obligation | Any | None | [E] refused (TM-D10) |
| M9 | Journal cancelled/reversed outside stock reversal, effect live | Any | None | **TM-D1** (refused) |
| M10 | Stock reversed, draft journal cancelled by that reversal | Any | None (neutral) | **TM-D2** (refused) |
| M11 | Any journal in the closure not Posted (live effect or reversing) | Any | None | **TM-D3** (refused) |
| M12 | Original account no longer valid | Any | None | **TM-D4** (refused) |
| M13 | Reversing journal later cancelled by hand | Any | None | **TM-D5** (refused) |
| M14 | Item group at posting time not provable | Any | None | **TM-D6** (refused when ambiguous) |
| M15 | Back-dated outflow that makes a LATER issue uncovered (replay would create an obligation) | Any | None | **TM-D13** (refused) |
| M16 | Stocktake/adjustment in the later history | Any | Adjustment counter account | Refused `classification_unavailable`. **Blast radius: any back-dated entry before a stocktake adjustment is refused**; likewise future sales/purchase cost postings once they exist. Owner decides priority of adjustment support (TM-D14). |
| M17 | Previous executed plan on the closure | Any | None | **TM-D12** (refused) |
| M18 | Method change across history | Any | Part B | C-D1..C-D4 |

## 4. Worked examples (exact figures)

**E1 - average, ordinary issue (M1).** Receipt A 10 units for 100 (03-10), issue 4 units on 03-12 costs 40: Dr K 40 / Cr I 40. Back-dated receipt B 5 units for 80 dated 03-11 (own journal Dr I 80 / Cr G 80): average on 03-12 (100+80)/15 = 12, the issue costs 48. Compensation: **Dr K 8 / Cr I 8**. Inventory 100 - 40 + 80 - 8 = 132 = 11 units x 12.

**E2 - FIFO (M1).** Receipt A 10 for 100 (03-10), issue 4 on 03-12 consumes A: 40. Back-dated receipt B 5 for 100 on 03-09 is older; the replay orders FIFO layers by effective date and sequence (the existing replay schedule), so the issue consumes B: 80. Compensation: **Dr K 40 / Cr I 40**. Inventory 100 - 40 + 100 - 40 = 120 = layers A 10@100 + B 1@20.

**E3 - settled obligation, full coverage (M3; after TM-D13).** Original: issue 7 uncovered at provisional 35: Dr K 35 / Cr O 35; receipt 7 for 28 settles: Dr O 35 / Cr G 28, Cr V 7 (the receipt has no journal of its own). Back-dated receipt 7 for 42 (own journal Dr I 42 / Cr G 42) covers the issue: replay issue 42 (Dr K 42 / Cr I 42), no obligation, the later receipt enters inventory (Dr I 28 / Cr G 28). Bucket diff: K +7, V +7, I -14, O 0, G 0. Compensation **Dr K 7, Dr V 7 / Cr I 14**. Final: inventory 28, K 42, V 0, O 0. Note: no original journal holds an inventory line, so I comes from the accounting version recorded on the posting (section 2 step 3).
*Two periods (issue in March, settlement in April):* March: **Dr K 7, Dr O 35 / Cr I 42**; April: **Dr I 28, Dr V 7 / Cr O 35**. O is zero only over the whole history; a single netted entry would leave O wrong at the March period end and block that close (E6).

**E4 - settled obligation, partial coverage (M4; after TM-D13).** 3 units on hand for 15 (average 5). Original: issue 10 covers 3 (15), 7 at provisional 35: Dr K 50 / Cr I 15, Cr O 35; receipt 7 for 28 settles: Dr O 35 / Cr G 28, Cr V 7. Back-dated receipt 4 for 20 (own journal Dr I 20 / Cr G 20): on hand 7 for 35; issue covers 7 (35), 3 at provisional 15: Dr K 50 / Cr I 35, Cr O 15; receipt settles 3 (actual 12): Dr O 15 / Cr G 12, Cr V 3; 4 units enter at 16: Dr I 16 / Cr G 16. Diff: K 0, I -4, V +4. Compensation **Dr V 4 / Cr I 4**. Inventory 20 - 4 = 16 = 4 x 4.
*Two periods:* issue period **Dr O 20 / Cr I 20**; settlement period **Dr I 16, Dr V 4 / Cr O 20**.

**E5 - ordinary issue later reversed (M6).** Issue 40 (Dr K 40 / Cr I 40) reversed (Dr I 40 / Cr K 40): net 0. After a back-dated receipt the issue costs 48 and the reversal restores exactly 48: net 0 **when both are in the same period**. If the issue is in March and the reversal in April, the bucket diff is March **Dr K 8 / Cr I 8** and April **Dr I 8 / Cr K 8** (a reversal posted with a later date is allowed when its journal is posted); netting across periods would misstate both period results, so the period split is the proposed rule (TM-D8/TM-D11).

**E6 - transfer (M2, TM-D7).** W1 average 10 units for 100; transfer 4 to W2 carries 40 (no journal); issue of 2 from W2 costs 20 (Dr K 20 / Cr I 20). Back-dated receipt into W1 5 for 80: W1 average 12, the transfer carries 48, W2 average 12, the issue costs 24. Compensation **Dr K 4 / Cr I 4**; the transfer needs no line. Final inventory 156 = 13 units x 12.

**E7 - why original accounts matter (D-044).** E3 booked as only "Dr K 14 / Cr I 14" would leave K 49 and V credit 7; net expense is 42 but the classification differs from restoration.

## 5. Decisions required from the owner

Unless decided, every item stays on its fail-closed behavior and its matrix cells stay refused.

**TM-D1 - live effect, journal cancelled/reversed outside the stock reversal (M9).** *Scenario:* an issue moved stock; an accountant cancelled its draft journal without reversing the stock document (state "failed", blocks close). *Treatments:* (a) refuse replay of the chain until the stock document is reversed or the accounting side repaired; (b) silent catch-up booking of the whole hypothetical effect; (c) ignore. *Impact:* (b) books what the accountant removed; (c) leaves cost ledger and ledger permanently different. **Recommended: (a)** (the state already means "needs attention"). *Fail-closed:* `open_decision`.

**TM-D2 - stock reversed, draft journal cancelled by that reversal (M10).** *Scenario:* an unposted posting is reversed; the reversal cancels its draft (JournalAction 1). *Treatments:* (a) refuse; (b) accept as ledger-neutral when the replay proves original and reversal net to zero stock and value; (c) accept unproven. **Recommended: (b)**, but only when it is provable that the journal was cancelled BY the stock reversal (JournalAction 1 alone is ambiguous with a journal already cancelled outside: that case is TM-D1); if the system cannot distinguish them (to be verified against journal events during design) it stays refused. *Fail-closed:* `open_decision`.

**TM-D3 - any journal in the closure not yet Posted (M11).** *Scenario:* a live effect whose journal is Draft/Pending/Approved, or a reversing journal not yet posted. *Treatments:* (a) refuse until posted; (b) treat as booked; (c) compensate against the current ledger. *Impact:* (b)/(c) depend on a mutable draft and can double-book (section 2 step 6). **Recommended: (a)** (cost: post the draft, re-run). *Fail-closed:* evidence accepts the state; the plan refuses `unposted_effect` (not yet implemented; refusal is the only behavior).

**TM-D4 - original account no longer valid (M12).** *Scenario:* the original account is inactive, a group, or foreign-currency-limited. *Treatments:* (a) refuse; (b) book anyway; (c) explicit remap inside the plan to an active postable account of the same type and nature, base-currency compatible, approved by someone other than the author, audited, in the approval content and fingerprint. *Impact:* (b) violates chart rules; (a) blocks legitimate corrections after chart cleanups; with (c) the old account is never corrected and the new account takes the whole difference (visible in the plan). **Recommended: (c), default (a)** when no remap is approved. *Fail-closed:* `open_decision`.

**TM-D5 - reversing journal later cancelled by hand (M13), and cancelled compensation drafts.** *Scenario:* stock is reversed but the ledger still carries the original effect; the same family arises if a compensation draft is cancelled (cost ledger re-costed, general ledger behind; the close probe blocks, but no repair path exists, and the probe's failed-journal check covers only stock posting journals). *Treatments:* (a) refuse until repaired by a separate repair action; (b) ledger authoritative; (c) ignore. **Recommended: (a)** plus, after approval, a small repair slice (re-create the reversing/compensation journal) and extending the probe's failed-journal check to compensation journals. *Fail-closed:* `open_decision`.

**TM-D6 - item-group provenance (M14).** *Scenario:* journals aggregate per account; posting lines store no item group; the group at posting time comes from live data. *Treatments:* (a) refuse when ambiguous (all items of a posting resolving to one account, confirmed by the recorded version, is accepted); (b) also persist the resolved inventory account per posting line from now on (company migration: **needs approval**); (c) infer. **Recommended: (a) now, (b) as a separate approved migration.** *Fail-closed:* `classification_ambiguous` / refusal for mixed-group postings.

**TM-D7 - transfers (M2).** *Scenario:* replay changes the cost a transfer carries; transfers book no journal (E4). Cross-branch transfers cannot exist (refused at validation), so the legs always share branch and inventory account. *Treatments:* (a) keep refusing; (b) accept; downstream issues follow M1. **Recommended: (b).** *Fail-closed:* `classification_unavailable`.

**TM-D8 - date/period of compensation (all matrix cells).** *Treatments:* (a) one entry per affected period, dated at the affected original operation: Open periods allowed; a Soft Closed period changes reported figures (stock-side posting and reversal currently apply NO soft-close override; any soft-close permission exists only for journals: to be verified and designed); a Final Closed period refuses the whole plan; (b) all entries on one chosen date, usually today. *Impact:* (b) leaves earlier periods unchanged in the general ledger but the cost-ledger re-cost still carries the original dates, so the close probe (cumulative comparison per period end) would show a permanent difference at the earlier closed period and block its Final Close unless the cost-ledger re-cost is also dated at the same later date (which would make cost history itself wrong). **Recommended: (a); never silently fall back to (b).** *Fail-closed:* plan cannot be created.

**TM-D9 - approval and separation of duties.** *Proposal [P]:* plan prepared by one user, approved by a different user holding a new approval permission, materialized atomically; compensation journals are ordinary Draft journals under the existing journal approval policy and posting permission. Approver sees exact entries, plan fingerprint and linked evidence; audit links plan, journals, approvers and fingerprint. *Decision requested:* confirm no extra role beyond the existing approval policy (recommended) or name the additional separation. *Fail-closed:* no execution exists.

**TM-D10 - open obligations (M8).** *Scenario:* back-dated event for a position with an open obligation. *Treatments:* (a) keep refusing (E1 codes); (b) support re-provisioning. **Recommended: (a);** (b) needs a new accounting contract (O and K amounts change while nothing is settled). Consequence for users stated in E1.

**TM-D11 - approve the method itself.** The bucket-difference method of section 2 (per account, branch, currency and fiscal period; pinned accounts with the recorded-version fallback; append-only cost-ledger re-cost; unposted state refused; compensation journals as ordinary drafts). *Treatments:* (a) approve as proposed; (b) change it. **Recommended: (a).** Without this decision no matrix cell may be implemented.

**TM-D12 - repeat and chained plans, idempotency, links.** *Scenario:* a second back-dated entry on a closure that already has a plan; or a repeated plan before the first one is posted. *Proposal:* section 2 step 7 (source key per plan and bucket; plan-to-journal link table; `plan_pending`; posted compensation feeds the next E_orig). *Needs approval:* proposal/link tables (company migration). *Fail-closed:* closures with an executed plan are refused. **Recommended: approve.**

**TM-D13 - obligations created, resized or removed by replay (M3-M5, M7, M15).** *Scenario:* E3 removes an obligation, E4 shrinks one, a back-dated outflow can make a later issue uncovered (an obligation nobody approved, with O/V accounts absent from the original evidence); the obligation, settlement and approval records are append-only and a manual provisional cost was approved as a snapshot. *Treatments:* (a) first increment: refuse every plan whose replay creates, resizes or removes an obligation (supports M1, M2, M6 only); (b) later: append-only replay-correction rows; a shrink inherits the original approval, a growth or a new obligation requires the existing negative-exception approval again. **Recommended: (a) now, (b) as a later approved slice.** *Fail-closed:* refused.

**TM-D14 - scope priority of stocktake/adjustment support (M16).** Today any back-dated entry before a stocktake adjustment is refused. *Treatments:* (a) accept this limit for the first increment; (b) design adjustment replay next (adjustment counter account bucket). **Recommended: (a) for the first increment, then (b).**

## 6. Part B - method conversion dependencies (D-045 prerequisites)

All **proposals**; conversion stays disabled (`valuation_method_changed`).

* **C-D1 - return/reversal after conversion of an operation made before it.** (a) restore the ORIGINAL amounts and re-enter them as new basis (average: added to quantity/value; FIFO: one new layer at the original amount), never resurrecting old layers; (b) re-cost at the new method. **Recommended (a)**: the reversing journal mirrors the original exactly and carrying value is preserved. This changes E2 ("restores the original layers") after a conversion and must be tested.
* **C-D2 - transfers across a conversion.** A transfer posts both legs in one operation on one date, so no transfer is "in flight". The relevant case is a LATER reversal or replay of a pre-conversion transfer. **Recommended:** the reversal restores the original amounts to the source as new basis (as C-D1) and takes them out of the destination at the destination's current method, refusing when the destination no longer holds the quantity.
* **C-D3 - back-dated movements before a conversion date.** They change the quantity/value carried into the conversion. **Recommended:** conversion barrier: such movements are refused.
* **C-D4 - revaluation.** Not approved; excluded from this phase.

## 7. Technical dependencies needing no owner decision

* Negative-stock replay (obligations/settlements/reversals) and the E_hyp journal builder: internal, read-only; implement only after TM-D11 (and TM-D13 for obligation cells).
* Proposal storage as non-postable proposals and atomic execution; migrations only after TM-D6(b)/TM-D12 approval.
* Tests before activation: every matrix cell (single and two periods), per-bucket-and-period balance, pending/volatile fingerprints, double execution, plan chaining, concurrency (close during execution, lock order), authorization masking, probe interaction.

## 8. Decision request summary

| ID | Question | Recommended |
|---|---|---|
| TM-D1 | Live effect, journal cancelled/reversed outside stock reversal | Refuse; repair first |
| TM-D2 | Reversed with draft cancelled by that reversal | Accept as neutral, only when provable |
| TM-D3 | Any journal in the closure not Posted | Refuse until posted |
| TM-D4 | Original account no longer valid | Approved same-type remap; default refuse |
| TM-D5 | Reversing/compensation journal cancelled by hand | Refuse; add repair slice and probe coverage |
| TM-D6 | Item-group provenance | Refuse ambiguity; approve a migration later |
| TM-D7 | Transfers | Accept (same branch/account guaranteed) |
| TM-D8 | Compensation date/period | Per period at the affected operation; Final Closed refuses; never silent fallback |
| TM-D9 | Approval/separation | Existing policy, different approver |
| TM-D10 | Open obligations | Keep refusing |
| TM-D11 | The bucket-difference method itself | Approve |
| TM-D12 | Repeat/chained plans and links | Approve (needs migration) |
| TM-D13 | Replay that changes obligations | First increment refuses; later append-only corrections |
| TM-D14 | Stocktake/adjustment replay priority | Later |
| C-D1..C-D4 | Conversion contracts | As section 6 |

**Suggested first increment if you approve TM-D1/D3/D5/D8/D9/D10/D11/D13(a) as recommended:** M1, M6 and M2 (plus TM-D7) with ordinary receipts/issues/transfers only, per-period compensation, everything else refused. That delivers real back-dated posting for ordinary stock without touching the negative-stock model.
