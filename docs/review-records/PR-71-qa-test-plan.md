# Independent review record

* **Pull request:** #71 (docs-only: `docs/qa/RELEASE1-TEST-PLAN.md`)
* **Head SHA reviewed (full 40 characters):** 2d11cd7039eeb818ebd175a794d551b61c557169
* **Reviewer context:** Agent E, a separate session from the author (Agent C); model claude-sonnet-5-5, the SAME model family as the author. Independence is limited to a separate session and context. Full 1-file diff read. Prompt: "independent FULL-diff review (read-only, report to lead only) of PR #71: check every covered claim names a real test, scenario numbering and verdicts 21.1-21.10, no invented/weakened requirements, no secrets".
* **Scope read:** the full diff of the PR against `origin/main` (one file, `docs/qa/RELEASE1-TEST-PLAN.md`).
* **Checks run (commands and results):** reading and grep-based verification of the named tests by the reviewer. No test, build or database command was executed by the reviewer or the author.
* **Findings:** no Critical, no High, no Medium; four Low.

| ID | Severity | Location | Problem | Resolution |
|----|----------|----------|---------|------------|
| L1 | Low | scenario 21.8 verdict | "PARTIAL (attachment verification only)" read as progress while the traceability matrix has the backup phase as not started. | Fixed in the follow-up commit: relabelled "NOT COVERED (criterion unmet: ...)" and the text states exactly what slice 1 of Phase 4.3 delivers and what is missing. |
| L2 | Low | scenario 21.9 verdict | "LIKELY COVERED" was stale: `CascadingMenu.test.tsx` (lines 21-30, 60, 92, 103) and `App.test.tsx` (line 288) assert hover-expands and click-or-keyboard-only execution. | Fixed: "COVERED at component level; E2E step missing" with the line references; the E2E hover step stays listed as a gap. |
| L3 | Low | integration risk 1 | Pinned counts stated as "at least 15". | Fixed: counted, 19 occurrences (14 at `Assert.Equal(26L ...)`, 5 at `Assert.Equal(27L ...)`). |
| L4 | Low | section D and section 2 | "~70 files" estimate and an unnamed "Concurrency generally" grep. | Fixed: 54 `.cs` files in the unit project (including one helper) and 53 integration test methods whose names contain Concurrent, Simultaneous or Parallel (counted by grep). |

* **Plan refresh (author):** after the review, the plan was refreshed for `main` = `c0fc2f4` (merged: #69, #73, #72, #74, #70; PostgreSQL registry 1029; company migrations 0001-0026, system 0001-0027).
* **Blocker or High findings open:** none
* **NOT VERIFIED:** nothing was executed: no test, build, CI-equivalent or PostgreSQL command was run by the reviewer or the author; every "covered" statement means a test exists in the repository, not that it passed. The reviewer shares the author's model family.
* **Verdict:** approve
