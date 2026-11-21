# Independent review record

* **Pull request:** #137 (stacked on #136; contains its prerequisites)
* **Head SHA reviewed (full 40 characters):** d77fa4160417905acd94883b72bdf9938834220b (H's review; the fixes are in 52152e003f63c15f698b94dfe9e657a401e754af; H's confirmation of that delta is pending, see NOT VERIFIED)
* **Reviewer context:** Reviewer: Agent H (independent review, separate agent; not the author); model family Claude Sonnet; prompt: lead's independent review of draft PR #137 (S5a UI approval panel, stacked on #136) against CLAUDE.md, D-140/D-141 and the PHASE-3.13 S5a section: shared frontend only, Tabs standard, ar/en parity, keyboard accessibility, UI mirrors but never replaces server authorization, no test types shown in production (D-079), #126 Links merge, test coverage of the new panel; Head SHA reviewed d77fa4160417905acd94883b72bdf9938834220b; executed: git diff origin/main...head and origin/feature/phase-3-13-s5a...head (UI delta: 5 frontend files, +174/-6), read of the S5a spec, the `/approvals` route and the stage-refusal codes in the backend, check of whether #126's merge commit is an ancestor, export of the head to a scratch tree with `npm ci`, typecheck (0 errors) and the full web vitest suite plus a rerun of the two relevant files; Tauri compile not run; verdict approve; findings 0 Critical/High/Medium, 5 Low.
* **Scope read:** the full UI delta against origin/main and against the S5a branch
* **Checks run (commands and results):** by H as stated above. By the author after the fixes (52152e0): web typecheck 0 errors; `commercialDocuments.test.tsx` and `authStates.test.tsx` 259/259 passed; hosted CI on the earlier head 8100494: frontend-build-test, desktop-build, e2e-smoke, backend-build-test, ci-guard, dependency-scan, docs-traceability, guard-selftest, secret-scan success.
* **Findings:** 0 Critical, 0 High, 0 Medium, 5 Low (table below): L2, L3, L4 fixed in 52152e0, L1 and L5 recorded as follow-ups

| # | Severity | Finding (H) | Resolution |
|---|----------|-------------|------------|
| 1 | Low | ApprovalPanel shows the role and approver as the first 8 characters of the id | Follow-up (needs a role/user name lookup; recorded in docs/PHASE-3.13.md), no code now |
| 2 | Low | Reject stays enabled while Approve is hidden for an ineligible user; self-approval disables rather than hides | FIXED in 52152e0: Reject is shown only when Approve would be (same eligibility, same stated reason); the self-approval case disables both as before; tests assert Reject hidden |
| 3 | Low | hasRole counts a branch-scoped grant for another branch, so Approve can show where the server refuses | FIXED in 52152e0: `hasRole(roleId, branchId)` counts a company-wide grant or a grant for the document's branch; two tests (other branch refuses, own branch offers) |
| 4 | Low | The "every stage complete" test wipes body.innerHTML and re-opens in one it(); fragile | FIXED in 52152e0: split into separate tests, no body wipe |
| 5 | Low | No-roleId shows "Any approver", and a failed progress fetch is silent with no notice | Follow-up (a short notice when the progress cannot be read; recorded in docs/PHASE-3.13.md), no code now |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the Tauri compile was not run locally (no cached crates and no network in the author's environment); it is covered by the hosted `desktop-build` job, which passed on the earlier head 8100494 and must be re-checked on the final head. The 7 full-suite timeouts seen locally were load-only (machine load average about 38; per H's rerun and the author's isolated reruns they pass). H's confirmation of the delta d77fa41..52152e0 is pending.
* **Verdict:** approve
