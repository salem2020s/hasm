# Independent review record

* **Pull request:** #70 (Phase 4.3 slice 1: coordinated company backup set)
* **Head SHA reviewed (full 40 characters):** ca6a45f68b43fd043022b2dcc090a28aa361317f (first review round was on 2243ec978c97b6db6c4fb98455b250f40d3cdb42; re-review by Agent C of ca6a45f is PENDING)
* **Reviewer context:** Agent C, a separate session; model family claude-sonnet-5-5, the same family as the author (stated honestly: independent context, not an independent model family). Prompt: review the full diff against origin/main for correctness, isolation (Scenario 21.8), key handling (decision K) and path safety. Findings below are relayed from Agent C's reports through the lead; this record does not reproduce the reports verbatim.
* **Scope read:** full diff against origin/main (src/backend/Hasm.Infrastructure/Backup/BackupSet.cs, tests/unit/Hasm.Infrastructure.Tests/BackupSetTests.cs, docs/PHASE-4.3.md)
* **Checks run (commands and results):** author: none locally (no .NET SDK in the author session). Hosted CI on ca6a45f: reported fully green by the lead (Backend build-test incl. BackupSetTests, postgres-integration, Verification). The first push (4c95366) failed 8 BackupSetTests in CI because of a header-only key read; fixed in ca6a45f.
* **Findings:**
  * Medium: `RestoreAttachments` did not bind the company; a set of another company could be restored under this one. Fixed in 4c95366: `expectedCompanyId` is required, mismatch is refused (`company_mismatch`); tests added.
  * Medium: `KeyIdsRequired` covers attachment envelopes only; encrypted import-file originals live outside the folder. Accepted and recorded as a limitation (manifest doc comment, docs/PHASE-4.3.md); the import-file rewrap retirability report is a later slice.
  * Low (fixed in 4c95366/ca6a45f): staged files re-hashed before the final move; envelope header read instead of the whole file; partial set removed when Create fails; directory and file symbolic links refused; overlap checks on resolved paths; root target refused with BackupException; tests added for cross-company restore, symlinks, failed-create cleanup and altered source.
* **Blocker or High findings open:** none known
* **NOT VERIFIED:** the author ran no code locally. The pg_dump/pg_restore path, encryption, scheduling, retention, destinations, monitoring and RPO/RTO are not built. Windows symlink behaviour is not exercised (test skipped on Windows).
* **Verdict:** pending (Agent C's re-review of ca6a45f)
