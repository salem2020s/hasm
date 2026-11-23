# Independent review record

* **Pull request:** #233 (Phase 3.8D threat T2, slice A1: company migration 0037): confirmation of the merge rounds after the approved head
* **Head SHA reviewed (full 40 characters):** 8f33f17133e509910c8c70d37d7ee5c10eac1112
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt summary: in one pass, confirm the merges after the approved head 3a74c5326fe93fb7ea7923d78de4595d32c92b30 (record 41640fa9303d6a8792c21b32d6c28b913f21b829): the merge of main 3b85a00 (conflicts in CompanySchemaTests and the registry) and of main 8375985 (PR 221, registry 1770). Check the conflict resolutions with --remerge-diff, the registry sum, that company 0037 is still free on main, and that the A1 content is unchanged.
* **Scope read:** the first-parent merges after 41640fa: 5819c57 (parents 3a74c53 and main 3b85a00), 6509ade (parents 5819c57 and 41640fa, the review record branch head) and 8f33f17 (parents 6509ade and main 8375985); `git show --remerge-diff` of each; the net diff origin/main (8375985bf8fc142b8937399a1cdcb845fa26d352)..8f33f17 compared with the approved diff 435c44d..3a74c53.
* **Checks run (commands and results):**
  * The three merge diffs (`--remerge-diff`):
    * 5819c57: two conflicts. In backend.yml the count line is resolved to 1760 (main's 1750 plus A1's 10). In CompanySchemaTests it is main's system list [1..37] (S-1's system 0037) with A1's company list [1..37]. Both sides are kept; nothing else changed.
    * 6509ade: no conflict and no remerge diff (it only brings the review record).
    * 8f33f17: in backend.yml the count line becomes 1770, and the registry keeps both A1's "ReplaySignerRegistryTests": 10 and PR 221's "EpochContentEngineOracleTests": 8, "EpochTextColumnPinTests": 2; nothing else changed.
  * A1 content unchanged: `git diff 41640fa HEAD` over the A1 sources (CompanyDatabase, the 0037 generator, ReplaySignerRegistryTests.cs, ReplayRsaVectors.cs) is empty. The net diff against main touches exactly the approved file set (23 files, plus this PR's review record), and its hunks in src, the 0037 generator and the A1 tests are identical to the approved diff 435c44d..3a74c53. The net workflow and CompanySchemaTests changes against main are only the generator step, the count 1760 to 1770, the ReplaySignerRegistryTests entry and the company list [1..37].
  * Registry: origin/main 1760 (title and per-class sum), head 1770 (title and per-class sum), so the difference is exactly A1's 10.
  * Company 0037 is free on main: the company migrations of origin/main end at 0036_company_plan_execution.sql, and nothing in main's CompanyDatabase references a 0037. origin/main is an ancestor of the head.
  * `node tools/schema/company-0037-replay-signer-registry.cjs --check`, `company-0036-plan-execution.cjs --check` and `system-identity-integrity.cjs --check`: all match. `node ci-guard.js --base origin/main` (main's copy): no weakening found.
  * Hosted CI by REST on 8f33f17: backend-build-test, ci-guard, dependency-scan, docs-traceability, guard-selftest, postgres-integration and secret-scan completed success (the duplicate runs of the other event are skipped). No local test run: the content under review is unchanged and hosted postgres-integration ran the full 1770 with per-class counts.
* **Findings:** none. The merges resolve only the registry count and the migration lists, keep both sides each time, and leave A1 byte-identical in its sources and tests. The Low L1 of the review (EXECUTE on the verifier left to PUBLIC) stays assigned to slice A2.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** `verify-head.js` from main could not be run (GitHub GraphQL blocked in this session, HTTP 403); the coordinator runs it on the exact head before merging. No local PostgreSQL run in this confirmation (hosted CI only).
* **Verdict:** approve
