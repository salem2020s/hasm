# Independent review record

* **Pull request:** #120 (D-120: S1 ships with `user_totp` always excluded from the system dump; documentation only)
* **Head SHA reviewed (full 40 characters):** 20daccc177a431a4b8f983bf4b25cf7789fd9b8b
* **Reviewer context:** Agent G, an independent security/audit reviewer, a separate agent from the author (Agent D); model family Claude Opus; read-only.
* **Scope read:** `git diff origin/main...20daccc` (`docs/DECISIONS.md`, `docs/PHASE-4.1.md`) read in full and checked against D-091 (#116), D-106/D-107 and the #104 pin test (`SystemDumpExclusions` set-equal to `SecretStoreTables`).
* **Checks run (commands and results):** git fetch/ls-remote and `git diff origin/main...20daccc`, read in full and cross-checked as stated; no build or tests (documentation only).
* **Findings:** approve with 3 Low findings, all fixed in the wording delta below.

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "review #120 (D-120 user_totp backup sequencing) for consistency with D-091, the H1 safeguard and #116"; Head SHA reviewed 20daccc177a431a4b8f983bf4b25cf7789fd9b8b; executed: git fetch/ls-remote, `git diff origin/main...20daccc` (DECISIONS.md, PHASE-4.1.md) read in full and checked against D-091 (#116), D-106/D-107 and the #104 pin test (`SystemDumpExclusions` set-equal to `SecretStoreTables`); no tests (docs only); verdict APPROVE; findings 0 Critical/High/Medium, 3 Low (L1 order-independent flip and a D-120 reference in #116; L2 S1 test text still lists encrypted-run restores; L3 record the D-120..D-129 range in DECISIONS.md).

| ID | Severity | Problem | Resolution |
|----|----------|---------|------------|
| L1 | Low | The flip to "included only in encrypted runs" assumed a merge order; make it order-independent and reference D-120 in #116 | Wording fixed in c5f1ebdaf12202bcb34a2ece71f2a7de025c8d49 ("whichever of S1 and the 4.3 slice 5 implementation merges SECOND sets `user_totp` to included only in encrypted runs; until both are on main it is always excluded"). The D-120 reference inside #116 belongs to that PR's author (Agent B) and is NOT part of this PR |
| L2 | Low | The S1 test text still lists encrypted-run restore tests | Fixed in c5f1ebdaf12202bcb34a2ece71f2a7de025c8d49 (sections 4, 11 and 12: S1 tests the restore without `user_totp` rows; the encrypted-run restore tests move to the 4.3 slice 5 implementation PR) |
| L3 | Low | Record the D-120..D-129 allocation in DECISIONS.md and correct D-104's "exhausted" wording | Fixed in c5f1ebdaf12202bcb34a2ece71f2a7de025c8d49 (D-104 rows in `docs/DECISIONS.md` and the plan) |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** Agent G has not yet confirmed the wording delta 20daccc..c5f1ebd (L1-L3); the D-120 reference in #116 is outside this PR; documentation only (no code, workflow or protected file), so no build or test applies.
* **Verdict:** approve
