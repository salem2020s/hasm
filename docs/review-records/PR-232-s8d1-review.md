# Independent review record

* **Pull request:** #232 (Phase 4.3 S8d-1, read only: the audit destination field, authenticated deletion facts, the tombstone states, `replication_pending`, the destination plan, deletion readiness); full review of the merge base fed36d8..b2aac3c
* **Head SHA reviewed (full 40 characters):** b2aac3c958817823d146d922e8a455ed6fd52014
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author; reviewer of the S8d plan through all its rounds). Model family Claude Opus. Prompt (summary): "independent review of S8d-1 against the merged plan `docs/PHASE-4.3-S8D-PLAN.md` and my records. Nothing can delete or move a file (`AutomaticDeletionActivated` untouched, apply unchanged). The authenticated read of 4B as approved (`VerifyAsync` with `readEvidence` in one READ ONLY snapshot, facts only at or below `VerifiedThroughSeq` from a full pass from sequence 1, a forged row ends the prefix, `backup-status`'s raw read never accounts for a run), attacked with inserted rows: a forged 2015/2016 pair and a splice. The tombstone three-condition exception only through authenticated state. `replication_pending` can only keep more. `backup-retention-plan` writes only 2014 and every tree stays byte-identical. The changed `RetentionExecutorTests` assertion. The deferred items recorded".
* **Scope read:** the full diff against the merge base (16 files): `Audit/AuditVerifier.cs` (the `readEvidence` overload), `Backup/ReplicaAudit.cs` (`Build` with sequences, 2015/2016 facts, `ReadVerifiedAsync`, the raw `ReadAsync`), `ReplicaTombstones.cs`, `ReplicationPending.cs`, `DeletionReadiness.cs`, `ReplicaStatus.cs` and `ReplicaStatusReport.cs`, `Retention.cs` (`Plan` with `pendingRunIds` and `testedKeysElsewhere`), `RetentionService.cs` (`PlanLocationAsync`, the `destination=primary` token on the executor's 2014-2017 details), `Hasm.Admin/Program.cs` (`backup-retention-plan --destination`, readiness lines), `docs/PHASE-4.3.md` (as built), the registry, and the new tests `ReplicaDeletionEvidenceTests` (5), `ReplicaAuditVerifiedReadTests` (4) and `ReplicaRetentionPlanTests` (2), plus the changed `RetentionExecutorTests`.
* **Checks run (commands and results):**
  * **Nothing can delete or move a file.**
    * `BackupRetention.AutomaticDeletionActivated` is unchanged (`false`, `Retention.cs` line 75).
    * `backup-retention-apply` is unchanged: same usage, no `--destination`.
    * The executor's only change is the `destination=primary` token appended to its 2014-2017 details. Slice 6's `DeletedRecordedAsync` pattern `% run=<id> %` still matches.
    * Every new path is a read: tombstone and folder scans, `ReadRunStatus`, `CiphertextDigests.ReadVerified`, the read-only audit snapshot, and the classification. The plan command writes only audit 2014 (with the destination token) and, when asked, the operator's `--json` file, as before.
    * `ReplicaRetentionPlanTests` compares the primary and destination trees (every path and each file's SHA-256) before and after, and they are byte-identical.
  * **The authenticated read (4B).**
    * `AuditVerifier.VerifyAsync(stream, readEvidence)` calls the reader once, after the chain walk, inside the verifier's own transaction, which is `REPEATABLE READ` and `SET TRANSACTION READ ONLY` (one snapshot).
    * `ReadVerifiedAsync` reads 2015, 2016, 2040, 2041 and 2042 with `Seq <= VerifiedThroughSeq`; the pass always starts at sequence 1 (no checkpoint shortcut). `UnverifiedRemainder` marks rows above the prefix, and readiness reports `audit_unverified`.
    * The raw `ReadAsync` sets `Authenticated = false`. `ReplicaStatus` exposes `ExplainedDeletionRunIds` only for an authenticated state, and `ReplicationPending` uses the tombstone exception only when `Authenticated` is true. So `backup-status`'s raw read never accounts for a run; it only feeds the alert lines (unexplained or incomplete, both exit 1).
  * **Probe: a SPLICE of genuine records** (isolated cluster through the CPU gate; a temporary test in `ReplicaAuditVerifiedReadTests`, removed afterwards).
    * Above a genuine 2015/2016 pair for run A, I inserted a 2015/2016 pair for run B sealed with the REAL audit key (genuine MACs: `CheckRecord` returned `Ok` for both) but linked to a predecessor the stream does not have, which is a rolled-back history.
    * Result: `VerifiedThroughSeq` equals the genuine head and the remainder is flagged. Genuine pair A counts; spliced pair B does not, and its 2015 is not seen. Only the raw informational read sees B.
    * The forged-MAC pair and the sequence gap are covered by the suite's own tests.
  * **Mutations (local, reverted; the tree was verified clean after each), PostgreSQL classes `ReplicaAuditVerifiedReadTests` and `ReplicaRetentionPlanTests` through the CPU gate:**
    * base: 7/7, with my probe;
    * removing the `VerifiedThroughSeq` limit from the evidence read fails `A_forged_pair_above_the_head_…` and `A_missing_audit_key_…`, and makes my spliced pair count;
    * letting a non-authenticated state explain a tombstone in `ReplicationPending` fails `Replication_pending_is_read_from_the_destination_folder_and_the_tombstone_exception_needs_an_authenticated_audit_state`.
  * **Tombstone classification (5.3):** a run is explained only when ALL of the following hold:
    * the tombstone is a plain folder named like a run, whose signed status verifies and whose hash equals the primary's (its run folder, or its verified tombstone);
    * the audit state holds a 2015 for that destination and hash, and a completed 2015/2016 pair;
    * no 2040 follows the pair's 2015;
    * neither the run folder nor a `.deleting-*` folder exists.
    
    A 2015 with no 2016 (or a `.deleting-*` folder) is `retention_deletion_incomplete`; anything else is unexplained. A 2015 without a status is ignored. Exact tokens are used (`nas-1` is never `nas`), and an event with no destination token belongs to the primary.
  * **`replication_pending`:** it can only keep more.
    * The keep reason is added to the classification; nothing is removed from it.
    * A destination that cannot be read, a copy that is not replicable, a copy with a different status hash, a copy without a bound signed inventory, or one reported corrupt all count as missing.
    * `testedKeysElsewhere` only adds recovery keys with a proven test on any copy, and it is computed from verified runs with a valid proof.
  * **The changed `RetentionExecutorTests` assertion:** the expected suffix `recovered=audit folder=absent` becomes `recovered=audit folder=absent destination=primary`, because the detail now ends with the destination token. It is still an exact `EndsWith`, now including the token, so it is legitimate and not weakened.
  * **Deferred items** are recorded in `PHASE-4.3.md` (as built):
    * the quarantine exclusion (it needs the audit-head plan's slice 2);
    * the 2011 recency (S8d-2's protection check);
    * audit 2048;
    * the unaccounted-removal finding (S8d-3, the retirement events);
    * the full re-hash and the executor wiring (S8d-2).
  * **Registry:** 1733 over 259 classes = the merge base's 1727 + `ReplicaAuditVerifiedReadTests` 4 + `ReplicaRetentionPlanTests` 2. The PR is dirty against current main only through the registry; a merge round follows. No protected file is in the diff; `ci-guard --base` (the merge base, main's copy of the tool) is clean.
  * Hosted CI on b2aac3c, read by REST: backend-build-test, postgres-integration, ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest all completed success.
* **Findings:** 0 Critical, 0 High, 0 Medium, 3 Low (A1-A3), each recorded with a follow-up.
  * **Low A1 (forged rows are inserted through the owner connection).** The plan's T22 says the attacker is the system runtime role. The tests insert forged rows with the owner connection (`InsertRaw`). The reader is role-agnostic, so the result is the same, but one case should insert as the real restricted runtime role, so the test also proves the role can do it and that nothing else (a grant, a trigger) is relied on. Follow-up: S8d-2's T22 set.
  * **Low A2 (a status-less 2016 closes every open 2015 of the same destination and run).** `CompletedPairSeq` and `OpenStartSeq` treat a 2016 without a `status=` (the resume or recovery path) as closing ANY earlier 2015 of that destination and run, whatever status hash the 2015 named. One run id normally has one status hash, but a conflicting copy (a different hash under the same id) is possible. Follow-up: let a status-less 2016 close only the newest open 2015 before it, and add a unit case.
  * **Low A3 (the splice case is not in the suite).** S8d-1 builds the authenticated read, and the plan lists the splice as T22(c). The suite tests a forged MAC and a gap, but not a splice of genuine records. My probe shows the code handles it; add it as a permanent test (records sealed with the real key, a wrong `PrevHash`, above the head). Follow-up: this PR's merge round or S8d-2.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the following two items.
  * The full local suite: hosted postgres-integration on the exact head is the evidence.
  * The merge with current main, which is still to come and needs its own confirmation.
* **Verdict:** approve

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: review of S8d-1 (PR 232): no deletion path, the authenticated read attacked with a forged pair and a splice, the tombstone exception, `replication_pending`, the plan command, the changed executor assertion, the deferred items; Head SHA reviewed b2aac3c958817823d146d922e8a455ed6fd52014; executed: full diff read, a splice probe with genuine-MAC records (excluded from evidence), two mutations each caught by a named test, the tree-identity test read, registry 1733 equal to the sum, no protected file, ci-guard clean, hosted CI success; verdict approve; findings A1-A3 (Low, recorded with follow-ups).
