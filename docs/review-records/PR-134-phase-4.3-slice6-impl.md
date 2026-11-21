# Independent review record

* **Pull request:** #134 (Phase 4.3 slice 6 implementation: retention of backup runs, automatic deletion inactive; stacked on #128)
* **Head SHA reviewed (full 40 characters):** 82074cd77408036cda5de5c5973548312938fa2e (round 1)
* **Reviewer context:** Agent C (independent QA/security reviewer), a separate session from the author (Agent B); model family Claude Opus. The prompt is given below.
* **Scope read:** the FULL diff `origin/main...59daaf7`, with the findings concentrated on the slice 6 delta against `feature/phase-4-3-slice5-impl`, plus the fix delta 59daaf7..82074cd.
* **Checks run (commands and results):**
  * Release build; unit 1239/1239 (and API 117/117 at 59daaf7);
  * PostgreSQL BackupRetention / SystemDatabase / EncryptedBackup / BackupRun / CompanyBackupService / BackupRestoreDrill 40/40 (0 skipped) on 82074cd, on the disposable 5433 cluster (sole holder, destroyed);
  * the failed hosted postgres-integration log of 59daaf7 (42601 in migration 0030);
  * R1's schema dictionary on main;
  * hosted CI on 82074cd NOT VERIFIED (no check runs: the branch conflicts with main 364a519).
* **Findings:** round 1 requested changes: 0 Critical, 2 High, 0 Medium, 4 Low.

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | H1 | High | Migration 0030 CHECK syntax (42601 in hosted CI) | Fixed in 82074cd (the parenthesis of the scope CHECK). |
| 1 | H2 | High | The D-134 attachment-hold tripwire does not match R1's LegalHold/RetentionPolicy/RetentionRule keys, so attachment legal holds would be ignored, fail-open once main is merged | Fixed in b6e050e. Retention reads R1's company `LegalHold` table: any active hold of any scope keeps every run containing the company (`attachment_hold`), and an unreadable hold table keeps everything (`hold_check_failed`). The exact R1 keys are pinned by a unit test, and any other company "Hold" table trips `hold_check_failed`. A PostgreSQL test places a real record-scoped R1 hold and shows that every run is kept and the engine deletes nothing. |
| 1 | L1 | Low | Recursive time-of-check to time-of-use in `DeleteRunFolder` | Recorded as an ACTIVATION PREREQUISITE in PHASE-4.3 (deletion is inactive); to be listed with the 4.3-ACT register row. |
| 1 | L2 | Low | Audit 2016 is not resumable | Recorded as an ACTIVATION PREREQUISITE in PHASE-4.3. |
| 1 | L3 | Low | Resume only with a matching tombstone or 2015 | Fixed in b6e050e: resumed only when the engine's own tombstone exists (`resume_unverified`, audit 2017), with a unit test. |
| 1 | L4 | Low | Audit 2017 for runtime-prerequisite refusals | Fixed in b6e050e. |

Round 1: Reviewer: Agent C (independent QA/security, separate session); model family Claude Opus; prompt: lead's independent review of Draft PR #134 (Phase 4.3 slice 6, backup retention and inactive deletion, stacked on #128) against D-086, D-087, D-114, D-118 and D-130..D-139 (inactive deletion with its three layers and the lowest-level gate, no production use of the test override, classifier minimums over recoverable runs, period/year ends on both sides, explicit time zones and DST, unknowns kept, append-only holds and pins, fail-closed R attachment holds, key inventory M2, audit 2014-2018, restore lock, exit 8, path/symlink/TOCTOU, no owner-reserved policy); read the FULL diff origin/main...59daaf7 with the findings concentrated on the slice 6 delta against feature/phase-4-3-slice5-impl, plus the fix delta 59daaf7..82074cd; Head SHA reviewed 82074cd77408036cda5de5c5973548312938fa2e; executed: Release build, unit 1239/1239 (and API 117/117 at 59daaf7), PostgreSQL BackupRetention/SystemDatabase/EncryptedBackup/BackupRun/CompanyBackupService/BackupRestoreDrill 40/40 (0 skipped) on 82074cd on the disposable 5433 cluster (sole holder, destroyed), the failed hosted postgres-integration log of 59daaf7 (42601 in migration 0030), and R1's schema dictionary on main; hosted CI on 82074cd NOT VERIFIED (no check runs: the branch conflicts with main 364a519); verdict request changes; findings H1 (High, fixed in 82074cd: migration 0030 CHECK syntax), H2 (High, open: the D-134 attachment-hold tripwire does not match R1's LegalHold/RetentionPolicy/RetentionRule keys, so attachment legal holds would be ignored, fail-open once main is merged), L1–L4 (Low).

* **Blocker or High findings open:** H2 fixed in b6e050e, awaiting C's confirmation.
* **NOT VERIFIED:** hosted CI of the fixes. The tree has the allocated gap at system 0031 until #133 merges (system migration 0032 by lead allocation), so the PostgreSQL suites and the migration-loading unit tests are red by design until then. The author ran the unit tests and BackupRetentionTests 6/6 locally with the migration temporarily at 0031.
* **Verdict:** request changes
