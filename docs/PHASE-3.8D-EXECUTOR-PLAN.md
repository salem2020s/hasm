# Phase 3.8D: atomic replay executor, implementation plan

Status: **PLAN ONLY. Nothing in this document is implemented, and no code is written until the plan has been independently reviewed and the lead has released the first slice.** Branch `feature/3-8d-executor`, from main `4b52fd5` (PR 48 merged: the impact preview, the plan-execution database layer, company migration 0036, plan storage and approval services, execution disabled).

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
* **X4:** dedicated scoped permissions and an Inventory Cost Approver role; no automatic widening of existing roles. (Open owner question: no preset exists; nothing here creates one.)
* **X5:** refuse a second replay plan on a position already affected by an executed epoch.
* **X6:** refuse unsupported reversals affecting an executed epoch, including original, event and rebook operations.
* **X7:** the source stock document follows normal approval; a document bound to a pending plan cannot be posted through normal posting.
* **X8 (D-215):** the original posted business stock document is counted once for Trial; internal replay operations and auto-generated compensation entries consume no additional Trial transactions.

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
2. **Locks**: company posting/structure advisory lock (`CompanyBranchService.LockKey1/2`), then positions by (warehouse, item) as `LockPositionsAsync`, then the plan row; `FOR SHARE` on every journal, period and account row the plan depends on. Same order as posting and reversal (the database guards take position then period).
3. **Re-plan** on this connection under the EXECUTOR's permissions (`LoadCompensationPlanAsync` variant that takes an open transaction); every fingerprint and the closure hash must equal the approved ones, else the plan is marked stale in its own transaction and the executor stops (not for permission, cancellation, budget, serialization or transient errors).
4. **Audit and execution row**: execution id, the audit event 1154 (subject = plan, `TargetUserId` = execution id), then the execution row naming the audit event and (deferred) the posting.
5. **Storno** per affected original operation in global descending entry order: storno operation (natural kind, `reverses_operation_id` NULL), its `EpochMember` row (role 1) before its entries, entries with entry-level reversal links.
6. **Event**: posted through the NORMAL posting path inside its own savepoint (`PostInWorkAsync`); the plan-scoped hook inserts the event operation's member row (role 2) before its entries; the position date is written with `GREATEST` (3.5).
7. **Rebook** in global ascending (date, original entry id) order: rebook operations, members (role 3), entries with the replayed amounts, lineage rows.
8. **Verify** position totals and layers against the replay's proposed final state (mismatch rolls everything back); create the compensation journals as DRAFT journals under the existing journal approval policy (TM-D9); mark the plan `executed`; the deferred checks of 3.4 run at commit.

The executor is the only code that may write the four execution tables, and the only code that may enter the barrier exception; there is no setting, flag or parameter that does either (Q1).

---

## 4. Slices (each about 1 to 2 days, independently reviewable, in dependency order)

Legend: **[X]** = this executor workstream. **[CI]** = depends on the **compensation and accounting integration** workstream. **[P]** = depends on the **close-probe extension and compensation repair** workstream. Every slice is a Draft PR on its own branch from main; a slice merges only after its independent review and exact-head CI; nothing is enabled before slice E5.

### E1. Replay derivation (pure, no database write) [X] (about 1 day)
`EpochSchedule`: from the planner result (comparison changes, proposed steps, revised trace, closure) derive the exact ordered list of epoch operations: storno entries in global descending entry id, the event, rebook entries in global ascending (date, original entry id), with the expected rebook amount per entry, the expected final position and layer state, and the lineage map (original to storno to rebook). Transfers are paired across positions, equal-date legs keep their original sequence (N29). Refuses what section 6 of the design refuses (obligations, settlements, reversals in history, adjustments, mixed provenance, unposted journals, closed periods, closure with an executed plan).
* **Tests:** unit and property tests only: the schedule reproduces `CostReplay`'s final state for FIFO and average, single- and two-warehouse chains, both directions with equal dates (N29 derivation), every refusal of section 6. No database, no grant.
* **Depends on:** nothing new. **Stays disabled:** pure code, no caller.

### E2. Engine epoch seams [X] (two PRs, about 1.5 days each)
**E2a. The guarded exception in the engine.** The one change to existing production code of this workstream, the most reviewed one:
* the barrier at `InventoryCostingService` accepts an earlier date ONLY for an operation that is an `EpochMember` of an execution written in this transaction (the same condition the database enforces), through an internal `EpochWriteScope` that only the executor can construct; the ordinary path is untouched (N16, N23: `backdated_not_supported` still answers first for every ordinary request, on every path);
* the position's last date is written with `GREATEST(old, entry date)` (3.5); `f_589a0a19` stays unrelaxed (N6); a unit test pins that the ordinary path writes the entry date;
* the other barriers (negative stock, stocktake, adjustments) stay as they are (TM-D14, TM-D13a).
* **Tests:** N6, N12, N16, N22, N23 (with the executor-level variant), the ordinary-path pin, `InventoryCostingTests` and the whole existing costing/posting/period suites unchanged.

**E2b. Entry-level storno and rebook writers.** `EpochWriter` (internal, requires an `EpochWriteScope`): insert operation, then its `EpochMember` row, then entries; storno entry mirrors its original exactly on the original date with the entry-level link (FIFO: restores the layer and consumption rows the original issue consumed, as the existing reversal does; average: the running figures advance by sequence); rebook entry writes the replayed amount; lineage rows. Event posting hook in `PostInWorkAsync` (plan-scoped; member row first; inside the savepoint).
* **Tests (writer level, under the test-only grant, no orchestration):** N2, N4, N7, N9 (savepoint accepted; rolled-back savepoint leaves nothing), N10, N11, N20, N26, N27 (service answers `operation_rewritten_by_epoch`), N30; positions, layers and values equal E1's expectation for FIFO and average; `ReconcileAsync` empty.
* **Depends on:** E1, E2a.

### E3. Executor orchestration [X] (about 2 days)
`CompensationPlanExecutor.ExecuteAsync` (internal, **unregistered**, no endpoint): steps 1 to 8 above with the lock order, the re-plan under the executor's permissions, stale marking, audit 1154, execution row, verification, mark executed, idempotent return; new permission code `company.compensationplans.execute` in the catalog, privileged, no preset (a catalog change, no system migration while no preset holds it). Compensation journals are behind an interface `ICompensationJournalWriter` whose default implementation **refuses** any plan that has compensation lines with `compensation_not_integrated` (fail closed); a plan without lines (an effect that books nothing) can execute cost-only.
* **Tests:** N1 to N32 re-run against the real executor under the test-only grant: N3 (SoD, never the creator), N5, N8 (second and concurrent execution: exactly one), N12, N13 (`document_bound_to_plan` service answer), N14, N15, N17, N18 (dump and restore with executed epochs), N19, N21, N24, N28 (the real period-close service racing an execution: exactly one wins), N29, N31, N32; rollback injected at each step of section 5; E1/E2/E6 end to end for the COST side (positions, layers, values, hash-identical originals, `ReconcileAsync` empty); X8: the epoch operations create no Trial transaction and the event's document counts once (D-215; test through the entitlement counting path); fresh and upgrade migration; masking and SoD.
* **Depends on:** E2b. **Not enabled:** the runtime role still has SELECT only on the four tables; the executor runs only under the test-only grant.

### CI. Compensation and accounting integration [CI] (separate workstream, owner of the accounting contract; about 1 to 2 days of this plan's own slice plus its review)
`CompensationJournalWriter` (the real `ICompensationJournalWriter`): from the plan's approved compensation lines create the compensation journals as DRAFT journals in the same transaction under the existing journal approval policy (TM-D9), bucket per (period, branch, account), source key = plan id + bucket so a repeat cannot create a second set (A6, TM-D12), original accounts and original period (D-044, TM-D8(a)); `CompensationPlanExecutionJournal` rows; the plan's lines equal the journals' lines (verified before commit). The ledger checks E1 132, E2 120, E6 156 after the drafts post.
* **Tests:** journal balance and accounts valid (TM-D4), dates permitted (TM-D8), fingerprint unchanged, original journals and operations untouched (hash-identical), idempotent source key, rollback with the epoch, the ledger totals of E1/E2/E6 after posting, the close probe's cost-ledger versus general-ledger comparison.
* **Depends on:** E3 (the interface) and the existing journal service. **Blocks:** E5.

### P. Close-probe extension and compensation repair [P] (separate workstream; about 1 to 2 days)
The close probe's failed-journal check is extended to compensation journals; the probe blocks the period while compensation drafts are pending and reconciles after they post; the repair action re-creates a CANCELLED compensation draft (TM-D5) so cost ledger and general ledger cannot silently diverge. Design section 5 names both as **prerequisites for enabling execution**.
* **Tests:** a cancelled compensation draft blocks the close and can be repaired; pending drafts block, posted drafts reconcile; N28 with the extended probe.
* **Depends on:** CI (compensation journals exist). **Blocks:** E5.

### E5. Enablement [X] (about 1 day; the only slice that changes a grant)
* `CompanyRuntimePrivileges`: INSERT returns on `CompensationPlanExecution`, `CompensationPlanExecutionJournal`, `EpochMember`, `EpochEntryLineage` (the grant catalog, applied by `grant-company-runtime`; no migration), the executor is registered in the container, and `ICompensationJournalWriter` is the real writer.
* **Tests:** `PlanExecutionRuntimeRoleDisabledTests` is replaced by the enabled equivalent (the runtime role holds INSERT only on those four tables, through the audited executor path; a raw-SQL epoch by the runtime role is still refused by every guard unless it is a complete, approved, audited, same-transaction execution, which the guards cannot tell from the executor: this is the accepted T2 risk of the design, section 7, mitigated by audit verification and the optional definer of 3.6, see section 9); `PlanExecutionDisabledTests` changes only where it names the executor; the full N1 to N32 matrix runs with the PRODUCTION grants, no test-only grant; restore drill (N18) and upgrade migration with the grant step; the entire existing suite unchanged.
* **Depends on:** E3, CI, P, and an independent review of the whole executor path (the combined diff of E1 to E3, CI, P). **Even after E5 there is still no endpoint, no UI and no preset role:** execution is reachable only through the internal service; the API and the shared Web/Tauri UI are a later, separate phase.

### Sequence and parallelism

```
E1 -> E2a -> E2b -> E3 -----------\
                     \-> CI -> P --> E5 (enable)
```
E1, E2a and E2b are sequential and safe to review one by one. CI and P can start once E3's interface (`ICompensationJournalWriter`) is merged and run in parallel with the review of E3. **Nothing on the E1 to E3 path can execute anything in production: the grant has not returned.** If CI or P slips, E1 to E3 are still valuable and stay merged and disabled; E5 waits.

---

## 5. What stays disabled, and until when

| Disabled | Until | How it is enforced |
|---|---|---|
| Any write to the four execution tables by the runtime role | E5 only | `CompanyRuntimePrivileges` SELECT only; `PlanExecutionRuntimeRoleDisabledTests` (42501) |
| The executor itself (no registration, internal class, `EpochWriteScope` not constructible elsewhere) | E5 registers it | `PlanExecutionDisabledTests` (no route, no service, no executor type or entry point, IL scan of the audit constant) |
| The barrier exception | only inside an executor-held `EpochWriteScope` and only for a member of this transaction's execution | engine unit and database tests (N6, N12, N16, N23) |
| Executing a plan that has compensation lines | E5 (after CI) | `compensation_not_integrated` in E3 |
| Any endpoint, route or UI for plan creation, approval or execution | a later phase | route-table test |
| Any preset holding `create`, `approve` or `execute` (X4 open) | owner decision | catalog and system-database tests |
| Revaluation, method conversion, adjustments and stocktake replay, reversal replay (M6), obligations and settlements | their own contracts | refusals of design section 6 |

---

## 6. Tests of design section 8, by slice

| Tests | First proven in |
|---|---|
| N1, N4, N5, N6, N7, N12, N16, N17, N18, N22, N25 (only with 3.6) and the existing suites | already green on main; re-run in every slice; N6, N12, N16, N22, N23 extended in E2a |
| N2, N9, N10, N11, N20, N26, N27 (database part), N30 | E2b (writer level) |
| N3, N8, N13, N14, N15, N19, N21, N24, N27 (service part), N28, N29, N31, N32, rollback at each step, E1/E2/E6 cost side, fresh and upgrade migration, masking and SoD, FIFO and average, single- and two-period | E3 |
| E1/E2/E6 ledger side (journals 132, 120, 156), the probe reconciles after the drafts post and blocks while pending | CI and P |
| The whole matrix under the PRODUCTION grants, runtime-role grants, restore drill with executed epochs | E5 |

---

## 7. Migrations

**No migration is required.** The nine tables, the entry/operation/posting guards, the deferred checks, the closure hash, X5 to X7 and the next-sequence rule are company migration 0036, merged. The grant is a catalog change in `CompanyRuntimePrivileges` (applied by `grant-company-runtime`); the permission code `execute` is a catalog change with no preset. The engine barrier, the writer, the event hook, the executor, the journal writer and the probe extension are application code.

If a reviewer or the owner decides on either of the following, I name it here and the lead allocates the number; I do not pick one:
* **Optional hardening M-A (additive):** a deferred check that every compensation-line bucket of the plan has its `CompensationPlanExecutionJournal` row at commit (defence in depth for CI; the service already refuses to commit otherwise). Candidate company migration, number to be allocated.
* **Optional hardening M-B (design 3.6, the definer function):** the back-dated insert reachable only through a SECURITY DEFINER function with `search_path` pinned to the migration's own schema, closing the T2 forgery of a well-formed epoch by a hostile holder of the runtime role. It needs an owner decision (the migration guard of Q5 refuses `search_path`, so it needs the approved security process). Candidate company migration, number to be allocated.

---

## 8. Risks and how the plan handles them

* **The barrier change is the riskiest edit** (it touches the engine every posting uses). E2a is its own PR with the existing costing suites unchanged, the ordinary-path pin, and mutation proofs (removing the member condition fails N16/N23 variants).
* **FIFO layer and consumption handling in storno and rebook** (layers get new arrival ranks in replay order). E1 derives the expected layers purely, E2b proves layer and value equality against it for FIFO and average before any orchestration exists.
* **Lock duration and closure size.** The closure is bounded by the planner's limits; the executor takes all position locks up front in the documented order (N28, N8); the audit stream lock stays the last lock of the transaction.
* **The T2 risk of the design (a hostile holder of the runtime role can write a well-formed epoch)** becomes real exactly at E5. It is the accepted risk of design section 7; E5 therefore requires the independent review of the whole path, and I recommend deciding M-B before production use.
* **Trial counting (D-215):** epoch operations have no stock document and no posting of their own except the event, which is the document's single posting; E3 pins it by test.
* **Partial delivery:** E1 to E3 are inert without E5; CI and P are separate workstreams with their own contracts; no slice depends on the unfinished revaluation or conversion work.

---

## 9. Choices left to the reviewer (the design leaves them to the implementer)

1. Whether E2 stays two PRs (as above) or one; I propose two for review size.
2. Whether `ICompensationJournalWriter` ships in E3 as an interface with the fail-closed default (as above), or CI is merged before E3 and E3 composes the real writer; the plan works either way, the first lets E3 merge earlier.
3. Whether M-A and/or M-B (section 7) are wanted, and when.
4. The re-plan inside the execution transaction needs a planner variant that runs on an open connection under READ COMMITTED with the locks already held (design 5, step 3); I propose an internal overload of `LoadCompensationPlanAsync` taking the connection and transaction, with its own tests, as part of E3.

---

## 10. Process

Each slice: its own branch from current main, Draft PR, targeted tests through the CPU gate, one full local PostgreSQL suite on the final head, hosted CI on the exact head, an independent review (separate agent, full diff against main, recorded in `docs/review-records/`) with no open Critical or High finding, then merge under the autonomous merge policy. This plan is reviewed before any implementation starts.
