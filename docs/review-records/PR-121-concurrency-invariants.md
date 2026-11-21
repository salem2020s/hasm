# Independent review record

* **Pull request:** #121
* **Head SHA reviewed (full 40 characters):** a975fd7b4a40bfff83cf3caa061486e768b5f27f (F's full review was at 146a66e0fbce1da6c99909a235704deee95fb35c; the delta 146a66e..a975fd7 was confirmed, see below)
* **Reviewer context:** Agent F (independent accounting/integration QA, separate agent; not the author); model family Claude Opus; prompt: lead's independent review of PR #121 (concurrency invariants: 8 same-revision writers, one winner, the rest 412/PreconditionFailed, stored row = winner at revision +1, one audit row for account/branch/warehouse; not flaky; H's claim that parallel journal posting and import application are covered on main; nothing weakened)
* **Scope read:** `git diff origin/main...146a66e`; the update paths of ChartOfAccountsService, CompanyBranchService, CompanyWarehouseService, PartyService, PricingConfigService, TaxConfigService (row lock before the revision check); JournalTests (8 parallel posts; concurrent gapless numbering) and ImportApprovalTests (6 concurrent applications) on main; catalog keys and test hooks of the frontend test
* **Checks run (commands and results):** git fetch; git diff origin/main...146a66e; reads of the six update paths; git grep of JournalTests and ImportApprovalTests on main; registry sum 1097; catalog-key and test-hook presence for the frontend test; REST check-runs on 146a66e (all jobs success incl. postgres-integration, frontend-build-test, desktop-build, e2e-smoke); PostgreSQL not run by the reviewer (another agent's 5433 cluster was up)
* **Findings:** 0 Critical, 0 High, 0 Medium, 3 optional Low, all fixed (details below):
  * L1: the single-audit-row assertion was missing for party, price list and tax. Fixed in cb43395c543b7dea3d424806862a198e217ff1c9 (all six entities now assert exactly one new audit row).
  * L2: the losers' code and currentRevision were not asserted. Fixed in cb43395c543b7dea3d424806862a198e217ff1c9 (every loser must be PreconditionFailed / revision_conflict with CurrentRevision equal to the revision the winner stored).
  * L3: a stray blank line in the describe.each of the frontend test. Fixed in cb43395c543b7dea3d424806862a198e217ff1c9.
  * Not a finding: the registry is reconciled with main (1092) at merge time.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** exact-head hosted CI on the head that merges main (read it before the merge)
* **Verdict:** approve

## Confirmation of the fix delta

Reviewer: Agent F (independent accounting/integration QA, separate agent; not the author); model family Claude Opus; prompt: lead's confirmation of the #121 fix delta for F's three Lows (audit rows for party/price list/tax; losers' revision_conflict and CurrentRevision; stray blank line); Head SHA reviewed a975fd7b4a40bfff83cf3caa061486e768b5f27f (delta 146a66e..a975fd7: fix cb43395, review record a975fd7); executed: full delta read, check that every service's revision-conflict path carries CurrentRevision and that PartyManagementService passes the service result through, check that CoCount targets the company database, Actions runs list for the branch (only 146a66e has runs, all success), git merge-tree against main (conflict in backend.yml registry 1097 vs 1093, so no pull_request CI on a975fd7 until main is merged); PostgreSQL not run locally (another agent's 5433 cluster up; H reports 3 local runs green); verdict approve; findings Critical 0, High 0, Medium 0, Low 0 open (all three fixed); exact-head CI still pending, after H merges main.
