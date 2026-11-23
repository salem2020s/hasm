# Independent review record

* **Pull request:** #236 (Phase 3.8D threat T2, slice A2: insert-time verification of a replay execution grant, company migration 0038): confirmation of the review fixes and the merge of main
* **Head SHA reviewed (full 40 characters):** 0ba67f53812cbe8b46fe6b85eaa9da52153a1b1c
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt summary: confirm the fixes after the review at fbb8b4a411eede1ed0cda47730e08527d9b8bbf2 (record 8f0ab279918d87aa571c4c42d4cf246d902ecbdf, request changes on M1): M1 (`grant_key_has_no_signer`; the test-bed keys bound by default; positive and unbound tests; the independence cases with keys registered for that person), L1 (no leading zero), L2 (the verifier caller named), L3 (the design function names), and the merge of main abfe9ae (registry 1788). Re-run the unbound-key probe and check the merge with --remerge-diff.
* **Scope read:** af0b4ae (parents 8f0ab27 and main abfe9ae: the merge, which also carries the fixes) and 0ba67f5 (the independence cases): the 0038 migration and its generator, ReplayGrantVerificationTests.cs, ReplaySignerRegistryTests.cs, CompanySchemaTests.cs, backend.yml and the design text (4.3, 4.4, 5 and 8).
* **Checks run (commands and results):**
  * `git show --remerge-diff af0b4ae`: two conflicts, both resolved by keeping both sides. The count line becomes 1788 (main's 1781 plus A2's 7). In CompanySchemaTests it is main's system list [1..38] (S-2a's system 0038) with A2's company list [1..38]. The other hunks of the merge are the fixes:
    * 0038 and its generator: `IF k.signer IS NULL THEN RAISE 'grant_key_has_no_signer'`, then the key-owner equality without the NULL exemption; the two confirmation-time patterns become `(0|[1-9][0-9]{0,11})`;
    * the design: `f_17b9c93f` becomes `f_e08495de`, and `f_3cdca6ba` becomes `f_724b73d6`, in 4.3, 4.4, 5 and 8;
    * the A1 test names the verifier's caller: it compares the list of caller names with `FnGrantInsert`;
    * A2's tests: RegisterKey binds keys to the bed's signer by default; the new cases are an unbound key (`grant_key_has_no_signer`) and leading zeros in both confirmation lines.

    0ba67f5 makes the independence cases sign with a key registered FOR the person under test (MakeAs), so the independence rule, not the key-owner rule, is what refuses them.
  * `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~ReplayGrantVerificationTests|FullyQualifiedName~ReplaySignerRegistryTests"` (isolated cluster, through the CPU gate, 107 s) with a temporary reviewer probe: 18 of 18 passed. The probe, as the database owner:
    * the bed's key now names the bed's signer;
    * an unbound key with an arbitrary signer id: refused, P0001 `grant_key_has_no_signer`;
    * an unbound key with the bed's own signer: refused the same way;
    * a bound key with an arbitrary signer id: `grant_signer_is_not_the_key_owner`;
    * a bound key with its own signer: accepted;
    * a signed attestation with a leading zero in a confirmation time: `grant_attestation_invalid: attestation_creator_line`.

    The probe was reverted.
  * Three local mutations of 0038, each run on ReplayGrantVerificationTests (probe excluded) and each reverted; each was caught (1 failed):
    * the no-signer rule removed;
    * the confirmation-time patterns reverted to `[0-9]{1,12}`;
    * the signer-is-approver rule removed. This one was masked by the key-owner rule before 0ba67f5 would have been possible; it is now caught by the PlanApprover case signing with its own registered key.
  * `node tools/schema/company-0038-replay-grant-verification.cjs --check`, `company-0037-replay-signer-registry.cjs --check` and `system-user-tag.cjs --check`: all match. `node ci-guard.js` (main's copy) against origin/main and against fbb8b4a: no weakening found.
  * Registry: main (abfe9ae) 1781, head 1788 ("ReplayGrantVerificationTests": 7), title and per-class sum agree. The net workflow change against main is only the 0038 generator step, the entry and the count. Company 0038 follows 0037, and main is an ancestor of the head. GitHub REST reports the PR mergeable (state clean).
  * Hosted CI by REST on 0ba67f5: all seven jobs completed success.
  * The tree was verified clean after every mutation and after the probe; bin/obj were deleted.
* **Findings:** M1 and L1 to L3 are closed; no Critical, High or Medium finding is open.
  * M1 (Medium, from 8f0ab27): fixed in af0b4ae and 0ba67f5. A key that names no person is refused until resolution (ii) exists (D1), the grant's signer must be the key's owner, and every accepted grant in the suite now uses a bound key (proven above).
  * L1: fixed; leading zeros are refused in both confirmation lines (tests and mutation).
  * L2: fixed; the A1 test now names the only caller of the verifier.
  * L3: fixed; the design names the real functions.
  * Information (unchanged): commit-time re-verification, the epoch binding and the journal binding are slices A3 and A4, and they remain prerequisites of any activation (design 16).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** `verify-head.js` from main could not be run (GitHub GraphQL blocked in this session, HTTP 403); the coordinator runs it on the exact head before merging. The full PostgreSQL suite was not run locally (hosted postgres-integration on 0ba67f5 is green, 1788 by the registry).
* **Verdict:** approve
