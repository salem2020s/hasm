# Independent review record

* **Pull request:** #229 (X4-PRESET-ROLE: the cash and bank account presets). Delta confirmation 41bfd8e..49956ca of `PR-229-cash-presets-review.md` (record 2e919f8, approve with L1 and L2). The delta is:
  * 4539b78, tests for L1;
  * 49956ca, a merge of main cbaff77 (PRs 224 and 230).
* **Head SHA reviewed (full 40 characters):** 49956ca0f7d626d8d01a9636e592944a82b72385
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetUI185); model family Claude Opus. Prompt (summary): "confirm that the L1 tests are real (re-run a mutation, for example dropping the Ready check); that `--remerge-diff` shows only the title; that the registry equals the per-class sum; and that system 0036 is still free on main."
* **Scope read:**
  * `git diff 41bfd8e 4539b78`: the new theory `Grant_and_revoke_refuse_an_EXISTING_company_that_is_not_Ready_and_change_nothing` (Pending, Failed) in `CashPresetRolesApiTests`.
  * `git show --remerge-diff 49956ca`.
  * The PR's own diff before and after the merge: `9b450f2..4539b78` against `cbaff77..49956ca`.
  * `git diff --name-only cbaff77 ea07aca`.
* **Checks run (commands and results):** all builds and test runs went through `cpu-gate.sh build`.
  * **The L1 test.** It plants an existing company in state Pending (and, in the second case, Failed) with a membership and an active assignment of each preset, written directly. It then asserts:
    * both grants answer `CompanyUnavailable`;
    * both revokes do not answer `Revoked`;
    * the active and total assignment counts for that company are unchanged (2 and 2).
  * **PostgreSQL** (`scripts/test-pg/run-isolated.sh <wt> --build --filter FullyQualifiedName~CashPresetRolesApiTests`):
    * base: 7/7 passed (5 earlier tests plus the 2 theory cases);
    * **mutation 1:** the Ready condition removed from `FindMemberForDelegationAsync` (the grant path). **Killed:** both theory cases fail;
    * **mutation 2:** the Ready condition removed from `RevokePresetApproverOfflineAsync` (the revoke path). **Killed:** both theory cases fail.

    Each mutation was reverted afterwards, and the tree was clean.
  * **Merge.** `git show --remerge-diff 49956ca` shows one conflict hunk in `.github/workflows/backend.yml`:
    * the title: 1734 (PR) | 1730 (main), resolved to 1739;
    * in the same hunk, `CashPresetRolesApiTests` raised from 5 to 7 for the two new L1 cases. Commit 4539b78 had not raised it; the merge resolution did.

    Nothing else differs from the automatic merge. Comparing `git diff cbaff77 49956ca` with `git diff 9b450f2 4539b78` (index lines ignored), the only differences are those two lines: the PR's content is otherwise byte-identical.
  * **Registry, per class:**
    * at 49956ca: 260 classes, sum 1739, title 1739;
    * main cbaff77 (and ea07aca): 1730;
    * the head's map equals main's map plus exactly `CashPresetRolesApiTests` 7, `CashPresetRolesToolTests` 1 and `CashPresetRolesMigrationTests` 1 (9). No main class is missing.
  * **System 0036 on main:** origin/main is now ea07aca (PR 231, documentation only since cbaff77). Its newest system migration is `0035_totp_owner_only_trigger.sql`, so **0036 is still free**. `git merge-tree --write-tree origin/main 49956ca` is clean.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); `mergeable_state` is clean.
* **Findings:** none. L1 of the review is resolved, with both Ready checks proven by mutation. L2 (renumbering must move together) is unchanged and does not apply, because 0036 is still free and this PR is first in the merge queue. The approval of the review (2e919f8) carries over.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following three items.
  * The full local PostgreSQL suite on 49956ca: not run. The delta is one test plus a merge whose only resolved change is the registry; hosted postgres-integration passed on this exact head.
  * CI on the eventual merge into ea07aca was not run; that main change is documentation only and the merge-tree is clean.
  * `tools/verify/verify-head.js`: GitHub GraphQL is blocked in this session (HTTP 403); the checks were read by REST.
* **Verdict:** approve
