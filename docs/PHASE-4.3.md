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

## Slice 5: encrypted and signed backup sets, key export, DEV/TEST-only guard - PLAN (docs only; revised after review round 1 of #116 by Agent G)
Sources: owner decisions D-085 (envelope encryption to an offline recovery key pair, authenticated encryption, signed manifests, key ids, rotation, recovery test), D-087 (`backup-keys`), D-088 (unencrypted sets DEV/TEST only); SRS 16, 17; RELEASE1-ACCEPTANCE G. Risk: Critical.
Builds on slice 4 (PR #104, approved, in final CI). Decision ids D-089..D-095 and, after review round 1, D-116..D-119 (allocated by the lead; autonomous under D-048; D-118 and D-119 are lead decisions).

### Scope
* Every backup artefact is encrypted to the offline recovery PUBLIC key (the private key never reaches the server) and every manifest is signed with a separate server-held signing key. This covers:
  * the slice 3 company set;
  * the slice 4 run, including the system dump and `run.json`;
  * the `backup-keys` export.
* Unattended runs need only public material and the signing key.
* Restore, trial restore and the recovery test need the offline private key, brought in by the operator.
* `backup-keys` (D-087).
* `backup-recovery-test`. A recovery key is "production-ready" only after a recorded, successful recovery test (D-085).
* The DEV/TEST-only guard for unencrypted sets (D-088).
* Not in this slice:
  * retention and deletion (slice 6, D-086, inactive by default);
  * the live cutover (slice 7, D-081);
  * multiple destinations.
* **No migration** (keys and markers are files). **No API, no UI.**

### Cryptographic design (routine technical choices under D-048; .NET built-in primitives only, no new dependency)
1. **Recovery key pair (D-089).**
   * The pair is ECDH on NIST P-256, generated OFFLINE by `Hasm.Admin backup-recovery-keygen <out-dir>`. That command needs no database configuration and refuses to run when any `Hasm__Database__*` variable is set, so it is not run on a configured server by accident.
   * Outputs:
     * the public key as PEM (SubjectPublicKeyInfo);
     * the private key as passphrase-encrypted PKCS#8 (PBES2: AES-256-CBC with PBKDF2-HMAC-SHA256, 600 000 iterations), written TWICE (`copy-1`, `copy-2`) for two offline media, with instructions.
   * **Passphrases (review M3).** They come from a prompt, entered twice, or for scripted use from standard input (`--passphrase-stdin`, file descriptor 0); never from an argument or an environment variable.
     * At least 20 characters (six diceware words also qualify), plus a matching confirmation.
     * `--separate-passphrases` encrypts copy 1 and copy 2 under different passphrases, for separate custodians.
     * PBES2 has no MAC of its own, so the recovery kit keeps the SHA-256 of each copy (review L4).
   * The key id is `rk-` plus the first 16 hex characters of SHA-256 over the SubjectPublicKeyInfo.
   * The server is configured with the public key only: `Hasm__Backup__RecoveryPublicKeyFile`.
   * Alternatives:
     * X25519/Ed25519 via libsodium/NSec: rejected, a new native dependency under the dependency policy;
     * RSA-OAEP-3072: acceptable, but larger keys and slower, with no advantage here;
     * the external `age` tool: rejected, an external binary and process plus a key format we do not control;
     * OpenSSL CMS: rejected, external tooling.
2. **File encryption format `HBE1` (D-090).** Each file is encrypted independently (ECIES), so any one file can be recovered alone.
   * **Header:**
     * magic `HBE1`, format version 1;
     * the recovery key id;
     * an ephemeral P-256 public key;
     * a 32-byte salt and a 7-byte nonce prefix;
     * a purpose label (for example `company-dump`, `attachment`, `system-dump`, `run-report`, `key-export`) and the set-relative file name;
     * a header MAC tag.
   * **Key derivation:** ECDH(ephemeral, recovery public) gives a shared secret. HKDF-SHA256 (salt, info `hasm-backup/v1` + purpose + file name + key id) derives a 256-bit file key and a header key.
   * **Body:** streaming AES-256-GCM in 1 MiB plaintext chunks. Each chunk nonce is the 7-byte prefix, a 4-byte big-endian chunk counter and a 1-byte last-chunk flag. The AAD is SHA-256 of the header plus the chunk index.
   * **What this detects:** reordering, truncation (the last-chunk flag is required), extension, header tampering, and moving a file to another name or purpose (bound through HKDF info and the AAD).
   * **Integrity is not authenticity (review H1).** ECIES to a public key and the header MAC prove that a file is intact, not who wrote it: anyone holding the public key can produce a valid HBE1 file. Authenticity comes only from the signed manifest (D-092, D-116).
     * Each manifest entry carries the SHA-256 of the file's HBE1 header and the ordered list of the SHA-256 of every stored chunk (ciphertext and tag).
     * **The manifest signature is verified first.** While decrypting, the header and then each stored chunk are checked against that signed list BEFORE the chunk is decrypted and released; the chunk count must match exactly. So no decrypted byte reaches `pg_restore` or attachment staging until it is authenticated.
     * **One read, the same buffer (review round 2, L-a).** Each chunk's hash is computed over the exact buffer that is then decrypted; nothing is read a second time.
     * **A failure partway through is a failed restore.** Output already released belongs to the disposable target, which is dropped (or the attachment staging, which is removed). A trial restore or recovery test never leads to a cutover.
     * The AEAD tag, chunk index and last-chunk flag remain a second, independent layer.
     * Memory stays bounded for multi-GB dumps. The chunk list costs about 64 bytes of manifest per MiB.
   * **Format details (review L1):**
     * encryption refuses beyond 2^32 - 1 chunks (`hbe_too_large`);
     * an empty file is ONE empty final chunk;
     * the ephemeral public key must be a P-256 point on the curve (the platform import rejects off-curve points; anything else is `hbe_malformed`).
   * Alternatives:
     * the existing attachment envelope `HFE1`: rejected; it is whole-file in memory and wrapped by a SERVER key, which contradicts D-085;
     * one key per run: rejected; any single file could not be recovered alone, and a key leak would affect the whole run.
3. **No plaintext at rest, and what an encrypted run contains (D-091).**
   * `pg_dump` writes to stdout (custom format), which is piped through the encryptor into `database.dump.hbe`. Attachments are read and encrypted file by file. `run.json` and the system manifest are encrypted as well; the run folder keeps only a small signed `run.status.json` (verdict, counts, times, key ids; no names) for `backup-status`.
   * Restore and the recovery test verify the signed manifest first, then decrypt in a streaming fashion into `pg_restore` stdin, releasing only authenticated chunks (H1). Attachments are decrypted into the private (0700) staging folder of the restore target, as today.
   * Alternative rejected: dump to a file, then encrypt. Plaintext would exist at rest, even if briefly and in a 0700 folder.
   * **Secret-store data in an ENCRYPTED run.**
     * The data of the session and login-throttle tables stays excluded: it is ephemeral and has no recovery value (slice 4).
     * Once Phase 4.1 creates the TOTP-secret table, its data is INCLUDED in encrypted runs. Its rows are already ciphertext under the protected key ring (D-080(2)), and the run adds `HBE1` on top.
     * Restoring TOTP secrets therefore needs the key-directory export (D-087), so MFA enrolment survives a full restore.
     * Unencrypted DEV/TEST runs keep excluding the TOTP data.
     * The slice 4 pinning test is extended so that every flagged table is either excluded, or included only in encrypted runs (a per-table rule in `SystemSchema`).
     * **Coordination with Phase 4.1 S1 (D-120, PR #120).** Whichever of 4.1 S1 and the slice 5 implementation merges SECOND switches `user_totp` to "included only in encrypted runs". Until both are on main, it stays always excluded (in `SystemDumpExclusions`).
       * The slice 5 implementation PR also replaces the #104 pin test, set-equality with `SystemDumpExclusions`, by a per-table rule.
       * Whichever of 4.1 S1 and the slice 5 implementation merges second introduces the per-table rule and makes the switch of `user_totp` to "included only in encrypted runs" (D-120).
       * Unencrypted DEV/TEST runs still exclude it.
       * The pinning test changes to match: session and login throttle always excluded; `user_totp` excluded in unencrypted runs and included, as ring ciphertext under `HBE1`, in encrypted runs.
       * This is on the implementation checklist.
4. **Signed manifests (D-092).**
   * The signing key is ECDSA P-256 in its own owner-only directory, `Hasm__Backup__SigningKeyDirectory`, separate from the attachment key directory and from the backup root, with an active key id `sk-...`. It is generated by `backup-signing-keygen <key-id>` on the server.
   * Every manifest (company `manifest.json`, the system manifest, `run.status.json`, the key-export manifest) gets a detached `*.sig`: base64 IEEE-P1363 signature over the exact file bytes, plus the signing key id.
   * **Which bytes are signed (review round 2, L-b).**
     * A manifest that stays readable (`run.status.json`, the key-export manifest) is signed over its exact file bytes.
     * An ENCRYPTED manifest (the company and system manifests, `run.json`) is signed over its exact CIPHERTEXT file, so the signature is checked before any decryption.
     * After decryption, the plaintext is held in memory only, with a size bound (at most 64 MiB, enough for about a million chunk hashes), and is parsed only after the signature and the file's own HBE1 authentication have passed.
   * **What the signature covers (review M1).** Every signed document contains, and so the signature covers:
     * its kind and the run id;
     * the company id, where there is one;
     * `createdUtc` and the format version;
     * the recovery key id and the signing key id;
     * for each file, its purpose, its name, its size, the header hash and the chunk-hash list (H1).
   * **Refusals (review M1):**
     * An HBE1 file whose manifest has a missing or invalid `.sig` is refused.
     * Whether a version 1 or unencrypted set is accepted depends ONLY on the reader's own acknowledgement (D-095), never on a field inside the set.
     * `backup-status` verifies the signature of `run.status.json`. A missing or invalid signature counts as a FAILED run (exit 1, `status_unsigned`).
   * **Trust anchor for verification:** the signing PUBLIC key (`sk-*.pub.pem`), which the operator keeps in the recovery kit (exported by `backup-keys` and printed at keygen). Verification never trusts a key carried inside the set itself.
   * **Key status in the recovery kit (review M5).** The kit lists every signing public key with a status: `active`, `retired` or `compromised`.
     * Verification refuses a `compromised` key (`signing_key_compromised`).
     * **Retired keys (review round 2, L-d).** A `retired` key verifies ONLY sets whose signed `createdUtc` is before its retirement date. That date is held in the kit's status list; it is never taken from the set.
     * The procedure is documented: mark the key compromised in every kit copy, generate a new signing key, take a new run, and treat sets signed by the compromised key after the suspected time as untrusted. Retiring a key needs independent approval (D-080(6)).
   * Manifest versions:
     * version 2 for encrypted sets; it lists ciphertext file names, sizes, header hashes and chunk-hash lists (H1), and the recovery and signing key ids;
     * version 1, the current format, stays readable for DEV/TEST sets only.
   * Alternatives:
     * HMAC: rejected; verification would need the secret;
     * Ed25519: rejected, not built in;
     * signing with the recovery key: rejected; its private key is offline by design.
5. **Rotation and versioning (D-093).**
   * Rotating the recovery key means a new pair, a new id and a new public key file. Older sets keep naming their old id, and the operator keeps every old private key offline for as long as those sets are retained (slice 6 retention).
   * Rotating the signing key means a new key id. Old public keys stay in the recovery kit; `backup-keys` exports every public signing key.
   * Every artefact names its format version and key ids. A reader refuses an unknown version or an unknown key id with a stable code.

### Commands
* `backup-recovery-keygen <out-dir>` (offline workstation; no database).
* `backup-signing-keygen <key-id>` (server; owner-only; never overwrites).
* `backup-run` and `backup-company`, unchanged arguments: encrypted and signed when the recovery public key and the signing key are configured; otherwise refused (`backup_encryption_not_configured`, exit 3), see the guard.
* `trial-restore-company <set> <company> <target> --recovery-key <private-key-file> --signing-public-key <pem> [--keep]`.
  * The passphrase comes from a prompt or from standard input (review M3). `HASM_RECOVERY_KEY_PASSPHRASE` is NOT supported: environment variables leak into process listings and crash reports.
  * **Runbook (review L3):** after a trial restore or a recovery test, remove the private-key file from the machine. Bring it on removable media and take it away again; never copy it to the server's disks.
  * The private key is loaded into memory only, and zeroed after use.
* `backup-keys <destination>` (D-087, D-094):
  * exports EVERY key directory (review M4) as one `HBE1`-encrypted, signed archive:
    * the attachment and encryption master keys (`Hasm:Encryption:KeyDirectory`);
    * the audit key ring (`Hasm:Audit:KeyDirectory`);
    * the backup signing PUBLIC keys and their status list ONLY (review round 2, N2).
      * The signing PRIVATE keys are never exported. A holder of the recovery key could otherwise sign forged sets, which would undo the split between the custodians and the server.
      * After a disaster, a NEW signing key is generated (D-093). Old sets still verify with the public keys held in the kit.
    * every signing public key;
  * the destination must not overlap the backup root, the attachment root, or the encryption, audit or signing key directories. The same set of directories is added to the backup-root overlap checks of `backup-run` and `backup-company`.
  * the key ids are listed in the signed export manifest;
  * key recovery (decrypt the export and verify the ids) is part of the recovery test.
* `backup-recovery-test <run-dir> --recovery-key <file> --signing-public-key <pem> [--keys-export <dir>]` (D-085):
  * verifies every signature;
  * decrypts every file streaming, checking hashes and AEAD tags;
  * restores the system dump and every company set into NEW disposable databases (trial-restore semantics; dropped afterwards);
  * runs the slice 3 checks;
  * decrypts the key export, when given, and checks that it contains every key id the sets need.
  * **Proof that cannot be forged with the server's signing key (review M2).**
    * Each run writes a random 32-byte canary encrypted to the recovery public key (`canary.hbe`). Only the canary's SHA-256 goes into the signed `run.status.json`; the plaintext is never retained.
    * **The proof is the preimage (review round 2, N1).** `recovery-test.json` contains the decrypted canary PREIMAGE: single-purpose random bytes, harmless once revealed.
    * `backup-status` accepts the test only if SHA-256(preimage) equals the hash in that run's signed `run.status.json`. The hash alone is public, so it proves nothing.
    * **Residual limitation:** the proof can be forged only by whoever holds the recovery private key, or by whoever controlled the backup process when the run was taken, because that process could retain the canary.
      * The proof shows that the private key decrypted this run, or that the run-time server was compromised.
      * It does not prove who ran the test; the audit event (D-118) records the operator.
  * **Keys checked (review M4):** the KEK ids that Phase 4.1's `user_totp` rows reference, and the audit key ids of the system and business streams.
  * **TOTP rows are never decrypted by the recovery test (review M6).** Only their key ids are compared with the export.
  * `backup-status` reports `recovery_untested` (exit 8) while no successful recovery test exists for the active recovery key id, so the key is "not production-ready" until it succeeds (D-085).

### DEV/TEST-only guard for unencrypted sets (D-088, D-095)
* **Default: refused.** Without a configured recovery public key AND signing key, every producing command refuses with `backup_encryption_not_configured` (exit 3): `backup-run`, `backup-company`, and `backup-keys`, which always needs the recovery key.
* **The only way to get an unencrypted set** is an explicit acknowledgement value, `Hasm__Backup__UnencryptedDevTestOnly=yes-this-is-not-production`. Any other value is a configuration error. It follows the disposable-cluster acknowledgement pattern of the test harness.
* **The acknowledgement is refused while a recovery public key is configured** (`unencrypted_ack_with_recovery_key`, review L2), so a production installation that has encryption set up can never be downgraded by one variable.
* **DEGRADED status (review L2):** while the acknowledgement is set, `backup-status` and every production-readiness or health status shows `DEGRADED: unencrypted backups (DEV/TEST only)`. This includes the API's readiness endpoint once one exists; the API exposes the flag read only and never sets it.
* **Every unencrypted artefact is marked:**
  * `encrypted: false`, `devTestOnly: true` in `run.json` and the manifests;
  * a file `UNENCRYPTED-DEV-TEST-ONLY` in the set and run folders.
* **Unencrypted sets stay visible:**
  * `backup-status` reports `unencrypted_dev_test` (exit 8) for every such run, so it can never look green;
  * `trial-restore-company` and `backup-recovery-test` refuse an unencrypted set unless the same acknowledgement is set.
* **How production is prevented:**
  1. Encryption is the default, and there is no silent fallback.
  2. The override needs an exact acknowledgement string, and its use is printed in every output and recorded in the report.
  3. Release acceptance stays blocked by D-088 until encryption and signed manifests are tested.
  4. A unit test pins that no shipped configuration or installer template in the repository sets the acknowledgement variable. Installers come in 4.5, and that test then covers their templates.
  5. The existing PostgreSQL tests set the acknowledgement explicitly, because they are DEV/TEST.
  * Alternative rejected: detecting "production" from an environment name. HASM has no such concept; a name is easy to get wrong, and a positive acknowledgement is auditable.

### Exact tests (to be detailed in the review round)
* **Unit tests:**
  * `HBE1` round trip: empty file, sizes around the 1 MiB chunk boundary, and multiple chunks.
  * Detection of each kind of tampering: header byte, chunk swap, truncation at a chunk boundary, removed last chunk, appended data, wrong key, file renamed or used for another purpose.
  * Key ids.
  * Encrypted-PKCS#8 round trip, and refusal of a wrong passphrase.
  * Signature verification: tampered manifest, wrong key, unknown key id, a `compromised` key (M5), a missing `.sig` (M1).
  * H1, release only after authentication:
    * every header and chunk is checked against the signed list before release;
    * a reordered, removed, truncated, extended or swapped file gives `hbe_not_authenticated`, while the format's own MAC and AEAD still catch it under a re-signed digest;
    * no plaintext is written before the first authenticated chunk (a failing first chunk leaves the output empty).
  * M1: `run.status.json` unsigned or invalid gives `backup-status` exit 1 (`status_unsigned`); acceptance of a version 1 or unencrypted set depends only on the reader's acknowledgement.
  * M2: canary. The run stores only the hash; a recovery test with a wrong preimage is refused; the plaintext canary is never on disk.
  * M3: passphrases shorter than 20 characters or mismatched are refused; separate passphrases per copy; no environment-variable passphrase.
  * N2: the export contains the encryption and audit key directories and the signing PUBLIC keys with their status, and never a signing private key (a test pins this).
  * N1: a recovery test whose preimage does not hash to the signed value is refused by `backup-status`; a hash-only `recovery-test.json` is refused.
  * L-d: a retired key verifies only sets signed before its kit retirement date.
  * M4: the export contains the encryption and audit key directories; overlap checks include the audit and signing key directories; the recovery test checks the TOTP KEK ids and the audit key ids, and never decrypts TOTP rows.
  * L2: acknowledgement refused while a recovery key is configured; DEGRADED status.
  * Manifest v2 validation, and v1 read as DEV/TEST only.
  * Guard: refusal by default, the exact acknowledgement only, the markers, `backup-status` `unencrypted_dev_test`.
  * Pin: no repository configuration sets the acknowledgement.
  * `recovery_untested` status.
* **PostgreSQL (new class, about 6 tests; the registry is raised at push time and the lead is told the count):**
  * an encrypted run, with no plaintext dump at rest (the run folder holds only `.hbe`, `.sig` and the status files);
  * trial restore with the private key;
  * `backup-keys` export and recovery;
  * `backup-recovery-test` end to end with the canary, writing `recovery-test.json` and turning `backup-status` green, and writing the system audit events (D-118);
  * a forged set (a valid HBE1 file under a manifest signed by another key, or an unsigned one) is refused before anything reaches `pg_restore`;
  * refusal without encryption configured, and the DEV/TEST path.
* **Existing suites:** `CompanyBackupServiceTests` and `BackupRunTests` run with the DEV/TEST acknowledgement (unencrypted, explicitly marked); their counts are unchanged.

### Interaction with Phase 4.1 (planned in PR #113; referenced, not duplicated)
* **Named administrator with MFA.**
  * `backup-recovery-keygen`, `backup-signing-keygen` and `backup-keys` are privileged administrative operations.
  * Under the Phase 4.1 plan they need a named administrator login with MFA (lead decision D-099 in that plan), applied through whatever mechanism 4.1 defines for administrative commands. This slice adds no separate authentication.
  * Until 4.1 delivers that mechanism, these commands run under the existing administrative-tool model: operating-system access to the service account and its configuration. The 4.1 slice that introduces the admin-tool login must cover them.
* **Independent approval of restores (lead classification under D-048, fail closed; D-118, review M7).** Trial restore (`trial-restore-company`) and `backup-recovery-test` ARE restore operations under D-080(6).
  * Once the Phase 4.1 approval framework exists, both require independent approval. Wiring them in is part of the 4.1 slice that delivers the framework, or of slice 7, whichever comes first.
  * Until then they run only through the administrative tool under operating-system access. This is a dated limitation recorded in `docs/RELEASE1-ACCEPTANCE.md` (G: "trial restore and recovery test without independent approval until the 4.1 approval framework, recorded 2026-11-21").
  * Either way, each run writes a SYSTEM-stream audit event: the operator (the named administrator under 4.1, the OS account until then), the recovery and signing key ids, the company ids, and a tag (SHA-256 prefix) of each target database; never a physical database name.
  * Event codes: `BackupTrialRestore` and `BackupRecoveryTest`, plus `BackupKeysExported` for `backup-keys` and `BackupKeyGenerated` for the signing keygen. They are allocated from the system-stream range when implemented; no migration (codes are constants).
  * The live restore cutover is slice 7 (D-081), with approval under D-080(6).
* **Credential revocations after a system restore (D-119, review M6, round 2 L-c).** The canonical rules are D-106 (restore effects) and D-107 (installation id and recovery-key compromise), from the Phase 4.1 plan. They are not restated here. They bind the Phase 4.3 full-installation restore and slice 7, and are implemented there.
* **TOTP secrets** (D-080(2)): see "Secret-store data in an ENCRYPTED run" above.
* **Key retirement.** Retiring a recovery or signing key is an encryption-key retirement and needs independent approval (D-080(6)). This slice only creates and rotates keys; it never retires or deletes one.

### Decisions added after review round 1 (D-116..D-119)
* **D-116, authenticity before release (H1, option a).** The signed manifest carries each file's header hash and per-chunk hash list. Decryption releases a chunk only after it matches. The header MAC is integrity only.
  * Alternative rejected: (b) a full hash pass over a held-open, replacement-protected handle, then decryption. It needs two reads of multi-GB files and platform-specific handle protection.
* **D-117, recovery-test proof by canary (M2, round 2 N1).** A per-run random canary is encrypted to the recovery key and only its hash is signed in `run.status.json`. `recovery-test.json` contains the decrypted PREIMAGE, which `backup-status` checks against that signed hash. The widened residual limitation is stated above.
* **D-118, trial restore and recovery test are restore operations (M7, LEAD decision, fail closed).** Independent approval once the 4.1 framework exists; until then admin tool only, recorded as a dated limitation in RELEASE1-ACCEPTANCE; system-stream audit events for both, and for `backup-keys` and the signing keygen.
* **D-119, credential revocations after a system restore (M6, round 2 L-c).** A reference to the canonical D-106 and D-107; it binds the Phase 4.3 full-installation restore and slice 7.
* The other review items amend D-089 (passphrases, M3/L4), D-090 (L1), D-092 (coverage, refusals and key status, M1/M5), D-094 (every key directory, overlap checks, TOTP KEK and audit key checks, M4) and D-095 (L2).

### Recorded as built (implementation of slice 5)
* **System-stream audit event codes, block 2010-2019** (lead allocation). Before use the registry was checked: retention (R, E) holds 2000-2009 and commercial documents 1900-1999, and nothing used 2010-2019 on main or on any open branch.
  * 2010: trial restore.
  * 2011: recovery test.
  * 2012: key export.
  * 2013: signing key generated.
  * 2014-2019: reserved for slices 6 and 7.
  * Persisted codes; never renumbered.
* **Interim mode (G round 1, L5).** Until the Phase 4.1 administrative-tool login (D-099) exists, `backup-recovery-keygen`, `backup-signing-keygen` and `backup-keys` run under operating-system access without a named administrator with MFA. This is the second dated limitation in RELEASE1-ACCEPTANCE G (through the roadmap generator).
* **Implementation status (code PR, branch `feature/phase-4-3-slice5-impl`).** Built as planned:
  * `RecoveryKeys`, `RecoveryKit` and `backup-recovery-keygen` (D-089); `Hbe1` (D-090); `BackupSigningKeys`, `SigningTrust` and the detached `.sig` (D-092); `SignedDocuments` (version-2 manifests encrypted and signed over their ciphertext, the signed `run.status.json`; M1, L-b, L-d);
  * `EncryptedSets` and the encrypted `backup-company` / `backup-run`: `pg_dump` stdout piped into HBE1, attachments encrypted file by file, no plaintext at rest (D-091), every stored file checked against its signed digest (D-116); the canary (D-117);
  * `KeyExport` and `backup-keys` (D-087, D-094, M4, N2), `backup-signing-keygen`;
  * the encrypted trial restore (streaming decryption into `pg_restore`, attachments into a private staging folder) and `RecoveryTestService` / `backup-recovery-test` with `recovery-test.json` and `recovery_untested` (D-085, D-117, N1);
  * the DEV/TEST guard (D-095, L2) with the `UNENCRYPTED-DEV-TEST-ONLY` marker in unencrypted sets and runs;
  * the system audit events 2010-2013 (`BackupAudit`; operator `os:<account>`, database TAGS only; fail closed);
  * the per-table secret-store rule (D-120): `SystemSchema.SecretStoreTables` maps each table to `AlwaysExcluded` or `IncludedOnlyInEncryptedRuns`, `BackupRunService.SystemDumpExclusionsFor(encrypted)` applies it, and the #104 pin test checks the rule in both modes and that any TOTP table is encrypted-only. Phase 4.1 S1 has not merged, so no TOTP table exists yet: whichever merges second adds it as `IncludedOnlyInEncryptedRuns` (the pin test enforces this).
* **Deviations from the plan text (no change of decision):**
  * `backup-signing-keygen` takes no argument: the key id is derived (`sk-` + 16 hex of SHA-256 over the public key), like the recovery key id, so an id can never be mistyped or reused.
  * The trust anchor of the restore commands is `--signing-trust <signing-trust.json>` (the kit's status list: `active`, `retired` with `retiredUtc`, `compromised`; M5, L-d). `--signing-public-key <pem>` remains, trusting one key as active.
  * `recovery-test.json` is not signed: the proof is the canary PREIMAGE checked against the SIGNED status (N1); a signature by the server key would add nothing and would tie the test to the signing private key.
  * `backup-recovery-test` refuses an unencrypted run in every case (`unencrypted_run`): it has no recovery key to prove. `trial-restore-company` accepts an unencrypted set only under the reader's acknowledgement, as planned.
  * The backup root is an argument of `backup-run`, not a setting, so `backup-keys` refuses a destination that is, or lies inside, a run folder or a folder that holds run folders (`destination_in_backup_root`), besides the planned overlap checks.
  * `backup-keys` also writes a READABLE copy of the trust list (`signing-trust.json`, public keys only) for the recovery kit.
  * The TOTP KEK-id check of the recovery test (M4) has nothing to check until Phase 4.1 creates `user_totp`; the attachment and audit key ids are checked.
* **Review of #128 by Agent C (fixed):**
  * **H1:** a version-1 (unencrypted, unsigned) company set planted in an encrypted run was restored by the recovery test. Now `CompanyBackupService.TrialRestoreAsync` itself refuses a version-1 set unless its caller passes `allowUnencrypted` from its own DEV/TEST acknowledgement, and always inside a run. The recovery test refuses every company set that is not encrypted (`unencrypted_set_refused`).
  * **L1:** each company set must belong to the run (`run_mismatch`), and the company folders must match the signed status (`company_sets_mismatch`).
  * **L2:** unit tests for an off-curve ephemeral point (`hbe_malformed`) and for a first chunk that fails authentication (nothing released).
  * **L3:** refused trial restores and recovery tests are audited too (2010/2011 with `outcome=refused` and the code).
  * **L4:** `backup-keys` parses every signing public key at write time (`key_file_invalid`).
* **Not built here (later slices):** writing the key export back to disk (only verified: decrypt and compare the ids) belongs to the full-installation restore and the cutover (slice 7); retention and deletion (slice 6).
* **Tests:** unit tests for every unit above (HBE1, keys, signatures and trust, signed documents and proof, guard and pin, encrypted sets, key export, status rules); a new PostgreSQL class `EncryptedBackupTests` (6 tests): an encrypted run with no plaintext at rest, trial restore with the private key equal to the source, forged/unsigned/swapped sets refused before `pg_restore`, `backup-keys` and `backup-signing-keygen` audited and recoverable, the recovery test end to end turning `backup-status` green, and encryption by default with the DEV/TEST path. The existing suites keep their counts and run with the DEV/TEST acknowledgement.

### As built: S8b (copy engine, `backup-replicate`, replication after `backup-run`)
* **Code:** `ReplicaCopier` (one run to one destination, D-185), `ReplicationService` (restore lock, destination lock, catch-up order, replication code), `DestinationLock`, `ReplicaStatusReport` (the S8a status lines, now shared by `backup-status`, `backup-run` and `backup-replicate`), `SystemReplicaEvents` (2040, 2041, 2043); `Hasm.Admin` gains `backup-replicate` and the replication step of `backup-run`. No migration; audit codes 2040, 2041 and 2043 (2042 is S8c).
* **Deviation from the text of D-185 step 4, recorded:** the plan lists "every file listed in the signed manifests exists with the signed size, header hash and chunk-hash list". The manifests are ENCRYPTED to the offline recovery key, and the copy must never need that key, so the copy cannot read the digests inside them. The implementation verifies instead: the signature of the run status and of every manifest over its ciphertext (against the server trust list), the status hash against the primary's, every file against the primary's size and SHA-256 (read back from the destination through a new handle after flush and close, with a best-effort `posix_fadvise(DONTNEED)` on Linux), the HBE1 structure of every encrypted file, and the folder's cleanliness (the slice 6 rule). Whether the PRIMARY matches its own signed manifests is the recovery test's job (it holds the key); the copy inherits the primary's state. The limitation of the read-back (page cache or client cache) stands as stated in D-185.
* **Publish order:** stage, flush, verify, 2040, rename, flush the parent; a publish that fails after 2040 is answered by 2041 `replica_publish_failed` (carrying the 2040's status hash), the staging folder is removed and no run folder exists under the run id. Staging older than 24 hours is swept by the next copy and reported by `backup-status` (`replica_incoming_stale`).
* **Never overwritten:** a folder under the run id that is not the primary's verified run (another status hash, or one that does not verify) is `replica_conflict` (2041) and stays as evidence.
* **Exit codes (D-186):** worst of the run's code, this run's replication code and every destination's post-copy contribution; 3 for an invalid or unsafe configuration (checked before anything is written), 4 for `destination_busy`.
* **Tests:** unit `ReplicaCopierTests` over real signed HBE1 run folders without a database (byte-for-byte copy, 2040 before the rename, identical copy re-verified, conflict, flipped byte, flush error, failed publish, simulated crash, audit failure, unreachable destination, later recovery proof, damaged primary, lock object, replication code); PostgreSQL `BackupReplicaCopyTests` (8): two destinations through the binary with 2040 twice and the same severity as `backup-status`, an unreachable destination (exit 1, 2041), lagging and catch-up oldest first and newest first, a publish failure, a flipped byte, a crash and a conflict, an unencrypted run never copied, destination locking, overlapping and invalid configurations refused with 2043.

### Owner decisions this slice relies on (not re-asked)
D-080(2) and D-080(6), D-085, D-087, D-088. D-081 (cutover) and D-086 (retention) belong to slices 7 and 6.

## Slice 6: retention and deletion of backup runs (D-086) - PLAN (docs only)
Sources:
* owner decision D-086 (retention and deletion), which this slice implements exactly;
* the related owner decisions D-082 (removed-attachment retention, register item K), D-080(6) (independent approval for key retirement and restore), D-081 (cutover, slice 7) and D-085 (encrypted backups);
* lead decision D-114 (attachment-retention holds on backups, `docs/PHASE-R-attachment-retention.md` section 8);
* SRS 16.

Builds on slices 4 and 5 (encrypted, signed runs; PR #128). Decision ids D-130..D-139 (allocated by the lead; technical defaults under D-048, chosen fail-closed). Risk: **Critical** (deleting a backup is irreversible).

### What D-086 requires, and where each requirement is met
| D-086 requirement | Met by |
|---|---|
| Daily: keep AT LEAST the latest 7; weekly: AT LEAST the latest 4; configuration may raise these, never lower them | D-132 (minimums; a lower value is a configuration error, never clamped) |
| Month-end and fiscal-year-end backups follow a company-approved legal policy; the default is no automatic deletion | D-131 (classification), D-133 (no legal policy is built in this slice: they are never deleted automatically); owner question Q1 |
| Classification by the company fiscal calendar and explicitly configured time-zone rules | D-131 |
| Never automatically delete the last recoverable backup, a backup under legal hold, or one needed by an active recovery chain | D-133 (recoverable), D-134 (legal holds, including D-114), D-135 (recovery chains) |
| Audit records, retention preview, safe-deletion validation | D-139 (audit 2014-2019), D-136 (preview and validation) |
| Automatic deletion inactive until the encryption, retention and restore safeguards are complete and independently tested | D-137 (three enforcement layers; activation only by an owner-instructed PR) |

### Scope
* **In scope:**
  * classification and keep rules;
  * the retention preview with a reason for every run;
  * backup legal holds and recovery-chain pins;
  * safe-deletion validation;
  * the deletion executor, built but INACTIVE (D-137);
  * the audit events;
  * the key inventory in the signed run status, for the key-retirement report (D-138).
* **Not in scope:**
  * the company legal policy for month-end and year-end runs (owner questions Q1-Q3; until answered those runs are never deleted automatically);
  * releasing a backup legal hold (it needs the Phase 4.1 named login and independent approval; until then a hold can be placed, never released);
  * off-site or second destinations (none exist);
  * standalone `backup-company` sets outside a backup root (operator-managed; never touched);
  * the cutover (slice 7);
  * key retirement itself (a report only).
* **One system migration** (backup holds and recovery-chain pins). Its number is requested from the lead when the implementation starts. Migration numbers are contiguous and taken in merge order.
* No API and no UI in this slice: the commands run through the administrative tool, under the interim mode of slice 5.

### Retention model
* **D-130, unit of retention: the whole run.** A run is one folder `<root>/<run id>/`: the system dump, one set per company, the signed `run.status.json`, the canary and possibly `recovery-test.json`. Retention keeps or deletes whole runs only.
  * Every set inside a run is listed in signed documents (the run status names its companies; the manifests name every file). Deleting one company set would make the run fail verification, so it is never done.
  * A run is KEPT when any rule keeps it, for any company in it or for the system part. It is eligible for deletion only when no rule keeps it for any part.
  * Standalone sets written by `backup-company` outside a root, kit material, key exports and every folder or file whose name is not a run id are never touched.
  * Alternative rejected: per-company deletion inside a run. It breaks the signed run, and a run is the unit of a full-installation restore.
* **D-131, classification (fiscal calendar and explicit time-zone rules).** Every complete run is classified separately for each company it contains, and for the system part.
  * **Instant of a run:** the run's window `[startedUtc, finishedUtc]`, read from the VERIFIED signed `run.status.json` (slice 5). Retention never decrypts anything and never needs the recovery private key.
  * **Time zones:**
    * Daily and weekly classification of a company uses the company's configured time zone (company settings).
    * Period-end and year-end classification uses the time zone recorded on the fiscal year (`FiscalYear.TimeZone`, fixed when the year is created).
    * The system part uses `Hasm__Backup__Retention__InstallationTimeZone`, which is required.
    * Ids are IANA ids resolved by .NET `TimeZoneInfo`.
    * **The server's local time zone is never used** (pinned by a test).
    * An unknown or unresolvable id makes the classification of that part `classification_unknown`, and the run is kept.
    * Review L1 of #130: a company time zone that is not configured, as well as an unknown or unresolvable one, gives `classification_unknown` and the run is kept.
  * **Boundaries:**
    * A local date turns into an instant at local 00:00 of the following day.
    * A local time skipped by a daylight-saving change becomes the first valid instant after it.
    * A repeated local time takes the EARLIER instant.
    * Conversion from an instant to a local date is unambiguous.
    * Unit tests use zones with a midnight daylight-saving change.
  * **Daily backup of a company:** on each local calendar day, the newest recoverable run (D-133) whose `startedUtc` falls on that day. Further runs of the same day are not daily backups.
  * **Weekly backup:** in each week, the newest recoverable run. The week is ISO 8601 (Monday start, ISO week-year); the start day is configurable (`Hasm__Backup__Retention__WeekStart`). This is a technical default, not a legal one.
  * **Period-end backup (D-086 "month-end"):** for every boundary B, the run is a period-end backup when it is either:
    * the newest recoverable run with `startedUtc < B` (the last one before the end); or
    * the oldest recoverable run with `finishedUtc >= B` (the first one after the end, which contains the final postings of the period).
    * The boundaries B are the end of every fiscal period of the company's fiscal calendar AND the end of every calendar month in the company's time zone.
    * Keeping both sides of every boundary is the fail-closed reading. Which one the law means is owner question Q2, and whether calendar months count in addition to fiscal periods is Q3.
    * A run whose window spans B is both.
  * **Fiscal-year-end backup:** the same rule at the end of every fiscal year of the company (`FiscalYear.EndDate` + 1 day, local 00:00 in the year's time zone).
  * **Gaps and unknowns:**
    * A run date not covered by any fiscal year of the company (no calendar configured, a gap, or later than the last year created) is `classification_unknown` for period-end and year-end, and the run is KEPT.
    * A company that is not Ready, not registered any more, or whose database cannot be read is `classification_unknown`, and all of its runs are kept (owner question Q5).
* **D-132, minimums (daily at least 7, weekly at least 4).**
  * `Hasm__Backup__Retention__DailyKeep` defaults to 7 and `Hasm__Backup__Retention__WeeklyKeep` to 4.
  * A value below 7 or 4, non-numeric, or above 10 000 is a configuration error (`retention_below_minimum` / `retention_config_invalid`, exit 3). It is **never clamped silently**.
  * The constants 7 and 4 are pinned by a unit test. Lowering them needs a new owner decision.
  * "Latest 7 daily" means the 7 most recent local days that HAVE a recoverable run of that company, each represented by its daily backup. These are not the last 7 calendar days, so missed days never shrink the history kept. The same applies to "latest 4 weekly".
  * Daily and weekly sets may overlap; a run is kept if either rule keeps it.
* **D-133, recoverable, and the last recoverable backup.**
  * A run counts as a recoverable backup of company X only when all of the following hold:
    * it is complete;
    * its `run.status.json` verifies with the server's signing public keys;
    * X's outcome in it is `ok` (`ok_missing_keys` does not count);
    * it is encrypted (no DEV/TEST acknowledgement);
    * its recovery key id has at least one PASSED recovery test on some run (`recovery-test.json`, slice 5).
  * The system part counts when the run's verdict is not `failed`, under the same signature, encryption and recovery-test conditions.
  * Runs that are not recoverable never COUNT toward a minimum, and they are not deleted automatically either:
    * incomplete or unreadable runs (as in slice 4);
    * runs whose status does not verify (`status_unverifiable`: for example a signing key removed after a compromise; kept as evidence and reported);
    * unencrypted DEV/TEST runs;
    * runs encrypted to a recovery key that was never tested.
  * Hard keeps, independent of the counts:
    * the newest recoverable run of every company and of the system ("last recoverable backup");
    * the newest run with a passed recovery test for each recovery key id that a retained run uses (so deletion never turns `backup-status` back to `recovery_untested`, and the key stays proven);
    * the newest complete run overall.
  * **Month-end and year-end runs are never deleted automatically in this slice** (D-086 default, `legal_policy_absent`). Building the company legal policy waits for owner questions Q1-Q3.
* **D-134, legal holds.**
  * **Backup legal holds (new):** a row in a system table `backup_legal_hold`, append-only (guard trigger; never deleted or updated except the release columns once).
    * Scope: one run (`run id`), or one company with an optional date range of run start (`company id`, `from`, `to`; open ends allowed). A reason is required; the placing operator and the time are recorded.
    * Placing is protective and immediate: `backup-hold-place (--run <id> | --company <id> [--from <date>] [--to <date>]) --reason <text>` writes audit 2018.
    * **Releasing is not available in this slice** (`hold_release_unavailable`). It needs a named administrator and independent approval (two persons), which the administrative tool gets only with the Phase 4.1 login and approval framework. Audit code 2019 is reserved for it. Until then a hold is permanent; an erroneous hold costs storage only.
  * **Holds from attachment retention (D-114, D-110):**
    * A hold on an already-purged attachment marks the retained backup sets that contain it as held. Until the Phase R backup inventory exists (R4), D-114 says every retained backup set counts as held: while the company's attachment-retention schema has any such hold and the inventory is unavailable, every run is kept (`d114_hold`).
    * **Default (owner question Q4):** ANY active attachment legal hold of a company (company-wide, record or attachment scope) keeps every run that contains that company (`attachment_hold`).
    * A company whose hold tables do not exist yet (migration level below Phase R) can have no holds. A company whose hold tables exist but cannot be read keeps all of its runs (`hold_check_failed`).
  * Holds are read at preview time AND again immediately before each deletion.
* **D-135, active recovery chains.** Every run is a full, self-contained backup (no increments), so a chain is the run plus what is needed to read and use it. A run is pinned while any of these holds:
  * a trial restore or a recovery test from it is running. These now take a shared advisory lock (run-lock namespace of slice 4, object 1) for their duration, and deletion takes it exclusively, so the two are mutually exclusive;
  * a kept trial database references it (a `*.trial-*.json` file inside the run);
  * it is the newest recovery-tested run of a recovery key in use (D-133);
  * a row of the new system table `backup_retention_pin` references it and has not expired. Slice 7 writes these rows: the safety backup taken before a cutover (D-081), pinned for its rollback window. Slice 6 creates the table and honours it. Pins are append-only and expire only by their own `expiresUtc`.
  * If incremental backups are ever introduced, the base and every increment form one chain and are kept together. That would need a decision of its own.
* **Exclusions from deletion that are not keep rules:**
  * a run folder that contains an unexpected file (`run_not_clean`: anything except the slice 5 files and `recovery-test.json`);
  * a symbolic link anywhere in the run (`symbolic_link_refused`);
  * a run outside the resolved root.

  These are reported and kept.

### Preview, validation and deletion (D-136)
* **`backup-retention-plan <root> [--json <file>]`** (new; read-only for files; reads the system and company databases):
  * classifies every run and prints, per run, its id, its window, the class tags per company (`daily`, `weekly`, `period_end`, `year_end`, `unknown`), and either the keep reasons or `eligible`;
  * the JSON form is for monitoring and for an operator's record; it carries the plan hash (SHA-256 over the sorted eligible run ids and their status hashes);
  * it writes audit 2014 (counts by reason and the plan hash);
  * exit 0 with the plan printed, 3 configuration, 1 failure.
  * The slice 4 `backup-retention-preview --keep-last` stays as it is: a database-free count preview, documented as NOT the D-086 policy. Nothing is removed.
* **`backup-retention-apply <root>`** (new; INACTIVE, D-137) is the only deletion path, and it is the "automatic deletion" of D-086 (meant for the operating-system scheduler after `backup-run`). There is no manual deletion command, and no override for held, pinned, month-end, year-end or last-recoverable runs (owner question Q6). Every execution:
  1. checks the activation gate (D-137); while inactive it writes audit 2017 `retention_deletion_inactive` and stops, exit 3, deleting nothing;
  2. takes the slice 4 run lock (no backup runs meanwhile) and the restore lock exclusively (no trial restore or recovery test meanwhile), with bounded waits (`run_in_progress`, exit 4);
  3. **requires current, healthy backups:** `backup-status` of the root must be `ok` (exit 0). Never delete while backups are failing, stale, partial, untested or unreadable (`backups_not_current`);
  4. **clock sanity:** refuses when now is earlier than the newest run's `finishedUtc` or than the last deletion recorded (`clock_inconsistent`);
  5. computes the plan and applies the **mass-deletion brake**: it refuses when the plan would delete more than `Hasm__Backup__Retention__MaxDeletionsPerRun` runs (default 7, at most 31), or more than half of the complete runs (`deletion_volume_suspicious`, audit 2017). A changed clock, calendar or configuration must never empty a root in one night;
  6. for each eligible run, oldest first, **re-validates immediately before touching it:**
     * the status signature verifies, and its hash equals the hash in the computed plan;
     * holds and pins are re-read;
     * classification is recomputed over the CURRENT root;
     * the invariants still hold AFTER the simulated removal: every company and the system keep their last recoverable run and their minimums, and every recovery key in use keeps a tested run;
     * the run is clean (no unexpected files and no symbolic links), and its path is inside the resolved root.

     Any failure skips that run with audit 2017 and the reason; the others continue;
  7. **deletes audit-first and resumably:**
     * writes audit 2015 (run id, status hash, the keep-rule evaluation, operator or `scheduler`) and commits it;
     * copies `run.status.json` and its `.sig` to the tombstone folder `<root>/.retention/tombstones/<run id>/` (owner-only);
     * renames the run folder to `<run id>.deleting-<16 hex>`, so it is no longer a run;
     * deletes that folder recursively, refusing to follow links;
     * writes audit 2016 (files, bytes, tombstone path).

     A crash leaves a `.deleting-*` folder. It is never a run, the next execution completes it (audit 2016 with `resumed`), and `backup-status` reports it (`retention_deletion_incomplete`, exit 8). When audit 2015 cannot be written, nothing is touched;
  8. exit codes: 0 done (possibly nothing eligible), 8 done with skipped runs, 3 inactive or configuration, 4 a run or restore in progress, 1 failure.
* **Signed sets, slice 5 (what deletion does to them):**
  * Every run is signed on its own: there is no hash chain or reference across runs. Deleting a run therefore never invalidates the signature, manifest or proof of any other run, and every remaining run stays verifiable with the kit's trust list exactly as before.
  * Deletion touches only files inside the run folder. It never touches the signing keys, the trust list, the key exports or the recovery kit.
  * **The tombstone keeps the deleted run's signed `run.status.json` and its `.sig`.** These are small, contain no names, and are readable and verifiable with the same trust list. Together with audit events 2015 and 2016, which carry the SHA-256 of both files, they prove which authentic run existed and when it was deleted. A gap in the run history is therefore explained, never silent. Tombstones are never deleted by HASM (owner question Q7).
  * Deletion is not cryptographic erasure. The ciphertext was encrypted to the recovery key that also protects every other run (D-085), so a copy that survives elsewhere (storage remnants, a snapshot of the volume, an off-site copy made by the operator) stays decryptable by the key holder. Per-class or per-period recovery keys would allow erasure by key destruction; that would be a change of D-085, recorded as a possible follow-up for the owner, not proposed here.
* **Monitoring:** `backup-status` adds `retention_deletion_incomplete` (exit 8). A deleted run never counts toward `company_missed_runs`, which counts only consecutive complete runs.

### Automatic deletion stays INACTIVE: how it is enforced (D-137)
Three independent layers. Any one of them alone keeps deletion off.
1. **Code gate.** `BackupRetention.AutomaticDeletionActivated` is a compile-time `const bool` that is `false` in this slice. `backup-retention-apply` checks it first.
   * A unit test pins the value to `false`. Changing it is a code change in a reviewed PR, the **activation PR**.
   * **The agents never self-merge the activation PR:** it enables an irreversible production operation (CLAUDE.md), so it merges only on an explicit owner instruction recorded in the INTEGRATION-STATUS owner-decision register.
   * The activation PR must show, in its review record:
     * (a) the safeguards are complete:
       * encryption and signing (slice 5, merged);
       * this slice merged;
       * the restore safeguards: the slice 7 cutover with its safety backup pinned, plus the Phase R4 backup inventory, or the D-114 hold that keeps every run until it exists;
     * (b) they were independently tested:
       * an independent review of the deletion path;
       * a recorded drill: from a root with more than the minimums, a preview, then a deletion on a disposable copy, then a recovery test of a remaining month-end run that passes.
2. **Runtime prerequisites**, checked on EVERY execution even with the code gate on:
   * encryption configured and no DEV/TEST acknowledgement (`unencrypted_ack` refused);
   * the retention configuration is valid (D-132);
   * every hold source is readable;
   * the restore safeguards are detectable: the pin table exists (this slice's migration is applied) and the slice 7 cutover schema is present (`restore_safeguards_missing` otherwise);
   * backups are current and recovery-tested (step 3 above).
3. **Explicit opt-in per installation.** `Hasm__Backup__Retention__AutomaticDeletion=yes-delete-eligible-backups` (exact value; anything else is a configuration error). Absent means preview only.
   * A unit test pins that no repository configuration or installer template sets it, as for D-095.

**Where the gate is checked (review M1 of #130).** The code gate (1) and the opt-in (3) are checked inside the deletion executor itself, in the lowest-level routine that renames or deletes a run folder, not only in the command. The internal test seam is the only path that can supply a test-only override, it is reachable only from the test assemblies, and a unit test asserts that every non-test entry point refuses while the gate is off.

**What is tested while inactive:**
* `backup-retention-apply` refuses, writes 2017 and deletes nothing.
* The executor's validation and deletion steps are tested through an internal test seam, on disposable roots only. The seam is `internal`, visible only to the test assemblies; the command line has no path to it.
* A unit test calls every non-test entry point that reaches the executor (the command handler and the public service method) and asserts that each refuses while the gate is off, and that the executor's own rename and delete routine refuses without the seam's override.

Alternative rejected: not building the executor until activation. The activation PR would then introduce untested irreversible code at the last moment. Building it now, gated, lets review and tests cover it twice.

### Interaction with key retirement (register item K, D-082, D-080(6); D-138)
* **Key inventory in the signed status.**
  * `run.status.json` version 2 adds, per company, the attachment master-key ids its set needs (`keyIdsRequired`, today only inside the encrypted manifest). At run level it adds the audit key ids of the system and company streams and of every business stream in the run (read as key ids only).
  * These are key ids, not secrets, and the server can read them unattended without the recovery private key. Version 1 statuses (slice 5) stay readable; their key needs are `unknown`.
  * **Review M2 of #130:** A retained run's key needs are `unknown` when its status is version 1, does not verify (`status_unverifiable`), is missing or unreadable, or the run is incomplete; any `unknown` run makes the report false (`backup_inventory_incomplete`).
* **Key-retirement report** (consumed by the key-management phase and by Phase R4; retirement itself needs independent approval under D-080(6) and is not built here):
  * An attachment master key or audit key is retirable only when:
    * no live row needs it (the existing `oldKeysMayBeRetired` rule; for attachments, the D-114/R rule that a purged row still counts until R4);
    * AND no RETAINED run lists it;
    * AND no retained run has `unknown` key needs (version 1, unverifiable, missing or unreadable status, or an incomplete run; `backup_inventory_incomplete` makes the answer false: fail closed).
  * Runs that are held, pinned, month-end or year-end are retained, so their keys stay needed for as long as they are kept. **Keys follow the backups, never the reverse.**
* **Recovery keys (`rk-`):** the report lists, per recovery key id, the retained runs encrypted to it. An old recovery private key may be destroyed only when no retained run and no retained key export is encrypted to it (`recovery_key_still_needed`). The operator's kit procedure in `docs/DEVELOPMENT.md` is extended accordingly.
* **Signing keys (`sk-`):** retiring a signing key (kit status `retired` with a date) does not affect retained runs signed before that date, and deletion needs no private signing key. A COMPROMISED key's runs fail verification on a server whose key directory no longer trusts it. They are `status_unverifiable`: kept, never counted, and reported for the operator.
* **Key exports** live in the recovery kit, outside the root, and are never deleted by HASM. The preview lists, per retained run, the recovery key id and the key ids it needs, so the operator knows which kit material must be kept.
* **Deletion never retires, destroys or rewraps a key.** It is the step that may later make a key retirable. The order is always: runs deleted (audited), then the report, then independent approval, then retirement.

### Audit events (system stream, block 2014-2019; D-139)
| Code | Event | Detail (codes, ids, counts and hashes only; never names or paths of databases) |
|---|---|---|
| 2014 | `BackupRetentionEvaluated` | operator, root tag (SHA-256 prefix of the resolved root), run counts by keep reason, eligible count, plan hash |
| 2015 | `BackupDeletionStarted` | run id, status hash, signing key id, recovery key id, the rule evaluation, operator or `scheduler` |
| 2016 | `BackupDeleted` | run id, status hash, files, bytes, tombstone written, `resumed` flag |
| 2017 | `BackupDeletionRefused` | run id or `*`, reason (`retention_deletion_inactive`, `backups_not_current`, `deletion_volume_suspicious`, `clock_inconsistent`, a validation code) |
| 2018 | `BackupLegalHoldPlaced` | hold id, scope, run id or company id and date range, reason length (the reason text is stored in the hold row, not in the event) |
| 2019 | `BackupLegalHoldReleased` | reserved; release is not available until the Phase 4.1 approval framework |

* Persisted codes, never renumbered; the registry is checked again before use.
* This uses the last six codes of the 2010-2019 block that slice 5 reserved for slices 6 and 7. **Slice 7 requests a new block from the lead.**
* Every command fails closed when its event cannot be written. For a deletion, nothing is touched without event 2015.

### Commands (summary)
* `backup-retention-plan <root> [--json <file>]`: preview, audited; usable while deletion is inactive.
* `backup-retention-apply <root>`: INACTIVE (D-137).
* `backup-hold-place (--run <id> | --company <id> [--from <yyyy-mm-dd>] [--to <yyyy-mm-dd>]) --reason <text>`.
* `backup-hold-list`.
* No release command (D-134).
* Interim mode: operating-system access, as for the slice 5 key commands (the dated limitation of RELEASE1-ACCEPTANCE G covers it). The implementation adds a dated limitation "automatic backup deletion inactive (D-086, D-137); backups accumulate and storage is managed by the operator" through the roadmap generator.

### Exact tests (implementation PR)
* **Unit tests:**
  * Time zones:
    * instant-to-local-date conversion and boundary construction, including a zone with a daylight-saving change at midnight (skipped and repeated local midnight);
    * a pin that `TimeZoneInfo.Local` is never used;
    * an unknown zone, and a company whose time zone is not configured, give `classification_unknown`, and the run is kept (L1).
  * Fiscal calendar:
    * monthly periods, a non-calendar fiscal year, 13 periods and 4-4-5 periods;
    * the last-before and first-after rule at every boundary, a run spanning a boundary, calendar month-ends added;
    * a date outside every fiscal year gives `unknown`, and the run is kept.
  * Minimums:
    * 7 and 4 pinned; 6 and 3 refused (exit 3, never clamped); higher values honoured;
    * days without a run do not shrink the kept history;
    * several runs per day: only the representative counts.
  * Recoverable runs (D-133):
    * `ok_missing_keys`, unverifiable, unencrypted, untested-key and incomplete runs never count and are never deleted;
    * the last recoverable run per company and for the system is kept;
    * the newest tested run per recovery key is kept.
  * Holds and pins:
    * run, company and date-range holds;
    * the D-114 rule (any purged-attachment hold without the inventory keeps every run);
    * an attachment hold keeps the company's runs;
    * an unreadable hold source keeps everything;
    * a pin until `expiresUtc`;
    * a kept-trial file inside a run pins it.
  * Validation:
    * the brake (count and half);
    * `backups_not_current`, `clock_inconsistent`;
    * `run_not_clean`, a symbolic link, a path outside the root;
    * a status hash that changed since the plan.
  * The gate:
    * `AutomaticDeletionActivated == false` pinned;
    * the opt-in variable absent from every repository configuration;
    * apply refuses with 2017 and nothing deleted;
    * every non-test entry point refuses while the gate is off, and the executor's own rename/delete routine refuses without the seam's override (M1).
  * Executor (internal seam, temporary roots):
    * audit-first ordering (an audit failure deletes nothing);
    * tombstone written and verifiable;
    * rename then delete;
    * resume of a `.deleting-*` folder;
    * `backup-status` reports an incomplete deletion.
  * Key inventory:
    * a version 2 status carries the ids; version 1, an unverifiable status, a missing or unreadable status and an incomplete run each give `unknown` (M2);
    * any `unknown` run makes the report false with `backup_inventory_incomplete`;
    * a key needed only by a held month-end run is not retirable.
* **PostgreSQL** (new class, about 7 tests; the registry is raised at push time and the lead is told the count):
  * a preview over real runs with companies whose fiscal calendars and time zones differ, and the classes asserted;
  * holds placed with audit 2018, and honoured; release refused;
  * an active attachment-retention hold (when the Phase R tables exist on main; otherwise the "no tables, no holds" path) keeps the company's runs;
  * apply refuses while inactive (2017, nothing deleted);
  * the executor through the seam deletes exactly the eligible run of a disposable root (2015, 2016, tombstone), and a recovery test of a remaining run still passes;
  * apply refuses while the run lock or the restore lock is held;
  * the migration's append-only guards on the hold and pin tables.

### Decisions (D-130..D-139, recorded in `docs/DECISIONS.md`, status planned)
D-130 unit of retention; D-131 classification; D-132 minimums; D-133 recoverable runs and hard keeps; D-134 legal holds; D-135 recovery chains and pins; D-136 preview, validation and deletion; D-137 inactive enforcement; D-138 key inventory and key retirement; D-139 audit codes.

### Owner questions (legal or retention policy; not decided here; the safe default applies until answered)
* **Q1. Legal policy for month-end and fiscal-year-end runs:**
  * the retention period;
  * who in the company approves it (author and approver distinct, as for attachment retention D-108);
  * whether an approved policy may ever let HASM delete such runs automatically.

  **Default: never deleted automatically; no policy model is built until answered.**
* **Q2. Which run is "the" month-end backup:** the last one before the boundary, or the first one after it (which contains the final postings)? **Default: both are kept.**
* **Q3. Calendar month-ends in addition to fiscal period ends** (they differ for 4-4-5, 13-period or quarterly calendars). **Default: both kinds of boundary are kept.**
* **Q4. Do attachment legal holds extend to backups?** D-114 covers holds on already-purged attachments. **Default: any active attachment hold of a company keeps every run containing that company.**
* **Q5. Runs of companies that are no longer Ready or registered** (closed, archived, failed). **Default: all of their runs are kept.**
* **Q6. An authorised manual deletion path** for protected runs (for example an erasure obligation that reaches backups). **Default: none; protected runs cannot be deleted through HASM.**
* **Q7. How long tombstones and the deletion audit are kept.** **Default: forever.**

**Owner decisions 2026-11-21 (consolidated package; recorded once in `docs/DECISIONS.md`, where the owner's wording stands; this block only points to it and does not rewrite the plan).** Slice 6: Q1 = D-225 (month-end and fiscal-year-end backups follow a company-approved legal retention policy with independent approval; without one they are retained indefinitely; the policy model is new work). Q2-Q7 = D-226 (the defaults above are confirmed). 4.3-ACT = D-227: automatic deletion STAYS DISABLED; recording these answers enables nothing, and no production deletion is authorised.

Each question's default is the preserving one. An answer that relaxes one is an owner decision recorded in the register, and its implementation follows the PR rules.

### Owner decisions this slice relies on (not re-asked)
D-086 (implemented exactly; nothing lowered), D-082 and item K, D-080(6), D-081 (pins only; the cutover is slice 7), D-085, D-114.

### Recorded as built (implementation of slice 6; branch `feature/phase-4-3-slice6-impl`, stacked on #128)
* **Built as planned:**
  * `RetentionOptions` (D-132): minimums 7/4 pinned; a lower or unreadable value is refused and never clamped; `InstallationTimeZone` required.
  * `RetentionTime` (D-131): IANA zones through `TimeZoneInfo`, never the local zone (pinned by a source scan); skipped local midnight = first valid instant after it, repeated local midnight = the earlier instant.
  * `RetentionClassifier` (D-130..D-135): every keep reason listed in the operator section of `docs/DEVELOPMENT.md`. An unconfigured, unknown or unresolvable company time zone, an unreadable company and a date outside every fiscal year are `classification_unknown` (review L1 of #130).
  * `RetentionInventory`: the root scan reads only VERIFIED signed statuses. It reports unclean runs, kept trials, symbolic links and unfinished deletions.
  * Run status version 2 with the key inventory (D-138): per-company attachment key ids and the run's audit key ids. A run whose inventory cannot be read completely is written as version 1. Key needs are unknown for a version 1, unverifiable, missing or unreadable status and for an incomplete run, and any unknown retained run makes the inventory incomplete (review M2 of #130).
  * The system tables `BackupLegalHold` and `BackupRetentionPin` (one migration, append-only guards, derived names) and `BackupHoldStore` (placing a hold writes audit 2018 in the same transaction).
  * `RetentionService.PlanAsync` / `backup-retention-plan` (audit 2014, JSON plan) and `RetentionService.ApplyAsync` / `backup-retention-apply` (inactive: audit 2017, exit 3).
  * `RetentionExecutor` (D-136): reachable only through the internal `RetentionGateOverride` test seam.
  * The shared restore lock in trial restores and recovery tests (D-135).
  * `backup-status` reports `retention_deletion_incomplete` (exit 8).
* **Review M1 of #130, as built:**
  * `BackupRetention.GateOpen` (code gate AND opt-in) is checked in `ApplyCoreAsync`, in `RetentionExecutor.ExecuteAsync`, and again in `RenameForDeletion` and `DeleteRunFolder`, the lowest-level routines that touch a run folder.
  * `RetentionGateOverride.ForTests()` is `internal` (visible only to the test assemblies), and a unit test scans `src/` to confirm that no production code calls it.
  * A unit test calls every non-test entry point and asserts the refusal while the gate is off.
* **Layer 2 as built:**
  * `RestoreSafeguards.CutoverAvailable` is `false` until slice 7, so `restore_safeguards_missing` refuses every non-test execution even after the code gate changes.
  * The test seam also stands in for this one check, because the slice 7 safeguards cannot exist yet.
* **Phase R attachment holds:** the company schema has no attachment-hold tables yet, so no such hold can exist. As soon as tables named `AttachmentLegalHold*` or `AttachmentRetention*` appear in the dictionary, every company is `hold_check_failed` until retention reads them (fail closed).
* **Migration:** system migration `0032_backup_retention.sql` (lead allocation: #133 took 0031 and merged first, so the sequence is contiguous). No test pins its number; the pinned system-migration counts (`SystemDatabaseTests`, `CompanySchemaTests`) are 32.
* **Review H2 of #134 (fixed):** the attachment-hold tripwire looked for keys that R1 never used, so active Phase R legal holds would have been ignored (fail open). Retention now reads R1's company `LegalHold` table. ANY active hold (company, record or attachment scope) keeps every run containing the company (`attachment_hold`). A hold table that cannot be read keeps everything (`hold_check_failed`). A company whose migration level predates R1 has no hold table and so can have no hold.
  * The exact R1 keys are pinned by a unit test. Any other company-schema table whose name contains "Hold" trips `hold_check_failed` for every company until retention reads it.
  * A PostgreSQL test places a real record-scoped R1 hold and shows that every run is kept and the engine deletes nothing.
* **Activation prerequisites (review L1-L4 and L6 of #134; deletion is inactive, so they must be resolved before any owner-approved activation, and are listed with the 4.3-ACT register row). L1, L2 and L6 are resolved by the follow-up PR `fix/phase-4-3-retention-activation-prereqs`; deletion stays INACTIVE:**
  * **L1 (fixed in the follow-up PR):** `DeleteRunFolder` re-checked for symbolic links only once per entry while walking, so a link swapped in between the check and the delete (time-of-check to time-of-use) was not excluded.
    * Now every directory is re-checked FRESH immediately before it is enumerated and before it is removed, and the directory holding each file immediately before the file is deleted. Each must not be a link and must still resolve INSIDE the deleting folder (real path).
    * Otherwise the walk stops with `path_escaped_deleting_folder`, which the executor records as a refusal (2017) for that run, leaving the remainder for the operator. A link entry itself is still only unlinked.
    * Residual: the window between the last re-check and the system call is minimal but not zero, because .NET has no descriptor-relative delete. The tree is owner-only (0700) and owned by the service account, so swapping an entry needs that account.
    * **Mount points are not detected:** a bind mount or any other mount inside the deleting folder is not a link, so the walk would descend into it (review L-d of #156). Creating a mount needs administrative (root) rights. The operator runbook keeps mounts out of the backup root.
    * Test: a directory swapped for a link between the enumeration and the descent leaves the link's target untouched.
  * **L2 (fixed in the follow-up PR):** audit 2016 was not resumable: a crash after the folder was gone and before 2016 left 2015 without its 2016.
    * Now a completion marker (`completed.json`) is written into the tombstone after each 2016.
    * Each execution finds every tombstone that has the deletion marker but no completion marker, whose run folder and deleting folder are both gone. For each, it writes the missing 2016 (`resumed=yes recovered=audit folder=absent`: the folder may also have been removed outside the engine; review L-d of #156) and then the marker.
    * The 2016 is written EXACTLY once: it is skipped when the system stream already holds a 2016 for that run, which covers a crash between the event and the marker.
    * A tombstone whose run folder still exists (a crash before the rename) is not treated as a completed deletion.
  * **L6 (fixed in the follow-up PR; review of #134 by Agent C):** `AcquireRestoreLockAsync` returned the connection holding the shared restore lock, so the lock was released only when that unpooled connection closed, which PostgreSQL does asynchronously. A deletion right after a trial restore or recovery test could therefore be refused (`restore_in_progress`; fail closed).
    * Now it returns a `RestoreLockHolder` whose disposal runs `pg_advisory_unlock_all` before closing the session.
    * Test: the exclusive lock is free immediately after the holder is disposed.
  * **L3 (fixed now):** an interrupted deletion is resumed only when the engine's own tombstone (written after audit 2015) exists; otherwise the folder is left for the operator (`resume_unverified`, audit 2017).
  * **L4 (fixed now):** a refusal for a missing runtime prerequisite (encryption, configuration, restore safeguards) writes audit 2017 too.
* **Review L5 of #134 (fixed):** a LegalHold table that is missing in a company whose migration journal already reaches the migration that creates it (dropped or renamed) is `hold_check_failed`, never "no hold"; that migration's version is derived from the embedded company scripts.
* **Tests:**
  * unit tests: `RetentionClassifierTests`, `RetentionExecutorTests`, `BackupRetentionSchemaDerivationTests`, and the strengthened TOTP pin;
  * PostgreSQL class `BackupRetentionTests` (6, including the R1 hold test of review H2): the preview per company calendar and time zone, holds and guards, inactive deletion, the engine through the seam with a remaining run that still recovers, and the locks.
* **Not built here:** the company legal policy for month-end and year-end runs (owner questions Q1-Q3), hold release (Phase 4.1), the slice 7 pins and safeguards, and activation (an owner-instructed PR).

## Slice 7: live company restore (cutover) with acknowledgement, independent approval and a new audit checkpoint epoch (D-081) - PLAN (docs only)
Sources:
* owner decision **D-081**: a live company restore requires explicit acknowledgement, independent administrative approval and comprehensive audit. Cutover creates a new audit checkpoint epoch while the previous audit chain is preserved and verified; epoch rotation never conceals earlier audit failures or bypasses tamper detection. Required: pre-cutover validation, a secure backup taken before cutover, consistent database and attachment restoration, runtime permissions, a rollback plan and post-restore integrity checks;
* **D-080(6)** (independent approval for restore operations) and **D-118** (trial restore and the recovery test are restore operations);
* the Phase 4.1 independent-approval framework (`docs/PHASE-4.1.md` section 7, slice S4: operation `restore.execute`, "the 4.3 slice integrates it"; S8 wires it);
* D-099 (named administrator with MFA in the administrative tool), D-102 (notifications), D-106, D-107 and D-119 (restore effects on credentials);
* the code of slices 3-6: `CompanyBackupService.TrialRestoreAsync`, `EncryptedSets`, `SignedDocuments`, `RecoveryTestService`, `BackupRetentionPin`, `RestoreSafeguards.CutoverAvailable`, `AuditTrail` / `AuditVerifier` / `FileCheckpointStore`.

Decision ids D-143..D-153 (allocated by the lead; D-152 and D-153 added after review round 1 of #143; technical defaults under D-048, chosen fail-closed). Audit block 2020-2029 (allocated). **Depends on #134** (slice 6): `RestoreSafeguards.CutoverAvailable` and the `backup_retention_pin` table come from it (review L5 of #143). Risk: **Critical**: it replaces live financial data and changes the audit-integrity mechanism.

### Scope
* **In scope:** the restore of ONE company's database and attachment folder from an encrypted, signed company set (slice 5), replacing the live ones.
  * the request with a typed acknowledgement;
  * the independent approval through the Phase 4.1 framework;
  * pre-cutover validation and a pinned, encrypted safety backup;
  * the switch, which is journaled, resumable and reversible;
  * runtime permissions and post-restore integrity checks;
  * the new checkpoint epoch of the company's business audit stream;
  * the rollback window;
  * the system and business audit events.
* **Not in scope (later slice 8, "full-installation restore"):**
  * restoring the SYSTEM database or a whole run;
  * its credential effects (D-106, D-107, D-119: logins blocked until post-backup revocations are re-applied, and an installation-id change makes users `RecoveryRequired`);
  * system-stream epochs.

  A company cutover changes no user, credential, session, membership or role, and no EFFECTIVE permission. Its only writes to the system database are:
  * its own records: the `restore_cutover` journal, the registry state, the retention pin and the system audit events;
  * the soft revocation of scoped role assignments that are ALREADY INERT in live (D-152, step 3.5). This removes nothing anyone can use.

  Users, credentials, memberships, roles, the registry row and the company's access stream (`company:<id>`) otherwise stay live. So D-106 and D-107 are NOT triggered by this slice. This is stated, not assumed: a test pins that a cutover writes nothing to the user, session or credential tables, and that its only role-assignment changes are the D-152 revocations (review M7 of #143).
* Unencrypted (DEV/TEST) sets are never cut over, whatever the acknowledgement: `unencrypted_set_refused`.
* No UI in this slice (administrative tool, like the other 4.3 commands). The API exposes nothing new.

### Dependencies and the gate (D-144)
* **The independent approval comes ONLY from the Phase 4.1 framework** (`admin_approval_request`, operation `restore.execute`, approver independent of the requester, MFA-fresh, bound to `payload_hash`, single use, expiry, restore-epoch expiry). This slice adds no second approval mechanism.
* **`RestoreSafeguards.CutoverAvailable`** (slice 6, today `false`) is the code gate of every cutover entry point. It is also layer 2 of the slice 6 deletion gate. It stays `false` until ALL of these are on main:
  * this slice's engine;
  * the 4.1 approval framework (S4) with the D-099 named-administrator login in the administrative tool;
  * the S8 wiring of `restore.execute`.

  Until then every cutover command refuses (`restore_cutover_unavailable`, audited 2027). The engine is tested through an internal test seam, as in slice 6 (the gate is checked in the lowest-level switch routine too; review M1 of #130 applies).
* **Implementation split:**
  * **7a** builds the engine, the epochs and the journal behind the gate. It can merge before S4.
  * **7b** (with 4.1 S8) wires `restore.execute` and the named login.
  * Flipping `CutoverAvailable` is its own PR. Agents never self-merge that PR: it needs an explicit owner instruction (owner question Q3) and a recorded drill.

### The procedure
1. **Request** (`restore-cutover-request <set-dir> <company-id> --recovery-key <file> --signing-trust <file> [--passphrase-stdin]`, by a named administrator; D-099):
   * The set is verified as in slice 5: signatures, stored digests, run id. Then it is restored into a NEW, unregistered database and a staging attachment folder ON THE SAME FILESYSTEM as live storage (the rename in step 3.7 must be atomic), using the slice 3 trial-restore path, KEPT.
   * The copy is migrated FORWARD to this installation's company level when it is older (Q5 default; a newer set is refused, as today).
   * **What-would-be-lost pre-checks (D-153; F1-F3):** the lost-tail classification, the numbering comparison (counters and issued and voided numbers) and the fiscal-state comparison of step 3.6 run here against live; any accounting loss refuses the request by default (Q7, Q9).
   * **Live chain pre-check (Q4 default):** a live business stream that does not verify refuses the request.
   * The slice 3 checks run against the LIVE system database: shared links, attachment rows against files, migration compatibility. The copy's business audit stream is verified as an authentic PREFIX of the live stream's checkpoints (D-147); a copy that is not a prefix is refused (`restore_set_not_a_prefix`).
   * **Typed acknowledgement:** the requester types the company id, the source run id (or set id) and the phrase `replace live company data`. Its SHA-256 is recorded.
   * An approval request (`restore.execute`) is filed with a canonical payload:
     * company id;
     * the set's signed manifest hash and run id;
     * the copy's database tag and migration level;
     * the restored business-stream head (seq, hash);
     * the live stream's verification verdict and head at request time;
     * the acknowledgement hash.
   * Audit 2020. Nothing live has changed. The request expires (framework default); an expired request drops its prepared copy through the slice 3 kept-trial path (audit 2028).
2. **Independent approval** (4.1 framework): another named administrator, MFA-fresh, approves the exact payload. Audit: the framework's own events.
3. **Execute** (`restore-cutover-execute <request-id>`, by a named administrator, attributed). Under the company provisioning lock, the backup run lock and the restore lock (no backup, retention, trial restore, migration or runtime grant of this company meanwhile):
   1. **Re-validate and CLAIM the approval (review M1 of #143):**
      * the approval is valid and unconsumed, and the payload hash matches;
      * the prepared copy is unchanged (its tag and its business-stream head);
      * the live company is Ready;
      * the gate is open.

      Then, in ONE system transaction under the framework's row lock, the request is claimed: `restore_cutover` becomes `Switching`, with a unique constraint of one cutover per approval request, and the approval is marked consumed (`Executed` in the framework). Audit events 2021 (approval consumed) and 2022 (started) are written in that same transaction.
      * From the claim on, the cutover FINISHES or REVERSES whatever the expiry. Expiry only stops claims that have not started.
      * A reversed cutover leaves its approval consumed, and a retry needs a NEW request and approval.

      This deviates from the PHASE-4.1 section 7 rule that "a failed execution leaves the request `Approved` and retryable": a cutover is a multi-step operation whose first steps change live state, so retrying under the same approval is not safe. The deviation is recorded here as part of the S8 contract for `restore.execute`, and S8 implements it.
   2. **Block the company:** a new registry state `Restoring` (system migration). The API resolves only `Ready` companies, so every request now gets the generic `503`. The engine waits out the 15-second state cache (production blocker 5) and then terminates the remaining sessions of the live database (`pg_terminate_backend` for that database only).
   3. **Verify the live business stream** (read-only `AuditVerifier`) and record its verdict and head (V_old, seq_old, hash_old). A verdict other than Verified stops the cutover here (Q4 default; `live_chain_not_verified`, audit 2027, then the reversal of step 2). Whatever the owner decides for Q4, a failed verdict is never hidden: it is recorded in the epoch (D-147).
   4. **Safety backup (D-146):**
      * an ENCRYPTED, signed `backup-company` of the live company into the backup root's cutover folder;
      * pinned in `backup_retention_pin` with a NON-EXPIRING pin kind (kind 3, `lost_tail_evidence`) until the owner decides otherwise (review M2 of #143). It holds the only copy of the lost tail of the parent epoch, so its pin never lapses with the rollback window;
      * its run id recorded.

      If it cannot be taken, the cutover stops before anything is switched (owner question Q1 for a live database that is lost).
   5. **Company-scope revocations stay applied (D-152, review H1 of #143).**
      * Branches, warehouses and cost centers carry an `ActivationEpoch`. The system-side scoped role assignments record the epoch they were made under. An assignment is effective only while its entity is active AND its epoch equals the recorded one.
      * A restored copy carries the OLDER epochs and active states, so a grant made inert by a change after the backup point would come back to life.
      * **Mechanism: system-side revocation of inert scoped assignments.** This is a lead decision under D-048 (review M7 of #143). It replaces the earlier in-copy deactivate/reactivate cycles, which collided with company safeguards: the immediate main-branch CHECK of 0004 (M6), the cost-center tree trigger of 0008 (M6) and the negative-obligation warehouse trigger of 0020 (M7). It is security-preserving: there is no company-database write, no trigger bypass and no new migration.
        * **When:** after quiescing, under the company provisioning lock, after the step 6 checks have passed and immediately before the switch (step 7).
        * **What:** the engine reads every branch, warehouse and cost center of the LIVE database: its epoch and its active flag. It then soft-revokes on the system side every not-yet-revoked branch-, warehouse- and cost-center-scoped assignment of the company that is NOT effective in live:
          * its recorded epoch differs from the entity's live epoch; or
          * the entity is inactive in live; or
          * the entity is missing from live.
        * **The inactive case is needed for cost centers.** A cost-center epoch is bumped only on REactivation (0009). So a cost center deactivated after the backup keeps its epoch: its grants are inert in live only because it is inactive, and they would revive in a copy where it is still active.
        * **How:** the existing soft revocation (revoked at / revoked by; rows are never deleted), attributed to the executing named administrator (D-099).
        * **Why this changes no effective permission:** every revoked assignment is already PERMANENTLY inert in live. A reactivation always bumps the epoch, so none of them could ever become effective again in live. The impact is that inert grants disappear permanently, which is already their effect.
        * **What remains:** only assignments effective in live (recorded epoch = live epoch, entity active). After the cutover such an assignment stays effective only where the restored entity has the same epoch and is active; that entity has not changed state since the backup point. Where the restored epoch differs, the assignment becomes inert. This fails closed: administrators re-grant.
        * **Cost-center coverage (self-review of the redesign).** A cost-center grant covers the cost center AND its descendants in the CURRENT tree, and cost centers can be moved (Phase 3.2C). So a restored tree can place under a granted cost center a descendant that live had moved out. **Default: REFUSED** when an assignment that stays effective would cover, in the copy, a cost center it does not cover in live (`cost_center_scope_widened`). A refusal changes no permission, and the alternative of revoking a grant that is effective in live is left to the owner (Q8).
        * **Atomicity and journal:** the revocations, the journal step's `done` record and ONE system event per cutover, **2029 `RestoreInertScopedAssignmentsRevoked`**, are written in ONE system transaction. The event lists the assignment ids and, per entity, the entity id, the recorded epoch and the live epoch (ids and numbers only). On resume, a step without `done` was rolled back and is recomputed from the live state; a step with `done` is not repeated.
        * **Reversal:** a later reversal of the cutover (step 9, resume BACK) does NOT un-revoke. The revoked rows were inert in live, the old database is live again, and they stay inert.
        * **Assignment writes are blocked meanwhile:** the company is `Restoring` (the API resolves only `Ready` companies), and the implementation refuses any assignment write of a company in `Restoring` (tested). The step 9 post-check re-evaluates at switch time.
        * **Any failure refuses the cutover** (`inert_assignment_revocation_failed`, audit 2027), and so does **a live epoch or active flag that cannot be read** (`live_epoch_unreadable`). A live database that cannot be read never yields "no grants to protect".
        * **No company-database writes in this step:** the copy's business stream keeps its restored head seq_r, and its branch, warehouse and cost-center rows are unchanged. So a company with an open negative-stock obligation (company migration 0020) is cut over normally (M7).
      * This applies D-106's principle ("revocations after the backup point stay applied") at company scope. It is a security-preserving default, not new policy, and is listed as owner question Q8 with this default.
   6. **What the restore would lose (D-153, review H2 of #143 and the accounting review F1-F4 by Agent F; owner questions Q7 and Q9).** The copy rolls back everything the live company did after the restore point. That is accounting policy, so it is the owner's decision. Until the owner decides, the preserving defaults below REFUSE the cutover whenever the restore would lose accounting history.
      * **Quiesced comparison (F4).** The comparisons below run at request, and again at execution AFTER quiescing (registry `Restoring`, sessions terminated, `ALLOW_CONNECTIONS false`, under the company provisioning lock), and once more immediately before the switch. A number issued, or a period closed, between request and switch is caught.
      * **Lost-tail classification (F1).**
        * The D-147 head comparison identifies the live business-audit events after the restored head (seq_r+1..seq_old).
        * Each event type is classified by a code-defined table as ACCOUNTING or not. Accounting types are: number issue or void; journal post, reverse or cancel; cost operations; stock posting, reversal or adjustment; negative-stock settlements; fiscal period or year state; exchange rates; approval decisions. An event type the table does not know counts as accounting (fail closed).
        * **Default: REFUSED when the lost tail contains ANY accounting event** (`accounting_events_after_restore_point`).
        * The request shows the approver a per-type inventory of the lost tail (counts per event type), and that inventory is part of the `restore.execute` payload hash.
      * **Numbering (H2, F3).**
        * The live and the copy's `NumberCounter` AND `NumberIssue` rows are compared, including the voided flags, per series key (document type, branch, fiscal year).
        * A series key present only in live counts as advanced. A number voided in the lost tail (a cancelled approved document) would come back un-voided.
        * **Default: REFUSED on any difference** (`numbers_issued_after_restore_point`).
      * **Fiscal state (F2).**
        * Every fiscal period's and year's status and revision, the year-close and carry-forward state and the pending reopen requests are compared between live and the copy. A period closed (possibly reported or filed) after the restore point would otherwise come back Open.
        * **Default: REFUSED on any difference** (`fiscal_state_differs`; owner question Q9).
      * **System-side references to lost company data (warning).** Party-registry identities, links and proposals (the request outbox) that refer to a local party absent from the copy are reported (`party_reference_target_missing`, count), next to `shared_links_missing_locally` (slice 3) and `scope_target_missing` (L6).
      * **Note for operators: revision ETags and idempotency keys.**
        * Revision counters roll back with the copy, and all sessions are terminated. A client still holding a lost-tail ETag could later match a DIFFERENT revision with the same number (an ABA risk, Low); clients must reload after a cutover.
        * Client idempotency keys of lost-tail requests are forgotten. A retry creates the document again, which is desired, because it was lost.
      * **Owner options (not decided here).**
        * Q7 option "advance and void" for numbering is sound ONLY under these conditions (from Agent F's review):
          1. ROWS, not a jump: one issued-then-voided `NumberIssue` row per skipped number, with a reserved sentinel document id (the cutover id, never a real document) and the reason "lost in restore cutover <id>". No guard is weakened.
          2. The live maxima are taken per series key from the QUIESCED live database (F4), including keys only in live and voided numbers.
          3. The void ranges are listed in the request and in the approval payload hash.
          4. A post-check confirms every counter is at or above the live maximum and every skipped number exists as a void row.
          5. An operational re-entry procedure (owner or legal): documents of the lost tail that were communicated externally are re-entered under NEW numbers, cross-referenced to the voided ones, from the pinned safety backup.
          6. F1 and F2 still apply: voiding numbers fixes numbering only.
        * The Q9 option for fiscal state: re-apply the live closures to the copy before the switch. Never reopen anything.
   7. **Switch (D-145), journaled step by step in the new system table `restore_cutover`:**
      * Before the termination in step 2, `ALTER DATABASE <live> WITH ALLOW_CONNECTIONS false`, so no new session can arrive between termination and rename (review L2 of #143).
      * `ALTER DATABASE <live> RENAME TO <retained, generated opaque name>`, then `ALTER DATABASE <copy> RENAME TO <live name>`. The registry row, the runtime role name and the credential stay valid because the physical name is unchanged.
      * The runtime role's grants on the RETAINED database are revoked (CONNECT included), so the retained copy is reachable only by the administrator (L2).
      * The attachment folder: `rename(live, retained)`, then `rename(staging, live)`.
      * **Resume rules (review L1 of #143):** every step is recorded before (`intent`) and after (`done`) it runs. `restore-cutover-resume <id>` decides from the OBSERVED state, not from the journal alone: which database names exist, which folder names exist, `ALLOW_CONNECTIONS`, the grants.
        * Up to and including the database renames, the rule is BACK: restore the original names, re-allow connections, state Ready, audit 2024.
        * From the completed renames on, the rule is FORWARD: finish the grants, post-checks and epoch.
        * An epoch file that already exists for this cutover id is REUSED, never duplicated.
        * Never by hand.
   8. **Runtime permissions:** `grant-company-runtime` on the switched database (the restored copy has no grants: `--no-privileges`), then the runtime login and the schema gate are verified.
   9. **Post-restore integrity checks (read-only):**
      * the slice 3 checks again on the switched database;
      * the business stream verified as a prefix of the previous epoch (D-147);
      * every attachment row checked against its file;
      * the migration level and checksums;
      * **no revival (D-152):** every scoped assignment of the company that is effective against the switched database was effective in live at cutover time: recorded epoch = live epoch and the entity active in live. No cost-center assignment covers a cost center it did not cover in live;
      * **no numbering series is behind the live maximum, no issued or voided number differs, and the fiscal state equals live** (D-153; F2, F3);
      * **warning check (review L6 of #143):** system-side scope assignments that point at a branch, warehouse or cost center missing from the restored copy are reported (`scope_target_missing`, count). They are inert anyway.

      **Any failure here reverses step 7 from the journal** (the old database and folder are live again, the epoch is NOT rotated), sets the state back to Ready and writes audit 2024. The approval stays consumed (M1).
   10. **New checkpoint epoch (D-147):** the epoch-start checkpoint is written, then the business-stream event 2026 is the first event of the new epoch in the switched database. The system-stream events 2026 and 2023 carry:
      * the old and new heads;
      * V_old;
      * the epoch numbers;
      * the safety-backup run id;
      * the retained database tag.
   11. **Ready:** the state returns to `Ready`, `restore_cutover` becomes `Completed`, and audit 2023 (completed) is written in the same transaction. Notifications go out per D-102.
4. **Rollback window (D-149):**
   * The retained database, the retained folder and the pinned safety backup stay for the window (owner question Q2).
   * `restore-cutover-rollback <cutover-id>` is a restore operation with its own `restore.execute` approval, and it runs the FULL procedure (review M3 of #143):
     * live verification;
     * a safety backup of the outgoing (restored) company with its non-expiring evidence pin;
     * the revocation of inert scoped assignments (D-152, against the epochs and active flags of the outgoing live database) and the D-153 checks (lost-tail accounting events, numbering including voided numbers, fiscal state), after quiescing (review R1 by Agent F);
     * the journaled switch, KEEPING the outgoing database and folder as retained;
     * runtime grants and post-checks;
     * ANOTHER epoch whose parent is the epoch the retained database belongs to.

     Events written in between remain in the preserved epoch, in the outgoing database and in the system stream. Audit 2025.
   * The end of the window only expires the pin. Removing the retained database and folder is not built (owner question Q2).

### Checkpoint epochs (D-147): how the previous chain stays preserved and verified
Today a restored company database rolls its business stream back while the external checkpoints stay ahead (`tail_truncated`, and every later write is refused with `audit_stream_truncated`). An epoch is the controlled, audited answer: never an edit of checkpoint files.
* **Files:** the checkpoint file of a stream (one JSON-lines file per stream, named by a hash of the stream key) stays as it is and becomes epoch 0. Each cutover appends a NEW file `<hash>.e<n>.hcp` for epoch n. Old files are never modified, truncated or deleted.
* **The epoch-start checkpoint** is the first line of epoch n, a new checkpoint format version. It is authenticated with the checkpoint key and carries:
  * the epoch number and the parent epoch;
  * the restored head (seq_r, hash_r);
  * the parent epoch's LAST checkpoint MAC, so removing or replacing an epoch file breaks the chain between epochs;
  * the cutover id and the approval request id;
  * the parent epoch's verdict (V_old) with its failure codes.
* **Prefix rule:**
  * The restored stream 1..seq_r must equal the parent epoch's checkpointed history wherever a parent checkpoint exists at or below seq_r (same hash).
  * When the parent has NO checkpoint at or below seq_r, the prefix cannot be proven and the request is refused (`restore_prefix_unprovable`; review L3 of #143).
  * In addition, at request and before the switch, the restored head (seq_r, hash_r) is compared DIRECTLY with the live stream's record at seq_r (L3).
  * This is checked at request, again before the switch, and by every later verification. A restore cannot smuggle in a forged history (`restore_set_not_a_prefix` / `epoch_prefix_mismatch`).
* **Binding (review M4 of #143):** every epoch from 1 on must be bound to a `restore_cutover` row in state `Completed` or `RolledBack`, AND to the system-stream event 2026 carrying the same cutover id, approval request id and heads. `audit-verify` reports `Failed` (`epoch_unbound`) for an epoch without both. Writing an epoch file by hand therefore never yields a verified stream.
* **Verifier** (`audit-verify`):
  * It verifies the current epoch as today, plus the prefix rule and the inter-epoch chain.
  * It ALWAYS reports the history: `epoch 0: <verdict at rotation, its failure codes>; epoch 1: ...`.
  * A failed earlier epoch keeps the overall verdict at `Failed` (exit 6) until an auditor reviews it. The epoch record cannot clear it: "never conceals earlier audit failures".
  * The lost tail of the parent epoch (seq_r+1..seq_old) is preserved in TWO places: the pinned safety backup (non-expiring pin, M2), and the retained database while it exists (review M2 of #143). `audit-verify --epoch <n> --from-set <safety set>` or `--from-retained <cutover id>` verifies it against the parent epoch's file.
* **Runtime** (`AuditTrail`): before the first write of a transaction it reads the newest epoch file. A stream shorter than the newest epoch's last checkpoint is still `audit_stream_truncated`: an epoch is never inferred, only created by the cutover.
* **System and company streams are not rotated by this slice:** the system database is not restored.
* Alternative rejected: re-anchoring the restored head in the same file with `--establish-baseline`. It would make the restored state the trusted baseline and hide the lost tail; the anchor command refuses this, rightly.

### Persistence (one system migration; its number is allocated by the lead at implementation)
* **`restore_cutover`** (append-only):
  * the request id, company id, set reference (run id, signed manifest hash), prepared copy tag, acknowledgement hash and approval request id;
  * the steps journal (step, state, at);
  * the old and new heads, V_old, the epochs, the safety-backup run id, the retained database tag and the retained folder name;
  * the status, a closed set: `Prepared|Approved|Switching|Completed|Reversed|Failed|RolledBack|Expired`.
  * Status transitions are guarded by a trigger; nothing is ever deleted.
* The registry state CHECK gains `Restoring`; the provisioning and migration services treat it as busy.
* The `backup_retention_pin` kind CHECK (from #134) gains kind 3 (`lost_tail_evidence`, non-expiring: `expires_at` may be the far future or NULL, chosen at implementation with the slice 6 guard).
* `restore_cutover` has a unique constraint on the approval request id (M1).
* No company migration.

### Audit (system stream; business stream for 2026; D-150)
| Code | Event | Detail (ids, codes, hashes, tags; never names or paths) |
|---|---|---|
| 2020 | `RestoreCutoverRequested` | operator, company, run id, manifest hash, copy tag, restored head, live verdict, acknowledgement hash, approval request id |
| 2021 | `RestoreCutoverApproved` | approval request id, approver (as recorded by the framework), payload hash; written when consumed |
| 2022 | `RestoreCutoverStarted` | cutover id, live head and V_old, safety-backup run id |
| 2023 | `RestoreCutoverCompleted` | cutover id, old/new heads, parent/new epoch, retained database tag |
| 2024 | `RestoreCutoverReversed` | cutover id, the failed step and code (automatic reversal before the epoch rotation) |
| 2025 | `RestoreCutoverRolledBack` | cutover id, rollback approval id, new epoch |
| 2026 | `AuditEpochStarted` | stream, epoch, parent epoch, restored head, parent verdict and failure codes (in the system stream AND as the first event of the new epoch in the business stream) |
| 2027 | `RestoreCutoverRefused` | reason (`restore_cutover_unavailable`, a validation code, `unencrypted_set_refused`, `restore_set_not_a_prefix`, `restore_prefix_unprovable`, `numbers_issued_after_restore_point`, `accounting_events_after_restore_point`, `fiscal_state_differs`, `live_chain_not_verified`, `inert_assignment_revocation_failed`, `live_epoch_unreadable`, `cost_center_scope_widened`) |
| 2028 | `RestoreCutoverExpired` | request id (the prepared copy is dropped) |
| 2029 | `RestoreInertScopedAssignmentsRevoked` | cutover id, the revoked assignment ids, and per entity its id, the recorded epoch and the live epoch (review M7 of #143; ids and numbers only) |

* Every step fails closed when its event cannot be written.
* The detail carries database TAGS only.

### Exact tests (implementation PR)
* **Unit tests:**
  * the epoch file format and MAC chain (version pinned with a golden test, like format 1);
  * inter-epoch linking (a removed, replaced or reordered epoch file is detected);
  * the prefix rule (equal, shorter, diverging);
  * the verifier always reporting a failed earlier epoch (exit 6) after a rotation;
  * the runtime never inferring an epoch;
  * the journal state machine (every crash point resumes forward or backward);
  * the gate (`CutoverAvailable == false` pinned; every entry point refuses; the lowest-level switch routine refuses without the seam; no production use of the seam);
  * the typed acknowledgement;
  * the payload canonicalization and hash;
  * refusal of unencrypted sets.
* **PostgreSQL** (isolated runner; about 8 tests; the registry is raised at push time):
  * the request prepares a copy and changes nothing live;
  * the seam execute switches the database and the folder, re-applies grants, verifies, rotates the epoch, writes 2020-2023 and 2026, and the company serves the restored data;
  * a post-check failure reverses the switch (old data live, no epoch, 2024);
  * crash-resume at each journal step;
  * a forged or non-prefix set is refused;
  * a live stream that did not verify is recorded in the epoch and stays reported as Failed;
  * rollback through a second approval creates another epoch and restores the old data;
  * concurrent backup, retention, trial restore, migration and runtime grant are blocked while `Restoring`;
  * nothing in the user, session or credential tables changes, and the only role-assignment changes are the D-152 revocations of assignments inert in live;
  * H1: grant a branch-scoped role, deactivate the branch, cut over to a pre-deactivation set; the grant stays ineffective (and a warehouse and cost-center variant);
  * M7 (D-152 redesign):
    * grant a branch-scoped role, deactivate and reactivate the branch (live epoch +2), cut over to a pre-deactivation set: the grant stays ineffective, the inert row is soft-revoked and listed in ONE system event 2029 with its recorded and live epochs; an assignment effective in live stays effective where the restored epoch is equal, and becomes inert where it differs;
    * a warehouse with an open negative-stock obligation (company migration 0020): the cutover proceeds;
    * the MAIN branch, and a PARENT cost center with active children: the cutover proceeds, and their inert grants are revoked;
    * a cost center deactivated after the backup (same epoch, inactive in live): its grant is revoked;
    * a cost center moved out from under a granted cost center after the backup: refused (`cost_center_scope_widened`);
    * no company-database write: the copy's head stays seq_r and its branch, warehouse and cost-center rows are unchanged;
    * a resume after a crash inside the step recomputes it; a reversal does not un-revoke;
    * an assignment write for a company in `Restoring` is refused;
    * a live epoch that cannot be read refuses the cutover (`live_epoch_unreadable`);
  * H2/F3: issue a document number after the backup, request a cutover; it is refused (`numbers_issued_after_restore_point`), and the post-check fails on a forced bypass through the seam. Likewise for a number VOIDED after the backup, and for a series key that exists only in live;
  * F1: post (stock posting of an already approved document) after the backup; the request is refused (`accounting_events_after_restore_point`) and shows the per-type inventory; an unknown business event type counts as accounting;
  * F2: soft-close a period after the backup; the request is refused (`fiscal_state_differs`);
  * F4: a number issued between request and execution is caught by the execution-time comparison after quiescing;
  * M1: an approval claimed then reversed is consumed (a retry needs a new request); a claim after expiry is refused; a claimed cutover past its expiry still finishes;
  * M4: an epoch file without its Completed row and 2026 event verifies as Failed (`epoch_unbound`);
  * L2: no session can connect to the live database between termination and rename; the retained database refuses the runtime role.

### Decisions (D-143..D-151; recorded in `docs/DECISIONS.md`, status planned)
D-143 scope (single company; full-installation restore is a later slice bound by D-106/D-107/D-119); D-144 request/approve/execute and the gate; D-145 switch by database and folder rename, journaled and resumable; D-146 pre-cutover validation and the pinned encrypted safety backup; D-147 checkpoint epochs; D-148 runtime permissions, post-restore checks and automatic reversal before the epoch rotation; D-149 rollback window and rollback as an approved cutover; D-150 audit codes 2020-2029; D-151 interplay with slices 5 and 6 (encrypted sets only; `CutoverAvailable` shared with the slice 6 deletion gate; the D-118 restore operations adopt the same approval framework); D-152 company-scope revocations stay applied (inert scoped assignments revoked on the system side; Q8); D-153 what a restore may lose (lost-tail accounting events, numbering incl. issued and voided numbers, fiscal state; refuse by default after quiescing; Q7, Q9).

### Open questions for the owner (not decided here; the safe default applies until answered)

**Owner decisions 2026-11-21 (consolidated package; recorded once in `docs/DECISIONS.md`, where the owner's wording stands; this block only points to it and does not rewrite the plan).** Slice 7: every preserving default of the questions below is CONFIRMED, Q1, Q2, Q4-Q9 = D-228 (none of the listed options is chosen), and Q3 = D-229: live cutover activation requires a separate owner-authorised activation PR and a documented recovery drill, so `RestoreSafeguards.CutoverAvailable` stays false. Also recorded for the backup scope: retained encrypted import originals and their key references are to be included in the backup and recovery plans (IMPORT-ORIGINALS-BACKUP-SCOPE = D-224; new work, recovery completeness not claimed).
* **Q1. The live database is lost or unreadable**, so no pre-cutover safety backup can be taken. **Default: the cutover is refused.** A disaster variant without the safety backup (with the gap recorded) would need the owner's decision. **Note (F5):** consequently a company whose live database is corrupt or unreadable cannot be cut over in slice 7 at all, because every what-would-be-lost check (Q4, Q7, Q9) needs a readable live database. That case needs the later full-installation path or an owner-approved procedure.
* **Q2. How long the rollback window lasts, and what happens to the replaced database and folder after it.** **Default: a 7-day window; the replaced database and folder are never removed automatically.** Removal would need a separately approved, audited step that is not built.
* **Q3. Who may turn cutovers on.** **Default: only an activation PR merged on the owner's explicit instruction**, after a recorded drill (as for the 4.3-ACT deletion activation).
* **Q4. Cutover when the live audit chain does not verify** (corruption may be the very reason for the restore). **Default: REFUSED (review L4 of #143).** The option for the owner: allowed only when the approver re-types every failure code, with the failure reported forever (D-081: never concealed).
* **Q5. A set from an older migration level.** **Default: migrated forward on the prepared copy BEFORE the switch, with the checks run after migration;** a newer set is refused.
* **Q6. Who is notified, and how.** **Default: per D-102 (audit and in-app to every company administrator of the company, and to the identity administrators).**
* **Q7. What may a restore lose?** (review H2 of #143; accounting review F1 and F3).
  * **PRESERVING DEFAULT: nothing of accounting.** The cutover is REFUSED whenever the lost tail contains any accounting event, or any numbering series (counter, issued or voided number) differs from live.
  * The option for numbering: advance and void, ONLY under conditions 1-6 of step 3.6.
  * **Note for the owner:** while Q7 refuses, ANY company that has done accounting work (for example issued a document number) since its backup point cannot be cut over at all. Its lost work must be re-entered or recovered under a separate policy.
  * **Accounting configuration counts as accounting** (review Low of #143). The preserving default classifies configuration that drives accounting as an accounting event, so its loss refuses the cutover. This covers: chart and account settings, tax configuration, inventory-accounting versions, statement mappings, numbering rules, closing settings and approval policies. The owner may narrow this list.
* **Q8. Company-scope revocations after the backup point** (review H1 and M7 of #143).
  * **Default: before the switch, every branch-, warehouse- and cost-center-scoped assignment that is already inert in live is soft-revoked on the system side.** Assignments effective in live stay; they become inert where the restored epoch differs, which fails closed (administrators re-grant). A cost-center grant whose coverage the restored tree would widen refuses the cutover.
  * **This is security-preserving:** no permission is added, and none that live has is revived. Its only impact is that inert grants disappear permanently, which is already their effect, since a reactivation always bumps the epoch.
  * **No alternative is needed for the owner.** The owner may still choose to revoke instead of refuse for a widened cost-center coverage.
* **Q9. Fiscal state after the backup point** (accounting review F2). **PRESERVING DEFAULT: REFUSE the cutover when any fiscal period or year state, its revision, the year-close state or a pending reopen request differs between live and the copy.** The option for the owner: re-apply the live closures to the copy before the switch; never reopen anything.

### Owner decisions this slice relies on (not re-asked)
D-081, D-080(6), D-118, D-099, D-102, D-106/D-107/D-119 (bounded out: no system restore here), D-085..D-088.

## Import-file key coverage and the encryption-key retirement report (D-179..D-181; register item K follow-up)
Sources: SRS 14.4 and 16; owner decisions D-080(6) (independent approval for key retirement), D-082 (register item K, keys of retained data are never retired), D-085 and D-087 (encrypted backups, separate key-directory backup); D-114 (a purged attachment row still counts); D-138 (the key inventory of the retained runs). It closes the UNFINISHED Release 1 item "import-file rewrap retirability report and import-file key coverage".

* **The facts (D-179).**
  * A company backup set holds the company database dump and the company's ATTACHMENT folder only. Encrypted import-file originals (`Hasm__Imports__StorageDirectory`) are NOT part of any set.
  * Whether to include them was the owner question IMPORT-ORIGINALS-BACKUP-SCOPE (including them keeps originals in backups beyond the company's own import retention period, default 30 days, which is a data-retention policy choice). **The owner has decided it: D-224 (2026-11-21), import originals and their required key references are to be included in backups. D-224 is not built**, and this work does not build it.
  * The backup content scope is NOT changed here: until D-224 is built, import originals are excluded, as today. Consequently a restore brings back the batch rows but not their originals; such a batch reports its original as unavailable and needs a new upload (fail closed, nothing is guessed).
  * Because no backup that exists today contains an import original, backups do not need a key FOR import originals yet. Only the LIVE originals do, and they are what this work covers.
  * **Binding for the D-224 implementation:** it must put the key ids of the import originals it backs up into the run key inventory (the signed run status) and into the encryption-key retirement report (D-181). Otherwise a key that only a backed-up original needs would be reported retirable. Recorded with the binding items in `docs/INTEGRATION-STATUS.md`.
* **Import rewrap retirability (D-180).**
  * `ImportService.RewrapAsync` now returns `ImportRewrapOutcome`, like the attachments' `RewrapOutcome`:
    * `rewrapped`, `skipped` (purged or already current meanwhile) and `failed`;
    * the key usage of every not-yet-purged original, read after the run: `keyIdsInUse`, `remainingUnderOtherKeys` and `unknownKeys`;
    * `oldKeysMayBeRetired` = nothing failed AND nothing left under another key AND nothing unknown.
  * A failure is never silent:
    * a missing original, or an old key that is unavailable or invalid, is counted, and the other files go on;
    * the no-op cases abort their transaction with a code. Before this change, a missing original made the whole rewrap fail, because a company mutation must append an audit event.
  * The API response of `POST /api/v1/company/imports/maintenance/rewrap` gains these fields additively (no UI uses this endpoint).
  * **Key usage of stored items** (`EncryptionKeyUsageBuilder`; `ImportService.KeyUsageAsync` and `AttachmentService.KeyUsageAsync`; read-only, nothing is decrypted):
    * The key ids are those named by the ROW and by the envelope HEADER on disk. A crash between a file replacement and the row update of a rewrap can leave them different, so both are kept.
    * A file that cannot be read, a malformed envelope, an invalid row key id, or a row with neither key is `unknown`.
    * A missing file still counts its row's key.
    * Attachments: every SENSITIVE row (active, removed and purged; D-114). Imports: every original not yet purged.
* **The encryption-key retirement report (D-181):** `Hasm.Admin encryption-key-retirement-report <backup-root> [<backup-root> ...] [--all-backup-copies-listed] [--json <file>]`.
  * A master key in `Hasm__Encryption__KeyDirectory` is retirable ONLY when:
    * it is not the active key;
    * no live sensitive attachment needs it;
    * no live import original needs it;
    * no backup run PRESENT in ANY given backup root lists it (the key ids in each run's signed status, slice 6).
  * Fail closed: ANY unknown makes the report incomplete, and then no key is retirable. Unknowns are:
    * a registered company that is not Ready (a Pending company has no database and therefore no data), cannot be read, or has no configured attachment or import storage;
    * any `unknown` key usage;
    * no backup root given;
    * a backup root that does not exist, is not a directory, or is itself a symbolic link (`backup_root_missing`, `backup_root_not_a_directory`, `backup_root_symbolic_link`);
    * a backup root without at least one complete, verified run (`backup_root_no_complete_verified_run`);
    * a present run whose key needs are unknown (`backup_run_key_needs_unknown`), or an unfinished deletion, a `.deleting-*` folder (`backup_root_deletion_incomplete`);
    * any entry of a backup root that is not a run folder, a `.deleting-*` folder or `.retention` (`backup_root_unrecognised_entry:<names>`, see below);
    * anything inside `.retention` other than slice 6 tombstones (`backup_root_unrecognised_entry:.retention/<path>`), and a present run that holds anything a run does not hold (`backup_run_unrecognised_content:<run ids>`), see below;
    * a backup root that cannot be read;
    * no attestation of the backup scope (`backup_scope_not_attested`, see the scope limits below);
    * no active key configured.
  * **A backup root must prove itself** (review H1 of PR 182). The retention scan returns no runs for a folder that does not exist, and "every retained run's key needs are known" is true over no runs. A typo, a share that is not mounted or the wrong folder would therefore read as "no retained run needs any key". So:
    * the root is checked before AND after the scan (a root that disappears meanwhile is refused);
    * a symbolic link as the root is refused, because the link can be re-pointed between the report and the retirement;
    * the root must hold at least one run that is finished, whose signed status VERIFIES against the trusted signing keys, whose key needs are known (run status version 2), and whose verdict is `ok` or `partial`. An incomplete, failed, unverifiable or unencrypted DEV/TEST run does not count. Without a trusted signing key nothing verifies, so the report is incomplete;
    * one good root never excuses a bad one: every given root must pass.
  * **No configured backup root exists.** `backup-run`, `backup-status` and the retention commands all take the root as an argument, and no `Hasm__Backup__*` setting names a root or a destination (the multiple-destinations slice 8, D-182..D-189, is planned below and not built). The report therefore cannot require "the configured root" today. When slice 8 adds configured destinations (D-183), the report must require the primary root and every configured destination to be among the roots it scans (recorded with the binding items of the slice 8 implementation in `docs/INTEGRATION-STATUS.md`).
  * **Scope limits, stated in every output and in the JSON (`scopeLimits`).** The report reads the live data of every registered company and every run present in ONLY the roots it is given. It does NOT see:
    * a backup root that is not given: another disk, an off-site or offline copy, a second destination;
    * standalone `backup-company` sets and copies of sets. They are not runs, are not inventoried, and can hold attachment envelopes under a key;
    * an upload in flight and orphan files (see the race below).
  * **The limits make keys non-retirable by default.** The tool cannot know whether other copies exist, so it never says "retirable" on its own: without `--all-backup-copies-listed` the report is INCOMPLETE (exit 8). The flag is the operator's attestation that the given roots are every place a backup of this installation is kept and that no standalone set or copy made under an older key is retained elsewhere. With it, a key is reported as `RETIRABLE WITHIN THE SCANNED SCOPE`, and the output repeats what was not seen. The attestation is an input of the report, not a proof; the independently approved retirement operation (D-080(6)) must record who attested.
  * **Race with uploads, and orphan files (review L1 of PR 182).** The scan reads committed rows.
    * An upload in flight encrypts its file under the key that is active when it starts and commits its row afterwards. If the active key changes during that upload, the file is under the PREVIOUS active key and its row is not yet visible, so a report made in that window can call the previous key retirable.
    * A file with no row (an orphan left by a crash, before the orphan sweep) is never inventoried, whatever key it is under.
    * The report cannot close this window: it takes no lock and changes nothing. The future retirement operation MUST re-run this check under a lock that excludes uploads and rewraps of attachments and import originals for every company (or scan the storage folders for files without rows), and must refuse when the result differs from the approved report.
  * **Strictly read-only on the file system (review L2 of PR 182).** The ordinary read path of both file stores creates the owner-only storage root and company folder (`PathOf`). The key-usage scan uses `ReadWithoutCreating` instead: it creates nothing and changes no permission. A company folder that does not exist while a row names a file in it is NOT "no file" (the storage may be unmounted or misconfigured): the item is `unknown`.
  * **Every run on disk counts, not only the runs retention would keep (review M-FH1 of PR 182).**
    * The retention preview's key inventory (D-138) lists the keys of the runs the plan KEEPS. That definition is right for the preview and is not changed here.
    * It is wrong for this report: a run the plan would delete is still on disk, complete, verified and restorable, and it stays there, because automatic deletion is inactive (D-137). A key that only such a run needs would be printed as retirable while a present backup needs it, against D-082.
    * So the report does not use the plan. It reads the signed status of EVERY run folder in the root and takes the union of their key ids (reason `needed_by_present_backups`). It never asks whether a run is eligible for deletion, and it needs no retention configuration.
    * Any present run whose key needs are unknown (unfinished, unverifiable, a version 1 status, an unencrypted DEV/TEST run) makes the root's inventory incomplete. So does a `.deleting-*` folder: what is left of that run cannot be read as a run. The known keys are still listed.
    * A key stops being needed by a run only when that run is really gone from every scanned root.
  * **Every entry of a root is accounted for (review M-FH3 of PR 182).**
    * The retention scan reads only the folders whose name is a run id (`yyyyMMddTHHmmssZ-<8 lower-case hex>`) and ignores every other entry. That is right for the retention preview and is not changed.
    * For this report it was a blind spot: a complete, signed, restorable run moved or copied inside the root under another name (`archive/<run id>/`, `<run id>.bak/`, a hidden `.old/`, `run-<run id>/`, an upper-case id, an archive file of a run) was not read, so a key only that run needs was printed as retirable.
    * So the report lists every entry directly in each root (directories, files, symbolic links and hidden entries) before and after the scan. Recognised are only: a real directory named by a run id, a real directory `<run id>.deleting-<16 hex>` (itself a blocker, above) and a real directory `.retention`. Any other entry is the blocker `backup_root_unrecognised_entry:<names>`: the names in ordinal order, percent-encoded (so a name cannot break the code or carry a control character into the output), at most 20, then `+N_more`. A symbolic link is never recognised, whatever its name.
    * The way out: move the entry out of the root (or back to its run-id name, if it is a run). Write the `--json` file outside the roots; inside one it is an unrecognised entry of the next report.
  * **One level down (review M-OBKP4 of PR 182).** The same blind spot existed in the two places the entry check accepted without looking inside.
    * A run folder: a complete signed run copied or moved INSIDE another run folder (`<other run>/old/<run id>/`) is not read as a run. The retention scan already marks such a folder not clean (it holds files a run does not hold), so the report now treats a present run that is not clean as one whose key needs are unknown: `backup_run_unrecognised_content:<run ids>` (encoded and bounded like the entry code), checked right after the entry check. The run's own known keys are still listed.
    * `.retention`: it is now walked, not trusted. Recognised are only what slice 6 writes there (verified against `RetentionExecutor.WriteTombstone` and `WriteCompleted`): a real folder `.retention/tombstones`, in it real folders named by a run id, and in each only real files `run.status.json`, `run.status.json.sig`, `deleted.json`, `completed.json` (any subset, since a crash can interrupt a tombstone) or a crash leftover of one of them from the atomic writer (`<name>.tmp-<32 hex>`). Anything else, such as a run kept in `.retention/archive/<run id>/` or a stray file, is `backup_root_unrecognised_entry:.retention/<path>`, each path segment percent-encoded.
    * The retention preview, the scan and the classifier are unchanged.
  * **The union is over every present run, whatever its verdict (review L-OBKP1 of PR 182).** A run whose verdict is `failed` (its system dump failed) does not prove the root, but its company sets can have been written and its signed status lists their key ids. Those keys are kept.
  * **The JSON names its own scope (review L-FH1 of PR 182):** `backupRootsScanned`, `backupScopeAttested` and `generatedUtc` (UTC), next to the keys, the blockers and `scopeLimits`. It is the artefact an independent approver is shown; it describes that moment only.
  * **A stored file name that the store refuses** (it does not match the store's name pattern) is an item whose key cannot be determined: `attachment_key_unknown` or `import_original_key_unknown`, not an unhandled error (review L-FH2 of PR 182).
  * A key that data or a present run needs but that is MISSING from the key directory is reported (`missing_from_key_directory`).
  * Read-only, and no audit event: retirement itself is a separate, independently approved operation (D-080(6)) and is not built. Exit codes: 0 complete, 8 incomplete, 3 configuration.
  * With the multiple-destinations slice 8 (planned below, not built), every destination root must be given.
* **Tests:**
  * Unit (`EncryptionKeyRetirementTests`): the rule per source; every blocker, including a bad root next to a good one; the attestation and the scope limits; the root check (missing, a file, relative, a link to a directory, a link to a file, a dangling link); which run proves a root (not a failed, incomplete, unverified, version 1 or unencrypted one); the inventory of present runs (the union of every run's keys, also of a run whose verdict is `failed` next to a good run, review L-OBKP1; an unknown run, a `.deleting-*` folder; an unrecognised entry first); the one-level-down checks (review M-OBKP4: a run that is not clean has unknown key needs and is named; inside `.retention` a complete tombstone with a crash leftover, an incomplete one and an empty `tombstones` folder are accepted, while `.retention/archive/<run id>`, a stray file, a hidden folder, a non-run-id folder or a file in `tombstones`, a folder or another file inside a tombstone, a malformed leftover, a `tombstones` file and links are each named); the strict entry check of a root (review M-FH3: a clean root with run folders, a `.deleting-*` folder and `.retention` has none; a nested run, a renamed run, `run-<id>`, a hidden folder, an upper-case id, a wrong deletion suffix, a file, an archive file, a hidden file, a FILE named like a run id, a `.retention` file, and links named like a run id each block; several in ordinal order) and the encoding and bound of the code's names; the scope fields of the report; a missing needed key; the usage scan (row and header kept, a missing file counted, unreadable, malformed and invalid items and a refused stored name unknown).
  * PostgreSQL, 10 tests in all:
    * `ImportServiceTests` (+4): a rewrap with a missing original and an unavailable old key (counted, old keys must be kept); the report over an EMPTY folder and over a missing root (incomplete, nothing retirable; a key only an import original needs is named until a complete rewrap); a corrupt original, and a missing company storage folder (`import_original_key_unknown`, incomplete, no folder created); an original whose envelope header names another key than its row (both kept). The existing rotation drill asserts the new signal.
    * `AttachmentTests` (+2): the same two cases for a sensitive attachment (`attachment_key_unknown`; row key and header key both kept).
    * `BackupRetentionTests` (+2): every bad root over real data (missing; empty; a file; only an incomplete run; a real run with no trust, with a foreign trust list, and with a tampered status; a symbolic link to the good root; a bad root next to the good one); and the positive case over a real, complete, verified run, with and without the attestation, including the `Hasm.Admin` command end to end (exit 0 and 8, the scope lines, the JSON with its roots, attestation and time, no audit event, nothing created).
    * `BackupRetentionTests` (+1, review M-FH1): twelve real daily runs, of which only one, a run the retention plan marks eligible for deletion, holds an attachment envelope under an older key. The preview's inventory does not list that key (D-138, unchanged); the report keeps it (`needed_by_present_backups`), also through the command. An unfinished run folder and a `.deleting-*` folder make the report incomplete. Only after that run is really removed is the key retirable.
    * `BackupRetentionTests` (+1, review M-FH3): two real encrypted runs, only the first needing an older key. A clean root reports complete. That run moved to `archive/<id>/`, `<id>.bak/`, `.old/`, `run-<id>/` and an upper-case id, a copy of it next to the original, and a file in the root each block (`backup_root_unrecognised_entry:<name>`, nothing retirable); the command with the attestation exits 8, names the entries and prints the rule in its scope text; the retention preview still reads only the run folders; once the root is clean again the report is complete. Review M-OBKP4, in the same test: that run moved inside the other run folder (`backup_run_unrecognised_content:<other run>`, also through the command with exit 8) and into `.retention/archive/`, and a stray file in `.retention`, each block; a tombstone written as slice 6 writes it is still accepted (complete).
  * Mutation proofs (isolated runner):
    * import originals dropped from the rule: the unit and PostgreSQL report tests fail;
    * failures counted as skipped: the rewrap test fails;
    * the scans ignoring the file on disk (`() => null` in both `KeyUsageAsync`), together with the root checks switched off: exactly the 7 tests above that cover them fail (the 4 corrupt and header-key tests, the empty-folder report test and both `BackupRetentionTests`), 29 of 36 pass.
    * the report taking the retention plan's inventory again (the keys of the runs the plan keeps), and the inventory ignoring an unknown run and a `.deleting-*` folder: the new PostgreSQL test fails at the key of the deletion-eligible run (no reason is left to keep it), 1 of 10 `BackupRetentionTests`; the new unit test fails, 1 of 16.
    * review round 3: the key union restricted to the runs that prove the root (`runs.Where(IsCompleteVerifiedRun)`): the unit inventory test fails at the `failed`-verdict run (L-OBKP1), 1 of 19 `EncryptionKeyRetirement*` unit tests. The strict entry check switched off in the report (no unrecognised entries passed to the inventory): the new PostgreSQL test fails at the first moved run, exactly 1 of 11 `BackupRetentionTests`.
* **Not built here:** key retirement itself (D-080(6)); including import originals in backups (decided by the owner, D-224; not built); the audit and recovery keys (unchanged; slice 6 lists them).

## Slice 8: multiple backup destinations (local and network copies, SRS 16) - PLAN (docs only)

Sources: SRS 16 bullet 1 (retention and multiple local or network destinations, encryption, alerting and monitoring of the backup result); ROADMAP 4.3 deliverables ("destinations"); owner decisions D-085 (encryption and signed manifests) and D-086 (retention minimums and safeguards); slices 4, 5 and 6 of this document. Risk: Critical (ROADMAP 4.3). Decisions D-182..D-189 (status planned, `docs/DECISIONS.md`); audit codes 2040-2049 (system stream; lead allocation, recorded in `docs/INTEGRATION-STATUS.md`).

### Scope
* **In scope:**
  * replicate each complete, encrypted and signed run of the primary root to N configured destinations (local folders and network shares mounted or addressed by the operating system);
  * verify every copy at its destination by signature and stored digests before it is reported as a good copy;
  * per-destination status in `backup-status`, with stale, failed or corrupt copies raising the alert exit codes;
  * slice 6 retention evaluated and applied per destination, with the same D-086 minimums and the same INACTIVE deletion gate (D-137);
  * refusal of overlapping or unsafe destinations, reusing and extending the slice 3/5 overlap checks.
* **Not in scope:**
  * object storage or cloud APIs, and any network protocol implemented by HASM: a network destination is a path the operating system already reaches (a mounted share on Linux, a UNC path or mapped share on Windows);
  * network credentials: HASM stores none. Access is the service account's own operating-system identity (privilege separation, as for scheduling in slice 4);
  * incremental or differential copies: every copy is a full run, as every run is (D-135);
  * a backup screen in the shared frontend (the later monitoring UI slice).
* **Migrations: none.** Replication state lives in the destination folders and in the system audit stream. The legal-hold and pin tables of slice 6 are keyed by run id and apply to every destination unchanged. If implementation shows a migration is needed, work stops and the lead allocates the number.
* **What it does not meet by itself:** RELEASE1-ACCEPTANCE G stays blocked per D-088 (REL-ACC). Automatic deletion stays INACTIVE on every destination (D-137). Full-installation restore and the cutover (slice 7) are unaffected.

### D-182, the model: a primary root and replicas of complete runs
* `backup-run <root>` keeps writing the run to the primary root exactly as today. A destination receives a byte-for-byte COPY of a complete run folder; it is never a second dump. So every copy carries the same run id, the same signed `run.status.json`, the same signed manifests and the same ciphertext, and a copy verifies with the same trust list (slice 5) as the original.
* **What is replicated:** every complete run whose `run.status.json` verifies and whose verdict is `ok` or `partial`. A `partial` run still holds good company sets.
* **What is never replicated:**
  * a run with verdict `failed`, an incomplete run (no valid status), and a run whose status does not verify;
  * an unencrypted DEV/TEST run (D-088, D-095), whatever acknowledgement the reader holds: a copy would multiply plaintext, and DEV/TEST sets never need off-machine copies (`replica_unencrypted_refused`);
  * standalone `backup-company` sets, key exports (`backup-keys`) and kit material. The key export has its own separate destination by D-087.
* **When it runs:**
  * at the end of `backup-run`, after `run.status.json` is signed, for every configured destination;
  * by `backup-replicate <root> [--run <id>] [--destination <id>]`, which catches up runs that a destination missed (for example while a share was offline). Without `--run` it copies, oldest first, every replicable run of the primary that is missing or unverified at the destination, newest first if `--newest-first` is given.
* **Alternative rejected:** dumping once per destination. It costs N database snapshots per run, gives copies with different contents and run ids, and breaks "one run, one signed unit" (D-130).

### D-183, configuration format
* Destinations are settings of the administrative tool (not of the API), in `appsettings` or environment variables:

  ```json
  "Hasm": { "Backup": { "Destinations": [
    { "Id": "nas-1",   "Path": "/mnt/hasm-nas/backups", "Kind": "network" },
    { "Id": "usb-a",   "Path": "/srv/hasm-usb/backups", "Kind": "local", "DailyKeep": 14 }
  ] } }
  ```

  or `Hasm__Backup__Destinations__0__Id=nas-1`, `Hasm__Backup__Destinations__0__Path=...`, `Hasm__Backup__Destinations__0__Kind=network`.
* Fields:
  * `Id`: required, `[a-z0-9-]{1,32}`, unique; it names the destination in reports and audit events (never the path);
  * `Path`: required, absolute (a UNC path is absolute on Windows);
  * `Kind`: `local` or `network`, required; it is descriptive (reports, the volume check below) and changes no copy rule;
  * optional `DailyKeep` and `WeeklyKeep`: retention for that destination; they may RAISE the D-132 values, never lower them (below 7 or 4 is `retention_below_minimum`, exit 3; never clamped);
  * optional `MaxAgeHours`: the freshness limit of copies at that destination for `backup-status` (default: the `--max-age-hours` of the call, 26 hours).
* At most 8 destinations. No destination configured means today's behaviour (a single root), and `backup-status` reports `replicas: none` without changing its exit code until owner question MD-Q1 is answered otherwise.
* Any invalid entry makes every command that reads the configuration exit 3 (`destination_config_invalid`) before anything is written. A configuration error is never skipped silently, and no destination is dropped silently.

### D-184, unsafe and overlapping destinations (reusing slices 3 and 5)
* Checked by every command before anything is copied or deleted, on resolved paths (symbolic links refused, as in slice 3):
  * the destination exists or its parent exists; it is created owner-only (0700, verified on Linux) when absent;
  * a destination that is itself a symbolic link, or contains one on its resolved path, is refused (`symbolic_link_refused`);
  * no overlap, in either direction, with: the primary root, any other destination, the attachment root, the encryption, audit and signing key directories (the slice 5 set, `destination_overlaps_source`), and the trial-restore staging folders;
  * a destination equal to the primary root, or two destinations resolving to the same folder, is refused (`destination_duplicate`).
* `backup-keys` (slice 5) additionally refuses a key-export destination inside any configured backup destination (`destination_in_backup_root`), so the recovery material never travels with the data copies (D-087).
* **Same volume.** A destination on the same volume as the primary root (same device id on Linux, same volume serial on Windows) protects against neither disk failure nor loss of the machine. It is reported as a warning (`destination_same_volume`) in `backup-status` and on each copy; whether it must be refused is owner question MD-Q2.

### D-185, atomic copy and verification at the destination
For each run R and destination D (in `backup-run` and `backup-replicate`):
1. **Locks.** The restore lock of slice 6 is taken SHARED for R's duration, so a retention deletion at the primary cannot remove R while it is being copied. A per-destination advisory lock (run-lock namespace of slice 4, object derived from the destination id) is taken exclusively, so two replicators never write the same destination and retention at D never runs during a copy (`destination_busy`, exit 4, bounded wait).
2. **Already there.** If `<D>/<R>/` exists and its `run.status.json` hash equals the primary's, the copy is re-verified (step 4) instead of copied again. A folder with that run id and a DIFFERENT status hash is never overwritten: it is reported (`replica_conflict`, audit 2041) and kept as evidence.
3. **Copy into staging.** Files are streamed into `<D>/<R>.incoming-<16 hex>/` (owner-only), each file written to its final name inside the staging folder, flushed to disk (`fsync` of every file and of the staging folder on Linux, `FlushFileBuffers` on Windows). The copy reads the primary's files only; it never decrypts anything and never needs the recovery private key.
4. **Verify the COPY, read back from D** (never from the bytes just written in memory). Every write handle is flushed and CLOSED first, and each file is then REOPENED for reading through a new handle on the destination path:
   * `run.status.json` and every manifest verify against the server's signing trust list (slice 5; a retired key only for sets signed before its retirement, a compromised key never);
   * the status hash equals the primary's;
   * every file listed in the signed manifests exists with the signed size, header hash and chunk-hash list (H1), recomputed over the file at D;
   * the folder holds no file outside the slice 5 set plus `recovery-test.json` (the slice 6 `run_not_clean` rule).
   * **Limitation of this read-back (review L4 of #176).** A read that follows a write is normally answered from the operating system's page cache (local disk) or from the client cache of the share (SMB, NFS), even through a new handle. Step 4 therefore proves the copy AS THE OPERATING SYSTEM PRESENTS IT after the flush: it catches a truncated, incomplete, substituted or altered file, a wrong status, an unexpected file and a failed flush (`fsync` or `FlushFileBuffers` returning an error fails the copy). It does NOT prove the bytes on the medium or on the file server, and HASM makes no such claim: "verified" in this slice, in audit 2040 and in `backup-status` means verified by read-back after flush, not proven durable on the medium.
   * **Mitigations.** (a) Flush, close and reopen, as above; where the platform offers it the implementation also asks the system to drop the cached pages of each file after the flush and before the read (`posix_fadvise(POSIX_FADV_DONTNEED)` on Linux), best effort and never relied on. (b) `backup-replica-verify` (below) re-reads the copies later, in another process, and is the check that finds medium faults and bit rot; a copy's first re-verification is reported by `backup-status` (`reverified` age per destination), and the drill restores from the network copy on another machine. (c) A copy later found corrupt is exit 1 and blocks every deletion (D-187). Residual limitation, stated and not hidden: a deletion at the primary can still rest on copies that so far have only their publish-time verification; this is one reason `backup-replica-verify` is to be scheduled, and it is to be weighed in the evidence of the activation PR (4.3-ACT), since deletion stays INACTIVE until then (D-137).
5. **Publish.** Only after step 4 passes: audit 2040 is committed (D-188: nothing is renamed into place before the event of its verification is committed), then the staging folder is renamed to `<D>/<R>/` (atomic within one file system) and the parent folder is flushed. A run folder at a destination therefore always holds a verified copy; there is no partial run under a run id.
   * **A publish that fails after 2040 (review L5 of #176).** The order stays audit first, because the opposite order could publish a copy with no audit record. If the rename or the flush of the parent folder then fails, the command writes the COMPENSATING event 2041 with reason `replica_publish_failed` (same run id, destination id and status hash as the 2040 it answers), removes the staging folder (best effort) and records the destination's outcome as `failed`. If that 2041 cannot be written either, the command exits 1 and the staging folder is left for the sweep of step 7.
   * **What 2040 means, and what decides the existence of a copy.** 2040 records "verified by read-back, publish attempted". A 2040 is followed by exactly one of: the published folder `<D>/<R>/`; a 2041 `replica_publish_failed`; or, after a crash between the event and the rename, nothing until the next copy, which finds no run folder, copies again and writes a new 2040. Whether a copy EXISTS is ALWAYS read from the destination folder (its signed status and step 4), never from an audit event alone (a claim that a copy was DELETED needs folder state and audit together, D-187): `backup-status`, `replication_pending` (D-187) and every deletion check ignore a 2040 whose folder is absent.
6. **Failure.** Any error in steps 3-5 removes the staging folder (best effort), records the destination's outcome `failed` with its code (`destination_unreachable`, `destination_full`, `replica_verification_failed`, `replica_conflict`, `replica_publish_failed`) and writes audit 2041. One destination's failure never stops the others, and never changes the primary run.
7. **Crash.** A leftover `<R>.incoming-*` folder is never a run. The next copy to D removes staging folders older than 24 hours (the slice 3 sweep rule) and copies again; `backup-status` reports them (`replica_incoming_stale`, exit 8).
* **The copy's `recovery-test.json`.** A recovery test is written to the primary run after the run. `backup-replicate` copies a later `recovery-test.json` into an existing verified copy through the same staging rule (copy, verify the preimage against the signed hash, rename into place); it never changes any signed file.
* **Re-verification of old copies.** `backup-replica-verify [--destination <id>] [--all]` re-runs step 4 over the copies at a destination (by default the newest copy per company and the copies the retention hard keeps protect). A copy that no longer verifies (bit rot, tampering) is reported `replica_corrupt` (audit 2042) and is NEVER repaired in place: the next `backup-replicate` writes a fresh copy beside it only after the operator has moved the corrupt folder away (it stays evidence). Scheduled by the operating system like `backup-run`.

### D-186, success semantics and per-destination status
* **Severity order of exit codes (review M1 of #176; the existing convention of the backup commands, `Hasm.Admin` and slices 3 to 6: 0 ok, 8 warning or partial, 1 failure or alert).** Results are combined by SEVERITY, never by the numeric value: **failure (exit 1) is worse than warning (exit 8), which is worse than ok (exit 0); written 1 > 8 > 0.** "Worst of" below always means this order, so one exit-1 contribution makes the result 1 whatever else is 8, and a numeric maximum is never used (it would turn a failure into a warning). The codes 2 (usage), 3 (configuration, `destination_config_invalid`, `retention_below_minimum`, `retention_deletion_inactive`) and 4 (a lock is held, `run_in_progress`, `destination_busy`) are REFUSALS of the command: they are returned alone, before any result is computed, and are never combined with 0, 8 or 1.
* **One status function.** The contribution of a destination (the rules listed under `backup-status` below) is computed by ONE function, used by `backup-run`, `backup-replicate`, `backup-status` and the deletion checks of D-187, so the same state of a destination has the same severity in every command.
* **The primary run's verdict does not change.** `run.status.json` is signed before replication and is never rewritten, so a run's signed verdict (`ok`, `partial`, `failed`) describes the backup itself. Replication outcomes are separate.
* **`backup-run` result and exit code:**
  * the replication outcome per destination is printed and written to audit (2040 or 2041);
  * the exit code is the worst (1 > 8 > 0) of three contributions:
    1. the run's own code (slice 4: 0 `ok`, 8 `partial`, 1 `failed`; a failed run is not replicated);
    2. the replication code of THIS run: 0 when every configured destination verified its copy, 8 when one or more failed (`replication_incomplete`), 1 when none verified while at least one is configured (`replication_failed`);
    3. the status contribution of every destination AFTER the copy attempt, from the one status function (the `backup-status` rules below);
  * **`backup-run` and `backup-status` agree (review L1 of #176).** Because of contribution 3, `backup-run` is never milder than `backup-status` would be immediately afterwards for the same destinations. A destination whose copy failed and which is left with NO verified copy, or only one older than its `MaxAgeHours`, makes `backup-run` exit 1 (`replica_missing`, `replica_stale`, printed together with `replication_incomplete`), exactly as `backup-status` reports it. A destination whose copy failed but which still holds an older verified copy within its age gives 8 in both commands (`replica_lagging`). `backup-run` can be stricter than a later `backup-status` only through contribution 2 (all copies of this run failed although older copies are still fresh: 1 against 8), which is intended: the run's own replication failed completely;
  * this is the preserving reading of SRS 16 ("multiple destinations"): a run is fully successful only when EVERY configured destination holds a verified copy. Whether fewer copies may count as success is owner question MD-Q1.
* **`backup-status <root>` adds, for each configured destination:** its id and kind; the newest verified copy (run id and age); whether the primary's newest replicable run is present there; staging leftovers; the same-volume warning; and the exit contributions:
  * the destination's newest verified copy is older than its `MaxAgeHours`, or it holds no verified copy: exit 1 (`replica_stale`, `replica_missing`);
  * the primary's newest replicable run is not yet at the destination while an older copy is still within its age: exit 8 (`replica_lagging`);
  * a copy reported corrupt by the last re-verification, or a `replica_conflict`: exit 1 (`replica_corrupt`, `replica_conflict`);
  * `replica_incoming_stale` and `destination_same_volume`: exit 8;
  * a tombstone at the destination that the system audit stream does not explain (no matching 2015 and 2016 for that run id, status hash and destination id, or a status hash other than the primary's): exit 1 (`replica_tombstone_unexplained`, D-187);
  * an interrupted deletion at the destination with its matching 2015 in the system audit stream and no 2016: exit 1 (`retention_deletion_incomplete` for that destination, D-187; the next `backup-retention-apply` completes it).
  * **Root status and overall status.** The ROOT status of a folder is what the slice 4, 5 and 6 rules give for that folder alone (its newest signed run, age, `recovery_untested`, `retention_deletion_incomplete`, unreadable reports). The OVERALL status is the worst (1 > 8 > 0) of the primary's root status and every configured destination's contribution. `backup-status <primary root>` prints both and exits with the OVERALL code, so a stale, missing, corrupt or conflicting copy always alerts through the operating-system monitor as a failure (SRS 16, D-071) and can never be reduced to a warning by another warning.
* **`backup-status <destination path>`** on a destination folder works as on a primary root (the same signed-status rules) and gives that folder's ROOT status only (it has no view of the primary or of the other destinations), so a monitor on another machine can check a share directly.

### D-187, retention per destination (slice 6 unchanged, applied at every destination)
* Each destination is a root of its own for slice 6: `backup-retention-plan` and `backup-retention-apply` take `--destination <id>` (or a root path) and classify the runs PRESENT there, with D-131 classification, D-132 minimums (the destination's own `DailyKeep`/`WeeklyKeep`, never below 7 and 4), D-133 recoverability, D-134 holds and D-135 pins.
* **Global facts used at every destination:** legal holds and retention pins (system tables keyed by run id), and the recovery-test proof of a recovery key id (a passed test on any copy of any run counts for that key id, because the copies are byte-identical and signed).
* **New keep reason at the primary, `replication_pending` (defined without a hypothetical policy; review L3 of #176):** the primary never deletes a REPLICABLE run (D-182) while any CONFIGURED destination has no verified copy of it. "Verified copy" means the published folder `<D>/<R>/` passing step 4 of D-185 at the time of the check (read from the folder, never from an audit event); a destination that cannot be read counts as having no copy. No attempt is made to classify the run "as if" it were at the destination: a run that is not there is simply pending. The one exception is a run that the destination's OWN retention deleted, and it is deliberately narrow (review M-FG1 of #176).
  * **Why a tombstone alone proves nothing.** A slice 6 tombstone is a copy of the run's own signed `run.status.json` and `.sig` plus unsigned marker files. It verifies for anyone who holds those two files, so it proves that the run was authentic, NOT that this destination's retention deleted it. Whoever can write to the destination share could delete `<D>/<R>/` and plant the two files under `<D>/.retention/tombstones/<R>/`. Slice 6 also writes the tombstone BEFORE the rename and the delete, so a tombstone can exist without a completed deletion. A tombstone is therefore never sufficient.
  * **The exception applies only when ALL THREE hold; anything else is PENDING and is copied again:**
    1. the tombstone at D verifies against the signing trust list AND its status hash equals the hash of the primary's `run.status.json` for that run id;
    2. the SYSTEM AUDIT STREAM, read from the system database and never from the share, holds BOTH the 2015 `BackupDeletionStarted` and the 2016 `BackupDeleted` event for that run id, that status hash and `destination` = D's id (the destination field of D-188). A 2015/2016 pair for the primary or for another destination does not count, and a 2016 written by the slice 6 recovery path (`recovered=audit`) counts only together with its 2015, which is written solely by `backup-retention-apply` after its full validation. **The pair must be the latest word on that copy (review L-FG2 of #176):** it counts only if NO 2040 (copy verified) for that run id and destination id comes LATER in the audit stream (by stream sequence, not by clock) than the pair's 2015, and therefore none later than its 2016. A run that was deleted at D and then copied there again (by the explicit re-copy below, or by `backup-replicate` while a deletion was interrupted) is an ordinary copy; its old tombstone and old 2015/2016 pair stay as history and can never again end the pending state, so removing the re-copied folder by hand leaves the run pending and it is copied again. Only a NEW deletion by `backup-retention-apply`, with its own 2015 and 2016 after that 2040, does;
    3. neither `<D>/<R>/` nor a `<R>.deleting-*` folder exists at D.
  * **Folder state and audit together (how this fits the rule of D-185 step 5).** The EXISTENCE of a copy is read from the destination folder and never from an audit event alone (a 2040 without its folder is no copy). A DELETION claim is the mirror case and needs both: the folder state (conditions 1 and 3), which the share's writers control, AND the audit record in the database (condition 2), which they do not. Neither side alone ends the pending state: a 2016 without the tombstone and the absent folder does not, and a tombstone without the 2015/2016 pair does not.
  * **An unexplained tombstone alerts, and an interrupted deletion has its own code (review M-FG2 of #176).** Two states are told apart by the DATABASE, never by a file on the share:
    * `retention_deletion_incomplete` at that destination, exit 1: the tombstone verifies with the primary's status hash, the system audit stream holds a matching 2015 (that run id, status hash and destination id) and no 2016 for it, and the run folder is gone or a `<R>.deleting-*` folder exists. This is a deletion HASM itself started and did not finish; the next `backup-retention-apply` for that destination completes it (see the order below) and the status returns to 0. (With a matching 2015 and the run folder still present and verifying, slice 6's crash before the rename, there is nothing to complete and nothing to alert: the copy is there, the run is an ordinary copy and the next plan evaluates it afresh.)
    * `replica_tombstone_unexplained`, exit 1: a tombstone at D with NO matching 2015 in the system audit stream, or whose status hash is not the primary's. It is never resumed.

    Both are reported through the one status function of D-186; the run stays pending and `backup-replicate` copies it to D again. HASM never deletes or rewrites the tombstone: it stays as evidence, and the alert remains until an operator has investigated and moved it away. This applies to `replica_tombstone_unexplained`; `retention_deletion_incomplete` clears by itself when the deletion is completed.
  * **"Not copied again" is conditional (review L-FG1 of #176).** `backup-replicate` skips a run at D only while all three conditions hold. If the destination's `DailyKeep` or `WeeklyKeep` is raised later, an already completed, audited deletion is not undone automatically (the retention decision was valid when it was taken, and its audit stands); an operator who wants the run back names it: `backup-replicate --run <id> --destination <id>` copies it again through D-185 while the primary still holds it, after which condition 3 no longer holds and the run is an ordinary copy (the tombstone and its audit stay as history). An unexplained tombstone never suppresses a copy.
  * **Consequences, all preserving.** A share that is offline never causes the only copy to be lost. A destination that stays offline, or one that was just added and has not caught up, keeps EVERY replicable run at the primary until `backup-replicate` has copied them or an administrator removes the destination from the configuration (an explicit configuration change; while it is configured and offline, `backup-status` exits 1 for it). The primary's disk can fill for that reason; the backup then fails loudly (exit 1) and nothing is deleted to make room.
* **Which status a deletion needs (review L2 of #176).** Slice 6 step 3 (`backups_not_current`) is tightened, never relaxed. With destinations configured, `backup-retention-apply` requires, besides every slice 6 step:
  * at the PRIMARY: the OVERALL status of D-186 is ok (exit 0), that is the primary's root status AND every destination's contribution;
  * at a DESTINATION: the OVERALL status is ok (exit 0) AND that destination's own ROOT status is ok (exit 0).
  * **Order: interrupted deletions are completed FIRST, the status is evaluated AFTER (review M-FG2 of #176).** After the activation gate and under the locks (slice 6 steps 1 and 2, plus the per-destination lock), `backup-retention-apply` first runs the completion of interrupted deletions for the root it was called for: the slice 6 step 7 resume of a `.deleting-*` folder and the L2 recovery of a missing 2016. Only then is the status of this section evaluated, and it decides whether any NEW deletion may start. Completion deletes nothing that an audited 2015 did not already commit to, so it does not need a healthy status; without this order an interrupted deletion would raise the status, the status would refuse the apply, and the resume could never run.
    * At a DESTINATION a deletion is resumed or recovered only when the system audit stream in the database holds the 2015 for that run id, status hash and destination id. The `deleted.json` marker on the share is NOT enough there (the share's writers could plant it): a `.deleting-*` folder or an incomplete tombstone without that 2015 is left untouched (`resume_unverified`, audit 2017) and reported.
    * How this relates to the code of slice 6 as it is today (`RetentionService.ExecuteAsync`, verified): the status check (`backups_not_current`) stands BEFORE the resume loop, as in the slice 6 step list (step 3 before step 7). It does not deadlock today only because the status handed to the executor is the run status without `retention_deletion_incomplete`, which `backup-status` adds separately. Slice 8's requirement of the overall status removes that implicit exemption, so slice 8 moves the completion ahead of the check explicitly, for the primary and for every destination, and resumes at the primary keep slice 6's rule (the engine's own tombstone marker).

  Any exit 8 or exit 1 anywhere that remains AFTER that completion (a lagging, stale, missing, corrupt or conflicting copy, stale staging, `recovery_untested`, an earlier deletion that could not be completed, an unexplained tombstone) refuses every new deletion with `backups_not_current` (audit 2017). The per-destination lock of D-185 is held exclusively. Audit 2015/2016/2017 carry the destination id; tombstones are written under `<D>/.retention/tombstones/`.
  * **Consequence of the MD-Q2 default, stated here because it is easy to miss.** A destination on the primary's volume reports `destination_same_volume` (exit 8) on every check, so the overall status is never 0 while such a destination is configured, and NO retention deletion is possible, at the primary or at any destination, until the destination is moved to another volume, removed from the configuration, or the owner answers MD-Q2. This is deliberate and preserving: the warning is not exempted from the deletion check, because a same-volume "copy" is not a second copy against disk loss, and exempting a warning would weaken slice 6. Nothing is deleted today in any case (D-137).
* **D-137 applies to every destination with no separate switch:** the same three layers keep deletion INACTIVE everywhere, and the activation PR (4.3-ACT) activates it for all roots together. Nothing in this slice activates deletion.

### D-188, audit events (system stream, block 2040-2049, lead allocation)

| Code | Event | Payload (never a path, a name or file contents) |
|------|-------|--------------------------------------------------|
| 2040 | `BackupReplicaVerified` (verified by read-back after flush; committed BEFORE the rename, so it records "publish attempted", see D-185 step 5) | run id, status hash, destination id and kind, files, bytes, duration, `reverified` flag |
| 2041 | `BackupReplicaFailed` | run id, destination id, reason code (`destination_unreachable`, `destination_full`, `replica_verification_failed`, `replica_conflict`, `replica_publish_failed`, `replica_unencrypted_refused`); for `replica_publish_failed` also the status hash of the 2040 it compensates |
| 2042 | `BackupReplicaCorrupt` | run id, destination id, the failing check (signature, size, header hash, chunk hash, unexpected file) |
| 2043 | `BackupDestinationRefused` | destination id, reason (`destination_overlaps_source`, `destination_duplicate`, `symbolic_link_refused`, `destination_config_invalid`) |
| 2044-2049 | reserved for this slice's follow-ups | - |

* Every command fails closed when its event cannot be written: a copy is not reported as verified without 2040, and nothing is renamed into place before the event of its verification is committed. A rename that fails after 2040 is answered by 2041 `replica_publish_failed` (D-185 step 5); no reader may take a 2040 alone as proof that a copy exists.
* The 2014-2017 retention events gain a `destination` field (`primary` or the destination id); their codes do not change.

### D-189, restore from a copy
* `trial-restore-company` and `backup-recovery-test` accept a run folder at any destination: they verify signatures and digests first (slice 5), exactly as at the primary. A copy is therefore usable for every restore path without the primary.
* The slice 7 cutover (when built) takes its source set from any verified copy under the same rules; this slice changes nothing there.

### Exact tests (implementation PR)
* **Unit:**
  * configuration parser: valid arrays and environment variables, duplicate ids, invalid ids, relative paths, more than 8 destinations, `DailyKeep`/`WeeklyKeep` below the minimums (exit 3, never clamped);
  * overlap matrix: each destination against the primary root, other destinations, the attachment root and every key directory, in both directions, through a symbolic link, with a trailing separator and with case differences on Windows;
  * status rules: every combination of primary verdict and destination outcome gives the exit code of D-186 (`replica_stale`, `replica_missing`, `replica_lagging`, `replica_corrupt`, `replica_conflict`, `replica_incoming_stale`, `destination_same_volume`);
  * the severity order 1 > 8 > 0 (M1): every pair and triple of contributions, in every order of evaluation; in particular an exit-1 destination together with an exit-8 destination, with an exit-8 primary (`partial`, `recovery_untested`) and with `destination_same_volume` gives 1, never 8; a guard test that the combination is not a numeric maximum; 2, 3 and 4 are never produced by the combination;
  * `backup-run` against `backup-status` (L1): for every destination state the `backup-run` code is at least as severe as the `backup-status` code computed immediately afterwards, and both come from the same status function;
  * the `replication_pending` keep reason (L3): a replicable run absent from one configured destination, an unreadable destination, a newly added destination, a published copy that no longer verifies, a 2040 event without a folder (still pending);
  * the tombstone exception (M-FG1, L-FG1): a tombstone with its matching 2015 and 2016 for that destination id and no run or `.deleting-*` folder (not pending, skipped by `backup-replicate`); a PLANTED tombstone without a 2016 (pending, copied again, `replica_tombstone_unexplained` exit 1); a tombstone with a 2015 but no 2016; a 2015/2016 pair for the primary or for a DIFFERENT destination id (pending, exit 1); a tombstone whose status hash is not the primary's; a tombstone whose run folder still exists and one with a `.deleting-*` folder (the exception does not apply); a 2015/2016 pair with no tombstone (pending); an explicit `backup-replicate --run --destination` of a genuinely deleted run after `DailyKeep` was raised (copied, tombstone and audit kept);
  * interrupted deletions (M-FG2): a crash after 2015 at a destination (`retention_deletion_incomplete` exit 1, not `replica_tombstone_unexplained`), the next `backup-retention-apply` completes it BEFORE the status check, writes its 2016, and the status returns to 0; the same at the primary with the overall status required; a PLANTED tombstone with a deletion marker and a `.deleting-*` folder but no 2015 in the database is NOT resumed (`resume_unverified`, `replica_tombstone_unexplained` exit 1, new deletions refused); a 2015 for another destination id does not allow the resume;
  * replay of an old pair (L-FG2): delete at D (2015, 2016), explicit re-copy (2040), run folder removed by hand: pending and copied again, the old pair does not count; the same when the re-copy happened during an interrupted deletion (2040 between the 2015 and the 2016);
  * the deletion precondition (L2): root status against overall status; any exit 8 anywhere, `destination_same_volume` included, refuses with `backups_not_current` at the primary and at every destination;
  * destination retention uses the destination's own minimums and the global holds, pins and recovery-test facts.
* **PostgreSQL class `BackupReplicationTests`** (disposable cluster on 5433; the isolated runner):
  * a real encrypted run replicated to two destinations: both copies verify, are byte-identical to the primary, carry the same status hash, and audit 2040 is written twice; `backup-status` exits 0;
  * one destination unreachable (a removed folder) that holds NO earlier copy: the other is verified, 2041 is written, `backup-run` exits 1 (`replication_incomplete` and `replica_missing`) and `backup-status` reports `replica_missing` (exit 1): the same severity in both;
  * one destination unreachable for one run while it still holds the previous run's verified copy within its age: `backup-run` exits 8 (`replication_incomplete`, `replica_lagging`) and `backup-status` exits 8;
  * a rename that fails after verification (test seam): 2040 is followed by 2041 `replica_publish_failed`, no run folder appears under the run id, the destination's outcome is `failed`, and the run stays `replication_pending` at the primary;
  * the copy files are closed and reopened before verification (the read handle is not the write handle), and a flush error fails the copy;
  * a flipped byte injected at the destination between copy and verification (test seam): verification fails, nothing is renamed into place, no run folder appears under the run id;
  * a crash between copy and rename (test seam): the `.incoming-*` folder is never a run, `backup-status` reports it, and the next `backup-replicate` produces a verified copy;
  * catch-up: a destination offline for two runs receives both through `backup-replicate`, oldest first;
  * an existing copy with a different status hash is never overwritten (`replica_conflict`);
  * an unencrypted DEV/TEST run is never replicated;
  * `backup-replica-verify` detects a corrupted copy (2042) and leaves it in place;
  * a trial restore from a destination copy gives the same catalog-and-content digest as one from the primary;
  * retention plan at a destination honours a legal hold placed on the run id and the destination's raised `DailyKeep`; the primary keeps a run with `replication_pending`; `backup-retention-apply --destination` stays INACTIVE (2017, nothing deleted);
  * an overlapping destination is refused before anything is written (2043).
* **Drill log:** the drill of `docs/qa/DRILL-BACKUP-ENCRYPTION-ACCEPTANCE.md` is extended with a network destination (an operating-system mounted share) and a restore from that copy; recorded as a manual drill where CI cannot mount a share.
* The PostgreSQL registry in `.github/workflows/backend.yml` is raised by the new tests at the merge turn.

### Decisions (D-182..D-189, recorded in `docs/DECISIONS.md`, status planned)
D-182 the model (primary root, byte-identical copies of complete signed encrypted runs; failed, unsigned and unencrypted runs never replicated); D-183 the configuration format; D-184 unsafe and overlapping destinations (the trial-restore staging folders included); D-185 atomic copy and verification at the destination (read-back after flush, close and reopen, with its stated limitation; 2040 before the rename, 2041 `replica_publish_failed` as the compensating event); D-186 success semantics and per-destination status (severity order 1 > 8 > 0, one status function for `backup-run` and `backup-status`, root and overall status; all configured destinations required by default, MD-Q1); D-187 retention per destination (slice 6 at every destination, `replication_pending` for every replicable run without a verified copy at every configured destination, the tombstone exception only with the matching 2015 and 2016 in the system audit stream, deletion only with the overall status ok, D-137 everywhere); D-188 audit events 2040-2043, 2044-2049 reserved; D-189 restore from a copy.

### Owner questions (policy not covered by D-085, D-086 or SRS 16; the preserving default applies until answered)
* **MD-Q1. How many verified copies make a run successful?** SRS 16 requires multiple destinations but no count. **Default: EVERY configured destination must hold a verified copy**; fewer gives at least exit 8 (`replication_incomplete`), and exit 1 when no copy of the run was verified anywhere or when a destination is left with no verified copy within its age (D-186, severity order 1 > 8 > 0). Options: a configured minimum number of copies (for example 1 of 2); or a set of required destinations with the others best-effort. Also: whether an installation with NO destination configured must alert (default: no change, `replicas: none` is reported but does not alert, as today).
* **MD-Q2. Must a destination be on a different volume, machine or site?** **Default: not refused; a destination on the same volume as the primary is reported (`destination_same_volume`, exit 8) on every status check and every `backup-run`.** Consequence of this default (D-187): while such a destination is configured the overall status is never 0, so no retention deletion is possible at the primary or at any destination; this is preserving and is not exempted. Options: refuse same-volume destinations (then the permanent warning disappears, because the configuration is refused with exit 3 instead); require at least one destination of kind `network`; require an off-site copy; or accept a same-volume destination as information only, without the warning exit (this would let retention delete while the only extra copy shares the primary's disk, so it is the owner's decision and not a default).

**Owner decisions 2026-11-21 (consolidated package; recorded once in `docs/DECISIONS.md`, where the owner's wording stands; this block only points to it and does not rewrite the plan).** Slice 8: MD-Q1 and MD-Q2 = D-230, which CONFIRMS the merged plan (D-186: full success only with a verified copy at every configured destination; the same-volume warning stays and no deletion is possible while it stands, D-187). Two readings are kept on the strict side and are for the implementation review: every configured destination is a required one, and an installation whose backups have no independent destination is not a case in which deletion may be enabled (see `docs/INTEGRATION-STATUS.md`).

### Implementation slices (lead-approved cut, 2026-11-22)
Slice 8 is built in five independently reviewed pull requests. None activates deletion (D-137, D-227) or the cutover; none needs a migration (if one turns out to be needed, work stops and the lead allocates the number).
* **S8a:** configuration, safety checks and the status function (built, below).
* **S8b:** the copy engine, `backup-replicate` and replication at the end of `backup-run` (D-182, D-185; audit 2040, 2041, 2043) (built, below).
* **S8c:** `backup-replica-verify`, corrupt and re-verified copies in the status (audit 2042), restore from a copy (D-189).
* **S8d:** retention per destination (D-187), with L-FG3 and the D-230 prerequisite inside the deletion executor.
* **S8e:** the encryption-key retirement report requires every configured destination (after #182).

Lead rulings of 2026-11-22 for the implementation:
* With destinations configured, `backup-status` reads the system database read-only from the slice that first needs audit evidence (S8c: 2042; S8d: the 2015/2016 pairs). Unreachable database = exit 1 (`audit_unreadable`). Without destinations, or on a destination path: no database read. This changes the "no database" property of `backup-status` and is documented with it.
* The new audit reads parse the detail tokens exactly (a `LIKE` match would take `nas-1` for `nas-10`). Slice 6's existing `LIKE` reads stay as they are: they carry no destination token, so the case cannot arise there (recorded).
* The per-destination advisory lock uses the run-lock namespace with object `0x10000` plus a 16-bit stable hash of the destination id. A collision only serialises two destinations.
* The retirement report (S8e) uses "at least one root that is not a destination" as the proxy for the primary root. No primary-root setting is added (not in D-183); this is a recorded limitation.

### As built: S8a (configuration, safety checks, per-destination status)
* **Configuration (D-183):** `BackupDestinations.FromConfiguration` reads `Hasm:Backup:Destinations` strictly. Any of the following refuses the WHOLE configuration with `destination_config_invalid` (exit 3); the cause names the entry and the rule, never a path:
  * more than 8 entries;
  * entries not numbered exactly 0 to n-1 (a gap in environment variables would otherwise drop an entry silently);
  * an entry or field that is not a plain value, or an unknown field (a misspelt `DailyKeep` must not fall back to the minimum);
  * a missing, malformed (`[a-z0-9-]{1,32}`) or repeated `Id`;
  * a `Path` that is not absolute or carries surrounding white space;
  * a `Kind` other than `local` or `network`;
  * an optional field that is empty, not a plain decimal or out of range (`MaxAgeHours` 1 to 8760).

  `DailyKeep` below 7 or `WeeklyKeep` below 4 is `retention_below_minimum` (exit 3, never clamped). No destination configured is today's behaviour.
* **Safety checks (D-184), `BackupDestinations.Check`, read-only (nothing is created):**
  * `symbolic_link_refused`: the destination or any folder on its path is a link, a dangling one included;
  * `destination_duplicate`: it resolves to the primary root or to another destination;
  * `destination_overlaps_source`: it lies inside or contains the primary, another destination, the attachment root, or the encryption, audit or signing key directory.
* **Same volume:** the volume is the device of the mount that holds the path (`/proc/self/mountinfo`, longest mount point) on Linux, and the volume root on Windows. A destination that does not exist yet lies on its nearest existing parent's volume. A volume that cannot be determined counts as the primary's, because independence cannot be shown.
* **Residual (PR 209, L2, recorded):** the overlap and duplicate checks compare resolved paths and do not normalize Windows 8.3 short names or the case of a case-insensitive Linux mount, so two spellings of one folder can pass as different folders there. Operators configure each folder under one canonical spelling.
* **The other direction of D-184:** `backup-keys` refuses an export folder inside or around a configured destination (`destination_in_backup_root`, D-087). So does `trial-restore-company` for its attachment target, checked before any key is loaded. The trial-restore staging folders are arguments, not configuration, so this is where D-184's "trial-restore staging folders" is enforced.
* **The ONE status function (D-186), `ReplicaStatus.Contribution`:**
  * exit 1: `replica_missing` (no verified copy; also an absent or unreadable folder), `replica_stale` (older than the destination's `MaxAgeHours` or the call's limit), `replica_conflict`, `replica_corrupt`, `replica_tombstone_unexplained`;
  * exit 8: `replica_lagging`, `replica_incoming_stale` (staging older than 24 hours), `destination_same_volume`.

  `BackupSeverity.Worst` combines by severity (1 > 8 > 0) and throws for 2, 3 and 4, which are never combined.
* **Reading a destination (`ReplicaStatus.Inspect`), read-only and without a database:**
  * every run folder is read through its signed status, exactly as at the primary;
  * a copy is VERIFIED when it is replicable (finished, signature verified, encrypted, clean, verdict `ok` or `partial`) and, while the primary holds that run id, carries the primary's status hash;
  * another hash is a conflict, and a run folder that does not verify is corrupt (a destination only ever holds published copies, D-185);
  * the full re-hash of every file is `backup-replica-verify` (S8c);
  * a tombstone or a `.deleting-*` folder at a destination counts as unexplained in S8a: no deletion at a destination exists yet, and the audit evidence that can explain one is read in S8d.
* **`backup-status <root>`:** the destination configuration is read and checked first; a refusal is exit 3 and is returned alone. Then the primary's root status (slices 4 to 6, unchanged) is computed, then each destination's line and findings, then the OVERALL status (the worst of all), which is the exit code. With no destination configured it prints `Replicas: none` and the exit code is the root status, as before. On a destination folder it gives that folder's root status only. It stays read-only and without a database in S8a, and writes no audit event.
* **Not in S8a:** no copy is made and no audit event is written (2040 to 2043 come with S8b and S8c). A configured destination therefore reads `replica_missing` (exit 1) until S8b ships: that is the intended fail-closed state.
* **Tests:**
  * unit `BackupDestinationsTests`: the parser, every invalid form, environment variables, the minimums, the overlap matrix in both directions with trailing separators, links (a dangling one included), the volume probe and the mountinfo parser, and the refusals of a key export or a trial-restore folder;
  * unit `ReplicaStatusTests`: the severity order over every triple in several orders, refusals never combined, every finding with its severity, a destination's own `MaxAgeHours`, the overall status, the replicable predicate, and the inspection of a folder;
  * PostgreSQL `BackupReplicationTests` (3), over real encrypted runs with byte-for-byte copies made by the test:
    * missing, verified (another volume, 0), same volume (8, also an unknown volume), a status call on the destination folder, lagging, a corrupt copy, an unexplained tombstone, stale staging, through the service and the `Hasm.Admin` binary, with no audit event written;
    * invalid, overlapping, duplicate and linked configurations (exit 3, nothing created);
    * `backup-keys` and `trial-restore-company` into or around a destination refused, nothing written.

### Owner decisions this slice relies on (not re-asked)
D-085 (encryption with an offline recovery key, signed manifests; copies are of encrypted signed runs only), D-086 (retention minimums at least 7 daily and 4 weekly, configurable upward only; never delete the last recoverable, held or chain-needed backup; deletion inactive until the safeguards are tested), D-087 (the key export goes to a separate destination), D-088 (unencrypted sets are DEV/TEST only), D-137 (deletion inactive until an owner-instructed activation PR).

## Limitations (recorded)
* The manifest detects corruption and loss; it is not a signature and the set is not encrypted (later slice).
* `KeyIdsRequired` covers attachment envelopes only. Encrypted import-file originals are outside the attachment folder and are not part of any set today (D-179; decided by the owner, D-224; not built). Their LIVE key coverage is the import rewrap outcome and the encryption-key retirement report (D-180, D-181).
* Restore into a NEW database only; replacing a live company database (cutover) and full-installation restore are not built. Scheduling (by the operating system), monitoring and measured durations: slice 4 (implemented). Set encryption and retention: owner package 4.3-2.

## BLOCKED / owner decisions
* Audited restore acknowledgement and a new checkpoint epoch after a company restore: decided (D-081); planned in slice 7 (D-143..D-151). Cutovers stay unavailable (`RestoreSafeguards.CutoverAvailable = false`) until the engine, the Phase 4.1 approval framework (S4/S8) and an owner-instructed activation are in place; owner questions Q1-Q6 of slice 7.
* Retention/purge of removed attachments (decision R).
* Set encryption and authentication and the key-directory export: decided (D-085, D-087, D-088), slice 5. Retention and deletion of backups: decided (D-086), planned in slice 6; automatic deletion stays INACTIVE until its activation PR (D-137), and the month-end/year-end legal policy waits for owner questions Q1-Q3 of slice 6. Scheduling (by the operating system), monitoring and measured durations: implemented in slice 4. Multiple destinations (local and network copies, SRS 16): planned in slice 8 (D-182..D-189; owner questions MD-Q1 and MD-Q2), not built. Full-installation restore: not started.
