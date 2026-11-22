# Independent review record

* **Pull request:** #214 (Phase 4.3 slice 8 S8c: replica re-verification, corrupt copies in the status, restore from a copy; D-185, D-186, D-188, D-189); full review of f365991..6dce4b3 (the PR's base, main f365991)
* **Head SHA reviewed (full 40 characters):** 6dce4b3e7d1a39371b936489458f9bbfbe6a7af9
* **Reviewer context:** Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus. Prompt (summary): "independent security review of Draft PR 214, full diff against the PR's base f365991 in a new worktree. Focus: (1) can a copy be falsely reported verified (whole run folder replaced by another signed run, rollback under the same run id, status, inventory and files swapped together; what binds a copy to the run id and the company); (2) can backup-status be fooled (token parsing, forged or missing audit, report-once hiding a newly corrupt copy, a fresh 2040 clearing a report for the wrong folder); (3) whether the default-selection gap for company- and date-scoped holds is acceptable as Low; (4) locks and side effects, read-only database access; (5) restore from a copy equals restore from the primary and a damaged copy is refused; (6) tests and mutations real, registry 1390, no protected files; targeted runs through the CPU gate only".
* **Scope read:** the full diff `git diff f365991 6dce4b3` (13 files): `ReplicaVerifyService.cs` and `ReplicaAudit.cs` (new), `ReplicaCopier.cs` (`VerifyStoredAsync`, `CorruptAsync`), `ReplicaStatus.cs`, `ReplicaStatusReport.cs`, `BackupAuditEvents.cs`, `Hasm.Admin/Program.cs`, `docs/DEVELOPMENT.md`, `docs/PHASE-4.3.md`, the registry in `.github/workflows/backend.yml`, `BackupReplicaVerifyTests`, and the new `ReplicaCopierTests` and `ReplicaStatusTests` cases. Context read: `SignedDocuments.ReadRunStatus`, `CiphertextDigests.ReadVerified`, `RetentionInventory.Scan`, `Read` and `Inspect`, `RetentionClassifier.Plan` (hard keeps), `BackupAudit.Operator`, `BackupMaterialLoader.ServerTrust`, D-133, D-134, D-185, D-186, D-189, and the planned text of `docs/PHASE-4.3.md` ("Re-verification of old copies").
* **Checks run (commands and results):**
  * Binding of a copy (probe P2 to P4, a temporary unit test against `VerifyStoredAsync`, removed afterwards):
    * P2: another run's complete signed folder placed under this run id gives `status`. `ReadRunStatus` binds the signed run id to the folder name.
    * P3: this run's status and inventory with another run's dump swapped in gives `hash`.
    * P4: this run's status with another run's signed inventory and files gives `digest_list` (the inventory's hash is bound in the signed status).
    * Rollback under the same run id: not possible. `run.status.json` is written and signed once per run (`BackupRun` is the only writer), so there is no older signed status for that id.
    * Company: the company folders and every encrypted file are pinned by name, size and SHA-256 in the signed inventory, and the manifests' signatures are checked.
    * No trust (P5) gives `status`.
    * A sub-folder replaced by a link (P6) gives `link`.
  * **Probe P1, finding M1:** the destination's run folder was replaced by a symbolic link to the primary's run folder. `VerifyStoredAsync` returns null (VERIFIED). `RetentionInventory.Scan` reads that folder as not replicable, so the default `Select` picks it ("a folder that does not verify its status is always examined"). `ReplicaVerifyService` then writes 2040 with `reverified=true` and reports VERIFIED (exit 0 when alone), while `backup-status` counts the same folder as `replica_corrupt`.
  * Audit parsing: the tokens are exact (`nas-1` is never `nas-10`). The values cannot inject tokens: the operator is `[A-Za-z0-9._-]`, the run id is checked with `IsRunId`, the destination id is `[a-z0-9-]`, and the check is a fixed code. Rows of other types are ignored. Report-once is keyed per (destination, run), so a newly corrupt copy is reported, the command still exits 1 on every pass, and `backup-status` keeps `replica_corrupt`, so nothing is hidden. A 2040 is matched to its own (destination, run).
  * Locks and side effects:
    * `ReplicaVerifyService` takes the shared restore lock (`pg_try_advisory_lock_shared`) and each destination's lock.
    * Neither it nor `VerifyStoredAsync` moves, deletes or writes anything at a destination.
    * `backup-status` issues one `SELECT` on the system audit table (not inside a READ ONLY transaction; see L3).
    * The 2042 detail carries codes only, with no path; the PostgreSQL test asserts this.
  * Restore: `A_company_restores_from_a_destination_copy_...` restores from the primary and from the copy. Both equal the source's catalog-and-content digest (public base tables). A bit flipped in the copy's dump is refused with a `BackupException` before anything is restored.
  * Mutations (local, reverted; the tree was verified clean after each):
    * Removing the `reverified=true` condition fails `The_audit_state_...`.
    * Removing the hash comparison in `VerifyStoredAsync` fails `A_good_copy_verifies_alone_...`.
    * Removing the hold and pin selection fails `The_default_selection_...`.
    * Removing the reported-corrupt exclusion in `ReplicaStatus.Read` fails the PostgreSQL test `A_bit_flipped_in_a_copy_is_reported_once_...`. No unit test catches it: the unit status test sets `ReportedCorruptRunIds` directly.
    * Writing 2042 on every pass (no report-once) fails the same PostgreSQL test.
  * Runs through the CPU gate:
    * Unit `ReplicaCopierTests` and `ReplicaStatusTests`: 30/30 at 6dce4b3 (29 tests plus my probe), and four mutation runs (29/30 each, except the exclusion mutation at 30/30, as stated).
    * Isolated PostgreSQL (`scripts/test-pg/run-isolated.sh`) `BackupReplicaVerifyTests`: 5/5 at 6dce4b3 (79 s including the gate wait), and two mutation runs at 4/5 each.
  * Registry: the title is 1390, and the sum is 1390 over 228 classes (1385 + `BackupReplicaVerifyTests` 5; the class has 5 `[PostgresFact]`). No protected file is in the diff (`CLAUDE.md`, `tools/verify/**`, `required-checks.json`, `secret-scan-allow.json`, `CODEOWNERS`, `verification.yml`). `ci-guard --base f365991` (main's copy) is clean.
  * Hosted CI on 6dce4b3, read by REST: backend-build-test, postgres-integration, ci-guard, secret-scan, dependency-scan, docs-traceability and guard-selftest all completed success. The PR is a Draft. GitHub reports it not mergeable against the current main (d003b1b); the author merges main after this record. The head commit is labelled `wip(...)`.
* **Findings:** 0 Critical, 0 High, 2 Medium (M1, M2), 3 Low (L1, L2, L3); details and resolutions follow.
  * **Medium M1 (a copy falsely reported verified):** `ReplicaCopier.VerifyStoredAsync` and `RetentionInventory.Inspect`. The run folder itself is never checked for being a link: `Inspect` checks only the entries inside it, and `Names` likewise. A destination run folder that is a symbolic link (to the primary's run, or anywhere that holds a genuine signed run) is re-verified as VERIFIED, audit 2040 `reverified=true` is written, and the command exits 0. Yet no independent copy exists, and `backup-status` calls the same folder `replica_corrupt`. Fix: refuse when the run directory is a link (`new DirectoryInfo(dir).LinkTarget is not null` gives check `link`, then 2042), before anything is read. Add a regression test with a linked run folder, run through the service so it covers 2042, the outcome and the exit code. Open.
  * **Medium M2 (default selection narrower than the approved plan):** `ReplicaVerifyService.Select`. The planned text (PHASE-4.3, "Re-verification of old copies") says that by default re-verification covers "the newest copy per company and the copies the retention hard keeps protect". The implementation omits the following:
    * company-scoped and date-scoped legal holds (D-134) and attachment-hold keeps (D-114);
    * the newest run with a passed recovery test per recovery key (D-133 `recovery_test_proof`);
    * the newest RECOVERABLE run per company, meaning a tested key and outcome `ok` (it picks the newest replicable instead).
    
    These are exactly the copies an operator would restore from or must keep as evidence, and bit rot in them is found only with `--all`. The answer to question 3 is therefore no: this is not acceptable as Low. It is cheap to fix now with a conservative superset that needs no company database:
    * every copy containing a company that has an unreleased company-scoped hold (dates ignored);
    * the newest `ProofValid` copy per `RecoveryKeyId`;
    * the newest copy per company among runs whose recovery key has a valid proof.
    
    Alternatively, the lead amends the planned text to the built selection; that is a lead decision, not mine. Open.
  * **Low L1 (no trust is reported as corruption):** `backup-replica-verify` falls back to `new SigningTrust([])` when the signing key directory is absent. Every copy is then selected (none verifies its status), and each gets a permanent 2042 `status` with the instruction to move the folder away. Fix: exit 3 before anything is written when no trusted signing key is available. Ideally also distinguish `signing_key_unknown` from `status`. Follow-up: in this PR.
  * **Low L2 (a compensated 2040 clears a report):** `ReplicaAuditReader.Build` ignores 2041. A 2040 compensated by 2041 `replica_publish_failed` (the rename failed after 2040, for example because the folder reappeared) still becomes the last word and clears a 2042 for a folder that may be the corrupt one. This needs concurrent write access at the destination, and the next re-verification writes 2042 again. Fix: treat a 2041 carrying `status=` as cancelling the preceding 2040 of that copy. Follow-up: in this PR or S8d.
  * **Low L3 (backup-status and the database):**
    * `backup-status` reads raw audit rows without verifying the chain, so an inserted 2040 would hide a 2042 until the next re-verification. That needs write access to the audit table, which `audit-verify` detects.
    * It now uses the administrative system connection, with no READ ONLY transaction.
    * The usage header in `Program.cs` still says `backup-status` is "READ-ONLY, no database".
    
    Fix: run the read in a `READ ONLY` transaction, update the usage header, and record the trust assumption (or use a least-privilege read role later). Follow-up: in this PR (header, read-only transaction) and the D-099 login work (role).
* **Blocker or High findings open:** none (M1 and M2 open; they must be fixed before merge)
* **NOT VERIFIED:** the following five items.
  * a run folder that is a bind mount or junction rather than a symbolic link (not detectable by `LinkTarget`; not constructible in the container);
  * hard-linked files shared with the primary;
  * Windows behaviour;
  * the full local suite (hosted CI on the exact head is the evidence);
  * the merge of current main into the branch (not yet done; it must be confirmed after the author merges).
* **Verdict:** request changes

Confirmation: Reviewer: Agent OSEC (independent security and database reviewer, separate agent; not the author); model family Claude Opus; prompt: full security review of PR 214 against base f365991 (false verification, backup-status audit parsing and report-once, default selection, locks and side effects, restore from a copy, mutations, registry, protected files); Head SHA reviewed 6dce4b3e7d1a39371b936489458f9bbfbe6a7af9; executed: full diff read, binding probes P1-P6 (P1 symlinked run folder re-verified as VERIFIED), five mutations (four caught by unit or PostgreSQL tests; the status exclusion only by PostgreSQL), unit 30/30 and isolated PostgreSQL 5/5 through the CPU gate, registry 1390 equal to the sum, no protected file, ci-guard clean, hosted CI success; verdict request changes; findings M1 (linked run folder verified), M2 (default selection narrower than the hard keeps of the plan), L1, L2, L3.
