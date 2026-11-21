# Independent review record

* **Pull request:** #130 (Phase 4.3 slice 6 plan: retention and deletion of backup runs, D-086; D-130..D-139)
* **Head SHA reviewed (full 40 characters):** 0734152b8fb73e52e471f3730a61705fdebc5123 (round 1)
* **Reviewer context:** an independent reviewer subagent, a separate agent from the author (Agent B), with no access to the author's context; model family Claude Opus; read-only. The prompt is given below.
* **Scope read:** `git diff origin/main...origin/docs/phase-4-3-slice6-plan` at 0734152 (the full diff: `docs/PHASE-4.3.md` slice 6 section, `docs/DECISIONS.md` D-130..D-139, the BLOCKED list).
* **Checks run (commands and results):** the plan PR is documentation only, so no build or tests apply. The reviewer ran:
  * `git fetch` and `git diff origin/main...origin/docs/phase-4-3-slice6-plan`;
  * the decision-ID duplicate scan at the head;
  * reads of D-082, D-085..D-088 and D-114, and of INTEGRATION-STATUS on main;
  * `git grep` on main for the company `Settings.TimeZone`, `RequireBusinessTimeZoneAsync` and `FiscalYear.TimeZone`;
  * a REST read of #128 (open).
* **Findings:** round 1 requested changes: 0 Critical, 0 High, 2 Medium, 3 Low.

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | M1 | Medium | The gate was checked only in the command; the executor and its seam could run without layers 1 and 3 | Fixed in 42623b8 with the reviewer's wording: "The code gate (1) and the opt-in (3) are checked inside the deletion executor itself, in the lowest-level routine that renames or deletes a run folder, not only in the command. The internal test seam is the only path that can supply a test-only override, it is reachable only from the test assemblies, and a unit test asserts that every non-test entry point refuses while the gate is off." (PHASE-4.3 D-137 section, tests list, DECISIONS D-137). Carried into the WIP code on `feature/phase-4-3-slice6-impl`. |
| 1 | M2 | Medium | Unknown key needs were not defined to include unverifiable, missing, unreadable or incomplete statuses | Fixed in 42623b8 with the reviewer's wording: "A retained run's key needs are `unknown` when its status is version 1, does not verify (`status_unverifiable`), is missing or unreadable, or the run is incomplete; any `unknown` run makes the report false (`backup_inventory_incomplete`)." (PHASE-4.3 D-138 section, unit tests added to the test list, DECISIONS D-138). |
| 1 | L1 | Low | An unconfigured company time zone was not explicitly `classification_unknown` | Fixed in 42623b8: D-131 now says "a company time zone that is not configured, as well as an unknown or unresolvable one, gives `classification_unknown` and the run is kept"; a unit test is in the test list. |
| 1 | L2 | Low | The audit block split (2010-2019) and D-130..D-139 are not recorded in INTEGRATION-STATUS on main | Recorded by the lead in the handoff: 2010-2013 slice 5, 2014-2019 slice 6, 2020-2029 slice 7; D-130..D-139 allocated to Agent B. |
| 1 | L3 | Low | The owner-only activation is not in the owner-decision register | Recorded by the lead in the handoff: register row "4.3-ACT: activation of automatic backup deletion, OPEN with the owner". |

Round 1: Reviewer: independent reviewer subagent (independent, separate agent); model family Claude Opus; prompt: lead's independent full-diff review of PR #130 (Phase 4.3 slice 6 plan, retention and deletion of backup runs, against D-086 with D-085, D-087, D-088, D-082/K and D-114: raise-only minimums, month-end/year-end default, fiscal-calendar and time-zone classification, hard keeps, audit/preview/safe deletion, three-layer inactivity and the owner-instructed activation, fail-closed key retirement, honest erasure note, audit 2014-2019, safe owner-question defaults, nothing invented); Head SHA reviewed 0734152b8fb73e52e471f3730a61705fdebc5123; executed: git fetch, git diff origin/main...origin/docs/phase-4-3-slice6-plan, the decision-ID duplicate scan at the head, reads of D-082/D-085..D-088/D-114 and of INTEGRATION-STATUS on main, git grep of the company Settings.TimeZone, RequireBusinessTimeZoneAsync and FiscalYear.TimeZone on main, REST read of #128 (open); verdict request changes; findings M1 (gate checked only in the command; the executor and its seam can run without layers 1 and 3), M2 (unknown key needs not defined to include unverifiable, missing, unreadable or incomplete statuses), L1 (an unconfigured company time zone is not explicitly classification_unknown), L2 (audit block 2010-2019 split and D-130..D-139 not recorded in INTEGRATION-STATUS on main), L3 (owner-only activation not in the owner-decision register); no Critical or High.

* **Blocker or High findings open:** none
* **NOT VERIFIED:** the fixes in 42623b8 await the reviewer's confirmation. The plan is implemented and tested only in the slice 6 implementation PR.
* **Verdict:** request changes
