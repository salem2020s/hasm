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

* Review of PR #78, round 3 (fixed): PostgreSQL 14+ is supported and the tool fails closed. The locale provider is read through `to_jsonb(d)->>'datlocprovider'` (the column exists only from 15; absent means libc); `PGSSLROOTCERT=system` is used only when the tool's client major version (read from `--version` before the tool runs) is at least 16; for an older or unknown client in verify-ca/verify-full mode without a configured certificate the call is refused with `ssl_root_certificate_required` (wording corrected in round 4: the refusal follows the `--version` probe, which does launch the binary, and precedes any connection or CREATE DATABASE); an ICU/builtin locale provider, a missing locale or an unexpected locale name is refused with `locale_unsupported` instead of silently using the template0 default (libc names with spaces, such as Windows locales, are reproduced); `UnauthorizedAccessException` while resolving a path is `path_unresolvable`.
* **Decision (standing authorization of the lead, 2026-11-21): keep PostgreSQL 14+ support, fail closed.** Rationale: HASM documents 14+ as supported, and a backup tool that only worked on 16+ would silently shrink that promise. Alternatives rejected: (a) raise the supported minimum to 16 (a product decision outside this slice, and it would strand deployments on 14/15); (b) always set `PGSSLROOTCERT=system` (breaks libpq < 16); (c) fall back to a weaker `sslmode` when no certificate is configured (a TLS downgrade, never acceptable). Operators on 14/15 with verify-ca/verify-full configure a certificate path (`Hasm:Database:SslRootCertificate`).
* Review of PR #78, round 4 (fixed): on PostgreSQL 15+ the CREATE DATABASE clause pins `LOCALE_PROVIDER libc` (template0 may be ICU/builtin there; PostgreSQL 14 has no keyword and only libc). The keyword is chosen by the TARGET server's major version, not by whether the source provider is known: a source captured on 14 (no provider column, libc by definition) restored onto 15+ must be pinned as well. The drill also compares the provider. The client-version probe and the child environment are prepared BEFORE CREATE DATABASE, so `ssl_root_certificate_required` never costs a create/drop round trip. Every tool process (probe and run) gets a fixed working directory (`/`, or the Windows system directory), never the inherited service directory. The probe cache is a `ConcurrentDictionary`. The locale-name rule uses `\A...\z` (a trailing newline is refused) and allows `(` and `)` (Windows names such as `Chinese (Simplified)_China.936`; safe inside the quoted literal). Process wiring is now tested with fake POSIX tools (`PgBackupToolProcessTests`): the environment and working directory a started tool sees, no credential on its command line, the probe, its cache, the refusal order before any database contact, the exit-code mapping and the timeout kill.
* **Decision D-053 (standing authorization D-048, lead, 2026-11-21): the source database locale is captured at dump time and carried by the backup set.** `PgBackupTool.DumpAsync` reads encoding, LC_COLLATE, LC_CTYPE and locale provider of the source (before the dump; a missing source is `dump_failed`) and returns them; `BackupSet.Create` stores them in the manifest as an optional `databaseLocale` record (manifest version unchanged; an older manifest without it reads with null; a malformed stored record invalidates the manifest, `invalid_database_locale` at creation). `RestoreIntoNewDatabaseAsync` uses the captured values; only when they are absent does it read the live source, and when that does not exist either it refuses with `source_locale_unknown` before anything is created. Rationale: a restore is a disaster-recovery operation and must not depend on the source database still existing; the manifest already is the validated description of the set, so this adds one optional record and no new file. Alternatives rejected: (a) read the locale from the dump's TOC (the custom-format archive does carry the source's CREATE DATABASE entry, but using it means extracting and parsing SQL text produced by the tool, which this class deliberately never reads or keeps, and its correctness would depend on the pg_dump version's statement format); (b) refuse every restore whose source is gone (defeats disaster recovery); (c) fall back silently to template0 defaults (a silent collation change, never acceptable).
* The client major version is read from the tool's `--version`, which reports the tool's own version, not the libpq it is linked against. Distribution packages ship matching versions; a tool linked against an older libpq with `PGSSLROOTCERT=system` fails to connect (fail closed, never a downgrade).
* Vendor PostgreSQL installs that need `LD_LIBRARY_PATH` are not supported by the cleared child environment: use distribution packages or binaries with an embedded library path (rpath), and point the tool at them with its binary directory. No new configuration option was added.

## Recorded for the orchestration slice
* Real-tool TLS is not exercised by any test (the drill runs on a non-TLS disposable server); the environment a started tool sees, the timeout and the kill are exercised with fake POSIX tools only (the tests pass without running on Windows: an early return, not a reported skip). The PostgreSQL 14/15 server paths (no provider column; `LOCALE_PROVIDER` on 15) are unit-tested on the clause only and NOT VERIFIED against a real 14/15 server (CI runs 16). The self-hosted workflow has no client-version assertion (owner's call).
* The orchestrating slice must pass the manifest's `databaseLocale` to `RestoreIntoNewDatabaseAsync`.
* `PgBackupTool` validates NAMES only. The orchestrating slice must tie `sourceDatabase` to a real database of the company registry and the dump to the manifest's company before calling it.
* A blocked drop of a failed restore target (the `restore_failed_cleanup_failed` path) is not exercised by a test (no robust way to block a drop on the shared disposable server).

## Slice 3: orchestration (plan approved in PR #91; implemented on `feature/phase-4-3-orchestration-impl`)

### Implementation status (code PR)
* **Built as planned:**
  * `CompanyBackupService` (`BackupCompanyAsync`, `TrialRestoreAsync`, `DropTrialRestoreAsync`);
  * the three `Hasm.Admin` commands;
  * `FakeTools` (unit tests);
  * the optional manifest field `companyMigrationVersion`;
  * the operator section "Company backup and trial restore" in `docs/DEVELOPMENT.md`.
* **Review round 2 Lows, implemented:**
  * **R2-L1:** `drop-trial-restore` locks the registry table in SHARE mode from the registry check until the DROP has completed.
  * **R2-L2:** the company's provisioning advisory lock is held for the whole backup (`company_busy`, exit code 4, when it is taken), and the migration level is re-read after the dump; a change is `migration_level_changed`.
  * **R2-L3:** registry links whose local party is absent, or has no global id, in the restored copy are a warning, `shared_links_missing_locally`.
* **Deviations from the plan text (no change of decision):**
  * Check (a) reads this company's links with one read-only query on the registry tables instead of `PartyRegistryStore.GetLinkedIdentitiesAsync`. The store needs an audit trail to construct, and the query also yields the reverse direction required by R2-L3.
  * PostgreSQL test 7 creates the link inconsistencies before the backup instead of after it. This is equivalent for the check, which compares the restored copy with the registry at restore time.
  * Additional codes:
    * `target_not_empty`: checked before the restore;
    * `company_busy`;
    * `migration_level_changed`;
    * `restored_locale_mismatch`;
    * `trial_drop_failed`: the trial database could not be dropped; it is then recorded in a kept-trial file instead of being left unnamed.
  * **`PosixFactAttribute` was NOT built.** A reported skip (`Skip = "..."` on Windows) is refused by the protected `ci-guard` ("adds skipped, ignored or focused tests"). That gate is not worked around (no evasive spelling) and its tooling is not changed here. The fake-tool tests therefore keep the early return: on Windows they pass WITHOUT running, which is documented in the tests. A reported Windows skip needs an explicit ci-guard allowance, which is the owner's decision on protected tooling. Everything else of plan item 3 is built: the exec-capable folder under the test output, all tools written before any process starts, and the retry only on ETXTBSY.
* **Review of PR #94, round 1 (Agent C, request changes; one Medium):**
  * **M1, fixed.** The staging dump was created with the process umask (typically 0644) in an operator-chosen parent, so the full company dump was readable by other local users during the dump, the copy or after a crash. Now:
    * the dump is written into a FRESH folder `<dest>.staging-<32 hex>/`, created owner-only (0700, verified on Linux; refused with `staging_exists` if it already exists, `staging_not_private` if group/other bits remain);
    * the folder is removed afterwards;
    * stale folders of exactly that pattern (never a symbolic link or a file) are swept after 24 hours, replacing the earlier file sweep;
    * unit tests assert the 0700 mode and the sweep rules.
    * On Windows the folder inherits the parent's ACL (recorded; the operator chooses a private destination parent).
  * **L1, fixed:** the trial attachment target is also checked against the key directory (`target_overlaps_keys`), mirroring the backup destination check.
  * **L2, fixed:** `drop-trial-restore` sets `lock_timeout = 5s` for the SHARE lock of the registry table and maps a timeout to `company_busy`.
  * **L3, recorded:** the existing per-company advisory key uses the first 4 bytes of the company id, so two companies can collide. A collision only makes an administrative command report busy; it never permits concurrent work on one company.
  * **L4, fixed:** the docs say the fake-tool tests pass without running on Windows.
  * **Pre-existing flaky test, made robust without weakening it:** `CompanyRuntimeGrantGuardTests.A_company_being_worked_on_elsewhere_is_reported_and_not_changed` takes its lock with a short bounded retry, because the server releases the lock of the just-closed provisioning session asynchronously. Every assertion is unchanged.
  * `CompanyBackupServiceTests` runs in a non-parallel xunit collection. The trial databases that `Hasm.Admin` names opaquely would otherwise make the cluster-identity gate of a concurrently starting test refuse the cluster.
* Local verification and CI results: see the PR description.

### Plan (as approved)
Goal: one company is backed up and trial-restored END TO END from the company registry, through administrative commands, using the slice 1 set and the slice 2 tool. The trial restore includes the read-only checks SRS 16.2 / Scenario 21.8 require: shared party links, attachment rows against files, and migration compatibility. Risk: Critical (ROADMAP 4.3).
* **Migrations: none.** The registry and `Hasm_System` are only read, and nothing is added to any schema. If implementation shows a migration is needed, work stops and the lead allocates it.
* **No API endpoint and no UI in this slice.** The shared Web/Tauri frontend is unaffected; a backup screen belongs to the monitoring/scheduling slice.
* **Scenario 21.8 is NOT fully met by this slice.** It still lacks the cutover (replacing the live company database and attachment folder, re-applying the runtime grants, bringing registry and company to consistent points) and the audited restore acknowledgement with a new checkpoint epoch. Both are BLOCKED on an owner decision (see below). Slice 3 must never be cited as meeting 21.8; it delivers the backup, the trial restore and the pre-cutover checks only.

### Design
1. **`CompanyBackupService`** (`Hasm.Infrastructure.Backup`). Options: `HasmDatabaseOptions`, `AttachmentOptions`, `EncryptionOptions`, a clock. It wraps `PgBackupTool`, `BackupSet`, `CompanyRegistry`, `PartyRegistryStore` (read-only) and the embedded company migrations.
   * **`BackupCompanyAsync(companyId, destination)`:**
     1. The company is read from the registry (`CompanyRegistry.GetAsync`). An unknown company is `company_not_found`; any state other than `Ready` is `company_not_ready`. The source database is ALWAYS the registry's `PhysicalDatabaseName`; no caller ever names a database.
     2. `Hasm:Attachments:StorageDirectory` must be configured, otherwise `attachment_storage_not_configured`. This fails closed and has no override: a backup run with a missing setting must never silently produce a set without the attachments (documented for operators, see "Operator documentation"). The company folder is `<root>/<company id N>`, exactly as `AttachmentFileStore` builds it; a missing folder means no attachments yet.
     3. The destination is checked before anything runs: absolute, empty or absent, and outside the attachment root and the key directory (`destination_not_empty`, `destination_overlaps_source`).
     4. **Stale staging files are swept first** (implemented as staging FOLDERS `<leaf>.staging-<32 hex>`: see review M1 above). The pattern is exactly `<destination leaf>.dump-<32 lowercase hex>` in the destination's parent directory, last written more than 24 hours ago (well above the 30-minute tool timeout). Nothing else is ever deleted, and the count is reported.
     5. **Migration level.** The company migration level (highest version in the source's migration journal) is read with the admin account and stored in the manifest as the optional `companyMigrationVersion`. Older manifests still read: the field is null.
     6. **Dump.** `PgBackupTool.DumpAsync` writes the dump to a private staging file `<destination>.dump-<random>` (owner-only, removed on success and on failure) and captures the locale.
     7. **Restorability at backup time.** The captured locale must pass the restore rules (`PgBackupTool.EnsureRestorable`: libc provider and the character rule). Otherwise the result is `locale_unsupported` and NO set is written: an ICU/builtin company is refused up front instead of producing a set that can never be restored.
     8. **Create and verify the set.** `BackupSet.Create(companyId, staging dump, company folder, destination, now, locale, migration level)`, then `BackupSet.Verify(destination).IsClean` as a post-condition; if it fails, the result is `backup_set_not_clean` and the set is removed.
     9. **Key coverage.** `BackupSet.CheckKeys(manifest, ListKeyIds(Encryption:KeyDirectory))`. Missing key ids are REPORTED (result and exit code 8), not a refusal.
   * **Why taking the dump before copying the attachment folder is safe:**
     * Attachment files are written BEFORE their row commits (`AttachmentService`), and removed attachments keep their bytes, so every file referenced by a row in the dump is present when the folder is copied.
     * This argument depends on decision R staying open (no purge of removed bytes). A future purge must coordinate with a running backup, for example by excluding or waiting for backups in progress; this is recorded for decision R.
     * A file uploaded after the dump is an unreferenced extra in the set. It is reported by the trial restore and is harmless; the orphan sweep removes it after a restore.
     * After a rewrap, the row's stored key id can be STALE: the dump may hold the old key id while the copied envelope names the new key. Decryption always uses the key id in the envelope, and the manifest takes its key ids from the envelopes. The trial restore reports row/envelope key-id differences as warnings.
   * **`TrialRestoreAsync(setDirectory, expectedCompanyId, attachmentTarget, keep)`:**
     1. **Set and company.** The set is verified (`backup_set_not_clean`). Its company must equal `expectedCompanyId` (`company_mismatch`) and must exist in the registry (`company_not_found`).
     2. **Compatibility before anything is created (check c).** A manifest `companyMigrationVersion` newer than the highest embedded company migration of this installation is refused with `set_newer_than_installation`; an older one is reported as a warning (`migration_level_older`: a cutover would first need `migrate-companies`). An older set without the field is checked after the restore instead.
     3. **Locale.**
        * The manifest locale is ALWAYS validated by the restore rules.
        * When the registry's database still exists, the manifest locale must equal the live one, otherwise `locale_mismatch`. A database's locale cannot change after creation, so a difference means a tampered or foreign set.
        * In that comparison a provider of null (PostgreSQL 14, which has no provider column) and `c` are the SAME libc provider, so an upgrade from 14 to 15+ never raises a false `locale_mismatch`. Encoding, collation and ctype must match exactly.
        * When the database is gone, the manifest locale is used alone. An older set without it falls back to the live database, and with neither the restore is refused (`source_locale_unknown`, slice 2).
     4. **Target database.** A NEW generated physical name (`PhysicalIdentifier.GenerateDatabaseName`), never registered and never the registry's name. `PgBackupTool.RestoreIntoNewDatabaseAsync(set dump, target, registry name, manifest locale)` restores into it, after which the database is tagged with `COMMENT ON DATABASE <target> IS 'hasm-trial-restore:<random 32 hex token>'`.
     5. **Attachments.** `BackupSet.RestoreAttachments(set, attachmentTarget, companyId)`. The target must be absolute, empty or absent, and OUTSIDE the live attachment root (`target_in_live_storage`): a trial restore never writes into live storage.
     6. **Read-only checks of the restored copy** (they write nothing anywhere except the trial database's comment):
        * **Locale and migrations (check c, continued).** The database opens with the admin account, and its encoding, collation, ctype and provider equal the manifest (null = `c`).
          * Its migration journal must be readable.
          * Its highest version must equal the manifest's `companyMigrationVersion` when present, otherwise `migration_level_mismatch`.
          * For an older set without the field, a level newer than this installation is `set_newer_than_installation`.
          * Each journal row's checksum must equal the checksum of the embedded script with that version, otherwise `migration_checksum_mismatch` (a set from a diverged build).
        * **Shared party links (check a).** Every restored local party with a `GlobalPartyId` must resolve in `Hasm_System`, read only: `PartyRegistryStore.GetLinkedIdentitiesAsync` must return a link for (company, local party) to the SAME global party. Unresolved or different links are a failure, `shared_links_unresolved`, with the count only (never party data).
        * **Attachment rows against files (check b).** Every attachment row, active AND removed, is checked:
          * its file must exist in the restored folder (`attachment_file_missing`);
          * a non-sensitive file's SHA-256 must equal the row's hash (`attachment_hash_mismatch`);
          * a sensitive envelope is decrypted with the configured key ring and the row's context, and the plaintext's SHA-256 must equal the row's hash; a decryption failure with an available key is `attachment_undecryptable`;
          * an envelope whose key is missing from the key directory is reported as missing key coverage (warning, exit code 8; consistent with D-061);
          * a row key id that differs from the envelope's key id is reported as a warning (`attachment_key_id_stale`);
          * files without a row are counted (information).
          * Counts and generated storage names only; never file names or content.
        * Key coverage of the restored set against the key directory is reported.
     7. **Verdict.** `ok`, `ok_with_warnings` (exit 8) or `failed` (exit 1, codes and counts listed). The restored database is DROPPED after the checks by default: only the database this run generated, whatever the verdict.
     8. **`--keep`.**
        * The database is kept, and its physical name is NEVER printed (the `Hasm.Admin` convention).
        * The name, the token and the company id are written to an owner-only file next to the set, `<set dir>.trial-<random>.json`. It cannot go inside the set: that would make the set unclean. Only that file's path is printed.
        * No registry change, no runtime grant, no cutover.
   * **`DropTrialRestoreAsync(keptFile)`** removes a kept trial database later. It reads the owner-only file and refuses (`not_a_trial_restore`) unless all of these hold:
     * the recorded name is a valid physical identifier;
     * it is not any registered company's database and not the system, maintenance or built-in databases;
     * the database's comment equals `hasm-trial-restore:<token from the file>`.
     Only then does it run `DROP DATABASE ... WITH (FORCE)` and delete the file. This is a documented procedure, not a manual DBA step.
2. **`Hasm.Admin` commands:**
   * `backup-company <company-id> <destination-dir>`
   * `trial-restore-company <set-dir> <company-id> <attachment-target-dir> [--keep]`
   * `drop-trial-restore <kept-file>`
   Configuration comes from the environment, as for every other command: the admin database account is required, plus `Hasm__Attachments__StorageDirectory` and `Hasm__Encryption__KeyDirectory`. The PostgreSQL client tools come from `PATH` (no new configuration option). Output is stable codes, counts, the company id and file paths given by or created for the operator; never a physical database name, a password, tool output, party data or attachment file names. Exit codes:
   * 0: ok
   * 1: refused or failed (code printed)
   * 2: usage
   * 3: configuration
   * 8: completed with warnings (missing key ids, older migration level, stale key ids)
3. **Test infrastructure:** (`PosixFactAttribute` was not built: see the implementation status; the fake-tool tests pass without running on Windows.) `PosixFactAttribute` (unit tests) sets `Skip` on Windows, so the fake-tool tests are REPORTED as skipped instead of passing by an early return. All fake tools of a test class are written in the class constructor, before any process of that class starts, under the test output directory (exec-capable, unlike a possibly `noexec` temp folder). A helper retries the first `--version` run ONLY when the launch fails with `ETXTBSY` (errno 26, the race with a concurrent fork in another test); every other launch error fails at once. The `PgBackupToolProcessTests` move to it.
4. **Operator documentation** (`docs/DEVELOPMENT.md`, new "Company backup and trial restore" section):
   * the three commands and their exit codes;
   * no backup is possible without `Hasm__Attachments__StorageDirectory` (no override);
   * the staging-file pattern and its 24-hour sweep;
   * the owner-only kept-trial file and `drop-trial-restore`;
   * the key directory is backed up separately (decision K);
   * the cutover is not available (BLOCKED) and the manifest is not authenticated.

### Exact tests
* **Unit (`Hasm.Infrastructure.Tests`):**
  * `PosixFactAttribute` sets `Skip` exactly on Windows. The four existing `PgBackupToolProcessTests` use it, write their tools in the constructor, and retry only on `ETXTBSY` (a non-`ETXTBSY` launch error is not retried: tested with a non-executable file).
  * `PgBackupTool.EnsureRestorable`: libc and PostgreSQL 14 (no provider) are accepted; ICU, builtin and unsafe names are refused with `locale_unsupported`.
  * Restore-locale choice (pure function):
    * manifest = live -> manifest;
    * manifest differs from live only by provider null vs `c` -> manifest (no mismatch);
    * a real difference -> `locale_mismatch`;
    * manifest only -> manifest; live only -> live; neither -> `source_locale_unknown`;
    * an invalid manifest locale is refused even when it equals the live one.
  * Compatibility classification (pure function):
    * newer than the installation -> refused;
    * older -> warning;
    * equal -> ok;
    * restored level different from the manifest -> `migration_level_mismatch`;
    * a checksum that differs from the embedded script -> `migration_checksum_mismatch`.
  * Attachment check classification (pure function, from facts about a row and its file): missing, hash mismatch, undecryptable, key missing (warning), stale row key id (warning), extra file (information).
  * Manifest: `companyMigrationVersion` round trip; a manifest without it still reads.
  * Path rules: a destination inside the attachment root or the key directory is refused; a trial attachment target inside the live attachment root is refused, including through a symbolic link (POSIX).
  * Staging sweep: only exact `<leaf>.dump-<32 hex>` names older than 24 hours are removed. A younger file, another leaf, an uppercase or shorter suffix, and a directory are all kept.
  * Kept-trial file: owner-only permissions (POSIX); malformed content is refused.
* **PostgreSQL (`Hasm.Database.IntegrationTests`, new class `CompanyBackupServiceTests`, 11 tests).** The test registry in `.github/workflows/backend.yml` is RAISED by 11 at push time, counted from whatever main is then.
  1. A registered Ready company is backed up into a clean set bound to its id, with the captured locale and migration level. The source, another company database and the system database are unchanged (catalog-and-content digests, as in the slice 2 drill).
  2. Refusals before any dump: unknown company, a Pending company, unconfigured attachment storage, a non-empty destination, a destination inside the attachment root. No staging file and no set remain. A stale staging file older than 24 hours is swept and a young one is kept.
  3. An ICU company database (created `LOCALE_PROVIDER icu` on the PostgreSQL 16 CI server, whose image has ICU, and registered as Ready) is refused with `locale_unsupported` up front; no set and no staging file remain.
  4. A trial restore into a new unregistered database passes every check:
     * locale equal;
     * migration level and checksums equal;
     * a linked local party resolves in `Hasm_System`;
     * one plain and one sensitive attachment verified, including decryption;
     * key coverage complete.
     The database is dropped afterwards, and the registry and the `Hasm_System` digest are unchanged.
  5. Trial restore refusals, with nothing created in any case:
     * a set of another company (`company_mismatch`);
     * a set of an unknown company;
     * a manifest whose locale was edited to a different valid libc locale while the source exists (`locale_mismatch`);
     * an attachment target inside live storage (`target_in_live_storage`).
  6. Disaster recovery: the company's database is dropped after the backup (the registry row is kept). The trial restore succeeds from the manifest locale, and the restored digest equals the pre-backup digest.
  7. Check (a): after the backup, the company's link for a local party is changed in `Hasm_System` to another global party, and the link of a second party is removed (test setup on the disposable server). The trial restore reports `failed` with `shared_links_unresolved` = 2, and the `Hasm_System` digest is unchanged by the restore.
  8. Check (b):
     * failures: a set whose dump references an attachment file that is missing from the set, and one whose file content differs from the row hash (the manifest is regenerated so the SET is clean, simulating an inconsistent backup), give `attachment_file_missing` and `attachment_hash_mismatch`;
     * warnings: a sensitive file whose key is absent from the key directory, and a rewrapped envelope with a stale row key id, give warnings only (exit 8 path).
  9. Check (c): a manifest whose `companyMigrationVersion` exceeds the embedded maximum is refused with `set_newer_than_installation` before any database is created. A source whose journal checksum for one version was altered before the backup (disposable database) gives `migration_checksum_mismatch`.
  10. `--keep` and `drop-trial-restore`:
      * the kept file is owner-only and holds the name; the output does not contain the physical name;
      * `drop-trial-restore` refuses a kept file naming a registered company database, and one whose token does not match the database comment (`not_a_trial_restore`), dropping nothing;
      * with the right file it drops exactly the trial database and deletes the file.
  11. The real `Hasm.Admin` binary:
      * `backup-company`, then `trial-restore-company` (exit 0), then `--keep` + `drop-trial-restore`;
      * usage errors exit 2; a missing storage setting exits 3;
      * stdout and stderr contain neither the admin password, any physical database name nor any tool output.
* **Not tested (recorded):** real-tool TLS (the disposable server has no TLS); PostgreSQL 14/15 servers (CI runs 16); the blocked-drop path; the Windows process tests (they pass without running on Windows).
* **Commands before every push:**
  * `dotnet build src/backend/Hasm.slnx -c Release`;
  * `dotnet test tests/unit/Hasm.Infrastructure.Tests -c Release --no-build`;
  * the API tests;
  * the PostgreSQL suite in CI (postgres-integration is authoritative), or locally only through the single-holder lock of the Linux test harness (PR #89), once merged and only when free; never port 5432.

### Stays BLOCKED or out of this slice
* **BLOCKED (owner decision):**
  * the audited restore acknowledgement and the new checkpoint epoch, and therefore the cutover that replaces a live company database or touches its checkpoints (the part of 21.8 this slice does not meet);
  * retention/purge of removed attachments (decision R). The backup ordering argument above relies on R staying open.
  Nothing in this slice decides either. No audit event is added: an audited backup/restore record belongs with the acknowledgement and the monitoring slice.
* **Not in this slice:** scheduling, retention of sets, destinations, set encryption and signing (until then the manifest, including its locale and migration level, is NOT authenticated: see D-059), monitoring/alerts, RPO/RTO measurement, full-installation restore, key backup (kept separately by the operator), and encrypted import-file originals (decision K follow-up).

### Decisions under D-048 (recorded in docs/DECISIONS.md)
* **D-058: entry points and binding.**
  * Decision: three `Hasm.Admin` commands over one infrastructure service. The company and its database come only from the registry, and only in state Ready. Attachment storage must be configured (no override). Client tools come from `PATH`. A physical database name is never printed.
  * Alternatives rejected:
    * an API endpoint now (needs the RBAC, licensing and UI decisions of the monitoring slice);
    * accepting a database name;
    * treating missing storage configuration as "no attachments";
    * printing the kept trial database name.
* **D-059: restorability and manifest trust.**
  * Decision: a set is written only if its captured locale passes the restore rules. The unsigned manifest locale is always validated, cross-checked against the live database when it exists (null and `c` being the same libc provider; `locale_mismatch` otherwise), and trusted alone only when the database is gone, until set signing exists.
  * Alternatives rejected:
    * checking only at restore time;
    * ignoring the live database;
    * refusing whenever the database is gone (defeats D-053).
* **D-060: trial restore and its pre-cutover checks.**
  * Decision: a new generated, unregistered, token-tagged database plus an attachment folder outside live storage. It runs the read-only 21.8 checks: shared party links resolve in `Hasm_System`; attachment rows match their files, hashes and keys; migration compatibility. It is then dropped by default. `--keep` writes an owner-only file next to the set, and `drop-trial-restore` later drops only a database whose comment carries that file's token. No registry, grant or checkpoint change; the cutover stays BLOCKED.
  * Alternatives rejected:
    * deferring the 21.8 checks to the cutover;
    * restoring under the registered name or into live storage (that is a cutover);
    * keeping every trial database;
    * a manual DBA drop.
* **D-061: key coverage is reported, not refused,** at backup and at trial restore (exit code 8 and the missing key ids). A row's stale key id is a warning, because decryption uses the envelope. Alternative rejected: refusing the backup (the gap already exists in the live system; refusing would leave the company with no backup at all).
* **D-062: migration compatibility of a backup set.**
  * Decision: the company migration level is captured at backup time in the manifest (optional; older sets are checked after the restore). A level newer than this installation is refused before anything is created; an older level is a warning (a cutover would need `migrate-companies` first). The restored journal must equal the manifest level and match the embedded script checksums.
  * Alternatives rejected:
    * checking only after a restore (creates a database for a set that can never be used);
    * migrating the trial copy automatically (a write the trial restore must not make);
    * ignoring checksums (a set from a diverged build would pass).

## Slice 4: backup runs, monitoring and measured durations (plan approved in PR #100; implemented on `feature/phase-4-3-slice4-impl`)

### Implementation status (code PR)
* **Built as planned:**
  * `BackupRunService` with `BackupRunFiles`, `SystemBackupSet` and `BackupRunStatus` (`src/backend/Hasm.Infrastructure/Backup/BackupRun.cs`);
  * `PgBackupTool.DumpSystemAsync` (excluded table data) and `PgBackupTool.VerifyArchiveAsync` (`pg_restore --list`, exit code only);
  * the explicit secret-store flag `SystemSchema.SecretStoreTables` (session, login throttle), pinned to `BackupRunService.SystemDumpExclusions` in both directions by a unit test;
  * `Hasm.Admin backup-run`, `backup-status` and `backup-retention-preview`;
  * the trial-restore elapsed time (`TrialRestoreResult.Elapsed`, printed);
  * the operator section in `docs/DEVELOPMENT.md` (commands, the sensitive system dump, consistency, scheduling examples for systemd, cron and Windows, and the drill-log formulas);
  * D-069..D-073 marked implemented.
* **Nothing reserved by owner package 4.3-2 is built:** no encryption, no signature, no deletion, no key export. No migration, API endpoint or UI.
* **Details fixed by the implementation (no change of decision):**
  * `backup-status` and `backup-retention-preview` are evaluated before any database configuration is read, so a monitor needs no credentials.
  * Unreadable reports are counted as a warning (`unreadable_reports`) and are never used or selected.
  * In the retention preview, a run kept by either rule (`--keep-last`, `--keep-days`) is kept.
  * Additional codes:
    * `archive_unreadable`, `run_exists` and `run_not_private`;
    * `database_error`, `io_error` and `unexpected_error`: a company's or the system's failure is recorded by a code only and never stops the run.
    * `keep_last_invalid` and `keep_days_invalid`.
  * A refused second run (`run_in_progress`) writes nothing.
  * **`totp` is NOT in the excluded list yet** because no TOTP-secret table exists. The Phase 4.1 slice that creates it must add it to `SystemSchema.SecretStoreTables` and to `BackupRunService.SystemDumpExclusions` in the same change; the pinning test enforces that the two agree.
* **Review of PR #104 (Agent C on 5fbc221: approve, Lows only), dispositions:**
  * **L1, fixed:** the cron example exports the environment (`set -a; . admin.env; set +a`).
  * **L2, fixed (lead decision under D-048):** an unreadable `run.json` makes `backup-status` exit 8 (`unreadable_reports`) instead of passing behind an older `ok` run. Monitoring stays fail-closed; covered by a unit test.
  * **L3, fixed:**
    * `SessionKeepAlive` runs a `SELECT 1` every 30 seconds on the idle run-lock connection.
    * A failed no-op marks the run `partial` with the warning `run_lock_lost`, carried in an optional `warnings` field of `run.json` that older reports read without.
    * The documentation requires `idle_session_timeout` of the admin role to be 0, or above 60 seconds; this also covers the per-company locks held during long company dumps.
    * Unit tests cover the lost session and an immediate stop.
  * **L4, recorded as known limitations:**
    * Two runs started in the same second sort by their random suffix rather than by time. This is practically excluded: one run at a time, and a run takes longer than a second.
    * A `run.json` whose finish time lies in the future (clock skew or a manipulated report) is never reported stale until that time passes. Keep the server clock synchronised (NTP); a future check is a follow-up for the monitoring UI slice.
  * **L5, fixed:** this status states why `totp` is not yet excluded (above).
* **Review of PR #104, confirmation (Agent C on f087cef: approve), new Lows, both fixed:**
  * **L6:** the keep-alive is stopped, then the run lock is checked SYNCHRONOUSLY in `pg_locks` (this session, the run namespace, granted) before the verdict, so a loss in the last interval is never reported ok. Any failure counts as `run_lock_lost`.
  * **L7:** every exception in the keep-alive loop marks it failed, and its disposal never throws.
* Local verification and CI results: see the PR description.

### Plan (as approved)
Sources: SRS 16 (manual and scheduled backups, alerting and monitoring of the result, real restore tests, RPO/RTO measured rather than promised), SRS 14.4 (database and attachment folders backed up together), RELEASE1-ACCEPTANCE G. Risk: Critical (ROADMAP 4.3).

**Goal.** Back up the WHOLE installation in one unattended, schedulable run: the system database plus every Ready company (slice 3 sets). The run writes a machine-readable report. A status command lets an operating-system scheduler or monitor raise an alert. Durations are measured and recorded. Retention is evaluated as a preview only.

**Why this slice and not encryption first.** Encryption, deleting old backups and backing up the key directory each change key custody or the retention of financial data, so they are owner decisions. They are collected in ONE package (4.3-2, below). This slice is the largest remaining verifiable step that depends on none of them, and every later slice builds on its run layout.

* **Migrations: none.** Nothing is stored in any database; the run report lives in the run folder. If implementation shows a migration is needed, work stops and the lead allocates the numbers (next free: company 0030, system 0030).
* **No API endpoint and no UI.** The shared Web/Tauri frontend is unaffected; a backup screen belongs to the later monitoring UI.
* **What it does not meet.** This slice does NOT meet RELEASE1-ACCEPTANCE G or SRS 16 by itself:
  * sets stay unencrypted until package 4.3-2 is decided;
  * nothing is deleted automatically;
  * there are no multiple destinations (local and network copies, SRS 16): the run root is one local or mounted folder;
  * full-installation restore and the cutover are not built;
  * the restore acknowledgement remains BLOCKED (4.3-1).

### Design
1. **`BackupRunService`** (`Hasm.Infrastructure.Backup`, over `CompanyBackupService` and `PgBackupTool`):
   * **`RunAsync(root)`, refusals before anything is written:**
     * the root must be absolute and outside the attachment root and the key directory, in both directions (`destination_overlaps_source`);
     * only one run per installation: a session advisory lock in a dedicated namespace on the system database (`run_in_progress`, exit code 4).
   * **Run folder.** `<root>/<run id>/`, where the run id is `yyyyMMddTHHmmssZ-<8 hex>`, so ids sort by time. The folder is created owner-only (0700, verified on Linux, as the slice 3 staging folder) and refused if it already exists.
   * **System database, `system/`.**
     * `database.dump` comes from a NEW `PgBackupTool.DumpSystemAsync`, which dumps exactly the configured system database name (never a caller-supplied name). The company paths keep refusing the system database.
     * `manifest.json` is a `SystemBackupManifest`: version, kind `system`, creation time, the dump's size and SHA-256, the captured locale, and the highest system migration version.
     * After writing, the dump's table of contents is checked to be readable with `pg_restore --list`; only the exit code is used, the output is discarded. This proves ONLY that the archive's table of contents is readable, not that the data restores. Integrity against corruption is the SHA-256 in the manifest; authenticity needs package 4.3-2 item 1. Restoring the system database is the later full-installation slice.
     * **Sensitive contents of the system dump.** It holds:
       * password hashes of every user;
       * session records with session-token hashes;
       * login-throttle data (login identifiers, source addresses, failure counters);
       * membership and role-assignment data;
       * the shared party registry with personal and identifying data (legal names, tax and registration numbers);
       * the system audit chain;
       * once Phase 4.1 builds them, TOTP secrets.
       It is the most sensitive file of a run; package 4.3-2 item 4(a) (an encrypted volume for the backup root) applies to it in particular.
     * **TOTP secrets, fail closed.** Until package 4.3-2 item 1 is decided, `backup-run` EXCLUDES the data of the TOTP-secret table (`pg_dump --exclude-table-data`, by its opaque name) and records `"totp"` in the `excluded` list of the system manifest and of `run.json`.
       * Chosen over refusing the run: a refusal would leave the installation with no backup at all once MFA exists.
       * Consequence: after a full-installation restore, users re-enrol MFA through the Phase 4.1 recovery procedure.
       * **Requirement on Phase 4.1 and on the full-installation restore slice (review round 2, M1).** After a restore without TOTP secrets, a user whose account requires MFA but has no secret is BLOCKED until the Phase 4.1 recovery procedure has run. Such a user is never treated as having no MFA, and never logs in with the password alone. This ties to the OPEN owner item 4.1-3 (lost-authenticator recovery); until that is decided, such accounts stay blocked (fail closed).
       * The table does not exist yet (only the TOTP primitive, #72). The 4.1 slice that creates it must add it to the exclusion list.
     * **Ephemeral security data, excluded (lead decision under D-048, review round 2, L1).** `backup-run` also EXCLUDES the DATA of the session table (`SystemSchema.Session`) and of the login-throttle table (`SystemSchema.LoginThrottle`) from the system dump. Both are recorded in the `excluded` list of the system manifest and of `run.json`, with the TOTP entry.
       * They have no recovery value: a restored installation starts with no sessions and fresh throttling.
       * Excluding them removes session-token hashes and revivable sessions at the source.
       * The restore requirement to drop or invalidate sessions stays as defence in depth.
     * **Explicit secret-store marker (review round 2, L2).** The logical schema dictionary gets an explicit flag, not a naming convention: `SystemSchema` marks each table that stores secrets or ephemeral security data, starting with the session and login-throttle tables and later the TOTP table.
       * A unit test pins the dump exclusion list against that flag: every flagged table must be excluded (or, after package 4.3-2 item 1, explicitly encrypted), and every excluded table must be flagged; otherwise the test fails.
       * A new secret-store table cannot reach an unencrypted set by omission.
   * **Companies, `companies/<company id N>/`.**
     * Every company in state Ready, in registry order, is backed up through `CompanyBackupService.BackupCompanyAsync` (its own lock, staging folder, checks and verification).
     * The outcome per company is `ok`, `ok_missing_keys` or `busy`, or `failed` with its code.
     * One company's failure never stops the others. Companies not in state Ready are counted as skipped.
   * **A run is NOT one consistent point in time across databases.**
     * Order: the system database is dumped first, then the companies one by one in registry order. Each `pg_dump` takes its own snapshot of ONE database; PostgreSQL has no snapshot shared across databases.
     * The consistency window runs from the system dump's start to the last company dump's end. `run.json` records the start and end of each dump.
     * In an `ok` run these can disagree:
       * the registry (a company registered, provisioned or changed in state after the system dump);
       * party links in the registry against `GlobalPartyId` in a company;
       * memberships and role assignments against company-side references;
       * migration levels;
       * audit checkpoints against streams.
     * This is acceptable for the single-company restore of slice 3: Scenario 21.8 never rolls the system database back, and the trial restore compares the company copy with the LIVE system database (checks a and c).
     * **Recorded for the full-installation restore slice:** it must RECONCILE the restored system database with every restored company, reporting differences in the manner of slice 3's checks a and c (registry entries, links, memberships, migration levels), rather than assume one consistent point.
     * **Also recorded for that slice:** restored sessions must be dropped or invalidated, for example by deleting the session rows or advancing a session epoch, so that no revoked session is revived by a restore.
   * **Run report, `run.json`.** Owner-only, written LAST (temp file then rename), so a run folder without `run.json` is an incomplete run. Contents:
     * the run id, start and finish times (UTC) and duration;
     * the system outcome and duration;
     * per company: the company id, outcome code, duration in milliseconds, dump bytes, number of attachment files, number of missing key ids;
     * totals, and the verdict: `ok`, `partial` (some company or the key coverage failed) or `failed` (the system dump failed or no set was written);
     * the union of missing key ids (ids only);
     * the PostgreSQL client major version;
     * `encrypted: false`, stated explicitly until 4.3-2.
     * Never a physical database name, a path inside the attachment storage, a password or tool output.
   * **Exit codes:** 0 `ok`; 8 `partial`; 1 `failed`; 4 `run_in_progress`; 3 configuration.
2. **Monitoring: `backup-status <root> [--max-age-hours N]`.** Read only. It reads the newest COMPLETE run (the highest run id that has a valid `run.json`) and prints its verdict, age, totals and outcome counts.
   * Exit 0 when that run is `ok` and younger than the maximum age. The default maximum age is 26 hours (a daily schedule plus slack); it is configurable per call.
   * Exit 8 when it is `partial`.
   * Exit 1 when it `failed`, when no complete run exists (`backup_missing`), or when it is too old (`backup_stale`).
   * Incomplete run folders older than 24 hours are reported as a warning (`incomplete_runs`); they are never deleted.
   * Per company, the number of CONSECUTIVE complete runs (newest first) in which it was `busy` or `failed` is reported. Two or more is a warning (`company_missed_runs`, exit 8), so a company that is always busy at backup time does not stay unnoticed behind an otherwise `ok` verdict.
   * This exit code is the alert hook for the operating-system monitor (SRS 16).
3. **Scheduling.** Not built into HASM. An operating-system scheduler starts `Hasm.Admin backup-run <root>` under the service account; tested examples for a systemd timer, cron and Windows Task Scheduler go into `docs/DEVELOPMENT.md`. The API never runs a privileged backup: it holds no admin credential (privilege separation).
4. **Retention, preview only: `backup-retention-preview <root> --keep-last N [--keep-days D]`.**
   * A pure policy evaluates the complete runs and lists those that WOULD be removed. It never touches anything.
   * Hard rules, already in the preview:
     * the newest complete run is always kept;
     * incomplete runs are listed separately and never selected;
     * a run whose report cannot be read is never selected.
   * Actual deletion is BLOCKED by owner package 4.3-2.
5. **Measured durations (RPO/RTO inputs).** `backup-run` records the durations above. `trial-restore-company` adds its elapsed time to the result and output, which is the measured restore time of that set. `docs/DEVELOPMENT.md` documents:
   * how to record them in a drill log: RPO is at most the schedule interval plus the run duration; RTO is the measured trial-restore time plus the cutover procedure once it exists;
   * that no fixed value is promised (SRS 16).

### Exact tests
* **Unit (`Hasm.Infrastructure.Tests`):**
  * Run id: format and time ordering.
  * `run.json`: round trip; malformed, unknown-version or path-bearing content is refused.
  * `SystemBackupManifest`: round trip and verification (missing, altered or unlisted file).
  * Status evaluation (pure, from a list of runs and the current time):
    * ok -> 0; partial -> 8; failed -> 1; none -> `backup_missing`; too old -> `backup_stale`;
    * an incomplete newest folder is skipped in favour of the newest complete run and reported;
    * consecutive `busy`/`failed` misses per company are counted, and two or more give a warning.
  * Secret exclusion: the exclusion list (session, login-throttle, and later TOTP data) equals the set of tables flagged as secret stores in the logical schema dictionary, in both directions (see the design).
  * **PostgreSQL test 4 also asserts:** the table of contents of the system dump (`pg_restore --list`, read by the test itself) lists the session and login-throttle TABLES but no TABLE DATA entry for them.
  * Retention preview (pure): keep-last, keep-days and both together; the newest complete run is never selected; incomplete and unreadable runs are never selected.
  * Path rules: a root inside the attachment root or the key directory, or containing one, is refused.
  * Run folder: owner-only 0700 and refused if it exists (the slice 3 helper).
* **PostgreSQL (`Hasm.Database.IntegrationTests`, new class `BackupRunTests`, 6 tests).** The registry in `.github/workflows/backend.yml` is RAISED by 6 at push time, counted from main then.
  1. Two Ready companies and one Pending: the run writes the system set and two verified company sets. The report says `ok`, skipped = 1, and holds no physical name. Both company databases, another database and the system database are unchanged (digests, as in slices 2 and 3).
  2. One Ready company whose database was dropped (disaster): its outcome is `failed` with `company_database_missing`, the other company is `ok`, and the verdict is `partial`.
  3. One company whose provisioning lock is held: its outcome is `busy` and the verdict is `partial`. A second run started while the first holds the run lock is refused with `run_in_progress`.
  4. The system set's dump passes `pg_restore --list`; its manifest is verified and an altered dump is detected.
  5. `backup-status` on real runs: ok, partial, stale (by `--max-age-hours`), no run, and an incomplete newest folder.
  6. The real `Hasm.Admin` binary:
     * `backup-run` exits 0, `backup-status` exits 0, and `backup-retention-preview` lists, and deletes nothing;
     * usage errors exit 2; missing storage configuration exits 3;
     * stdout and stderr contain neither the admin password, any physical database name nor tool output.
* `trial-restore-company` elapsed time: covered by extending the existing `CompanyBackupServiceTests` assertions (no new test method; the registry count for that class is unchanged).
* **Not tested (recorded):** the operating-system scheduler examples, except by a documented manual drill; real-tool TLS; PostgreSQL 14/15 servers.
* **Commands before every push:**
  * `dotnet build src/backend/Hasm.slnx -c Release`;
  * the unit and API tests;
  * the PostgreSQL suite in CI, or locally only through the harness lock (status first; own cluster; destroyed afterwards).

### Decisions under D-048 (recorded in docs/DECISIONS.md, status planned)
* **D-069: scheduling by the operating system.** Decision: a backup run is an administrative command started by the operating-system scheduler; the API never runs privileged backups. Alternatives rejected:
  * a scheduler inside the API (needs the admin credential in the API process, against privilege separation);
  * a new resident service (new deployment surface and update path, a product decision for the 4.4/4.5 installers).
* **D-070: run layout, report and isolation.** Decision: a per-run owner-only folder; the system database dump included; one set per Ready company; `run.json` written last; one company's failure never stops the others; one run at a time. Alternatives rejected:
  * a report in the system database (a migration, and it would be lost exactly when that database is the problem);
  * stopping at the first failed company (other companies would lose their backup).
* **D-071: monitoring through `backup-status` exit codes** with a configurable maximum age (default 26 hours). Alternative rejected: e-mail or other push alerts from HASM (needs mail configuration and secrets; deferred to the monitoring UI slice).
* **D-072: retention is previewed, never applied, until owner package 4.3-2 is decided.** Hard rules in the preview: the newest complete run is always kept; incomplete or unreadable runs are never selected. Alternative rejected: deleting by a default policy (deleting backups of financial data is an owner decision).
* **D-073: RPO/RTO are measured, not promised.** Durations are recorded per run and per trial restore, and the formulas are documented. Alternative rejected: a promised fixed value (SRS 16 forbids it without measurement).

### OWNER DECISION PACKAGE 4.3-2 (one consolidated recommendation; recorded once in the INTEGRATION-STATUS register; fail-closed while open)
Backup sets today are verified but NOT encrypted or authenticated, and nothing is deleted automatically. The four items below decide key custody and the retention of financial data, so they need the owner. **Recommended answer: accept options 1(d), 2(b), 3(b) and 4(a) together.**

1. **Who can decrypt a backup (encryption key custody).** Every file of a run would be encrypted with streaming AES-256-GCM, and the manifest authenticated.
   * (a) A dedicated backup key in the server's existing key directory. Simple; but whoever controls the server can read every backup.
   * (b) Reuse the attachment master key. Simplest; but one compromise exposes both, and rotation is coupled.
   * (c) An operator passphrase. Unattended scheduled runs would need it stored on the server, which defeats the purpose.
   * **(d) Recommended: public-key encryption to an offline recovery key pair.** The server holds only the PUBLIC key (ECDH P-256, built into .NET), so a compromised server cannot read existing backups. The private key is kept offline by the owner or operator, in two separate places.
     * Restore and trial restore need the private key to be brought to the machine.
     * Losing every copy of the private key makes every backup unrecoverable.
     * The manifest is signed with a server-held signing key (ECDSA P-256 in the key directory), so tampering by anyone without that key is detected.
2. **Whether HASM may delete backups (retention of backups containing financial data).**
   * (a) Never delete automatically; operators delete by hand.
   * **(b) Recommended:** automatic deletion is allowed only for daily and weekly runs beyond a configured count (default: keep 7 daily and 4 weekly). Month-end and year-end runs are NEVER deleted automatically; they are kept until an owner-configured legal retention period has passed, and the default is "never". Every deletion is written to the run report.
     * Proposed definitions, for the owner to confirm:
       * a **month-end run** is the last complete run started in a calendar month (server local time);
       * a **year-end run** is the month-end run of any month in which at least one company's fiscal year ends (from its company configuration), kept for the whole installation.
   * (c) A fixed legal period built into the product. Not recommended: legal retention of accounting records differs between jurisdictions, and HASM does not choose it.
3. **Backup of the key directory.** Without it, encrypted attachments, and under 1(a)/(b) the backups themselves, cannot be read after a server loss.
   * (a) Stays a manual, documented operator task (decision K).
   * **(b) Recommended:** `backup-keys <destination>` exports the key directory encrypted to the same offline recovery public key as 1(d). It is written to a destination that must differ from the data run root, and the key ids are listed in the run report.
     * 3(b) as written works only with 1(d). Under 1(a) or 1(b), the export would contain the very key that decrypts the backups, so it needs a separate protection that the owner names (a second offline key or a passphrase); without one, item 3 falls back to 3(a). Under 1(c), the same passphrase would protect both, so one secret guards everything.
4. **Interim, until 1 is built.**
   * **(a) Recommended:** keep producing unencrypted sets, with the documentation requiring an encrypted volume or disk for the backup root.
   * (b) Refuse `backup-run` until encryption exists. Safer at rest, but no backups at all meanwhile.

**Effect while the package is open:**
* no set encryption or signature;
* TOTP secrets (once they exist), sessions and login-throttle data are excluded from the system dump;
* nothing deleted (retention is preview only);
* no key-directory export;
* sets remain unencrypted with the documentation requirement of item 4(a);
* RELEASE1-ACCEPTANCE G is not met.

The slice after this one implements whichever answer the owner gives. No migration is expected.

## Limitations (recorded)
* The manifest detects corruption and loss; it is not a signature and the set is not encrypted (later slice).
* `KeyIdsRequired` covers attachment envelopes only. Encrypted import-file originals are outside the attachment folder and need their own key coverage (the import-file rewrap retirability report, decision K follow-up, is not built yet).
* Restore into a NEW database only; replacing a live company database (cutover) and full-installation restore are not built. Scheduling (by the operating system), monitoring and measured durations: slice 4 (implemented). Set encryption and retention: owner package 4.3-2.

## BLOCKED / owner decisions
* Audited restore acknowledgement and a new checkpoint epoch after a company restore (`docs/DEVELOPMENT.md`, restore limitations): policy needs an owner decision.
* Retention/purge of removed attachments (decision R).
* Set encryption and authentication, automatic deletion of backups (retention), and the key-directory export: OWNER package 4.3-2 (slice 4 plan). Scheduling (by the operating system), monitoring and measured durations: implemented in slice 4. Destinations other than a local or mounted folder, and full-installation restore: not started.
