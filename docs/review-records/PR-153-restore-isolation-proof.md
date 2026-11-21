# Independent review record

* **Pull request:** #153
* **Head SHA reviewed (full 40 characters):** e7dad23396a7726ff720b5ad27c3488647f1c343 (fixes in 032e16c9a60199c346656af52bc342c9bea0a9f7; F's confirmation of the fix delta is pending)
* **Reviewer context:** Reviewer: Agent F (independent accounting/integration QA, separate agent; not the author); model family Claude Opus; prompt: lead's independent review of #153 (CompanyRestoreIsolationTests, SRS 21.8 trial path: content-complete hashing incl. sequences, mutation sensitivity, exact failure codes, no acceptance overclaim); Head SHA reviewed e7dad23396a7726ff720b5ad27c3488647f1c343; executed: git diff origin/main...e7dad23 read in full (test, generate.js data, regenerated RELEASE1-ACCEPTANCE, registry), check of per-test database naming in PostgresTestBase and of the identity/sequence count in the migrations, isolated PostgreSQL run of CompanyRestoreIsolationTests 2/2 passed 0 skipped (cleaned up); verdict request changes; findings Critical 0, High 0, Medium 2 (M1 digest omits sequences and schema objects; M2 no mutation check proving sensitivity), Low 3 (L1 warnings not asserted in the failure test; L2 vacuous path-string assertion; L3 evidence text does not say the sets are unencrypted v1 DEV/TEST sets)
* **Scope read:** git diff origin/main...e7dad23 (test class, generate.js evidence data, regenerated RELEASE1-ACCEPTANCE.md, registry)
* **Checks run (commands and results):** as stated in the reviewer line above; author after the fixes: isolated run of CompanyRestoreIsolationTests 2/2 passed, generate.js --check OK
* **Findings:** 0 Critical, 0 High, 2 Medium and 3 Low, all fixed in 032e16c9a60199c346656af52bc342c9bea0a9f7 (tests and text only; no product defect):
  * M1: the digest now also covers pg_sequences (last_value) and fingerprints trigger, function and constraint definitions of public. Against the pg_restore copy, constraint names and kinds are compared instead of constraint text, because pg_restore re-renders CHECK expressions (same meaning, different text; found while adding the fingerprint, not a defect).
  * M2: in-test mutation checks: one live sequence of B and one byte of a live attachment are changed, each is asserted to move the matching hash, then reverted and asserted equal again; one row of B is changed last and asserted to move B's hash (not reverted: the revision trigger allows only an advance by one and B is throwaway).
  * L1: the failure test asserts the exact (empty) warnings.
  * L2: the assertion is now that trialAtt lies outside the live storage (relative path starts with "..").
  * L3: the evidence entry in generate.js and the class summary state that the sets are unencrypted v1 sets (DEV/TEST path) and that encrypted-set restore is not covered.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** hosted CI on the head; F's confirmation of the fix delta
* **Verdict:** request changes
