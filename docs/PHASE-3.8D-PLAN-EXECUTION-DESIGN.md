# Phase 3.8D plan storage, approval and atomic execution (design proposal, revision 2)

Status: **design proposal, nothing implemented. Execution is BLOCKED on the owner decisions of section 8** (they relax database safeguards). Revision 2 answers the independent design review (8 High, 9 Medium, 4 Low; `review-records/PR-48-plan-execution-design.md`), which found revision 1 not implementable as written. Scope: the first increment of the transition matrix working baseline (cells M1/M2: ordinary receipts, issues and transfers; compensation per period; everything else refused). It builds on the existing read-only `LoadCompensationPlanAsync` (one snapshot: history, comparison, classification evidence, compensation, fiscal dates, live authorization). Decisions TM-D1..TM-D14 apply as recorded in `PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md` (owner baseline = the recommended conservative treatments). Writing the migration and executor is blocked on this design's independent review.

## 1. Facts the design rests on (verified in the schema/code)

* Cost **operations, entries and consumptions are append-only** (triggers); only a layer's remaining figures and a position's totals change; "an operation can only be marked reversed, once"; `ReversesEntryId` restores exactly the consumed layers/amounts (`0014_company_costing_engine.sql`). Every entry of a position carries the next sequence number and its own immutable running `quantity_after`/`value_after`.
* **CORRECTION (review H1):** the position trigger `f_589a0a19` (0014) raises "a cost position does not accept back-dated entries" whenever `last_effective_date` would decrease, so PostgreSQL itself rejects any back-dated entry, before the application barrier (`InventoryCostingService.cs:248`).
* **CORRECTION (review H2/H3):** `ReverseAsync` reverses a whole OPERATION (all its entries, locking every position it touches, requiring untouched inflows) and marks it reversed once; the operation CHECK ties kind 4 to a single unique `reverses_operation_id` (`k_f356c89a`, `k_99f6719a`). There is no entry-level storno and no entry lineage (original -> storno -> rebook).
* Operation kind CHECK covers 1 receipt, 2 issue, 3 transfer, 4 reversal; the stock posting tables, journals and approvals are append-only/immutable as described in the earlier contracts.
* The replay (`CostReplay`/`CostFlowComparison`) already yields, for a proposed ordinary event, the exact revised amount, quantity-after and value-after of every affected entry.

## 2. The central problem: how history changes without being edited

D-044/D-045 and SRS 19.3: historical cost operations and journals are never modified. A back-dated event changes the amounts AND the running figures of every later entry of the affected positions. Two append-only representations are possible.

**Option S - supersession view.** Keep all original entries; add an append-only `epoch` table marking original entries as superseded and holding the revised entry figures; every reader of "current history" (engine, snapshot reader, probe, reports) filters by supersession.
* Pro: no duplicate movements. Con: every existing reader changes; the current-state tables (positions/layers) are no longer derivable from entries by simple fold; large regression surface across 3.6-3.8.

**Option R - storno and rebook ("replay epoch") - proposed.** Under the position locks, for each affected position append, in sequence order: (1) reversing entries for the affected suffix entries in reverse order (existing reversal primitive, exact restoration); (2) the new back-dated event; (3) re-booked copies of the suffix entries in original (date, sequence) order with the replayed amounts. All three are ordinary entries/operations of one new operation kind **5 "replay epoch"** (a new CHECK value) linked to the plan. Running figures stay consistent by SEQUENCE; original rows stay untouched; the cost ledger total equals the replayed value; layers are restored and re-consumed by the same primitives; positions end at the replayed state.
* Pro: reuses proven primitives, no reader change for the current-state fold, full audit (originals + reversals + re-books), idempotent by plan key. Con: more rows; later date-ordered replays must understand reversal/epoch pairs - the "complete reversal replay" that M6/M7 need anyway (cells stay refused until it exists); the date ordering of rows is no longer monotone, so the date barrier must be replaced by an epoch-aware rule: back-dating is allowed ONLY through an executed plan.
* **Recommended: Option R.**

Cost-only: epoch entries carry NO journal. The general ledger changes only through the plan's compensation journals and the new event's own normal journal, exactly as proven by the PostgreSQL ledger checks (E1 132, E2 120, E6 156), so the close probe's cost-ledger vs general-ledger comparison holds after the journals post.

## 2a. What Option R really needs (review findings H1-H8, all accepted)

1. **Entry-level storno primitive** (new code): select the affected suffix entries across ALL positions of the closure; storno in GLOBAL descending entry id, rebook in GLOBAL ascending (date, original entry id) (transfers couple positions; average needs "no later live entries"; a wrong order would drive values negative and be rejected). Lock order: branch-structure advisory key (`CompanyBranchService.LockKey1/2`) first, then positions by (warehouse, item) as `LockPositionsAsync` does, then the plan row, then the normal posting path. READ COMMITTED with locks first (a RepeatableRead snapshot would be taken before the lock wait and be stale), plus `FOR SHARE` on every journal, period and account row the plan depends on, and `FiscalPeriodGate` per epoch entry date inside the transaction. The existing planner must be refactored to run on the executor's connection and transaction.
2. **Schema changes (owner approval required):** (a) the position trigger keeps `last_effective_date = GREATEST(old, new)` and accepts an older entry date ONLY for epoch operations; (b) new operation kinds for storno and rebook (the kind/reverses CHECK must be replaced: ALTER ... DROP CONSTRAINT contradicts the forward-only/no-DROP rule), or one epoch kind plus a lineage table; (c) a lineage table epoch -> (original entry, storno entry, rebook entry); (d) the plan tables below.
3. **Dating (H7):** storno and rebook entries carry the ORIGINAL entry dates and the event its own date, never "now". Otherwise the close probe (cost entries by effective date vs general ledger) can never reconcile, because the compensation journals are dated at the original posting. Consequence: as-of valuation for any date from the back-dated event onward changes after the epoch (open periods only); a report produced before cannot be reproduced afterwards except through the epoch audit trail.
4. **The event:** an approved stock document must exist for the new event (who creates and approves it is an owner decision). Execution sequence: storno part, normal posting of the event through the normal functions with a plan-scoped bypass (it creates its OWN cost operation), then the rebook part: several operations with keys derived from the plan key, all referenced by the execution row. (Revision 1 contradicted itself here.)
5. **Later effects:** explicit refusals `plan_on_epoch_position` (a second plan on a position that already has an epoch) and `operation_rewritten_by_epoch` (reversing a document rewritten by an epoch, including the event itself) until epoch-aware replay/reversal exist; the existing snapshot refusal is only incidental. Epoch reconciliation checks are added (every storno has a rebook, the signed epoch sum equals the replay delta, positions equal the proposed positions).
6. **Stale handling:** the stale mark commits in its own transaction; it is NOT applied for executor-permission failures (masked forbidden), cancellation, budget, serialization or transient errors. The approval re-runs the planner under the APPROVER's permissions; approvals are unique per (plan, revision, fingerprints); the fingerprint also binds position totals/layer hash, valuation policy and accounting version; the plan key is unique at the execution row and re-creation after execution is refused (a cancelled compensation draft frees the journal source key).
7. **Movement reports** order by (date, sequence) and would look scrambled after an epoch: order by sequence or show an epoch marker (UX decision).

## 3. Persistence (one company migration and one system access migration, numbered from the next free numbers at implementation time; forward-only, no DROP)

**Migration numbering:** no number is reserved ahead. Company and system migration numbers are the next free ones on `main` at implementation time and follow merge order, allocated in `docs/INTEGRATION-STATUS.md` (the earlier reservation of company 0023 / system 0025 is stale: both numbers are used on `main`). No migration has been written for this design.

* `CompensationPlan`: id, item, warehouse roots, the proposed event payload (canonical JSON), plan/comparison/evidence/periods fingerprints, status (1 proposed, 2 approved, 3 executed, 4 rejected, 5 cancelled, 6 stale), revision, creator, timestamps, approver. Payload and fingerprints immutable by trigger; status only along allowed transitions; never deleted.
* `CompensationPlanLine`: line number, posting, date, period, branch, account, debit, credit (immutable).
* `CompensationPlanEvent`: append-only history (created, approved, rejected, executed, staled), actor, time, note.
* `CompensationPlanExecution`: plan (unique), the replay-epoch cost operation, the new event's stock posting, the compensation journal ids (append-only; unique source key = plan id + line group, so a repeat cannot create a second set: A6/idempotency and TM-D12).
* A plan is **non-postable data**: no journal exists until execution; the compensation journals are created only inside the execution transaction as ordinary Draft journals (source key = plan key) following the normal approval workflow. Normal journal posting must refuse a journal whose source key belongs to a plan that is not `executed`.

## 4. Approval (TM-D9)

Created by one user (permission to plan), approved by a DIFFERENT user holding a new approval permission, who sees the exact lines, fingerprints and evidence summary. Approval is bound to the fingerprints and the plan revision; any change makes the plan `stale` and requires a new plan and approval. Rejection/cancellation are events. Reuses the existing separation-of-duties pattern (approver is neither creator nor executor where the policy demands). New permission codes are added to the catalog with preset-role decisions recorded separately; no existing role is widened implicitly.

## 5. Atomic execution

One company transaction (RepeatableRead is NOT used; locks instead), in this order, all-or-nothing:
1. Idempotency: if an execution row exists for the plan, return it (replay-safe).
2. Take the same locks as posting/reversal, same order: company posting/structure advisory lock, then position rows of the affected item × warehouses (ascending ids), then the plan row.
3. Re-run the full read-only planning inside the locked transaction (history, comparison, evidence, compensation, fiscal dates, permissions of the EXECUTOR) and require every fingerprint to equal the approved ones; otherwise mark the plan stale and stop. A period closed since planning, a changed journal status, a new posting, a changed account validity all end here.
4. Append the replay epoch (section 2, Option R) with the plan key; verify position totals and layers equal the planned final state (the replay's proposed positions); any mismatch rolls everything back.
5. Create the new event's own stock posting and journal through the NORMAL posting functions with the epoch-aware back-date permission scoped to this plan (no general lifting of the barrier), and the compensation journals as Draft journals.
6. Append execution/plan events and the audit event (plan id, fingerprints, approver, executor, journal ids).
Pending accounting stays visible: until the drafts are posted the close probe blocks the period (existing behavior); cancelled compensation drafts are TM-D5 (repair slice).

## 6. Invariants and tests required before enabling

* Original journals, cost operations, entries, consumptions untouched (hash of pre-existing rows unchanged); sum of cost ledger = replayed value; GL inventory balance after journals post equals cost ledger; no row without plan key in the epoch; double execution / concurrent execution create exactly one set; stale plan after a concurrent posting, a period closing, an account deactivation; rollback on injected failure at each step; authorization masking and separation of duties; the barrier still refuses back-dating outside an executed plan; FIFO and average; multi-warehouse transfer chains; both single-period and two-period compensation.
* Refusals unchanged: obligations/settlements, reversals, adjustments, mixed-group provenance, unposted journals, closed/soft-closed periods, repeat plans until the plan-chaining read exists.

## 8. Owner decisions needed BEFORE the migration and executor are written

| ID | Decision | Recommendation |
|---|---|---|
| X1 | Relax the position back-date trigger and replace the operation kind CHECK (weakens a database safeguard; needs your explicit schema approval) | Approve, limited to epoch operations of an approved, executed plan |
| X2 | Dating of epoch entries: original dates (probe reconciles; as-of figures in open periods change) vs "now" (history stable but the probe never reconciles) | Original dates |
| X3 | Executor separation: may the creator or approver execute? | Executor different from creator; approver may execute |
| X4 | Role presets for the new plan/approve/execute permissions | Dedicated approver preset, no widening of existing roles |
| X5 | Plan on a position that already has an epoch | Refuse in the first increment |
| X6 | Reversal of documents rewritten by an epoch | Refuse in the first increment |
| X7 | Who creates and approves the stock document of the back-dated event | Created by the planner, approved through the normal document approval before the plan can be approved |
| X8 | Trial cap behavior (epoch entries are not user transactions; are compensation drafts counted?) | Epoch entries not counted; drafts follow the normal journal rule with a clear refusal code |

Until these are decided the planner stays read-only and the barrier stays active. Technical choices the agent makes without asking: table layout, key derivation, lock order within the rules above, isolation level, reconcile checks, error codes, plan-status triggers.

## 7. Open technical questions for the design review (answered by the review, kept for the record)

1. Is a new operation kind 5 plus epoch-aware replay acceptable, or should epoch rows reuse kind 4/1/2 with a plan link only (smaller CHECK change, weaker typing)?
2. How should snapshot reading treat epoch history for LATER plans: by sequence fold of the current state (cheap) or by recognising epoch groups (needed for date-ordered replay)? Proposal: the first increment refuses a second plan on any position that already has an epoch (TM-D12 baseline), so no epoch-aware replay is needed yet.
3. Lock order versus the existing posting/reversal paths (deadlock analysis) and the advisory lock key to reuse.
4. Whether the new event's posting must be part of the same plan transaction (proposed) or may be posted afterwards (rejected: it would leave the plan half-applied).
