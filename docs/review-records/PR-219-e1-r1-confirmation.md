# Independent review record

* **Pull request:** #219 (Phase 3.8D executor slice E1). Delta confirmation cf1706b..beb4bdb of `PR-219-e1-delta-confirmation.md` (record 383936f, request changes for R1). The delta is the author's test-and-plan commit beb4bdb on top of my record; there is no main merge.
* **Head SHA reviewed (full 40 characters):** beb4bdbac5a92b81f1a97d39f361532ad2e04e29
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetX1A); model family Claude Opus. Prompt (summary): "confirm R1:
  * (a) no nested types except compiler closures;
  * (b) delegates and untyped object members refused;
  * (c) a transitive check of backend types through fields, properties and constructor parameters;
  * (d) no static fields on records.

  Re-run P3, P4, P5b and P7 and try new shapes (a record struct with a generic parameter holding a service, an interface-typed field such as IDisposable, a closure capturing a service, a property returning IEnumerable<CompensationPlanService>). Check that R2 and R3 are recorded in the plan."
* **Scope read:**
  * `git diff cf1706b beb4bdb`:
    * `PlanExecutionDisabledTests.cs` (+32/−11: nested-type rule, static-state rule, `AssertPureType` with visited set, delegate and `object` refusal, transitive walk over fields, properties and constructor parameters);
    * `docs/PHASE-3.8D-EXECUTOR-PLAN.md` (+3: R2, R3);
    * my record 383936f.
  * Changed files: only `docs/PHASE-3.8D-EXECUTOR-PLAN.md`, the record, and the test. No production code changed.
* **Checks run (commands and results):**
  * **Safety test against probes.** Each run used `scripts/test-pg/run-isolated.sh <wt> --build --filter FullyQualifiedName~PlanExecutionDisabledTests.No_executor_type_or_entry_point_exists_in_the_backend` on its own isolated cluster, through `cpu-gate.sh build`. The probe edits were local, applied by a script and reverted after each run; `git status` was clean afterwards. The table gives the failure message for each caught probe.

    | Probe | Change | Result and message |
    |---|---|---|
    | P3 | a `Func<object, object>` property on `EpochEntryPlan` | **CAUGHT**: "… EpochEntryPlan.<Hook>k__BackingField: an untyped member" |
    | P4 | a backend record wrapping `CompensationPlanService`, carried by `EpochLineagePlan` | **CAUGHT**: "… EpochLineagePlan.<Box> > OdcServiceBox.<Service>: CompensationPlanService is not plain data" |
    | P5b | a static `object` field on `EpochOperationPlan` | **CAUGHT**: "EpochOperationPlan.Sp is static state" |
    | P7 | a nested `Runner` class inside `EpochSchedule` | **CAUGHT**: "EpochSchedule declares the nested type Runner" |
    | N1 | `internal readonly record struct OdcBox<T>(T Value)` used as `OdcBox<CompensationPlanService>?` on `EpochLineagePlan` | **CAUGHT**: "… <B>: CompensationPlanService is not plain data" |
    | N3 | a closure in `Derive` capturing a `CompensationPlanService` local | **CAUGHT**: "EpochSchedule/<>c__DisplayClass2_0.odcSvc: CompensationPlanService is not plain data" |
    | N4 | an `IEnumerable<CompensationPlanService>` property on `EpochOperationPlan` | **CAUGHT**: "… <Services>: CompensationPlanService is not plain data" |
    | N2 | an `IDisposable?` property on `EpochEntryPlan` | **NOT caught** |
    | N5 | `System.Array?` and `System.Reflection.MethodInfo?` properties on `EpochOperationPlan` | **NOT caught** |
    | N6 | in `Derive`'s body, behind a never-true branch, `Activator.CreateInstance(Type.GetType("Hasm.Infrastructure.Costing.CompensationPlanService"))` | **NOT caught** |

    I ran N2, N5 and N6 together; the test passed (1/1). I confirmed that all three edits were applied and compiled.
  * **Plan notes.** `docs/PHASE-3.8D-EXECUTOR-PLAN.md` under E1b now carries:
    * **R3:** the P lines change T2's epoch text (v4); a T2 amendment with a new message version and vectors is needed before slice A, and E1b must not emit P lines into the signed text under v4;
    * **R2:** from E2 on, the set of types and methods allowed to reference `EpochSchedule`, the content, the scope and the writers is enforced by an IL or reflection scan with a positive control, not by names.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); a skipped duplicate of each also ran.
  * **Merge with current main:** `git merge-tree --write-tree origin/main beb4bdb` is clean against main 6f0b983. REST `mergeable` was still being computed (null) when read, with base 6ee0653.
* **Findings:** 0 Critical, 0 High, 0 Medium, 2 Low (T1, T2). R1 of the delta confirmation is resolved; R2 and R3 are recorded in the plan.
  * **Resolved R1.** All four probes of the previous round fail the test, each with a precise path in the message, as do three of the new shapes (N1, N3, N4):
    * nested types are refused, except compiler closures whose fields are checked;
    * delegates and `object` members are refused;
    * backend types are walked transitively;
    * records hold no static state.
  * **Resolved R2 and R3,** recorded in the E1b plan section as above.
  * **Low T1: opaque non-backend member types still pass.** An interface-typed member (`IDisposable`, N2) and `System.Array` or `System.Reflection.MethodInfo` members (N5) can hold a service or a callable reference at runtime. They are the same class as the delegate and `object` members this round refuses.
    * Recommendation: invert the rule for non-`Hasm.*` types into an allow-list. Allow primitives, `string`, `decimal`, `Guid`, `DateOnly`, `Nullable<>`, `ImmutableArray<>` (and any other immutable collection actually used), `CancellationToken` as a parameter, and the compiler's record members (`EqualityContract`'s `Type`, `StringBuilder` in `PrintMembers`, `object` in `Equals`). Refuse every other interface, abstract base and reflection type.
    * Do it in E1b, where the content types grow, or earlier.
  * **Low T2: the shape pin cannot see what `Derive`'s body does (N6).** A reflective instantiation of a service inside the allowed function passes the test and the source scan, which forbids only `Npgsql`, `DbConnection`, `TimeProvider`, `DateTime.`, `Random`, `Guid.NewGuid`, `File.` and `HttpClient`.
    * Recommendation: extend the planned IL scan (R2) from callers to callees. The methods and closures of `EpochSchedule` (and later `EpochContent`) may call only an allow-listed set of members (BCL collections, LINQ and hashing; the costing data types; `CostingException`), never `Activator`, `System.Reflection`, `Type.GetType`, or a backend service. Use a positive control.
    * This is the same IL machinery as R2, so it fits E1b or E2a.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following three items.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
  * The full local PostgreSQL suite on beb4bdb: not run; the delta is test-only, and hosted postgres-integration passed on this exact head.
  * REST mergeability: still being computed when read; the local merge-tree against 6f0b983 is clean.
* **Verdict:** approve
