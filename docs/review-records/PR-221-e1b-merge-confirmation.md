# Independent review record

* **Pull request:** #221 (Phase 3.8D executor slice E1b). Merge-round confirmation of e00a4d2: a merge of main 3b85a00 into the approved dd6d64f line. Prior approvals: `PR-221-e1b-review.md` and `PR-221-e1b-m1-confirmation.md` (record 79f2eb5, approve). The merge commit also carries a documentation addition to the E2a plan entry (T3/T4).
* **Head SHA reviewed (full 40 characters):** e00a4d267a01674dc97feb7144349489532fc73f
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetX1A); model family Claude Opus. Prompt (summary): "check the merge with --remerge-diff (registry title conflict only, now 1760 = 1750 + 10), confirm the registry sum, and check that the plan text faithfully states T3/T4 with R1 to R4 as negative controls."
* **Scope read:**
  * `git show --remerge-diff e00a4d2`, whose parents are 79f2eb5 and 3b85a00;
  * the PR's own code and tests before and after the merge (`ec3b4fd..79f2eb5` against `3b85a00..e00a4d2`, `src` and `tests`);
  * the registry in `.github/workflows/backend.yml` at e00a4d2, 3b85a00, 79f2eb5 and ec3b4fd;
  * the E2a section of `docs/PHASE-3.8D-EXECUTOR-PLAN.md`, compared with my record 79f2eb5 (T3, T4).
* **Checks run (commands and results):**
  * **Remerge-diff of e00a4d2.** There is exactly one conflict, in `.github/workflows/backend.yml`: the title, 1737 (PR) | 1750 (main), resolved to 1760. The only other content the merge adds is the documentation in `docs/PHASE-3.8D-EXECUTOR-PLAN.md`: three bullets in E2a ("Requirements carried from the ODC confirmation of PR 221 (Lows T3 and T4)"). Nothing else differs from the automatic merge.
  * **PR content unchanged.** `git diff ec3b4fd 79f2eb5 -- src tests` and `git diff 3b85a00 e00a4d2 -- src tests` are identical (index lines ignored).
  * **Registry, per class:**
    * at e00a4d2: 263 classes, sum 1760, title 1760;
    * main 3b85a00: 1750;
    * the PR's own classes relative to ec3b4fd are `EpochContentEngineOracleTests` 8 and `EpochTextColumnPinTests` 2 (10);
    * the head's map equals main's map plus exactly those two, so **1760 = 1750 + 10**.
  * **Plan text against my T3/T4: faithful.**
    * T3: limit "other plain data" to the types the derivations use, and/or require compiler-generated accessors and a primary or compiler-generated constructor. Negative controls R1 (an I/O getter) and R2 (an I/O constructor).
    * T4: refuse static-field writes, and refuse static-field reads of backend types except the Epoch literals and the closure caches. Negative controls R3 (static field read and write) and R4 (a delegate in a static field through `Enumerable.Select`).
    * Both match the record word for word in substance.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); `mergeable_state` is clean.
  * **Current main:** `git merge-tree --write-tree origin/main e00a4d2` is clean; origin/main is 3b85a00.
* **Findings:** none against this merge. One correction to my own wording, carried into the plan:
  * **Correction (Low, editorial):** T4 says to refuse "`stsfld` and `stsflda` entirely". There is no `stsflda` opcode. The opcodes that write a static field or take its address are `stsfld` and `ldsflda`. The E2a requirement should read: refuse `stsfld` and `ldsflda` entirely, and refuse `ldsfld` of any backend type other than the Epoch types' own literals and the compiler's closure caches.
  * Fix this in the E2a slice when T4 is implemented. The intent is unchanged.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following two items.
  * No local test run: the merge changes only the registry title and plan documentation, and hosted postgres-integration passed on this exact head with the 1760-test registry.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
* **Verdict:** approve
