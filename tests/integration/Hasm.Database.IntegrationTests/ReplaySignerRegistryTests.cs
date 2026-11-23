using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Hasm.Infrastructure.CompanyDatabase;
using Npgsql;
using RK = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplaySignerKey;
using RG = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayExecutionGrant;
using RU = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayGrantUse;
using RA = Hasm.Infrastructure.CompanyDatabase.CompanySchema.ReplayAuthorization;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// T2 slice A1 (company migration 0037): the signer key registry, the RSA verifier and the empty grant shells. INERT: nothing calls the verifier, the runtime role holds SELECT only, no grant exists. These tests prove
/// the verifier against fixed vectors in SQL and in an independent .NET twin, the registry's rules, the runtime role's lack of any write, the no TEMPORARY / no CREATE invariant of the design (4.5, M1) and the migration's
/// compliance with the migration-guard rules (no SECURITY DEFINER, no search_path, schema-qualified relations).
/// </summary>
public class ReplaySignerRegistryTests : PlanExecutionTestBase
{
    private static byte[] Hex(string h) => Convert.FromHexString(h);
    private static string Lit(byte[] b) => "'\\x" + Convert.ToHexStringLower(b) + "'::bytea";
    private static BigInteger Big(byte[] b) => new(b, isUnsigned: true, isBigEndian: true);
    private static byte[] Message(string suffix = "") => Encoding.UTF8.GetBytes(ReplayRsaVectors.Message + suffix);
    private static byte[] Fixed(BigInteger v, int length) { var b = v.ToByteArray(isUnsigned: true, isBigEndian: true); return [.. new byte[Math.Max(0, length - b.Length)], .. b]; }

    /// <summary>The independent .NET twin of the verifier: the same rule written straight from RFC 8017 (RSASSA-PKCS1-v1_5, SHA-256, e = 65537, k = 384), comparing the whole encoding.</summary>
    private static bool Twin(BigInteger n, byte[] signature, byte[] message)
    {
        if (signature.Length != 384 || n.IsEven || n < BigInteger.Pow(2, 3071) || n >= BigInteger.Pow(2, 3072)) return false;
        var s = Big(signature);
        if (s >= n) return false;
        var em = Fixed(BigInteger.ModPow(s, 65537, n), 384);
        var expected = new byte[384];
        expected[1] = 1; Array.Fill(expected, (byte)0xff, 2, 330);
        Convert.FromHexString("3031300d060960864801650304020105000420").CopyTo(expected, 384 - 51);
        SHA256.HashData(message).CopyTo(expected, 384 - 32);
        return em.AsSpan().SequenceEqual(expected);
    }

    private Task<bool> SqlVerify(BigInteger n, byte[] signature, byte[] message) =>
        Sql<bool>($"SELECT public.{RA.FnRsaVerify}('{n}'::numeric, {Lit(signature)}, {Lit(message)})");

    private static IEnumerable<(string Name, BigInteger N, byte[] Signature, byte[] Message, bool Expected)> Cases()
    {
        var n = Big(Hex(ReplayRsaVectors.Modulus)); var valid = Hex(ReplayRsaVectors.Valid); var msg = Message();
        yield return ("valid", n, valid, msg, true);
        yield return ("valid with a leading zero byte", n, Hex(ReplayRsaVectors.ZeroSignature), Message("|" + ReplayRsaVectors.ZeroMessageSuffix), true);
        yield return ("message changed", n, valid, Message("x"), false);
        yield return ("signature changed (last byte)", n, [.. valid[..^1], (byte)(valid[^1] ^ 1)], msg, false);
        yield return ("signed by another key", n, Hex(ReplayRsaVectors.OtherKeySignature), msg, false);
        yield return ("383 bytes (first byte dropped)", n, valid[1..], msg, false);
        yield return ("leading zero byte dropped", n, Hex(ReplayRsaVectors.ZeroSignature)[1..], Message("|" + ReplayRsaVectors.ZeroMessageSuffix), false);
        yield return ("385 bytes (zero appended)", n, [.. valid, 0], msg, false);
        yield return ("385 bytes (zero prepended)", n, [0, .. valid], msg, false);
        yield return ("empty signature", n, [], msg, false);
        yield return ("s = 0", n, new byte[384], msg, false);
        yield return ("s = 1", n, Fixed(1, 384), msg, false);
        yield return ("s = n - 1", n, Fixed(n - 1, 384), msg, false);
        yield return ("s = n", n, Fixed(n, 384), msg, false);
        yield return ("s = n + 1", n, Fixed(n + 1, 384), msg, false);
        yield return ("s = 2^3072 - 1", n, Fixed(BigInteger.Pow(2, 3072) - 1, 384), msg, false);
        yield return ("altered padding", n, Hex(ReplayRsaVectors.AlteredPadding), msg, false);
        yield return ("altered DigestInfo", n, Hex(ReplayRsaVectors.AlteredDigestInfo), msg, false);
        yield return ("SHA-1 DigestInfo", n, Hex(ReplayRsaVectors.Sha1DigestInfo), msg, false);
        yield return ("PSS signature", n, Hex(ReplayRsaVectors.Pss), msg, false);
        yield return ("2048-bit key", Big(Hex(ReplayRsaVectors.Modulus2048)), [.. new byte[128], .. Hex(ReplayRsaVectors.Signature2048)], msg, false);
        yield return ("exponent 3 key", Big(Hex(ReplayRsaVectors.ModulusExponent3)), Hex(ReplayRsaVectors.SignatureExponent3), msg, false);
        yield return ("s + n (congruent to a valid signature, not below the modulus)", n, Hex(ReplayRsaVectors.PlusNSignature), Message("#" + ReplayRsaVectors.PlusNMessageSuffix), false);
        yield return ("the reduced signature of that message is valid", n, Hex(ReplayRsaVectors.PlusNSignatureReduced), Message("#" + ReplayRsaVectors.PlusNMessageSuffix), true);
        yield return ("valid signature of a 3071-bit key", Big(Hex(ReplayRsaVectors.Modulus3071)), Hex(ReplayRsaVectors.Signature3071), msg, false);
        yield return ("modulus 2^3071 + 1 with the valid signature", BigInteger.Pow(2, 3071) + 1, valid, msg, false);
        yield return ("even modulus", n + 1, valid, msg, false);
    }

    [PostgresRoleFact]
    public async Task The_grant_message_vector_of_the_design_is_what_is_signed()
    {
        Assert.Equal("ef7615cb4c06e2fcd5422e2bbd901334439f1f58285c51c3ae9b3412a7715e1b", Convert.ToHexStringLower(SHA256.HashData(Message())));
        Assert.Equal(1408, Message().Length);
        await Task.CompletedTask;
    }

    [PostgresRoleFact]
    public async Task The_verifier_gives_the_same_answer_in_SQL_as_in_the_dotnet_twin_for_every_vector_and_the_expected_one()
    {
        await PrepareRuntime();
        foreach (var (name, n, signature, message, expected) in Cases())
        {
            Assert.True(Twin(n, signature, message) == expected, "the .NET twin disagrees with the expectation: " + name);
            Assert.True(await SqlVerify(n, signature, message) == expected, "SQL: " + name);
        }
        // the valid vectors also verify with the platform's own implementation (the signature vector is a real PKCS#1 v1.5 / SHA-256 signature)
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Fixed(Big(Hex(ReplayRsaVectors.Modulus)), 384), Exponent = [1, 0, 1] });
        Assert.True(rsa.VerifyData(Message(), Hex(ReplayRsaVectors.Valid), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(rsa.VerifyData(Message("x"), Hex(ReplayRsaVectors.Valid), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        // NULL input is false, never an error
        Assert.False(await Sql<bool>($"SELECT public.{RA.FnRsaVerify}(NULL, {Lit(Hex(ReplayRsaVectors.Valid))}, {Lit(Message())})"));
        Assert.False(await Sql<bool>($"SELECT public.{RA.FnRsaVerify}('{Big(Hex(ReplayRsaVectors.Modulus))}'::numeric, NULL, {Lit(Message())})"));
        Assert.False(await Sql<bool>($"SELECT public.{RA.FnRsaVerify}('{Big(Hex(ReplayRsaVectors.Modulus))}'::numeric, {Lit(Hex(ReplayRsaVectors.Valid))}, NULL)"));
    }

    [PostgresRoleFact]
    public async Task The_verifier_ignores_the_session_settings_that_change_text_and_numbers()
    {
        await PrepareRuntime();
        var n = Big(Hex(ReplayRsaVectors.Modulus));
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        foreach (var setting in new[] { "SET TimeZone = 'Asia/Riyadh'", "SET DateStyle = 'German'", "SET IntervalStyle = 'iso_8601'", "SET bytea_output = 'escape'", "SET extra_float_digits = -3", "SET lc_numeric = 'C'" })
        {
            await using (var s = new NpgsqlCommand(setting, conn)) await s.ExecuteNonQueryAsync();
            await using var cmd = new NpgsqlCommand($"SELECT public.{RA.FnRsaVerify}('{n}'::numeric, {Lit(Hex(ReplayRsaVectors.Valid))}, {Lit(Message())})", conn);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!, setting);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ the registry

    private static string KeyInsert(string id, BigInteger n, Guid? signer = null, string extra = "") =>
        $"INSERT INTO {RK.Table} ({RK.KeyId}, {RK.Modulus}, {RK.SignerUserId}) VALUES ('{id}', '{n}'::numeric, {(signer is null ? "NULL" : $"'{signer}'")}){extra}";

    private async Task<string?> AsOwner(params string[] statements)
    {
        await using var conn = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName));
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var s in statements) { await using var cmd = new NpgsqlCommand(s, conn, tx); await cmd.ExecuteNonQueryAsync(); }
            await tx.CommitAsync();
            return null;
        }
        catch (PostgresException ex) { return Describe(ex); }
    }

    [PostgresRoleFact]
    public async Task The_owner_registers_a_key_with_the_database_clock_and_the_modulus_must_be_an_odd_integer_of_exactly_3072_bits()
    {
        await PrepareRuntime();
        var n = Big(Hex(ReplayRsaVectors.Modulus)); var signer = Guid.NewGuid();
        Assert.Null(await AsOwner(KeyInsert("k1", n, signer)));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {RK.Table} WHERE {RK.KeyId} = 'k1' AND {RK.Modulus} = '{n}'::numeric AND {RK.SignerUserId} = '{signer}' AND {RK.RevokeFrom} IS NULL"));
        // registered-at is the database clock, whatever the writer says
        Assert.Null(await AsOwner($"INSERT INTO {RK.Table} ({RK.KeyId}, {RK.Modulus}, {RK.RegisteredAtUtc}) VALUES ('k2', '{n + 2}'::numeric, TIMESTAMPTZ '2000-01-01 00:00:00+00')"));
        Assert.True(await Sql<bool>($"SELECT {RK.RegisteredAtUtc} > now() - interval '1 hour' FROM {RK.Table} WHERE {RK.KeyId} = 'k2'"));
        var two3071 = BigInteger.Pow(2, 3071); var two3072 = BigInteger.Pow(2, 3072);
        Assert.Null(await AsOwner(KeyInsert("edge-low", two3071 + 1)));
        Assert.Null(await AsOwner(KeyInsert("edge-high", two3072 - 1)));
        const string check = "23514";
        Assert.StartsWith(check, await AsOwner(KeyInsert("small", two3071 - 1)));                       // 3071 bits
        Assert.StartsWith(check, await AsOwner(KeyInsert("big", two3072 + 1)));                         // 3073 bits
        Assert.StartsWith(check, await AsOwner(KeyInsert("even", two3071 + 2)));
        Assert.StartsWith(check, await AsOwner(KeyInsert("fraction", n).Replace($"'{n}'::numeric", $"'{n}.5'::numeric")));
        Assert.StartsWith(check, await AsOwner(KeyInsert("rsa2048", Big(Hex(ReplayRsaVectors.Modulus2048)))));
        foreach (var id in new[] { "K1", "a_b", "", new string('a', 41), "a b" })
            Assert.NotNull(await AsOwner(KeyInsert(id, n + 4)));                                         // a key id is lower case, digits and hyphens, 1 to 40 characters
        Assert.StartsWith("23505", await AsOwner(KeyInsert("k1", n + 6)));                              // unique
        RefusedWith("registered unrevoked", await AsOwner($"INSERT INTO {RK.Table} ({RK.KeyId}, {RK.Modulus}, {RK.RevokeFrom}) VALUES ('pre-revoked', '{n + 8}'::numeric, now())"));
    }

    [PostgresRoleFact]
    public async Task A_key_is_append_only_except_for_its_one_time_revoke_from()
    {
        await PrepareRuntime();
        var n = Big(Hex(ReplayRsaVectors.Modulus));
        Assert.Null(await AsOwner(KeyInsert("k1", n, Guid.NewGuid())));
        RefusedWith("only sets its revoke-from", await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = NULL WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("immutable", await AsOwner($"UPDATE {RK.Table} SET {RK.Modulus} = {RK.Modulus} + 2 WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("immutable", await AsOwner($"UPDATE {RK.Table} SET {RK.SignerUserId} = gen_random_uuid() WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("immutable", await AsOwner($"UPDATE {RK.Table} SET {RK.RegisteredAtUtc} = {RK.RegisteredAtUtc} + interval '1 day' WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("immutable", await AsOwner($"UPDATE {RK.Table} SET {RK.KeyId} = 'k9' WHERE {RK.KeyId} = 'k1'"));
        Assert.Null(await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = TIMESTAMPTZ '2026-03-01 00:00:00+00' WHERE {RK.KeyId} = 'k1'"));        // retroactive revocation is allowed (D5)
        RefusedWith("set once", await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = TIMESTAMPTZ '2026-02-01 00:00:00+00' WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("set once", await AsOwner($"UPDATE {RK.Table} SET {RK.RevokeFrom} = NULL WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("never removed", await AsOwner($"DELETE FROM {RK.Table} WHERE {RK.KeyId} = 'k1'"));
        RefusedWith("never removed", await AsOwner($"TRUNCATE {RK.Table}, {RG.Table}, {RU.Table}"));       // the three together: PostgreSQL refuses a lone TRUNCATE of a referenced table before any trigger
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {RK.Table}"));
    }

    [PostgresRoleFact]
    public async Task The_runtime_role_holds_select_only_on_the_three_tables_and_every_write_is_refused_with_42501()
    {
        await PrepareRuntime();
        Assert.Null(await AsOwner(KeyInsert("k1", Big(Hex(ReplayRsaVectors.Modulus)))));
        await using var conn = await AsRuntime();
        foreach (var table in new[] { RK.Table, RG.Table, RU.Table })
        {
            Assert.True((bool)(await Scalar(conn, $"SELECT has_table_privilege(current_user, '{table}', 'SELECT')"))!, table);
            foreach (var privilege in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
                Assert.False((bool)(await Scalar(conn, $"SELECT has_table_privilege(current_user, '{table}', '{privilege}')"))!, $"{table} {privilege}");
            Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges);
            await Scalar(conn, $"SELECT count(*) FROM {table}");        // it may read
            foreach (var statement in new[] { $"INSERT INTO {table} DEFAULT VALUES", $"UPDATE {table} SET {(table == RK.Table ? RK.RevokeFrom : table == RG.Table ? RG.NotBefore : RU.GrantId)} = {(table == RK.Table ? "now()" : table == RG.Table ? "1" : "gen_random_uuid()")}", $"DELETE FROM {table}", $"TRUNCATE {table}" })
            {
                await using var tx = await conn.BeginTransactionAsync();
                var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand(statement, conn, tx); await cmd.ExecuteNonQueryAsync(); });
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
                await tx.RollbackAsync();
            }
        }
        // the verifier is a function everyone may call (it reads nothing and writes nothing): no table is reachable through it
        Assert.True((bool)(await Scalar(conn, $"SELECT public.{RA.FnRsaVerify}('{Big(Hex(ReplayRsaVectors.Modulus))}'::numeric, {Lit(Hex(ReplayRsaVectors.Valid))}, {Lit(Message())})"))!);
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql) { await using var cmd = new NpgsqlCommand(sql, conn); return await cmd.ExecuteScalarAsync(); }

    [PostgresRoleFact]
    public async Task The_grant_tables_are_append_only_shells_with_structural_checks_and_a_server_clock()
    {
        await PrepareRuntime();
        var plan = await ProposedPlan(Avg, "2026-03-11");
        Assert.Null(await AsOwner(KeyInsert("k1", Big(Hex(ReplayRsaVectors.Modulus)))));
        // this test is about the SHELL (structural checks, the database clock, append-only); since slice A2 (company 0038) an insert is also verified by a BEFORE INSERT trigger, whose rules ReplayGrantVerificationTests proves
        // one by one, so the verification trigger is switched off here (as the owner of the disposable test database) to reach the table's own constraints
        Assert.Null(await AsOwner($"ALTER TABLE {RG.Table} DISABLE TRIGGER {CompanySchema.ReplayGrant.TrGrantVerify}"));
        string Grant(Guid id, string key = "k1", Guid? planId = null, long nb = 1000, long na = 8200, string digest = "", int sigLength = 384, string attestation = "a", string extra = "") =>
            $"INSERT INTO {RG.Table} ({RG.Id}, {RG.KeyId}, {RG.PlanId}, {RG.ExecutorUserId}, {RG.SignerUserId}, {RG.NotBefore}, {RG.NotAfter}, {RG.EpochDigest}, {RG.Attestation}, {RG.Signature}{(extra == "" ? "" : ", " + extra.Split('=')[0])}) " +
            $"VALUES ('{id}', '{key}', '{planId ?? plan.Id}', gen_random_uuid(), gen_random_uuid(), {nb}, {na}, '{(digest == "" ? new string('a', 64) : digest)}', '{attestation}', decode(repeat('ab', {sigLength}), 'hex'){(extra == "" ? "" : ", " + extra.Split('=')[1])})";
        var g = Guid.NewGuid();
        Assert.Null(await AsOwner(Grant(g, extra: $"{RG.AcceptedAtUtc}=TIMESTAMPTZ '2000-01-01 00:00:00+00'")));
        Assert.True(await Sql<bool>($"SELECT {RG.AcceptedAtUtc} > now() - interval '1 hour' FROM {RG.Table} WHERE {RG.Id} = '{g}'"));       // the database clock, not the writer's
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), sigLength: 383)));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), sigLength: 385)));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), nb: 9000, na: 9000)));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), nb: -1)));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), digest: new string('A', 64))));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), digest: new string('a', 63))));
        Assert.StartsWith("23514", await AsOwner(Grant(Guid.NewGuid(), attestation: "")));
        Assert.StartsWith("23503", await AsOwner(Grant(Guid.NewGuid(), key: "unknown")));
        Assert.StartsWith("23503", await AsOwner(Grant(Guid.NewGuid(), planId: Guid.NewGuid())));
        RefusedWith("append-only", await AsOwner($"UPDATE {RG.Table} SET {RG.NotAfter} = {RG.NotAfter} + 1 WHERE {RG.Id} = '{g}'"));
        RefusedWith("append-only", await AsOwner($"DELETE FROM {RG.Table} WHERE {RG.Id} = '{g}'"));
        RefusedWith("append-only", await AsOwner($"TRUNCATE {RG.Table}, {RU.Table}"));
        // a key with a grant can never be removed (the key table refuses it anyway) and the use table is append-only too (its rows need an execution, which only the executor slices can create, so only its statement-level truncate guard can be driven here; its row guard is the same function the grant table proves)
        RefusedWith("append-only", await AsOwner($"TRUNCATE {RU.Table}"));
        Assert.Equal(1L, await Sql<long>($"SELECT count(*) FROM {RG.Table}"));
    }

    // ------------------------------------------------------------------------------------------------------------ name resolution (design 4.5, M1) and migration-guard compliance

    [PostgresRoleFact]
    public async Task The_runtime_role_can_create_no_temporary_relation_and_no_object_and_the_invariant_survives_a_second_grant_step()
    {
        await PrepareRuntime();
        async Task Check()
        {
            await using var conn = await AsRuntime();
            Assert.False((bool)(await Scalar(conn, "SELECT has_database_privilege(current_user, current_database(), 'TEMPORARY')"))!);
            Assert.False((bool)(await Scalar(conn, "SELECT has_database_privilege(current_user, current_database(), 'CREATE')"))!);
            Assert.False((bool)(await Scalar(conn, "SELECT has_schema_privilege(current_user, 'public', 'CREATE')"))!);
            foreach (var statement in new[]
                     {
                         "CREATE TEMPORARY TABLE t_shadow (x int)", "CREATE TEMP TABLE t_shadow2 AS SELECT 1", "CREATE TABLE public.t_shadow3 (x int)", "CREATE FUNCTION public.f_shadow() RETURNS int LANGUAGE sql AS 'SELECT 1'",
                         "CREATE TYPE public.ty_shadow AS ENUM ('a')", "CREATE SCHEMA s_shadow", "CREATE VIEW public.v_shadow AS SELECT 1",
                     })
            {
                await using var tx = await conn.BeginTransactionAsync();
                var ex = await Assert.ThrowsAsync<PostgresException>(async () => { await using var cmd = new NpgsqlCommand(statement, conn, tx); await cmd.ExecuteNonQueryAsync(); });
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
                await tx.RollbackAsync();
            }
        }
        await Check();
        // the grant step is idempotent: running it again converges on the same invariant (a restored database goes through this step)
        var granted = await new CompanyRuntimeProvisioner(Configured(useRoleAdmin: true), Registry).GrantAsync(Company.Id);
        Assert.True(granted.LoginVerified);
        await Check();
        // and the application's own check of the role (the provisioner's violation query) reports nothing for it
        await using (var admin = new NpgsqlConnection(Options.BuildCompanyAdminConnectionString(Company.PhysicalDatabaseName)))
        {
            await admin.OpenAsync();
            Assert.Empty(await CompanyRuntimePrivileges.FindViolationsAsync(admin, CompanyRuntimeRole.NameFor(Company.PhysicalDatabaseName)));
        }
    }

    [PostgresRoleFact]
    public async Task The_migration_has_no_security_definer_and_no_search_path_and_every_relation_in_a_function_body_is_schema_qualified()
    {
        await PrepareRuntime();
        var functions = new[] { RA.FnRsaVerify, RA.FnGrantGuard, RK.FnGuard };
        foreach (var name in functions)
        {
            Assert.False(await Sql<bool>($"SELECT p.prosecdef FROM pg_proc p WHERE p.proname = '{name}'"), name);
            Assert.True(await Sql<bool>($"SELECT p.proconfig IS NULL FROM pg_proc p WHERE p.proname = '{name}'"), name + " has no settings (no search_path)");
            Assert.Equal("plpgsql", await Sql<string>($"SELECT l.lanname FROM pg_proc p JOIN pg_language l ON l.oid = p.prolang WHERE p.proname = '{name}'"));
            var body = await Sql<string>($"SELECT p.prosrc FROM pg_proc p WHERE p.proname = '{name}'");
            // a relation named after FROM, JOIN, INTO, UPDATE or a table reference must be public.<name>; the functions of this migration name no table at all besides the check against TG_TABLE_NAME
            Assert.DoesNotMatch(new Regex(@"(?<!DISTINCT\s)\b(?:FROM|JOIN|UPDATE)\s+(?!public\.|NEW\.|OLD\.)[a-z_]", RegexOptions.IgnoreCase), Regex.Replace(body, "'[^']*'", "''"));        // string literals (messages) are not relations
        }
        var sql = Regex.Replace(File.ReadAllText(Path.Combine(MigrationDirectory(), "0037_company_replay_signer_keys.sql")), "--[^\n]*", "");        // the header comments explain the rules in words
        Assert.DoesNotMatch(new Regex(@"SECURITY\s+DEFINER|search_path|set_config|\bEXECUTE\s+'|CREATE\s+EXTENSION", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"\bpg_\w+", RegexOptions.IgnoreCase), sql);
        // every relation of the DDL itself is qualified too
        foreach (Match m in Regex.Matches(sql, @"\b(?:CREATE\s+(?:UNIQUE\s+)?(?:TABLE|INDEX\s+\S+\s+ON|FUNCTION)|REFERENCES|REVOKE\s+ALL\s+ON\s+TABLE|ON|EXECUTE\s+FUNCTION)\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.IgnoreCase))
        {
            var target = m.Groups[1].Value;
            if (target is "FUNCTION" or "TABLE" or "EACH" or "ROW" or "STATEMENT") continue;
            Assert.StartsWith("public.", target);
        }
    }

    private static string MigrationDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "backend", "Hasm.Infrastructure", "CompanyDatabase", "Migrations");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("the migration directory was not found");
    }

    [PostgresRoleFact]
    public async Task Only_the_grant_insert_check_of_A2_and_the_commit_check_of_A3_call_the_verifier_and_no_grant_exists_for_the_runtime_role()
    {
        await PrepareRuntime();
        // slice A2 (company 0038) calls the verifier from the insert-time check of a grant and slice A3 (company 0039) from the commit-time check of an execution; nothing else in the database mentions it
        Assert.Equal(string.Join(",", new[] { CompanySchema.ReplayGrant.FnGrantInsert, CompanySchema.ReplayEpoch.FnCheckExecution }.Order(StringComparer.Ordinal)), await Sql<string>($"SELECT string_agg(p.proname, ',' ORDER BY p.proname) FROM pg_proc p WHERE p.prosrc LIKE '%{RA.FnRsaVerify}%' AND p.proname <> '{RA.FnRsaVerify}'"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {RG.Table}"));
        Assert.Equal(0L, await Sql<long>($"SELECT count(*) FROM {RU.Table}"));
        // an execution is still refused for the runtime role (no INSERT on the four execution tables), grant tables or not
        foreach (var table in ExecutionTables) Assert.Equal(["SELECT"], CompanyRuntimePrivileges.Tables.Single(t => t.Table == table).Privileges);
    }
}
