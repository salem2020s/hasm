# Independent review record

* **Pull request:** #221 (Phase 3.8D executor slice E1b). Delta confirmation 77d9b21..dd6d64f of `PR-221-e1b-review.md` (record 61741f8, request changes: M1, L1, L2, L3).
* **Head SHA reviewed (full 40 characters):** dd6d64f3e0cbf8d17c78cb04884af43a028f6a1f
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetX1A); model family Claude Opus. Prompt (summary): "confirm the delta:
  * M1, a member-level callee allow-list: on backend types, only the three functions, their closures and the Epoch records, the named helpers, and the constructors, accessors and compiler-generated members of other backend records; no CompilerServices; `SerializeToUtf8Bytes` only; `Encoding.GetBytes` and `UTF8`;
  * Q1 to Q3 now fail;
  * L1, the registry title 1737;
  * L2 recorded.

  Try more probes: a property getter of a backend record that does I/O, a constructor with side effects, a delegate obtained through an allowed helper, and a static field read of a backend type."
* **Scope read:** `git diff 77d9b21 dd6d64f`:
  * `PlanExecutionDisabledTests.cs` (+28/−12: `IsPlainData`, `PermittedBackendCallee`, the member-level framework rules);
  * `backend.yml` (title);
  * `docs/PHASE-3.8D-EXECUTOR-PLAN.md` (the E2b L2 note);
  * my record 61741f8, merged into the branch.
* **Checks run (commands and results):**
  * **Safety-test probes.** Each ran on its own isolated cluster with `scripts/test-pg/run-isolated.sh <wt> --build --filter …PlanExecutionDisabledTests.No_executor_type_or_entry_point_exists_in_the_backend`, through `cpu-gate.sh build`. Each was a local edit of `EpochContent.Derive` inside a never-true branch, plus for R1 to R4 one new file `Costing/OdcProbeTypes.cs`. All edits were applied by a script and reverted after the run, and the tree was clean afterwards.

    | Probe | Change | Result |
    |---|---|---|
    | Q1 | `MigrationScript.LoadEmbedded()` | **CAUGHT** (`MigrationScript::LoadEmbedded`) |
    | Q2 | `RuntimeHelpers.GetUninitializedObject(...)` then `IDisposable.Dispose()` | **CAUGHT** (`RuntimeHelpers::GetUninitializedObject`) |
    | Q3 | `JsonSerializer.Deserialize<HasmDatabaseOptions>` | **CAUGHT** (`JsonSerializer::Deserialize`) |
    | R1 | a NEW backend record whose hand-written property getter writes a file, read in `Derive` | **NOT caught** |
    | R2 | a NEW backend record whose constructor writes a file, constructed in `Derive` | **NOT caught** |
    | R3 | a read of a static field (`OdcHolder.Hook?.Dispose()`) and a write (`OdcHolder.Counter++`) of a NEW backend static class | **NOT caught** (field opcodes are not scanned; `IDisposable.Dispose` is permitted) |
    | R4 | a delegate held in a NEW static field, invoked through the allowed helper `Enumerable.Select` | **NOT caught** |

    All probes compiled (checked with a Release build of `Hasm.Infrastructure`).
  * **Existing code reachable by R1 to R4:** a search of `src/backend` finds no non-private static field of a delegate, `Lazy` or mutable type. Private static fields are not reachable from `Derive` without reflection, which is refused. So R1 to R4 need NEW code to be written elsewhere in the backend; none of them reaches existing code.
  * **Registry at dd6d64f:** 259 classes, sum 1737, title 1737.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan).
  * **Merge with current main:** `git merge-tree --write-tree origin/main dd6d64f` reports a **conflict in `.github/workflows/backend.yml`** against main 435c44d. PR 224 merged after this head and changed the registry. REST `mergeable_state` was still being computed when I read it.
* **Findings:** 0 Critical, 0 High, 0 Medium, 2 Low (T3, T4), 1 note (N1). M1, L1 and L2 of the review are resolved; L3 stays open as recorded.
  * **Resolved M1.** All three probes of the review now fail, each with the exact callee named:
    * backend callees are allowed by member;
    * `System.Runtime.CompilerServices` is closed;
    * `System.Text.Json` is limited to `SerializeToUtf8Bytes`;
    * cryptography, threading, globalisation, text and interop are each limited to the members used.

    The scan now refuses every code path into EXISTING backend or framework code I could find.
  * **Resolved L1:** the title says 1737.
  * **Resolved L2:** the E2b plan entry requires the E2b oracle to compare entry dates and operation rows.
  * **L3 (T2 S1)** stays open, as recorded (T2 amendment before slice A).
  * **Low T3: the scan trusts every backend record's constructor and property accessors (R1, R2).**
    * `PermittedBackendCallee` accepts any constructor and any `get_`/`set_` of any backend record, struct or enum, including hand-written ones with a body.
    * A new record with an I/O getter or an I/O constructor passes.
    * Recommendation, for E2a with the R2 IL machinery:
      * limit "other plain data" to an explicit list of the types `Derive` actually uses (the snapshot, comparison and replay records);
      * and/or require a permitted accessor to carry `[CompilerGenerated]` (auto-property) and a permitted constructor to be the record's primary or compiler-generated one;
      * add R1 and R2 as negative controls.
  * **Low T4: field access is not scanned (R3, R4).**
    * `CalleesOf` follows only `InlineMethod` operands, so `ldsfld`, `stsfld` and `ldsflda` on backend types are invisible.
    * `Derive` could read global state (and run code through a permitted interface or delegate call on the value it read), or write global state, which is a purity break.
    * Recommendation:
      * refuse `stsfld` and `stsflda` entirely;
      * refuse `ldsfld` of any backend type other than the Epoch types' own literals and the compiler's closure caches (`<>9…`);
      * add R3 and R4 as negative controls.

    Today no existing non-private static delegate or mutable field exists in the backend, so this is defence in depth against future code.
  * **Note N1: the PR needs a merge of main before merge.** The conflict is the registry title and map in `backend.yml`, after PR 224. The exact-head gates repeat on the merged head.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following three items.
  * I did not run the full local PostgreSQL suite: the delta is test and documentation only, and hosted postgres-integration passed on this exact head.
  * REST mergeability against the moved main: still being computed; the local merge-tree shows the `backend.yml` conflict.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
* **Verdict:** approve
