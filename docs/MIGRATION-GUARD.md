# Migration guard hardening (design note)

**Status:** implemented as a separate security workstream on the owner decision of 2026-11-21 (Q5 of `docs/PHASE-3.8D-RESUME-REVIEW.md`, finding F2 / review M1 of #171). Operating rules and the allowlist procedure: `docs/DEVELOPMENT.md`, "Migration guard".

## Problem
The database enforces accounting and security invariants with triggers and guard functions. The migration guard refused only `DROP`, `TRUNCATE` and `DELETE FROM`, so a migration could switch a guard trigger off, or redefine a guard function with `CREATE OR REPLACE FUNCTION`, without being noticed. The old comment handling (every `--` to the end of the line removed by a regular expression) also let a statement hide: in `SELECT '--'; DROP TABLE t;` the `DROP` was removed together with the "comment".

## Constraints
- Every embedded migration is validated at every load, so a rule that refuses an applied migration stops every installation. 25 system migrations (0006-0030) contain 30 paired `DISABLE TRIGGER` / `ENABLE TRIGGER` statements on two immutability triggers; company migrations 0009, 0027 and 0029 replace six functions of earlier migrations.
- Applied migrations and their checksums are immutable. No migration file is changed, and the checksum computation is unchanged.
- No protected verification file is changed: the guard lives in `Hasm.Infrastructure` (`MigrationGuard`, `MigrationSqlLexer`, `MigrationRunner`) and its tests. The allowlist is `MigrationSafety/ApprovedFunctionReplacements.cs`, outside the two database folders, because the schema generators (`tools/schema`) scan those folders for physical names that are already taken.

## Design
**1. Reading the SQL (`MigrationSqlLexer`).** One pass that follows PostgreSQL's lexical rules: `--` comments, nested `/* */` comments, string literals (`''` escapes; backslash escapes only in `E''`), quoted identifiers and dollar-quoted bodies with any tag. It produces three views:
- *Scan*: comments removed, literals and bodies kept (bodies are read again as SQL, since that is how the server runs them). Rules of the kind "must not appear anywhere" use it, so a guarded word inside a string or a function body is refused.
- *Code*: as Scan with the content of string literals blanked, for the rules where a data string must not count (`EXECUTE`, `LANGUAGE`, `COPY`, `MERGE`).
- *Statements*: the top-level statements, split at `;`, bodies replaced by a placeholder.

SQL the lexer cannot read unambiguously is refused: an unterminated construct; a plain literal that ends in a backslash before its quote (read differently when `standard_conforming_strings` is off; mentioning that setting is itself refused); an identifier written with Unicode escapes (`U&"..."`, which can spell any name, also a guarded one; `U&'...'` string literals stay accepted, company migration 0004 seeds a name that way); and a `$` that would open a dollar quote directly after a digit or after another dollar quote (`1$$...$$`, `$$a$$$$b$$`), where the reading depends on details of the server's scanner.

**2. Static rules (`MigrationGuard`), per migration.** Strict allow-patterns: the guard recognises a small canonical form and refuses every other mention.
- Trigger switching: the number of `DISABLE|ENABLE [REPLICA|ALWAYS] TRIGGER|RULE` occurrences in Scan must equal the number of canonical top-level statements; each `DISABLE` needs a later `ENABLE` of the same normalized (table, trigger); `ALL` and `USER` are refused. A plain `ENABLE` of a named trigger without a preceding `DISABLE` is accepted, because it only restores protection.
- `ENABLE REPLICA` leaves the trigger firing only in replica sessions, which is "off" for the application; `ENABLE ALWAYS` is not the state the trigger was in. Both are refused statically. The run-time check treats `R` as switched off and accepts `A`, because an `ALWAYS` trigger does fire.
- Refused outright (none is used by any existing migration): any mention of `session_replication_role`, `standard_conforming_strings`, `backslash_quote` or `search_path`; any mention of `set_config`, quoted or not; any name starting with `pg_` except the two calls listed under "System names" below; `dblink` names; functions that run a query given as text (`query_to_xml*`, `cursor_to_xml*`, `ts_stat`, `ts_rewrite`); `ALTER FUNCTION|PROCEDURE|ROUTINE`; `ALTER [EVENT] TRIGGER`; `CREATE OR REPLACE TRIGGER`; `CREATE RULE`; `CREATE OPERATOR`; `CREATE EXTENSION`; `COPY` and `MERGE` in any form; languages other than `sql`/`plpgsql`; `BEGIN ATOMIC`; top-level transaction control.
- `EXECUTE` is accepted in exactly two places and nowhere else: `EXECUTE FUNCTION|PROCEDURE <name> (` in a top-level `CREATE [CONSTRAINT] TRIGGER` statement (286 uses today), and `EXECUTE ON` in a top-level `GRANT` / `REVOKE` / `ALTER DEFAULT PRIVILEGES` statement. The number of `EXECUTE` words in the whole text, bodies included, must equal the number of accepted ones. Dynamic SQL, `PREPARE`/`EXECUTE` and every `EXECUTE` inside a `DO` block or function body are therefore refused, including `EXECUTE function;` where `function` is a plpgsql variable.
- **System names (default-deny).** Every name that starts with `pg_` is refused wherever it stands in the read text (code, function bodies, string literals), quoted or not, qualified or not: the catalogs (`pg_trigger`, `pg_proc`, ...), the schemas `pg_catalog` and `pg_temp`, and every system function. This is what closes catalog writes through an auto-updatable view (`CREATE VIEW v AS SELECT * FROM pg_trigger; UPDATE v ...`): the view cannot be defined. **No catalog relation is ever allowed**, for reading either. The only exceptions are plain calls (unquoted, unqualified, followed by `(`) of the read-only built-in functions in `MigrationGuard.AllowedSystemFunctions`, pinned by a test:
  - `pg_current_xact_id()`: the id of the current transaction. The plan-execution migration of Phase 3.8D (PR 48) binds rows to the transaction that wrote them with it (10 uses). It reads only.
  - `pg_has_role(...)`: a role-membership test, needed by the planned TOTP trigger. It reads only.

  The bare prefix `pg_` with nothing after it is not a name and is accepted: system migration 0001 uses the pattern `'^pg_'` to keep company database names out of the reserved prefix. Adding a function to the list is a change of the guard and follows the same reviewed procedure as an allowlist entry; a catalog relation is never added.
- Names are compared part by part (schema, name), never as one dotted string, so a quoted name that contains a dot is not confused with a qualified name.

**3. Static rule across a set.** `MigrationScript.Validate` (every load, every runner and provisioner call) walks the ordered set and refuses `CREATE OR REPLACE FUNCTION|PROCEDURE f` when an earlier migration created `f`, unless (file name, checksum, function) is in `MigrationGuard.ApprovedFunctionReplacements`. Binding the entry to the checksum means an entry authorises exactly the reviewed content. `Validate` also re-checks each script on its own, so a `MigrationScript` built without `Parse` is held to the same rules.

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
- **Direct catalog writes.** A superuser can write to most catalogs with plain DML, also through an auto-updatable view on a catalog (`allow_system_table_mods` is needed for DDL on catalogs, not for these writes), so this matters whenever the migration account is a superuser (owner question 3). Every `pg_` name is refused statically, which rules out both the direct write and the view. A write that reaches the catalog by a route the text rules do not see is caught at run time only if it switches a trigger off, removes it or re-binds it; a changed function body (`pg_proc.prosrc`) is not.
- **Changes outside migrations** (manual SQL by a database owner) are outside the guard; only a switched-off trigger or event trigger is detected, at the next run of the migration runner. A replaced function body is not detected at run time. Follow-up: a catalog snapshot test (function bodies and trigger definitions pinned per migration set) would close this for the embedded migrations.
- **`session_replication_role` set and reset inside one migration** by a means the text rules do not see would not be caught by the before/after comparison. Every textual mention is refused, `set_config` is refused in every spelling, dynamic SQL and the functions that run a query given as text are refused, and setting it requires superuser rights. The comparison itself is tested through a seam of the runner that supplies an additional reading (it can only add a refusal), because the test cluster's administrative role is deliberately not a superuser.
- **A trigger removed and re-created under the same name on the same function** with other events or conditions is not detected at run time (`DROP` and `CREATE OR REPLACE TRIGGER` are refused statically). Same follow-up as above.
- **A switched-off event trigger** is refused by the run-time check, but the refusal has no PostgreSQL test (creating an event trigger needs a superuser); the decision is unit-tested and the catalog query runs in every test. The product creates no event trigger.
- **String-building that never reaches `EXECUTE`** is harmless and not judged; a guarded phrase assembled from pieces can only run through `EXECUTE`, which is refused.
- **The guard is not a SQL parser.** It is strict on purpose: unusual but harmless spellings (for example `ALTER TABLE ONLY t DISABLE TRIGGER x`, a quoted language name, a guarded word inside a data string) are refused and must be rewritten in the canonical form.

## Behaviour changes to be aware of
- A migration that uses dynamic SQL, `ALTER FUNCTION`, `CREATE RULE`, `COPY`, `MERGE`, `CREATE EXTENSION`, `CREATE OPERATOR`, `search_path` (also as a `SET search_path` clause of a function definition), `set_config`, any `pg_` name other than the two allowed calls (so no migration can read a catalog, call `pg_advisory_xact_lock` or qualify a name with `pg_catalog.`), `dblink`, a `U&"..."` identifier or a non-canonical trigger switch is now refused at load. No embedded migration does; system migration 0034 of the open MFA-policy work (paired `DISABLE`/`ENABLE`) was checked against the guard and is accepted.
- The runner now fails, also on a no-op run, when the database contains a switched-off trigger. Tests or tools that switch a trigger off must enable it again before the runner is used (all existing ones do).
- The words `DROP`, `TRUNCATE`, `DELETE FROM` are now also found after `--` inside a string literal or quoted identifier, where the old regular expression lost them.

## Privileges of the migration account
The repository's deployment documentation says that migrations and provisioning run with the administrative account (`Hasm__Database__AdminUsername`), which owns the objects it creates (the audit table explicitly, `docs/DEVELOPMENT.md` "Database roles"), creates the system and company databases, and is never the API runtime account. Whether that account may, must or must not be a PostgreSQL superuser in production is **not specified**. The test and CI clusters use a non-superuser role with `CREATEDB` only. The guard is written for the worst case (a superuser): the rules on `session_replication_role`, catalog writes, `COPY`, extensions and languages matter mostly there. Recommendation, not a decision: specify a non-superuser owner account for migrations.

## Runbook: a run refused because a trigger is switched off
The message names each trigger as `<table>.<trigger> (<state>)`, or `event trigger <name> (<state>)`. The check covers **every** non-internal trigger of the database, not only the product's guards: a trigger that an extension installed or that a database administrator disabled deliberately is refused too.
1. Find out why it is off (an interrupted manual operation, a restore made with triggers disabled, a deliberate change) before changing anything; a switched-off immutability trigger means the protected rows may have been changed in the meantime.
2. As the owner of the table, in the database named in the message: `ALTER TABLE <table> ENABLE TRIGGER <trigger>;` (for an event trigger, as a superuser: `ALTER EVENT TRIGGER <name> ENABLE;`).
3. Run the administrative command again (`init-system-db`, `provision-company` or `migrate-companies`). Nothing else has to be repaired: a refused run applies nothing and records nothing in the journal.

There is no switch that makes the runner accept a switched-off trigger.

## Owner questions (open; the current behaviour stays as implemented until answered)
1. **May new migrations use a `DISABLE`/`ENABLE` pair, or only the 30 existing ones?** Today the pattern is accepted generically for any trigger in any migration, as long as the pair matches (the open MFA-policy migration 0034 relies on it). The alternative is to freeze the pattern to the applied migrations (by checksum) and require an allowlist entry for each new use.
2. **The fail-closed no-op run.** The migration runner is reached only through the administrative tool `Hasm.Admin`, never by the API at start-up: `init-system-db` (system database), `provision-company` and `migrate-companies` (company databases). Today each of these commands refuses a database with a switched-off trigger even when no migration is pending, until the trigger is enabled; the running API is not affected. The alternative is to check only after migrations that were actually applied, and to report a switched-off trigger without refusing.
3. **Privileges of the migration account.** Whether the administrative account that runs migrations may be a PostgreSQL superuser in production is not specified (section "Privileges of the migration account"). The answer decides how serious the catalog-write and `session_replication_role` routes are: only a superuser can use them. If the account is specified as a non-superuser owner, those rules become defence in depth; if a superuser is allowed, they are the only static barrier.

## Follow-up (recorded, not built)
- **`SET search_path` on function definitions.** Every mention of `search_path` is refused today, which also rules out the usual hardening clause of a `SECURITY DEFINER` function. Before the first such function is needed, the guard should accept exactly one literal clause, `SET search_path = pg_catalog, pg_temp`, on a `CREATE FUNCTION` statement, and nothing else.
- **Catalog snapshot test** for function bodies and trigger definitions (see "What is NOT covered").
