# Phase 3.8D plan storage, approval and atomic execution (design proposal, revision 3)

Status: **design proposal; nothing is implemented. Execution stays BLOCKED on owner decisions Q1 and Q2** of `PHASE-3.8D-RESUME-REVIEW.md` (merged in #171, `785181b`).
* **Q1** is X1-A, which replaces X1 as written.
* **Q2** is the X2-X8 package, including the X3 and X7 additions.

**Binding owner constraints for this revision (2026-11-21):**
* X1-A and X2-X8 stay **PENDING OWNER DECISIONS** everywhere; X8 also depends on OD-12.
* The existing PostgreSQL safeguards and all earlier #48 work are preserved.
* Nothing is implemented: no X1-A, no migration, no financial execution path.
* Revaluation stays within the approved 3.8D scope, but needs its own separately reviewed accounting contract, which must preserve FIFO layers (section 11).
* The method-conversion safeguard exception (Q4) stays unimplemented until it is independently reviewed and approved.
* Migration-guard hardening (Q5) is a separate security workstream that needs protected-file authorization. It is referenced here, not folded in.

Everything marked **[P-Q1]** or **[P-Q2]** is *proposed, PENDING OWNER DECISION*. **[P-Q1]** = X1-A; **[P-Q2]** = X2-X8, with X8 also pending OD-12. Nothing here changes code, a migration, a trigger, a CHECK or any other safeguard. The planner `LoadCompensationPlanAsync` stays read-only and the application barrier `backdated_not_supported` stays active.

**History of this document:**
* Revision 1 was reviewed at row level and found not implementable (8 High; `review-records/PR-48-plan-execution-design.md`).
* Revision 2 recorded those corrections and deferred eight safeguard and policy questions (X1-X8) to the owner.
* Revision 3 (this one) adopts the additive X1-A design. That design was written in the resume review, independently cross-checked by Agent G's X1 review, and reviewed in #171 (`review-records/PR-171-phase-3.8d-resume-review.md`). Revision 3 also corrects revision 2's overstatement of the existing database guard and fixes stale text.

Revision 3 needs its own independent design review before any migration or executor is written.

Scope: the first increment of the transition-matrix working baseline: cells M1 and M2 (ordinary receipts, issues and transfers, compensation per period). M6 (a later reversed issue) follows once reversal replay exists. Everything else is refused. Decisions TM-D1..TM-D14 apply as the owner-authorized conservative working baseline (`PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md`).

## 1. Facts the design rests on (verified on `main` `785181b`, company migrations 0001-0032)

* **Cost history is append-only.** Cost **operations, entries and consumptions are append-only** (`0014_company_costing_engine.sql`: `f_9c827423`, `f_8b9d471d`, `f_4bf3449c`). Only a layer's remaining figures (`f_b16f3723`) and a position's totals change. An operation can be marked reversed only once. A reversal entry restores exactly the consumed layers and amounts. Every entry of a position carries the next sequence number (`k_214c34ab`) and its own immutable running `quantity_after`/`value_after`. An entry is reversed at most once (`k_c8fdc6c0`).
* **CORRECTION of revision 2 (finding F1, resume review c.1; independently found by Agent G).** The position trigger `f_589a0a19` (0014 lines 70-86) forbids only a **decrease of the position's `last_effective_date`** (lines 79-81). No trigger compares an **entry's** date (`t_bc727c1d.c_12665ffb`) with anything. The costing service writes the entry date into the position (`InventoryCostingService.cs` line 273), and the application barrier refuses earlier dates (line 248). A writer holding the runtime role's grants that inserts a back-dated entry and keeps the position date with `GREATEST(old, new)` passes PostgreSQL today. Revision 2's statement that "PostgreSQL itself rejects any back-dated entry" holds only by convention.
* **Operation kinds cannot be widened.** The operation CHECK `k_f356c89a` (0014 line 33) admits kinds 1 receipt, 2 issue, 3 transfer and 4 reversal, with kind 4 ⇔ `reverses_operation_id` set. Unique index `k_99f6719a` allows one reversal per operation. Widening the CHECK needs `DROP CONSTRAINT`, which the migration guard refuses (`MigrationScript.cs` line 24; precedent DB-1 in 0020). A value-only entry is impossible (`k_0b3e9de8`: quantity > 0). This is finding F3 of the resume review.
* **The migration guard has a blind spot.** It refuses only `DROP`, `TRUNCATE` (except `BEFORE TRUNCATE`) and `DELETE FROM`. It does not detect `CREATE OR REPLACE FUNCTION` of an existing guard function, or `ALTER TABLE ... DISABLE TRIGGER`. This is finding F2, and the guard hardening is owner question Q5 (an allow-pattern). **This design therefore redefines no existing function and disables no trigger.**
* **Reversal is per operation.** `ReverseAsync` reverses a whole operation, locks every position it touches, requires untouched inflows, and marks the operation reversed once. There is no entry-level storno primitive in the application yet. Entry-level reversal links (`c_4f9e777a`) are allowed by the schema in any operation kind.
* **Normal posting runs inside a savepoint.** `StockPosting.PostInWorkAsync` (main, #107) runs the posting under a SAVEPOINT inside the caller's transaction (`StockPosting.cs` lines 154-171). Any same-transaction binding must therefore be correct inside savepoints. The `xmin` binding of 0022 (`f_831e3a7a`) is not; it fails closed there.
* **The replay is already available read-only.** `CostReplay` / `CostFlowComparison` already yield, for a proposed ordinary event, the exact revised amount, quantity-after and value-after of every affected entry.

## 2. How history changes without being edited

D-044/D-045 and SRS 19.3: historical cost operations and journals are never modified. A back-dated event changes the amounts AND the running figures of every later entry of the affected positions.

**Option R: storno and rebook ("replay epoch"), retained.** Under the position locks, each affected position gets, in order:
1. reversing (storno) entries for the affected suffix entries;
2. the new back-dated event;
3. re-booked (rebook) copies of the suffix entries with the replayed amounts.

The rows are ordinary entries. Running figures stay consistent by SEQUENCE, the original rows stay untouched, the positions end at the replayed state, and the cost ledger total equals the replayed value.

Option S (a supersession table beside the entries) is rejected. A position cannot change without an entry, because `f_589a0a19` line 78 requires the sequence to advance by exactly 1. Every reader would also have to change.

**Typing of epoch rows [P-Q1]. This changes revision 2, which used a new kind 5 or a replaced CHECK; that is infeasible (F3).** Epoch operations keep NATURAL kinds:
* **storno operations:** one per affected original operation, with the original's kind (1/2/3) and `reverses_operation_id` NULL. Their entries carry entry-level `c_4f9e777a` links to the stornoed entries.
* **the event:** its normal posting, with its own normal operation.
* **rebook operations:** one per stornoed original operation, with its natural kind.

Their meaning comes from a new append-only **epoch membership/lineage table** (section 3.2). This is the same pattern as 0020 DB-1, which typed obligations and settlements by a side table instead of widening `k_f356c89a`. The reversed-by mark of the ORIGINAL operations is **not** set, so no historical row is updated (D-044).

**Ordering.** Storno in GLOBAL descending entry id; rebook in GLOBAL ascending (date, original entry id). Transfers couple positions, and average needs "no later live entries", so a wrong order would drive values negative and be refused. FIFO: the event's receipt layer and the rebooked receipt layers take new arrival ranks in replay order, so FIFO consumption follows effective date (example E2 of the transition matrix).

**Entry-level storno.** Only the closure's entries of an operation are stornoed. Entries of other items in the same multi-item operation stay live. The lineage table maps each original entry to its storno entry and its rebook entry. Readers of a posting's cost operation must consult it. In increment 1 they simply refuse (X5/X6).

**Cost-only.** Epoch entries carry NO journal. The general ledger changes only through the plan's compensation journals and the event's own normal journal (PostgreSQL ledger checks E1 132, E2 120, E6 156), so the close probe's cost-ledger vs general-ledger comparison holds after the journals post.

**Dating, X2 [P-Q2].** Storno and rebook entries carry the ORIGINAL entry dates; the event carries its own date. `created_at` keeps the recording time (SRS 9 separates the operation date from the recording date).
* Every epoch date must lie in an **Open** period. Soft Closed refuses, because the stock side has no soft-close override. Final Closed refuses the whole plan.
* Consequence: as-of valuation for any date from the event onward changes after the epoch (open periods only). A report produced before cannot be reproduced afterwards except through the epoch lineage.
* Movement reports must order by sequence or show an epoch marker. This is a UX choice, not a policy (residual risk R1).

## 3. Persistence [P-Q1]

One new **company** migration and one **system** access migration. Both are forward-only and additive: no DROP, no `CREATE OR REPLACE` of an existing function, no `DISABLE TRIGGER`, no ALTER of an existing constraint. Their numbers are the next free ones on `main` at implementation time, allocated in merge order in `docs/INTEGRATION-STATUS.md`. No number is reserved here; the earlier "company 0023 / system 0025" reservation is void. Opaque physical names (D-029), with the logical dictionary in a new `CompanySchema.CompensationPlan.cs`. The runtime role gets INSERT and SELECT only (plus UPDATE of the plan status column), and the completeness guard (#132/#140) covers the new tables.

### 3.1 Plan tables

* **`CompensationPlan`**: id, item, warehouse roots, the proposed event's stock document id and revision, the proposed event payload (canonical JSON), the fingerprints (plan, comparison, evidence, periods, position totals/layer hash, valuation policy, accounting version), status (1 proposed, 2 approved, 3 executed, 4 rejected, 5 cancelled, 6 stale), revision, creator, approver and timestamps. A trigger makes the payload and fingerprints immutable; status moves only along allowed transitions; nothing is ever deleted. **SoD, X3 [P-Q2]:** a trigger refuses an approval when the approver is the creator, the last editor or the submitter of the plan, or the creator, last editor or submitter of the event's stock document. This is the D-066/D-158 pattern, enforced in both the service and PostgreSQL. The approval is bound to the plan revision and fingerprints.
* **`CompensationPlanLine`**: line number, posting, date, period, branch, account, debit and credit (immutable).
* **`CompensationPlanEvent`**: append-only history (created, approved, rejected, cancelled, executed, staled), with actor, time and note.
* **`CompensationPlanExecution`**: `UNIQUE (plan)`. It records the event's stock posting and the compensation journal ids (append-only; the journal source key is plan id + bucket, so a repeat cannot create a second set; A6, TM-D12).
  * **Same-transaction binding (resume review L4):** a column `created_xact xid8` is assigned by a BEFORE INSERT trigger with `NEW.created_xact := pg_current_xact_id()`. A client-supplied value is overwritten and never trusted.
  * "Written in the current transaction" means `created_xact = pg_current_xact_id()`. This is a 64-bit top-level id: no wraparound and no frozen-tuple ambiguity, and it is **correct inside savepoints**, which the event's normal posting uses (section 1).
  * It replaces the `xmin` low-32-bit pattern of 0022 for this design.
  * `UNIQUE (plan)` stays the replay barrier.

### 3.2 Epoch membership and lineage

* **`EpochMember`**: operation id (PK, FK to the operation), execution id (FK), role (1 storno, 2 event, 3 rebook), and the original operation (required for roles 1 and 3, NULL for 2). Append-only (UPDATE/DELETE/TRUNCATE refuse), `REVOKE ALL FROM PUBLIC`. The BEFORE INSERT trigger requires:
  * the execution exists with `created_xact = pg_current_xact_id()`, its plan is in status `approved`, and the plan's approver is not its creator;
  * the operation has **no entries yet**, and no obligation or settlement references it. This proves the operation is being built now. It cannot adopt an operation from an earlier transaction: an empty operation from an earlier transaction cannot exist outside obligations and settlements because of `r_d621465c`, 0020. No `xmin` check is needed on the existing operation table;
  * for roles 1 and 3, the original operation is not itself an epoch member (no epoch on an epoch, X5).
* **`EpochEntryLineage`**: original entry → storno entry → rebook entry (each unique), with the execution id. Append-only. Insert is allowed only when all three entries belong to members of the same execution.

### 3.3 New entry-level guard (tightening) [P-Q1]

A NEW BEFORE INSERT trigger on `t_bc727c1d`. It is a new function; no existing function is replaced. It reads the position row `FOR UPDATE`; the engine already holds that lock, so no new lock order arises (Agent G option (a)). It reads only table rows, **never a GUC or session variable**.
1. If `NEW.c_12665ffb` < the position's current `c_3c880b93`, **refuse unless** `NEW.c_eb34b7d8` has an `EpochMember` row whose execution has `created_xact = pg_current_xact_id()`. The execution's plan must also be Approved, with an approval whose approver is not the creator and whose fingerprints equal the plan's. This closes F1 for every ordinary path. G's assessment: the new guard could also ship as its own strengthening slice, independent of 3.8D (owner choice).
2. If `NEW.c_4f9e777a` is set and the operation's kind is not 4, refuse unless the operation is an epoch member with role 1 (storno).
3. For any entry of an epoch member:
   * the entry date must lie in an **Open** fiscal period (Soft Closed, Final Closed or missing refuses). This gives epoch rows the database period guard that the open owner item STOCK-PERIOD-DB proposes for all stock effects;
   * the position must hold no open obligation (TM-D10/TM-D13a);
   * the position must have no epoch member of ANOTHER plan (X5 as a database rule).
4. **X6 as a database rule:** a NEW BEFORE INSERT trigger on the operation table refuses a kind-4 reversal whose reversed operation has stornoed entries (an epoch lineage row). This is a new trigger; `f_9c827423` is unchanged.

### 3.4 Deferred checks (at commit) [P-Q1]

A deferred constraint trigger on `CompensationPlanExecution` (and on `EpochMember` for orphan detection) checks:
* every storno member has exactly one rebook member for the same original operation, and every storno entry has its lineage row;
* the execution has at least one member, and exactly one event member whose stock posting is the execution's posting;
* the plan is in status `executed`, set in this transaction;
* **the plan's business audit event exists in `t_2db8cf1f` (0002, append-only, hash-chained) and was written in the same transaction** (resume review L5). The audit table (0002) has no transaction column and is not altered. The binding works like this:
  * the execution id is generated by the executor in this transaction;
  * the audit event carries the execution id as its target reference (one of the uuid reference columns of `t_2db8cf1f`), and the execution row stores the audit event id;
  * the deferred check requires that the referenced audit row exists and that its target is this execution, whose `created_xact` equals the current transaction.

  Without it, the commit fails. Whether this is sufficient against a pre-chosen id is review question 2 in section 10;
* no epoch member exists without an execution of this transaction, and every member operation has at least one entry.

### 3.5 Executor contract (application) [P-Q1]

* The executor writes `last_effective_date = GREATEST(old, entry date)` for epoch rows and for the event. `f_589a0a19` is therefore **never relaxed** and still refuses any decrease.
* The ordinary path is unchanged. Its date is never earlier than the last date (application barrier at line 248), so `GREATEST` equals today's value. A unit test pins that the ordinary path's written date equals the entry date.

### 3.6 Optional hardening before production (resume review L2)

A single SECURITY DEFINER writer for back-dated rows:
* owned by the company owner role, verified NOT a superuser;
* pinned `search_path`;
* EXECUTE only for the runtime role;
* the entry guard additionally requires `current_user` = that owner for a back-dated row;
* grants re-applied by `grant-company-runtime` after restore.

Then a direct runtime-role SQL write (T2) must also pass the definer's checks. This is not required for increment 1, but recommended before production.

### 3.6a Alternative kept on record (Agent G option (b))

An append-only epoch table read through ONE mandatory read function, with no back-dated write into the cost tables. It is safest for the schema, but it gives two sources of truth for position and layers, changes every reader, and needs an owner decision on how positions move. Choose it only if the owner rules out any back-dated write into cost tables.

**Rejected:**
* relaxing `f_589a0a19` by `CREATE OR REPLACE`;
* any session GUC condition (a general bypass);
* replacing `k_f356c89a` (needs DROP).

### 3.7 Unchanged backstops

`k_c8fdc6c0` (an entry is stornoed once: the database backstop for X6), the append-only triggers, the sequence rule, the no-negative rules, `f_589a0a19` in full, `k_f356c89a`, `k_99f6719a`, `r_7c0b96d2` and `r_d621465c`. The before/after catalog test N17 proves they are byte-identical.

## 4. Approval [P-Q2]

* **Creation:** a user with the new plan permission creates the plan. The event's stock document must already exist and be **approved through the normal document approval** before the plan can be approved (X7).
* **Approval:** a DIFFERENT user with the new approve permission approves it. That user is never the creator, last editor or submitter of the plan or of the event document (X3 addition). The approver sees the exact lines, fingerprints and evidence summary.
* **Execution:** the executor differs from the creator; the approver may execute (X3).
* **Staleness:** approval re-runs the planner under the APPROVER's permissions. Approvals are unique per (plan, revision, fingerprints). Any change makes the plan `stale` and requires a new plan and approval. Rejection and cancellation are events.
* **Permissions (X4):**
  * New branch- and warehouse-scoped codes for plan, approve and execute.
  * Approve and execute are classified **privileged** under D-096.
  * A delegated "Inventory Cost Approver" preset is granted offline (D-066 pattern), not part of Company Administrator. No existing role is widened.
  * The codes are added to the endpoint permission matrix (#119).
* **Plan-bound document, X7 addition [P-Q2]:** the normal posting path refuses a stock document referenced by a plan in status proposed or approved (`document_bound_to_plan`). It may post only through the plan's execution. Cancelling or rejecting the plan releases the document.
* **Trial limit (X8):** follows OD-12 when the owner decides it. The event's stock document counts once; epoch rows and compensation journals are automatic. Until OD-12 is decided, fail closed with a clear code.

## 5. Atomic execution [P-Q1, P-Q2]

One company transaction, READ COMMITTED with locks taken first (a RepeatableRead snapshot would be taken before the lock wait and be stale). All or nothing, in this order:
1. **Idempotency:** if an execution row exists for the plan, return it.
2. **Locks:** the company posting/structure advisory lock (`CompanyBranchService.LockKey1/2`), then positions by (warehouse, item) as `LockPositionsAsync` does, then the plan row. Then `FOR SHARE` on every journal, period and account row the plan depends on. This is the same order as posting and reversal (deadlock-free; T8).
3. **Re-plan:** re-run the full read-only planning on this connection and transaction, under the EXECUTOR's permissions. Every fingerprint must equal the approved ones. Otherwise mark the plan stale in its own transaction and stop. The stale mark is NOT applied for executor-permission failures (masked forbidden), cancellation, budget, serialization or transient errors.
4. **Audit and execution row:** generate the execution id, write the plan's business audit event targeting it, then insert the execution row (its `created_xact` is assigned by the trigger) referencing that audit event.
5. **Storno:** for each affected original operation in global descending entry order, insert the storno operation, its `EpochMember` row (role 1), then its entries with `c_4f9e777a` links.
6. **Event:** post the event through the NORMAL posting functions (`PostInWorkAsync`, inside its own savepoint). The plan-scoped hook inserts the event operation's `EpochMember` row (role 2) before its entries and writes `GREATEST`. The application barrier accepts the earlier date only when that member row exists for an execution of this transaction. There is no general lifting of the barrier and no session flag.
7. **Rebook:** in global ascending (date, original entry id) order, insert the rebook operations, their members (role 3), their entries with the replayed amounts, and the lineage rows.
8. **Verify:** position totals and layers must equal the replay's proposed final state; any mismatch rolls everything back. Create the compensation journals as Draft journals (source key = plan key) under the existing journal approval policy (TM-D9). Mark the plan `executed`. The deferred checks of 3.4 run at commit.

**Pending accounting stays visible.** Until the drafts are posted, the close probe blocks the period (existing behavior). The probe's failed-journal check is extended to compensation journals, and a repair action re-creates a cancelled compensation draft (TM-D5). Both are prerequisites for enabling execution.

## 6. Later effects and refusals (increment 1)

* `plan_on_epoch_position` (X5): a plan whose closure contains any epoch member is refused. The snapshot readers refuse histories containing epoch members.
* `operation_rewritten_by_epoch` (X6): reversal of a document whose cost operation has stornoed entries is refused in the service. The database backstop is `k_c8fdc6c0`.
* `document_bound_to_plan` (X7), section 4.
* Refusals unchanged: obligations and settlements (TM-D13a), open obligations (TM-D10), reversals in the history until M6 replay exists, adjustments and stocktakes (TM-D14), mixed-group provenance (TM-D6), unposted journals (TM-D3), closed or soft-closed periods (TM-D8), a closure with an executed plan (TM-D12).

## 7. Threat model (resume review c.5)

| # | Threat | Protection |
|---|---|---|
| T1 | An ordinary path writes a back-dated entry with `GREATEST` | entry guard 3.3(1) |
| T2 | Direct runtime-role SQL | needs a forged approved plan plus an execution in the same transaction, with the SoD trigger and the audit row (3.4); optionally the SECURITY DEFINER writer (3.6) |
| T3 | Reuse of an old executed plan | `created_xact` must equal the current transaction; `UNIQUE (plan)` |
| T4 | Session or GUC tampering | no flag exists (N12) |
| T5 | Epoch into a closed period | entry guard 3.3(3) |
| T6 | Epoch on an open-obligation position | entry guard 3.3(3) and the planner |
| T7 | Reversal of a rewritten document | service refusal and `k_c8fdc6c0` |
| T8 | Concurrency | lock order of section 5 |
| T9 | Savepoints and subtransactions | **corrected:** the `xid8` binding returns the top-level id, so writes inside the event's posting savepoint are accepted (they belong to the same transaction). A `ROLLBACK TO SAVEPOINT` removes the rows written in it, and the deferred checks catch an incomplete epoch at commit. (The resume review's T9 and N9 still described the `xmin` behavior; revision 3 aligns them with L4.) |
| T10 | DB owner or superuser | out of scope (SRS 4: no DBA rights for users) |
| T11 | Restore or COPY | `pg_restore` creates triggers post-data: no false refusals and no re-validation. The new tables are in the company backup scope. The deferred checks do not fire during restore (N18) |

**Residual risks:**
* R1: movement-report ordering.
* R2: as-of figures of open periods change.
* R3: every later reader must understand the lineage.
* R4: a multi-item operation can be partly live after an entry-level storno.

## 8. Tests required before enabling (all on the isolated 5433 cluster)

**Existing tests, unchanged:** `InventoryCostingTests` (back-dated refusal line 234, direct-SQL protection line 544, method immutability line 214, the reversal, long-scenario and concurrency tests), `StockAdjustmentTests` line 368, the `NegativeStockTests` date rules, `StockReversalTests`, `InventoryPeriodInvariantTests`, `ConcurrencyInvariantTests`, the endpoint constraint matrices, `PhysicalNamingAcceptanceTests` (21.10), the runtime-role completeness guard, and all #48 read-only tests.

**New negative tests:**

| # | Case | Expected |
|---|---|---|
| N1 | Direct SQL inserts a back-dated entry of an ordinary operation with `GREATEST` | refused (passes today) |
| N2 | Member whose execution was written in an earlier transaction | refused |
| N3 | Execution for a plan that is not approved, or approved by its creator, last editor or submitter, or by the event document's creator/editor/submitter | refused |
| N4 | Member or back-dated entry without an execution | refused |
| N5 | UPDATE, DELETE or TRUNCATE on any new table | refused |
| N6 | Lowering a position's last date | still refused |
| N7 | Reverses-entry link in a non-kind-4 operation without storno membership | refused |
| N8 | Second or concurrent execution of one plan | exactly one |
| N9 | Event posted inside the `PostInWorkAsync` savepoint | accepted (same top-level transaction); a rolled-back savepoint leaves no member, entry or lineage, and an incomplete epoch fails at commit |
| N10 | Epoch entry in a Soft Closed, Final Closed or missing period | refused |
| N11 | Epoch on an open-obligation position | refused |
| N12 | Arbitrary `SET` before a back-dated insert | no effect, refused |
| N13 | Normal posting of a plan-bound document | `document_bound_to_plan` |
| N14 | Reversal of a rewritten document | `operation_rewritten_by_epoch`; DB backstop |
| N15 | A plan on an epoch closure | `plan_on_epoch_position` |
| N16 | Ordinary back-dated posting on every path after the migration | existing codes unchanged |
| N17 | Before/after catalog: `pg_get_functiondef`, `pg_get_constraintdef` and `pg_get_triggerdef` of every existing object byte-identical; no trigger disabled (`pg_trigger.tgenabled`) | identical |
| N18 | Trial restore of a dump with executed epochs | identical catalog and data; the schema gate accepts it |
| N19 | Execution without its business audit event in the same transaction | refused at commit |
| N20 | Member adopting an operation that already has entries, or one referenced by an obligation or settlement | refused |
| N21 | Re-executing an executed plan | returns the existing execution and writes nothing |
| N22 | N1 repeated for each entry kind: receipt, issue, transfer out and in, and reversal | refused |
| N23 | The application path still answers `backdated_not_supported` before the database guard for ordinary requests | unchanged code |
| N24 | Approval whose fingerprints differ from the plan's | refused |
| N25 | (only with 3.6) direct runtime-role back-dated insert outside the definer; a `search_path` hijack attempt; EXECUTE not granted to PUBLIC | refused / not possible |

**Positive and invariant tests:**
* E1 (132), E2 (120) and E6 (156) executed end to end;
* pre-existing rows hash-identical (operations, entries, consumptions, journals);
* `ReconcileAsync` empty;
* the probe reconciles after the drafts post and blocks while they are pending;
* rollback on an injected failure at each step of section 5;
* fresh and upgrade migration; runtime-role grants;
* masking and SoD;
* FIFO and average;
* a two-warehouse transfer chain;
* single-period and two-period compensation;
* the ordinary path writes the entry date (3.5).

## 9. Owner decisions (from the merged resume review; recorded once, not re-asked)

| ID | Decision | Status |
|---|---|---|
| Q1 | X1-A in place of X1 as written (sections 2-3, 5) | OPEN; everything marked [P-Q1] waits for it |
| Q2 | X2-X8 as one package with the X3/X7 additions (sections 2, 4) | OPEN; everything marked [P-Q2] waits for it |
| X8 / OD-12 | Trial counting | PENDING; X8 also depends on OD-12 |
| Q3 | Revaluation contract (owner: WITHIN 3.8D scope, separately reviewed accounting contract) | PENDING; section 11 states the layer-preservation requirement |
| Q4 | Method-conversion safeguard exception | stays UNIMPLEMENTED until independently reviewed and approved; not part of this design |
| Q5 | Migration-guard hardening (allow-pattern) | separate security workstream, needs protected-file authorization; referenced only. This design complies by construction (N17) |

Until Q1 and Q2 are decided, the planner stays read-only and the barrier stays active. Technical choices the agent makes without asking: table layout, key derivation, lock order within the rules above, isolation level, reconcile checks, error codes, plan-status triggers.

## 10. Questions for the design review of revision 3

1. Is the "no entries yet, not referenced by an obligation or settlement" condition (3.2) a sufficient proof that a member operation was created in this transaction, given `r_d621465c`?
2. Is the audit binding of 3.4 sufficient without changing the business audit table? The audit row has no transaction column, so the binding rests on the execution id being generated in this transaction, the audit target reference and the execution's `xid8`. Can a runtime-role writer pre-insert an audit row for a guessed future execution id? (The id is a fresh random UUID, and audit rows are key-authenticated by the application.)
3. Do the storno and rebook orderings of section 2 cover transfer chains in both directions with equal dates?
4. Is the plan-scoped hook in the normal posting path (section 5 step 6) narrow enough: no new flag, only the member row of this transaction's execution?

## 11. Revaluation: requirements for its separate contract (owner: within 3.8D scope; contract PENDING)

This design does not define revaluation; its accounting contract is separately reviewed (D-045, Q3). The contract MUST satisfy:
* **No silent layer collapse.** A FIFO revaluation must never consume all layers and re-create one layer, and must never discard layer history.
* **Value-only entries are impossible** (`k_0b3e9de8`: quantity > 0), so any representation must use quantity-bearing entries or a new additive structure. The layer-preserving approach to evaluate: **one out/in pair PER LAYER**, typed by the epoch membership table with a new revaluation role:
  * the out entry consumes exactly that layer's remaining quantity at its remaining value;
  * the in entry creates its successor layer at the revalued amount;
  * a lineage row links the old layer to the new one, keeping the original receipt date and the original arrival order as lineage data;
  * FIFO consumption order is preserved by creating the successors in the original layer order.
* **The cost of that approach is stated:** new arrival ranks and layer ids (the original layers stay immutable and fully traceable through the lineage), and as many entries as layers. If the contract finds this insufficient (for example, original arrival ranks must stay authoritative), it must propose a different additive structure, such as a per-layer value-adjustment table read by the engine. That would be a reader change subject to its own review.
* **Moving average:** one out/in pair at the carrying quantity.
* **Accounting:** counter accounts, write-down only or also write-up, approval, and period and date rules are for the contract, not for this design.
* **Same protections as epochs:** the same entry guard, the same same-transaction binding, the audit binding and the before/after catalog test. No existing safeguard is changed.

