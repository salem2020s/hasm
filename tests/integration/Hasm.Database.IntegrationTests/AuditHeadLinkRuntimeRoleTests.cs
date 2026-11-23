using Hasm.Infrastructure.Audit;
using Hasm.Infrastructure.Backup;
using Hasm.Infrastructure.Configuration;
using Hasm.Infrastructure.SystemDatabase;
using Npgsql;
using Ev = Hasm.Infrastructure.SystemDatabase.SystemSchema.AuditEvent;

namespace Hasm.Database.IntegrationTests;

/// <summary>
/// Audit head integrity, slice 1, with the ACTUAL restricted system runtime role (a real LOGIN role granted by the real <c>grant-system-runtime</c>; it holds INSERT and SELECT on the audit table and CONNECT only on
/// the database, PR 230): it cannot create a temporary table or any <c>pg_temp</c> object that would stand in for the audit table, so the guard's head lookup (schema-qualified) cannot be fed a fake head; a gapped or
/// unlinked row it inserts is refused by the guard (review Y1 of PR 228, adversarial test 10); and the stated limit holds: a row linked to the real head is accepted whatever its MAC. Role-capable cluster only.
/// </summary>
public class AuditHeadLinkRuntimeRoleTests : CompanyRuntimeTestBase
{
    private const string RolePrefix = "hasm_ahl_";
    private readonly List<string> testRoles = [];
    private string runtimeRole = null!;
    private HasmDatabaseOptions RuntimeOptions = null!;
    private static string Q(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string RoleAdminConnection => Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar)!;

    private async Task RoleAdmin(string sql)
    {
        await using var conn = new NpgsqlConnection(RoleAdminConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task StartRestricted()
    {
        var reason = PostgresRoleTestEnvironment.Evaluate();
        if (reason is not null) throw new InvalidOperationException($"Role-isolation gate is closed: {reason}");
        await StartCompanies();
        runtimeRole = RolePrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        lock (testRoles) testRoles.Add(runtimeRole);
        await RoleAdmin($"CREATE ROLE {Q(runtimeRole)} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '{password}'");
        RuntimeOptions = new HasmDatabaseOptions
        {
            Host = Options.Host, Port = Options.Port, SystemDatabaseName = Options.SystemDatabaseName, Username = runtimeRole, Password = password,
            AdminUsername = Options.AdminUsername, AdminPassword = Options.AdminPassword, MaintenanceDatabase = Options.MaintenanceDatabase, SslMode = Options.SslMode, AllowInsecureTransport = true,
        };
        Assert.Empty((await SystemRuntimeAdministration.GrantAsync(RuntimeOptions)).Findings.Violations);
    }

    private async Task<NpgsqlConnection> AsRuntime()
    {
        var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(RuntimeOptions.BuildSystemConnectionString(admin: false)) { Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<PostgresException> Fails(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    private async Task InsertAsRuntime(NpgsqlConnection rt, AuditRecord r)
    {
        await using var ins = new NpgsqlCommand($"INSERT INTO {Ev.Table} ({AuditStorage.Columns}) VALUES (@s,@q,@id,@t,@e,@c,@a,@tu,@su,@l,@d,@v,@k,@p,@h,@m)", rt);
        ins.Parameters.AddWithValue("s", r.StreamKey); ins.Parameters.AddWithValue("q", r.Seq); ins.Parameters.AddWithValue("id", r.EventId); ins.Parameters.AddWithValue("t", r.OccurredAtUtc);
        ins.Parameters.AddWithValue("e", r.EventType); ins.Parameters.AddWithValue("c", (object?)r.CompanyId ?? DBNull.Value); ins.Parameters.AddWithValue("a", (object?)r.ActorUserId ?? DBNull.Value);
        ins.Parameters.AddWithValue("tu", (object?)r.TargetUserId ?? DBNull.Value); ins.Parameters.AddWithValue("su", (object?)r.SubjectId ?? DBNull.Value); ins.Parameters.AddWithValue("l", (object?)r.LegacyRef ?? DBNull.Value);
        ins.Parameters.AddWithValue("d", (object?)r.Detail ?? DBNull.Value); ins.Parameters.AddWithValue("v", r.FormatVersion); ins.Parameters.AddWithValue("k", r.KeyId);
        ins.Parameters.AddWithValue("p", r.PrevHash); ins.Parameters.AddWithValue("h", r.Hash); ins.Parameters.AddWithValue("m", r.Mac);
        await ins.ExecuteNonQueryAsync();
    }

    private async Task<long> Count() => await Scalar<long>($"SELECT count(*) FROM {Ev.Table} WHERE {Ev.StreamKey} = 'system'");

    [PostgresRoleFact]
    public async Task The_runtime_role_cannot_shadow_the_audit_table_and_cannot_get_a_gapped_or_unlinked_row_past_the_guard()
    {
        await StartRestricted();
        await BackupAudit.WriteAsync(Options.BuildSystemConnectionString(admin: true), Audit, BackupAuditEvents.RetentionEvaluated, "backup retention evaluated operator=os:t");
        AuditRecord head;
        await using (var conn = new NpgsqlConnection(Options.BuildSystemConnectionString(admin: true)))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"SELECT {AuditStorage.Columns} FROM {Ev.Table} WHERE {Ev.StreamKey} = 'system' ORDER BY {Ev.Seq} DESC LIMIT 1", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            head = AuditStorage.Read(r);
        }
        var attacker = new InMemoryAuditKeyRing("k1");
        AuditRecord Forge(long seq, byte[] prev) => AuditCanonical.Seal(head with { Seq = seq, EventId = Guid.NewGuid(), Detail = "forged", PrevHash = prev, Hash = [], Mac = [] }, attacker.GetActive());
        await using var rt = await AsRuntime();

        // shadowing: no temporary table of the audit table's name, no pg_temp object of any kind (CONNECT only on the database)
        Assert.Equal("42501", (await Fails(rt, $"CREATE TEMP TABLE {Ev.Table} (LIKE {Ev.Table})")).SqlState);
        Assert.Equal("42501", (await Fails(rt, $"CREATE TABLE pg_temp.{Ev.Table} (LIKE {Ev.Table})")).SqlState);
        Assert.Equal("42501", (await Fails(rt, "CREATE DOMAIN pg_temp.x AS int")).SqlState);
        Assert.Equal("42501", (await Fails(rt, "CREATE TYPE pg_temp.bytea AS (a int)")).SqlState);

        // gapped and unlinked rows are refused (the guard's head lookup is public.<table>, so nothing can stand in for it)
        var before = await Count();
        foreach (var forged in new[] { Forge(head.Seq + 2, head.Hash), Forge(head.Seq + 1, new byte[32]), Forge(head.Seq, head.Hash), Forge(head.Seq + 1, AuditCanonical.GenesisHash("system")) })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsRuntime(rt, forged));
            Assert.Equal("audit_link_violation", ex.MessageText);
        }
        Assert.Equal(before, await Count());

        // the stated limit: a row linked to the real head is accepted whatever its MAC, and the writer then refuses (the recovery is a later slice)
        await InsertAsRuntime(rt, Forge(head.Seq + 1, head.Hash));
        Assert.Equal(before + 1, await Count());
        var refused = await Assert.ThrowsAsync<AuditIntegrityException>(() => BackupAudit.WriteAsync(Options.BuildSystemConnectionString(admin: true), Audit, BackupAuditEvents.RetentionEvaluated, "backup retention evaluated operator=os:t"));
        Assert.Equal("audit_head_corrupt", refused.Code);
    }

    public override async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await base.DisposeAsync();
        if (Environment.GetEnvironmentVariable(PostgresRoleTestEnvironment.RoleAdminConnectionVar) is not { Length: > 0 }) return;
        string[] roles;
        lock (testRoles) roles = [.. testRoles];
        foreach (var role in roles)
        {
            if (!role.StartsWith(RolePrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a role this suite did not create.");
            await RoleAdmin($"DROP ROLE IF EXISTS {Q(role)}");
        }
    }
}
