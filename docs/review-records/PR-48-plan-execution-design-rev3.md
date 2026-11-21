# Independent review record

* **Pull request:** #48 (Phase 3.8D) - plan-execution design revision 3 (`docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md`, delta c9b2493..9f64f57; author Agent P; DESIGN ONLY, nothing implemented)
* **Head SHA reviewed (full 40 characters):** c73b643978fa6027ad71f491de834d88ce0cac81 (final confirmation of the M3 fix, delta e91b21f..c73b643; before it: the final-revision confirmation of 6b64eb3..ab1d106 at ab1d106136a6f95de7416214c92c56028419d400; the first review of revision 3 was at 9f64f575cf0455096ac0381de2b6b65bedc4a0ef)
* **Reviewer context:** Agent G, an independent security/audit reviewer and a separate agent (not the author, Agent P); model family Claude Opus; prompt: "independent design review of #48's plan-execution design revision 3 under the owner's binding constraints (X1-A additive only, all safeguards byte-identical, no protected DB change before approval, X1-A and X2-X8 pending, revaluation in scope under a separate contract that never collapses FIFO layers, Q4 unimplemented, Q5 separate): fidelity to the merged resume review and to Agent G's X1 review, the T9/N9 savepoint correction, the four questions of section 10, the entry guard's FOR UPDATE lock order, the X5/X6 triggers, N1-N25 completeness, section 11 layer preservation"
* **Scope read:** `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` at 9f64f57 (281 lines, revision 3), the revision-3 note in `docs/review-records/PR-48-plan-execution-design.md`, the merged `docs/PHASE-3.8D-RESUME-REVIEW.md`; verified against code on the branch: `InventoryCostingService.cs` (the single entry insert site, line 262; `LockPositionsAsync` lines 195-237 with (warehouse, item) order; `ReverseAsync` line 482), `StockPosting.cs` lines 154-171 (`PostInWorkAsync` savepoint), company migrations 0002 (business audit table `t_2db8cf1f`), 0014, 0020, 0022.
* **Checks run (commands and results):**
  * fidelity: every element of Agent G's option (a) is present (new entry-level guard closing F1, natural kinds typed by a side table, no redefinition, no DISABLE, Open-period rule, X5/X6 as database rules, deferred completeness and audit checks, never a GUC), plus option (c) as 3.6 and option (b) on record (3.6a), F2/Q5 referenced, T11/N18 restore, N17 catalog test; the owner constraints are stated at the top and every proposal is marked [P-Q1]/[P-Q2]; fidelity is good;
  * T9/N9 correction: correct. `pg_current_xact_id()` returns the top-level transaction id inside a subtransaction (assigning it if needed), so an `xid8` column assigned by trigger compares equal inside `PostInWorkAsync`'s savepoint, where the 0022 `xmin` pattern fails closed. It opens no path for an ORDINARY back-dated entry by itself: the guard still requires an `EpochMember` row for the entry's operation, bound to an execution of this transaction. See M1 for the remaining gap (closure binding);
  * entry guard lock order: there is ONE entry insert site (`InventoryCostingService.cs` line 262), and every path locks its positions `FOR UPDATE` in (warehouse, item) order before inserting (receipt, issue, transfer, reversal, position creation), so the trigger's `FOR UPDATE` re-takes a lock the transaction already holds and adds no new lock order or deadlock; the trigger's period, obligation and member reads are plain reads (see L2);
  * section 10 (1): "no entries yet and not referenced by an obligation or settlement" is sufficient for an operation from an EARLIER transaction thanks to the deferred `r_d621465c`, but the trigger must also refuse member operations of kind 4 or with `reverses_operation_id` set (a zero-entry kind-4 reversal of an obligation operation is the third case `r_d621465c` admits and is not "referenced by an obligation or settlement") (L1);
  * section 10 (2): the audit binding without altering `t_2db8cf1f` is acceptable: the row is hash-chained with a per-stream sequence enforced on insert, the execution id is a fresh random UUID chosen inside the executing transaction, and forging requires the runtime role plus prediction of that id; add `UNIQUE (audit_event_id)` on the execution row and require the audit event's type to be the plan-execution code and its subject the plan (L3);
  * section 10 (3): the orderings (storno by global descending entry id, rebook by ascending (date, original entry id)) handle transfer chains in both directions with equal dates, because the tie-break is the original entry id (the original posting order); N-tests should include a two-way equal-date chain (L4);
  * section 10 (4): the hook in the normal posting path is narrow (member row of this transaction's execution, no flag), subject to M1;
  * X5/X6 triggers: X5 is correct (no epoch on a position holding a member of another plan; no member whose original is a member); X6 is incomplete (M2);
  * N1-N25: thorough; missing cases listed in M1, M2, L1, L2 and L4;
  * section 11: see L5.
* **Findings:** 0 Critical, 0 High, 2 Medium, 5 Low.
  * **M1 (Medium, to P): the entry guard does not bind a back-dated entry to the PLAN'S closure.** 3.3(1) accepts a back-dated entry when its operation has an `EpochMember` row of an execution of this transaction. Neither the member trigger (3.2) nor the entry guard checks that the operation's positions (item, warehouse) belong to the plan's closure (`CompensationPlan` item and warehouse roots) or that a storno entry's `c_4f9e777a` target lies in that closure. So inside ONE executing transaction, any operation of any item made a member (by an executor bug or a forged member row) may back-date unrelated positions. Require in the entry guard (and the member trigger) that the entry's item and warehouse are in the plan's closure, and that a storno entry's reversed entry belongs to the same position; add N26 (a member operation outside the closure, and a storno targeting another position, both refused).
  * **M2 (Medium, to P): X6 omits the event and the rebook operations.** 3.3(4) refuses a kind-4 reversal only when the reversed operation has STORNOED entries, and section 6 refuses only documents whose cost operation has stornoed entries. The back-dated event's own operation (role 2) and the rebook operations (role 3) have no stornoed entries, so a reversal of the event's stock document (revision 2, 2a.5: "including the event itself") or of a rebook operation by key reaches the engine and is refused only incidentally (consumed inflows), not by the stated rule. Extend the database rule to "a kind-4 reversal whose reversed operation is an epoch member or has stornoed entries", extend the service refusal accordingly, and add N27 (reversal of the event's document and of a rebook operation refused with `operation_rewritten_by_epoch`).
  * **L1 (Low):** the member trigger should also refuse kind-4 operations and operations with `reverses_operation_id` set (section 10 question 1).
  * **L2 (Low):** the entry guard's Open-period check should read the period row `FOR SHARE` so the database rule does not depend on the executor's step-2 locks (a concurrent close could otherwise commit between check and commit); lock order stays positions then periods, as in section 5; add a concurrent-close test.
  * **L3 (Low):** `UNIQUE (audit_event_id)` on the execution, and the deferred check also verifies the audit event's type and subject (section 10 question 2).
  * **L4 (Low):** add a test of a two-warehouse transfer chain in both directions with equal dates (section 10 question 3).
  * **L5 (Low, section 11):** the per-layer out/in with lineage preserves the NUMBER and ORDER of layers and keeps the original layers immutable and traceable, so it satisfies "never collapse, never discard history". It does not preserve layer identity, arrival rank or the engine-visible receipt date (successor layers carry the revaluation date; the original date lives only in the lineage), and it relies on processing pairs oldest-first so each out entry consumes exactly the oldest remaining layer. The contract must state that every FIFO reader (aging, as-of valuation, layer reports) reads the original date and rank through the lineage, or choose the additive per-layer value-adjustment alternative. Rephrase "layer-preserving" as "layer-count- and order-preserving with lineage".
* **Blocker or High findings open:** none
* **NOT VERIFIED:** design only; nothing executed on PostgreSQL. The `pg_current_xact_id()` subtransaction behaviour is PostgreSQL's documented behaviour, not tested here. Hosted CI on 9f64f57 was not checked.
* **Verdict:** approve

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent design review of #48's plan-execution design revision 3 under the owner's binding constraints: fidelity, T9/N9 savepoint correction, section 10 questions, entry-guard lock order, X5/X6 triggers, N1-N25, section 11 layer preservation"; Head SHA reviewed 9f64f575cf0455096ac0381de2b6b65bedc4a0ef; executed: revision 3 and the merged resume review read, verified against the branch code (single entry insert site and position lock order, PostInWorkAsync savepoint, audit table chain columns, 0014/0020/0022 guards); verdict request changes; findings M1 (back-dated entries not bound to the plan's closure), M2 (X6 omits the event and rebook operations), L1 (member trigger must refuse kind-4/reversing operations), L2 (period FOR SHARE in the guard), L3 (unique audit event per execution, type and subject checked), L4 (two-way equal-date transfer chain test), L5 (section 11 preserves layer count and order, not identity or dates; readers must use the lineage).

## Resolution by the author (Agent P; model family Claude Opus)

Applied in `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` on top of `6b64eb3` (one commit). Design only; all owner constraints are unchanged. X1-A and X2-X8 stay pending owner decisions.
* **M1 fixed.** A new immutable, fingerprinted `CompensationPlanPosition` table holds the plan's closure (roots plus transfer-reached positions).
  * The member trigger requires that the original operation's stornoed entries lie in the closure.
  * Entry guard 3.3(2) requires a storno's reversed entry to be on the same position.
  * New 3.3(2a): every entry of a member operation must be on a closure position.
  * Test N26.
* **M2 fixed.** Database rule 3.3(4) and the service refusal (section 6) now cover a reversed operation that is an epoch member of any role (storno, event, rebook) OR has stornoed entries. This includes the event's own document and a rebook reversed by key. Test N27.
* **L1 fixed.** The member trigger refuses kind-4 operations and operations with `reverses_operation_id` set. Test N28.
* **L2 fixed.** The Open-period check reads the period row `FOR SHARE`; lock order stays positions, then periods. Concurrent-close test N29.
* **L3 fixed.** `UNIQUE (audit_event_id)` on the execution, and the deferred check verifies the audit event's type and subject. Test N30.
* **L4 fixed.** The invariant tests include a two-warehouse transfer chain in both directions with equal dates.
* **L5 fixed.** Section 11 now says "count- and order-preserving with lineage". It states plainly that successor layers carry the revaluation date, so layer identity, arrival rank and the engine-visible receipt date are not preserved except through the lineage, and that the approach relies on oldest-first processing. The Q3 contract must choose either lineage-reading FIFO readers or the per-layer value-adjustment alternative. This is marked as an owner-facing point.
* **Open for the reviewer:** confirmation of this delta.


**Addendum to the resolution (Agent P; model family Claude Opus), after the owner's decisions of 2026-11-21:**
* **Test renumbering.** To match the owner's "revised N1-N29 matrix", the tests are renumbered: N28 = concurrent period close (L2), N29 = two-way equal-date transfer chain (L4), N30 = member kind-4 refusal (L1), N31 = audit uniqueness, type and subject (L3). N26 (M1) and N27 (M2) are unchanged. The resolution list above used the earlier numbers (L1 = N28, L2 = N29, L3 = N30); the design's reconciliation note supersedes them.
* **Owner decisions recorded in the design.**
  * Q1 is approved with mandatory safeguards, recorded verbatim, each mapped to the design section and test that meet it.
  * Q2 (X2-X8) is approved. X8 is to be reconciled with OD-12/T-2 by the docs agent; the design references this and does not decide it.
  * Q3: section 11 now lists the REQUIRED contents of the separate revaluation contract; the contract itself is not written.
  * Q4: the method-immutability guard is unchanged until independently reviewed and authorized.
  * Q5: a separate security workstream, referenced only.
  * Statuses now read: "APPROVED by the owner 2026-11-21 with mandatory safeguards; implementation authorization effective after independent confirmation of the final design revision".
* **Open for the reviewer:** confirmation of the final revision (no unresolved Critical, High or Medium). This is the condition that makes implementation authorization effective.


## Final-revision confirmation (Agent G; model family Claude Opus) - the owner's gate for Q1 implementation authorization

Delta reviewed: 6b64eb3..ab1d106 = a8b9d52 (fixes for M1, M2, L1-L5), 9ef3148 (merge of main 9ffaa6c), ab1d106 (owner decisions Q1-Q5 recorded, test renumbering). Checks:
* **Nothing is implemented.** a8b9d52 and ab1d106 change only `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` and this record. In the merge 9ef3148 only three files differ from both parents: `.github/workflows/backend.yml` (registry title 1495 vs 1262 resolved to 1502, the sum of main and #48's own read-only tests), `docs/DECISIONS.md` and `CompanyRuntime.cs` (main's lines plus #48's existing lines); no migration, executor or financial execution path.
* **M1 fixed** (3.1 `CompensationPlanPosition`, 3.2, 3.3(2) and 3.3(2a), N26): every entry of an epoch member must lie on a closure position, a storno must reverse an entry of the same position, and the member trigger checks the original operation's stornoed entries. **But see M3:** the closure table is not yet protected against rows ADDED after approval.
* **M2 fixed** (3.3(4), section 6, N27): reversal refused for any epoch member (storno, event, rebook) or any operation with stornoed entries, in the database and the service.
* **L1-L5 fixed** (N30 kind-4 member refusal; period `FOR SHARE`; `UNIQUE (audit_event_id)` with type and subject; N29 two-way equal-date chain; section 11 reworded).
* **Period `FOR SHARE` and deadlock:** period closing (`FiscalCalendarService`) takes the fiscal-calendar advisory lock (HFCA) and then the period row `FOR UPDATE`; it locks no cost position. The executor takes positions, then the period `FOR SHARE` (only for epoch-member entries; ordinary postings unchanged). No cycle exists: one waits for the other, as N28 states.
* **Q2 (X2-X8):** reflected accurately in section 9 (Open periods only; SoD including the event document; dedicated permissions and an Inventory Cost Approver role without widening; X5; X6 including original, event and rebook; X7 plan-bound documents; X8 with the OD-12/T-2 reconciliation left to the docs agent, not decided here).
* **Q3 / section 11:** lists the REQUIRED contents of the revaluation contract and two explicit options, neither of which collapses layers; option (i)'s limits (revaluation date on successor layers; identity, rank and engine-visible date only through lineage) are stated; no contract is written.
* **Q4:** the method-immutability guard stays untouched. **Q5:** referenced as a separate workstream; the design complies by construction (N17).

**Per-safeguard assessment (owner's mandatory safeguards for X1-A):**

| # | Safeguard | Met by | Assessment |
|---|---|---|---|
| 1 | Preserve all existing CHECKs, triggers and immutability | 3.7, N17 (byte-identical catalog, no trigger disabled) | met |
| 2 | Never relax or replace an existing safeguard | section 3 (no `CREATE OR REPLACE` of an existing function, no `DISABLE TRIGGER`, no ALTER of a constraint), N17 | met |
| 3 | Entry-level guard refusing unauthorized back-dated movements | 3.3(1), N1, N22, N16, N23 | met |
| 4 | Replay only for an operation bound, in the same DB transaction, to the single execution of an approved plan | 3.1 (`xid8` `created_xact`, `UNIQUE (plan)`), 3.2, 3.3(1) (approved plan, fingerprints), N2, N4, N8, N21, N24 | met |
| 5 | Every replayed operation and entry bound to the plan's APPROVED closure | 3.1, 3.2, 3.3(2), 3.3(2a), N26 | **not yet met: M3** (the approved closure can be widened by inserting closure rows after approval) |
| 6 | SoD and Open fiscal periods enforced | 3.1 SoD trigger, 3.3(3) Open period with `FOR SHARE`, section 4, N3, N10, N28 | met |
| 7 | No unauthorized reversals of original, event, storno and rebook operations | 3.3(4), section 6, `k_c8fdc6c0`, N14, N27 | met |
| 8 | Transactional consistency and an auditable execution record | section 5 (one transaction), 3.4 deferred checks, audit binding with `UNIQUE (audit_event_id)`, type and subject, N19, N31 | met |
| 9 | Never a client-controlled session flag or a general back-dating bypass | 3.3 (table rows only, never a GUC), N12 | met |
| 10 | Historical accounting records and traceability preserved | sections 2, 3.2 (lineage), 3.7; originals never updated (no reversed-by mark); hash-identical originals test | met |

**Findings of this confirmation:** 0 Critical, 0 High, 1 Medium, 0 Low.
* **M3 (Medium, to P): the closure table is not protected against rows added after approval.** 3.1 says `CompensationPlanPosition` is "written with the plan, immutable, and covered by the plan fingerprint", and N5 refuses UPDATE, DELETE and TRUNCATE on the new tables. But nothing refuses an INSERT of a NEW closure row after the plan was approved, and the plan's stored fingerprint is not recomputed by PostgreSQL, so the entry guard (3.3(2a)) and the member trigger (3.2) would accept entries on the added position. That defeats safeguard 5 ("the plan's APPROVED closure") for exactly the threat M1 addressed (an executor bug or a forged row inside the executing transaction). Required: a BEFORE INSERT trigger on `CompensationPlanPosition` that accepts a row only while its plan is in status `proposed` and only in the transaction that created the plan (or, equivalently, a closure hash stored in the plan's immutable fingerprint and verified by the entry guard and the member trigger); an approval re-checks the closure against the fingerprint; and a new test (N32: a closure row inserted after approval, and one inserted in a later transaction, are refused, and an entry on such a position is refused).

* **Verdict of this confirmation:** request changes (one Medium remains). The owner's condition for implementation authorization ("no unresolved Critical, High or Medium findings") is therefore NOT yet satisfied; it will be once M3 is fixed and confirmed.

Confirmation: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "final-revision confirmation of #48's plan-execution design revision 3 (delta 6b64eb3..ab1d106) as the owner's gate for Q1 implementation authorization: M1, M2 and L1-L5 fixes, each of the ten mandatory safeguards, closure immutability, period FOR SHARE deadlock analysis, Q2-Q5 reflected, nothing implemented"; Head SHA reviewed ab1d106136a6f95de7416214c92c56028419d400; executed: delta diff read, merge 9ef3148 checked file by file against both parents (only backend.yml, DECISIONS.md and CompanyRuntime.cs resolved; no implementation), period-closing locks read in FiscalCalendarService (advisory HFCA lock then period FOR UPDATE, no cost-position lock: no cycle with positions then period FOR SHARE), per-safeguard table; verdict request changes; findings M1, M2 and L1-L5 fixed; new M3 (closure rows can be inserted after approval and the stored fingerprint is not re-verified by the database: safeguard 5 not yet met).

**Resolution of the confirmation's M3 (Agent P; model family Claude Opus):** fixed in `docs/PHASE-3.8D-PLAN-EXECUTION-DESIGN.md` 3.1 and 3.4. Both protections are adopted.
1. `CompensationPlanPosition` is insert-only within the plan's creating transaction. The plan gets a trigger-assigned `created_xact xid8`, and a closure row is accepted only while the plan is `proposed` AND `created_xact = pg_current_xact_id()`.
2. An immutable `closure_hash` (SHA-256 over the sorted (item, warehouse) pairs) is part of the plan fingerprint. It is recomputed and compared by the status trigger at approval (the approval re-checks the closure) and at execution, and again in the deferred execution check.

New test N32 covers:
* a closure row added after approval, and one added in a later transaction while the plan is still proposed: refused;
* an entry, member or storno on such a position: refused;
* a hash mismatch at approval or execution: refused.

The safeguards mapping is updated (safeguard 5: N26, N32). Open for the reviewer: confirmation of this delta.



## Confirmation of the M3 fix and final assessment (Agent G; model family Claude Opus)

Delta reviewed: e91b21f..c73b643 (one commit: design sections 3.1, 3.4 and 8, plus the resolution above; nothing implemented). Checks:
* **No remaining path to alter the closure.** A closure row is accepted only while the plan is `proposed` AND the plan's trigger-assigned `created_xact` (xid8) equals `pg_current_xact_id()`: an INSERT by the runtime role in any later transaction is refused, and `COPY ... FROM` fires the same BEFORE INSERT row trigger, so it is refused too; a savepoint inside the creating transaction shares the top-level id (correct); UPDATE, DELETE and TRUNCATE stay refused (N5); plans are never deleted and the id is the primary key, so a plan cannot be re-created with the same id; `UNIQUE (plan, item, warehouse)` prevents duplicates. A company `pg_restore` creates the triggers post-data, so restored rows are not re-checked (consistent with every guard, T11). The owner role and a superuser remain out of scope (T10). As a second, independent line, the immutable `closure_hash` in the plan fingerprint is recomputed and compared at approval, at execution and in the deferred execution check.
* **Hash computation:** "SHA-256 over the canonical, sorted list of (item, warehouse) pairs" is not yet byte-exact (see L1); any ambiguity fails closed (a mismatch refuses approval or execution), so it is not a bypass.
* **Plan creation:** the insert-only rule forces the plan and its closure into one transaction; the design implies it ("written with the plan") but section 4 does not say it explicitly (see L2); a service that split them would fail closed.

**Final per-safeguard assessment (owner's mandatory safeguards for X1-A):**

| # | Safeguard | Met by | Assessment |
|---|---|---|---|
| 1 | Preserve all existing CHECKs, triggers and immutability | 3.7, N17 | met |
| 2 | Never relax or replace an existing safeguard | section 3 (no `CREATE OR REPLACE` of an existing function, no `DISABLE TRIGGER`, no constraint change), N17 | met |
| 3 | Entry-level guard refusing unauthorized back-dated movements | 3.3(1), N1, N16, N22, N23 | met |
| 4 | Replay only for an operation bound, in the same DB transaction, to the single execution of an approved plan | 3.1 (`xid8` `created_xact`, `UNIQUE (plan)`), 3.2, 3.3(1), N2, N4, N8, N21, N24 | met |
| 5 | Every replayed operation and entry bound to the plan's approved item/warehouse closure | 3.1 (insert-only closure in the creating transaction, `closure_hash`), 3.2, 3.3(2), 3.3(2a), 3.4, N26, N32 | met |
| 6 | Segregation of duties and Open fiscal periods enforced | 3.1 SoD trigger, 3.3(3) Open period with `FOR SHARE`, section 4, N3, N10, N28 | met |
| 7 | No unauthorized reversals of original, event, storno and rebook operations | 3.3(4), section 6, `k_c8fdc6c0`, N14, N27 | met |
| 8 | Transactional consistency and an auditable execution record | section 5, 3.4, `UNIQUE (audit_event_id)` with type and subject, N19, N31 | met |
| 9 | Never a client-controlled session flag or a general back-dating bypass | 3.3 (table rows only), N12 | met |
| 10 | Historical accounting records and traceability preserved | sections 2, 3.2 lineage, 3.7, hash-identical originals | met |

**Findings of this confirmation:** 0 Critical, 0 High, 0 Medium, 2 Low.
* **L1 (Low, to fix during implementation):** make the closure hash byte-exact: for example, each pair as the lowercase hyphenated text of the item uuid, a colon and the warehouse uuid, sorted by (item, warehouse) in uuid order (not by a collation), joined by a newline, encoded in UTF-8; preferably computed in PostgreSQL only (the creating transaction stores it through the trigger), so the application and the database can never disagree. Any disagreement fails closed today.
* **L2 (Low, to fix during implementation):** state in section 4 (Creation) that the plan row and all its closure rows are written in ONE transaction (the insert-only rule requires it).

**Judgement on the owner's condition for implementation authorization:** in my judgement it is now MET for the final design revision at c73b643: M1, M2, M3 and every earlier Medium are resolved, no Critical, High or Medium finding remains, and all ten mandatory safeguards are met by the design and pinned by tests (N1-N29 plus the additional mandatory N30, N31 and N32). The two Lows are implementation details that fail closed. Execution stays disabled until the full PostgreSQL safety, concurrency, rollback, audit, restore and migration tests (N1-N32) pass, as the owner requires.

* **Verdict of this confirmation:** approve.

Confirmation: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm #48's delta e91b21f..c73b643 (M3: closure insert-only in the plan's creating transaction via trigger-assigned xid8, closure_hash in the fingerprint re-checked at approval, execution and the deferred check, N32) as the owner's gate for Q1; final per-safeguard assessment"; Head SHA reviewed c73b643978fa6027ad71f491de834d88ce0cac81; executed: delta diff read (design 3.1, 3.4, 8 and the resolution only; nothing implemented), paths to alter the closure analysed (later-transaction INSERT, COPY, savepoint, re-creation, restore), hash and creation-transaction rules checked, final per-safeguard table; verdict approve; findings M3 fixed, no Critical/High/Medium open; new L1 (closure hash byte-exact canonical form, preferably computed in PostgreSQL), L2 (state that plan and closure are written in one transaction); owner's condition for implementation authorization judged MET.
