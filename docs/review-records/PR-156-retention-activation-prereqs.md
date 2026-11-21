# Independent review record

* **Pull request:** #156 (Phase 4.3: clear retention activation prerequisites L1, L2 and L6 of #134; deletion stays inactive)
* **Head SHA reviewed (full 40 characters):** 5caaab695cbf061e97459628c7b46ef5364fac97 (G's confirmation of 39782e7..5caaab6; round 1 at 39782e71f3256276fe6dbd1db8a35bccccd155d0)
* **Reviewer context:** Agent G (independent security/audit reviewer), a separate agent from the author (Agent B); model family Claude Opus. The lead reassigned the review from Agent C. The prompt is given below.
* **Scope read:** the full diff against `origin/main` (`RetentionService.cs`, `RetentionExecutorTests.cs`, `BackupRetentionTests.cs`, `docs/PHASE-4.3.md`, `docs/INTEGRATION-STATUS.md`).
* **Checks run (commands and results):**
  * Retention unit tests: 61/61;
  * isolated PostgreSQL BackupRetentionTests: 7/7, including a temporary uncommitted probe of the real `DeletedRecordedAsync`;
  * mutations, all reverted:
    * `RequireInside` as a no-op: caught;
    * a forced 2016: caught;
    * the run-folder-exists skip dropped: caught;
    * `pg_advisory_unlock_all` removed: NOT caught in 2 runs (L-a).
* **Findings:** round 1 approve: 0 Critical, 0 High, 0 Medium, 4 Low. All four Lows are fixed in 31e499f, the registry in 5caaab6, and main is merged in 5eec771 (parent ee5bf02; see L-e).

| Round | ID | Severity | Finding | Resolution |
|---|---|---|---|---|
| 1 | L-a | Low | The L6 test does not detect the fix (removing `pg_advisory_unlock_all` still passed, because the probe opens slower than the backend exits) | Fixed in 31e499f: `RestoreLockHolder` has a test hook on the real dispose path, run after the explicit unlock and before the close. The overlap test asserts that `pg_locks` (advisory, `pid = pg_backend_pid()`) is 0 on the holder's own session, with no timing dependency. Mutation proof on the isolated runner: with the unlock removed, the test fails (expected 0, got 1); restored, the class passes 7/7. |
| 1 | L-b | Low | No committed PostgreSQL test of `DeletedRecordedAsync` | Fixed in 31e499f: `A_completion_event_is_recognised_only_for_its_own_run` runs the real (now internal) method. A 2015 does not match, the matching 2016 does, and the neighbouring run id (last hex digit different) does not until its own 2016 exists. A malformed id is refused (`invalid_run_id`). It is registered in 5caaab6 (BackupRetentionTests 6 → 7; total 1185 at that head). |
| 1 | L-c | Low | `Context.DeletedRecorded` is optional | Fixed in 31e499f: it is required. The unit tests pass an in-memory equivalent of the stream check. |
| 1 | L-d | Low | A recovered 2016 for a tombstone whose folder vanished outside the engine; mounts are not named in the residual text | Fixed in 31e499f: a recovered 2016 records `resumed=yes recovered=audit folder=absent`. PHASE-4.3 names bind mounts and other mount points in the residual: they are not links, so they are not detected; creating one needs root; the runbook keeps mounts out of the backup root. |
| 2 | L-e | Low | Cosmetic: the merge commit 5eec771's message names main d607cfb, but its merged parent is ee5bf02 (the content checks against ee5bf02 match) | Accepted (lead decision): history is not rewritten; this row records the correct parent. |

Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "independent full-diff review of Draft PR #156 retention activation prerequisites: L1 checks not bypassable beyond the documented residual window, L2 never double-writes 2016 or marks an incomplete deletion complete, deletion gate unreachable in production, tests meaningful"; Head SHA reviewed 39782e71f3256276fe6dbd1db8a35bccccd155d0; executed: full diff vs origin/main read, Retention unit tests 61/61, isolated PostgreSQL BackupRetentionTests 7/7 including a temporary uncommitted probe of the real DeletedRecordedAsync (2015 not matched, matching 2016 matched, neighbouring run id not matched), mutations (RequireInside no-op caught; forced 2016 caught; dropped run-folder-exists skip caught; removed pg_advisory_unlock_all NOT caught in 2 runs), all reverted; verdict approve; findings L-a (L6 test does not detect the fix), L-b (no committed PostgreSQL test of DeletedRecordedAsync), L-c (DeletedRecorded optional in the context), L-d (recovered 2016 for a tombstone whose folder vanished outside the engine; also name mounts in the residual-window text).

Confirmation: Reviewer: Agent G (independent security/audit reviewer, separate agent; not the author); model family Claude Opus; prompt: "confirm the #156 fix delta 39782e7..5caaab6: L-a timing-free lock-release test (re-run the mutation), L-b PostgreSQL test of the real DeletedRecordedAsync (internal visibility widens no production surface), L-c DeletedRecorded required, L-d folder=absent and mounts documented, merge of main brings no unrelated content, gate still unreachable"; Head SHA reviewed 5caaab695cbf061e97459628c7b46ef5364fac97; executed: delta diff read, merge checked against its actual main parent ee5bf02 (only the PR's files plus the 4.3-ACT row; sorted removed-lines check matches), InternalsVisibleTo checked (test assemblies only), isolated PostgreSQL BackupRetentionTests 7/7, L-a mutation (pg_advisory_unlock_all removed) fails "Expected 0, Actual 1", reverted, Retention unit tests 61/61; verdict approve; findings L-a, L-b, L-c, L-d fixed; new Low (the merge commit message names d607cfb, but the merged parent is ee5bf02).

* **Blocker or High findings open:** none. L-a..L-d are fixed in 31e499f and confirmed by G at 5caaab6; L-e is accepted (cosmetic).
* **NOT VERIFIED:** hosted CI of the final head (#156 waits for its turn in the serial registry queue, after #158; it will merge main and re-set the registry total then).
* **Verdict:** approve
