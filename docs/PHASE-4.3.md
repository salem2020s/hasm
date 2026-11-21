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
* **Migration:** system migration `0032_backup_retention.sql` (lead allocation: #133 takes 0031 and merges first; until it does, this tree has a gap at 0031 and the migration loader refuses it, so the branch stays a Draft). No test pins its number; the pinned system-migration counts (`SystemDatabaseTests`, `CompanySchemaTests`) are set to 32 and are final once #133 lands.
* **Review H2 of #134 (fixed):** the attachment-hold tripwire looked for keys that R1 never used, so active Phase R legal holds would have been ignored (fail open). Retention now reads R1's company `LegalHold` table. ANY active hold (company, record or attachment scope) keeps every run containing the company (`attachment_hold`). A hold table that cannot be read keeps everything (`hold_check_failed`). A company whose migration level predates R1 has no hold table and so can have no hold.
  * The exact R1 keys are pinned by a unit test. Any other company-schema table whose name contains "Hold" trips `hold_check_failed` for every company until retention reads it.
  * A PostgreSQL test places a real record-scoped R1 hold and shows that every run is kept and the engine deletes nothing.
* **Activation prerequisites (review L1-L4 of #134; deletion is inactive, so they must be resolved before any owner-approved activation, and are listed with the 4.3-ACT register row):**
  * **L1 (open):** `DeleteRunFolder` re-checks for symbolic links only once per entry while walking. A link swapped in between the check and the delete (time-of-check to time-of-use) must be excluded, by re-checking `LinkTarget` immediately before each delete or by a non-following delete primitive.
  * **L2 (open):** audit 2016 is not resumable. A crash after the folder is gone and before 2016 leaves 2015 without its 2016. The next execution must detect a tombstone without a completion record and write the missing 2016.
  * **L3 (fixed now):** an interrupted deletion is resumed only when the engine's own tombstone (written after audit 2015) exists; otherwise the folder is left for the operator (`resume_unverified`, audit 2017).
  * **L4 (fixed now):** a refusal for a missing runtime prerequisite (encryption, configuration, restore safeguards) writes audit 2017 too.
* **Tests:**
  * unit tests: `RetentionClassifierTests`, `RetentionExecutorTests`, `BackupRetentionSchemaDerivationTests`, and the strengthened TOTP pin;
  * PostgreSQL class `BackupRetentionTests` (5): the preview per company calendar and time zone, holds and guards, inactive deletion, the engine through the seam with a remaining run that still recovers, and the locks.
* **Not built here:** the company legal policy for month-end and year-end runs (owner questions Q1-Q3), hold release (Phase 4.1), the slice 7 pins and safeguards, and activation (an owner-instructed PR).

## Limitations (recorded)
* The manifest detects corruption and loss; it is not a signature and the set is not encrypted (later slice).
* `KeyIdsRequired` covers attachment envelopes only. Encrypted import-file originals are outside the attachment folder and need their own key coverage (the import-file rewrap retirability report, decision K follow-up, is not built yet).
* Restore into a NEW database only; replacing a live company database (cutover) and full-installation restore are not built. Scheduling (by the operating system), monitoring and measured durations: slice 4 (implemented). Set encryption and retention: owner package 4.3-2.

## BLOCKED / owner decisions
* Audited restore acknowledgement and a new checkpoint epoch after a company restore (`docs/DEVELOPMENT.md`, restore limitations): policy needs an owner decision.
* Retention/purge of removed attachments (decision R).
* Set encryption and authentication and the key-directory export: decided (D-085, D-087, D-088), slice 5. Retention and deletion of backups: decided (D-086), planned in slice 6; automatic deletion stays INACTIVE until its activation PR (D-137), and the month-end/year-end legal policy waits for owner questions Q1-Q3 of slice 6. Scheduling (by the operating system), monitoring and measured durations: implemented in slice 4. Destinations other than a local or mounted folder, and full-installation restore: not started.
