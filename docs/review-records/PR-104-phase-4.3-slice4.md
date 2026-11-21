# Independent review record

* **Pull request:** #104 (Phase 4.3 slice 4: backup run of the whole installation, backup-status, retention preview, measured durations; decisions D-069..D-073 implemented)
* **Head SHA reviewed (full 40 characters):** 5fbc221e10c54fafc40ff702a8ec64e4c9146dd2
* **Reviewer context:** Agent C (independent QA/security, separate session from the author, Agent B); model family Claude Opus (claude-opus-5-5), the same family as the author (independent context, not an independent model family); prompt: lead's Critical full-diff review of origin/main...feature/phase-4-3-slice4-impl (nothing of owner package 4.3-2 built, restore safety, migration contiguity and registry, zero skips).
* **Scope read:** the full diff origin/main...feature/phase-4-3-slice4-impl at 5fbc221.
* **Checks run (commands and results):** reviewer: Release build; unit 1162/1162; API 100/100; PostgreSQL BackupRun/CompanyBackupService/BackupRestoreDrill 19/19 (0 skipped) on the disposable 5433 cluster (created and destroyed); CI on 5fbc221 all green; mergeable with main d3bee74. Author (before the review): full PostgreSQL suite 1130/1130 through the harness on the commit before merging main a94bc61, and 68/68 for the backup, runtime and provisioning classes after it.
* **Findings:** verdict approve with five Lows; no Critical, High or Medium
  | ID | Severity | Finding | Disposition |
  |----|----------|---------|-------------|
  | L1 | Low | The cron example sources `admin.env` without exporting, so the variables never reach `dotnet` and the run exits 3 | Fixed after the reviewed head: `set -a; . /etc/hasm/admin.env; set +a` (docs/DEVELOPMENT.md) |
  | L2 | Low | `backup-status` exits 0 when the newest `run.json` is unreadable and an older `ok` run is younger than 26 hours | Fixed after the reviewed head (lead decision under D-048): any unreadable report gives exit 8 `unreadable_reports`; unit test extended; documented; D-071 row updated |
  | L3 | Low | The run lock is a session lock held on an idle connection; `idle_session_timeout` or a network drop would release it mid-run | Fixed after the reviewed head: `SessionKeepAlive` (`SELECT 1` every 30 s on the lock connection); a failed no-op marks the run `partial` with the warning `run_lock_lost` (optional `warnings` in `run.json`); the requirement that `idle_session_timeout` of the admin role be 0 or above 60 s is documented; unit tests for a lost session and an immediate stop |
  | L4 | Low | Two run ids started in the same second sort by their random suffix; a `FinishedUtc` in the future (clock skew) is never stale | Recorded as known limitations in docs/PHASE-4.3.md (one run at a time makes the first practically impossible; NTP required; a future-time check is a follow-up for the monitoring UI slice) |
  | L5 | Low | The implementation status does not say why `totp` is absent from the excluded list | Fixed after the reviewed head: docs/PHASE-4.3.md states that the table does not exist yet and must be added to the flag and the exclusions in the same change (pinned by the test) |
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the L2 and L3 code changes come after the reviewed head and await Agent C's confirmation of that delta. Not tested: the operating-system scheduler examples (manual drill only), real-tool TLS, PostgreSQL 14/15 servers. Owner package 4.3-2 remains open (no encryption, signature, deletion or key export).
* **Verdict:** approve
