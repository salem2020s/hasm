# Independent review record - PR #95 (isolated measurement tests)

* **Pull request:** #95
* **Head SHA reviewed (full 40 characters):** 77b342a1a67d2ff9cd4401fe72a74bd3c14f62a1
* **Reviewer context:** Agent C, a separate session from the author (Agent D) and from the lead; model family Claude Opus (claude-opus-5-5). Prompt: independent FULL-diff review, read-only, of the serial measurement collection, the CPU-time measurement in the tax scale test and D-063. Key question: whether any test is weakened (unchanged thresholds; whether CPU time still catches an engine regression; whether whole-process measurement is sound given xUnit's handling of DisableParallelization collections).
* **Scope read:** git diff origin/main...77b342a (new SerialMeasurementCollection, collection attribute on XlsxReportRendererTests and TaxEngineTests, the TaxEngine measurement change, the D-063 row).
* **Checks run (commands and results):** Release build 0 errors; Hasm.Infrastructure.Tests 8 runs, 1119/1119 each (tax scale test 1.2-1.6 s CPU, memory test 6.5-9.9 s, no failure); TRX timestamps show the 57 serial tests started after all 1062 others ended (0 overlap; xUnit 2.9.3 runs a DisableParallelization collection after all parallel collections, alone); the pre-change flake (memory test, 143 MB against 120 MB) was reproduced twice in 11 runs on another branch; hosted CI green on 77b342a. The author's diagnosis: 1 failure in 22 loaded runs before the change, 0 in 18 loaded runs after.
* **Findings:** none. Thresholds unchanged (120 MB, 30 s); nothing skipped or removed; CPU time still detects algorithmic regressions of the pure, single-threaded engine and includes runtime background threads (stricter); absolute wall-clock budgets belong to the Phase 5.1 performance suite as recorded in D-063. Note: both test classes (57 tests) now run serially; suite time stays 10-15 s.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** behaviour on the self-hosted Windows runner.
* **Verdict:** approve
