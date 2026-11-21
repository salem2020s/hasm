# Independent review record - PR #102, one shared bounded acquisition of a company's advisory lock

* **Pull request:** #102
* **Head SHA reviewed (full 40 characters):** 9b9a5947661eb6abfafe1d1f079a8805f2580079
* **Reviewer context:** Agent C, independent QA/security, a separate session from the author (Agent D), model family Claude Opus (claude-opus-5-5, a different family from the author's claude-sonnet-5-5), fresh context, read-only. Prompt: full-diff review of the bounded advisory-lock acquisition: semantics, resources held while waiting, deadlock, masking of real problems, test weakening and cancellation.
* **Scope read:** the full diff against `origin/main`: `BoundedWait`, `CompanyProvisioner.AcquireLockAsync`, its four call sites (provision, grant-company-runtime, migrate-companies, backup), the changed tests and the registry.
* **Checks run (commands and results):** Release build 0 errors; unit project 1153 of 1153; `Hasm.Api.Tests` 91 of 91; PostgreSQL 73 of 73 (CompanyProvisioning, CompanyRuntime, CompanyBackupService, BackupRestoreDrill, CompanyMigrat* classes) on a disposable 5433 cluster that was created and destroyed. The author additionally ran 80 lock-related PostgreSQL tests five times in a row (80 of 80 each time) through the Linux harness lock.
* **Findings:** no Critical, no High, no Medium. Verified without defect: every status code and message is unchanged; `TryLockAsync` stays the single attempt; while waiting only one idle pooled connection is held and no transaction is open; there is no deadlock (`pg_try_advisory_lock` never blocks and one company lock is taken at a time); a lock that is truly held still reports busy after the bounded wait; the tests were not weakened (only the holder's acquisition in two existing tests changed, all assertions identical); cancellation is honoured. Three Low, recorded as follow-ups and not fixed here:

| # | Sev | Finding | Disposition |
|---|-----|---------|-------------|
| L1 | Low | The two "released a moment ago is waited for" tests rely on PostgreSQL releasing a disconnected session's lock within the roughly 1.7 s margin left by the bounded wait. | Recorded follow-up (optional): the holder could release explicitly with `pg_advisory_unlock` before disconnecting. |
| L2 | Low | `migrate-companies` over N busy companies now adds about 2.7 s per busy company. | Recorded follow-up: document it in the administration docs. |
| L3 | Low | The lock key is a 4-byte projection of the company id, so two companies could in principle collide. | Pre-existing, recorded only. |

* **Blocker or High findings open:** none
* **NOT VERIFIED:** postgres-integration was still running on the hosted CI at review time (it is confirmed by the lead's exact-head check before merge).
* **Verdict:** approve
