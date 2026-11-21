# Phase 4.4: Signed manual updates - PLAN (docs only)

Sources:
* **SRS 16.5:** updates are MANUAL ONLY, as signed packages delivered by download or by offline transfer. Before applying, the update checks the signature, compatibility, the database version, permissions and the backup. It rolls back safely, and the Web and Desktop clients stay compatible.
* **SRS 2** (offline licence and update are supported) and **D-033** (updates are manual only, online or offline, through signed packages with compatibility checks and recovery).
* **ARCHITECTURE 12:** manual update through signed packages, a preflight of the migrations and the system state, then a health check after the upgrade.
* **ROADMAP 4.4:** signed packages, compatibility and migration preflight, the backup requirement, safe rollback and client compatibility. Acceptance: a tampered package is refused, and a failed upgrade rolls back. Mandatory tests: upgrade tests across versions. **Database migration implications: none.**

Decision ids **D-170..D-178** (lead allocation, confirmed; audit block 2030-2039; technical defaults under D-048, chosen fail-closed). Owner questions **4.4-Q1..Q4** are separate and listed at the end; they are not decided here.

Dependencies:
* **Phase 4.3:** encrypted, signed backup runs (slice 5), the trial-restore path (slice 3) and the rename switch with its journal (slice 7, D-145).
* **Phase 4.1:** the named-administrator login (D-099) and, for Q2, the approval framework.
* **Phase 4.5** builds the installers and the service integration on top of this mechanism.

Risk: **High.** An update replaces all of the code, including the security code, and migrates every database.

## Scope
* **In scope:**
  * the update package format and its signature;
  * the administrative commands `update-verify`, `update-preflight`, `update-apply`, `update-commit`, `update-rollback` and `update-status`;
  * the compatibility and migration preflight;
  * the mandatory pre-update backup;
  * pre-update database copies and the automatic rollback;
  * the post-update health check;
  * the client-compatibility contract for Web and Desktop;
  * system audit events;
  * upgrade tests across versions.
* **Not in scope:**
  * installers, the service manager integration and the first-run wizard (4.5);
  * the signed DESKTOP installer and its operating-system code signing (4.5). This phase only carries the desktop installer as a payload and checks the client version range;
  * licence entitlement for updates (4.2, owner question Q4);
  * automatic or background updates of any kind (forbidden by SRS 16.5).
* **No database migration** (ROADMAP 4.4):
  * the update journal is a file;
  * the audit codes are constants;
  * the copies are ordinary databases made with `CREATE DATABASE ... TEMPLATE`.

  A package whose own migrations need something from this phase is impossible by construction, because this phase adds no schema.

## Package format (D-171)
* **Container:** one file, `hasm-update-<version>.hup`. It is a ZIP archive (`System.IO.Compression`, built in; no new dependency) with stored or deflated entries.
* **Entries:**
  * `update-manifest.json`;
  * `update-manifest.json.sig`, a detached signature in the slice 5 format: base64 IEEE-P1363 ECDSA P-256 over the exact manifest bytes, plus the key id;
  * `payload/<platform>/...`: the backend (API and administrative tool, self-contained, so no runtime prerequisite changes) and the Web bundle;
  * optionally, `desktop/<platform>/...`: the desktop installer, separately code-signed in 4.5;
  * `notes/release-notes.<lang>.md`.
* **The manifest (signed; format version 1):**
  * product `hasm`, the package version (SemVer), the build id and `createdUtc`;
  * the update signing key id;
  * `upgradeFrom`: the range of installed versions this package may be applied to;
  * `platforms`;
  * **database levels:** the system and company migration levels and the ordered list of EVERY migration's name and checksum. With these, preflight can compare against the live journals without running new code;
  * `postgresMajorVersions` (supported);
  * `apiCompatibility`: the API compatibility level, and the supported desktop client version range;
  * for each payload file: its path, size, SHA-256 and platform.
* **Reading rules (fail closed; stable codes):**
  * The manifest and `.sig` are read first, each with a size bound (at most 1 MiB). The signature is verified BEFORE the manifest is parsed (as D-116: nothing unauthenticated is interpreted).
  * Then each payload entry is streamed into a private (0700) staging folder while its SHA-256 is computed over the same bytes. A mismatch or a size difference deletes the staging folder (`package_digest_mismatch`).
  * **Refused:**
    * an entry that is not listed in the manifest, or a listed entry that is missing (`package_entry_unlisted`, `package_entry_missing`);
    * duplicate names, including case-insensitive duplicates for Windows;
    * absolute paths, `..`, drive letters, symbolic links, and names that would escape the staging folder (`package_path_invalid`; zip-slip);
    * a total uncompressed size above the manifest's declared sum, or a compression ratio beyond a fixed bound (`package_too_large`; zip bomb);
    * an unknown format version or an unknown key id.
  * Nothing from the package is executed before the whole package has been verified.
* **No encryption:** the package is public software. Integrity and authenticity come from the signature.

## Signature and trust (D-172)
* **Reused machinery from Phase 4.3 slice 5:**
  * `SignedDocuments`: detached `.sig`, the IEEE-P1363 signature over the exact bytes, and the size-bounded read before parsing;
  * the key-id scheme: a prefix plus 16 hex characters of SHA-256 over the SubjectPublicKeyInfo, derived and never typed;
  * `SigningTrust`: the status list (`active`, `retired` with `retiredUtc`, `compromised`). A compromised key is refused (`signing_key_compromised`). A retired key verifies only packages whose signed `createdUtc` is before its retirement date, and that date is held in the trust list, never taken from the package.
* **A separate trust domain, by construction:**
  * Backup manifests are signed by each INSTALLATION's own server key (`sk-...`).
  * Update packages are signed by the PUBLISHER (HASM) with an update signing key (`uk-...`). An installation could not sign its own updates, and a server compromise must not let a forged update verify.
  * So the update trust list is SEPARATE from the backup signing trust (`--signing-trust`) and never reads it.
  * A `uk-` key is refused anywhere a backup signature is expected, and an `sk-` key is refused for packages (`signing_key_wrong_purpose`).
* **Where the update trust comes from:**
  * The update trust list (public keys and statuses) is compiled into each release and installed with it.
  * A package may carry a NEW trust list, which takes effect only after the whole package has verified under the CURRENT installed list. This is how keys rotate, and how a key is marked compromised for installations that are offline.
  * Configuration cannot add a trust key: a local setting that adds keys would turn a stolen administrator account into a code-injection path.
* **Residual limitations:**
  * An installation that never applies a later package keeps trusting a key the publisher has since compromised. The publisher's advisory is then the only channel. This is inherent to offline operation.
  * Key custody is owner question **Q1**.

## Compatibility and migration preflight (D-173)
`update-preflight <package>` changes nothing live and needs no downtime. It verifies the package (above), then checks the following:
1. **Version path:**
   * the installed version is inside `upgradeFrom`;
   * the package version is HIGHER than the installed one: a downgrade or a re-apply is refused (`update_not_newer`);
   * a skipped version outside the range needs the intermediate package (`update_path_unsupported`).
2. **Platform and PostgreSQL:** the platform matches, and the PostgreSQL major version is in the supported list (`postgres_version_unsupported`).
3. **Migration journals:**
   * For the system database and EVERY registered company, the applied migrations must be an exact prefix of the package's list: the same names and checksums, in order.
   * Refused: an edited or unknown applied migration (`migration_journal_incompatible`), a company that is not `Ready` (`company_not_ready`; preflight never migrates a company that is `Provisioning`, `Failed` or `Restoring`), and a missing or unreadable journal.
4. **Trial migration on copies:**
   * Each live database (system and every company) is copied into a disposable database on the same cluster by a streamed `pg_dump | pg_restore`. No file is written, so there is no plaintext at rest (D-091). No recovery private key is needed: it stays offline (slice 5). No downtime is needed either, because `CREATE DATABASE ... TEMPLATE` would require the source to have no sessions.
   * The package's NEW administrative tool, from the verified staging folder, migrates those copies forward.
   * Then the schema gate (`CompanySchemaGate`) and `audit-verify` of every stream run on the copies.
   * The copies are dropped afterwards, and the runtime roles never get a grant on them.
   * This proves on current real data that the migrations apply. A failure refuses the update with the failing database's TAG and migration (`trial_migration_failed`).
   * That the BACKUP restores is proven separately by the recovery test (slice 5). `backup-status`, which must be ok for the update (D-174), already refuses while `recovery_untested`.
5. **Permissions (SRS "الصلاحيات"):**
   * the operator is a named administrator with MFA (D-099; while that login does not exist, the interim mode of the administrative tool, as in slice 5);
   * the database admin account can create databases (`CREATEDB`, for the copies) and run migrations;
   * the service account can write the installation directory and owns the staging folder;
   * the key, credential and checkpoint directories are readable and owner-only.
6. **Space:**
   * The pre-update copies need about the size of every database (`pg_database_size`), and the new version needs its payload size.
   * Space is measured where the updater can see it.
   * When PostgreSQL runs on another server (the split model), the operator confirms the free space there with a typed acknowledgement, recorded in the journal (`db_space_unconfirmed` without it).
7. **Clients:** the report lists the desktop client range of the package, so administrators can plan the workstation installs (D-177).

The result is a human-readable report plus a JSON report. `update-apply` re-runs checks 1-3, 5 and 6 itself, after quiescing, and refuses if anything changed.

## Backup requirement before applying (D-174)
* `update-apply` takes its OWN full run (system database and every company) AFTER quiescing (step 2 below), encrypted and signed (slice 5). It then requires:
  * `backup-status` to be ok for that run;
  * the run to verify against the backup signing trust.
* **Refused:**
  * no encryption configured (`backup_encryption_not_configured`);
  * an unencrypted DEV/TEST set: the update refuses outside the DEV/TEST acknowledgement, exactly as slice 5 does.
* **The pre-update run is the recovery point if the copies are lost.** Its run id is recorded in the journal and in the audit events.
* **Retention:**
  * Slice 6's retention rules keep the newest runs, and deletion is inactive.
  * A dedicated retention pin for pre-update runs would need a schema change, which this phase does not make. If the lead later allocates one, it is added there.
* **A recovery test is not required per update.** The standing rule of slice 5 applies: `backup-status` refuses while the active recovery key has no successful recovery test (`recovery_untested`).

## The apply procedure (D-175)
`update-apply <package>` is run by a named administrator (and, per Q2, with an approval). It is journaled step by step in an append-only, owner-only update journal file (`Hasm__Update__StateDirectory`): `intent` and `done` per step, with the same observed-state resume rules as the slice 7 switch (D-145).
1. **Verify and preflight:** the package, then checks 1-3, 5 and 6 again.
2. **Quiesce:**
   * every API instance must be stopped, which is checked: no session of any company runtime role or the system runtime role in `pg_stat_activity` (`api_still_running`). The check is repeated before step 7;
   * no backup, retention, restore or provisioning is running (the existing locks are taken and held until the outcome);
   * while the copies are taken (step 5), new connections to the source databases are blocked (`ALLOW_CONNECTIONS false`), because `CREATE DATABASE ... TEMPLATE` needs a source without sessions. Connections are re-allowed for the migration, with the API still stopped.
3. **Audit `UpdateStarted`** in the system stream, BEFORE the copies are taken, so the copies contain it.
4. **Pre-update backup** (D-174).
5. **Pre-update copies:**
   * `CREATE DATABASE <copy> TEMPLATE <live>` for the system database and every company database (generated opaque names; database TAGS only in logs and events);
   * a TEMPLATE copy inherits the source's grants, so the runtime role's privileges, `CONNECT` included, are revoked on each copy right after it is created and verified absent. The copies are reachable only by the administrator.
6. **Install side by side:**
   * The verified payload is moved into `versions/<version>/`, and the active-version pointer is switched atomically (a rename of a pointer file, or a symbolic link where the platform allows one).
   * The previous version's directory stays.
7. **Migrate:** the NEW administrative tool runs `init-system-db`, `migrate-companies` and `grant-company-runtime` for every company, as in the documented upgrade order.
8. **Health check (read-only):**
   * the schema gate for every company, the runtime login with the installed credentials, and the privilege proof of `grant-company-runtime`;
   * `audit-verify` of every stream, with heads equal to those recorded at step 3;
   * the attachment key ids and the audit key ids still resolve;
   * the new API starts in a health-check mode (no external listener) and answers `/api/v1/health` with the package version and the compatibility level.
9. **Reopen and await commit:**
   * connections are re-allowed and the API starts. Stopping and starting the API service belongs to the installation model's service adapter (4.5). Until 4.5, the operator stops and starts the service when the command asks, and the command verifies each state through `pg_stat_activity` and the health endpoint;
   * the update is `Applied`, and the operator runs `update-commit` once satisfied (see rollback). `update-status` shows the state.
10. **Audit:** `UpdateApplied`, written in the new version's system stream (it is a continuation of the same chain).

**No audit write between the copy and the outcome.** Between step 5 and step 9, nothing writes to an audit stream:
* the migrations write their journal rows only;
* the update's own events are written at step 3 (before the copies) and at step 10 or at rollback (after the state is settled).

Because of this, a rollback to the copies never leaves a checkpoint ahead of its stream (`audit_stream_truncated`). A test pins it.

## Rollback (D-176)
* **Automatic, before reopening:** any failure in steps 5-8 rolls back from the journal:
  1. the pointer is switched back to the previous version;
  2. each migrated database is renamed to a RETAINED name, and its copy is renamed back to the live name (the D-145 rename switch). The physical names, registry rows and credentials stay valid;
  3. runtime grants are re-applied to the restored databases and verified, and the retained failed databases keep no runtime grant;
  4. connections are re-allowed, and the old version starts and passes its own health check;
  5. audit `UpdateRolledBack`, with the failed step and code.

  This meets "a failed upgrade rolls back". It needs no restore from backup.
* **Resume:** `update-rollback` (or a restart after a crash) decides from the OBSERVED state: the pointer, the database names, `ALLOW_CONNECTIONS` and the grants. It never acts by hand-edited journal state.
  * Before the first rename the rule is BACK.
  * After the health check has passed the rule is FORWARD (finish reopening).
* **After reopening:**
  * The automatic rollback window ends when the installation reopens for business (step 9). From then on, rolling back would discard business written under the new version, which is the same loss question as D-153.
  * A rollback after reopening is therefore NOT offered. Recovery after that point uses the restore procedures (4.3 slice 7, and the full-installation restore of slice 8) with their safeguards.
  * Owner question **Q3** holds the alternative.
* **What stays after commit:**
  * The pre-update copies, the retained failed databases (if any) and the previous version's directory stay until the operator releases them with `update-release <id>` (named administrator; audited `UpdateReleased`). Nothing is removed automatically.
  * Releasing copies after a successful update loses no business data: live holds everything, and the pre-update backup run remains.
* **Not rolled back, by design:**
  * Attachments, key directories and credential files are not changed by an update. A package that needs to change them is refused by format version (`package_requires_unsupported_step`).
  * So only the databases and the binaries roll back.

## Client compatibility (D-177)
* **One rule for both clients:** the shared API client sends the client version on every request (header `X-Hasm-Client-Version`). The SERVER refuses a version outside the supported range with `426` and the stable code `client_version_unsupported`. This is enforced server-side, never in the frontend only.
  * A request without the header is refused the same way. Only `GET /api/v1/health` and the anonymous login-status read are exempt, so the login screen can show the message.
* **Web:**
  * The Web bundle ships IN the package and is served by the same backend, so its supported range is exactly the server's version.
  * A browser still running the old bundle after an update receives `426`. It shows the shared "updated, please reload" message (Arabic RTL and English LTR) and reloads on confirmation. Unsaved form input is never discarded silently: the message comes before navigation.
  * Sessions end at the update anyway (step 2).
* **Desktop (Tauri):**
  * The desktop client bundles its own copy of the shared frontend, so it can be older or newer than the server.
  * Its supported range is the package's desktop client range.
  * The shared frontend shows the localized message with the required version range. The platform adapter only says HOW to update: on Desktop, install the signed desktop installer from the update package or its download, manually (no automatic update, SRS 16.5).
* **Shared behaviour:** the compatibility check, its messages and its tests live once in the shared frontend (CLAUDE.md: one frontend, small platform adapters).
  * Before login, `GET /api/v1/health` already returns the version. It gains the compatibility level and the desktop client range: public, non-sensitive values, needed before login.
* **Desktop offline drafts (4.7):** drafts saved locally by an older client must survive an update (SRS 19, item 6: no draft loss). 4.7 must version its draft format and migrate or keep drafts across client versions. Recorded here as a requirement on 4.7.

## Authorization and audit (D-178)
* **Commands:**
  * `update-verify` and `update-preflight`: any administrator;
  * `update-apply`, `update-commit`, `update-rollback` and `update-release`: a named administrator with MFA (D-099).
  * Until the D-099 login exists, these commands run under the administrative tool's interim operating-system mode, recorded as a dated limitation in RELEASE1-ACCEPTANCE, as for slice 5.
* **Independent approval:** owner question **Q2**.
* **System-stream audit events** (block **2030-2039**, allocated by the lead to Phase 4.4). Each carries ids, versions, key ids, run ids and database TAGS only, and every step fails closed when its event cannot be written. The events: `UpdateVerified` (verified or refused, with the code), `UpdatePreflight`, `UpdateStarted`, `UpdateApplied`, `UpdateRolledBack`, `UpdateCommitted`, `UpdateReleased` and `UpdateRefused`.

## Exact tests (implementation PR)
* **Unit tests:**
  * the manifest format (golden test of version 1);
  * signature verification: a tampered manifest, a tampered payload byte, a swapped entry, an unlisted or missing entry, a wrong key, an unknown key, a `compromised` key, a retired key after its date, and a `uk-`/`sk-` purpose mismatch;
  * zip-slip, absolute paths, symbolic links, case-insensitive duplicates and a zip bomb refused;
  * nothing parsed before the signature;
  * trust rotation: a new trust list takes effect only after verifying under the old one;
  * the version-path rules (downgrade, re-apply, out-of-range);
  * the migration-journal prefix rule (edited, unknown, missing);
  * the journal state machine (every crash point resumes forward or backward);
  * the client-version rule (server-side refusal; shared frontend message on Web and Desktop, Arabic and English);
  * a pin that no configuration can add an update trust key.
* **PostgreSQL** (isolated runner; about 8 tests; the registry is raised at push time):
  * **Upgrade across versions:**
    * a fixture installation at version N-1 (the previous release; before Release 1, the build of the merge base), with seeded companies and audit streams;
    * apply a package of version N built in the test: migrations, grants, health check, audit heads unchanged and verified, the API answering with N.
  * **Tampered package refused** before anything changes (acceptance).
  * **Failed upgrade rolls back** (acceptance): a package whose company migration fails on the second company. The old version and databases are live again, the copies are restored by rename, the grants are re-applied, `audit-verify` is Verified with no `audit_stream_truncated`, and there is `UpdateRolledBack`.
  * Crash at each journal step, then resume.
  * Preflight refuses a company that is not `Ready`, and an edited applied migration.
  * The trial migration failing on real data refuses before any downtime.
  * A running API (a session of a runtime role) refuses the apply.
  * The pre-update backup is required: no encryption configured means refused.
* **Frontend:** shared component tests for the reload and `client_version_unsupported` messages (both languages, RTL and LTR), the typecheck, the production build and the Tauri build check.

## Decisions (D-170..D-178; recorded in `docs/DECISIONS.md`, status planned)
* **D-170** scope and principles: manual only; no automatic updates; no migration in this phase.
* **D-171** the package format.
* **D-172** signature and trust: slice 5 machinery reused; a separate publisher trust domain (`uk-`); trust compiled into each release and rotated only by a verified package.
* **D-173** compatibility and migration preflight, with a trial migration on streamed disposable copies.
* **D-174** the pre-update encrypted, signed backup taken after quiescing.
* **D-175** the apply procedure: quiesce, copies by TEMPLATE, side-by-side install, migrate, health check, no audit write between copy and outcome.
* **D-176** rollback: automatic by rename before reopening; after reopening only the restore procedures; nothing removed automatically.
* **D-177** client compatibility: Web served with the server; Desktop range enforced server-side; shared frontend messages.
* **D-178** authorization and audit.

## Open questions for the owner (not decided here; the safe default applies until answered)
* **Q1. Key custody for update signing.** Should the publisher's update signing key be held under a custody that is separate from every other key: the installations' backup manifest signing keys, the licence signing key of 4.2 and the desktop code-signing certificate of 4.5? And who holds it (offline medium, hardware security module, two-person rule)?
  * **Preserving default:** a DEDICATED update signing key, used for nothing else, kept offline and never on a build or update server, with every signing event recorded by the publisher.
  * Technically, the backup signing keys cannot sign updates anyway (they are per installation, D-172). The question is the publisher-side custody and its separation from the licence and code-signing keys.
* **Q2. Independent approval for applying an update.**
  * D-080(6) lists sensitive role grants, encryption-key retirement and restore operations. Updates are not listed.
  * **Preserving default:** `update-apply` requires an independent approval through the Phase 4.1 framework (operation `update.apply`) once it exists. Until then, a named administrator under the interim mode, recorded as a dated limitation (as D-118).
  * The owner may instead decide that a single named administrator suffices.
* **Q3. Rollback after the installation has reopened.**
  * **Preserving default:** none. After reopening, recovery uses the restore procedures with their safeguards, and the pre-update copies and run stay until released.
  * The option for the owner: a rollback window after reopening, which would discard business written under the new version and therefore needs the D-153 accounting rules.
* **Q4. Licence entitlement for updates** (SRS 15: an expired maintenance of a perpetual licence does not stop the owned version; whether it blocks NEW versions is commercial policy, owned by 4.2).
  * **Preserving default:** this phase checks no entitlement and blocks no update for licence reasons. 4.2 decides, with the owner.

## Decisions this phase relies on (not re-asked)
* **Owner decisions:** D-033 (manual updates through signed packages), D-048, D-080(6) (listed actions only; Q2), D-085..D-088 (encrypted, tested backups).
* **Lead decisions reused:** D-091/D-092 and D-116 (no plaintext at rest, signed manifests, authenticate before use), D-099 (named administrator with MFA), D-145 (the rename switch, planned in 4.3 slice 7), D-153 (what a rollback may lose, planned in 4.3 slice 7).
