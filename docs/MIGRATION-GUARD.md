# Migration guard hardening (design note)

**Status:** implemented as a separate security workstream on the owner decision of 2026-11-21 (Q5 of `docs/PHASE-3.8D-RESUME-REVIEW.md`, finding F2 / review M1 of #171). Operating rules and the allowlist procedure: `docs/DEVELOPMENT.md`, "Migration guard".

## Problem
The database enforces accounting and security invariants with triggers and guard functions. The migration guard refused only `DROP`, `TRUNCATE` and `DELETE FROM`, so a migration could switch a guard trigger off, or redefine a guard function with `CREATE OR REPLACE FUNCTION`, without being noticed. The old comment handling (every `--` to the end of the line removed by a regular expression) also let a statement hide: in `SELECT '--'; DROP TABLE t;` the `DROP` was removed together with the "comment".

## Constraints
- Every embedded migration is validated at every load, so a rule that refuses an applied migration stops every installation. 26 system migrations (0006-0030 and 0034) contain 32 paired `DISABLE TRIGGER` / `ENABLE TRIGGER` statements on two immutability triggers (no company migration does); since the owner decision of 2026-11-22 (Q1) these 26 are the ONLY migrations that may (section "Frozen trigger-switch allowlist"); company migrations 0009, 0027 and 0029 replace six functions of earlier migrations.
- Applied migrations and their checksums are immutable. No migration file is changed, and the checksum computation is unchanged.
- No protected verification file is changed: the guard lives in `Hasm.Infrastructure` (`MigrationGuard`, `MigrationSqlLexer`, `MigrationRunner`) and its tests. The allowlist is `MigrationSafety/ApprovedFunctionReplacements.cs`, outside the two database folders, because the schema generators (`tools/schema`) scan those folders for physical names that are already taken.

## Design
**1. Reading the SQL (`MigrationSqlLexer`).** One pass that follows PostgreSQL's lexical rules: `--` comments, nested `/* */` comments, string literals (`''` escapes; backslash escapes only in `E''`), quoted identifiers and dollar-quoted bodies with any tag. It produces three views:
- *Scan*: comments removed, literals and bodies kept (bodies are read again as SQL, since that is how the server runs them). Rules of the kind "must not appear anywhere" use it, so a guarded word inside a string or a function body is refused.
- *Code*: as Scan with the content of string literals blanked, for the rules where a data string must not count (`EXECUTE`, `LANGUAGE`, `COPY`, `MERGE`).
- *Statements*: the top-level statements, split at `;`, bodies replaced by a placeholder.

SQL the lexer cannot read unambiguously is refused: an unterminated construct; a plain literal that ends in a backslash before its quote (read differently when `standard_conforming_strings` is off; mentioning that setting is itself refused); an identifier written with Unicode escapes (`U&"..."`, which can spell any name, also a guarded one; `U&'...'` string literals stay accepted, company migration 0004 seeds a name that way); and a `$` that would open a dollar quote directly after a digit or after another dollar quote (`1$$...$$`, `$$a$$$$b$$`), where the reading depends on details of the server's scanner.

**2. Static rules (`MigrationGuard`), per migration.** Strict allow-patterns: the guard recognises a small canonical form and refuses every other mention.
- Trigger switching (per migration; the set rule 3a below decides WHICH migrations may use it at all): the number of `DISABLE|ENABLE [REPLICA|ALWAYS] TRIGGER|RULE` occurrences in Scan must equal the number of canonical top-level statements; each `DISABLE` needs a later `ENABLE` of the same normalized (table, trigger); `ALL` and `USER` are refused. A plain `ENABLE` of a named trigger without a preceding `DISABLE` is accepted, because it only restores protection.
- `ENABLE REPLICA` leaves the trigger firing only in replica sessions, which is "off" for the application; `ENABLE ALWAYS` is not the state the trigger was in. Both are refused statically. The run-time check treats `R` as switched off and accepts `A`, because an `ALWAYS` trigger does fire.
- Refused outright (none is used by any existing migration): any mention of `session_replication_role`, `standard_conforming_strings`, `backslash_quote` or `search_path`; any mention of `set_config`, quoted or not; any name starting with `pg_` except the two calls listed under "System names" below; `dblink` names; functions that run a query given as text (`query_to_xml*`, `cursor_to_xml*`, `ts_stat`, `ts_rewrite`); `ALTER FUNCTION|PROCEDURE|ROUTINE`; `ALTER [EVENT] TRIGGER`; `CREATE OR REPLACE TRIGGER`; `CREATE RULE`; `CREATE OPERATOR`; `CREATE EXTENSION`; `COPY` and `MERGE` in any form; languages other than `sql`/`plpgsql`; `BEGIN ATOMIC`; top-level transaction control.
- `EXECUTE` is accepted in exactly two places and nowhere else: `EXECUTE FUNCTION|PROCEDURE <name> (` in a top-level `CREATE [CONSTRAINT] TRIGGER` statement (286 uses today), and `EXECUTE ON` in a top-level `GRANT` / `REVOKE` / `ALTER DEFAULT PRIVILEGES` statement. The number of `EXECUTE` words in the whole text, bodies included, must equal the number of accepted ones. Dynamic SQL, `PREPARE`/`EXECUTE` and every `EXECUTE` inside a `DO` block or function body are therefore refused, including `EXECUTE function;` where `function` is a plpgsql variable.
- **System names (default-deny).** Every name that starts with `pg_` is refused wherever it stands in the read text (code, function bodies, string literals), quoted or not, qualified or not: the catalogs (`pg_trigger`, `pg_proc`, ...), the schemas `pg_catalog` and `pg_temp`, and every system function. This is what closes catalog writes through an auto-updatable view (`CREATE VIEW v AS SELECT * FROM pg_trigger; UPDATE v ...`): the view cannot be defined. **No catalog relation is ever allowed**, for reading either. The only exceptions are plain calls (unquoted, unqualified, followed by `(`) of the read-only built-in functions in `MigrationGuard.AllowedSystemFunctions`, pinned by a test:
  - `pg_current_xact_id()`: the id of the current transaction. The plan-execution migration of Phase 3.8D (PR 48) binds rows to the transaction that wrote them with it (10 uses). It reads only.
  - `pg_has_role(...)`: a role-membership test, needed by the planned TOTP trigger. It reads only.

  The bare prefix `pg_` with nothing after it is not a name and is accepted: system migration 0001 uses the pattern `'^pg_'` to keep company database names out of the reserved prefix. Adding a function to the list is a change of the guard and follows the same reviewed procedure as an allowlist entry; a catalog relation is never added.
- Names are compared part by part (schema, name), never as one dotted string, so a quoted name that contains a dot is not confused with a qualified name.

**3. Static rule across a set.** `MigrationScript.Validate` (every load, every runner and provisioner call) walks the ordered set and refuses `CREATE OR REPLACE FUNCTION|PROCEDURE f` when an earlier migration created `f`, unless (file name, checksum, function) is in `MigrationGuard.ApprovedFunctionReplacements`. Binding the entry to the checksum means an entry authorises exactly the reviewed content. `Validate` also re-checks each script on its own, so a `MigrationScript` built without `Parse` is held to the same rules.

**3a. Trigger switching is frozen to an allowlist (owner decision Q1, 2026-11-22).** The same walk refuses every migration that contains a matched `DISABLE`/`ENABLE` pair unless it is on `MigrationGuard.ApprovedTriggerSwitches` (`Hasm.Infrastructure/MigrationSafety/ApprovedTriggerSwitches.cs`). An entry is bound to the migration's set (system or company), number, file name, checksum and the exact triggers it switches ("table/trigger"), so an edited migration, the same text under another number, name or set, a migration of an unspecified set (hand-built) or a migration that switches one more trigger never matches. A refusal names the migration, its set, number and checksum and the triggers. `MigrationScript.Set` is set only by the two embedded loaders. The single-script rule (`MigrationGuard.EnsureAcceptable`, used by `MigrationScript.Parse`) still accepts the canonical pair: it is the set rule that freezes the pattern. Existing applied migrations pass unchanged.

**4. Run-time check (`MigrationRunner`).** A run does not start when `session_replication_role` is not `origin`. For each script the runner records, inside the script's transaction, the non-internal triggers (table, name, function, state) and the replication role; after the script and its journal row, before the commit, it fails with a `MigrationException` when
- a trigger is switched off (`tgenabled` `D` or `R`; `O` and `A` fire and are accepted) or an event trigger is switched off (`pg_event_trigger.evtenabled`);
- a trigger that existed before the script no longer exists, or is bound to another function;
- `session_replication_role` is not `origin` or differs from its value before the script.

Leaving the transaction by exception rolls the script back. The switched-off check runs once more at the end of every run, including a run that applies nothing. The catalog statements use no operator and no unqualified name (`SELECT ... FROM pg_catalog.pg_trigger t WHERE NOT t.tgisinternal`, casts to `pg_catalog` types only) and all filtering and comparing happen in the application, so an operator named `=` planted in another schema, or a changed `search_path`, cannot change what the check sees. This is the backstop for what a text check cannot prove.

## What is covered
| Attempt | Refused by |
|---|---|
| `DISABLE TRIGGER` never re-enabled; re-enabled on another trigger, table or schema; `ALL` / `USER` | static rule; run-time check |
| `ENABLE REPLICA TRIGGER`, `DISABLE RULE`, `CREATE RULE` | static rule (`R` also by the run-time check) |
| Switch inside `DO`, a function, or a string given to `EXECUTE` | static rule (and dynamic SQL is refused as such) |
| Replacing a guard function of an earlier migration | set rule and allowlist |
| `ALTER FUNCTION ... RENAME / SET SCHEMA / SET search_path`, `ALTER TRIGGER ... RENAME`, `CREATE OR REPLACE TRIGGER` | static rule |
| Dynamic SQL in any spelling, including `EXECUTE function;` with a variable named `function` or `procedure` | static rule (`EXECUTE` allow-pattern) |
| `SET search_path`, `ALTER ROLE/DATABASE ... SET search_path`, `CREATE OPERATOR`, `set_config` in any spelling (also `"set_config"(...)`) | static rule; the run-time check does not depend on them |
| Catalog writes, direct (`UPDATE pg_trigger ...`) or through an auto-updatable view on a catalog (`CREATE VIEW v AS SELECT * FROM pg_proc; UPDATE v ...`); `COPY`, `MERGE` | static rule (every `pg_` name is refused); run-time check for the switched-off, removed or re-bound trigger |
| Statements sent another way: `dblink*`, `query_to_xml('...')`, `ts_stat('...')` | static rule |
| A guarded name spelled with `U&"..."`; an ambiguous `$` quote | lexer |
| Trigger removed, or removed and re-created on another function, by a means the text rules do not see | run-time check |
| `SET [LOCAL] session_replication_role`, `ALTER DATABASE/ROLE ... SET` of it | static rule; run-time check of the value before and after each script |
| A statement hidden behind `--` in a string or quoted name, split by a block comment, or placed in a dollar-quoted body | lexer |
| `COMMIT` inside a migration (to escape the rollback) | static rule |
| Trigger switched off out of band (manual SQL, a restore with triggers disabled) | run-time check at the end of every run |

## What is NOT covered (known limits)
- **A function with the same name but a different argument list** created with plain `CREATE FUNCTION` is an overload, not a replacement, and is not refused. Replacement is compared by name only, which is stricter for `OR REPLACE` and silent for overloads.
- **What a migration does between a paired `DISABLE` and `ENABLE`** is not judged by the guard. The pair is the owner-approved pattern for seeding rows into tables protected by an immutability trigger; whether the statements in between are legitimate remains a matter of review.
- **Shadowing of functions**: a function of the same name created in another schema is not refused as such. A migration can no longer change `search_path` (every mention is refused), and triggers are bound to their function by identity; a `search_path` changed outside the migrations (role or database setting) could still make a guard function that calls another function by unqualified name resolve it elsewhere.
- **Weakening without replacing**: a new trigger or function that counteracts an existing one, a changed `CHECK` constraint (`ALTER TABLE ... ADD/VALIDATE CONSTRAINT`; dropping one is already refused), changed privileges (`GRANT`/`REVOKE`), `ALTER TABLE ... RENAME`, `ALTER TABLE ... OWNER TO`, `CREATE OR REPLACE VIEW`, row-level-security statements (not in use). These remain a matter of code review and of the catalog tests of each phase.
- **`CREATE OR REPLACE` inside the creating migration** is accepted; the migration is reviewed as one unit.
- **Direct catalog writes.** A superuser can write to most catalogs with plain DML, also through an auto-updatable view on a catalog (`allow_system_table_mods` is needed for DDL on catalogs, not for these writes), so this matters whenever the migration account is a superuser (owner decision Q3: the account is a non-superuser, and the commands refuse a superuser). Every `pg_` name is refused statically, which rules out both the direct write and the view. A write that reaches the catalog by a route the text rules do not see is caught at run time only if it switches a trigger off, removes it or re-binds it; a changed function body (`pg_proc.prosrc`) is not.
- **Changes outside migrations** (manual SQL by a database owner) are outside the guard; only a switched-off trigger or event trigger is detected, at the next run of the migration runner. A replaced function body is not detected at run time. Follow-up: a catalog snapshot test (function bodies and trigger definitions pinned per migration set) would close this for the embedded migrations.
- **`session_replication_role` set and reset inside one migration** by a means the text rules do not see would not be caught by the before/after comparison. Every textual mention is refused, `set_config` is refused in every spelling, dynamic SQL and the functions that run a query given as text are refused, and setting it requires superuser rights. The comparison itself is tested through a seam of the runner that supplies an additional reading (it can only add a refusal), because the test cluster's administrative role is deliberately not a superuser.
- **A trigger removed and re-created under the same name on the same function** with other events or conditions is not detected at run time (`DROP` and `CREATE OR REPLACE TRIGGER` are refused statically). Same follow-up as above.
- **A switched-off event trigger** is refused by the run-time check, but the refusal has no PostgreSQL test (creating an event trigger needs a superuser); the decision is unit-tested and the catalog query runs in every test. The product creates no event trigger.
- **String-building that never reaches `EXECUTE`** is harmless and not judged; a guarded phrase assembled from pieces can only run through `EXECUTE`, which is refused.
- **The guard is not a SQL parser.** It is strict on purpose: unusual but harmless spellings (for example `ALTER TABLE ONLY t DISABLE TRIGGER x`, a quoted language name, a guarded word inside a data string) are refused and must be rewritten in the canonical form.

## Behaviour changes to be aware of
- A migration that uses dynamic SQL, `ALTER FUNCTION`, `CREATE RULE`, `COPY`, `MERGE`, `CREATE EXTENSION`, `CREATE OPERATOR`, `search_path` (also as a `SET search_path` clause of a function definition), `set_config`, any `pg_` name other than the two allowed calls (so no migration can read a catalog, call `pg_advisory_xact_lock` or qualify a name with `pg_catalog.`), `dblink`, a `U&"..."` identifier or a non-canonical trigger switch is now refused at load. No embedded migration does. System migration 0034 (MFA policy, paired `DISABLE`/`ENABLE`) is one of the 26 on the trigger-switch allowlist.
- A NEW migration with a `DISABLE`/`ENABLE` pair is now refused at load (owner decision Q1). A migration that needs one needs an explicit owner approval and an allowlist entry (procedure in `docs/DEVELOPMENT.md`).
- `init-system-db`, `provision-company` and `migrate-companies` refuse a PostgreSQL superuser account unless the logged development/test override is given (owner decision Q3).
- The runner now fails, also on a no-op run, when the database contains a switched-off trigger. Tests or tools that switch a trigger off must enable it again before the runner is used (all existing ones do).
- The words `DROP`, `TRUNCATE`, `DELETE FROM` are now also found after `--` inside a string literal or quoted identifier, where the old regular expression lost them.

## Migration account (owner decision Q3, 2026-11-22)
Production migrations run as a **dedicated non-superuser administrative account** (`Hasm__Database__AdminUsername`), separate from every runtime credential. The privileges below were derived from the code and are proven, end to end, by `NonSuperuserMigrationAccountTests` (PostgreSQL suite, isolated cluster, real `Hasm.Admin` commands):

| Needed | Why |
|--------|-----|
| `LOGIN` | the account connects |
| `CREATEDB` | `init-system-db` creates the system database, `provision-company` creates each company database, `trial-restore-company` and `backup-recovery-test` create the disposable restore databases |
| ownership of the system and of every company database (it owns what it creates; nothing is transferred) | migrations, `ALTER TABLE ... ENABLE/DISABLE TRIGGER`, `grant-system-runtime` and `grant-company-runtime` (GRANT/REVOKE on its own objects), backup (`pg_dump`) and restore (`pg_restore`) |
| nothing else | no `SUPERUSER`, no `CREATEROLE`, no `REPLICATION`, no `BYPASSRLS`, no membership of `pg_read_all_data`, `pg_write_all_data` or any other predefined role |

Creating the runtime login roles needs `CREATEROLE`. That is deliberately **not** on the administrative account: it is a second dedicated account (`Hasm__Database__RoleAdminUsername` / `RoleAdminPassword`) that holds only `CREATEROLE` (with PostgreSQL 16 role-administration semantics it can hand out only attributes it holds itself, which is why the runtime roles carry none) and is used only to create or alter the runtime roles. The administrative account owns the databases and issues the grants. The API holds neither account. The disposable test and CI clusters are built exactly this way (`hasm_test_admin`: `CREATEDB` only; `hasm_test_roleadmin`: `CREATEROLE` only; there is no superuser login at all), so every PostgreSQL test of the repository already runs the workflows without superuser rights.

**Enforcement.** `init-system-db`, `provision-company` and `migrate-companies` read `is_superuser` of the account's session before anything is created or applied, and refuse a superuser with a message that names the decision and the override; nothing is changed (`MigrationAccountPolicy`, also checked inside `MigrationRunner`). For development and tests only, the command-line switch `--allow-superuser-for-development` lets the command run; it is accepted by these three commands only (any other command fails the usage check), it is **never** read from configuration or environment variables (so a production configuration file cannot carry it silently), and every use prints a `WARNING` line to the standard error stream. The override is not needed on any documented cluster; it exists for a developer who has only a superuser login on a local database.

## Runbook: a run refused because a trigger is switched off
The message names each trigger as `<table>.<trigger> (<state>)`, or `event trigger <name> (<state>)`. The check covers **every** non-internal trigger of the database, not only the product's guards: a trigger that an extension installed or that a database administrator disabled deliberately is refused too.
1. Find out why it is off (an interrupted manual operation, a restore made with triggers disabled, a deliberate change) before changing anything; a switched-off immutability trigger means the protected rows may have been changed in the meantime.
2. As the owner of the table, in the database named in the message: `ALTER TABLE <table> ENABLE TRIGGER <trigger>;` (for an event trigger, as a superuser: `ALTER EVENT TRIGGER <name> ENABLE;`).
3. Run the administrative command again (`init-system-db`, `provision-company` or `migrate-companies`). Nothing else has to be repaired: a refused run applies nothing and records nothing in the journal.

There is no switch that makes the runner accept a switched-off trigger.

## Owner decisions (2026-11-22)
The owner decided the three questions raised by the review of this change. The texts below are quoted verbatim; the implementation of each follows it. The separate decision X4-PRESET-ROLE is DEFERRED by the owner: this change introduces no preset role and no automatic role grant.

> **Q1 — DISABLE/ENABLE TRIGGER in future migrations**
> Do not allow arbitrary future migrations to disable and re-enable triggers.
> Preserve all existing, approved migration behavior, including already applied migrations.
> Any new use must be explicitly reviewed and allowlisted, bound to the exact migration identity and checksum.
> Do not weaken accounting, audit, security or immutability protections.

Implemented: rule 3a above and the allowlist below. Every existing migration, applied or not, passes unchanged (unit test over both embedded sets).

> **Q2 — Disabled triggers when no migration is pending**
> Keep the fail-closed behavior.
> Administrative migration commands must refuse a database containing disabled non-internal triggers, even when no migration is pending.
> Provide clear diagnostics identifying the affected triggers.
> Do not automatically re-enable triggers without investigation, and do not assume previously protected data is valid merely because a trigger has been restored.
> The running application must remain unaffected by a rejected administrative command.

Implemented: `init-system-db`, `provision-company` (also for a company that is already Ready, which runs no migration) and `migrate-companies` refuse such a database. The message names every affected trigger as `<table>.<trigger> (<state>)` (state `D` disabled, `R` replica only), `migrate-companies` prints it under the company's `Failed [migration]` line, and nothing is ever re-enabled by a command (a company is not marked Failed by the refusal; no migration is recorded; no lock is left). The API never runs the migration runner (source-scan test) and keeps working against the database (PostgreSQL test).

> **Q3 — PostgreSQL migration administrator privileges**
> Production migrations must use a dedicated non-superuser administrative account with only the privileges necessary for provisioning and schema management.
> Keep it separate from application runtime credentials.
> Validate that all supported deployment, provisioning, upgrade and restore workflows remain functional with this account.
> Do not introduce broad permissions or superuser privileges as a workaround.

Implemented: section "Migration account" above (privileges, separation, enforcement) and `NonSuperuserMigrationAccountTests`.

## Frozen trigger-switch allowlist (owner decision Q1)
The 26 migrations on `main` that contain a matched `DISABLE`/`ENABLE` TRIGGER pair; each pauses an immutability trigger of the system database for one versioned insert of permission rows into a preset role, inside the migration's own transaction. No company migration uses the pattern. The list is pinned by `MigrationTriggerSwitchAllowlistTests`, independently of the production list.

| Set | Number | Name | Checksum (SHA-256, line endings normalized) | Triggers switched (table.trigger) |
|-----|--------|------|------------------------------------------|-----------------------------------|
| System | 0006 | `0006_company_configuration_support.sql` | `debedc9bd0f637a2d59a6f140c7fa4da9494c582e44f1c80dc8641c90de37c08` | `t_3e3d4119.r_2601e368` |
| System | 0007 | `0007_branch_management_access.sql` | `e3e01f167854e09d226ad56eaca51c0d6a7bbea3d55c72cdaaff94a8c29250cb` | `t_3e3d4119.r_2601e368` |
| System | 0008 | `0008_warehouse_management_access.sql` | `6139b4186ab3033fe7695ddf7398c3462d1937091b32c4c17b180793c7448e00` | `t_3e3d4119.r_2601e368` |
| System | 0009 | `0009_fiscal_calendar_access.sql` | `bb2c3183ed6d5441206eb73ff181d887b12f703c2e5f29d696caa3122a59aa5b` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |
| System | 0010 | `0010_chart_of_accounts_access.sql` | `bc25cf34681f21d152e3f25cc5cce8e766c1fdc5682defdaa280a1228acbf05f` | `t_3e3d4119.r_2601e368` |
| System | 0011 | `0011_general_ledger_access.sql` | `f4c5e7f2547db13f599d1553f18b386f321c63a2dc0113d60ec5996e72f52e70` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |
| System | 0012 | `0012_cost_center_scope.sql` | `93116982f3ce604b488cb3ab659b3c75691b0bb1776366f5352381bc99fc5ed4` | `t_3e3d4119.r_2601e368` |
| System | 0013 | `0013_import_framework_access.sql` | `a0a8d612a1c78a0fb8f93992843e61da93404012322daf79ce824934cc42a2fb` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |
| System | 0014 | `0014_financial_reporting_access.sql` | `6f1cfed10fcce6cf8f2ede0aab5b4330d5717d47d8a0fb0b285117f5d8193ed0` | `t_3e3d4119.r_2601e368` |
| System | 0015 | `0015_inventory_master_data_access.sql` | `da583e7427b97d8083b90fedb3ddb7e23b1187ed420fc8b6eb28c5779d619540` | `t_3e3d4119.r_2601e368` |
| System | 0016 | `0016_stock_documents_access.sql` | `5f7a487b84e5a1fd59aa92da7ab006f5b7397043f8ef2d913081b25dc3299da6` | `t_3e3d4119.r_2601e368` |
| System | 0017 | `0017_inventory_accounting_access.sql` | `1cdb4dccfb963ee0e4a26c7a991c287924619ef8cfdd34dcec7c15efe4b931f2` | `t_3e3d4119.r_2601e368` |
| System | 0018 | `0018_stock_posting_access.sql` | `0ff833c34cfe83126db94a99a113e29ec0f1ba3e051fb5009e4ac854f69a7020` | `t_3e3d4119.r_2601e368` |
| System | 0019 | `0019_stock_reversal_access.sql` | `43bbc569c4d84fdaa4be30a247dcf826ba4b896a1472f8903732cfff382ea1c5` | `t_3e3d4119.r_2601e368` |
| System | 0020 | `0020_stock_balance_access.sql` | `8fdd0f4131b25337682448e82ae58d318f6661a28f807ec447c78356b456cc1e` | `t_3e3d4119.r_2601e368` |
| System | 0021 | `0021_stocktake_access.sql` | `d2aac90e19ea71e9628a274472d675fffc23e7546b25882cb3cd3c25138e727c` | `t_3e3d4119.r_2601e368` |
| System | 0022 | `0022_negative_stock_access.sql` | `3546694dae2892dc241e757504a40ee5420dc5c871f3fd6cf666f4bf3e68738e` | `t_3e3d4119.r_2601e368` |
| System | 0023 | `0023_party_registry.sql` | `9d33adff94cb79c0c0133a7929f62006b14bddecdea6f066a35fd533d502edcc` | `t_3e3d4119.r_2601e368` |
| System | 0024 | `0024_tax_access.sql` | `f53cbaed6b4e8190fe2f12ca62ca859af87089acc601fb8a3ac4bf44782ad5eb` | `t_3e3d4119.r_2601e368` |
| System | 0025 | `0025_party_roles_access.sql` | `6ecefbf81ea025934138e8eb359144054c99c1c486000c9ab649b60f220e5680` | `t_3e3d4119.r_2601e368` |
| System | 0026 | `0026_attachments_access.sql` | `05bd3bb4dac449c853d3a6940f9c7b500841212695f0bf8030b39cab740188ca` | `t_3e3d4119.r_2601e368` |
| System | 0027 | `0027_pricing_access.sql` | `de980d8d01e5f177edcaa980c77d896b1c7bb31abe561d351d4da36b0a919e23` | `t_3e3d4119.r_2601e368` |
| System | 0028 | `0028_commercial_documents_access.sql` | `e1cb7bcfa277187ac46d5c4c3027e696c41baefdb08a082e9fcd13dfeb1abd4b` | `t_3e3d4119.r_2601e368` |
| System | 0029 | `0029_commercial_approver.sql` | `902fa5bd6ec1fa94a04a82bbf95569fd4a5386ec1bebef706add375bc31efa80` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |
| System | 0030 | `0030_attachment_retention_access.sql` | `004c557674706584afb9e753dc50d3929e2cc467cc2b5a9f23564e00b21f8f9b` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |
| System | 0034 | `0034_company_mfa_policy.sql` | `f9aedd4eee2de5ee1c4db6fa84ca48d92b75fda4b906c039d37c521c70794ead` | `t_359332ac.r_d3408f58, t_3e3d4119.r_2601e368` |

Adding an entry is a safeguard change: the owner approves the specific migration explicitly (recorded in the owner-decision register), an independent security review reads the migration, and the pinned list in `MigrationTriggerSwitchAllowlistTests` is extended in the same reviewed change.

## Known limitation: `SECURITY DEFINER` functions and a safe `search_path`
**What the guard refuses today.** Every mention of `search_path` (a `SET search_path` clause, `SET`/`ALTER ... SET search_path`, `set_config`) and every `pg_` name other than the two plain calls on the allowlist (so also `pg_catalog.` and `pg_temp`) is refused, in code, in function bodies and in strings. A migration therefore cannot create a function that carries the usual hardening clause of a `SECURITY DEFINER` function (`SET search_path = pg_catalog, pg_temp`).
**Why.** The guard is a static text check. `search_path` decides which function or operator an unqualified name means, so a function whose search path can be influenced can be made to call another object, and a statement that changes it can neutralize a guard function. Accepting the clause in general would need a parser that proves every name in the body is qualified or resolved safely; the guard is deliberately not that. Refusing the word is the strict, simple rule, and no existing migration uses it.
**Consequence.** No migration may create a `SECURITY DEFINER` function today. Every function the product creates runs with the privileges of the caller.
**Any future exception must be narrowly specified, independently reviewed and tested before use.** Specified: exactly one literal clause, `SET search_path = pg_catalog, pg_temp`, on a top-level `CREATE FUNCTION` statement, as the only change of the rule, with the function bound to its migration by an allowlist entry (set, number, name, checksum) like the other exceptions. Reviewed: an independent security review of the function body (qualified names, no dynamic SQL, the privileges it exposes) and of the guard change. Tested: unit tests for the accepted and every refused spelling, and a PostgreSQL test that the function cannot be made to resolve another object. None of that exists yet, and nothing may rely on such a function before it does.

## Follow-up (recorded, not built)
- **`SET search_path` on function definitions.** See the section "Known limitation: `SECURITY DEFINER` functions and a safe `search_path`": refused today; any exception needs its own owner approval, an independent review and tests first.
- **Catalog snapshot test** for function bodies and trigger definitions (see "What is NOT covered").
