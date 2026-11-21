# Independent review record

* **Pull request:** #171 (docs/PHASE-3.8D-RESUME-REVIEW.md: Phase 3.8D resume review of PR #48, X1-X8 revalidation and the additive X1-A replacement; author Agent P; docs only, inert)
* **Head SHA reviewed (full 40 characters):** d3720017729bb5bd5f07ba7f8f32e487003c4adc
* **Reviewer context:** Agent G, an independent security/audit reviewer and a separate agent (not the author, Agent P); model family Claude Opus; prompt: "adversarial review of Draft PR #171 (head d372001, base 86e3ce8): factual claims (migration, trigger and CHECK names and lines, #48 ahead/behind), X1-A against Agent G's threat model (F2 guard gap, SECURITY DEFINER hardening, restore/COPY, same-transaction detection robustness), the X2-X8 revalidation including the X3/X7 additions, the revaluation scope gap (C-D4 vs ROADMAP/SRS 9, D-045, k_0b3e9de8), whether Q1-Q4 are necessary and complete; list what the doc lacks from Agent G's X1 report". Independence note: Agent G wrote its own X1 review (appended below) BEFORE reading this document; this review compares the two after the fact.
* **Scope read:** the full diff `git diff origin/main...d372001` (one new file, 211 lines); verified against `86e3ce8` and #48 at `1e8f477c2fd6039d4393aea3e4cf6e9e2d1ed3eb`: company migrations 0014, 0020, 0022, `MigrationScript.cs`, `InventoryCostingService.cs`, `InventoryCostingTests.cs`, `StockAdjustmentTests.cs`, `docs/ROADMAP.md`, `docs/SRS.md` section 9, `docs/INTEGRATION-STATUS.md`, #48's `DECISIONS.md` (D-044/D-045), `PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` and `PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md`.
* **Checks run (commands and results):**
  * #48 state: `git rev-list --count` gives 39 commits on #48 not on main and 868 on main not on #48 (matches); merge base `fe44321` (matches); registry step titles 962 (merge base), 1202 (#48), 1255 (main at 86e3ce8) (matches);
  * cited lines verified exactly: 0014 line 33 `k_f356c89a`, 35 `k_99f6719a`, 36-49 `f_9c827423`, 68 `k_81ff32eb`, 70-86 `f_589a0a19` (77 method, 78 sequence, 79-81 back-date), 112-113 `k_0b3e9de8` (quantity `c_dba27822 > 0`), 115-116 `k_214c34ab`/`k_c8fdc6c0`, 118-124 and 181-187 append-only; 0020 lines 466-477 and 329; `MigrationScript.cs` lines 24-33 and 59-67; `InventoryCostingService.cs` lines 248 and 273; `InventoryCostingTests` lines 214, 234, 544; `StockAdjustmentTests` line 368; 0022 `f_831e3a7a` (the xmin binding, lines 191-204); `CREATE OR REPLACE` precedents in company 0009, 0027, 0029; INTEGRATION-STATUS line 34 (D-044/D-045 reserved) and STOCK-PERIOD-DB (line 108); ROADMAP line 46 ("method change and revaluation"), SRS 9 bullet 2 (method-change and revaluation controls), D-045's last sentence (revaluation needs its own contract), #48 C-D4 ("Not approved; excluded from this phase") - all correct;
  * F1 (no row-level entry-date guard) independently reached in Agent G's own report: confirmed;
  * comparison of X1-A with Agent G's option (a), (b), (c) and threat model (appendix).
* **Findings:** 0 Critical, 0 High, 1 Medium, 6 Low.
  * **M1 (Medium, to P): the migration-guard gap is missing (Agent G's F2).** `MigrationScript` refuses only the text DROP/TRUNCATE/DELETE FROM; it does not detect `CREATE OR REPLACE FUNCTION` of an existing guard function or `ALTER TABLE ... DISABLE TRIGGER`. The document notes that relaxing `f_589a0a19` "is technically possible (CREATE OR REPLACE ... precedents)" and Q4 itself proposes a `CREATE OR REPLACE` of `f_589a0a19`, without saying that such a weakening would pass the guard silently. The owner must see this for Q1 and Q4: add the gap, require that X1-A's migration redefines NO existing guard function, require for Q4 that the replaced function is reviewed as a safeguard change and pinned by a before/after catalog test, and record a follow-up to extend the guard (refuse `CREATE OR REPLACE` of a function that already exists and `DISABLE TRIGGER`, with an explicit owner-approved allow path).
  * **L1:** naming clash: the document's "F2" (a new operation kind cannot be added) is a different finding from Agent G's F2 (guard gap); rename one to avoid confusion in the owner decision.
  * **L2:** SECURITY DEFINER hardening (Agent G's option c) is not mentioned: an owner-owned definer writer with a pinned `search_path`, EXECUTE only for the runtime role, plus a `current_user` check in the entry guard for back-dated rows, would make T2 (direct runtime-role SQL) require the definer's checks too. Add it as optional hardening before production.
  * **L3:** restore/COPY behaviour is absent from the threat model: company restore is `pg_restore` of a full dump with triggers created post-data (no false refusals, no re-validation), the new plan, execution and membership tables must be in the backup scope, and the deferred trigger stays silent during restore; add a trial-restore test with epochs.
  * **L4:** same-transaction detection: the 0022 pattern compares `xmin` with the low 32 bits of `txid_current()` and fails closed inside savepoints. Prefer a trigger-assigned `xid8` column (`NEW.col := pg_current_xact_id()`, never client-supplied) compared with `pg_current_xact_id()`: 64-bit, no wraparound or frozen-tuple ambiguity, and correct inside savepoints (top-level id); keep the unique execution per plan as the replay barrier.
  * **L5:** the audit chain is not bound: c.4 relies on "an append-only audit trail" but no deferred check requires the plan's business audit event in the same transaction; add it (or state why not), and add a before/after catalog test (`pg_get_functiondef`/`pg_get_constraintdef` of every existing guard byte-identical) to the test matrix, which also guards M1.
  * **L6:** revaluation as an out/in pair (section e item 3) would, for FIFO, consume all layers and create one new layer, losing layer dates and granularity (a structural change akin to conversion); say so in Q3 so the owner decides it knowingly.
* **Agreement and differences with Agent G's independent X1 report (appendix):** X1-A equals Agent G's option (a) in substance (no relaxation of `f_589a0a19`, no CHECK replacement, kinds 1-3 typed by a side table after 0020 DB-1, a new entry-level guard closing F1, a same-transaction plan execution, Open periods only, X5/X6 as database rules). The document's rejection of A3 (supersession) is stronger than Agent G's "safe for the schema" verdict on option (b), correctly: a position update without an entry is refused by the sequence rule (0014 line 78). X2-X8: correct and conservative; the X3 addition (approver never creator, last editor or submitter, in service and PostgreSQL) and the X7 gap (a plan-bound document must be refused by normal posting) are valid and should stay. Q1-Q4 are necessary; with M1 added, they are complete (OD-12 and STOCK-PERIOD-DB are correctly not re-asked).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing was executed on PostgreSQL (docs-only PR; F1 follows from the trigger and CHECK text); hosted CI on d372001 was not checked by the reviewer; the company owner role's superuser status was not checked.
* **Verdict:** request changes

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "adversarial review of Draft PR #171 (head d372001, base 86e3ce8): factual claims, X1-A against Agent G's threat model, X2-X8 revalidation, revaluation scope gap, Q1-Q4; list what the doc lacks from Agent G's X1 report"; Head SHA reviewed d3720017729bb5bd5f07ba7f8f32e487003c4adc; executed: full diff read, every cited migration, trigger, CHECK, test and register line verified on 86e3ce8 and 1e8f477, #48 distance and registry counts recomputed; verdict request changes; findings M1 (migration-guard gap: CREATE OR REPLACE and DISABLE TRIGGER undetected, relevant to Q1 and Q4), L1 (F2 naming clash), L2 (SECURITY DEFINER hardening missing), L3 (restore/COPY missing), L4 (prefer a trigger-assigned xid8 over the xmin binding), L5 (audit binding and before/after catalog test missing), L6 (FIFO revaluation as out/in pair loses layer structure).

## Resolution by the author (Agent P; model family Claude Opus)

Applied in `docs/PHASE-3.8D-RESUME-REVIEW.md` on top of `c6dd72d` (one commit). It stays inert; nothing is executed or approved.
* **M1 fixed.** Section c.1 adds Agent G's F2 (the guard misses `CREATE OR REPLACE` of an existing guard function and `DISABLE TRIGGER`). It requires that X1-A's migration redefines no existing guard function and disables no trigger (pinned by the new N17). For Q4, the replacement is a safeguard change, reviewed and pinned by a before/after catalog test. A follow-up guard extension is recorded that tells a first creation apart from a replacement, and it is added as owner hardening question **Q5**. Count correction: on `86e3ce8`, `CREATE OR REPLACE` appears in 3 company migration files (0009, 0027, 0029; 10 matching lines) and in no system migration, not in 28 migrations. The guard extension must keep all of them loadable either way.
* **L1 fixed.** The document's "no new operation kind" finding is renamed **F3**; F2 now means Agent G's guard gap.
* **L2 fixed.** c.4 item 6 adds the optional SECURITY DEFINER single writer before production: owner-owned and not a superuser, pinned `search_path`, EXECUTE only for the runtime role, `current_user` check for back-dated rows, grants re-applied after restore.
* **L3 fixed.** Threat T11 covers restore/COPY: triggers are created post-data, the new tables are in the backup scope, and the deferred checks stay silent. New test N18 is a trial restore with epochs.
* **L4 fixed.** c.4 item 1 uses a trigger-assigned `xid8` column (`pg_current_xact_id()`, never client-supplied) instead of the `xmin` low-32-bit binding. Execution stays unique per plan.
* **L5 fixed.** c.4 item 5 adds a deferred check that the plan's business audit event (`t_2db8cf1f`) is written in the same transaction (new test N19). N17 is the before/after catalog test of every existing guard function, constraint and trigger.
* **L6 fixed.** Section (e) item 3 and Q3 state that a FIFO revaluation as an out/in pair collapses the layers. The per-layer alternative keeps granularity but not the arrival ranks.
* **Open for the reviewer:** confirmation of this delta.

---

## Appendix: Agent G's independent X1 review (written before reading PR #171; unchanged)

## X1 (PR #48, Phase 3.8D) - independent architecture and database-safety review

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author of #48 or of any other 3.8D analysis). Model family Claude Opus.
Scope: analysis only. No code, migration or safeguard was changed. Read: `origin/main` company migrations (the safeguards as they are today), the runtime privilege catalogue, the costing service, the migration guard, and PR #48 at `1e8f477c2fd6039d4393aea3e4cf6e9e2d1ed3eb` (`docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` revision 2, `docs/PHASE-3.8D-TRANSITION-MATRIX-DECISIONS.md`). I did not read `docs/PHASE-3.8D-RESUME-REVIEW.md` or any other agent's 3.8D analysis. No PostgreSQL run was needed: every finding follows directly from the trigger and CHECK text, quoted below.

### 0. Verdict in one paragraph

X1 as worded ("relax the position back-date trigger and replace the operation-kind CHECK") should **not** be approved:
* The first half is **unnecessary**. The existing trigger only forbids the POSITION's `last_effective_date` from going down; it never looks at an entry's date. An epoch that keeps the position date at `GREATEST(old, new)` passes it unchanged.
* The second half **contradicts the forward-only rule**. Widening `k_f356c89a` needs `DROP CONSTRAINT`, which the migration guard refuses. Migration 0020 already solved the same problem additively (DB-1).

The function can be delivered by an **additive** company migration that **strengthens** the database. Today PostgreSQL does not refuse a back-dated entry at all (finding F1). The migration adds an entry-level guard that refuses every back-dated entry except one bound to a plan approved and executed in the same transaction (option a). Running the back-dated writes through a SECURITY DEFINER writer is a further layer on top (option c). Option b is the safest for the schema, but has the largest regression surface.

### 1. The safeguards as they exist today (origin/main)

| # | Guard | Where | What it enforces |
|---|---|---|---|
| G1 | Position trigger `f_589a0a19` (`r_5de79b17` BEFORE UPDATE OR DELETE, `r_b7546021` BEFORE TRUNCATE) on the cost position `t_a2903683` | `Migrations/0014_company_costing_engine.sql` lines 70-86 (the back-date test at 79-81) | The position is never deleted. Item, warehouse and creation time are immutable, and so is the method. `last_seq` advances by exactly 1 per update. `last_effective_date` (`c_3c880b93`) may never become NULL or **decrease** ("a cost position does not accept back-dated entries"). |
| G2 | Operation CHECK `k_f356c89a` and unique index `k_99f6719a` on the operation table `t_a648ca40` | 0014 lines 33 and 35 | Kind ∈ {1 receipt, 2 issue, 3 transfer, 4 reversal}. Kind 4 ⇔ `reverses_operation_id` IS NOT NULL. At most one reversal per operation. |
| G3 | Operation trigger `f_9c827423` (`r_dfcc7b00`, `r_bdcb866c`) | 0014 lines 36-49 | Operations are never deleted. Everything except `reversed_by` is immutable, and `reversed_by` is set once. |
| G4 | Entry CHECK `k_0b3e9de8`, unique indexes `k_214c34ab` (position, seq) and `k_c8fdc6c0` (`reverses_entry_id`), append-only trigger `f_8b9d471d` | 0014 lines 112-124 | Entry kind ∈ 1..4, direction ±1, positive quantity, non-negative running figures. One sequence number per position. An entry is reversed at most once. Never updated or deleted. **No date rule at all** (`c_12665ffb` is checked against nothing). |
| G5 | Layer CHECK `k_65138e7a` and trigger `f_b16f3723` | 0014 lines 145-163 | Only the remaining figures of a layer change, within [0, original]. |
| G6 | Consumptions append-only, `f_4bf3449c` | 0014 lines 180-187 | Never updated or deleted. |
| G7 | Last-average trigger `f_9b70c8c4` (`r_9040ab41`) | `0020_company_negative_stock.sql` lines 31-47 | The last average pair of a position is written only from its own figures. |
| G8 | Deferred "open obligation ⇒ empty position" check (`r_7c0b96d2`, `f_8821e766`) | 0020 lines 315-329 | Checked at commit on every position update with quantity > 0. |
| G9 | Deferred "operation without entries" check (`r_d621465c`, `f_6df04d67`) | 0020 lines 466-477 | An operation with no entries is allowed only for obligations and settlements. 0020's header (lines 10-11) records why: widening `k_f356c89a` would need `DROP CONSTRAINT`, so kinds stay natural and a side table plus a deferred trigger carry the meaning. |
| G10 | Journal posting period check | `0008_company_general_ledger.sql` lines 592-596 | A journal cannot post outside every period, into a Final Closed period, or into a Soft Closed period without the flag. **Cost entries have NO database period check.** The cost-side period rule is application-only (FiscalPeriodGate in the planner and posting). |
| G11 | Stock postings and stock reversals append-only | 0016, 0017 | Both reference the operation. |
| G12 | Business audit chain `t_2db8cf1f` | `0002_company_business_audit.sql` (append-only triggers at lines 41-46, insert trigger at line 58) | Hash-chained, key-authenticated, append-only. The runtime role holds INSERT and SELECT only. |
| G13 | Runtime privileges | `CompanyRuntimePrivileges.cs` lines 94-99 | The runtime role holds INSERT and UPDATE on operations, INSERT and UPDATE on positions, INSERT on entries, INSERT and UPDATE on layers, INSERT on consumptions. No DELETE, no TRUNCATE, no CREATE in the schema. |
| G14 | Migration guard | `MigrationScript.cs` line 24 | Refuses the text `DROP`, `TRUNCATE` (except `BEFORE TRUNCATE`) and `DELETE FROM`. It does **not** refuse `CREATE OR REPLACE FUNCTION` or `ALTER TABLE ... DISABLE TRIGGER`. |
| G15 | Application back-date barrier | `InventoryCostingService.cs` line 248 (`AppendEntryAsync`) | Every entry, including reversals (lines 497 and 523, dated with the request date), must be ≥ the position's last date, else `backdated_not_supported`. The position date is then set to the entry date (line 273). |

**F1 (finding, pre-existing).** PostgreSQL alone does not refuse a back-dated ENTRY. G1 compares the position date with its old value, and G4 has no date rule. A writer holding the runtime role's grants (G13) can insert an entry dated before the position's last date and update the position with `last_effective_date = GREATEST(old, entry date)`: every guard in the table above accepts it. The only barrier is the application check G15. PR #48's design statement (section 1, "CORRECTION (review H1) ... so PostgreSQL itself rejects any back-dated entry") is therefore overstated. It holds only when the writer follows the convention that the position date equals the entry date.

**F2 (finding).** G14 does not detect a weakening done with `CREATE OR REPLACE FUNCTION f_589a0a19` or `ALTER TABLE ... DISABLE TRIGGER`. A "relaxation" written that way would pass the migration guard silently. That is one more reason X1 must not be implemented by replacing a guard function.

### 2. What #48 proposes (design only; #48 contains NO migration)

`docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` (revision 2, head 1e8f477):
* Section 2 adopts Option R, "storno and rebook (replay epoch)". Under the position locks it appends:
  1. storno entries for the affected suffix, in reverse order;
  2. the new back-dated event;
  3. rebook copies with the replayed amounts.
  All of these are ordinary entries of ONE new operation kind **5 "replay epoch"**, linked to the plan.
* Section 2a(2) lists the schema changes:
  * (a) the position trigger keeps `last_effective_date = GREATEST(old, new)` and accepts an older entry date ONLY for epoch operations;
  * (b) new operation kinds, which need the CHECK replaced; the doc itself notes that `ALTER ... DROP CONSTRAINT` contradicts the forward-only, no-DROP rule. It offers "or one epoch kind plus a lineage table";
  * (c) a lineage table (original entry → storno → rebook);
  * (d) the plan tables (section 3: plan, plan line, plan event and plan execution; immutable or append-only by trigger; execution unique per plan).
* Section 2a(3) / X2: epoch entries carry the ORIGINAL dates.
* Section 5(5): the event's normal posting runs with an "epoch-aware back-date permission scoped to this plan (no general lifting of the barrier)".
* Section 8 X1: "Relax the position back-date trigger and replace the operation kind CHECK ... Approve, limited to epoch operations of an approved, executed plan".

How the plan-scoped permission would be carried is not specified (no GUC, column or table named). Two contradictions follow:
* By F1, the trigger does not need relaxing for (a): `GREATEST` already satisfies G1.
* (b) cannot be built under G14 without a DROP. So X1 asks the owner to approve something that is partly unnecessary and partly disallowed.

### 3. Can an additive, forward-only migration deliver the function?

Yes. Common base for every option:
* **Keep G1-G14 byte-for-byte**: no `CREATE OR REPLACE` of an existing guard function, no DROP, no DISABLE TRIGGER.
* **Keep the operation kinds 1/2/3.** A storno is an entry with the flipped direction and `reverses_entry_id` set (G4 already allows this, and `k_c8fdc6c0` keeps it at most once). Epoch operations are typed through an append-only side table, exactly as 0020 DB-1 does. This also answers #48's own open question 7.1 ("reuse kinds with a plan link"). The weaker typing is compensated by the side table plus a deferred trigger: every operation listed there must have entries, and every storno needs a rebook.
* **Add** append-only `epoch_operation (operation_id PK/FK, plan_id FK, role {storno, event, rebook}, created_txid)` and `epoch_lineage (original_entry, storno_entry, rebook_entry)`, with the same append-only and TRUNCATE guards as G3/G4. Also add the plan tables of #48 section 3.

### (a) Narrow exception in a NEW entry-level trigger keyed to plan rows (not a relaxation of G1)

**What changes.** A new `BEFORE INSERT` trigger on the entry table `t_bc727c1d`:
1. It reads the position row `FOR UPDATE`. The engine already holds that lock, so no new lock order arises.
2. If `NEW.effective_date >= position.last_effective_date` (or the position is new), it accepts. This is today's behaviour for every ordinary path, and it makes G15 a database rule (it closes F1).
3. Otherwise it accepts only when ALL of these hold, checked inside the trigger from table rows, never from a GUC:
   * the entry's operation has an `epoch_operation` row;
   * its plan is in status Approved, with an approval row whose approver ≠ creator and whose fingerprints equal the plan's;
   * the plan's unique execution row exists and was inserted **in the current transaction** (`xmin` of the execution row = `pg_current_xact_id()`, or an `executing_txid` column checked the same way);
   * the entry date lies in an Open period (no Soft or Final Closed: this closes the G10 gap for epoch rows);
   * the position has no earlier epoch from another plan (#48's X5 refusal, enforced in the database).
   
   A deferred constraint trigger then requires, at commit, that the plan's status is Executed and that every storno has its rebook (the lineage is complete).
4. Add a `BEFORE INSERT` trigger on operations: a kind-4 reversal of an operation rewritten by an epoch is refused (#48's X6).

**Threats.**
* *Runtime role abuse.* The runtime role can insert plan, approval and execution rows, so a fully compromised runtime role (the application itself) can forge an approved plan. That is the same trust level as every other separation-of-duties rule in HASM (journal approvals are runtime-role rows too). What changes is that forging now needs several append-only, audited, hash-chain-adjacent rows in one transaction, instead of the single convention-following UPDATE that F1 allows today. **Net effect: stronger than today.**
* *GUC spoofing.* None: no GUC is read. A `SET` is worthless.
* *Replayed plan id.* Execution is unique per plan, and "execution row from the current transaction" means an old Executed plan id cannot be reused in a later transaction. Plan status transitions are one-way (#48 §3 triggers).
* *Concurrent execution.* The unique execution row means the second transaction blocks and then fails. Position row locks serialise entries, and the trigger re-reads the position under `FOR UPDATE`.
* *Restore / COPY.* Company restore is `pg_restore` of a full dump. Triggers are created in post-data, so they do not fire on the restored rows: nothing breaks, and nothing is re-validated, consistent with every other trigger. The system COPY restore (D-106/D-107) does not touch company tables. The deferred trigger also stays silent during restore.
* *Audit chain.* The execution appends business audit events in the same transaction (the plan id, the fingerprints, the approver and executor, the operation ids). The trigger can additionally require that an audit event for the plan id was appended in the transaction before commit (deferred). The chain itself is untouched (G12).

**Replacement protection.** G1 stays. The new entry guard is strictly stronger for every non-epoch writer (closes F1), and the exception needs plan, approval and execution rows from the same transaction plus an open period.

**Verdict: safe with conditions.**
1. The trigger never reads a GUC or session variable.
2. The binding is by the execution row created in the same transaction.
3. G1 is not replaced, and no existing guard function is redefined.
4. Kinds 1-3 are kept with the side-table typing; a kind-5 CHECK change would need its own explicit owner exception to the no-DROP rule, which I do not recommend.
5. The period check is included.
6. The X5/X6 refusals are database rules.
7. The tests of section 5 pass.

### (b) Separate append-only epoch table unioned by the read model (no back-dated cost writes)

**What changes.** Nothing is written back-dated into entries, operations or positions. The revised figures live in append-only epoch tables, and every reader (engine snapshot, probe, reports, valuation as-of) reads entries ⊕ epoch through ONE database view or function. This is #48's Option S.

**Threats.**
* *Runtime role abuse, GUC spoofing, replayed plan id:* the epoch table is append-only, unique per plan and audited, so a forged epoch is visible and attributable. There is no exception path in the cost tables at all.
* *Concurrent execution:* the unique plan key.
* *Restore:* plain data.
* *Audit:* same as (a).

**The real risk is correctness, not database safety.**
* There are two sources of truth: the current-state position and layers either stay at the pre-epoch figures, which is wrong for the next posting, or they need a current-dated adjustment, which contradicts X2 (original dates). Moving the position's quantity and value without an entry would break the fold "position = Σ entries" that G1's `last_seq` discipline protects.
* Every reader across 3.6-3.8 changes, and a reader that forgets the union silently reports old figures.

**Replacement protection.** No database guard is weakened; protection depends on a single mandatory read function plus reconciliation checks (Σ entries ⊕ epoch = position).

**Verdict: safe for the schema; safe with conditions overall.** Conditions:
1. One read function used everywhere, enforced by a test that greps for direct reads of the entry table outside it.
2. A reconciliation invariant checked at commit.
3. An explicit owner decision on how the position and layers move.

It is the least risky for the database and the most expensive and error-prone for the application. I recommend it only if the owner rejects any back-dated write into cost tables.

### (c) SECURITY DEFINER function as the only writer of back-dated rows

**What changes.** A function owned by the company owner role, `SECURITY DEFINER`, with `SET search_path = pg_catalog, public`, `REVOKE EXECUTE FROM PUBLIC` and `GRANT EXECUTE` to the runtime role only. It takes the plan id and validates the plan, approval and execution rows itself; it never accepts SQL text or table names. It appends the epoch rows. Two ways to go:
* **Literally "the only writer":** revoke INSERT on entries and UPDATE on positions from the runtime role. That would also break every ordinary posting, unless all costing writes move behind definer functions, which means rewriting the costing engine. Out of scope.
* **Practical form, combined with (a):** the new entry trigger additionally requires `current_user = <company owner role>` for a back-dated entry. Only code running inside the definer function can then back-date, even if the runtime role forges plan rows. The runtime role has no CREATE in the schema (G13), so it cannot define its own definer function.

**Threats.**
* *Runtime role abuse:* reduced to calling the function with a plan id that passes the in-function validation (the same forgery as in (a), now needing the function's checks too).
* *GUC spoofing:* the pinned `search_path` defeats search-path injection, and no GUC is read.
* *Replayed plan id, concurrency:* as in (a).
* *Restore:* functions are restored with their owner. The grant model must be re-applied by `grant-company-runtime` (it converges on the catalogue). The definer must not be a superuser (verify that the company owner role is not one), because the owner can bypass all triggers anyway.
* *Audit:* the function writes the audit event itself, inside the same transaction.

**Replacement protection.** As in (a), plus the definer boundary.

**Verdict: safe with conditions.**
1. Use it on top of (a), not instead of it.
2. Fixed `search_path`, no dynamic SQL, minimal arguments.
3. EXECUTE granted only to the runtime role, and the grant recorded in `CompanyRuntimePrivileges`.
4. The owner role is not a superuser.
5. The function body is small and reviewed; it validates and appends, and planning stays in the application.

### Not acceptable (for the record)

* **Relaxing `f_589a0a19`** by `CREATE OR REPLACE` with an epoch condition: unnecessary (F1/GREATEST), and it creates the path F2 warns about.
* **A session GUC** (`SET hasm.epoch_plan = ...`) as the condition: the runtime role can set any GUC, so it is a general back-dating bypass. **Unsafe.**
* **Replacing `k_f356c89a`:** needs DROP, which the guard refuses. Only acceptable with a separate, explicit owner exception to the forward-only rule. Not recommended, because 0020 DB-1 shows the additive alternative works.

### 4. Threat summary per option

| Threat | (a) entry trigger + plan rows | (b) epoch table + union | (c) definer writer (+a) | GUC relaxation (rejected) |
|---|---|---|---|---|
| Runtime-role abuse | needs forged, audited, append-only rows in one transaction; stronger than today (F1 closed) | forged epoch visible, no cost-table path | additionally needs the definer's checks; the runtime role cannot back-date directly | one `SET` suffices: **bypass** |
| GUC spoofing | n/a (rows only) | n/a | n/a (pinned search_path) | **bypass** |
| Replayed plan id | execution row from the current transaction; unique per plan | unique per plan | same as (a) | not prevented |
| Concurrent execution | unique execution row plus position locks | unique plan key | same | depends on the application |
| Restore / COPY | post-data triggers; no false refusals | plain data | functions restored; re-run grants | n/a |
| Audit chain | events in the same transaction; optional deferred "audit present" check | same | the function writes the event | not enforced |
| Ordinary movements | now refused in the DATABASE when back-dated (stronger) | unchanged | unchanged plus definer boundary | weakened |

### 5. Tests required before enabling (PostgreSQL, isolated cluster)

**Ordinary movements stay protected (these must fail without the new guard; run each mutation):**
1. A runtime-role connection inserts a back-dated entry and updates the position with `GREATEST`. Today this **passes** (proves F1); after the migration it is refused. Cover receipt, issue, transfer out and in, and reversal.
2. Every existing costing, posting, reversal, adjustment and negative-stock suite unchanged: same counts, all green.
3. The application path still answers `backdated_not_supported` first (G15 unchanged).
4. G1-G14 byte-identical: a catalog test compares `pg_get_functiondef` and `pg_get_constraintdef` of every existing guard before and after the migration (pattern of PhysicalNamingAcceptanceTests).

**Negative tests for the exception:**
5. A back-dated entry of an operation with no epoch row is refused.
6. An epoch row whose plan is Proposed, Rejected, Cancelled or Stale is refused.
7. Approved, but the approver is the creator, or the fingerprints differ: refused.
8. The execution row is missing, or was created in an EARLIER transaction (replay of an executed plan id): refused.
9. A second execution of the same plan, concurrently: exactly one succeeds, and the other blocks and then fails.
10. A GUC set to a plan id by the runtime role has no effect.
11. An epoch entry dated in a Soft Closed or Final Closed period: refused.
12. A second plan on a position that already has an epoch: refused (X5). A kind-4 reversal of an operation rewritten by an epoch: refused (X6).
13. A storno without a rebook, an operation in the epoch table without entries, or a plan not set to Executed by commit: refused by the deferred trigger.
14. The audit event for the plan id is absent: refused (if the deferred audit check is adopted), and the chain verifies after execution.
15. (c) only: a direct runtime-role INSERT of a back-dated epoch entry outside the definer function is refused (`current_user` check); the function with a mismatched `search_path` cannot be hijacked; EXECUTE is not granted to PUBLIC.
16. Restore: a company dump with epochs, trial-restored, gives an identical catalog and data, and the schema gate accepts it.
17. Idempotency: re-executing an executed plan returns the existing execution and writes nothing.

### 6. Recommendation

**Do not approve X1 as worded.** Replace it with an owner decision that approves an ADDITIVE company migration. It weakens no existing safeguard and strengthens the database:

1. Keep G1-G14 unchanged, with no `CREATE OR REPLACE` of guard functions.
2. Option **(a)**: a new entry-level back-date guard (closes F1) with a narrow exception bound to plan, approval and execution rows created in the same transaction, an open-period check, and X5/X6 as database rules.
3. Kinds 1-3 plus an append-only epoch side table and lineage (the 0020 DB-1 precedent), instead of a kind-5 CHECK replacement.
4. Optionally harden with **(c)** (a SECURITY DEFINER writer plus a `current_user` check) before enabling in production.

Choose (b) only if the owner rules out any back-dated write into cost tables; it is then a larger application change, not a database-safety question.

Separately, and independent of 3.8D, two items should be tracked:
* F1: today's database does not refuse back-dated cost entries. The (a) guard fixes it, and could ship as its own strengthening slice.
* F2: the migration guard does not detect `CREATE OR REPLACE` of an existing guard function or `DISABLE TRIGGER`. Extending it changes a protected gate only if it lives under `tools/verify`; `MigrationScript.cs` is application code.

**Not verified:** nothing was executed on PostgreSQL. F1 follows from the text of G1 and G4 (no entry-date rule exists), and the proposed tests 1 and 4 would prove it. The company owner role's superuser status was not checked.
