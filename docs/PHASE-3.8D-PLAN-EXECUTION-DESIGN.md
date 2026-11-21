# Phase 3.8D plan storage, approval and atomic execution (design proposal, revision 3)

Status: **design revision 3; nothing is implemented.** The owner decided Q1-Q5 on 2026-11-21 (section 9).
* **Q1 (X1-A) and Q2 (X2-X8):** APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision. Authorization becomes effective only after Agent G, or another qualified independent reviewer, confirms this final design revision with no unresolved Critical, High or Medium finding.
* **Before execution is enabled:** the full PostgreSQL safety, concurrency, rollback, audit, restore and migration tests are required, including the revised N1-N29 matrix (section 8).
* **Deviations:** any material deviation from X1-A needs new owner authorization.

Until then:
* no migration, no executor and no financial execution path is written;
* the planner `LoadCompensationPlanAsync` stays read-only;
* the application barrier `backdated_not_supported` stays active;
* nothing in this document changes code, a migration, a trigger, a CHECK or any other safeguard.

Markers: **[A-Q1]** = X1-A and **[A-Q2]** = X2-X8, each APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision. X8 is also reconciled with OD-12/T-2 (section 9).

### Mandatory safeguards of Q1, recorded verbatim as binding (owner, 2026-11-21)

* preserve all existing CHECKs, triggers and immutability; never relax or replace an existing safeguard;
* an entry-level guard refusing unauthorized back-dated movements;
* the replay exception only for an operation bound, in the same DB transaction, to the single execution of an approved plan;
* every replayed operation and entry bound to the plan's approved item/warehouse closure;
* segregation of duties and Open fiscal periods enforced;
* no unauthorized reversals of original, event, storno and rebook operations;
* transactional consistency and an auditable execution record;
* never a client-controlled session flag or a general back-dating bypass;
* historical accounting records and traceability preserved.

Where each one is met: 3.7 and N17 (existing safeguards); 3.3(1) and N1/N22 (entry guard); 3.1, 3.2 and 3.3(1) (same-transaction binding, N2/N8); 3.1 `CompensationPlanPosition` with the insert-only rule and `closure_hash`, 3.2, 3.3(2, 2a) and 3.4 (approved closure; N26, N32); 3.1, 3.3(3) and section 4 (SoD and Open periods; N3, N10, N28); 3.3(4) and section 6 (reversals; N27); section 5 and 3.4 (transactional consistency and audit; N19, N31); 3.3 (no GUC; N12); sections 2 and 3.7 (history preserved; hash-identical originals).

### Other binding owner constraints

* Keep #48 and all of its work.
* #48 is not merged until the complete phase scope, accounting correctness, safeguards, required UI and acceptance criteria are met.
* Adjustments (TM-D14), conversion (Q4) and revaluation (Q3) stay visible in scope.

**History of this document:**
* Revision 1 was reviewed at row level and found not implementable (8 High; `review-records/PR-48-plan-execution-design.md`).
* Revision 2 recorded those corrections and deferred eight safeguard and policy questions (X1-X8) to the owner.
* Revision 3 (this one) adopts the additive X1-A design. That design was written in the resume review, independently cross-checked by Agent G's X1 review, and reviewed in #171 (`review-records/PR-171-phase-3.8d-resume-review.md`). Revision 3 also corrects revision 2's overstatement of the existing database guard and fixes stale text.

Revision 3 was reviewed by Agent G (`review-records/PR-48-plan-execution-design-rev3.md`: 2 Medium, 5 Low, all applied). Its confirmation of this final revision is the remaining condition for implementation authorization.

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

**Typing of epoch rows [A-Q1]. This changes revision 2, which used a new kind 5 or a replaced CHECK; that is infeasible (F3).** Epoch operations keep NATURAL kinds:
* **storno operations:** one per affected original operation, with the original's kind (1/2/3) and `reverses_operation_id` NULL. Their entries carry entry-level `c_4f9e777a` links to the stornoed entries.
* **the event:** its normal posting, with its own normal operation.
* **rebook operations:** one per stornoed original operation, with its natural kind.

Their meaning comes from a new append-only **epoch membership/lineage table** (section 3.2). This is the same pattern as 0020 DB-1, which typed obligations and settlements by a side table instead of widening `k_f356c89a`. The reversed-by mark of the ORIGINAL operations is **not** set, so no historical row is updated (D-044).

**Ordering.** Storno in GLOBAL descending entry id; rebook in GLOBAL ascending (date, original entry id). Transfers couple positions, and average needs "no later live entries", so a wrong order would drive values negative and be refused. FIFO: the event's receipt layer and the rebooked receipt layers take new arrival ranks in replay order, so FIFO consumption follows effective date (example E2 of the transition matrix).

**Entry-level storno.** Only the closure's entries of an operation are stornoed. Entries of other items in the same multi-item operation stay live. The lineage table maps each original entry to its storno entry and its rebook entry. Readers of a posting's cost operation must consult it. In increment 1 they simply refuse (X5/X6).

**Cost-only.** Epoch entries carry NO journal. The general ledger changes only through the plan's compensation journals and the event's own normal journal (PostgreSQL ledger checks E1 132, E2 120, E6 156), so the close probe's cost-ledger vs general-ledger comparison holds after the journals post.

**Dating, X2 [A-Q2].** Storno and rebook entries carry the ORIGINAL entry dates; the event carries its own date. `created_at` keeps the recording time (SRS 9 separates the operation date from the recording date).
* Every epoch date must lie in an **Open** period. Soft Closed refuses, because the stock side has no soft-close override. Final Closed refuses the whole plan.
* Consequence: as-of valuation for any date from the event onward changes after the epoch (open periods only). A report produced before cannot be reproduced afterwards except through the epoch lineage.
* Movement reports must order by sequence or show an epoch marker. This is a UX choice, not a policy (residual risk R1).

## 3. Persistence [A-Q1]

One new **company** migration and one **system** access migration. Both are forward-only and additive: no DROP, no `CREATE OR REPLACE` of an existing function, no `DISABLE TRIGGER`, no ALTER of an existing constraint. Their numbers are the next free ones on `main` at implementation time, allocated in merge order in `docs/INTEGRATION-STATUS.md`. No number is reserved here; the earlier "company 0023 / system 0025" reservation is void. Opaque physical names (D-029), with the logical dictionary in a new `CompanySchema.CompensationPlan.cs`. The runtime role gets INSERT and SELECT only (plus UPDATE of the plan status column), and the completeness guard (#132/#140) covers the new tables.

### 3.1 Plan tables

* **`CompensationPlan`**: id, item, warehouse roots, the proposed event's stock document id and revision, the proposed event payload (canonical JSON), the fingerprints (plan, comparison, evidence, periods, position totals/layer hash, valuation policy, accounting version), status (1 proposed, 2 approved, 3 executed, 4 rejected, 5 cancelled, 6 stale), revision, creator, approver and timestamps. A trigger makes the payload and fingerprints immutable; status moves only along allowed transitions; nothing is ever deleted. **SoD, X3 [A-Q2]:** a trigger refuses an approval when the approver is the creator, the last editor or the submitter of the plan, or the creator, last editor or submitter of the event's stock document. This is the D-066/D-158 pattern, enforced in both the service and PostgreSQL. The approval is bound to the plan revision and fingerprints. **Closure binding at the plan (G's M3 at `ab1d106`):**
  * the plan row has `created_xact xid8`, assigned by a BEFORE INSERT trigger (`pg_current_xact_id()`, never client-supplied);
  * it also has an immutable `closure_hash`: SHA-256 over the canonical, sorted list of (item, warehouse) pairs of its closure, which is part of the plan fingerprint;
  * the status trigger, on the transition to `approved` (and again on `executed`), recomputes the hash from the `CompensationPlanPosition` rows and refuses on any difference, so the approval re-checks the closure.
* **`CompensationPlanPosition`** (rev-3 review M1): the plan's **closure**, i.e. every (item, warehouse) position the replay may touch: the item and warehouse roots plus every downstream position reached through transfers. Written with the plan, immutable, covered by the plan fingerprint, with `UNIQUE (plan, item, warehouse)`. **Insert-only within the plan's creating transaction (G's M3 at `ab1d106`; both protections are adopted):**
  1. a BEFORE INSERT trigger accepts a closure row only while its plan is in status `proposed` AND the plan's `created_xact = pg_current_xact_id()`. A row added after approval, or in any later transaction (even while the plan is still proposed), is refused; UPDATE, DELETE and TRUNCATE are refused as before (N5);
  2. the plan's immutable `closure_hash` (above) is recomputed and compared at approval and at execution, and in the deferred execution check (3.4).

  A new closure set therefore requires a new plan and a new approval.
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
  * for roles 1 and 3, the original operation is not itself an epoch member (no epoch on an epoch, X5), and every entry of the original operation that will be stornoed lies on a position in the plan's closure (`CompensationPlanPosition`; rev-3 review M1);
  * the operation's kind is not 4 and its `reverses_operation_id` is NULL. Epoch operations are never reversal operations (rev-3 review L1).
* **`EpochEntryLineage`**: original entry → storno entry → rebook entry (each unique), with the execution id. Append-only. Insert is allowed only when all three entries belong to members of the same execution.

### 3.3 New entry-level guard (tightening) [A-Q1]

A NEW BEFORE INSERT trigger on `t_bc727c1d`. It is a new function; no existing function is replaced. It reads the position row `FOR UPDATE`; the engine already holds that lock, so no new lock order arises (Agent G option (a)). It reads only table rows, **never a GUC or session variable**.
1. If `NEW.c_12665ffb` < the position's current `c_3c880b93`, **refuse unless** `NEW.c_eb34b7d8` has an `EpochMember` row whose execution has `created_xact = pg_current_xact_id()`. The execution's plan must also be Approved, with an approval whose approver is not the creator and whose fingerprints equal the plan's. This closes F1 for every ordinary path. G's assessment: the new guard could also ship as its own strengthening slice, independent of 3.8D (owner choice).
2. If `NEW.c_4f9e777a` is set and the operation's kind is not 4, refuse unless the operation is an epoch member with role 1 (storno) **and the reversed entry belongs to the same position as `NEW`** (rev-3 review M1).
2a. **Closure binding (rev-3 review M1):** for any entry of an epoch member, back-dated or not, the entry's (item, warehouse) must be a position in the closure of the member's plan (`CompensationPlanPosition`). Inside one executing transaction, a member operation can therefore never touch an unrelated position, whether through an executor bug or a forged member row.
3. For any entry of an epoch member:
   * the entry date must lie in an **Open** fiscal period (Soft Closed, Final Closed or missing refuses). The guard reads the period row **`FOR SHARE`** (rev-3 review L2), so a concurrent close cannot commit between this check and the commit. The rule does not depend on the executor's step-2 locks. Lock order stays positions, then periods, as in section 5. This gives epoch rows the database period guard that the open owner item STOCK-PERIOD-DB proposes for all stock effects;
   * the position must hold no open obligation (TM-D10/TM-D13a);
   * the position must have no epoch member of ANOTHER plan (X5 as a database rule).
4. **X6 as a database rule (rev-3 review M2):** a NEW BEFORE INSERT trigger on the operation table refuses a kind-4 reversal whose reversed operation **is an epoch member (any role: storno, the event's own operation, rebook) OR has stornoed entries** (an epoch lineage row). This covers reversal of the original documents, of the back-dated event's own document, and of a rebook operation by key. This is a new trigger; `f_9c827423` is unchanged.

### 3.4 Deferred checks (at commit) [A-Q1]

A deferred constraint trigger on `CompensationPlanExecution` (and on `EpochMember` for orphan detection) checks:
* every storno member has exactly one rebook member for the same original operation, and every storno entry has its lineage row;
* the execution has at least one member, and exactly one event member whose stock posting is the execution's posting;
* the plan is in status `executed`, set in this transaction;
* the plan's `closure_hash` equals the hash recomputed from its `CompensationPlanPosition` rows (G's M3; a second, independent line of defence next to the insert-only rule);
* **the plan's business audit event exists in `t_2db8cf1f` (0002, append-only, hash-chained) and was written in the same transaction** (resume review L5). The audit table (0002) has no transaction column and is not altered. The binding works like this:
  * the execution id is generated by the executor in this transaction;
  * the audit event carries the execution id as its target reference (one of the uuid reference columns of `t_2db8cf1f`), and the execution row stores the audit event id, with **`UNIQUE (audit_event_id)`** (rev-3 review L3), so one audit event can never vouch for two executions;
  * the deferred check requires that the referenced audit row exists, that its **event type is the plan-executed type and its subject is this plan** (rev-3 review L3), and that its target is this execution, whose `created_xact` equals the current transaction.

  Without it, the commit fails. Whether this is sufficient against a pre-chosen id is review question 2 in section 10;
* no epoch member exists without an execution of this transaction, and every member operation has at least one entry.

### 3.5 Executor contract (application) [A-Q1]

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

## 4. Approval [A-Q2]

* **Creation:** a user with the new plan permission creates the plan. The event's stock document must already exist and be **approved through the normal document approval** before the plan can be approved (X7).
* **Approval:** a DIFFERENT user with the new approve permission approves it. That user is never the creator, last editor or submitter of the plan or of the event document (X3 addition). The approver sees the exact lines, fingerprints and evidence summary.
* **Execution:** the executor differs from the creator; the approver may execute (X3).
* **Staleness:** approval re-runs the planner under the APPROVER's permissions. Approvals are unique per (plan, revision, fingerprints). Any change makes the plan `stale` and requires a new plan and approval. Rejection and cancellation are events.
* **Permissions (X4):**
  * New branch- and warehouse-scoped codes for plan, approve and execute.
  * Approve and execute are classified **privileged** under D-096.
  * A delegated "Inventory Cost Approver" preset is granted offline (D-066 pattern), not part of Company Administrator. No existing role is widened.
  * The codes are added to the endpoint permission matrix (#119).
* **Plan-bound document, X7 addition [A-Q2]:** the normal posting path refuses a stock document referenced by a plan in status proposed or approved (`document_bound_to_plan`). It may post only through the plan's execution. Cancelling or rejecting the plan releases the document.
* **Trial limit (X8, approved):** the original posted business stock document of the event counts once. Internal replay operations and auto-generated compensation entries consume no additional Trial transactions. The reconciliation with OD-12/T-2 is being done by the docs agent; this design does not decide it.

## 5. Atomic execution [A-Q1, A-Q2]

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
* `operation_rewritten_by_epoch` (X6, rev-3 review M2): reversal of a document whose cost operation **is an epoch member (storno, event or rebook) or has stornoed entries** is refused in the service. This includes the back-dated event's own stock document, and a reversal of a rebook operation by key. The database rule is 3.3(4); `k_c8fdc6c0` remains an additional backstop for stornoed entries.
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
| N26 | (rev-3 M1) A member operation whose entry lies on a position outside the plan's closure; a storno entry whose reversed entry belongs to another position; a member whose original operation's stornoed entries are outside the closure | refused |
| N27 | (rev-3 M2) Reversal of the back-dated event's own stock document; reversal of a rebook operation by key; reversal of an original rewritten by the epoch | `operation_rewritten_by_epoch` in the service, and the database rule 3.3(4) refuses a direct kind-4 insert |
| N28 | (rev-3 L2) A period close racing an execution with epoch entries in that period | exactly one wins: either the close waits and then refuses because of the epoch, or the execution refuses `period_not_open`; never an epoch entry in a closed period |
| N29 | (rev-3 L4) Two-warehouse transfer chain in both directions (W1→W2 and W2→W1) with equal dates, executed | storno and rebook keep same-date legs in their original sequence; positions, layers and values equal the replay; `ReconcileAsync` empty |
| N30 | (rev-3 L1) Member row for a kind-4 operation, or for an operation with `reverses_operation_id` set | refused |
| N31 | (rev-3 L3) Two executions referencing one audit event; an audit event of another type or another plan | refused |
| N32 | (G's M3 at `ab1d106`) A closure row inserted after approval; a closure row inserted in a later transaction while the plan is still proposed; an entry, member or storno on such an added position; an approval or execution whose recomputed closure hash differs | all refused |

**Numbering reconciliation (owner's "revised N1-N29 matrix"):** N1-N25 are unchanged. N26 = closure binding (M1), N27 = reversal of any epoch operation (M2), N28 = concurrent period close (L2), N29 = two-way equal-date transfer chain (L4). These complete N1-N29. N30 (member kind-4 refusal, L1), N31 (audit uniqueness and type, L3) and N32 (closure immutability, G's M3) are additional mandatory tests. An earlier draft of this fix numbered L1/L2/L3 as N28/N29/N30; this table supersedes it.

**Positive and invariant tests:**
* E1 (132), E2 (120) and E6 (156) executed end to end;
* pre-existing rows hash-identical (operations, entries, consumptions, journals);
* `ReconcileAsync` empty;
* the probe reconciles after the drafts post and blocks while they are pending;
* rollback on an injected failure at each step of section 5;
* fresh and upgrade migration; runtime-role grants;
* masking and SoD;
* FIFO and average;
* a two-warehouse transfer chain in both directions with equal dates (N29, rev-3 review L4);
* single-period and two-period compensation;
* the ordinary path writes the entry date (3.5).

## 9. Owner decisions (decided 2026-11-21; recorded once, not re-asked)

| ID | Decision | Status |
|---|---|---|
| Q1 | X1-A (sections 2-3, 5), with the mandatory safeguards recorded verbatim in the header | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision. Before execution: the full PostgreSQL safety, concurrency, rollback, audit, restore and migration tests, including N1-N29. A material deviation needs new owner authorization |
| Q2 / X2 | preserve the original effective dates; replay only in Open periods; reject Soft Closed and Final Closed | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X3 | the approver is never the creator, last editor or submitter of the plan or of the related stock document; execution follows the approved separation of duties | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X4 | dedicated scoped permissions and an Inventory Cost Approver role; no automatic widening of existing roles | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X5 | refuse a second replay plan on a position already affected by an executed epoch | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X6 | refuse unsupported reversals affecting an executed epoch, including original, event and rebook operations | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X7 | the source stock document follows normal approval; a document bound to a pending plan cannot be posted through normal posting | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision |
| Q2 / X8 | the original posted business stock document is counted once for Trial; internal replay operations and auto-generated compensation entries consume no additional Trial transactions | APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision. **To be reconciled with OD-12/T-2:** the docs agent is doing that reconciliation; this design references it and does not decide it |
| Q3 | revaluation stays in 3.8D scope, under a separate accounting contract (section 11 lists its REQUIRED contents) | APPROVED in scope by the owner 2026-11-21. The contract needs independent accounting review and explicit owner approval before any financial execution. It does not block the ordinary replay increment |
| Q4 | conversion design work approved under D-044/D-045 | the method-immutability guard is NOT changed until an independent review and explicit authorization |
| Q5 | migration-guard hardening | APPROVED as a separate security workstream handled by another agent; referenced only. This design complies by construction (N17) |

Technical choices the agent makes without asking: table layout, key derivation, lock order within the rules above, isolation level, reconcile checks, error codes, plan-status triggers.

## 10. Questions for the design review of revision 3

The revision-3 review (`review-records/PR-48-plan-execution-design-rev3.md`) answered them: question 1 sufficient with the L1 addition; question 2 with L3; question 3 by test L4; question 4 narrow enough. Its findings M1, M2 and L1-L5 are applied in sections 3.1-3.4, 6, 8 and 11.

1. Is the "no entries yet, not referenced by an obligation or settlement" condition (3.2) a sufficient proof that a member operation was created in this transaction, given `r_d621465c`?
2. Is the audit binding of 3.4 sufficient without changing the business audit table? The audit row has no transaction column, so the binding rests on the execution id being generated in this transaction, the audit target reference and the execution's `xid8`. Can a runtime-role writer pre-insert an audit row for a guessed future execution id? (The id is a fresh random UUID, and audit rows are key-authenticated by the application.)
3. Do the storno and rebook orderings of section 2 cover transfer chains in both directions with equal dates?
4. Is the plan-scoped hook in the normal posting path (section 5 step 6) narrow enough: no new flag, only the member row of this transaction's execution?

## 11. Revaluation: REQUIRED contents of the separate accounting contract (owner Q3, 2026-11-21)

Revaluation stays within the Phase 3.8D scope. It needs a separate accounting contract, with independent accounting review and explicit owner approval before any financial execution. It does not block the ordinary replay increment. **This revision does not write the contract.** The contract MUST contain:

1. **Counter accounts:** which accounts take revaluation differences, per company and item group, and how invalid or inactive accounts are refused.
2. **Direction:** write-down, and whether write-up or reversal of an earlier write-down is permitted, with its limit (for example, not above original cost).
3. **Approval and audit:** independent approval (approver never the creator, last editor or submitter), an auditable record, the same-transaction execution binding and the audit binding of section 3.
4. **FIFO and moving-average behaviour:** exact arithmetic for each method, including partial layers and rounding remainders.
5. **Preservation of original FIFO layer provenance, quantities, ordering and historical dates:** no silent collapse of layers and no loss of history. Value-only entries are impossible (`k_0b3e9de8`). Two technical options are on record (rev-3 review L5), and the contract must choose explicitly:
   * (i) one out/in pair per layer with lineage. It is **count- and order-preserving with lineage** and keeps the original layers immutable, but the successor layers carry the **revaluation date**. So layer identity, arrival rank and the engine-visible receipt date are NOT preserved except through the lineage, and it relies on oldest-first processing. Every FIFO reader (aging, as-of valuation, layer reports, later replay) would then have to read the lineage;
   * (ii) an additive per-layer value-adjustment table read by the engine. It keeps layer identity, rank and date, but is a reader change subject to its own review.
6. **Reporting:** clear reporting of historical receipt dates versus revaluation dates.
7. **Reconciliation:** with journals and valuation (cost ledger vs general ledger, close probe), per period.
8. **Periods and dates:** Open periods only; Final Closed is never touched.
9. **Same database protections as epochs:** no existing safeguard changed; the before/after catalog test.
