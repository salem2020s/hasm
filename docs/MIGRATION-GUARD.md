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
- *Code*: as Scan with the content of string literals blanked, for the two rules where a data string must not count (`EXECUTE`, `LANGUAGE`).
- *Statements*: the top-level statements, split at `;`, bodies replaced by a placeholder.

SQL the lexer cannot read unambiguously is refused: an unterminated construct, or a plain literal that ends in a backslash before its quote (read differently when `standard_conforming_strings` is off; mentioning that setting is itself refused).

**2. Static rules (`MigrationGuard`), per migration.** Strict allow-patterns: the guard recognises a small canonical form and refuses every other mention.
- Trigger switching: the number of `DISABLE|ENABLE [REPLICA|ALWAYS] TRIGGER|RULE` occurrences in Scan must equal the number of canonical top-level statements; each `DISABLE` needs a later `ENABLE` of the same normalized (table, trigger); `ALL` and `USER` are refused. A plain `ENABLE` of a named trigger without a preceding `DISABLE` is accepted, because it only restores protection.
- `ENABLE REPLICA` leaves the trigger firing only in replica sessions, which is "off" for the application; `ENABLE ALWAYS` is not the state the trigger was in. Both are refused statically. The run-time check treats `R` as switched off and accepts `A`, because an `ALWAYS` trigger does fire.
- Refused outright (none is used by any existing migration): `session_replication_role`, `standard_conforming_strings`, `backslash_quote`, `ALTER FUNCTION|PROCEDURE|ROUTINE`, `ALTER [EVENT] TRIGGER`, `CREATE OR REPLACE TRIGGER`, `CREATE RULE`, dynamic `EXECUTE`, languages other than `sql`/`plpgsql`, `BEGIN ATOMIC`, top-level transaction control.

**3. Static rule across a set.** `MigrationScript.Validate` (every load, every runner and provisioner call) walks the ordered set and refuses `CREATE OR REPLACE FUNCTION|PROCEDURE f` when an earlier migration created `f`, unless (file name, checksum, function) is in `MigrationGuard.ApprovedFunctionReplacements`. Binding the entry to the checksum means an entry authorises exactly the reviewed content. `Validate` also re-checks each script on its own, so a `MigrationScript` built without `Parse` is held to the same rules.

**4. Run-time check (`MigrationRunner`).** After each script and its journal row, before the commit, the runner lists non-internal triggers with `tgenabled IN ('D','R')` in the whole database and fails with a `MigrationException` if there is any; leaving the transaction by exception rolls the script back. It also refuses a script that ends with `session_replication_role` other than `origin`. The trigger check runs once more at the end of every run, including a run that applies nothing, so a database whose safeguard was switched off out of band is not accepted as migrated. This is the backstop for what a text check cannot prove.

## What is covered
| Attempt | Refused by |
|---|---|
| `DISABLE TRIGGER` never re-enabled; re-enabled on another trigger, table or schema; `ALL` / `USER` | static rule; run-time check |
| `ENABLE REPLICA TRIGGER`, `DISABLE RULE`, `CREATE RULE` | static rule (`R` also by the run-time check) |
| Switch inside `DO`, a function, or a string given to `EXECUTE` | static rule (and dynamic SQL is refused as such) |
| Replacing a guard function of an earlier migration | set rule and allowlist |
| `ALTER FUNCTION ... RENAME / SET SCHEMA / SET search_path`, `ALTER TRIGGER ... RENAME`, `CREATE OR REPLACE TRIGGER` | static rule |
| `SET [LOCAL] session_replication_role`, `set_config('session_replication_role', ...)`, `ALTER DATABASE/ROLE ... SET` of it | static rule; run-time check of the final value |
| A statement hidden behind `--` in a string or quoted name, split by a block comment, or placed in a dollar-quoted body | lexer |
| `COMMIT` inside a migration (to escape the rollback) | static rule |
| Trigger switched off out of band (manual SQL, a restore with triggers disabled) | run-time check at the end of every run |

## What is NOT covered (known limits)
- **A function with the same name but a different argument list** created with plain `CREATE FUNCTION` is an overload, not a replacement, and is not refused. Replacement is compared by name only, which is stricter for `OR REPLACE` and silent for overloads.
- **What a migration does between a paired `DISABLE` and `ENABLE`** is not judged by the guard. The pair is the owner-approved pattern for seeding rows into tables protected by an immutability trigger; whether the statements in between are legitimate remains a matter of review.
- **Shadowing**: a function of the same name created in another schema together with a changed `search_path` (for example `ALTER ROLE ... SET search_path`) is not refused. Triggers are bound to their function by identity and are not affected; a guard function that calls another function by unqualified name could be.
- **Weakening without replacing**: a new trigger or function that counteracts an existing one, a changed `CHECK` constraint (`ALTER TABLE ... ADD/VALIDATE CONSTRAINT`; dropping one is already refused), changed privileges (`GRANT`/`REVOKE`), `ALTER TABLE ... RENAME`, `ALTER TABLE ... OWNER TO`, `CREATE OR REPLACE VIEW`, row-level-security statements (not in use). These remain a matter of code review and of the catalog tests of each phase.
- **`CREATE OR REPLACE` inside the creating migration** is accepted; the migration is reviewed as one unit.
- **Direct catalog writes** (`UPDATE pg_trigger ...`) are not matched by a dedicated rule; they need superuser rights and `allow_system_table_mods`, and a resulting switched-off trigger is caught by the run-time check.
- **Changes outside migrations** (manual SQL by a database owner) are outside the guard; only a switched-off trigger is detected, at the next run of the migration runner. A replaced function body is not detected at run time.
- **`session_replication_role` set and reset inside one migration** by a means the text rules do not see would not be caught by the final-value check. Every textual mention is refused, dynamic SQL is refused, and setting it requires superuser rights. The final-value check has no automated test, because the test cluster's administrative role is deliberately not a superuser.
- **The guard is not a SQL parser.** It is strict on purpose: unusual but harmless spellings (for example `ALTER TABLE ONLY t DISABLE TRIGGER x`, a quoted language name, a guarded word inside a data string) are refused and must be rewritten in the canonical form.

## Behaviour changes to be aware of
- A migration that uses dynamic SQL, `ALTER FUNCTION`, `CREATE RULE` or a non-canonical trigger switch is now refused at load. No embedded migration does; system migration 0034 of the open MFA-policy work (paired `DISABLE`/`ENABLE`) was checked against the guard and is accepted.
- The runner now fails, also on a no-op run, when the database contains a switched-off trigger. Tests or tools that switch a trigger off must enable it again before the runner is used (all existing ones do).
- The words `DROP`, `TRUNCATE`, `DELETE FROM` are now also found after `--` inside a string literal or quoted identifier, where the old regular expression lost them.
