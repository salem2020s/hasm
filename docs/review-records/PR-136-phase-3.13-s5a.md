# Independent review record

* **Pull request:** #136
* **Head SHA reviewed (full 40 characters):** 1cc2fa1d4f0d16972b6de83402e8ffdf7162f50a (full review; the review fixes are in 58d6c94 and the merge of main in 4de70e0; F's confirmation of the delta 1cc2fa1..head is pending, see NOT VERIFIED)
* **Reviewer context:** Reviewer: Agent F (independent accounting/integration QA, separate agent; not the author); model family Claude Opus; prompt: lead's pre-PR full-diff review of S5a (commercial approval policies, company migration 0032: journal byte-for-byte unchanged, direct-SQL bypass of the decision and gate triggers (stage order, D-066 independence, current revision, approve-only, gate on 2→3 only), append-only floor, concurrency of four approvers with no deadlock against 0029/0030 and links, base value from sealed total × sealed rate with journal-equivalent rounding, digit-type limitation recorded, migration safety, S1b kept with no policy)
* **Scope read:** `git diff origin/main...1cc2fa1` read in full
* **Checks run (commands and results):** local never-pushed merge with origin/feature/attachment-retention-r1 (49a3557); generator --check 0028/0029/0030/0031/0032 (match); Release build (0 errors); Hasm.Infrastructure.Tests 1205/1205; disposable-cluster PostgreSQL run of Commercial*/Journal*/Approval*/DocumentLink*/FiscalClosing* 165/165 passed 0 skipped incl. CommercialPolicyTests 8/8 and the journal approval classes unchanged (cluster created and destroyed, sole holder). Author after the fixes (58d6c94): CommercialPolicyTests 9/9 on the pre-merge tree; on the merged head 4de70e0: generator --check 0028 to 0032 match, Release build 0 errors, Hasm.Infrastructure.Tests 1208/1208.
* **Findings:** verdict request changes at 1cc2fa1; Critical 0, High 0, Medium 1, Low 3.
  * M1 (policy threshold compares the unrounded gross×rate while the journal compares base amounts rounded to the base currency minor units): FIXED in 58d6c94 (ROUND to the minor units of the base currency, fail closed when undefined; boundary test 10.2595 raw / 10.260 rounded; D-140 updated).
  * L1 (the "approver must be one of the deciders" gate refusal is never exercised): FIXED in 58d6c94 (test satisfies the plan by direct-SQL decisions, then approves as a stranger).
  * L2 (contradictory 23505/P0001 assertion in the concurrency test): FIXED in 58d6c94 (one deterministic assertion, with the reason in a comment).
  * L3 (plan binding at submit is service-only, parity with the journal): ACCEPTED as a known limitation, recorded in docs/PHASE-3.13.md (a trigger can follow in a later slice).
  * Observation outside S5a (R1 #131 generator collision) was fixed by E and #131 is merged.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** (the verdict above is F's at 1cc2fa1; every finding is fixed or accepted, so it becomes approve once F confirms the delta) F's confirmation of the delta 1cc2fa1..4de70e0 (M1/L1/L2 fixes and the merge of main); PostgreSQL run on the merged head 4de70e0 (the shared test cluster was held by another agent; hosted CI runs it); registry total 1141 is main 1132 + 9.
* **Verdict:** request changes
