using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Hasm.Infrastructure.SystemDatabase;

/// <summary>
/// One owner-approved replacement of a function that an EARLIER migration created. The entry is bound to the exact content of the
/// replacing migration (its checksum), so an edited or different migration can never reuse it.
/// </summary>
public sealed record GuardFunctionReplacement(string Migration, string Checksum, string Function, string Justification);

/// <summary>
/// The migration guard (owner decision 2026-11-21, Q5 of docs/PHASE-3.8D-RESUME-REVIEW.md; design: docs/MIGRATION-GUARD.md).
/// It refuses, before anything is executed, a migration that could remove or neutralize a database safeguard:
/// destructive statements, unsafe trigger switching, replacement of a function of an earlier migration that is not on the
/// allowlist below, and the statements that would make these rules unreadable or bypass them. It is a static text check and
/// therefore deliberately strict: it accepts a small set of canonical forms and refuses everything else that mentions them.
/// </summary>
public static partial class MigrationGuard
{
    // The allowlist (ApprovedFunctionReplacements) is the other part of this class: MigrationSafety/ApprovedFunctionReplacements.cs.
    // It lives outside SystemDatabase/ and CompanyDatabase/ because the schema generators (tools/schema) scan those two folders for
    // physical names that are already taken, and the allowlist has to spell the names of the functions it authorises.

    /// <summary>What one migration does that matters across migrations; <see cref="Violation"/> is null when the script itself is acceptable.</summary>
    internal sealed record Analysis(string? Violation, IReadOnlySet<string> CreatedFunctions, IReadOnlySet<string> ReplacedFunctions);

    private static readonly ConcurrentDictionary<string, Analysis> Cache = new(StringComparer.Ordinal);

    private const string Ident = @"(?:""(?:[^""]|"""")+""|[A-Za-z_][A-Za-z0-9_$]*)";
    private const string QualifiedIdent = Ident + @"(?:\s*\.\s*" + Ident + ")?";

    [GeneratedRegex(@"--[^\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyLineComment();

    // "BEFORE TRUNCATE" is the append-only guard trigger (protective); every other TRUNCATE statement stays forbidden.
    [GeneratedRegex(@"\bDROP\b|(?<!\bBEFORE\s+)\bTRUNCATE\b|\bDELETE\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Destructive();

    [GeneratedRegex(@"\b(?:DISABLE|ENABLE)\s+(?:(?:REPLICA|ALWAYS)\s+)?(?:TRIGGER|RULE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerSwitch();

    [GeneratedRegex(@"^ALTER\s+TABLE\s+(" + QualifiedIdent + @")\s+(DISABLE|ENABLE)\s+TRIGGER\s+(" + Ident + @")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalTriggerSwitch();

    [GeneratedRegex(@"\bCREATE\s+(OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b(?:\s+(" + QualifiedIdent + @")\s*\()?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateFunction();

    // EXECUTE is accepted in exactly two places: the function of a trigger definition ("EXECUTE FUNCTION|PROCEDURE <name> (") in a
    // top-level CREATE TRIGGER statement, and the EXECUTE privilege ("EXECUTE ON") in a top-level GRANT / REVOKE / ALTER DEFAULT
    // PRIVILEGES statement. Every other EXECUTE (dynamic SQL, PREPARE/EXECUTE, anything inside a body) is refused.
    [GeneratedRegex(@"\bEXECUTE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnyExecute();

    [GeneratedRegex(@"\bEXECUTE\s+(?:FUNCTION|PROCEDURE)\s+" + QualifiedIdent + @"\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TriggerExecute();

    [GeneratedRegex(@"\bEXECUTE\s+ON\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivilegeExecute();

    [GeneratedRegex(@"^CREATE\s+(?:CONSTRAINT\s+)?TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTriggerStatement();

    [GeneratedRegex(@"^(?:GRANT|REVOKE|ALTER\s+DEFAULT\s+PRIVILEGES)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivilegeStatement();

    // Every NAME that starts with "pg_" (system catalogs, the pg_catalog and pg_temp schemas, system functions), quoted or not. The bare
    // prefix "pg_" with nothing after it names nothing and is not matched: system migration 0001 uses it in the pattern ('^pg_') that keeps
    // company database names out of the reserved prefix.
    [GeneratedRegex(@"\bpg_\w+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SystemName();

    /// <summary>
    /// The ONLY "pg_" names a migration may mention, each as an unquoted, unqualified function call "name(". Read-only built-in
    /// functions that the queued migrations need; no catalog relation is ever listed, so no view or statement can be built on one.
    /// Extending this list is a change of the guard (docs/MIGRATION-GUARD.md, "System names").
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedSystemFunctions = new HashSet<string>(StringComparer.Ordinal)
    {
        "pg_current_xact_id",   // the id of the current transaction: binds a row to the transaction that wrote it (plan execution, Phase 3.8D)
        "pg_has_role",          // role-membership test in a trigger (planned TOTP guard); reads, never changes
    };

    [GeneratedRegex(@"\b(?:COPY|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopyOrMerge();

    [GeneratedRegex(@"\bLANGUAGE\s+(?!(?:sql|plpgsql)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForeignLanguage();

    [GeneratedRegex(@"^(?:BEGIN|START\s+TRANSACTION|COMMIT|ROLLBACK|ABORT|END|SAVEPOINT|RELEASE|PREPARE\s+TRANSACTION)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionControl();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static readonly (Regex Pattern, string Reason)[] Neutralizers =
    [
        (new(@"\bsession_replication_role\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "changes or mentions session_replication_role (it switches every trigger off)"),
        (new(@"\bstandard_conforming_strings\b|\bbackslash_quote\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "changes or mentions a setting that alters how string literals are read"),
        (new(@"\bALTER\s+(?:FUNCTION|PROCEDURE|ROUTINE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains ALTER FUNCTION/PROCEDURE/ROUTINE (rename, move, owner or settings of a function)"),
        (new(@"\bALTER\s+(?:EVENT\s+)?TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains ALTER TRIGGER (rename or re-bind of a trigger)"),
        (new(@"\bCREATE\s+OR\s+REPLACE\s+(?:CONSTRAINT\s+)?TRIGGER\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains CREATE OR REPLACE TRIGGER (it can re-bind an existing guard trigger; use CREATE TRIGGER)"),
        (new(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?RULE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains CREATE RULE (a rewrite rule can route statements around a guard trigger)"),
        (new(@"\bsearch_path\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "changes or mentions search_path (it decides which function or operator an unqualified name means)"),
        (new(@"\bset_config\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "mentions set_config, quoted or not (a setting changed through a function call cannot be judged by the guard)"),
        (new(@"\b(?:query_to_xml\w*|cursor_to_xml\w*|ts_stat|ts_rewrite)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "mentions a function that runs a query given as text (dynamic SQL without EXECUTE)"),
        (new(@"\bdblink\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "mentions dblink (statements sent over another connection cannot be read by the guard)"),
        (new(@"\bCREATE\s+OPERATOR\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains CREATE OPERATOR (an operator can shadow a built-in one)"),
        (new(@"\bCREATE\s+EXTENSION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains CREATE EXTENSION (code the guard cannot read)"),
        (new(@"\bBEGIN\s+ATOMIC\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "contains a BEGIN ATOMIC function body (the guard cannot read it; use a dollar-quoted body)"),
    ];

    /// <summary>Throws when the single migration is not acceptable on its own (the rules that need no other migration).</summary>
    public static void EnsureAcceptable(string fileName, string sql)
    {
        var analysis = Analyze(sql);
        if (analysis.Violation is not null) throw new MigrationException($"Migration {fileName} {analysis.Violation}");
    }

    /// <summary>
    /// Throws when a migration of the ordered set replaces a function that an earlier migration of the same set created and the
    /// replacement is not on <see cref="ApprovedFunctionReplacements"/>. Also re-checks every script on its own, so a script that was
    /// not built through <see cref="MigrationScript.Parse"/> is held to the same rules.
    /// </summary>
    public static void EnsureAcceptableSet(IReadOnlyList<MigrationScript> ordered)
    {
        var createdEarlier = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var script in ordered)
        {
            var analysis = Analyze(script.Sql);
            if (analysis.Violation is not null) throw new MigrationException($"Migration {script.Name} {analysis.Violation}");
            foreach (var function in analysis.ReplacedFunctions)
            {
                if (!createdEarlier.TryGetValue(function, out var origin)) continue;
                if (ApprovedFunctionReplacements.Any(a => a.Migration == script.Name && a.Checksum == script.Checksum && a.Function == function)) continue;
                throw new MigrationException(
                    $"Migration {script.Name} replaces function {function}, which migration {origin} created. Replacing a function of an earlier migration is a safeguard change and is " +
                    "refused unless the exact migration and function are on the owner-approved allowlist (MigrationGuard.ApprovedFunctionReplacements; procedure in docs/DEVELOPMENT.md).");
            }
            foreach (var function in analysis.CreatedFunctions) createdEarlier.TryAdd(function, script.Name);
        }
    }

    internal static Analysis Analyze(string sql)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        return Cache.GetOrAdd(key, _ => AnalyzeUncached(sql));
    }

    private static Analysis AnalyzeUncached(string sql)
    {
        var none = new HashSet<string>();
        Analysis Refuse(string violation) => new(violation, none, none);

        // The original check, unchanged: every "--" to the end of the line is ignored, everything else is read.
        if (Destructive().IsMatch(LegacyLineComment().Replace(sql, "")))
            return Refuse("contains a destructive statement (DROP/TRUNCATE/DELETE); not allowed.");

        MigrationSqlLexer.Result lexed;
        try { lexed = MigrationSqlLexer.Lex(sql); }
        catch (MigrationException e) { return Refuse($"cannot be read safely ({e.Message}); not allowed."); }

        // The same check on the accurately read text, so a statement cannot hide behind "--" inside a string literal or a quoted name.
        if (Destructive().IsMatch(lexed.Scan))
            return Refuse("contains a destructive statement (DROP/TRUNCATE/DELETE); not allowed.");

        foreach (var (pattern, reason) in Neutralizers)
            if (pattern.IsMatch(lexed.Scan)) return Refuse($"{reason}; not allowed.");

        foreach (Match m in SystemName().Matches(lexed.Scan))
        {
            var quoted = m.Index > 0 && lexed.Scan[m.Index - 1] == '"';
            var qualified = lexed.Scan.AsSpan(0, m.Index).TrimEnd().EndsWith(".");
            var called = lexed.Scan.AsSpan(m.Index + m.Length).TrimStart().StartsWith("(");
            if (quoted || qualified || !called || !AllowedSystemFunctions.Contains(m.Value.ToLowerInvariant()))
                return Refuse($"mentions the system name {m.Value} (every name starting with pg_ is refused, in code and in strings alike, except a plain call of: {string.Join(", ", AllowedSystemFunctions.Order(StringComparer.Ordinal))}); not allowed.");
        }
        if (CopyOrMerge().IsMatch(lexed.Code))
            return Refuse("contains COPY or MERGE (bulk or conditional data changes, including deletion and file access, that the guard does not judge); not allowed.");
        if (ForeignLanguage().IsMatch(lexed.Code))
            return Refuse("declares a function or block in a language other than sql or plpgsql (written unquoted); the guard cannot read it; not allowed.");

        // Trigger switching: only "ALTER TABLE <table> DISABLE TRIGGER <trigger>" followed later, in the same migration, by
        // "ALTER TABLE <table> ENABLE TRIGGER <trigger>" for exactly the same table and trigger, both as whole top-level statements.
        var disabled = new List<(string Table, string Trigger)>();
        var canonical = 0;
        var acceptedExecutes = 0;
        foreach (var raw in lexed.Statements)
        {
            var statement = Whitespace().Replace(raw, " ").Trim();
            if (statement.Length == 0) continue;
            if (TransactionControl().IsMatch(statement))
                return Refuse("contains a transaction-control statement (BEGIN/COMMIT/ROLLBACK/SAVEPOINT...); a migration runs in the runner's single transaction; not allowed.");
            if (CreateTriggerStatement().IsMatch(statement) && AnyExecute().Count(statement) == 1 && TriggerExecute().Count(statement) == 1) acceptedExecutes++;
            else if (PrivilegeStatement().IsMatch(statement) && AnyExecute().Count(statement) == PrivilegeExecute().Count(statement)) acceptedExecutes += AnyExecute().Count(statement);
            var m = CanonicalTriggerSwitch().Match(statement);
            if (!m.Success) continue;
            canonical++;
            // Compared as tuples of name parts (never as one dotted string: a quoted name may itself contain a dot).
            var table = Key(m.Groups[1].Value);
            var trigger = Key(m.Groups[3].Value);
            if (m.Groups[3].Value.Equals("all", StringComparison.OrdinalIgnoreCase) || m.Groups[3].Value.Equals("user", StringComparison.OrdinalIgnoreCase))
                return Refuse($"switches TRIGGER {m.Groups[3].Value.ToUpperInvariant()}; only a single named trigger may be switched; not allowed.");
            var pair = (table, trigger);
            if (m.Groups[2].Value.Equals("DISABLE", StringComparison.OrdinalIgnoreCase))
            {
                if (disabled.Contains(pair)) return Refuse($"disables trigger {Show(trigger)} on {Show(table)} twice without re-enabling it; not allowed.");
                disabled.Add(pair);
            }
            else
            {
                // A plain ENABLE of a named trigger only restores protection; it is accepted with or without a preceding DISABLE.
                disabled.Remove(pair);
            }
        }
        if (TriggerSwitch().Count(lexed.Scan) != canonical)
            return Refuse("switches a trigger or rule in a form other than the whole top-level statements \"ALTER TABLE <table> DISABLE TRIGGER <name>\" / " +
                          "\"ALTER TABLE <table> ENABLE TRIGGER <name>\" (for example ALL, USER, REPLICA, ALWAYS, RULE, several actions in one ALTER TABLE, or inside a DO block, function or string); not allowed.");
        if (AnyExecute().Count(lexed.Code) != acceptedExecutes)
            return Refuse("contains EXECUTE outside the two accepted forms (\"EXECUTE FUNCTION|PROCEDURE <name>(\" in a top-level CREATE TRIGGER, \"EXECUTE ON\" in a top-level GRANT/REVOKE): " +
                          "dynamic SQL, PREPARE/EXECUTE and any EXECUTE inside a DO block or function body cannot be read by the guard; not allowed.");
        if (disabled.Count > 0)
            return Refuse($"disables trigger {Show(disabled[0].Trigger)} on {Show(disabled[0].Table)} and does not re-enable exactly that trigger on exactly that table later in the same migration; not allowed.");

        var created = new HashSet<string>(StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in CreateFunction().Matches(lexed.Scan))
        {
            if (!m.Groups[2].Success)
                return Refuse("creates a function or procedure whose name the guard cannot read; not allowed.");
            var name = Parts(m.Groups[2].Value)[^1];     // compared by the function name only, whatever the schema or the argument list (stricter)
            created.Add(name);
            if (m.Groups[1].Success) replaced.Add(name);
        }
        return new Analysis(null, created, replaced);
    }

    /// <summary>The parts of a (possibly schema-qualified) name: unquoted parts fold to lower case as PostgreSQL does, quoted ones keep their exact spelling.</summary>
    private static string[] Parts(string identifier) =>
        [.. Regex.Matches(identifier, Ident, RegexOptions.CultureInvariant).Select(p => p.Value.StartsWith('"') ? p.Value[1..^1].Replace("\"\"", "\"") : p.Value.ToLowerInvariant())];

    // NUL cannot occur in a PostgreSQL identifier, so the joined key is an unambiguous tuple.
    private static string Key(string identifier) => string.Join('\0', Parts(identifier));
    private static string Show(string key) => key.Replace('\0', '.');
}

/// <summary>
/// Reads migration SQL the way PostgreSQL does, far enough to tell code from comments and from literals: line comments, nested block
/// comments, string literals (plain, E'' with backslash escapes), quoted identifiers and dollar-quoted bodies. Dollar-quoted bodies
/// (functions, DO blocks) are read again as SQL, because that is how the server will run them.
/// </summary>
internal static class MigrationSqlLexer
{
    /// <param name="Scan">Comments removed; string literals and dollar-quoted bodies KEPT (bodies with their own comments removed). Used for "must not appear anywhere" rules.</param>
    /// <param name="Code">As <paramref name="Scan"/>, but the content of string literals is blanked. Used where a word inside a data string must not count.</param>
    /// <param name="Statements">The top-level statements of <paramref name="Code"/>, split at ";", each dollar-quoted body replaced by "$$".</param>
    internal sealed record Result(string Scan, string Code, IReadOnlyList<string> Statements);

    internal static Result Lex(string sql) => Lex(sql, nested: false, depth: 0);

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c >= 0x80;

    private static Result Lex(string s, bool nested, int depth)
    {
        if (depth > 8) throw new MigrationException("dollar-quoted bodies are nested too deeply");
        var scan = new StringBuilder(s.Length);
        var code = new StringBuilder(s.Length);
        var statement = new StringBuilder();
        var statements = new List<string>();
        var n = s.Length;
        var i = 0;

        // Inside a dollar-quoted body the text may be plain data (not SQL), so an unterminated construct there is kept as text and
        // still scanned; at the top level PostgreSQL itself would refuse it, and so does the guard.
        void Unterminated(string what, int from)
        {
            if (!nested) throw new MigrationException($"unterminated {what}");
            var rest = s[from..];
            scan.Append(rest); code.Append(rest); statement.Append(rest);
        }
        void All(string text) { scan.Append(text); code.Append(text); statement.Append(text); }

        while (i < n)
        {
            var c = s[i];
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                while (i < n && s[i] != '\n' && s[i] != '\r') i++;
                All(" ");
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                var start = i; var level = 1; i += 2;
                while (i < n && level > 0)
                {
                    if (s[i] == '/' && i + 1 < n && s[i + 1] == '*') { level++; i += 2; }
                    else if (s[i] == '*' && i + 1 < n && s[i + 1] == '/') { level--; i += 2; }
                    else i++;
                }
                if (level > 0) { Unterminated("block comment", start); break; }
                All(" ");
                continue;
            }
            if (c == '\'')
            {
                // E'...' (the E being a token of its own) uses backslash escapes; every other literal does not (standard_conforming_strings
                // is on by default, and the guard refuses any migration that mentions that setting).
                var escapes = i > 0 && (s[i - 1] == 'E' || s[i - 1] == 'e') && (i == 1 || !IsIdentChar(s[i - 2]));
                var start = i; i++; var closed = false;
                while (i < n)
                {
                    if (escapes && s[i] == '\\') { i += 2; continue; }
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'') { i += 2; continue; }
                        if (!escapes && s[i - 1] == '\\')
                            throw new MigrationException("a string literal ends in a backslash before its closing quote (read differently depending on server settings)");
                        i++; closed = true; break;
                    }
                    i++;
                }
                if (!closed) { Unterminated("string literal", start); break; }
                scan.Append(s, start, i - start); code.Append("''"); statement.Append("''");
                continue;
            }
            if (c == '"')
            {
                // U&"..." is an identifier written with Unicode escapes: it can spell any name (also a guarded one) without the guard seeing it.
                if (i >= 2 && s[i - 1] == '&' && (s[i - 2] == 'U' || s[i - 2] == 'u') && (i == 2 || !IsIdentChar(s[i - 3])))
                    throw new MigrationException("an identifier is written with Unicode escapes (U&\"...\")");
                var start = i; i++; var closed = false;
                while (i < n)
                {
                    if (s[i] == '"') { if (i + 1 < n && s[i + 1] == '"') { i += 2; continue; } i++; closed = true; break; }
                    i++;
                }
                if (!closed) { Unterminated("quoted identifier", start); break; }
                All(s[start..i]);
                continue;
            }
            if (c == '$' && (i == 0 || !IsIdentChar(s[i - 1])))
            {
                // $$ or $tag$ (a tag is an identifier without "$"); "$1" is a parameter, not a quote.
                var j = i + 1;
                if (j < n && (char.IsLetter(s[j]) || s[j] == '_' || s[j] >= 0x80))
                    while (j < n && s[j] != '$' && IsIdentChar(s[j])) j++;
                if (j < n && s[j] == '$')
                {
                    var tag = s[i..(j + 1)];
                    var end = s.IndexOf(tag, j + 1, StringComparison.Ordinal);
                    if (end < 0) { Unterminated("dollar-quoted body", i); break; }
                    var inner = Lex(s[(j + 1)..end], nested: true, depth + 1);
                    scan.Append(' ').Append(inner.Scan).Append(' ');
                    code.Append(' ').Append(inner.Code).Append(' ');
                    statement.Append(" $$ ");
                    i = end + tag.Length;
                    continue;
                }
            }
            if (c == '$' && i > 0 && (char.IsDigit(s[i - 1]) || s[i - 1] == '$'))
            {
                // "1$$...$$" or "$$...$$$$...$$": whether this "$" opens a dollar quote depends on details of the server's scanner.
                var j = i + 1;
                if (j < n && (char.IsLetter(s[j]) || s[j] == '_' || s[j] >= 0x80))
                    while (j < n && s[j] != '$' && IsIdentChar(s[j])) j++;
                if (j < n && s[j] == '$')
                    throw new MigrationException("a dollar quote opens directly after a digit or after another dollar quote (ambiguous)");
            }
            if (c == ';' && !nested)
            {
                statements.Add(statement.ToString());
                statement.Clear();
                scan.Append(c); code.Append(c);
                i++;
                continue;
            }
            All(c.ToString());
            i++;
        }
        if (statement.Length > 0) statements.Add(statement.ToString());
        return new Result(scan.ToString(), code.ToString(), statements);
    }
}
