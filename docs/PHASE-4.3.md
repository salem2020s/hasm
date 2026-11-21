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

* Review of PR #78 (fixed): the drill proves isolation by a catalog-and-content digest (tables, exact row counts, row hashes) of the source, another company database and the system database before and after; a restore whose half-created target cannot be dropped reports `restore_failed_cleanup_failed`; the restore path must be absolute; a creation race maps to `target_exists`; the target is created from `template0` with the source encoding; tool output goes to a null stream; the child process gets only `PATH`, `SystemRoot` and `PG*` variables; a cyclic symbolic link is a BackupException; CI asserts that `pg_dump`/`pg_restore` have the server's major version (docs/CI-RUNNER.md notes the client-tool requirement).

* Review of PR #78, round 2 (fixed): the child environment is built by a pure, unit-tested method; in verify-ca/verify-full mode without a configured certificate `PGSSLROOTCERT=system` is set (libpq 16+ only; without HOME the tools could not otherwise validate TLS) and a missing certificate makes the tools fail; `PGCONNECT_TIMEOUT` is set; libc collation/ctype of the source are copied (validated) and the drill compares encoding and locale; a failed cleanup keeps the original cause (`BackupException.Cause`); the SQLSTATE mapping and the cyclic-link refusal are tested.

* Review of PR #78, round 3 (fixed): PostgreSQL 14+ is supported and the tool fails closed. The locale provider is read through `to_jsonb(d)->>'datlocprovider'` (the column exists only from 15; absent means libc); `PGSSLROOTCERT=system` is used only when the tool's client major version (read from `--version` before the tool runs) is at least 16; for an older or unknown client in verify-ca/verify-full mode without a configured certificate the call is refused before any tool is launched with `ssl_root_certificate_required`; an ICU/builtin locale provider, a missing locale or an unexpected locale name is refused with `locale_unsupported` instead of silently using the template0 default (libc names with spaces, such as Windows locales, are reproduced); `UnauthorizedAccessException` while resolving a path is `path_unresolvable`.
* **Decision (standing authorization of the lead, 2026-11-21): keep PostgreSQL 14+ support, fail closed.** Rationale: HASM documents 14+ as supported, and a backup tool that only worked on 16+ would silently shrink that promise. Alternatives rejected: (a) raise the supported minimum to 16 (a product decision outside this slice, and it would strand deployments on 14/15); (b) always set `PGSSLROOTCERT=system` (breaks libpq < 16); (c) fall back to a weaker `sslmode` when no certificate is configured (a TLS downgrade, never acceptable). Operators on 14/15 with verify-ca/verify-full configure a certificate path (`Hasm:Database:SslRootCertificate`).
* Vendor PostgreSQL installs that need `LD_LIBRARY_PATH` are not supported by the cleared child environment: use distribution packages or binaries with an embedded library path (rpath), and point the tool at them with its binary directory. No new configuration option was added.

## Recorded for the orchestration slice
* The wiring of the built environment onto the started process and real-tool TLS are still not exercised by any test (the environment builder is unit-tested; the drill runs on a non-TLS disposable server).
* Process-launch paths (TLS environment as seen by a real tool, timeout, kill) are not exercised by a test; the self-hosted workflow has no client-version assertion (owner's call).
* `PgBackupTool` validates NAMES only. The orchestrating slice must tie `sourceDatabase` to a real database of the company registry and the dump to the manifest's company before calling it.
* A blocked drop of a failed restore target (the `restore_failed_cleanup_failed` path) is not exercised by a test (no robust way to block a drop on the shared disposable server).

## Limitations (recorded)
* The manifest detects corruption and loss; it is not a signature and the set is not encrypted (later slice).
* `KeyIdsRequired` covers attachment envelopes only. Encrypted import-file originals are outside the attachment folder and need their own key coverage (the import-file rewrap retirability report, decision K follow-up, is not built yet).
* Restore into a NEW database only; replacing a live company database (cutover), full-installation restore, scheduling, retention, destinations, set encryption, monitoring and RPO/RTO measurement are not built.

## BLOCKED / owner decisions
* Audited restore acknowledgement and a new checkpoint epoch after a company restore (`docs/DEVELOPMENT.md`, restore limitations): policy needs an owner decision.
* Retention/purge of removed attachments (decision R).
* Scheduling, retention, destinations, encryption, monitoring, RPO/RTO: not started.
