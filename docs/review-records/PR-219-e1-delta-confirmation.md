# Independent review record

* **Pull request:** #219 (Phase 3.8D executor slice E1, pure replay derivation). Delta confirmation 7288120..cf1706b of the review `PR-219-e1-review.md` (record 84bd646):
  * the author's fix commit cf1706b;
  * a merge of main, 0708bc6 (parents 84bd646 and 6ee0653).
* **Head SHA reviewed (full 40 characters):** cf1706b17a9b6e401707aa680c30353b65c76eec
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetX1A); model family Claude Opus. Prompt (summary): "confirm H1, M1, L1–L3 and N1:
  * H1: by type identity, the exact shape pin, banned namespaces and the name regex on the Epoch types. Re-run both probe shapes, then try to break it again (a delegate field, a Hasm record wrapping a service, a static IServiceProvider field, an extension method in another class, a nested type).
  * M1: the E1b plan entry and scope against the required list, and the corrected E1 text.
  * L1–L3: check the mutations.
  * The main merge: check it with --remerge-diff."
* **Scope read:**
  * `git show cf1706b`:
    * `EpochSchedule.cs` (+18/−5);
    * `PlanExecutionDisabledTests.cs` (+62/−5: `PureEpochTypes`, `AssertPureEpochShape`, `AssertPureType`);
    * `EpochScheduleTests.cs` (+40: three new tests);
    * `docs/PHASE-3.8D-EXECUTOR-PLAN.md` (the E1 text, a new E1b section, the sequence).
  * `git show --remerge-diff 0708bc6`.
  * For comparison: the merged T2 design (6ee0653, 4.4 v4 and section 13) and the T2 revision 4 confirmation (S1).
* **Checks run (commands and results):**
  * **Safety test against probes.** Each probe ran on its own isolated cluster: `scripts/test-pg/run-isolated.sh <wt> --build --filter FullyQualifiedName~PlanExecutionDisabledTests.No_executor_type_or_entry_point_exists_in_the_backend`, through `cpu-gate.sh build`. The probe edits were local, applied by a script and reverted after every run (`git status` was clean afterwards).

    | Probe | Change | Result |
    |---|---|---|
    | P1 | `Hasm.Api.OdcProbe.EpochEntryPlan.ExecuteReplayAsync(IServiceProvider, …)` | **CAUGHT** (offender `Hasm.Api.OdcProbe.EpochEntryPlan`) |
    | P2 | `EpochSchedule.ExecuteAsync(CompensationPlanService, …)` | **CAUGHT** (the method set of `EpochSchedule` differs) |
    | P5a | `internal static IServiceProvider? Sp` on `EpochOperationPlan` | **CAUGHT** (`EpochOperationPlan.Sp: System.IServiceProvider`) |
    | P3 | a `Func<object, object>? Hook` property on `EpochEntryPlan` | **NOT caught** |
    | P4 | `internal sealed record OdcServiceBox(CompensationPlanService Service)`, carried as a property of `EpochLineagePlan` | **NOT caught** |
    | P5b | `internal static object? Sp` on `EpochOperationPlan` | **NOT caught** |
    | P6 | `internal static class OdcHelpers { static Task RunAsync(this EpochScheduleResult r, CompensationPlanService s) }` | **NOT caught** |
    | P7 | a nested `private static class Runner { static Task GoAsync(CompensationPlanService s) }` inside `EpochSchedule` | **NOT caught** |

    I ran P3, P4, P5b, P6 and P7 together; the test passed (1/1). I confirmed afterwards that all five edits were applied and compiled.
  * **Unit tests** (`dotnet test tests/unit/Hasm.Infrastructure.Tests --filter FullyQualifiedName~EpochSchedule`, through the CPU gate): base 19/19 passed. Each mutation was killed by exactly the intended test:

    | Mutation | Killed by |
    |---|---|
    | L1: written-order check removed | `The_written_order_of_the_rebooks_is_checked_not_only_the_trace_order` |
    | L3: item check removed | `An_entry_of_another_item_is_refused` |
    | L2a: foreign-position check removed | `The_end_state_must_cover_every_closure_position_in_the_comparison_and_in_the_revised_trace` |
    | L2b: a position missing from the revised trace tolerated | the same test |

  * **Main merge:** `git show --remerge-diff 0708bc6` is empty, so it is a clean automatic merge of 6ee0653 into the branch at 84bd646.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); a skipped duplicate of each also ran.
  * **Mergeability:** `mergeable_state` is clean; origin/main is 6ee0653.
  * The author reports a full local PostgreSQL suite of 1758/1758 on cf1706b. I did not re-run it; hosted postgres-integration passed on this head.
* **Findings:** 0 Critical, 0 High, 1 Medium (R1), 1 Low (R2), 1 note (R3). Each finding is listed below.
  * **Resolved H1, for the bypasses demonstrated in the first review.**
    * The derivation types are allowed by `Type` identity, so a same-named type elsewhere is an offender (P1).
    * The method set of `EpochSchedule` is pinned to `Derive` (exact signature) and `Inconsistent` (P2).
    * Static IServiceProvider-typed members are refused (P5a).
    * The name regex applies to the Epoch types.
    * The test is now stricter than it was on main, so it is not a weakening.
  * **Medium R1: the shape pin is not closed over nested types or transitive member types.** Its own comment says it pins the shape "so the exception cannot hide an executor", but four probes pass:
    * **P7:** a nested type inside `EpochSchedule` with an executor-shaped member, `Task GoAsync(CompensationPlanService)`. `GetMethods(DeclaredOnly)` does not see nested types, and the nested type's name does not match `PlanTypeName`.
    * **P3:** a delegate-typed member, `Func<object, object>`. The `System` namespace is not banned.
    * **P4:** a backend record that wraps a service. `AssertPureType` accepts any `Hasm.*` record as data without looking inside it.
    * **P5b:** an `object`-typed static field on a record.

    Required, in `AssertPureEpochShape` / `AssertPureType`:
    * (a) The allowed types declare no nested types except compiler-generated closure types (`<>c…`). Their declared methods are compiler-generated lambdas (names starting with `<`), and their fields pass `AssertPureType`.
    * (b) `AssertPureType` refuses every type assignable to `Delegate`.
    * (c) `AssertPureType` recurses, with a visited set, into the fields and properties of a `Hasm.*` record (and into type arguments), so a wrapped service is found.
    * (d) The records declare no static fields.

    Each of P3, P4, P5b and P7 should then fail the test, which can be shown with a mutation-style note in the PR.
  * **Low R2: an extension method in another class (P6) is outside what this test can see.** Code in any type whose name does not match `PlanTypeName` was never covered by the name-based test, before E1 or after, so this is not an E1 regression.
    * Recommendation: when E2 and E3 replace `EpochScheduleReachabilityTests` with "the list of their allowed callers", also pin by IL or reflection that only the named callers reference `EpochSchedule.Derive` and the Epoch types. The current source scan already forbids every reference outside the file, which today catches P6's use of `EpochScheduleResult` in another file but not in the same file.
  * **Note R3: the P lines need a T2 design amendment.** The E1b scope adds P lines (per-position totals), which is the T2 revision 4 confirmation's follow-up S1. That changes the epoch text of T2 4.4 v4. Before slice A, record it as a T2 amendment with a new message version and recomputed known-answer vectors, so the signed format stays defined in one place.
  * **Resolved M1.**
    * The E1 text is corrected: the open layers are passed through, not their identity, sequence or source.
    * E1b is a plan section with the dependency **E1b before E2b and before slice A's commit-time comparison**, and the sequence diagram is updated.
    * The scope covers my required list in full:
      * L lines, with source as a deterministic key;
      * C lines, including the storno's restores;
      * R lines;
      * O metadata (deterministic operation key; source type and id, with the event as `execution-posting`; reverses NULL; creator as an input);
      * the byte-exact T2 epoch-text builder with T2's vectors;
      * schema-pinning test 4c, unless slice A takes it.
    * It also adds an engine oracle (`CostingMath.PlanFifo`) and a PostgreSQL oracle through the real `InventoryCostingService`.
  * **Resolved L1.** The flattened written rebook order is checked as ascending (date, original entry id), and the mutation was killed.
  * **Resolved L2.** The end-state proof is total. Every closure position must be in the comparison and in the revised trace, with no duplicates and no foreign positions. Both mutations were killed.
  * **Resolved L3.** Every entry's item must equal the snapshot's item, and the mutation was killed.
  * **Resolved N1.** The `Fingerprint` comment states that it is deterministic but not canonical, and that it is not the T2 epoch digest.
* **Blocker or High findings open:** none. R1 (Medium) is open; R2 is Low with a recommendation; R3 is a note.
* **NOT VERIFIED:** the following three items.
  * `verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
  * The full local suite on cf1706b: the author's run, not repeated.
  * Whether a C# 14 extension block could attach static members to `EpochSchedule`: not probed.
* **Verdict:** request changes
