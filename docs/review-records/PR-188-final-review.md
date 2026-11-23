# Independent review record

* **Pull request:** #188 (migration-guard hardening). This is the owner-mandated final review under the owner's decisions and merge authorization of 2026-11-22 (Q1, Q2, Q3, the X4 deferral, and merge steps 4 and 6).
  * Final delta: the author's commit 46262c4 (on 65c892d) and two merges of main (65c892d, 05a5ce9).
  * The last approved security review was opusSEC's at 8ef20be (record a985ea8).
* **Head SHA reviewed (full 40 characters):** 05a5ce98b18e48b6f6eda43f64f3a58588c181c0
* **Reviewer context:** Agent ODC (independent reviewer, separate agent, not the author sonnetUI185); model family Claude Opus. Prompt (summary): "(A) confirm independently that every migration on main passes the hardened guard, and verify the 26-entry DISABLE/ENABLE allowlist (complete, real checksums, exact triggers); (B) review the security of the final delta against Q1 to Q3 and the X4 deferral, including:
  * Set/Checksum spoofing;
  * the three commands' refusals;
  * the superuser override and the real is_superuser reading;
  * the minimal account privileges;
  * the rewritten test;
  * the pinned pg_-names test;
  * the docs and register rows;
  * both merges (remerge-diff, registry 1720);
  * protected files and weakened tests."
* **Scope read:**
  * The full diff `origin/main...05a5ce9`: 22 files, +2477/−26.
  * In detail, the author's delta `65c892d..46262c4` (20 files):
    * `MigrationGuard.cs`, `MigrationSafety/ApprovedTriggerSwitches.cs`, `MigrationScript.cs`, `MigrationRunner.cs`, `MigrationAccountPolicy.cs`;
    * `SystemDatabaseInitializer.cs`, `CompanyProvisioner.cs`, `CompanyAdministration.cs`, `HasmDatabaseOptions.cs`, `Hasm.Admin/Program.cs`;
    * the unit tests `MigrationTriggerSwitchAllowlistTests`, `MigrationAccountPolicyTests` (with `ApiNeverMigratesTests`) and `MigrationGuardTests`;
    * the PostgreSQL tests `MigrationAdminRefusalTests`, `NonSuperuserMigrationAccountTests` and `MigrationGuardPostCheckTests`;
    * `docs/MIGRATION-GUARD.md`, `docs/DEVELOPMENT.md`, `docs/INTEGRATION-STATUS.md`, `.github/workflows/backend.yml`.
  * The owner's decision text, verbatim (Q1, Q2, Q3, X4, merge authorization).
  * The test-cluster scripts (`scripts/test-pg/new-test-cluster.sh`, `common.sh`).
* **Checks run (commands and results):**
  * **(A1) The allowlist, checked independently in Python** (`allowlist_check.py`, which reads the migration files of `origin/main` with `git show`, not the build):
    * all 71 migrations on main were scanned: system 0001–0035 and company 0001–0036;
    * after removing `--` comments, every mention of `DISABLE|ENABLE [REPLICA|ALWAYS] TRIGGER|RULE` was counted, and the canonical `ALTER TABLE t DISABLE/ENABLE TRIGGER x;` statements were extracted;
    * the checksum was computed as SHA-256 over the text with CRLF→LF.

    Results:
    * exactly 26 migrations contain a pair, all SYSTEM (0006–0030 and 0034); no company migration contains one;
    * the set equals the 26 entries of `ApprovedTriggerSwitches`;
    * each entry's version, name and checksum equal the real file;
    * each entry's trigger list equals that migration's DISABLE statements, which equal its ENABLE statements;
    * there are no other switch forms (mentions = canonical statements);
    * no entry lacks a pair.

    Result: PASS.
  * **(A2) The hardened guard, run by me over every embedded migration.** A local probe test (not committed) ran through `cpu-gate.sh build`:
    * `MigrationScript.LoadEmbedded()` (35, versions 1–35) and `LoadEmbeddedCompany()` (36, versions 1–36), which apply `Parse` and `EnsureAcceptable` per script and `Validate` and `EnsureAcceptableSet` per set;
    * plus `MigrationGuard.Analyze` with a null violation for each of the 71 scripts, and `EnsureAcceptableSet` on both sets.

    All 71 are accepted:
    * 26 report switched triggers, matching A1;
    * three company migrations report replaced functions (0009, 0027, 0029), which are the six `ApprovedFunctionReplacements`.

    The 71 checksums the C# loader computed from the embedded resources equal the A1 checksums of main's files (0 mismatches).
  * **Unit tests** (through the CPU gate): `dotnet test tests/unit/Hasm.Infrastructure.Tests` filtered to MigrationTriggerSwitchAllowlistTests, MigrationGuardTests, MigrationAccountPolicyTests, ApiNeverMigratesTests and the probe: 268/268 passed.
  * **PostgreSQL** (`scripts/test-pg/run-isolated.sh <wt> --build --filter ...` through the CPU gate, own isolated cluster): 20/20 passed, 0 skipped, per the TRX:
    * MigrationAdminRefusalTests 4/4;
    * NonSuperuserMigrationAccountTests 2/2 (the role tests ran, not skipped);
    * MigrationGuardPostCheckTests 12/12;
    * PlanExecutionMigrationCompatibilityTests 1/1;
    * my probe 1/1 (below).
  * **Real superuser probe** (finding L3; local only, not committed). Inside the isolated run, the test read the disposable cluster's bootstrap superuser secret (`$HASM_TEST_ROOT/secrets/superuser.txt`, login `hasm_test_super`, `is_superuser = on`) and showed, with **no seam**:
    * `MigrationAccountPolicy.EnsureAsync` refuses it, and with the override passes with exactly one WARNING line;
    * `MigrationRunner.ApplyAsync` as that superuser is refused, with the journal unchanged; with `AllowSuperuser` it runs, applies nothing pending and logs one WARNING;
    * `SystemDatabaseInitializer` with that superuser as the admin account is refused before CREATE DATABASE (no database created);
    * the real `Hasm.Admin init-system-db` CLI as that superuser exits non-zero naming "superuser", also with `Hasm__Database__AllowSuperuserMigrationAccount=true` and another override-like variable in the environment. The environment cannot switch the override on.
  * **SECURITY DEFINER probe** (finding M1; local unit probe, not committed). `MigrationGuard.EnsureAcceptable` **accepts** all three of:
    * `CREATE FUNCTION ... LANGUAGE sql SECURITY DEFINER AS $$...$$`;
    * `... EXTERNAL SECURITY DEFINER ...`;
    * `CREATE PROCEDURE ... SECURITY DEFINER ...`.
  * Every probe was removed afterwards, and `git status` was clean.
  * **Merges.** `git show --remerge-diff 65c892d` and `05a5ce9` each show only the registry-title conflict in `.github/workflows/backend.yml`: 1274|1699 resolved to 1711, and 1717|1702 resolved to 1720. Nothing else differs from an automatic merge.
  * **Registry:** 256 classes, sum 1720, no duplicates; the step title says 1720. That is main's 1702 plus this PR's 18 (MigrationGuardPostCheckTests 12, MigrationAdminRefusalTests 4, NonSuperuserMigrationAccountTests 2).
  * **pg_ names:** an independent grep of main's migrations finds exactly system 0035 (`pg_has_role`) and company 0036 (`pg_current_xact_id`), matching the pinned test.
  * **Docs:** the Q1, Q2, Q3 and X4 owner blocks are quoted in full, verbatim, in `docs/INTEGRATION-STATUS.md` (checked by script). The register diff touches exactly four rows:
    * X4-PRESET-ROLE (now DEFERRED);
    * MIGRATION-ACCOUNT-PRIVILEGES (now DECIDED);
    * new MIGRATION-TRIGGER-SWITCH;
    * new MIGRATION-NOOP-FAILCLOSED.
  * **Protected files and weakened tests.** No protected file is touched (`tools/verify/**`, `.github/workflows/verification.yml`, `required-checks.json`, `secret-scan-allow.json`, `CODEOWNERS`, `CLAUDE.md`). No migration file differs from main. The only modified (not added) test-related file is `backend.yml`, where the registry is raised. No existing test file of main is modified.
  * **Hosted CI on the exact head, read by REST:** every check succeeded (backend-build-test, postgres-integration, ci-guard, guard-selftest, dependency-scan, docs-traceability, secret-scan); a skipped duplicate of each also ran.
  * **Mergeability:** `mergeable` true, `mergeable_state` clean, base 6ee0653 equals origin/main.
  * `tools/verify/verify-head.js` could not run: GitHub GraphQL is blocked in this session (HTTP 403).
* **Findings:** 0 Critical, 0 High, 1 Medium (M1), 4 Low (L1 to L4). Each finding is listed below.
  * **Medium M1: the documented SECURITY DEFINER limitation is not enforced, and the unhardened form is accepted.**
    * `docs/MIGRATION-GUARD.md`, "Known limitation: `SECURITY DEFINER` functions and a safe `search_path`" (new in this delta, the owner's documentation item), states: "**Consequence.** No migration may create a `SECURITY DEFINER` function today."
    * The guard only refuses `search_path`, so it refuses the HARDENED form. It accepts a definer function or procedure WITHOUT a pinned search path, which is the dangerous form, in every spelling probed.
    * The owner's text asks that the limitation be documented and that any exception be narrowly specified, reviewed and tested before use. As written, the document describes a protection that does not exist.
    * Required (preferred): refuse `SECURITY DEFINER` in every spelling as a neutralizer (`\bSECURITY\s+DEFINER\b`, which also covers `EXTERNAL SECURITY DEFINER`). No migration on main uses it in code; it appears only in comments of company 0034, which the lexer removes. Add unit tests for each spelling, and keep the existing "every embedded migration passes" tests green.
    * Alternative: correct the section to state exactly what is and is not refused. With the owner's T2 direction against unchecked definer functions, the refusal is the consistent choice.
  * **Low L1: `MigrationScript.Set` has a public `init`.**
    * Any code can label a hand-built script as System or Company. The checksum binding means only the exact approved text, name and number can then pass, so the effect is limited to applying an approved system script under a set label it was not loaded from, and that needs new code.
    * The per-read `Checksum` is sound: it is computed from the current text on every read, a `with { Sql = … }` copy gets the checksum of its own text, and spoofing it needs a SHA-256 collision.
    * Make the setter `internal init` so that only the two embedded loaders (and tests through InternalsVisibleTo) can set it.
  * **Low L2: the runbook says "in the database named in the message", but the messages deliberately name no database** (the tests assert this). State instead how the operator finds it: from the company id in the `migrate-companies` line, the company given to `provision-company`, or the system database for `init-system-db`.
  * **Low L3: the committed tests exercise the `is_superuser` reading only through the seam.**
    * My probe proves the real reading end to end against a real superuser login on an isolated cluster (above).
    * Follow-up: commit a version of the probe that uses the disposable cluster's bootstrap superuser when its secret is available to the test (isolated and CI clusters), skipping visibly otherwise.
  * **Low L4: restore coverage is the trial restore.**
    * `NonSuperuserMigrationAccountTests` proves init, provision, upgrade, both grants, backup-run, backup-company and trial-restore-company as a CREATEDB-only owner account with a separate CREATEROLE-only account.
    * `backup-recovery-test` is not exercised end to end with that account, and no production restore command exists yet.
    * Record it as a follow-up for when a production restore command is built.
* **Answers to the specific questions:**
  * **Q1 (binding).** The allowlist is bound by set, number, name, checksum and the exact switched triggers. The match is a single conjunction (`TriggerSwitchProblem`), and the tests prove each condition on its own.
    * The checksum cannot be spoofed: it is per-read, over the current text.
    * A spoofed Set admits only the exact approved text (L1).
    * A `with` copy that changes the text changes the checksum. A hand-built script is `Unspecified` and never matches.
    * Any loader is validated by `MigrationScript.Validate`, which calls `EnsureAcceptableSet`. The runner calls `Validate` before connecting, so no loader can produce an approved script except with exactly the approved text.
    * Existing behaviour is preserved: no migration file changed, and the journal checksums are unchanged.
  * **Q2.** Refusal with nothing pending:
    * init-system-db refuses through the final check of the run;
    * provision-company on a Ready company refuses through `EnsureNoSwitchedOffAsync`, and the company stays Ready;
    * migrate-companies fails only the tampered company.

    When migrations are pending, the per-script post-check refuses the first script, because any switched-off trigger fails it. The diagnostic names each item as `table.trigger (state)`, including event triggers. Nothing is re-enabled, which the tests verify after a refusal. The API never references the runner or commands: a source scan, plus a runtime test that opens the company through the API resolver after a refusal.
  * **Q3.**
    * `MigrationAccountPolicy` refuses a superuser before anything is created: init checks on the maintenance connection before CREATE DATABASE; provision checks before the lock and the database; the runner checks before the advisory lock. My probe confirms this with a real superuser.
    * **The override is acceptable as implemented:**
      * it is a CLI switch on the three migrating commands only (any other command gets a usage error);
      * it is set through a private setter that configuration and environment cannot reach, which my CLI probe confirmed;
      * it is logged to stderr on every effective use.
    * Refusing the override in a "production configuration" would need a production marker that `Hasm.Admin` does not have, and the same operator could change it. An operator who holds superuser credentials can bypass every control directly, so the switch adds no capability. The real control is that production operators hold no superuser login, as the "Migration account" section says.
    * The claimed privileges suffice end to end and are minimal: LOGIN, CREATEDB, ownership of what the account creates, and a separate CREATEROLE-only account (see L4).
  * **The rewritten test is legitimate.**
    * Under Q1 a new pair must be refused. The test asserts the refusal through the real runner, with nothing executed and the journal unchanged.
    * The application path of the post-check is still proven, because `Init` applies all 26 allowlisted paired migrations through the runner, each with the post-check, and the test asserts that afterwards no non-internal trigger is in any state but 'O'.
    * The other runtime post-check cases (left off, removed, rebound, replication role, shadowing operator, company database) are unchanged.
  * **The pinned pg_-names test** is exact: system 0035 `pg_has_role` and company 0036 `pg_current_xact_id`, confirmed independently.
  * **X4:** deferred. No preset role, no automatic grant and no migration were added.
  * **The merges** are clean apart from the registry title. The registry is consistent at 1720.
* **Blocker or High findings open:** none. M1 (Medium) is open and must be fixed before merge (CLAUDE.md "per task"; the owner's "do not merge ... unresolved security findings"). L1 to L4 are Low; fix or record them.
* **NOT VERIFIED:** the following three items.
  * `verify-head.js`: blocked by GraphQL; the checks were read by REST instead.
  * The full PostgreSQL suite was not re-run locally on 05a5ce9; hosted postgres-integration passed in full on this exact head.
  * `backup-recovery-test` as the non-superuser account (L4).
* **Verdict:** request changes
