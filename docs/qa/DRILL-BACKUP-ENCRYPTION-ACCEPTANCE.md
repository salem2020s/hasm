# Drill log: backup encryption and authenticated manifests (owner item 11, D-088; with D-085)

Status: recorded drill (evidence for `docs/RELEASE1-ACCEPTANCE.md` section G). This is not an approval. It records what was run, on what, and what it proved. It also states exactly what remains before release acceptance can be marked met for owner item 11.

## Run
* **Date:** 2026-11-21.
* **Tree:** `main` at `ff0fcca`, plus the branch `test/backup-acceptance-evidence` (this test file only; no production code, no migration).
* **Runner:** `scripts/test-pg/run-isolated.sh <worktree> --build --filter "FullyQualifiedName~BackupAcceptanceEvidenceTests"`.
  * It uses a private network namespace and a private disposable PostgreSQL 16 cluster (`/tmp/hasm-pgw-…`, port 5433 inside the namespace only, created and destroyed by the runner).
  * It never uses the development cluster or the shared harness.
* **Result:** `total=5 executed=5 passed=5 failed=0 skipped=0`. Per test, in seconds: 4.4 (round trip), 8.1 (tampering), 7.3 (status and downgrade), 6.4 (rotation), 4.1 (real backup and restore).
* **Hosted CI:** the PR's `postgres-integration` job runs the same class with the exact-count registry and is the merge arbiter.

## What was proven (class `BackupAcceptanceEvidenceTests`)
| D-088 / D-085 criterion | Test | Result |
|---|---|---|
| A real encrypted backup is unattended (public recovery key and signing key only on the server) and holds no plaintext (no `PGDMP`, no row text, no attachment bytes) | `A_real_encrypted_backup_holds_no_plaintext_and_restores_into_a_trial_database_with_the_recovery_key_equal_to_the_source` | passed |
| Without the offline recovery private key nothing restores (`recovery_key_required`) | same | passed |
| With the key, the set restores into a disposable trial database whose catalog-and-content digest equals the source, and the attachment comes back byte for byte | same | passed |
| The run status is signed and verifies with the server's public signing key; the key id is recorded | same | passed |
| Wrong recovery key, refused before pg_restore: `recovery_key_mismatch` | `Every_tampering_of_an_encrypted_set_is_refused_before_anything_reaches_pg_restore` | passed |
| One flipped ciphertext byte: `backup_set_not_clean` | same | passed |
| A truncated set (dump cut short, dump removed): `backup_set_not_clean` | same | passed |
| An edited manifest: `signature_invalid` | same | passed |
| A removed manifest signature: `signature_missing` | same | passed |
| A downgrade to an unencrypted version-1 set: `unencrypted_set_refused` | same | passed |
| Every refusal creates no database and no target, and the untouched set still restores | same | passed |
| An unsigned run status and an edited run status (one byte) count as `status_unsigned`, i.e. a FAILED run | `An_unsigned_or_edited_run_status_and_a_downgraded_run_never_count_as_a_good_backup` | passed |
| A genuine run is `recovery_untested` until a recovery test passes (D-085) | same | passed |
| A downgraded run (a version-1 set planted in an encrypted run) fails the recovery test, restores nothing of that company and never turns the status green | same | passed |
| Recovery key rotation: the old set restores with the OLD private key and the new set with the NEW one; key ids are bound, so each refuses the other (`recovery_key_mismatch`) | `Rotated_recovery_and_signing_keys_still_restore_what_they_protected_and_a_retired_key_signs_nothing_new` | passed |
| Signing key rotation: a set signed before the old key's retirement still verifies; a set signed with it after retirement is refused (`signing_key_retired`) | same | passed |
| Key export round trip after rotating the attachment (k1 → k2) and audit (a1 → a2) rings: readable only with the offline private key (another key gives `recovery_key_mismatch`); every ring comes back byte for byte; no private key material in the output; one `KeysExported` audit event | `The_key_export_round_trips_every_ring_including_rotated_keys_and_recovers_lost_key_directories` | passed |

This suite consolidates, end to end on PostgreSQL, what the slice 5 suites (PR #128) prove piecewise: `Hbe1Tests`, `EncryptedSetsTests`, `SignedDocumentsTests`, `KeyExportTests`, `RecoveryKitTests` (unit) and `EncryptedBackupTests` (PostgreSQL). Those stay in force unchanged.

## What remains before owner item 11 can be marked met in release acceptance
1. **The production recovery test.** Per D-085, encryption is not production-ready until a recovery test succeeds on the production installation with its real offline kit (`backup-recovery-test`, two offline copies of the private key). That is an operator drill per installation, not a CI test. It is recorded when it is performed.
2. **The recorded limitations against G** (D-118, D-099) stay open until the Phase 4.1 approval framework and the administrative-tool login exist or the owner accepts them:
   * trial restore and the recovery test without independent approval;
   * the key commands without a named MFA administrator.
3. **Real-tool TLS** to the database during backup and restore is not exercised by any test (the disposable server is non-TLS). This is recorded in PHASE-4.3.
4. **Full-installation restore** (the system set) and the live cutover are later slices (slice 7 and beyond). This drill covers the trial-restore path only.

Until 1 and 2 are closed or explicitly accepted, owner item 11 / D-088 stays NOT MET for release acceptance. The automated part of the criterion ("completed and tested") is covered by this suite together with the slice 5 suites.
