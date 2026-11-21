using Hasm.Infrastructure.SystemDatabase;

namespace Hasm.Infrastructure.Tests;

/// <summary>
/// Migration guard hardening (owner decision 2026-11-21, Q5): unsafe trigger switching, replacement of a function of an earlier
/// migration, and the statements that would neutralize a guard or hide a statement from the check. Design: docs/MIGRATION-GUARD.md.
/// </summary>
public class MigrationGuardTests
{
    private static MigrationScript P(string sql, string name = "0002_x.sql") => MigrationScript.Parse(name, sql);
    private static string Refused(string sql) => Assert.Throws<MigrationException>(() => P(sql)).Message;

    // ---------------------------------------------------------------- positive: everything that exists stays accepted

    [Fact]
    public void Every_embedded_migration_of_both_sets_is_still_accepted()
    {
        Assert.True(MigrationScript.LoadEmbedded().Count >= 33);
        Assert.True(MigrationScript.LoadEmbeddedCompany().Count >= 32);
    }

    [Fact]
    public void Every_existing_trigger_switch_is_a_matched_pair_on_the_same_table_and_trigger()
    {
        // Independent of the guard's own parser: each DISABLE line of the system set has its ENABLE twin later in the same file.
        var pairs = 0;
        foreach (var script in MigrationScript.LoadEmbedded().Concat(MigrationScript.LoadEmbeddedCompany()))
        {
            var lines = script.Sql.Split('\n').Select(l => l.Trim()).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Contains("DISABLE TRIGGER", StringComparison.Ordinal) || lines[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var twin = lines[i].Replace("DISABLE TRIGGER", "ENABLE TRIGGER", StringComparison.Ordinal);
                Assert.Contains(twin, lines.Skip(i + 1));
                pairs++;
            }
        }
        Assert.True(pairs >= 30, $"expected the 30 known pairs, found {pairs}");
    }

    [Theory]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a;\nINSERT INTO t_a VALUES (1);\nALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("alter table t_a disable trigger r_a; update t_a set x = 1; alter table t_a enable trigger r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_b DISABLE TRIGGER r_b; SELECT 1; ALTER TABLE t_b ENABLE TRIGGER r_b; ALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT 1; ALTER TABLE t_a ENABLE TRIGGER r_a; ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT 2; ALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE public.t_a DISABLE TRIGGER r_a; SELECT 1; ALTER TABLE PUBLIC.T_A ENABLE TRIGGER R_A;")]
    [InlineData("ALTER TABLE \"T a\" DISABLE TRIGGER \"R a\"; SELECT 1; ALTER TABLE \"T a\" ENABLE TRIGGER \"R a\";")]
    [InlineData("ALTER TABLE t_a /* why */ DISABLE -- now\n TRIGGER r_a; SELECT 1; ALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a ENABLE TRIGGER r_a;")]                                  // a plain ENABLE only restores protection
    public void A_paired_disable_and_enable_of_the_same_trigger_on_the_same_table_is_accepted(string sql) => P(sql);

    [Theory]
    [InlineData("-- ALTER TABLE t DISABLE TRIGGER ALL;\nCREATE TABLE x (a int);")]
    [InlineData("/* ALTER TABLE t DISABLE TRIGGER ALL; SET session_replication_role = replica; */ CREATE TABLE x (a int);")]
    [InlineData("/* outer /* nested ALTER FUNCTION f() RENAME TO g */ still a comment: DISABLE TRIGGER */ CREATE TABLE x (a int);")]
    [InlineData("CREATE FUNCTION f() RETURNS trigger LANGUAGE plpgsql AS $$\nBEGIN\n  -- never DISABLE TRIGGER here, never EXECUTE 'x'\n  RETURN NEW;\nEND;\n$$;")]
    [InlineData("SELECT '*/' /* '; ALTER TABLE t_a DISABLE TRIGGER r_a; -- */;")]              // the string closes first, the rest is one block comment
    public void Guarded_words_inside_a_real_comment_are_ignored(string sql) => P(sql);

    [Theory]
    [InlineData("CREATE TRIGGER r BEFORE UPDATE ON t FOR EACH ROW EXECUTE FUNCTION f();")]
    [InlineData("CREATE TRIGGER r BEFORE UPDATE ON t FOR EACH ROW EXECUTE PROCEDURE f();")]
    [InlineData("REVOKE EXECUTE ON FUNCTION f() FROM PUBLIC; GRANT EXECUTE ON FUNCTION f() TO r;")]
    [InlineData("INSERT INTO p VALUES ('company.attachmentretention.execute');")]
    [InlineData("CREATE FUNCTION f(a int) RETURNS int LANGUAGE sql AS $body$ SELECT $1 + 1 $body$; CREATE OR REPLACE FUNCTION f(a int) RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;")]
    [InlineData("DO $$ BEGIN IF NOT EXISTS (SELECT 1) THEN RAISE EXCEPTION 'it''s wrong'; END IF; END; $$;")]
    [InlineData("COMMENT ON TABLE t IS $$it's a table$$;")]
    public void Ordinary_statements_are_accepted(string sql) => P(sql);

    // ---------------------------------------------------------------- negative: unsafe trigger switching

    [Theory]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a;")]                                                             // never re-enabled
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; UPDATE t_a SET x = 1;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE TRIGGER r_b;")]                         // another trigger
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_b ENABLE TRIGGER r_a;")]                         // another table
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE other.t_a ENABLE TRIGGER r_a;")]                   // another schema
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER \"R_A\"; ALTER TABLE t_a ENABLE TRIGGER r_a;")]                     // a different (quoted) name
    [InlineData("ALTER TABLE t_a ENABLE TRIGGER r_a; ALTER TABLE t_a DISABLE TRIGGER r_a;")]                         // the ENABLE is not LATER
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE TRIGGER r_a; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER ALL;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER ALL; SELECT 1; ALTER TABLE t_a ENABLE TRIGGER ALL;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER USER; SELECT 1; ALTER TABLE t_a ENABLE TRIGGER USER;")]
    [InlineData("ALTER TABLE t_a ENABLE TRIGGER ALL;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE REPLICA TRIGGER r_a;")]                 // fires only in replica mode: effectively off
    [InlineData("ALTER TABLE t_a ENABLE REPLICA TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE ALWAYS TRIGGER r_a;")]                  // not the state it was in
    [InlineData("ALTER TABLE t_a ENABLE ALWAYS TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE RULE u_a;")]
    [InlineData("ALTER TABLE t_a DISABLE RULE u_a; ALTER TABLE t_a ENABLE RULE u_a;")]
    [InlineData("ALTER TABLE ONLY t_a DISABLE TRIGGER r_a; ALTER TABLE ONLY t_a ENABLE TRIGGER r_a;")]               // not the canonical form
    [InlineData("ALTER TABLE IF EXISTS t_a DISABLE TRIGGER r_a; ALTER TABLE IF EXISTS t_a ENABLE TRIGGER r_a;")]
    [InlineData("ALTER TABLE t_a ADD COLUMN z int, DISABLE TRIGGER r_a; ALTER TABLE t_a ENABLE TRIGGER r_a;")]       // several actions in one statement
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a, ENABLE TRIGGER r_a, DISABLE TRIGGER r_a;")]
    [InlineData("ALTER FOREIGN TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("DO $$ BEGIN ALTER TABLE t_a DISABLE TRIGGER r_a; END; $$;")]                                        // inside a block
    [InlineData("DO $$ BEGIN ALTER TABLE t_a DISABLE TRIGGER r_a; END; $$; ALTER TABLE t_a ENABLE TRIGGER r_a;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN ALTER TABLE t_a DISABLE TRIGGER r_a; END; $$;")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; DO $$ BEGIN ALTER TABLE t_a ENABLE TRIGGER r_a; END; $$;")]
    public void Unsafe_trigger_switching_is_refused(string sql) => Assert.Contains("not allowed", Refused(sql));

    // ---------------------------------------------------------------- negative: other ways to neutralize a guard

    [Theory]
    [InlineData("SET session_replication_role = replica;")]
    [InlineData("SET LOCAL session_replication_role TO 'replica';")]
    [InlineData("SELECT set_config('session_replication_role', 'replica', true);")]
    [InlineData("ALTER DATABASE d SET session_replication_role = replica;")]
    [InlineData("DO $$ BEGIN PERFORM set_config('session_replication_role', 'replica', true); END; $$;")]
    [InlineData("SET standard_conforming_strings = off;")]
    [InlineData("SET backslash_quote = on;")]
    [InlineData("ALTER FUNCTION f() RENAME TO g;")]
    [InlineData("ALTER FUNCTION f() SET SCHEMA other;")]
    [InlineData("ALTER FUNCTION f() SET search_path = evil;")]
    [InlineData("ALTER PROCEDURE f() RENAME TO g;")]
    [InlineData("ALTER ROUTINE f() OWNER TO r;")]
    [InlineData("ALTER TRIGGER r_a ON t_a RENAME TO r_b;")]
    [InlineData("ALTER EVENT TRIGGER e DISABLE;")]
    [InlineData("CREATE OR REPLACE TRIGGER r_a BEFORE UPDATE ON t_a FOR EACH ROW EXECUTE FUNCTION harmless();")]
    [InlineData("CREATE OR REPLACE CONSTRAINT TRIGGER r_a AFTER UPDATE ON t_a FOR EACH ROW EXECUTE FUNCTION harmless();")]
    [InlineData("CREATE RULE u AS ON INSERT TO t_a DO INSTEAD NOTHING;")]
    [InlineData("CREATE OR REPLACE RULE u AS ON UPDATE TO t_a DO INSTEAD NOTHING;")]
    [InlineData("DO $$ BEGIN EXECUTE 'ALTER TABLE t_a DISA' || 'BLE TRIGGER r_a'; END; $$;")]                        // dynamic SQL cannot be read
    [InlineData("DO $$ BEGIN EXECUTE format('CREATE OR REPLACE FUNCTION %I() RETURNS trigger LANGUAGE plpgsql AS %L', 'f', 'x'); END; $$;")]
    [InlineData("CREATE FUNCTION f(q text) RETURNS void LANGUAGE plpgsql AS $$ BEGIN EXECUTE q; END; $$;")]
    [InlineData("PREPARE p AS SELECT 1; EXECUTE p;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE plpython3u AS $$ pass $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE 'plpgsql' AS $$ BEGIN END; $$;")]                         // quoted language name: not canonical
    [InlineData("DO LANGUAGE plperl $$ 1; $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql BEGIN ATOMIC SELECT 1; END;")]
    [InlineData("CREATE TABLE a (x int); COMMIT; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("COMMIT;")]
    [InlineData("ROLLBACK;")]
    [InlineData("BEGIN; SELECT 1;")]
    [InlineData("START TRANSACTION;")]
    [InlineData("SAVEPOINT s;")]
    [InlineData("END;")]
    public void Statements_that_could_neutralize_a_guard_or_escape_the_check_are_refused(string sql) => Assert.Contains("not allowed", Refused(sql));

    // ---------------------------------------------------------------- review findings (independent review of PR 188)

    [Theory]
    // H1: a plpgsql variable may be called "function" or "procedure"; "EXECUTE function" is then dynamic SQL, not a trigger clause
    [InlineData("DO $$ DECLARE function text := 'ALTER TABLE t DISA' || 'BLE TRIGGER tg'; BEGIN EXECUTE function; END $$;")]
    [InlineData("DO $$ DECLARE procedure text := 'ALTER TABLE t DISA' || 'BLE TRIGGER tg'; BEGIN EXECUTE procedure; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE function('ALTER TABLE t DISA' || 'BLE TRIGGER tg'); END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE procedure('x'); END $$;")]
    [InlineData("DO $$ DECLARE function text := 'x'; BEGIN EXECUTE function || 'y'; END $$;")]
    [InlineData("DO $$ DECLARE function text := 'x'; BEGIN EXECUTE function USING 1; END $$;")]
    [InlineData("DO $$ DECLARE \"on\" text := 'x'; BEGIN EXECUTE on; END $$;")]
    [InlineData("DO $$ BEGIN EXECUTE FUNCTION f(); END $$;")]                                                        // the accepted phrase, but not in a CREATE TRIGGER
    [InlineData("DO $$ BEGIN CREATE TRIGGER r BEFORE UPDATE ON t FOR EACH ROW EXECUTE FUNCTION f(); END $$;")]       // nor inside a block
    [InlineData("SELECT 1; EXECUTE FUNCTION f();")]
    [InlineData("CREATE TRIGGER r BEFORE UPDATE ON t FOR EACH ROW EXECUTE FUNCTION f(); EXECUTE p;")]
    [InlineData("CREATE TRIGGER r BEFORE UPDATE ON t FOR EACH ROW EXECUTE f();")]
    [InlineData("CREATE FUNCTION g() RETURNS void LANGUAGE plpgsql AS $$ DECLARE function text; BEGIN EXECUTE function; END $$;")]
    [InlineData("EXECUTE ON;")]
    public void Execute_is_accepted_only_as_a_trigger_function_clause_or_a_privilege(string sql) => Assert.Contains("EXECUTE", Refused(sql));

    [Fact]
    public void Every_existing_EXECUTE_is_one_of_the_two_accepted_forms()
    {
        var uses = 0;
        foreach (var script in MigrationScript.LoadEmbedded().Concat(MigrationScript.LoadEmbeddedCompany()))
        {
            var code = System.Text.RegularExpressions.Regex.Replace(script.Sql, "--[^\n]*", "");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"\bEXECUTE\b\s+(\w+)\s+\w+\s*\("))
            {
                Assert.Equal("FUNCTION", m.Groups[1].Value);
                uses++;
            }
        }
        Assert.True(uses >= 286, $"expected the 286 known trigger clauses, found {uses}");
    }

    [Theory]
    // M1: nothing may decide which "=" or which function an unqualified name means
    [InlineData("SET search_path = evil, pg_catalog;")]
    [InlineData("SET LOCAL search_path TO evil;")]
    [InlineData("ALTER ROLE r SET search_path = evil, pg_catalog;")]
    [InlineData("ALTER DATABASE d SET search_path = evil;")]
    [InlineData("ALTER ROLE r IN DATABASE d SET search_path = evil;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE sql SET search_path = evil AS $$ SELECT 1 $$;")]
    [InlineData("CREATE OPERATOR evil.= (LEFTARG = oid, RIGHTARG = oid, FUNCTION = evil.never);")]
    [InlineData("DO $$ BEGIN CREATE OPERATOR evil.= (LEFTARG = oid, RIGHTARG = oid, FUNCTION = evil.never); END $$;")]
    // M2: a setting changed through a function call, whatever the setting
    [InlineData("SELECT set_config('search_' || 'path', 'evil', false);")]
    [InlineData("SELECT pg_catalog.set_config('session_' || 'replication_role', 'replica', true);")]
    [InlineData("DO $$ BEGIN PERFORM set_config ('x' || 'y', 'z', true); END $$;")]
    // M3: direct catalog writes
    [InlineData("UPDATE pg_trigger SET tgenabled = 'D';")]
    [InlineData("UPDATE pg_catalog.pg_trigger SET tgenabled = 'D';")]
    [InlineData("UPDATE ONLY pg_catalog . pg_trigger SET tgenabled = 'D';")]
    [InlineData("UPDATE \"pg_trigger\" SET tgenabled = 'D';")]
    [InlineData("UPDATE \"pg_catalog\".\"pg_trigger\" SET tgenabled = 'D';")]
    [InlineData("update pg_proc set prosrc = 'begin return new; end';")]
    [InlineData("INSERT INTO pg_catalog.pg_depend SELECT 1;")]
    [InlineData("INSERT INTO pg_attrdef VALUES (1);")]
    [InlineData("DELETE FROM pg_trigger;")]
    [InlineData("MERGE INTO pg_trigger t USING s ON true WHEN MATCHED THEN UPDATE SET tgenabled = 'D';")]
    [InlineData("COPY pg_trigger FROM STDIN;")]
    [InlineData("DO $$ BEGIN UPDATE pg_trigger SET tgenabled = 'D'; END $$;")]
    // L1: COPY in any form, extensions
    [InlineData("COPY t FROM '/etc/passwd';")]
    [InlineData("COPY t TO PROGRAM 'sh';")]
    [InlineData("COPY (SELECT 1) TO STDOUT;")]
    [InlineData("CREATE EXTENSION plpython3u;")]
    [InlineData("CREATE EXTENSION IF NOT EXISTS pgcrypto;")]
    // L4: MERGE (it can delete without the words DELETE FROM)
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN MATCHED THEN DELETE;")]
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN NOT MATCHED THEN INSERT VALUES (1);")]
    public void Settings_operators_catalog_writes_copy_extensions_and_merge_are_refused(string sql) => Assert.Contains("not allowed", Refused(sql));

    [Theory]
    // M2: an identifier written with Unicode escapes can spell a guarded name without the guard seeing it
    [InlineData("ALTER TABLE t DISABLE TRIGGER U&\"\\0061ll\";")]
    [InlineData("CREATE OR REPLACE FUNCTION U&\"f\\005fguard\"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;")]
    [InlineData("SELECT 1 AS u&\"x\";")]
    [InlineData("DO $$ BEGIN PERFORM U&\"set\\005fconfig\"('a', 'b', true); END $$;")]
    // L3: a "$" that may or may not open a dollar quote
    [InlineData("SELECT 1$$; ALTER TABLE t DISABLE TRIGGER r; --$$;")]
    [InlineData("SELECT $$a$$$$; ALTER TABLE t DISABLE TRIGGER r; --$$;")]
    [InlineData("SELECT $a$ x $a$$b$; ALTER TABLE t DISABLE TRIGGER r; --$b$;")]
    [InlineData("SELECT 1$x$; ALTER TABLE t DISABLE TRIGGER r; --$x$;")]
    public void Unicode_escaped_identifiers_and_ambiguous_dollar_quotes_are_refused(string sql) => Assert.Contains("cannot be read safely", Refused(sql));

    [Fact]
    public void Unicode_escaped_string_literals_stay_accepted_and_parameters_are_not_dollar_quotes()
    {
        P("INSERT INTO t VALUES (U&'\\0627\\0644');");                              // company 0004 seeds an Arabic name this way
        P("CREATE FUNCTION f(a int, b int) RETURNS int LANGUAGE sql AS $$ SELECT $1+$2 $$;");
    }

    [Theory]
    // L2: names are compared part by part; a quoted name that contains a dot is one part, not schema.table
    [InlineData("ALTER TABLE \"a.b\" DISABLE TRIGGER r; ALTER TABLE a.b ENABLE TRIGGER r;")]
    [InlineData("ALTER TABLE a.b DISABLE TRIGGER r; ALTER TABLE \"a.b\" ENABLE TRIGGER r;")]
    [InlineData("ALTER TABLE a.\"b.c\" DISABLE TRIGGER r; ALTER TABLE \"a.b\".c ENABLE TRIGGER r;")]
    public void A_quoted_name_with_a_dot_is_not_confused_with_a_qualified_name(string sql) => Assert.Contains("does not re-enable", Refused(sql));

    [Fact]
    public void A_quoted_trigger_called_all_is_a_name_but_is_refused_like_the_keyword_only_when_unquoted()
    {
        Assert.Contains("TRIGGER ALL", Refused("ALTER TABLE t DISABLE TRIGGER all; ALTER TABLE t ENABLE TRIGGER all;"));
        Assert.Contains("TRIGGER USER", Refused("ALTER TABLE t DISABLE TRIGGER User; ALTER TABLE t ENABLE TRIGGER User;"));
        P("ALTER TABLE t DISABLE TRIGGER \"all\"; SELECT 1; ALTER TABLE t ENABLE TRIGGER \"all\";");
    }

    [Fact]
    public void A_function_in_a_quoted_schema_with_a_dot_is_compared_by_its_function_name()
    {
        var first = P(Create, "0001_a.sql");
        var later = P("CREATE OR REPLACE FUNCTION \"x.y\".f_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;", "0002_b.sql");
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([first, later]));
        // "x.f_guard" in quotes is ONE name, a different function: not a replacement of f_guard.
        MigrationScript.Validate([first, P("CREATE OR REPLACE FUNCTION \"x.f_guard\"() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "0002_b.sql")]);
    }

    // ---------------------------------------------------------------- the runner's decisions (pure parts)

    private static MigrationRunner.TriggerState T(uint table, string name, uint function, char enabled = 'O') => new(table, $"t{table}", name, function, enabled);

    [Theory]
    [InlineData('O', true)]
    [InlineData('A', true)]
    [InlineData('D', false)]
    [InlineData('R', false)]
    [InlineData('X', false)]        // an unknown state is not trusted
    public void Only_origin_and_always_fire_in_a_normal_session(char enabled, bool fires) => Assert.Equal(fires, MigrationRunner.FiresInNormalSession(enabled));

    [Fact]
    public void The_post_migration_decision_refuses_switched_off_removed_and_rebound_triggers()
    {
        MigrationRunner.TriggerState[] before = [T(1, "r_a", 10), T(2, "r_b", 20)];

        Assert.Null(MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10), T(2, "r_b", 20), T(3, "r_new", 30)], []));
        Assert.Null(MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10, 'A'), T(2, "r_b", 20)], []));

        Assert.Contains("t1.r_a (D)", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10, 'D'), T(2, "r_b", 20)], []));
        Assert.Contains("t2.r_b (R)", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10), T(2, "r_b", 20, 'R')], []));
        Assert.Contains("t3.r_new (D)", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10), T(2, "r_b", 20), T(3, "r_new", 30, 'D')], []));
        Assert.Contains("event trigger e (D)", MigrationRunner.PostMigrationProblem(before, [.. before], ["event trigger e (D)"]));
        Assert.Contains("removes", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10)], []));
        Assert.Contains("t2.r_b", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10)], []));
        Assert.Contains("removes", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 10), T(9, "r_b", 20)], []));       // same name on another table is another trigger
        Assert.Contains("another function", MigrationRunner.PostMigrationProblem(before, [T(1, "r_a", 11), T(2, "r_b", 20)], []));
    }

    [Theory]
    [InlineData("origin", "origin", true)]
    [InlineData("origin", "replica", false)]
    [InlineData("origin", "local", false)]
    [InlineData("replica", "replica", false)]       // unchanged is not enough: it must be origin
    [InlineData("replica", "origin", false)]
    [InlineData("local", "local", false)]
    public void The_replication_role_must_be_origin_before_and_unchanged_after(string before, string after, bool accepted) =>
        Assert.Equal(accepted, MigrationRunner.ReplicationRoleProblem(before, after) is null);

    // ---------------------------------------------------------------- negative: hiding a statement

    [Theory]
    // "--" inside a string literal is not a comment: what follows on the line is a statement (the old check lost it)
    [InlineData("SELECT '--'; DROP TABLE t;")]
    [InlineData("SELECT '--'; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("INSERT INTO n VALUES ('a -- b'); DELETE FROM t;")]
    // "--" inside a quoted identifier is not a comment either
    [InlineData("SELECT 1 AS \"--\"; TRUNCATE t;")]
    // a block comment between the words does not split them
    [InlineData("ALTER TABLE t_a DISABLE/**/TRIGGER ALL;")]
    [InlineData("ALTER TABLE t_a DISABLE /* x */ TRIGGER /* y */ r_a;")]
    [InlineData("ALTER TABLE t_a DISABLE -- comment\n TRIGGER r_a;")]
    [InlineData("ALTER/**/FUNCTION f() RENAME TO g;")]
    [InlineData("SET session_replication_role /* */ = replica;")]
    // the end of a block comment inside a string, and a quote inside a comment, do not confuse the reader
    [InlineData("/* it's */ ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("-- it's\nALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("SELECT '/*'; ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT '*/';")]
    [InlineData("/* /* nested */ still a comment */ ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    // dollar quoting: a body is code, whatever its tag; a quote inside it does not swallow the rest
    [InlineData("DO $do$ BEGIN ALTER TABLE t_a DISABLE TRIGGER ALL; END $do$;")]
    [InlineData("DO $a$ BEGIN PERFORM $b$ ' $b$; ALTER TABLE t_a DISABLE TRIGGER r_a; END $a$;")]
    [InlineData("SELECT $$ ' $$; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("SELECT $q$ $$ $q$; ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT $q$ $$ $q$;")]
    [InlineData("SELECT $$ -- $$; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("SELECT $$ /* $$; ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT $$ */ $$;")]
    [InlineData("COMMENT ON TABLE t IS $$it's$$; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    // E'' strings use backslash escapes
    [InlineData("SELECT E'\\''; ALTER TABLE t_a DISABLE TRIGGER r_a; --';")]
    [InlineData("SELECT E'a\\\\'; ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT ' ';")]
    // a guarded word inside a string is still refused (it may be executed later; the guard cannot know)
    [InlineData("SELECT 'ALTER TABLE t_a DISABLE TRIGGER r_a';")]
    [InlineData("ALTER TABLE t_a DISABLE TRIGGER r_a; SELECT 'ALTER TABLE t_a ENABLE TRIGGER r_a';")]
    // function bodies are read too
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN DROP TABLE t; END; $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS void LANGUAGE sql AS $$ SELECT '--'; DELETE FROM t; $$;")]
    public void A_statement_hidden_behind_comments_strings_or_dollar_quotes_is_refused(string sql) => Assert.Contains("not allowed", Refused(sql));

    [Theory]
    [InlineData("SELECT 'never closed; ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("/* never closed ALTER TABLE t_a DISABLE TRIGGER r_a;")]
    [InlineData("DO $$ BEGIN NULL; END;")]
    [InlineData("SELECT 1 AS \"never closed;")]
    [InlineData("SELECT 'a\\'; SELECT 'b';")]                         // read differently when standard_conforming_strings is off
    public void Sql_that_cannot_be_read_unambiguously_is_refused(string sql) => Assert.Contains("cannot be read safely", Refused(sql));

    [Fact]
    public void The_existing_destructive_rule_is_unchanged_including_inside_block_comments_and_strings()
    {
        Assert.Contains("destructive", Refused("/* DROP */ CREATE TABLE x (a int);"));       // as before: only "--" comments are exempt for DROP/TRUNCATE/DELETE
        Assert.Contains("destructive", Refused("SELECT 'DROP TABLE x';"));
        P("-- DROP nothing\nCREATE TRIGGER r BEFORE TRUNCATE ON t FOR EACH STATEMENT EXECUTE FUNCTION f();");
    }

    // ---------------------------------------------------------------- replacement of a function of an earlier migration

    private const string Create = "CREATE FUNCTION f_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'immutable'; END; $$;";
    private const string Weaken = "CREATE OR REPLACE FUNCTION f_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;";

    [Theory]
    [InlineData(Weaken)]
    [InlineData("create or replace function F_GUARD() returns trigger language plpgsql as $$ begin return new; end; $$;")]
    [InlineData("CREATE OR REPLACE FUNCTION public.f_guard() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;")]
    [InlineData("CREATE OR REPLACE FUNCTION \"f_guard\"() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;")]
    [InlineData("CREATE OR /* x */ REPLACE -- y\n FUNCTION f_guard () RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;")]
    [InlineData("CREATE OR REPLACE FUNCTION f_guard(extra int) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;")]        // compared by name only (stricter)
    [InlineData("CREATE OR REPLACE PROCEDURE f_guard() LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;")]
    [InlineData("DO $$ BEGIN CREATE OR REPLACE FUNCTION f_guard() RETURNS trigger LANGUAGE plpgsql AS $f$ BEGIN RETURN NEW; END; $f$; END; $$;")]
    public void Replacing_a_function_of_an_earlier_migration_is_refused_when_not_allowlisted(string replacement)
    {
        var first = P(Create, "0001_a.sql");
        var later = P(replacement, "0002_b.sql");          // on its own it is a valid script: the rule needs the set
        var ex = Assert.Throws<MigrationException>(() => MigrationScript.Validate([first, later]));
        Assert.Contains("f_guard", ex.Message);
        Assert.Contains("0001_a.sql", ex.Message);
        Assert.Contains("allowlist", ex.Message);
    }

    [Fact]
    public void Replacing_is_refused_however_many_migrations_lie_between()
    {
        var set = new[] { P(Create, "0001_a.sql"), P("SELECT 1;", "0002_b.sql"), P("SELECT 2;", "0003_c.sql"), P(Weaken, "0004_d.sql") };
        Assert.Throws<MigrationException>(() => MigrationScript.Validate(set));
    }

    [Fact]
    public void Creating_and_replacing_inside_one_migration_and_a_first_creation_with_or_replace_are_accepted()
    {
        MigrationScript.Validate([P(Create + "\n" + Weaken, "0001_a.sql"), P("CREATE OR REPLACE FUNCTION f_new() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "0002_b.sql")]);
        MigrationScript.Validate([P(Create, "0001_a.sql"), P("CREATE FUNCTION f_other() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;", "0002_b.sql")]);
    }

    [Fact]
    public void A_function_whose_name_cannot_be_read_is_refused() =>
        Assert.Contains("cannot read", Refused("CREATE OR REPLACE FUNCTION a.b.c() RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;"));

    [Fact]
    public void The_allowlist_is_exactly_the_replacements_that_exist_in_the_embedded_migrations()
    {
        // Found independently of the guard: every "CREATE OR REPLACE FUNCTION x" whose x an EARLIER migration of the same set created.
        var found = new List<(string Migration, string Checksum, string Function)>();
        foreach (var set in new[] { MigrationScript.LoadEmbedded(), MigrationScript.LoadEmbeddedCompany() })
        {
            var earlier = new HashSet<string>();
            foreach (var script in set)
            {
                var code = System.Text.RegularExpressions.Regex.Replace(script.Sql, "--[^\n]*", "");
                var all = System.Text.RegularExpressions.Regex.Matches(code, @"CREATE\s+(OR\s+REPLACE\s+)?FUNCTION\s+(\w+)");
                foreach (System.Text.RegularExpressions.Match m in all)
                    if (m.Groups[1].Success && earlier.Contains(m.Groups[2].Value)) found.Add((script.Name, script.Checksum, m.Groups[2].Value));
                foreach (System.Text.RegularExpressions.Match m in all) earlier.Add(m.Groups[2].Value);
            }
        }
        var allowed = MigrationGuard.ApprovedFunctionReplacements;
        Assert.Equal(found.Order(), allowed.Select(a => (a.Migration, a.Checksum, a.Function)).Order());
        Assert.All(allowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Justification)));
        Assert.Equal(allowed.Count, allowed.Select(a => (a.Migration, a.Function)).Distinct().Count());

        // The entries present when the rule was introduced (owner decision 2026-11-21, Q5). A new entry is a safeguard change: it needs
        // the owner-approved procedure of docs/DEVELOPMENT.md, and this pinned list is then extended in the same reviewed change.
        Assert.Equal(
            new[]
            {
                "0009_company_gl_completion.sql:f_660e4c73", "0009_company_gl_completion.sql:f_ba4c6a0d",
                "0027_company_customer_groups.sql:f_4fab1a41", "0027_company_customer_groups.sql:f_86f87c19",
                "0029_company_commercial_approval.sql:f_7d3cbe20", "0029_company_commercial_approval.sql:f_bc72c55d",
            },
            allowed.Select(a => $"{a.Migration}:{a.Function}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_allowlisted_replacement_is_accepted_only_with_the_exact_reviewed_content()
    {
        var company = MigrationScript.LoadEmbeddedCompany();
        MigrationScript.Validate(company);                                         // the six allowlisted replacements pass

        // The same migration with any other content (here: one more line) no longer matches its allowlist entry.
        var nine = company.Single(s => s.Version == 9);
        var edited = MigrationScript.Parse(nine.Name, nine.Sql + "\nSELECT 1;");
        var ex = Assert.Throws<MigrationException>(() => MigrationScript.Validate([.. company.Where(s => s.Version < 9), edited]));
        Assert.Contains("allowlist", ex.Message);

        // Nor can another earlier function be replaced under the cover of an entry: adding the statement changes the content.
        var eight = company.Single(s => s.Version == 8);
        var other = System.Text.RegularExpressions.Regex.Match(eight.Sql, @"CREATE FUNCTION (\w+)\(\) RETURNS trigger").Groups[1].Value;
        Assert.NotEmpty(other);
        var smuggled = MigrationScript.Parse(nine.Name, nine.Sql + $"\nCREATE OR REPLACE FUNCTION {other}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END; $$;");
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([.. company.Where(s => s.Version < 9), smuggled]));

        // And an entry does not travel to another migration that carries the allowlisted file name's function.
        var renamed = MigrationScript.Parse("0009_other.sql", nine.Sql);
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([.. company.Where(s => s.Version < 9), renamed]));
    }

    [Fact]
    public void A_script_built_without_Parse_is_held_to_the_same_rules_by_Validate()
    {
        var direct = new MigrationScript(1, "0001_a.sql", "ALTER TABLE t_a DISABLE TRIGGER ALL;");
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([direct]));
        Assert.Throws<MigrationException>(() => MigrationScript.Validate([new MigrationScript(1, "0001_a.sql", "DROP TABLE t;")]));
    }

    [Fact]
    public void Checksums_of_the_embedded_migrations_are_not_affected_by_the_guard()
    {
        // The checksum is still the SHA-256 of the text with normalized line endings, nothing else.
        foreach (var script in MigrationScript.LoadEmbedded().Concat(MigrationScript.LoadEmbeddedCompany()))
        {
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script.Sql.Replace("\r\n", "\n")))).ToLowerInvariant();
            Assert.Equal(expected, script.Checksum);
        }
    }
}
