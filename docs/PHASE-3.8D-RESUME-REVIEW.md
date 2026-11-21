# Phase 3.8D resume review: PR #48, decisions X1-X8 and an additive replacement for X1

Status: **ANALYSIS ONLY, inert.** Nothing in this document is approved policy, and nothing here changes code, a migration, a trigger, a CHECK or any other database safeguard. Every safeguard change named below needs the owner's explicit approval (CLAUDE.md, "Only these need the owner"). Agent G reviews X1 independently and separately. This document was written without coordinating conclusions with that review.

Legend: **[A]** approved (SRS, `DECISIONS.md` or an explicit owner decision), **[E]** existing implemented behavior (cited), **[P]** proposal made here or in #48, **[D]** open owner decision.

Sources read: `origin/main` at `86e3ce8`; PR #48 (`feature/phase-3-8d-impact-preview`) at head `1e8f477`; `docs/SRS.md` sections 4, 5, 9, 15, 19 and 21; `docs/DECISIONS.md` (main and #48); `docs/ROADMAP.md` Phase 3.8D and the OD/T table; `docs/INTEGRATION-STATUS.md`; company migrations 0014, 0017, 0020 and 0022; `MigrationScript.cs`; `InventoryCostingService.cs`; #48's `docs/PHASE-3.8D-*.md` and `docs/review-records/PR-48-*.md`.

## (a) PR #48 inventory and state compared with main

| Item | State (verified 2026-11-21) |
|---|---|
| PR | #48, Draft, open, base `main`; GitHub reports `mergeable: false` (`dirty`) |
| Head | `1e8f477` (last commit 2026-11-19: "record the coordinated migration number reservation") |
| Merge base with main | `fe44321` |
| Distance | 39 commits on #48 that are not on main; **868 commits on main that are not on #48** |
| Diff against merge base | 96 files, +8465 / -86 |
| Hosted checks at `1e8f477` | backend-build-test, postgres-integration, ci-guard, guard-selftest, secret-scan and docs-traceability **passed**; dependency-scan **failed** (the old picomatch policy failure, already present on every branch at that time) |
| PostgreSQL registry | 962 at the merge base, **1202 on #48** (+240), **1255 on main**. After a merge it is main's total plus #48's own new tests, recounted with `tools/verify/ci-guard.js`; it is not a fixed number |
| Migrations | **#48 adds none.** Its design reserved company 0023 / system 0025. **That reservation is stale:** main now holds company 0001-0032 (0023 is `company_party_roles`) and system 0001-0033. Numbers are taken from the next free ones at merge time (currently company 0033 and system 0034), allocated in `INTEGRATION-STATUS.md` |
| Decisions | D-044/D-045 are on #48's `DECISIONS.md` only. Main keeps them reserved for #48 (`INTEGRATION-STATUS.md` line 34) |

**What #48 contains (preserve all of it; nothing is duplicated on main):**

* Internal, read-only costing code under `src/backend/Hasm.Infrastructure/Costing/`: `CostReplay`, `CostReplayBlocks`, `CostReplayFlow`, `ReplaySchedule`, `CostHistorySnapshot`, `CostFlowSnapshot.*` (7 partial files), `CostFlowComparison`, `CostImpactPreview`, `ClassificationEvidence`, `CompensationCalculator`, `CompensationDateGuard`. Plus `ReadCommandBudget` and a budgeted read-only validation path in `StockDocumentService.ReadValidation.cs`, `StockPosting.cs`, `CompanyRuntime*.cs`, `AccessService.cs` and `PermissionCatalog.cs` (an interface only; no new permission codes).
* The composed read-only planner `LoadCompensationPlanAsync`. It works in one snapshot: history, comparison, classification evidence, compensation, all-dates fiscal gate and a live re-check of authorization. **No API, no DI registration, no production caller, no persistence and no journal creation.** The date barrier (`backdated_not_supported`) is unchanged.
* 13 design and contract documents (`PHASE-3.8D-DESIGN`, `-ACCOUNTING-PROPOSAL`, `-CLASSIFICATION-CONTRACT`, `-CONVERSION-CONTRACT`, `-FISCAL-READ-CONTRACT`, `-FLOW-COMPARISON`, `-MULTILINE-CONTRACT`, `-PLAN-EXECUTION-DESIGN` revision 2, `-SOURCE-READ-CONTRACT`, `-TRANSFER-REPLAY`, `-TRANSFER-SNAPSHOT`, `-TRANSITION-MATRIX-DECISIONS` revision 2) and 23 review records `PR-48-*.md`.

**Conflicts a merge of main into #48 would face (measured with `git merge-tree`; nothing was merged):**

| File | Kind | Resolution |
|---|---|---|
| `.github/workflows/backend.yml` | registry step title (1202 against 1255) | main's total plus #48's own tests, recounted |
| `docs/AUTONOMOUS-PROGRESS.md`, `docs/AUTONOMOUS-STATE.json` | concurrent handoff edits | keep main's text and append #48's entries |
| `src/backend/Hasm.Infrastructure/Inventory/StockDocumentService.cs` | class declaration (#48 adds `partial`; main adds an optional `ILogger` parameter for `IssueOrRefuseAsync`) | trivial: `public sealed partial class ... (…, TimeProvider clock, ILogger? logger = null)` |

Semantic (non-textual) items for the merge:

1. `Costing/` on main is **unchanged** since the merge base, so #48's costing code does not collide.
2. `StockPosting.cs` on main gained `PostInWorkAsync` (in a caller-supplied transaction, #107). #48's change there is only `ReadSourceAmounts`, which merges automatically. Re-run #48's source-validation tests after the merge.
3. #48's review records contain **version-qualified model names**, which main removed everywhere in #138. They must be normalized to the model family before merging. This is a documentation edit; the findings stay as they are.
4. The design's "company 0023 / system 0025" text is stale (see the table above) and must be rewritten to "next free at merge".
5. The endpoint permission matrix (#119) and the runtime-role completeness guard (#132, #140) apply as soon as #48 adds tables or permissions. Today it adds none.

## (b) X1-X8 revalidation against the latest main

The owner authorized the conservative transition-matrix recommendations as the **working baseline** (#48 `AUTONOMOUS-PROGRESS.md`, "Owner authorized the conservative recommendations of the transition matrix as the working baseline"). X1-X8 have **never been decided** (`INTEGRATION-STATUS.md`, owner register: "X1-X8 (#48)" open).

| ID | Proposal in #48 | SRS / decision basis | Compatibility with main | Recommendation | Owner-reserved |
|---|---|---|---|---|---|
| X1 | Relax the position back-date trigger and replace the operation-kind CHECK for "replay epoch" rows | SRS 9 (back-dated movements allowed within period policy and permissions); SRS 19.3; D-009; D-020; D-044 ("historical cost operations never modified") | **Replacing the CHECK cannot be done:** `MigrationScript.Parse` refuses any `DROP` (`MigrationScript.cs` line 24), and 3.8B-3 already met this limit (0020 header, "DB-1"). Relaxing `f_589a0a19` is technically possible (`CREATE OR REPLACE FUNCTION` has precedents in 0009, 0027 and 0029) but **it is not needed** (section c) | **Replace X1 with "X1-A": no existing safeguard relaxed, no CHECK replaced; a new entry-level guard with a plan-bound exception in the same transaction** (section c) | **Yes** (new sanctioned back-dating path in the DB safeguard family) |
| X2 | Epoch rows carry the ORIGINAL dates | SRS 9: the operation date and the recording/approval date are distinguished; the date of a posted financial entry is never changed. TM-D8 baseline (per period, at the affected operation). E6 close probe | Compatible. The entries already carry `created_at` (the recording date) separately from `effective_date`. Only the original dates let the cumulative cost-ledger/GL probe reconcile | Original dates, Open periods only. **Soft Closed refuses** (the stock side has no soft-close override). Final Closed refuses the whole plan | Yes (policy: as-of figures in open periods change) |
| X3 | Executor differs from creator; approver may execute | SRS 4 (SoD needs a different approver when the policy is active); main precedent D-066/D-158: the approver is never the creator, last editor or submitter | Compatible | As proposed, **plus** the approver is never the creator, last editor or submitter of the plan or of the event's stock document (D-066 pattern), enforced in both the service and PostgreSQL | Yes (SoD policy) |
| X4 | Dedicated approver preset; no existing role widened | SRS 4; main D-066 (delegated approver presets outside Company Administrator), D-096 (privileged classification, fail closed), D-105 (sensitive grants) | Compatible; the precedent now exists | Plan/approve/execute permissions are branch-and-warehouse scoped. Approve and execute are classified **privileged** under D-096. A delegated "Inventory Cost Approver" preset is granted offline (D-066 pattern), not in Company Administrator | Yes (role presets) |
| X5 | Refuse a plan on a position that already has an epoch | TM-D12 baseline (closures with an executed plan refused) | Compatible | Refuse (`plan_on_epoch_position`) in increment 1 | Confirm only (it is the conservative baseline) |
| X6 | Refuse reversal of documents rewritten by an epoch | TM-D baseline (reversal replay not available); SRS 19.3 | Compatible. **A DB backstop exists already:** `k_c8fdc6c0` (an entry is reversed at most once), so a document reversal of stornoed entries fails in PostgreSQL | Refuse explicitly (`operation_rewritten_by_epoch`) before the DB backstop is reached | Confirm only |
| X7 | The planner creates the event's stock document; it is approved by the normal document approval before the plan can be approved | SRS 9 ("separate inventory and accounting adjustment documents approved before posting"); SRS 19.2 | Compatible. **Gap found:** nothing stops the NORMAL posting path from posting that approved document if its date stops being back-dated (it would only make the plan stale) | As proposed, plus: a stock document bound to a non-terminal plan is refused by the normal posting path (`document_bound_to_plan`) | Yes (workflow) |
| X8 | Epoch rows are not counted toward the Trial limit; compensation drafts follow the normal journal rule | SRS 15.6 (automatic entries are not counted as extra transactions); **OD-12/T-2 are still OPEN** | Depends on OD-12. Not independently decidable | Follow the OD-12 proposal: the back-dated stock document counts once; epoch rows and compensation journals are automatic and not counted. Until OD-12 is decided, fail closed with a clear refusal code | Yes, **through OD-12** (no new question) |

## (c) X1 analysis

### c.1 The current safeguards (company migration `0014_company_costing_engine.sql` unless stated)

| Safeguard | Location | What it actually enforces |
|---|---|---|
| Operation-kind CHECK `k_f356c89a` | line 33, table `t_a648ca40` (cost operations) | kind in (1 receipt, 2 issue, 3 transfer, 4 reversal), non-empty key, and **kind = 4 if and only if `c_03a68cea` (reverses-operation id) is set** |
| Unique index `k_99f6719a` | line 35 | an operation is reversed by at most one operation |
| Operation immutability `f_9c827423` / `r_dfcc7b00`, `r_bdcb866c` | lines 36-49 | no delete/truncate; only the reversed-by mark may be set, once; everything else is immutable |
| Position guard `f_589a0a19` / `r_5de79b17`, `r_b7546021` | lines 70-86, table `t_a2903683` (positions) | identity immutable; **method immutable** (line 77); **entry count advances by exactly 1 on every update** (line 78); **`c_3c880b93` (last effective date) never decreases: "a cost position does not accept back-dated entries"** (lines 79-81) |
| Position CHECK `k_81ff32eb` | line 68 | method in (1, 2); quantity and value never negative; no value without quantity |
| Entry CHECK `k_0b3e9de8` | lines 112-113, table `t_bc727c1d` (entries) | entry kind 1-4, direction ±1, **quantity > 0**, running figures never negative |
| `k_214c34ab`, `k_c8fdc6c0` | lines 115-116 | a unique sequence per position; an entry is reversed at most once |
| Entry / consumption append-only `f_8b9d471d`, `f_4bf3449c` | lines 118-124, 181-187 | no update, delete or truncate |
| Zero-entry operations `f_6df04d67` / `r_d621465c` (0020) | 0020 lines 466-477 | deferred: an operation without entries only for negative-stock obligations and settlements |
| Open obligation means quantity 0, `r_7c0b96d2` (0020) | 0020 line 329 | deferred |
| Application barrier | `InventoryCostingService.cs` line 248 `backdated_not_supported` | refuses an entry dated before the position's last date |
| Migration guard | `MigrationScript.cs` lines 24-33, 59-67; `MigrationRunner` checksums | no `DROP`/`TRUNCATE`/`DELETE FROM`; versions contiguous from 0001; an edited applied migration is refused |

**Key finding (F1): the DB back-date safeguard is indirect.** PostgreSQL forbids lowering the position's `last_effective_date`. It does **not** check an entry's date. The costing service writes the entry date into the position (`InventoryCostingService.cs` line 273). A writer that inserts a back-dated entry and keeps the position date with `GREATEST(old, new)` passes PostgreSQL today: `f_589a0a19` sees no decrease, and no trigger compares `t_bc727c1d.c_12665ffb` with the position. `PostgreSQL_protects_the_costing_tables_even_from_direct_sql` (`InventoryCostingTests.cs` line 544) does not test this case. Consequence: **X1's "relax the trigger" is unnecessary.** An executor that maintains `GREATEST` needs no change to `f_589a0a19`. The real DB-level gap is that ordinary entries are not date-guarded at row level.

**Key finding (F2): a new operation kind cannot be added.** `k_f356c89a` cannot be dropped or replaced under the migration guard (DB-1 precedent in 0020). A value-only revaluation entry cannot exist either (`k_0b3e9de8` requires quantity > 0), and a FIFO layer's original amount is immutable (`f_b16f3723`).

### c.2 The proposed change in #48 (X1 as written)

(a) Change `f_589a0a19` so that it accepts an older entry date only for epoch operations; (b) a new operation kind 5 or separate storno/rebook kinds (CHECK replacement); (c) a lineage table; (d) plan tables. Item (b) is infeasible (F2). Item (a) would weaken a guard that does not actually protect entry dates (F1), and the exception would need a way to recognize an "epoch operation" inside a position trigger.

### c.3 Additive alternatives

| Option | Description | Existing safeguard changed | Verdict |
|---|---|---|---|
| **A1** Kind-4 storno per original operation | storno each suffix operation with an ordinary kind-4 reversal (fits `k_f356c89a`/`k_99f6719a`), rebook with natural kinds | none, but **it sets the reversed-by mark on historical operations** (an UPDATE of the original row, arguably against D-044), consumes each document's single reversal slot, and forces whole-operation storno (multi-item documents drag unrelated positions into the chain) | Possible; not recommended |
| **A2** Natural-kind epoch operations typed by an append-only membership/lineage table (recommended) | storno operations keep the original's kind (1/2/3) with `c_03a68cea` NULL and entry-level `c_4f9e777a` links; the event is posted normally; rebook operations take natural kinds. A new append-only table types each operation (role 1 storno, 2 event, 3 rebook) and records lineage original entry -> storno entry -> rebook entry, bound to one plan execution | **none relaxed; `f_589a0a19`, `k_f356c89a`, `k_0b3e9de8`, `k_99f6719a` untouched**; precedent: 0020 DB-1 (typing by side table instead of widening the CHECK) | **Recommended** |
| A3 Supersession table (Option S of #48) | keep entries, store revised figures beside them | needs position updates without entries, which `f_589a0a19` line 78 refuses; every reader changes | Not additive; rejected |
| A4 Session flag / GUC exception | e.g. `SET LOCAL hasm.epoch = on` read by a trigger | forgeable by any session holding the runtime role | **Rejected** (owner instruction: never a session flag) |

### c.4 Replacement protection for A2 (one new company migration, forward-only, no DROP) [P]

1. **Plan tables** (as in the design, section 3): plan, lines, events, execution. Append-only; status only along allowed transitions; `UNIQUE (plan)` on execution. The execution row stores `created_xact` and is valid only when **written in the current transaction**: the same `xmin = txid_current()` binding that company migration 0022 already uses (`f_831e3a7a`, fail-closed inside savepoints).
2. **Epoch membership/lineage table** (append-only, `REVOKE ALL FROM PUBLIC`, runtime role INSERT/SELECT only). BEFORE INSERT trigger: the referenced execution exists, was written in **this** transaction, belongs to a plan in status `approved` whose approver differs from its creator, and the operation was inserted in this transaction too.
3. **New entry-level guard (tightening)**: BEFORE INSERT on `t_bc727c1d`:
   * if `NEW.c_12665ffb` < the position's current `c_3c880b93`, **refuse unless** `NEW.c_eb34b7d8` (operation) has an epoch-membership row bound to an execution written in this transaction;
   * if `NEW.c_4f9e777a` (reverses entry) is set and the operation's kind is not 4, refuse unless the operation is an epoch **storno** member;
   * for an epoch member: the entry's date lies in an **Open** fiscal period (Soft Closed, Final Closed or missing refuses; this gives epoch rows the database period guard that the open owner item STOCK-PERIOD-DB proposes for everything), and the position holds no open obligation.
4. **Executor contract** (application): writes `last_effective_date = GREATEST(old, entry date)` for epoch rows and for the event, so `f_589a0a19` is never relaxed and still refuses any decrease. The ordinary path is unchanged: its date is never earlier than the last date (barrier at line 248), so `GREATEST` equals today's value.
5. **Deferred constraint trigger** (at commit) on the execution: every storno has exactly one rebook or is the event's predecessor; the epoch has at least one member; the plan moves to `executed` in the same transaction.
6. **Unchanged backstops:** `k_c8fdc6c0` (an entry is stornoed once, the X6 DB backstop), the append-only triggers, the sequence rule, the no-negative rules, `r_7c0b96d2` and `r_d621465c`.

Net effect: for ordinary movements PostgreSQL becomes **stricter** than today (F1 closed). The only way to insert an earlier-dated entry is an operation bound, in the same transaction, to an approved plan's single execution.

### c.5 Threat model

| # | Threat | Today | With A2 |
|---|---|---|---|
| T1 | A bug in an ordinary posting path inserts a back-dated entry and writes `GREATEST` | passes PostgreSQL (F1); only the app barrier and `ReconcileAsync` catch it | refused by the entry guard |
| T2 | Direct SQL with the runtime role inserts a back-dated entry | passes if the position date is not lowered | refused unless an approved plan execution is forged in the same transaction, which needs a plan approved by a different user (SoD trigger) and leaves an append-only audit trail |
| T3 | An old executed plan is reused to back-date later | n/a | refused: the execution must be written in the current transaction, and execution is unique per plan |
| T4 | Session/GUC tampering | n/a | no flag exists; a test proves an arbitrary `SET` has no effect |
| T5 | Epoch into a closed period | service-level only | refused by the entry guard (Open only) |
| T6 | Epoch on a position with an open obligation | n/a | refused (trigger and planner; TM-D10/TM-D13a) |
| T7 | Reversal of a rewritten document | n/a | service refusal plus `k_c8fdc6c0` |
| T8 | Concurrency with ordinary posting | n/a | same advisory lock, then positions by (warehouse, item), then the plan row; READ COMMITTED with locks first (design revision 2, 2a.1) |
| T9 | Savepoint or subtransaction confusion | n/a | the xmin binding refuses inside savepoints (as 0022): fail closed |
| T10 | DB owner or superuser | out of scope (can bypass any trigger); SRS 4 (no DBA rights for users) | unchanged |

**Residual risks:** (R1) movement reports sorted by date look scrambled after an epoch (design 2a.7; UX decision, no policy). (R2) As-of valuation of open periods changes (inherent to SRS 21.5 and X2). (R3) Every later reader (3.8E reports, future replay) must understand the epoch lineage. X5/X6 keep increment 1 safe. (R4) Entry-level storno of a multi-item operation leaves that operation partly live; its posting link must be read through the lineage. Covered by X6 and the snapshot refusal.

## (d) Test matrix

**Existing tests that must stay green UNCHANGED (proof that ordinary movements stay protected):**

* `InventoryCostingTests`: `Back_dated_entries_are_refused_until_the_recalculation_slice_exists` (line 234, `backdated_not_supported` for ordinary receipts/issues), `PostgreSQL_protects_the_costing_tables_even_from_direct_sql` (line 544), `The_valuation_policy_decides_the_method_...` (method immutability, line 214), the reversal tests (lines 401-474), `A_long_deterministic_scenario_...` (line 490), and the concurrency tests (lines 325-382).
* `StockAdjustmentTests.Posting_is_refused_with_backdated_not_supported_when_the_item_moved_after_the_count_date` (line 368); the `NegativeStockTests` date rules (`negative_obligation_older`, R22); `StockReversalTests`; `InventoryPeriodInvariantTests`; `ConcurrencyInvariantTests`; endpoint constraint matrices parts 1 and 2; the schema-upgrade and opaque-name scenario 21.10 (#151); the runtime-role completeness guard (#132/#140).
* All 240 #48 read-only tests (the planner stays read-only until execution is approved).

**New negative tests (PostgreSQL, isolated 5433 cluster, `scripts/test-pg/run-isolated.sh`):**

| # | Case | Expected |
|---|---|---|
| N1 | Direct SQL: insert a back-dated entry for an ordinary operation and update the position with `GREATEST` | refused (**passes today**: proves the tightening) |
| N2 | Epoch membership whose execution was written in an earlier transaction | refused |
| N3 | Execution for a plan that is not `approved`, or approved by its creator | refused |
| N4 | Membership or back-dated entry without any execution row | refused |
| N5 | UPDATE, DELETE or TRUNCATE on plan, execution or membership tables | refused |
| N6 | Lowering a position's last date (existing guard) | still refused |
| N7 | Entry with reverses-entry set in a non-kind-4 operation without storno membership | refused |
| N8 | Second execution of the same plan; concurrent double execution | exactly one succeeds |
| N9 | Epoch inside a savepoint | refused (fail closed) |
| N10 | Epoch entry dated in a Soft Closed, Final Closed or missing period | refused |
| N11 | Epoch on a position with an open obligation | refused |
| N12 | `SET` of arbitrary custom settings before a back-dated insert | no effect, refused |
| N13 | Normal posting of a stock document bound to a pending plan | `document_bound_to_plan` |
| N14 | Reversal of a document rewritten by an epoch | `operation_rewritten_by_epoch`; DB backstop `k_c8fdc6c0` |
| N15 | Second plan on an epoch position | `plan_on_epoch_position` |
| N16 | Ordinary back-dated posting after the migration, via every path (receipt, issue, transfer, adjustment, stocktake, negative exception) | the existing codes, unchanged |

**New positive and invariant tests:** E1 (132), E2 (120) and E6 (156) executed end to end. Pre-existing rows are hash-identical before and after (operations, entries, consumptions, journals). `ReconcileAsync` is empty. The close probe reconciles once the drafts post and blocks while they are pending. Rollback on an injected failure at each executor step. Fresh-database and upgrade migrations. Runtime-role grants. Authorization masking and SoD. FIFO and average. A two-warehouse transfer chain. Single-period and two-period compensation.

## (e) TM-D and conversion-policy reconciliation with the SRS

| Item | Working baseline (owner-authorized conservative recommendation) | SRS / decision check | Result |
|---|---|---|---|
| TM-D1 | refuse; repair first | SRS 9 (pending differences block close) | consistent |
| TM-D2 | accept as neutral only when provable, else refuse | SRS 19.2 | consistent; stays refused until provability is shown |
| TM-D3 | refuse until posted | SRS 21.4 | consistent |
| TM-D4 | default refuse; an approved same-type remap later | D-044 ("where valid") | consistent; the remap needs its own slice |
| TM-D5 | refuse; later a repair slice and probe coverage of compensation journals | SRS 9, 21.4 | consistent; the probe extension is a hard prerequisite for execution (a cancelled compensation draft otherwise has no repair path) |
| TM-D6 | refuse ambiguity; a migration later | D-044 | consistent |
| TM-D7 | accept transfers | D-018 (actual cost moves with the transfer); E4 (no cross-branch transfer) | consistent |
| TM-D8 | per period at the affected operation; Final Closed refuses; no silent fallback | SRS 9, 19.5 | consistent; also refuse Soft Closed on the stock side (X2) |
| TM-D9 | different approver; compensation as ordinary drafts | SRS 4, 21.2 | consistent. Note: the cost ledger changes at execution while the journals are drafts; the plan approval shows the exact lines, and the close probe blocks until they post (SRS 21.4) |
| TM-D10 | keep refusing open obligations | SRS 9 | consistent |
| TM-D11 | the bucket-difference method | D-044 | consistent |
| TM-D12 | refuse closures with an executed plan | SRS 19.2 | consistent (= X5) |
| TM-D13 | (a) refuse obligation changes | D-019, SRS 9 | consistent |
| TM-D14 | adjustments later | ROADMAP 3.8D scope | consistent for increment 1; **phase completion needs it** |

**Conversion and revaluation: conflicts found inside #48:**

1. **C-D3 against the conversion contract section 1.** The TM recommends refusing back-dated movements before a conversion date. `PHASE-3.8D-CONVERSION-CONTRACT.md` says such events "must replay through that boundary". Resolution [P]: the conservative baseline (C-D3 refusal) governs the first conversion increment. The contract must be aligned, with replay through the boundary as a later reviewed slice.
2. **C-D2 against the conversion contract section 4.** The TM takes a reversed transfer out of the destination "at the destination's current method". The contract removes "the linked exact amount" and states that transfer-only movement creates no company-wide gain or loss. **D-018 settles this:** the actual cost moves with the transfer. The contract's exact-amount rule applies, with refusal when it cannot be proven. No owner question is needed.
3. **C-D4 (revaluation "excluded from this phase") against ROADMAP 3.8D**, which lists "controls for changing the valuation method with revaluation", and SRS 9 bullet 2. D-045 says revaluation needs its own contract. This is a genuine scope and accounting-policy gap (section f, Q3). Technical note: a value-only entry is impossible (`k_0b3e9de8`), so a revaluation would be represented as an out/in pair at the old and new values, typed through the A2 membership table, current-dated.
4. **Conversion needs the method-immutability guard to change** (`f_589a0a19` line 77; `k_ee055292` forbids a second position). The conversion contract itself says this barrier is "real, not an application flag to disable". It needs an owner-approved, narrow, same-transaction exception keyed to an approved conversion row (`CREATE OR REPLACE` of the function; there is no additive alternative that keeps one source of truth). Section f, Q4. It does not block increment 1.

## (f) Consolidated consequential owner questions (only real ones)

* **Q1 - X1 replacement.** Approve **X1-A** (section c.4) in place of X1 as written: no existing trigger relaxed, no CHECK replaced, a new entry-level guard that makes ordinary movements stricter, and an epoch exception that exists only for an operation bound in the same transaction to the single execution of an approved plan, with Open periods only. *Default until decided:* the planner stays read-only and nothing changes. (Agent G's independent review of X1 is a separate input to this decision.)
* **Q2 - X2-X8 as one package.** Confirm the conservative recommendations of section (b), including the two additions found here (X3: approver never the creator, last editor or submitter of the event document; X7: a plan-bound document is refused by normal posting), with X8 following OD-12. *Default:* no execution.
* **Q3 - Revaluation.** Is revaluation part of Phase 3.8D, and under which accounting contract (counter accounts, write-down only or also write-up, approval)? D-045 requires its own contract; #48's C-D4 excludes it, the roadmap includes it. *Default:* not built; the phase cannot be declared complete against ROADMAP 3.8D until this is answered.
* **Q4 - Method-conversion safeguard.** When the conversion slice starts, may `f_589a0a19`'s method-immutability rule receive a narrow exception bound in the same transaction to an approved conversion row (the same pattern as Q1)? *Default:* conversion stays disabled (`valuation_method_changed`). Not needed for increment 1.

Not re-asked (already open elsewhere): OD-12/T-2 (Trial unit, which X8 depends on), STOCK-PERIOD-DB (A2 gives epoch rows their own period guard either way), TM-D items (working baseline).

## (g) Recommendation, sequence, dependencies, risks and acceptance gates

**Recommendation [P]:** keep #48 and all of its work. Replace X1 with X1-A (additive, stricter for ordinary rows). Adopt X2-X8 and the TM baseline as the preferred design, subject to Q2. Deliver increment 1 = matrix cells M1, M2 and M6 only. Keep conversion and revaluation behind Q3 and Q4.

| Step | Work | Depends on | Acceptance gate |
|---|---|---|---|
| 1 | Bring #48 up to date: merge `main` into the branch (never rebase or force-push); resolve the 4 conflicts; recount the registry; normalize model names; rewrite the stale migration reservation | none (routine) | all mandatory checks green on the exact head; #48 tests unchanged |
| 2 | Plan-execution design **revision 3** adopting X1-A (A2), the X3/X7 additions and the stale-text fixes | step 1; Agent G's review | independent full-diff design review, no Critical/High |
| 3 | Owner decisions Q1 and Q2 | step 2 | recorded once in `INTEGRATION-STATUS.md` and as D-numbers |
| 4 | One company migration (next free number at merge) with the plan, execution and membership tables, the entry guard and the deferred check; system migration for the permissions and the delegated preset (D-066/D-096) | step 3 | N1-N16 green; existing protection tests unchanged; fresh and upgrade migration; runtime-role completeness |
| 5 | Executor (storno, event, rebook) plus approval and the refusals X5/X6/X7 | step 4 | E1/E2/E6 executed; untouched-history hashes; rollback and concurrency; probe reconciles |
| 6 | Probe coverage of compensation journals and the TM-D5 repair slice | step 5 | a cancelled compensation draft blocks close and can be repaired |
| 7 | Reversal replay (M6), then the API and the shared Web/Tauri UI (Tabs standard, RTL/LTR) | step 5 | shared component tests, typecheck/build, Tauri build |
| 8 | Adjustments (TM-D14), conversion (Q4) and revaluation (Q3) | owner Q3/Q4 | their own contracts and reviews |
| 9 | Phase acceptance: scenario 21.5, full regression, independent review, `verify-head.js`, merge with `--match-head-commit` | all of the above | autonomous merge policy gates |

**Risks:** (1) Divergence: #48 is 868 commits behind; every week adds merge cost. Step 1 should come first. (2) The GREATEST contract lives in the application; the new entry guard makes this safe, but a test must pin it (N1, N16). (3) A cost ledger re-costed while its journals are still drafts leaves a visible pending state; this is accepted under SRS 21.4, provided step 6 exists before execution is enabled. (4) Phase scope: without Q3/Q4 and TM-D14, 3.8D can deliver back-dated ordinary movements but not the whole roadmap phase. Report this honestly instead of narrowing the scope silently.

**Model family used for this analysis:** Claude Opus. No conclusions were exchanged with Agent G.
