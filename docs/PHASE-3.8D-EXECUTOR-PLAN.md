# Phase 3.8D: atomic replay executor, implementation plan

Status: **PLAN ONLY, revision 2 (after the independent review `review-records/PR-216-executor-plan-review.md`: 5 Medium, 3 Low, all addressed; section 11 maps each finding to its place). Nothing in this document is implemented, and no code is written until the plan has been independently reviewed and the lead has released the first slice.** Branch `feature/3-8d-executor`, from main `4b52fd5` (PR 48 merged: the impact preview, the plan-execution database layer, company migration 0036, plan storage and approval services, execution disabled).

Authoritative sources: `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` revision 3 (sections 2, 3.1 to 3.7, 5, 6, 8; the design is the source of truth, this plan only slices it), `docs/PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md` (TM-D1 to TM-D14), D-044, D-045, D-215. This plan invents no accounting rule and no architecture decision; where the design leaves a choice it is listed in section 9 for the reviewer.

**Out of scope, untouched (separate owner contracts):** revaluation (Q3, `PHASE-3.8D-REVALUATION-CONTRACT.md`) and method conversion (Q4, `PHASE-3.8D-CONVERSION-CONTRACT.md`). The method-immutability guard is not changed. Adjustments and stocktakes stay refused (TM-D14).

---

## 1. The owner's mandatory safeguards, verbatim

**Q1 (X1-A), recorded in the design header (owner, 2026-11-21):**

* preserve all existing CHECKs, triggers and immutability; never relax or replace an existing safeguard;
* an entry-level guard refusing unauthorized back-dated movements;
* the replay exception only for an operation bound, in the same DB transaction, to the single execution of an approved plan;
* every replayed operation and entry bound to the plan's approved item/warehouse closure;
* segregation of duties and Open fiscal periods enforced;
* no unauthorized reversals of original, event, storno and rebook operations;
* transactional consistency and an auditable execution record;
* never a client-controlled session flag or a general back-dating bypass;
* historical accounting records and traceability preserved.

**Q2 (X2 to X8), design section 9, APPROVED with mandatory safeguards:**

* **X2:** preserve the original effective dates; replay only in Open periods; reject Soft Closed and Final Closed.
* **X3:** the approver is never the creator, last editor or submitter of the plan or of the related stock document; execution follows the approved separation of duties.
* **X4:** dedicated scoped permissions and an Inventory Cost Approver role; no automatic widening of existing roles.
* **X5:** refuse a second replay plan on a position already affected by an executed epoch.
* **X6:** refuse unsupported reversals affecting an executed epoch, including original, event and rebook operations.
* **X7:** the source stock document follows normal approval; a document bound to a pending plan cannot be posted through normal posting.
* **X8 (D-215):** the original posted business stock document is counted once for Trial; internal replay operations and auto-generated compensation entries consume no additional Trial transactions.

*Note outside the quotation (not owner wording): the Inventory Cost Approver preset is an OPEN owner question; no preset exists, and nothing in this plan creates one.*

**Q3:** revaluation stays in 3.8D scope under a separate accounting contract; it needs independent accounting review and explicit owner approval before any financial execution; it does not block the ordinary replay increment.
**Q4:** conversion design work approved under D-044/D-045; the method-immutability guard is NOT changed until an independent review and explicit authorization.
**Q5:** migration-guard hardening is a separate security workstream; this design complies by construction (N17).

**Q1, before execution:** the full PostgreSQL safety, concurrency, rollback, audit, restore and migration tests, including N1 to N29 (and the additional N30 to N32), must pass.

---

## 2. What exists on main (the baseline every slice builds on)

| Part | State |
|---|---|
| Planner `CostFlowSnapshotService.LoadCompensationPlanAsync` (read-only; history, comparison, classification evidence, compensation lines, fiscal dates, live re-authorization), `CostFlowComparison`, `CostReplay*`, `CompensationCalculator` | merged |
| Database layer, company migration 0036: nine append-only tables, entry/operation/posting guards, deferred completeness checks (3.4), byte-exact closure hash, X5, X6, X7, next-sequence rule and deferred position check | merged, additive, forward-only, reviewed |
| `CompensationPlanService` (storage) and `CompensationPlanApprovalService` (approve, reject, cancel, stale) | merged; internal; **no endpoint, no registration**; permission codes `company.compensationplans.create` and `.approve`, privileged, no preset |
| Per-company runtime role: **SELECT only** on the four execution tables (execution, its journals, epoch members, epoch lineage); INSERT only where the storage/approval services write | merged (review M1); tests `PlanExecutionRuntimeRoleDisabledTests`, `PlanExecutionDisabledTests` |
| Engine barrier `backdated_not_supported` (`InventoryCostingService`, entry date earlier than the position's last date) and the equivalent barriers in negative-stock, stocktake and adjustment paths | **unchanged**, still the only application answer to a back-dated request |
| Database N1 to N32 tests, written against a TEST-ONLY INSERT grant (`PrepareRuntime(executorGrant: true)`) | merged |

So today: the database accepts an epoch only from a writer holding INSERT on the four tables, and nothing in the application writes one.

---

## 3. What the executor is (design section 5, mapped to the code seams)

One company transaction, READ COMMITTED with locks first, all or nothing:

1. **Idempotency** (N21): an execution row exists for the plan: return it, write nothing.
2. **Locks, ALL of them, up front** (section 6 gives the inventory and why): the company posting/structure advisory lock (`CompanyBranchService.LockKey1/2`); the positions of the closure by (warehouse, item) as `LockPositionsAsync`; the plan row; then, in one fixed order, every further row lock that steps 5 to 8 and the compensation journals take (documents, warehouses, items, units, journal numbering counters, accounts, branches, periods `FOR SHARE`). After step 2 no step takes a blocking lock; the audit stream lock is taken by the first audit append and stays the last lock (section 6).
3. **Re-plan** on this same connection and transaction under the EXECUTOR's permissions (the planner overload of section 4, E3, contract in L3); every fingerprint and the closure hash must equal the approved ones. If not: **roll the execution transaction back first**, and only then mark the plan stale in its own short transaction (revision re-checked there), so the second transaction never waits on a plan row that the first still holds. Stale is not applied for executor-permission failures, cancellation, budget, serialization or transient errors.
4. **Audit and execution row**: execution id, the audit event 1154 (subject = plan, `TargetUserId` = execution id), then the execution row naming the audit event and (deferred) the posting.
5. **Storno** per affected original operation in global descending entry order: storno operation (natural kind, `reverses_operation_id` NULL), its `EpochMember` row (role 1) before its entries, entries with entry-level reversal links.
6. **Event**: posted through the NORMAL posting path inside its own savepoint (`PostInWorkAsync`); the plan-scoped hook inserts the event operation's member row (role 2) before its entries; the position date is written with `GREATEST` (3.5).
7. **Rebook** in global ascending (date, original entry id) order: rebook operations, members (role 3), entries with the replayed amounts, lineage rows.
8. **Verify** position totals and layers against the replay's proposed final state (mismatch rolls everything back); create the compensation journals as DRAFT journals under the existing journal approval policy (TM-D9); mark the plan `executed`; the deferred checks of 3.4 run at commit.

The executor is the only code that may write the four execution tables and the only code that may obtain the barrier exception; there is no setting, flag or parameter that does either (Q1).

---

## 4. Slices (each about 1 to 2 days, independently reviewable, in dependency order)

Legend: **[X]** = this executor workstream. **[CI]** = depends on the **compensation and accounting integration** workstream. **[P]** = depends on the **close-probe extension and compensation repair** workstream. Every slice is a Draft PR from current main; it merges only after its independent review and exact-head CI. **Nothing is enabled before E5, and the runtime INSERT grant does not ship even in E5 (section 7).**

### Changes to EXISTING production paths (all of them, and the treatment each one gets)
Every one of these touches a path that every ordinary posting, reversal or read uses. Each gets the same four proofs in its slice: **(1) an ordinary-path pin** (a test that the ordinary request returns exactly what it returns today: same codes, same rows, same written dates); **(2) the existing suites unchanged** (posting, reversal, costing FIFO and average, transfers, adjustments, stocktake, period close, the costing and snapshot suites; no assertion edited); **(3) mutation proofs** (removing the new condition or the guard fails a named test); **(4) an inertness proof**: the change is guarded so that it acts only for a plan-bound document, an `EpochMember` operation or an `EpochWriteScope`, and a dedicated test runs the ordinary scenario with the plan and epoch tables empty and shows identical results and rows.

| # | Existing code changed | Slice | What changes | Guard that makes it inert |
|---|---|---|---|---|
| 1 | engine barrier `backdated_not_supported` (`InventoryCostingService`) and the position write | E2a | an earlier date is accepted only under an `EpochWriteScope` AND when the database condition holds for that entry | scope absent: no new code runs; scope present without a member row of this transaction: the barrier answers as today |
| 2 | position `last_effective_date` write (`GREATEST`, design 3.5) | E2a | the written date is `GREATEST(old, entry date)` | equals the entry date on every ordinary path (pinned); `f_589a0a19` untouched (N6) |
| 3 | `PostInWorkAsync` (normal posting function): the plan-scoped member hook for the event | E2b | inserts the event operation's member row before its entries, only when an `EpochWriteScope` is passed in | parameter absent on every ordinary call: no code runs |
| 4 | normal posting service: `document_bound_to_plan` answer (N13) | E2c | refuses a document bound to a proposed or approved plan before the database does | one indexed lookup of the live-plan index by document; the plan table is empty until a plan exists |
| 5 | reversal service: `operation_rewritten_by_epoch` answer (N14, N27) | E2c | refuses the reversal of an operation that is an epoch member or has stornoed entries, before the database rule | one indexed lookup of `EpochMember` and of the lineage table; empty until an epoch exists |
| 6 | snapshot readers (`CostHistorySnapshot`, `CostFlowSnapshot`, `ClassificationEvidence` readers): refuse histories that contain epoch members (design section 6, X5/X6 for readers) | E2c | `plan_on_epoch_position` / epoch-history refusal | one indexed lookup of `EpochMember` per position set; empty until an epoch exists |

### E1. Replay derivation (pure, no database write) [X] (about 1 day)
`EpochSchedule`: from the planner result (comparison changes, proposed steps, revised trace, closure) derive the exact ordered list of epoch operations: storno entries in global descending entry id, the event, rebook entries in global ascending (date, original entry id), with the expected rebook amount per entry, the expected final position and layer state, and the lineage map (original to storno to rebook). Transfers are paired across positions, equal-date legs keep their original sequence (N29). Refuses what section 6 of the design refuses (obligations, settlements, reversals in history, adjustments, mixed provenance, unposted journals, closed periods, closure with an executed plan).
* **Tests:** unit and property tests only: the schedule reproduces `CostReplay`'s final state for FIFO and average, single- and two-warehouse chains, both directions with equal dates (N29 derivation), every refusal of section 6. No database, no grant. **Changes no existing code.**

### E2a. The guarded exception in the engine [X] (about 1.5 days; existing-path changes 1 and 2)
**Specification (the scope alone never opens anything):**
* **The database condition is re-read per entry.** Even when an `EpochWriteScope` is present, for every entry whose date is earlier than the position's last date the engine executes the database check "the entry's operation is an `EpochMember` of an execution whose `created_xact` is THIS transaction" (the same condition the 0036 guard enforces, one indexed query by operation id). No member row of this transaction: the barrier answers `backdated_not_supported` exactly as today. The scope is a statement of intent, never evidence.
* **The scope is unforgeable by construction.** `EpochWriteScope` is a sealed class with a **private constructor**; the only way to obtain one is `EpochWriteScopeFactory.Create(CompanyAuditedWork work, Guid executionId)`, a method of the executor's own assembly part, bound to ONE `CompanyAuditedWork` (one transaction, one connection) and to one execution id. The scope carries that transaction; the engine compares it with the transaction of every call and refuses any other (`epoch_scope_wrong_transaction`). The scope cannot be stored, cached or passed to another unit of work.
* **Only the executor may call the factory.** Enforced by test (below), not by convention: `internal` visibility alone would let any code in the Infrastructure assembly and every `InternalsVisibleTo` assembly call it.
* The position's last date is written with `GREATEST(old, entry date)` (3.5); `f_589a0a19` stays unrelaxed (N6). The other barriers (negative stock, stocktake, adjustments) are not changed (TM-D14, TM-D13a).
* **Tests (E2a):** N6, N12, N16, N22, N23 (with the executor-level variant: the application still answers `backdated_not_supported` before the database guard for every ordinary request); the ordinary-path pin (the written date equals the entry date; a unit test); **a scope without a member row of this transaction is refused with `backdated_not_supported`**; **a scope created for another transaction is refused** (`epoch_scope_wrong_transaction`); a scope used after its transaction ended is refused; **an IL scan (token based, in the style of the audit-constant scan of `PlanExecutionDisabledTests`, with a positive control) shows that only `CompensationPlanExecutor` (and its nested types) references the factory and the scope constructor**; **mutation proofs: removing the per-entry member check, and removing the transaction binding, each fail a named test**; the inertness test of existing-path changes 1 and 2; `InventoryCostingTests` and every existing costing, posting and period suite unchanged.

### E2b. Entry-level storno and rebook writers and the event hook [X] (about 1.5 days; existing-path change 3)
`EpochWriter` (internal, requires an `EpochWriteScope`): insert operation, then its `EpochMember` row, then entries; storno entry mirrors its original exactly on the original date with the entry-level link (FIFO: restores the layer and consumption rows the original issue consumed, as the existing reversal does; average: the running figures advance by sequence); rebook entry writes the replayed amount; lineage rows. The event hook in `PostInWorkAsync` (plan-scoped member row first, inside the savepoint) takes the scope as an optional parameter.
* **Tests (writer level, under the test-only grant, no orchestration):** N2, N4, N7, N9, N10, N11, N20, N26, N30, the writer part of N27; positions, layers and values equal E1's expectation for FIFO and average; `ReconcileAsync` empty. Existing-path change 3: ordinary-path pin (every `PostInWorkAsync` caller unchanged), the posting suites unchanged, a mutation proof (the hook runs only with a scope), the inertness test.

### E2c. The service answers and reader refusals [X] (about 1 day; existing-path changes 4 to 6)
The normal posting service answers `document_bound_to_plan` (N13), the reversal service answers `operation_rewritten_by_epoch` (N14, N27 service part), and the snapshot readers refuse histories with epoch members, each before the database does. Today only the database (0036) refuses these.
* **Tests:** N13, N14, N27 service part, N15 (the planner refusal `plan_on_epoch_position`), the reader refusals; for each change: ordinary-path pin, the posting, reversal, costing and snapshot suites unchanged, mutation proof (removing the lookup fails the named test while the database backstop still refuses), the inertness test (identical results and rows with empty plan and epoch tables).

### E3. Executor orchestration [X] (about 2 days)
`CompensationPlanExecutor.ExecuteAsync` (internal, **unregistered**, no endpoint): steps 1 to 8 with the lock inventory of section 6, the re-plan, stale marking after rollback, audit 1154, execution row, verification, mark executed, idempotent return; new permission code `company.compensationplans.execute` (catalog, privileged, no preset, no system migration while no preset holds it).
* **The re-plan overload (contract, review L3).** An internal overload of `LoadCompensationPlanAsync` that: runs on the **same connection and transaction** as the executor (it opens no connection); **asserts READ COMMITTED** (refuses another isolation level); loads the executor's permissions **live**; uses the same command budget mechanism; runs **after** the step 2 locks are held; and uses **the same planner core as the approval** (the same code path computes the seven fingerprints and the closure hash, so they are comparable). **Race test:** an ordinary posting on a closure position waits for the execution at the position lock and neither sees nor changes half of an epoch; the re-plan sees the state the locks protect.
* Compensation journals are behind an interface `ICompensationJournalWriter` whose default implementation **refuses** a plan that has compensation lines (`compensation_not_integrated`, fail closed); a plan without lines can execute cost-only. E5 tests that the registered writer is the real one and that the refusing default is registered nowhere in production.
* **Tests:** section 8 (the table of N tests with their proving tests), the rollback matrix of section 8, the structural X8 pins of section 9, the concurrency and lock tests of section 6, fresh and upgrade migration, masking and SoD. **Not enabled:** the runtime role keeps SELECT only on the four tables; the executor runs only under the test-only grant.

### CI. Compensation and accounting integration [CI] (separate workstream; about 1 to 2 days plus its own review)
`CompensationJournalWriter` (the real `ICompensationJournalWriter`): from the approved compensation lines create the compensation journals as DRAFT journals in the same transaction under the existing journal approval policy (TM-D9), bucket per (period, branch, account), **source key = plan id + bucket with a SYSTEM source** so a repeat cannot create a second set (A6, TM-D12) and the journals are identifiable as automatic (X8/D-215), original accounts and original period (D-044, TM-D8(a)); `CompensationPlanExecutionJournal` rows; the plan's lines equal the journals' lines (verified before commit). **Additive company migration M-A** (number to be allocated by the lead; I do not pick one): a deferred check that every compensation-line bucket of the plan has its `CompensationPlanExecutionJournal` row at commit (the database backstop of TM-D9/A6 in the style of 0036), required before enabling per the review's ruling.
* **Tests:** journal balance and accounts valid (TM-D4), dates permitted (TM-D8), fingerprint unchanged, original journals and operations untouched (hash-identical), idempotent source key, rollback with the epoch, the ledger totals of E1 132, E2 120, E6 156 after posting, the close probe's cost-ledger versus general-ledger comparison; **the matching X8 structural pin** (section 9): every compensation journal carries the system source key of its plan and bucket and no user as originator; M-A refuses a commit with a missing bucket row.
* **Depends on:** E3 (the interface) and the existing journal service. **Blocks:** E5.

### P. Close-probe extension, compensation repair and audit-verification block [P] (separate workstream; about 1 to 2 days)
The close probe's failed-journal check is extended to compensation journals; the probe blocks the period while compensation drafts are pending and reconciles after they post; the repair action re-creates a CANCELLED compensation draft (TM-D5). Design section 5 names both as **prerequisites for enabling execution**. **If the owner chooses (a) and (b) of the T2 question (section 7): (b) belongs here** as a second probe: a fiscal period cannot be closed while it contains a replay whose audit event does not verify against the audit key held outside the database; it gets its own test (a replay with a forged audit row blocks the close; a genuine one does not).
* **Tests:** a cancelled compensation draft blocks the close and can be repaired; pending drafts block, posted drafts reconcile; N28 with the extended probe; the audit-verification probe test (if (b) is chosen).
* **Depends on:** CI. **Blocks:** E5.

### E5. Registration and full-matrix verification [X] (about 1 day)
* The executor is registered in the container and `ICompensationJournalWriter` is the real writer (a test pins that the refusing default is registered nowhere in production). **The runtime INSERT grant is NOT part of E5** (section 7).
* **Tests:** the full N1 to N32 matrix, the cost and ledger E tests, the restore drill (N18) and the upgrade migration run with a **test-scoped GRANT, applied by the test to the disposable database, whose privilege list is exactly the future catalog entry** (INSERT on the four tables), so the production behaviour is proven before the grant exists; `PlanExecutionRuntimeRoleDisabledTests` stays as it is (the production catalog still refuses the runtime role); `PlanExecutionDisabledTests` changes only where it names the executor (registration now exists; still no route, preset or endpoint).
* **Depends on:** E3, CI (with M-A), P, and the independent review of the whole path (the combined diff of E1 to E3, CI and P).

### Sequence and parallelism
```
E1 -> E2a -> E2b -> E2c -> E3 ----------\
                          \-> CI -> P ----> E5 (registration, full matrix)
                                              \
                       endpoint slice (later phase) + owner T2 decision --> runtime INSERT grant
```
E1 to E2c are sequential and reviewable one by one. CI and P start once E3's interface is merged and run in parallel with E3's review. Nothing on the E1 to E5 path can execute anything in production: **the grant has not returned and no caller exists.**

---

## 5. What stays disabled, and until when

| Disabled | Until | How it is enforced |
|---|---|---|
| Any write to the four execution tables by the runtime role | the **first real caller (the endpoint slice) AND the owner's T2 decision** (section 7), not E5 | `CompanyRuntimePrivileges` SELECT only; `PlanExecutionRuntimeRoleDisabledTests` (42501) |
| The executor (internal class; registered only in E5; `EpochWriteScope` not constructible elsewhere) | E5 registers it; nothing calls it | `PlanExecutionDisabledTests`, the IL scan of the factory |
| The barrier exception | only under an executor-held `EpochWriteScope`, and only for a member of this transaction's execution (re-read per entry) | E2a tests (member check, transaction binding, mutation proofs), N6, N12, N16, N23 |
| Executing a plan that has compensation lines | E5 (after CI) | `compensation_not_integrated` in E3 |
| Any endpoint, route or UI for plan creation, approval or execution | a later phase | route-table test |
| Any preset holding `create`, `approve` or `execute` (X4 open) | owner decision | catalog and system-database tests |
| Revaluation, method conversion, adjustments and stocktake replay, reversal replay (M6), obligations and settlements | their own contracts | refusals of design section 6 |

---

## 6. Lock order and the audit-stream rule (review M5)

`CompanyAuditedWork.AppendAuditAsync` takes the business stream lock immediately and requires it to be the LAST lock of the transaction. The executor appends audit 1154 at step 4 and the event's posting appends its own audit event at step 6, after which rebook, the event's posting locks and the compensation journals would take further locks: an ordinary transaction that holds such a lock and waits for the stream lock would deadlock with the executor.

**Decision: take every lock that steps 5 to 8 and the compensation journals need in step 2, in one fixed order (option A of the review). I do not pre-allocate audit ids and do not change the audit trail (option B).** Justification: the audit trail is the authenticated, hash-chained record, and an executor-only way to allocate event ids in advance would be a change to the most security-critical component, for an advantage (a later stream lock) that locking up front already gives. Cost: the executor holds its locks a little longer, which the closure bound (planner limits) and the single-writer model of N8 accept.

**Lock inventory of step 2 (fixed order; enumerated and pinned in E3 by reading the code paths that take them):** (1) the company posting/structure advisory lock; (2) the closure positions by (warehouse, item); (3) the plan row; (4) every row `PostInWorkAsync` locks for the event's document (document, its warehouses, items and units, in its own order); (5) the compensation journal creation's rows (journal numbering counters if a draft takes a number, accounts and branches `FOR SHARE`); (6) fiscal periods `FOR SHARE`. After the first audit append (step 4) the executor takes no blocking lock: it only inserts new rows into tables whose uniqueness is already protected by a lock held in step 2.

**Tests (E3 and CI):**
* **Deadlock test:** an ordinary posting on a closure position, an ordinary journal creation and an ordinary period close are started against the executor at each of its steps (test hooks pause the executor after step 2, after step 4 and after step 6); every transaction finishes, none ends in 40P01, and exactly the right ones wait.
* **Lock-set test:** the set of non-`RowExclusive` relation locks and tuple locks held by the executor's transaction right after step 4's audit append equals the set held just before commit (nothing blocking is taken after the stream lock).
* The posting's own audit append inside the event savepoint is accounted for: it takes the stream lock a second time (already held), and a rolled-back event savepoint restores the audit state as `CompanyAuditedWork` does (N9).

---

## 7. T2 and the runtime INSERT grant (review M4)

After the INSERT grant returns, any holder of the per-company runtime database credential could write a complete, well-formed back-dated epoch directly (with a raw plan, approval and audit row), because the database compares ids and cannot authenticate the application user. **The grant has no product use until a caller exists.** Therefore:

* E5 registers the executor and proves the full matrix with a test-scoped grant, but **the runtime catalog grant (`CompanyRuntimePrivileges`, applied by `grant-company-runtime`) ships only with the FIRST REAL CALLER (the endpoint slice), together with the owner's decision on T2 below.**
* The SECURITY DEFINER writer of design 3.6 ("M-B") is **not a precondition**: as specified it does not close T2 against a holder of the runtime credential (that role holds EXECUTE on the definer function, and the database cannot authenticate the application user). What a database-only attacker cannot forge is the audit authentication code, whose key lives outside the database. M-B stays an optional design alternative that needs a `search_path` exception to the migration guard under the Q5 security process.

**OPEN, owner (question proposed by the independent review, verbatim):**
> "Before compensation-plan execution is enabled, do you accept that a holder of the company's runtime database password could write a complete, well-formed back-dated replay directly in the database, provided that (a) every execution must carry its audit event, whose authentication code is made with the audit key held outside the database, and (b) a fiscal period cannot be closed while it contains a replay whose audit event does not verify against that key? Or do you require prevention inside the database (the SECURITY DEFINER writer of design 3.6, which needs a `search_path` exception to the migration guard under the Q5 security process), knowing that it does not stop an attacker who controls the application process itself?"

Status: **OPEN, owner.** If (a) and (b) are chosen, (b) goes into slice P with its own test (section 4). If prevention inside the database is chosen, that is a separate security workstream and slice that this plan does not pre-empt.

---

## 8. Tests of design section 8, by slice, with the test that proves each

"Existing" names a test already merged (PostgreSQL, `tests/integration/Hasm.Database.IntegrationTests`); "E3 new" a test written in that slice (class `CompensationPlanExecutorTests` unless stated). N25 is conditional on design 3.6 (not built).

| N | Proving test | First proven / re-run in |
|---|---|---|
| N1, N22 | existing `PlanExecutionGuardTests.N01_*`, `.N22_*` | merged; re-run every slice; extended E2a |
| N2 | existing `PlanExecutionEpochTests.N02_*` | E2b writer level |
| N3 | existing `PlanExecutionPlanTests.N03_*` (database) and `CompensationPlanApprovalTests.N03_*` (service); E3 new: executor refuses the creator and a non-independent user | E3 |
| N4 | existing `PlanExecutionGuardTests.N04_*` | E2b |
| N5 | existing `PlanExecutionPlanTests.N05_*` | merged; re-run |
| N6 | existing `PlanExecutionGuardTests.N06_*`; E2a new: `GREATEST` never lowers | E2a |
| N7 | existing `PlanExecutionGuardTests.N07_*` | E2b |
| N8 | existing `PlanExecutionEpochTests.N08_*`; E3 new: two concurrent executors | E3 |
| N9 | existing `PlanExecutionEpochTests.N09_*` (two tests); E3 new: the real event posting inside `PostInWorkAsync`'s savepoint, rolled back and continued | E2b, E3 |
| N10 | existing `PlanExecutionEpochTests.N10_*` (accepts the stock-period guard's messages explicitly, `RefusedByPeriod`) | E2b |
| N11 | existing `PlanExecutionEpochTests.N11_*` | E2b |
| N12 | existing `PlanExecutionGuardTests.N12_*` | E2a |
| N13 | existing `PlanExecutionEpochTests.N13_*` (database); E2c new: the service answer `document_bound_to_plan` | E2c |
| N14, N27 | existing `PlanExecutionEpochTests.N14_*`, `.N27_*` (two); E2c new: the reversal service answers `operation_rewritten_by_epoch` | E2c |
| N15 | existing `PlanExecutionEpochTests.N15_*`; E2c new: the planner refusal | E2c |
| N16, N23 | existing `PlanExecutionGuardTests.N16_*`, `.N23_*` | E2a, re-run every slice |
| N17 | existing `PlanExecutionCatalogTests.N17_*` (must stay green; E3/CI add objects only through M-A, extending its counts explicitly) | every slice; CI |
| N18 | existing `PlanExecutionCatalogTests.N18_*` (data with a real executed epoch, written by the executor) | E3, E5 |
| N19, N31 | existing `PlanExecutionEpochTests.N19_*`, `.N31_*` | E3 |
| N20 | existing `PlanExecutionEpochTests.N20_*` | E2b |
| N21 | existing `PlanExecutionEpochTests.N21_*` (database); E3 new: the executor returns the existing execution and writes nothing | E3 |
| N24 | existing `PlanExecutionPlanTests.N24_*` and `CompensationPlanApprovalTests.N24_*` (stale per fingerprint) | E3 re-plan |
| N26, N30 | existing `PlanExecutionEpochTests.N26_*`, `.N30_*` | E2b |
| N28 | existing `PlanExecutionCatalogTests.N28_*` (two, database); E3 new: the REAL period-close service racing the executor; CI/P: with the extended probe | E3, P |
| N29 | existing `PlanExecutionEpochTests.N29_*` (SQL level); E1 unit derivation; E3 new: real executor, both directions | E1, E3 |
| N32 | existing `PlanExecutionPlanTests.N32_*` | E3 |
| E1 132, E2 120, E6 156 cost side | E3 new `Executes_E1_E2_E6_on_the_cost_side` (positions, layers, values, originals hash-identical, `ReconcileAsync` empty) | E3 |
| E1/E2/E6 ledger side, drafts post, probe reconciles and blocks while pending | CI new, P new | CI, P |
| Fresh and upgrade migration; masking and SoD; FIFO and average; single- and two-period | E3 new; upgrade through `PlanExecutionMigrationCompatibilityTests` (existing) | E3, CI (M-A) |

**Rollback matrix (E3, one injected failure per row; each must leave every table byte-identical and the plan still approved, except where stated):** the eight points of section 5 of the design (after step 1 to after step 8) **and** the eight the review added: (a) between an epoch member row and its entries; (b) inside the event savepoint, rolled back and continued (N9); (c) mid-storno (after some storno entries); (d) mid-rebook; (e) a verification mismatch (step 8); (f) a failure of the compensation journal writer (CI); (g) a failure at commit (a deferred check refuses); (h) cancellation of the request; and (i) a failure of the stale-marking transaction after the rollback (the plan stays as it was, the caller still gets `plan_stale`).

---

## 9. Trial counting, D-215 and X8 (review M3)

There is no counting path to test yet: `TrialEntitlementPolicy` has `posted_business_transactions` `NotEnforced` ("No posting module exists yet"; Phase 4.2 builds it). So E3 and CI do not test a counter; they **pin the structural facts a counter will rely on:**
* **E3:** the event's document has **exactly one stock posting** (the execution's), the one the normal posting path creates; **epoch operations (storno and rebook) carry no document and no posting of their own**; the execution creates no other stock posting.
* **CI:** every compensation journal carries a **system source key** (plan id + bucket) and no user as originator, so it is identifiable as automatic.

**Binding requirement for Phase 4.2 (recorded here and carried into `docs/INTEGRATION-STATUS.md` and the Phase 4.2 entry of `docs/ROADMAP.md` by the E3 slice; quoted, not restated).** D-215, owner text (verbatim): *"APPROVED: Count each unique, user-originated posted business transaction once at its first successful posting. Draft, submitted and unposted documents do not count. Internal replay epochs, automatic compensation journals, system-generated accounting entries and internal technical operations do not count separately. Cancellation does not refund an already consumed Trial transaction. A separate, user-originated posted reversal or adjustment counts if it is itself an independent business transaction. Enforce the 100-transaction limit atomically under concurrency. Never double count the same transaction through its stock, invoice and ledger effects. Keep this consistent with the previously approved Phase 3.8D X8 decision."* X8 (design section 9, verbatim): *"the original posted business stock document is counted once for Trial; internal replay operations and auto-generated compensation entries consume no additional Trial transactions"*. Phase 4.2's counter must carry its own test that a replay epoch and its compensation journals add nothing to the count.

---

## 10. Migrations

* **E1 to E3, E5, P: no migration.** The nine tables, the guards, the deferred checks, the closure hash, X5 to X7 and the next-sequence rule are company migration 0036, merged. The permission code `execute` and (later) the grant are catalog changes; the engine scope, writers, hooks, service answers, executor, probes are application code.
* **CI: one additive company migration, M-A** (the deferred compensation-bucket completeness check, section 4): **number to be allocated by the lead; I do not pick one.** It must keep N17 green (the test's counts are extended explicitly) and is subject to the migration guard.
* **M-B (design 3.6 definer function): not planned**, not a precondition (section 7); only if the owner chooses prevention inside the database.

---

## 11. Review response, and choices still open

| Finding | Addressed in |
|---|---|
| M1 scope specification and proofs | section 4, E2a |
| M2 four further existing-path changes (here six items including the position write) | section 4 table, E2a to E2c |
| M3 structural X8 pins and the Phase 4.2 binding requirement | section 9, E3, CI |
| M4 grant only with the first caller and the owner's T2 decision; owner question verbatim, OPEN; M-B dropped as precondition; (b) in P | sections 4 (E5, P), 5, 7 |
| M5 lock order against the audit-stream rule | section 6 (option A, justified, with tests) |
| L1 proving test per N, and the missing rollback points | section 8 |
| L2 roll back first, then mark stale | section 3 step 3, rollback matrix (i) |
| L3 planner overload contract and race test | section 4, E3 |
| X4 note outside the verbatim text | section 1 |

Choices still left to the reviewer: whether E2c should be split further (the three existing-path changes are small and share one proof pattern, so I keep one PR), and whether the lock inventory of step 2 is complete (it is enumerated in E3 by reading the journal and posting paths, and pinned by the lock-set test).

---

## 12. Process

Each slice: its own branch from current main, Draft PR, targeted tests through the CPU gate, one full local PostgreSQL suite on the final head, hosted CI on the exact head, an independent review (separate agent, full diff against main, recorded in `docs/review-records/`) with no open Critical or High finding, then merge under the autonomous merge policy. This plan is reviewed before any implementation starts.
