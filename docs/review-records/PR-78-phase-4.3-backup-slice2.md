# Independent review record

* **Pull request:** #78 (Phase 4.3 slice 2: pg_dump/pg_restore into an isolated target, restore drill, review Lows of PR #70)
* **Head SHA reviewed (full 40 characters):** dd2521770e8050223f97d985749dcf95a9f3c714
* **Reviewer context:** Agent C, a separate session; model family claude-sonnet-5-5, the same family as the author (stated honestly: independent context, not an independent model family). Prompt: independent FULL-diff review, read-only, of PR #78: command injection and argument construction, admin credential handling, refusal rules, cleanup on failure, isolation (21.8), TOCTOU, resource handling, whether the drill tests prove isolation, test adequacy, registry +2. Findings below are relayed from Agent C's report through the lead.
* **Scope read:** the full 6-file diff against origin/main
* **Checks run (commands and results):** the reviewer executed nothing. Hosted CI on dd25217 is green (Backend: backend-build-test and postgres-integration including the 2 new drill tests; Verification). Author: nothing executed locally (no .NET SDK in the author session).
* **Findings:** Agent C approved dd25217 with one Medium and seven Lows; dispositions:
  * Medium: the drill proved the system database only by existence. Fixed after the reviewed head: catalog-and-content digests of the source, another company database and the system database are compared before and after, and the source digest is checked after the dump. A failed drop-on-failure cleanup is now reported as `restore_failed_cleanup_failed` instead of being swallowed (a test that blocks the drop is not built: no robust way on the shared disposable server; recorded).
  * Low, fixed after the reviewed head: the restore path must be fully qualified; a duplicate-database race on CREATE maps to `target_exists`; the target is created WITH `TEMPLATE template0` and the source encoding; tool output is copied to a null stream; the child environment is cleared to `PATH`, `SystemRoot` and `PG*`; a cyclic symbolic link in RealPath is a BackupException; a CI step asserts the client tools' major version equals the server's, docs/CI-RUNNER.md notes the requirement; refusals of postgres/template0/template1/maintenance are now tested.
  * Low, recorded in docs/PHASE-4.3.md: the class validates names only (the orchestration slice must tie the source to a real registry database and the dump to the manifest's company); the blocked-drop path is untested.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the reviewer executed nothing. The delta after dd25217 (the fixes above) has not been reviewed. Cutover, scheduling, retention, destinations, set encryption, monitoring and RPO/RTO are not built.
* **Verdict:** request changes
