# Independent review record - PR #182, import-file key coverage and the encryption-key retirement report (D-179..D-181): confirmation after rounds 2 to 4

* **Pull request:** #182
* **Head SHA reviewed (full 40 characters):** 5bfc43cd8ffded1bbc2b01cb30180ab9623695fd
* **Reviewer context:** Agent OBKP (independent reviewer, separate agent; not the author); model family Claude Opus. The authors are Agent B, continued by FB, then OBD (round 4). Earlier rounds: Agent R (record `PR-182-import-key-coverage-retirement-report.md`, request changes at 7b43859: H1, M1, L1, L2) and Agent FH (takeover review at 7be5e7a, request changes: M-FH1, M-FH2, L-FH1 to L-FH3; re-review at afa7d98, request changes: M-FH3; FH's record was never pushed). I replaced FH and reviewed afa7d98 (request changes: M-FH3 confirmed, L-OBKP1 to L-OBKP3), 5cf8bb5 (request changes: new M-OBKP4) and this head. Prompt (summary): the lead asked for a FULL-diff review against `main`, focused on each fix delta. The points to attack: whether the union-of-present-runs inventory can miss a run (nested or oddly named folders, symbolic links, hidden entries, `.retention`, tombstones, look-alike names, entries swapped between listings, hostile names in output and JSON); whether held or pinned runs are still counted; whether the attestation wording is honest; whether the report is strictly read-only; whether the merges bring only `main` plus the stated documents; the registry sum; decision-ID collisions; mutations and isolated PostgreSQL runs. The owner constraints: never report a key retirable when the tool cannot know (D-082); no retirement or deletion capability; 4.3-ACT, D-137, D-227 and D-230 untouched.
* **Scope read:** the full diff `644bc7f..5bfc43c` (the `main` merged last; 21 files, +1494 -30): all source (`EncryptionKeyRetirementService`, `EncryptionKeyRetirement`, `Hasm.Admin` command and usage text, `AttachmentService`/`EncryptedFileStore` `ReadWithoutCreating` and `KeyUsageAsync`, `ImportService` rewrap outcome, `ImportEndpoints`, `FileKekRing.ListKeyIds`), the four test files, the workflow registry, `DECISIONS.md` D-179..D-181, `DEVELOPMENT.md`, `PHASE-4.3.md`, `PHASE-3.12.md`, `INTEGRATION-STATUS.md`. The fix deltas fce1d6b, 73c8d97 and dd0e5fc in detail. Every merge (e2e5cbd, afa7d98, 1c50477, a222903, 5cf8bb5, 5bfc43c) by `git show --remerge-diff`. Cross-read on `main`: `RetentionInventory.Scan`/`Read`/`Inspect`, `BackupRun` (verdict, run status key ids, run-id pattern), `CompanyBackupService` failure cleanup, the slice 6 `WriteTombstone`/`WriteCompleted`, `ProvisioningState` transitions.
* **Checks run (commands and results):**
  * Head confirmed by REST: PR 182 open, head 5bfc43c, mergeable true. Hosted check-runs on 5bfc43c: backend-build-test, postgres-integration, ci-guard, guard-selftest, secret-scan, dependency-scan, docs-traceability all success.
  * Unit `EncryptionKeyRetirement*|RetentionClassifier*` at the head: 35/35 passed.
  * `scripts/test-pg/run-isolated.sh "$PWD" --build --filter "FullyQualifiedName~Import|FullyQualifiedName~Attachment|FullyQualifiedName~Backup|FullyQualifiedName~CompanyBackup"`: 149 executed, 149 passed. That is the head's 148 plus one throwaway probe test of mine, reverted afterwards with the tree verified clean. The probe ran a REAL slice 6 deletion through the test seam (four tombstones written by `WriteTombstone`/`WriteCompleted`): the report is complete over that layout.
  * Each of these blocks with a named code: a malformed `.tmp-` leftover; a leftover of a non-tombstone name; deep nesting inside a tombstone; a tombstone folder that is a symbolic link; `tombstones` itself a symbolic link; a file link inside a tombstone; a run nested in another run folder (`backup_run_unrecognised_content`); `.retention/archive/<run>`; a run hidden inside a real tombstone; a stray hostile file in `.retention` (percent-encoded); a stray file in a run folder.
  * Round 3 at 5cf8bb5 (same classes, 149/149 with a probe): every root-level case blocks. That covers `archive/<id>`, `<id>.bak`, `.old`, `run-<id>`, an upper-case id, a `.tar` file, a `.retention` or `.deleting-*` that is a symbolic link or a file, a run-id name that is a symbolic link, and look-alike names (fullwidth digit, Cyrillic letter, zero-width space). Hostile names (comma, colon, BEL, CR, LF, `%`, U+202E) are percent-encoded in stdout and JSON, and the cap is 20 names then `+N_more`.
  * Round 2 at afa7d98: a held and pinned run is counted (`needed_by_present_backups`).
  * Mutations, each local, reverted and the tree verified clean. At the head:
    * the `Clean` check dropped: unit 1 of 35 fail, PostgreSQL `BackupRetentionTests` 1 of 11 fail (the M-FH3 and M-OBKP4 test);
    * the `.retention` walk dropped: unit 1 of 35 fail, PostgreSQL `BackupRetentionTests` 1 of 11 fail (the same test).
  * Mutations at 5cf8bb5: the root-entry check removed fails unit 1 of 34 and PostgreSQL 1 of 11; my L-OBKP1 mutation (the union restricted to the runs that prove the root) fails unit 1 of 34; FB's round-2 mutation (plan inventory restored, two checks removed) fails unit 1 of 34.
  * Mutations at afa7d98: FB's mutation fails unit 1 and PostgreSQL 1.
  * Registry: step title 1297 = `main` (644bc7f) 1287 + 10; the per-class sum is 1297 (ImportServiceTests 11 to 15, AttachmentTests 10 to 12, BackupRetentionTests 7 to 11), equal to the attribute counts in the files.
  * Merges: the remerge-diffs contain only `main` and the stated documents and registry resolutions. 5bfc43c resolves only the registry title and the PR's own allocation row. The PR's diff against the merged `main` is exactly its 21 files.
  * D-179..D-181 are not on `main` and `DECISIONS.md` has no duplicate ID.
  * Hygiene: no closing keyword in commit messages; no version-qualified model identifier in added lines; only `.github/workflows/backend.yml` under `.github/`; key ids and codes only in output, JSON and tests.
* **Findings:** no Critical or High open; all findings of rounds 2 to 4 are resolved; details in the table below.
* **Blocker or High findings open:** none
* **NOT VERIFIED:** the items below.
  * Windows: junctions, hidden or system attributes with `AttributesToSkip=0`, and `Uri.EscapeDataString` on an unpaired surrogate name (Linux only).
  * A symbolic link in a PARENT of a root.
  * A root swapped by rename between the before- and after-scan checks (path-based checks; the future retirement operation must re-check under a lock, as documented).
  * Content at ALLOWED paths of a run that the signed status does not list, for example another run's company folder merged into a run by hand. The report trusts the signature, as D-138 does.
  * The report against an unreachable system database (code read only).
  * `verify-head.js` (not run; reviewer role).
  * The frontend (no screen consumes the rewrap response).
  * The later merge-only commit bringing `main` eda24e0.
* **Verdict:** approve

## Findings

| # | Sev | Round | Finding | Disposition |
|---|-----|-------|---------|-------------|
| M-FH3 | Medium | afa7d98 (FH; confirmed by OBKP) | The report read only folders named by a run id; a run moved or copied inside a root under another name was invisible and its key printed retirable. | Fixed in 73c8d97: every root entry other than a real run-id, `.deleting-*` or `.retention` directory blocks (`backup_root_unrecognised_entry`), listed before and after the scan. Verified by probe and mutation. |
| L-OBKP1 | Low | afa7d98 | No test killed a key union restricted to the runs that prove the root (a `failed`-verdict run with written company sets). | Fixed in 73c8d97 (unit case); mutation now fails 1 of 34. |
| L-OBKP2 | Low | afa7d98 | Stale "retained run" wording in the usage comment and the service summary. | Fixed in 73c8d97. |
| L-OBKP3 | Low | afa7d98 | Handoff row out of date. | Fixed in 1c50477. |
| M-OBKP4 | Medium | 5cf8bb5 | The same blind spot one level down: a run inside another run folder (the scan's `Clean` ignored) or inside `.retention` (never inspected) was invisible; reproduced, report complete and the key retirable. | Fixed in dd0e5fc: a non-clean run blocks (`backup_run_unrecognised_content`); `.retention` is walked and accepts only slice 6 tombstones (four file names, their `.tmp-<32 hex>` leftovers, real folders and files only). Verified against a real slice 6 deletion, the attacks above, and both mutations on unit and PostgreSQL. |
| Obs | - | 5cf8bb5 | `main`'s run-id and deleting-folder patterns end in `$`, which also matches before a final newline. Not a blind spot: such a folder is read as a run, does not verify, and blocks. | Recorded by the lead as a follow-up for a later slice touching `BackupRun.cs`. |

Owner constraints: nothing is reported retirable when the tool cannot know (D-082); no retirement or deletion capability is added; automatic backup deletion (4.3-ACT, D-137, D-227) stays closed; D-230 is unaffected.
