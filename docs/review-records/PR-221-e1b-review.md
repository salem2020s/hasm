# Independent review record

* **Pull request:** #221 (Phase 3.8D executor slice E1b: epoch content as data and the T2 epoch text; Draft; author sonnetX1A). Reviewed against:
  * the executor plan's E1b entry;
  * the T2 design (4.4 v4 text, section 13, test 4c);
  * my PR 219 findings (R2, R3, T1, T2).
* **Head SHA reviewed (full 40 characters):** 77d9b215e2e026063e25425d528a3df6b8d45c22
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author); model family Claude Opus. Prompt (summary): "review the FULL diff against origin/main:
  * `EpochContent.Derive` (L, C with storno restores, R; mirror layers; arrival ranks; open-layer proofs);
  * `EpochText` (byte-exact v4 with the vector, no P lines, O keys);
  * test 4c;
  * the PostgreSQL engine oracle (8 scenarios through `InventoryCostingService`, every row; FIFO, average, mirror, equal-date chains both ways);
  * the mutations, including one surviving equivalent mutant argued as subsumed;
  * the safety test's T1 allow-list and T2 callee IL scan. Try to break them with a call through an interface, a static constructor, a lambda capturing a delegate, a generic method call, and Type.GetType plus Invoke;
  * that the slice is inert;
  * the registry title against the per-class sum."
* **Scope read:**
  * The full diff `origin/main...77d9b21` (10 files, +1203/−24):
    * `EpochContent.cs`, `EpochText.cs`;
    * `EpochContentTests.cs`, `EpochTextTests.cs`, and the fixture and reachability changes in `EpochScheduleTests.cs`;
    * `EpochContentEngineOracleTests.cs`, `EpochTextColumnPinTests.cs`;
    * the `PlanExecutionDisabledTests.cs` additions (the shape pin of the three functions, T1 `PermittedSystemTypes`, T2 `CalleesOf` / `PermittedCallee` / `AssertCalleesPermitted`);
    * `backend.yml` (registry);
    * the plan entry.
  * For comparison: T2 4.4 v4 and its vectors; `tools/verify/pg-trx-check.js`. The PR body.
* **Checks run (commands and results):** every run went through `cpu-gate.sh build`.
  * **Unit tests:** `dotnet test tests/unit/Hasm.Infrastructure.Tests --filter "EpochScheduleTests|EpochContent|EpochText"` passed 34/34.
  * **Mutations** (each applied with `sed`, run with the same filter, then reverted; the tree was clean afterwards):

    | Mutation | Result |
    |---|---|
    | m1: the restore flag of an outflow storno set to false | killed (4 tests, including the property test and the T2 text test) |
    | m2: the arrival rank ignores zero-remaining layers | killed (3, including `A_consumed_mirror_layer_is_named_by_its_arrival_rank…`) |
    | m3: the untouched-layer check (`epoch_layer_consumed`) removed | killed (`A_receipt_layer_that_is_not_untouched…`) |
    | m5: the text flag spelled `true`/`false` | killed (3, including the known-answer vector) |
    | m6: restores sorted descending | killed (`Every_group_is_sorted…`) |
    | m4: the baseline `CheckSums` removed | **survives**, as the author states |

  * **PostgreSQL** (`scripts/test-pg/run-isolated.sh … --filter "EpochContentEngineOracleTests|EpochTextColumnPinTests|PlanExecutionDisabledTests"`, unmutated): 14/14 passed, 0 skipped:
    * oracle 8 (FIFO and average × 4 scenarios);
    * column pin 2;
    * disabled tests 4.
  * **Safety-test probes.** Each ran on its own isolated cluster with `--filter …PlanExecutionDisabledTests.No_executor_type_or_entry_point_exists_in_the_backend`. Each probe was an edit to `EpochContent.Derive`, inside a never-true branch unless stated otherwise, applied by a script and reverted after the run; the tree was clean afterwards.

    | Probe | Change | Result |
    |---|---|---|
    | none | unmodified head | passed |
    | Q5 | a static constructor on `EpochContent` | **CAUGHT** (`[Void .cctor()]`) |
    | Q6 | a closure capturing a `Func<int>` | **CAUGHT** (`EpochContent/<>c__DisplayClass6_1.odcF: a delegate type`) |
    | Q7 | the generic method `Activator.CreateInstance<StringBuilder>()` | **CAUGHT** (`System.Activator::CreateInstance`) |
    | Q8 | `Type.GetType("…CompensationPlanService").GetMethod("ToString").Invoke(…)` | **CAUGHT** (`MethodBase::Invoke; Type::GetMethod; Type::GetType`) |
    | Q1 | `Hasm.Infrastructure.SystemDatabase.MigrationScript.LoadEmbedded()`, a static method of a backend **record** that reads assembly resources and runs the migration guard | **NOT caught** |
    | Q2 | `((IDisposable)RuntimeHelpers.GetUninitializedObject(typeof(Hasm.Infrastructure.Identity.MfaSecret))).Dispose()`, which instantiates a backend class without its constructor and calls into it **through an interface** | **NOT caught** |
    | Q3 | `JsonSerializer.Deserialize<Hasm.Infrastructure.Configuration.HasmDatabaseOptions>("{}")`, a generic deserialisation that constructs an arbitrary backend type | **NOT caught** |

  * **Registry,** parsed per class: 259 classes summing to **1737**, but the step title still says **1727** (main is 1727). The two new classes are registered: EpochContentEngineOracleTests 8 and EpochTextColumnPinTests 2.
  * **Merges:** `git show --remerge-diff 3a5d6ed` (the merge of main) is empty. `git merge-tree --write-tree origin/main 77d9b21` is clean against 9b450f2.
  * **Inert:**
    * no migration, no `Program.cs` or DI change, and no protected file in the diff;
    * the unit reachability pin forbids every reference to the three functions outside their own files;
    * nothing writes: the oracle writes only through the ordinary engine in a test database.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); `mergeable_state` is clean.
* **Findings:** 0 Critical, 0 High, 1 Medium (M1), 3 Low (L1 to L3). Each finding is listed below.
  * **Medium M1: the callee IL scan (T2 of PR 219) permits whole namespaces and every method of every backend record, so code outside the permitted set can still run (Q1, Q2, Q3).**
    * `PermittedCallee` accepts:
      * any method of ANY `Hasm.*` record or struct, whatever it does (Q1: `MigrationScript.LoadEmbedded`);
      * the whole `System.Runtime.CompilerServices` namespace, including `RuntimeHelpers.GetUninitializedObject`. Combined with the permitted interface call `IDisposable.Dispose` (or `IEquatable`/`IComparable`/the collection interfaces), it dispatches into arbitrary backend code (Q2);
      * the whole `System.Text.Json` namespace, including `JsonSerializer.Deserialize<T>`, which runs constructors and setters of any type (Q3).
    * The scan's own comment says "a reflective Activator.CreateInstance, a Type or MethodInfo invocation, a Process, a file, a socket or a service cannot hide in it". Q1 to Q3 show that a call into code outside the permitted set can.
    * Required, a member-level allow-list instead of type and namespace wildcards:
      * **(a)** For backend types, permit only the three functions and their closures, the named pure costing helpers (`CostingMath`, `CostReplay`, `CostHistoryEntry`, `CostingException`), and the compiler-generated members of the **records the derivation actually uses**: accessors, `.ctor`, `<Clone>$`, `Equals`, `GetHashCode`, `ToString`, `PrintMembers`, `op_Equality`/`op_Inequality`, `Deconstruct`. Not any method of any record or struct.
      * **(b)** `System.Runtime.CompilerServices`: name the members actually used. `GetUninitializedObject`, `RunClassConstructor` and the `Unsafe` members stay refused.
      * **(c)** `System.Text.Json`: `JsonSerializer.SerializeToUtf8Bytes` only.
      * **(d)** Keep the positive control, and add Q1, Q2 and Q3 as negative controls (mutation proofs) next to the existing Activator and reflection probes.
  * **Low L1: the registry title is stale.** The step title says "Verify all 1727 PostgreSQL tests", but the per-class sum is 1737 (main 1727 + 8 + 2). `pg-trx-check.js` checks the per-class map, so CI is correct, but the title is wrong; set it to 1737.
  * **Low L2: the oracle's coverage limits, to be recorded for E2b.**
    * The engine oracle compares every entry's kind, direction, quantity, amount and running figures, every created layer (key, rank, figures, source), every consumption row per entry in order, every remaining figure, the untouched layers, the position rows, and the L/C/R text rebuilt from the engine's rows.
    * It does **not** compare entry dates (the oracle posts at D3+10 by design, because "dates do not influence layers, consumptions or costs") or the operation rows (the O lines).
    * That is acceptable for E1b's scope, but the E2b oracle, through the real writers with original dates, must compare the E dates and the O lines. Note this in the E2b plan entry.
  * **Low L3: T2 S1 (the cost position row is not signed) stays open.** The P lines exist as data (`EpochPositionTotal`) and are correctly **not** in the signed text (R3 honoured). The T2 amendment (new message version and vectors) is still required before slice A, as recorded.
* **Answers to the specific questions:**
  * **`EpochContent.Derive`: correct.**
    * L: one layer per receipt; one mirror per consumed layer of a transfer-out, in order, with the source as a deterministic key.
    * C: outflow stornos restore exactly the original's consumption rows (`Restores = t`); an inflow storno takes out its original's whole layers, which must be untouched (`epoch_layer_consumed`).
    * R: every created, consumed or restored layer.
    * Arrival rank is the engine's max + 1 per position, zero-remaining layers included (m2 killed).
    * Proofs:
      * `PlanFifo`, the engine's own planner, must reproduce every outflow's amount;
      * the open layers sum to the position at the start and at the end;
      * the open layers equal the revised trace's, in order.

    All are proven against the real engine by the oracle.
  * **`EpochText`: byte-exact v4.**
    * The known-answer vector is pinned: 11 lines, 1777 bytes, digest `719a4407…`, the same as T2 4.4.
    * The groups are O, E, L, C, R, sorted as the design orders them; culture and input order do not affect the text.
    * There are no P lines.
    * The O keys are `epoch:<plan>:1:<orig>`, `:2`, `:3:<orig>`, with the source types and `execution-posting` as in T2's vector.
  * **Test 4c:**
    * the live catalog of the four 0014 tables must equal the signed and excluded classification, which matches the T2 4.4 table column for column;
    * a pure test proves that an added or renamed column is detected;
    * both are in the PostgreSQL registry.
  * **The PostgreSQL engine oracle:** yes, the epoch is performed through `InventoryCostingService`:
    * reversals in plan order (`Undo` of each stornoed operation), the event, and the rebooks as ordinary operations;
    * every row is compared, except dates and operation rows (L2);
    * FIFO and average are both covered, with a transfer consuming two layers (two mirrors, scenario 1) and equal-date transfers in both directions (scenario 3, N29);
    * inflow stornos (scenarios 1 and 2) and an issue event (scenario 2) are covered too.
  * **The surviving mutant (m4, baseline `CheckSums`):** I accept the argument that it is subsumed.
    * Every branch of the simulation changes the layer sums by exactly the entry's quantity and amount, because each change is checked against the entry: restores sum to the entry, a removed inflow layer equals the entry, a receipt creates the entry, `PlanFifo`'s amount must equal the entry, and the transfer-in mirrors equal the out leg.
    * The ledger changes by the same amounts. A baseline mismatch therefore persists to the end and is refused by the end `CheckSums`; positions the epoch does not touch keep their baseline figures and are refused there too.
    * It is defence in depth with an earlier message, not an independent check.
  * **Safety test.**
    * T1 (the plain-type allow-list) holds; Q6 and the R1 probes of PR 219 are caught.
    * T2's callee scan catches the static constructor (Q5), the generic `Activator` call (Q7) and `Type.GetType` plus `Invoke` (Q8).
    * It misses the backend-record method (Q1), the call through an interface on an uninitialised object (Q2) and generic JSON deserialisation (Q3): M1.
  * **Inert:** yes (see checks).
  * **Registry:** the two classes are added with the right counts; the title is stale (L1).
* **Blocker or High findings open:** none. M1 (Medium) is open; L1 to L3 are Low.
* **NOT VERIFIED:** the following three items.
  * I did not run the full local PostgreSQL suite: the author reports 1793/1793 on this head, and hosted postgres-integration passed with the 1737-test registry. I did not reconcile the 1793 figure with the registry.
  * The author's other mutations: I re-ran six of my own instead.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
* **Verdict:** request changes
