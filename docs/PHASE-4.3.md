# Phase 4.3 - Backup and restore (in progress)

Sources: SRS 14.4, 16.1, 16.2, 16.4; Scenarios 21.8, 21.10; PHASE-3.12 (backup coordination, decision K).

## Slice 1 (this change): coordinated company backup set
`Hasm.Infrastructure.Backup.BackupSet`: a set directory with `database.dump`, `attachments/<32 hex>.att` (active AND removed attachments) and `manifest.json` (written last). The manifest has the company id, hashes and sizes, the master key ids the sensitive attachment envelopes need (never key material) and a count of skipped stray files.

* `Verify` names missing, altered and unlisted files by generated name only.
* `RestoreAttachments(set, target, expectedCompanyId)` verifies the whole set, refuses a set of another company (`company_mismatch`), writes only to an empty target outside the set (staged, re-hashed, then moved), never merges or touches other company folders.
* `CheckKeys` / `ListKeyIds` compare the needed key ids with a restored key directory (names only). Decision K: a key a retained attachment needs must be in the key backup.
* Symbolic links are refused; overlap checks use resolved paths; a failed create removes the partial set.

## Slice 2: database half and hardening
* `PgBackupTool` (Hasm.Infrastructure.Backup): `DumpAsync` (custom-format pg_dump of one company database, `--no-owner --no-privileges`) and `RestoreIntoNewDatabaseAsync` (creates a NEW database, pg_restore into it, drops it again when the restore fails). Refuses an existing target, the source, the system/maintenance/built-in databases. Admin credential only, passed in the child environment (never the command line); tool output is never copied into errors. The restored copy does not carry the source roles; the runtime role is re-applied by `grant-company-runtime` before any cutover (cutover itself is not built).
* Drill (`BackupRestoreDrillTests`, disposable PostgreSQL only): dump, set, verify, restore database and attachments to new targets; another company database proven unchanged; refusal cases; failed restore leaves no database. Needs the PostgreSQL client tools on PATH.
* Hardening of slice 1: optional-parameter seam instead of a static hook; header-plus-size envelope classification (no whole-file read); `RestoreResult` and `BackupVerification.NonEnvelopeFilesWithMagic` surface the counter; manifest without the counter still reads; `RealPath` repeats until stable.

## Limitations (recorded)
* The manifest detects corruption and loss; it is not a signature and the set is not encrypted (later slice).
* `KeyIdsRequired` covers attachment envelopes only. Encrypted import-file originals are outside the attachment folder and need their own key coverage (the import-file rewrap retirability report, decision K follow-up, is not built yet).
* Restore into a NEW database only; replacing a live company database (cutover), full-installation restore, scheduling, retention, destinations, set encryption, monitoring and RPO/RTO measurement are not built.

## BLOCKED / owner decisions
* Audited restore acknowledgement and a new checkpoint epoch after a company restore (`docs/DEVELOPMENT.md`, restore limitations): policy needs an owner decision.
* Retention/purge of removed attachments (decision R).
* Scheduling, retention, destinations, encryption, monitoring, RPO/RTO: not started.
