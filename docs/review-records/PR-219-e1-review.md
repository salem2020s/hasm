# Independent review record

* **Pull request:** #219 (Phase 3.8D executor slice E1: pure replay derivation `EpochSchedule.Derive`; Draft; author sonnetX1A). Reviewed against the merged executor plan (`docs/PHASE-3.8D-EXECUTOR-PLAN.md` at 22c9d23) and the merged T2 design (`docs/PHASE-3.8D-T2-AUTHORIZATION-DESIGN.md` at 6ee0653, section 13 and the epoch text of 4.4).
* **Head SHA reviewed (full 40 characters):** 72881204c959adfa51a352a2b21f19fd7431591a
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author); model family Claude Opus. Prompt (summary): "review the FULL diff against origin/main:
  1. whether the allow-list change in `PlanExecutionDisabledTests` is exactly as narrow as it must be;
  2. correctness against `CostReplay` for FIFO and average, N29 in both directions, the section 6 refusals and the `epoch_*` codes, re-running at least two mutations;
  3. whether the deterministic keys meet T2's epoch-text needs for E and later O;
  4. whether the E1b split and its scope are acceptable;
  5. whether the local evidence is sufficient;
  6. whether the slice is inert."
* **Scope read:**
  * The full diff `origin/main...7288120`: 4 files, +608/−2. Commits 6c21fbc, c193d83 and 7288120 are based on 22c9d23. The files are:
    * `src/backend/Hasm.Infrastructure/Costing/EpochSchedule.cs` (new);
    * `tests/unit/Hasm.Infrastructure.Tests/EpochScheduleTests.cs` (new);
    * `tests/integration/Hasm.Database.IntegrationTests/PlanExecutionDisabledTests.cs`;
    * `docs/PHASE-3.8D-EXECUTOR-PLAN.md`.
  * For context:
    * `CostFlowSnapshot.Model.cs` and `CostReplayFlow.cs` (`ReplayFlowPosition` carries `OpenLayers`);
    * migration 0014 (cost operation, entry, layer and consumption tables);
    * the 0036 `EpochMember` table;
    * the PR body and its comments (none).
* **Checks run (commands and results):**
  * **Targeted unit tests** (through `cpu-gate.sh build`): `dotnet test tests/unit/Hasm.Infrastructure.Tests --filter FullyQualifiedName~EpochSchedule` passed 16 of 16 (14 schedule tests and 2 reachability pins).
  * **Mutations** (each applied with `sed` to `EpochSchedule.cs`, run with the same filter, then reverted; the tree was clean afterwards). Every mutation was killed:

    | # | Mutation | Origin | Killed by |
    |---|---|---|---|
    | M1 | Suffix check removed | Author's | `Changed_entries_that_are_not_a_contiguous_suffix_of_their_position_are_refused` |
    | M2 | Per-entry figure check removed (`epoch_schedule_inconsistent` for event and rebook) | Author's | `A_replay_whose_figures_or_final_state_do_not_match_the_plan_is_refused` |
    | M3 | Rebook operations written in reverse order | Mine | N29 theory (FIFO and average) and both property tests (4 failures) |
    | M4 | Negative check removed (`epoch_storno_negative`) | Mine | `A_replay_whose_figures_or_final_state_do_not_match_the_plan_is_refused` |

  * **Probe of the allow-list (finding H1).** Two local changes, not committed:
    * a new type `Hasm.Api.OdcProbe.EpochEntryPlan` with `internal static Task ExecuteReplayAsync(IServiceProvider, CancellationToken)`;
    * a member `internal static Task ExecuteAsync(CompensationPlanService, CancellationToken)` on `EpochSchedule`.

    With both in place:
    * `scripts/test-pg/run-isolated.sh <wt> --build --filter FullyQualifiedName~PlanExecutionDisabledTests` (isolated cluster, through the CPU gate) passed 4 of 4, including `No_executor_type_or_entry_point_exists_in_the_backend`;
    * `EpochScheduleReachabilityTests` passed 2 of 2.

    The probe was then removed (`git status` clean).
  * **Hosted CI on the exact head, read by REST** (`gh api .../commits/7288120.../check-runs`). All succeeded: backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan. A skipped duplicate of each also ran.
  * `node tools/verify/verify-head.js` (main's copy) could not run: it calls GitHub GraphQL, which is blocked in this session (HTTP 403).
  * **Merge with current main:** `git merge-tree --write-tree origin/main 7288120` is clean.
* **Findings:** 1 High (H1), 1 Medium (M1), 3 Low (L1 to L3), 1 note (N1). Each is listed below.
  * **High H1: the allow-list weakens the owner-condition safety test in a way a future executor can use.** Location: `PlanExecutionDisabledTests.No_executor_type_or_entry_point_exists_in_the_backend`, the `allowed` set (c193d83).
    * The set compares **simple names** (`t.Name`) and checks nothing about the allowed types' members. So:
      * any type in any namespace of either assembly named `EpochSchedule`, `EpochScheduleResult`, `EpochOperationPlan`, `EpochEntryPlan`, `EpochLineagePlan` or `EpochRole` passes, including one in `Hasm.Api`;
      * any member added to the allowed types passes: the method-name check (`Execute|Executor|Replay|Storno|Rebook`) covers only the four plan services, not the Epoch types.
    * The probe above demonstrates both. The pin cited in the comment (`EpochScheduleReachabilityTests`) lives in another test project. Its source scan forbids only a few literal strings (`Npgsql`, `DbConnection`, `TimeProvider`, ...), so it does not catch a service-typed parameter or a type that does not contain the text `EpochSchedule`, and the comment says later slices "replace this pin".
    * Under CLAUDE.md, a PR that weakens a test is never self-merged without an owner instruction. The allow-list must therefore be made at least as strong as before for everything except the exact pure types.
    * **Required stricter form, in `PlanExecutionDisabledTests` itself:**
      * (a) Allow by **type identity**: a `HashSet<Type>` of `typeof(EpochSchedule)` and the others, compared with `t`, so the full name and assembly match. Any other type whose name matches `PlanTypeName` is still an offender.
      * (b) Assert the **shape** of the allowed types:
        * `EpochSchedule` is a static class. Its declared methods are exactly `Derive` and the private `Inconsistent`, with `Derive`'s signature pinned as `(CostFlowSnapshot, CostFlowComparisonResult, CancellationToken) -> EpochScheduleResult`.
        * The records and the enum declare no methods beyond the compiler-generated record members.
        * No method of any allowed type returns `Task` or `ValueTask`.
        * No field, property, constructor parameter or method parameter of an allowed type has a type from `Npgsql`, `System.Data`, `System.Net`, `IServiceProvider`, or any backend type with a constructor dependency (a service).
      * (c) Apply the method-name regex to the allowed Epoch types as well.

      With (a) to (c), the change is a narrowing exception that cannot hide an executor.
  * **Medium M1: the E1b split is not recorded, and E1 is not reconciled with T2 section 13.**
    * T2 section 13, Change 1 (merged on main in 6ee0653), requires E1 to emit the full epoch text: O, E, L, C and R lines with deterministic operation keys. This PR delivers the E part and the operation skeleton.
    * Neither the plan nor the PR body states that layers, consumptions and the rest are deferred, or what "E1b" contains. The plan's E1 text still promises "the expected final position and layer state". Only the open layers are passed through from the revised trace; their layer identity, sequence and source are not derived.
    * **Splitting is acceptable** (see the answer to point 4), but it must be written down. Merge main into the branch (T2 is now on main), and add an E1b entry to `docs/PHASE-3.8D-EXECUTOR-PLAN.md` stating the scope below, with the dependency **E1b before E2b** and before slice A's commit-time epoch comparison. E2b's tests already require "positions, layers and values equal E1's expectation".
    * Required E1b scope:
      * **L lines:** layers created by the event and the rebook, with their sequences, entry sequence and source layer as item/warehouse/sequence.
      * **C lines:** consumption rows, including the storno's `restores` rows that mirror the original consumptions.
      * **R lines:** the post-epoch remaining figures of every layer created, consumed or restored.
      * **O-line metadata:**
        * the deterministic operation key that E2b will write;
        * source type and source id (the event's as `execution-posting`);
        * reverses-operation NULL;
        * the creator as an input.
      * **The byte-exact T2 epoch-text builder**, with T2's known-answer vectors.
      * The schema-pinning test of T2 4c, unless slice A takes it.
  * **Low L1: the rebook write order is not checked; only the trace order is.**
    * `rebookPlans` groups entries by original operation in first-appearance order. The check `epoch_order_inconsistent` runs on `rebookOriginals`, which is in trace order, not on the flattened written order.
    * The two are equal only while every operation's entries are contiguous in (date, id). That holds today because a document posts its entries consecutively under the company posting lock, but nothing verifies it here. On one position, the ledger proof would catch an interleave as `epoch_schedule_inconsistent`; across positions, nothing would.
    * Fix: assert ascending (date, original entry id) on the flattened rebook entries of `operations`, failing closed.
  * **Low L2: the final-state proof is not total.** The end-state comparison iterates `comparison.Positions` only. A closure position absent from it is never compared with `running` or with the revised trace. Fix: require every closure position (every key of `running`) to be in `comparison.Positions` and in `RevisedTrace.Positions`, and compare each.
  * **Low L3: the item is not checked.** T2's E line takes the item from the snapshot (`snapshot.ItemId`), but `Derive` does not check that every entry's `ItemId` equals it. Fix: add the check (`impact_history_inconsistent`).
  * **Note N1: the fingerprint is not the T2 epoch digest.** `Fingerprint` serialises the records with System.Text.Json, whose decimal output follows each value's scale. It is deterministic for identical inputs but not canonical, and it must not be used as, or confused with, T2's byte-exact epoch digest. Say so in its doc comment.
* **Answers to the six points:**
  1. **The allow-list entry is too broad:** H1 above, with the required stricter form.
  2. **Correctness.** The derivation is sound for what it covers:
     * stornoed entries are exactly the changed ones, as a contiguous suffix per position, in whole operations, with no single-leg transfer;
     * stornos are written by global descending entry id, then the event, then rebooks in trace order with ascending (date, id) required;
     * original dates are kept (X2);
     * lineage is a bijection;
     * the independent signed-sum ledger proof checks each event and rebook entry's running figures and the end state.

     Section 6 refusals that the snapshot can show are refused (reversal in history, unsupported kinds, outside the closure). The others (obligations, settlements, mixed provenance, unposted journals, closed periods, executed-plan closure) remain the planner's and the database's, as the plan text now says. I verified this only by reading the snapshot reader.

     Tests:
     * the worked examples cover average and FIFO;
     * the N29 theory covers both methods and both transfer directions on equal dates;
     * the property test covers 400 random histories per method against an independent oracle;
     * the fail-closed codes each have a test, and my mutations M1 to M4 were all killed.

     FIFO layer correctness is not proven here (E1b).
  3. **Deterministic keys.**
     * **For T2's E lines:** sufficient, once L3 is fixed. (position, sequence) is assigned in write order from each position's last sequence, which is checked against the snapshot. The origin is the original entry id for storno and rebook, and none for the event. Kind, direction, date, quantity, amount and the running figures are all present. No generated id is used.
     * **For O lines:** role, original operation, kind and date are present. The key, source type, source id and creator are not; they belong to E1b (M1).
  4. **The split is acceptable,** as a correct, tested subset, provided it is recorded with the full E1b scope listed in M1. As stated ("layers and consumptions") it is too narrow: R lines, O metadata and the epoch-text builder with vectors must be included.
  5. **The local evidence is sufficient for this head.**
     * The full suite ran locally on 6c21fbc.
     * c193d83 changes one integration test, and 7288120 changes the sequence keys and their tests. Both changes are covered by the targeted runs and by hosted CI on the exact head, which passed in full: backend-build-test and postgres-integration, plus the five verification jobs.

     The fixes for H1, M1 and L1 to L3 change the head, so the exact-head gates repeat.
  6. **The slice is inert.**
     * All new types are internal; nothing registers or references them (reachability pin, and the container check of `No_service_of_the_running_API_...` passes).
     * There is no migration, no grant and no protected file. `PlanExecutionDisabledTests` is not on the protected list, but see H1 for the never-self-merge rule on weakened tests.
* **Blocker or High findings open:** H1 (High) open; M1 (Medium) open; L1 to L3 (Low) open.
* **NOT VERIFIED:** the following four items.
  * `verify-head.js`: blocked by GraphQL; the check runs were read by REST instead.
  * No full local suite at 7288120.
  * The section 6 refusals that live in the snapshot reader were judged by reading only.
  * Whether the real engine's FIFO layer and consumption rows match the open layers passed through (E1b and E2b).
* **Verdict:** request changes
